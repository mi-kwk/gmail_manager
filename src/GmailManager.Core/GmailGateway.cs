using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using MimeKit;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GmailManager.Core;

[SupportedOSPlatform("windows")]
public sealed class SecureTokenStore(string folder) : IDataStore
{
    private string FileFor<T>(string key) => Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(typeof(T).FullName + key))) + ".bin");
    public Task StoreAsync<T>(string key, T value)
    {
        Directory.CreateDirectory(folder);
        var path = FileFor<T>(key);
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), null, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, true); return Task.CompletedTask;
    }
    public Task DeleteAsync<T>(string key) { File.Delete(FileFor<T>(key)); return Task.CompletedTask; }
    public Task<T> GetAsync<T>(string key)
    {
        var path = FileFor<T>(key);
        return Task.FromResult(File.Exists(path) ? JsonSerializer.Deserialize<T>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser))! : default!);
    }
    public Task ClearAsync()
    {
        if(Directory.Exists(folder)) foreach(var f in Directory.EnumerateFiles(folder, "*.bin")) File.Delete(f);
        return Task.CompletedTask;
    }
}
[SupportedOSPlatform("windows")]
public sealed class GmailGateway(Store store, string credentialsPath) : IMailGateway, IDisposable
{
    private readonly Dictionary<string,GmailService> services = new();
    private ClientSecrets Secrets()
    {
        using var stream = File.OpenRead(credentialsPath);
        return GoogleClientSecrets.FromStream(stream).Secrets;
    }
    private SecureTokenStore Tokens(string id) => new(Path.Combine(store.Root,"tokens",id));
    public async Task<Account> ConnectAsync(string displayName, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(Secrets(), [GmailService.Scope.GmailModify], id, ct, Tokens(id));
        var service = new GmailService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "Gmail Manager" });
        var profile = await service.Users.GetProfile("me").ExecuteAsync(ct);
        var existing = store.All<Account>("account").FirstOrDefault(a => a.Email.Equals(profile.EmailAddress, StringComparison.OrdinalIgnoreCase));
        if(existing is not null) { await Tokens(existing.Id).StoreAsync(existing.Id, credential.Token); await Tokens(id).DeleteAsync<Google.Apis.Auth.OAuth2.Responses.TokenResponse>(id); if(services.Remove(existing.Id,out var old)) old.Dispose(); service.Dispose(); return existing; }
        var account = new Account(id,profile.EmailAddress, string.IsNullOrWhiteSpace(displayName) ? profile.EmailAddress : displayName.Trim());
        services[id] = service; store.Put("account",id,account); return account;
    }
    private async Task<GmailService> ServiceAsync(string id, CancellationToken ct)
    {
        if(services.TryGetValue(id,out var cached)) return cached;
        var token = await Tokens(id).GetAsync<Google.Apis.Auth.OAuth2.Responses.TokenResponse>(id)
            ?? throw new InvalidOperationException("Gmail連携をやり直してください");
        var flow = new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(new()
        { ClientSecrets = Secrets(), Scopes = [GmailService.Scope.GmailModify], DataStore = Tokens(id) });
        var credential = new UserCredential(flow, id, token);
        if(credential.Token.IsStale && !await credential.RefreshTokenAsync(ct)) throw new InvalidOperationException("再認証が必要です");
        var service = new GmailService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "Gmail Manager" });
        services[id] = service; return service;
    }
    public async Task<IReadOnlyList<Mail>> ListAsync(Account account, CancellationToken ct)
    {
        var service = await ServiceAsync(account.Id,ct);
        var result = new List<Mail>(); string? page = null;
        do
        {
            var request = service.Users.Messages.List("me");
            request.IncludeSpamTrash = false; request.MaxResults = 500; request.PageToken = page;
            var response = await request.ExecuteAsync(ct);
            foreach(var stub in response.Messages ?? [])
            {
                var get = service.Users.Messages.Get("me",stub.Id);
                get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                get.MetadataHeaders = new Google.Apis.Util.Repeatable<string>(["From","Subject","Date"]);
                var message = await get.ExecuteAsync(ct);
                string Header(string name) => message.Payload?.Headers?.FirstOrDefault(h => h.Name.Equals(name,StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                var sender = InternetAddressList.TryParse(Header("From"),out var addresses) ? addresses.Mailboxes.FirstOrDefault()?.Address ?? "" : "";
                result.Add(new(account.Id,message.Id,sender,Header("Subject"),
                    DateTimeOffset.FromUnixTimeMilliseconds(message.InternalDate ?? 0), (message.LabelIds ?? []).ToArray()));
            }
            page = response.NextPageToken;
        } while(!string.IsNullOrEmpty(page));
        return result;
    }
    public async Task<byte[]> RawAsync(Mail mail, CancellationToken ct)
    {
        var service = await ServiceAsync(mail.AccountId,ct);
        var request = service.Users.Messages.Get("me",mail.Id);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
        var message = await request.ExecuteAsync(ct);
        var b64 = message.Raw.Replace('-','+').Replace('_','/');
        return Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4,'='));
    }
    public async Task ChangeAsync(Mail mail, string action, string? label, CancellationToken ct)
    {
        var service = await ServiceAsync(mail.AccountId,ct);
        if(action == "ゴミ箱移動")
            await service.Users.Messages.Trash("me",mail.Id).ExecuteAsync(ct);
        else if(action == "アーカイブ")
            await service.Users.Messages.Modify(new ModifyMessageRequest { RemoveLabelIds = ["INBOX"] },"me",mail.Id).ExecuteAsync(ct);
        else if(action == "ラベル" && !string.IsNullOrWhiteSpace(label))
            await service.Users.Messages.Modify(new ModifyMessageRequest { AddLabelIds = [label] },"me",mail.Id).ExecuteAsync(ct);
        else throw new InvalidOperationException("未対応の操作です");
    }
    public async Task<IList<Label>> LabelsAsync(Account account, CancellationToken ct)
    {
        var service = await ServiceAsync(account.Id,ct);
        return (await service.Users.Labels.List("me").ExecuteAsync(ct)).Labels;
    }
    public void Dispose() { foreach(var s in services.Values) s.Dispose(); }
}
