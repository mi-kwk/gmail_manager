using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GmailManager.Core;

public sealed class Store
{
    public string Root { get; }
    private readonly string connectionString;
    private readonly object sync = new();
    public Store(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        connectionString = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(root, "gmail.db") }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS records(kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(kind,id));
            CREATE TABLE IF NOT EXISTS operation_items(operation_id TEXT NOT NULL, position INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(operation_id,position));
            """;
        cmd.ExecuteNonQuery();
        // An interrupted backup remains protected and becomes retryable.
        foreach (var b in All<Backup>("backup").Where(x => x.State is "保存中" or "保存待ち"))
            Put("backup", b.Mail.Key, b with { State = "失敗", Error = "前回の保存が中断されました。再試行してください。" });
        foreach (var op in All<Operation>("operation").Where(x => x.Items.Any(i => i.State == "実行中")))
            Put("operation", op.Id, op with { Items = op.Items.Select(i => i.State == "実行中" ? i with { State = "要確認", Error = "中断により結果不明。Gmailで状態を確認してください。" } : i).ToList() });
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }
    public void Put<T>(string kind, string id, T value)
    {
        lock(sync)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand(); cmd.Transaction=tx;
            cmd.CommandText = "INSERT INTO records(kind,id,json) VALUES($k,$i,$j) ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(value));
            cmd.ExecuteNonQuery();
            if(kind=="operation")
            {
                using var clear=db.CreateCommand(); clear.Transaction=tx;
                clear.CommandText="DELETE FROM operation_items WHERE operation_id=$id";
                clear.Parameters.AddWithValue("$id",id); clear.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    public void SaveOperationItem(Operation operation,int position)
    {
        lock(sync)
        {
            using var db=Open();
            using var cmd=db.CreateCommand();
            cmd.CommandText="INSERT INTO operation_items(operation_id,position,json) VALUES($id,$p,$j) ON CONFLICT(operation_id,position) DO UPDATE SET json=excluded.json";
            cmd.Parameters.AddWithValue("$id",operation.Id);
            cmd.Parameters.AddWithValue("$p",position);
            cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(operation.Items[position]));
            cmd.ExecuteNonQuery();
        }
    }
    private static T Hydrate<T>(SqliteConnection db,T value)
    {
        if(value is not Operation operation) return value;
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT position,json FROM operation_items WHERE operation_id=$id ORDER BY position";
        cmd.Parameters.AddWithValue("$id",operation.Id);
        using var reader=cmd.ExecuteReader();
        while(reader.Read())
        {
            int position=reader.GetInt32(0);
            if(position<0 || position>=operation.Items.Count) throw new InvalidDataException("操作履歴の位置が不正です");
            operation.Items[position]=JsonSerializer.Deserialize<OperationItem>(reader.GetString(1))!;
        }
        return value;
    }
    public T? Get<T>(string kind, string id)
    {
        lock(sync)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT json FROM records WHERE kind=$k AND id=$i";
            cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$i", id);
            return cmd.ExecuteScalar() is string json ? Hydrate(db,JsonSerializer.Deserialize<T>(json)!) : default;
        }
    }
    public List<T> All<T>(string kind)
    {
        lock(sync)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT json FROM records WHERE kind=$k ORDER BY rowid";
            cmd.Parameters.AddWithValue("$k", kind);
            using var reader = cmd.ExecuteReader();
            var list = new List<T>();
            while(reader.Read()) list.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
            reader.Close();
            return list.Select(value=>Hydrate(db,value)).ToList();
        }
    }
    public void ReplaceMail(Account account, IReadOnlyList<Mail> mails)
    {
        // One account snapshot is committed only after all pages were fetched.
        lock(sync)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using var del = db.CreateCommand(); del.Transaction = tx;
            del.CommandText = "DELETE FROM records WHERE kind='mail' AND json_extract(json,'$.AccountId')=$a";
            del.Parameters.AddWithValue("$a", account.Id); del.ExecuteNonQuery();
            foreach(var mail in mails)
            {
                using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO records VALUES('mail',$i,$j)";
                cmd.Parameters.AddWithValue("$i", mail.Key); cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(mail)); cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
