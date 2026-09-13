using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace CodexUsage;
public class Storage(string directory)
{
    private string FilePath(string name) => Path.Combine(directory, name);
    public void Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var path = FilePath(name);
        File.WriteAllBytes(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, true);
    }
    public void Save<T>(string name, T data) => Write(name, JsonSerializer.SerializeToUtf8Bytes(data));
    public T? Read<T>(string name) => File.Exists(FilePath(name)) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(FilePath(name))) : default;
    public void SaveSession(Session session) => Write("session.bin", ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session), null, DataProtectionScope.CurrentUser));
    public Session? LoadSession() => File.Exists(FilePath("session.bin")) ? JsonSerializer.Deserialize<Session>(ProtectedData.Unprotect(File.ReadAllBytes(FilePath("session.bin")), null, DataProtectionScope.CurrentUser)) : null;
    public string PendingReset(string accountId)
    {
        var pending = Read<Pending>("pending-reset.json");
        if (pending?.AccountId == accountId) return pending.RequestId;
        pending = new(accountId, Guid.NewGuid().ToString());
        Save("pending-reset.json", pending);
        return pending.RequestId;
    }
    public void CompleteReset() => File.Delete(FilePath("pending-reset.json"));
    public bool HasPendingReset(string accountId) => Read<Pending>("pending-reset.json")?.AccountId == accountId;
    public void ClearSession() { File.Delete(FilePath("session.bin")); File.Delete(FilePath("usage.json")); CompleteReset(); }
    private record Pending(string AccountId, string RequestId);
}
public record Settings(double? Left = null, double? Top = null, bool Pinned = true, bool Notify = true, int WindowIndex = 0);
