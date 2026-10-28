using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text.Json;

namespace CodexUsage;

public sealed class UsageLog
{
    private const long MaxFileBytes = 1024 * 1024;
    private const string CurrentFileName = "activity.jsonl";
    private const string RotatedFileName = "activity.1.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string directory;
    private readonly object gate = new();

    public UsageLog(string directory) => this.directory = directory;

    public void RecordFetch(string trigger, TimeSpan elapsed, Usage? previous, Usage? current, Exception? error)
    {
        int? httpStatus = error is HttpRequestException { StatusCode: HttpStatusCode status } ? (int)status : null;
        var entry = new UsageEvent(
            "fetch",
            DateTimeOffset.UtcNow,
            SafeTrigger(trigger),
            Math.Max(0L, (long)elapsed.TotalMilliseconds),
            error is null ? "success" : "failure",
            httpStatus,
            error?.GetType().Name,
            null,
            Snapshot(previous),
            Snapshot(current));
        TryWrite(entry);
    }

    public void RecordSkipped(string trigger, string reason)
    {
        var entry = new UsageEvent("refresh", DateTimeOffset.UtcNow, SafeTrigger(trigger), null,
            "skipped", null, null, reason == "busy" ? "busy" : "other", null, null);
        TryWrite(entry);
    }

    public void Clear()
    {
        lock (gate)
        {
            TryDelete(Path.Combine(directory, CurrentFileName));
            TryDelete(Path.Combine(directory, RotatedFileName));
        }
    }

    private void TryWrite(UsageEvent entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        if (bytes.Length + 1 > MaxFileBytes) return;

        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var current = Path.Combine(directory, CurrentFileName);
                var rotated = Path.Combine(directory, RotatedFileName);

                if (File.Exists(rotated) && new FileInfo(rotated).Length > MaxFileBytes)
                    File.Delete(rotated);

                if (File.Exists(current))
                {
                    var length = new FileInfo(current).Length;
                    if (length > MaxFileBytes)
                    {
                        File.Delete(current);
                        length = 0;
                    }
                    if (length > 0 && length + bytes.Length + 1 > MaxFileBytes)
                        File.Move(current, rotated, true);
                }

                using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
            }
            catch (Exception e) when (IsExpectedIoFailure(e))
            {
                // Diagnostics must never prevent the usage request or UI update.
            }
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (IsExpectedIoFailure(e)) { }
    }

    private static bool IsExpectedIoFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;

    private static LimitSnapshot[]? Snapshot(Usage? usage) => usage?.Windows
        .Select(window => new LimitSnapshot(window.Name, window.Remaining, window.ResetAt, window.Seconds))
        .ToArray();

    private static string SafeTrigger(string trigger) => trigger switch
    {
        "startup" or "manual" or "timer" or "unlock" or "resume" or "network-restored" or "sign-in" or "reset-precheck" or "reset-postcheck" => trigger,
        _ => "other"
    };

    private sealed record LimitSnapshot(string Name, int Remaining, long? ResetAt, long? Seconds);
    private sealed record UsageEvent(string Type, DateTimeOffset TimestampUtc, string Trigger,
        long? ElapsedMilliseconds, string Outcome, int? HttpStatus, string? ErrorType, string? Reason,
        LimitSnapshot[]? Previous, LimitSnapshot[]? Current);
}
