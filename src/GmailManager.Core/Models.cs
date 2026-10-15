namespace GmailManager.Core;

public record Account(string Id, string Email, string DisplayName)
{
    public string Caption => string.IsNullOrEmpty(Email) ? DisplayName : $"{DisplayName} <{Email}>";
}
public record Mail(string AccountId, string Id, string Sender, string Subject, DateTimeOffset Date, string[] Labels)
{
    public string Key => AccountId + "/" + Id;
    public bool Eligible => !Labels.Contains("TRASH") && !Labels.Contains("SPAM");
}
public record Backup(Mail Mail, bool Protected, string State, string? Path, string? Error);
public record OperationItem(Mail Mail, string State, string? Error = null);
public record Operation(string Id, DateTimeOffset Date, string Kind, List<OperationItem> Items, string? Label = null);
public record ProgressInfo(int Completed, int Total, string Message);
public interface IMailGateway
{
    Task<IReadOnlyList<Mail>> ListAsync(Account account, CancellationToken ct);
    Task<byte[]> RawAsync(Mail mail, CancellationToken ct);
    Task ChangeAsync(Mail mail, string action, string? label, CancellationToken ct);
}
