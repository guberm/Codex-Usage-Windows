using System.Text.Json;

namespace CodexUsage;

public record LimitWindow(string Name, int Remaining, long? ResetAt, long? Seconds);
public record Usage(string Plan, List<LimitWindow> Windows, int Resets, string? Balance, DateTimeOffset FetchedAt)
{
    public static Usage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<LimitWindow>();
        if (!root.TryGetProperty("rate_limit", out var core) || core.ValueKind != JsonValueKind.Object ||
            !core.TryGetProperty("primary_window", out var primary) || primary.ValueKind != JsonValueKind.Object)
            throw new FormatException("The response does not contain a Codex limit.");
        AddWindows(core, "Codex", windows);
        if (root.TryGetProperty("additional_rate_limits", out var extras) && extras.ValueKind == JsonValueKind.Array)
            foreach (var item in extras.EnumerateArray())
                if (item.TryGetProperty("rate_limit", out var limits) && limits.ValueKind == JsonValueKind.Object)
                    AddWindows(limits, Text(item, "limit_name") ?? Text(item, "metered_feature") ?? "Additional", windows);
        int resets = 0;
        if (root.TryGetProperty("rate_limit_reset_credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
            resets = (int)Math.Clamp(Number(credits, "available_count") ?? 0, 0, int.MaxValue);
        string? balance = null;
        if (root.TryGetProperty("credits", out var money) && money.ValueKind == JsonValueKind.Object && money.TryGetProperty("balance", out var b)) balance = b.ToString();
        return new(Text(root, "plan_type") ?? "ChatGPT", windows, resets, balance, DateTimeOffset.UtcNow);
    }
    private static void AddWindows(JsonElement limits, string name, List<LimitWindow> windows)
    {
        foreach (var key in new[] { "primary_window", "secondary_window" })
        {
            if (!limits.TryGetProperty(key, out var w) || w.ValueKind == JsonValueKind.Null) continue;
            if (!w.TryGetProperty("used_percent", out var p) || !p.TryGetDouble(out var used) || !double.IsFinite(used))
                throw new FormatException("A limit does not contain a valid usage percentage.");
            var seconds = Number(w, "limit_window_seconds");
            var label = seconds switch { >= 518400 and <= 691200 => "Weekly", >= 72000 and <= 100800 => "Daily", > 0 => $"{seconds / 3600d:0.#}h", _ => key == "primary_window" ? "Primary" : "Secondary" };
            var reset = Number(w, "reset_at");
            if (reset is < 0 or > 253402300799) throw new FormatException("Invalid reset time.");
            windows.Add(new($"{name} · {label}", (int)Math.Floor(Math.Clamp(100 - used, 0, 100) + .5), reset, seconds));
        }
    }
    internal static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    internal static long? Number(JsonElement value, string key) => value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? n : null;
    public static string Countdown(long? resetAt, DateTimeOffset now)
    {
        if (resetAt is null) return "—";
        var seconds = resetAt.Value - now.ToUnixTimeSeconds();
        if (seconds <= 0) return "due";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{span.Hours}h {span.Minutes}m" : $"{Math.Max(1, span.Minutes)}m";
    }
    public static int? Change(int? previous, int current) => previous is null || previous == current ? null : current - previous;
    public static string ResetOutcome(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var code = Text(doc.RootElement, "code");
        return code is "reset" or "already_redeemed" or "no_credit" or "nothing_to_reset" ? code : throw new FormatException("Unknown reset result. Retry with the same request ID.");
    }
}
public record Session(string AccessToken, string RefreshToken, string IdToken, string AccountId, string? Email, long? ExpiresAt)
{
    public static Session Parse(string json, Session? previous = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = Usage.Text(root, "access_token") ?? throw new FormatException("Missing access token.");
        var id = Usage.Text(root, "id_token") ?? previous?.IdToken ?? access;
        var refresh = Usage.Text(root, "refresh_token") ?? previous?.RefreshToken ?? throw new FormatException("Missing refresh token.");
        using var claims = Claims(id);
        using var accessClaims = Claims(access);
        string? Account(JsonElement p) => p.TryGetProperty("https://api.openai.com/auth", out var auth) ? Usage.Text(auth, "chatgpt_account_id") : null;
        var account = Account(claims.RootElement) ?? Account(accessClaims.RootElement) ?? throw new FormatException("Missing ChatGPT account ID.");
        return new(access, refresh, id, account, Usage.Text(claims.RootElement, "email") ?? previous?.Email, Usage.Number(accessClaims.RootElement, "exp"));
    }
    private static JsonDocument Claims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) throw new FormatException("Invalid OAuth token.");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
    }
}
