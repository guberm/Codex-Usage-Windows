using CodexUsage;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException) { checks++; return; } throw new Exception(name); }
const string payload = """{"plan_type":"pro","rate_limit":{"primary_window":{"used_percent":14,"limit_window_seconds":18000,"reset_at":2000000000},"secondary_window":{"used_percent":44.5,"limit_window_seconds":604800}},"additional_rate_limits":[{"metered_feature":"codex_bengalfox","limit_name":"Spark","rate_limit":{"primary_window":{"used_percent":120,"limit_window_seconds":86400},"secondary_window":{"used_percent":-1}}}],"rate_limit_reset_credits":{"available_count":2},"credits":{"balance":"5"}}""";
var usage = Usage.Parse(payload);
Check(usage.Windows.Count == 4, "Preserve all core and Spark windows");
Check(usage.Windows[0].Remaining == 86 && usage.Windows[1].Remaining == 56, "Remaining and half-up rounding");
Check(usage.Windows[2].Remaining == 0 && usage.Windows[3].Remaining == 100, "Clamp percentages");
Check(usage.Resets == 2 && usage.Balance == "5", "Credits");
var optional = Usage.Parse("""{"rate_limit":{"primary_window":{"used_percent":0,"reset_at":null,"limit_window_seconds":null}}}""");
Check(optional.Windows[0].ResetAt == null && optional.Windows[0].Remaining == 100, "Null optional fields");
Reject(() => Usage.Parse("{}"), "Missing limits must fail");
Reject(() => Usage.Parse("""{"rate_limit":{"primary_window":{}}}"""), "Missing percentage must fail");
Check(Usage.Countdown(2000000000, DateTimeOffset.FromUnixTimeSeconds(2000000000 - 198000)) == "2d 7h", "Countdown");
Check(Usage.Countdown(null, DateTimeOffset.UtcNow) == "—", "Unknown countdown");
Check(Usage.Countdown(1, DateTimeOffset.UtcNow) == "due", "Expired data does not invent a new allowance");
Check(Usage.Change(null, 86) == null && Usage.Change(86, 86) == null && Usage.Change(86, 85) == -1, "Change notifications");
foreach (var code in new[] { "reset", "already_redeemed", "no_credit", "nothing_to_reset" }) Check(Usage.ResetOutcome("{\"code\":\"" + code + "\"}") == code, "Reset outcome");
Reject(() => Usage.ResetOutcome("{\"code\":\"unknown\"}"), "Unknown reset result must remain pending");
string Jwt(object data) => "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(data)).TrimEnd('=').Replace('+','-').Replace('/','_') + ".signature";
var id = Jwt(new Dictionary<string,object> { ["https://api.openai.com/auth"] = new { chatgpt_account_id = "account-one" }, ["email"] = "test@example.invalid" });
var access = Jwt(new { exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
var tokenJson = JsonSerializer.Serialize(new { id_token = id, access_token = access, refresh_token = "test-refresh" });
var session = Session.Parse(tokenJson);
Check(session.AccountId == "account-one" && session.Email == "test@example.invalid", "Account claims");
Check(Session.Parse(JsonSerializer.Serialize(new { access_token = access }), session).RefreshToken == "test-refresh", "Token refresh fallback");
var directory = Path.Combine(Path.GetTempPath(), "codex-usage-check-" + Guid.NewGuid());
try {
    var storage = new Storage(directory);
    storage.SaveSession(session);
    Check(storage.LoadSession() == session, "DPAPI round trip");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "session.bin"))).Contains("test-refresh"), "No plaintext tokens");
    var requestId = storage.PendingReset(session.AccountId);
    Check(storage.HasPendingReset(session.AccountId), "Pending reset can be retried even if balance is now zero");
    Check(new Storage(directory).PendingReset(session.AccountId) == requestId, "Reset survives restart");
    Check(storage.PendingReset("account-two") != requestId, "Reset ID is account bound");
    storage.CompleteReset();
    Check(!storage.HasPendingReset(session.AccountId), "Completed reset is not pending");
    Check(storage.PendingReset(session.AccountId) != requestId, "Completed reset gets a new ID");
    File.WriteAllText(Path.Combine(directory, "activity.jsonl"), "{}\n");
    File.WriteAllText(Path.Combine(directory, "activity.1.jsonl"), "{}\n");
    storage.ClearSession();
    Check(storage.LoadSession() == null, "Sign out deletes credentials");
    Check(!File.Exists(Path.Combine(directory, "activity.jsonl")) && !File.Exists(Path.Combine(directory, "activity.1.jsonl")), "Sign out deletes current and rotated refresh logs");
    var calls = new List<string>();
    var handler = new FakeHandler(async request => {
        calls.Add(request.RequestUri!.AbsolutePath);
        if (calls.Count == 1) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (request.RequestUri!.AbsolutePath == "/oauth/token") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) };
        Check(request.Headers.GetValues("ChatGPT-Account-Id").Single() == "account-one", "Account header");
        await Task.CompletedTask;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
    });
    var api = new CodexApi(storage, new HttpClient(handler));
    storage.SaveSession(session);
    var fetched = await api.Fetch(CancellationToken.None);
    Check(fetched.Windows[0].Remaining == 86 && calls.Count == 3, "401 refreshes once and retries usage");
    var resetBodies = new List<string>();
    var resetApi = new CodexApi(storage, new HttpClient(new FakeHandler(async request => {
        resetBodies.Add(await request.Content!.ReadAsStringAsync());
        return resetBodies.Count == 1 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":\"already_redeemed\"}") };
    })));
    try { await resetApi.ConsumeReset(CancellationToken.None); throw new Exception("500 must fail"); } catch (HttpRequestException) { checks++; }
    Check(storage.HasPendingReset(session.AccountId), "Uncertain reset stays pending");
    Check(await resetApi.ConsumeReset(CancellationToken.None) == "already_redeemed", "Retry resolves completed reset");
    Check(resetBodies.Count == 2 && resetBodies[0] == resetBodies[1], "Same redeem_request_id after failure");
    Check(!storage.HasPendingReset(session.AccountId), "Terminal reset clears pending request");
    var deviceApi = new CodexApi(storage, new HttpClient(new FakeHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"device_auth_id\":\"test\",\"usercode\":\"ABCD-EFGH\",\"interval\":\"5\"}") }))));
    var device = await deviceApi.StartLogin(CancellationToken.None);
    Check(device.Code == "ABCD-EFGH" && device.Interval == 5, "Device login accepts alternate code key and string interval");
} finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
var usageLogType = typeof(Storage).Assembly.GetType("CodexUsage.UsageLog");
Check(usageLogType != null, "UsageLog records local refresh diagnostics");
var recordFetch = usageLogType!.GetMethod("RecordFetch", new[] { typeof(string), typeof(TimeSpan), typeof(Usage), typeof(Usage), typeof(Exception) });
Check(recordFetch != null, "UsageLog exposes the fetch record contract");
var recordSkipped = usageLogType.GetMethod("RecordSkipped", new[] { typeof(string), typeof(string) });
Check(recordSkipped != null, "UsageLog exposes the busy-skip record contract");
object CreateLog(string path) => Activator.CreateInstance(usageLogType, new object?[] { path })!;
void RecordFetch(object logger, string trigger, TimeSpan elapsed, Usage? previous, Usage? current, Exception? error) =>
    recordFetch!.Invoke(logger, new object?[] { trigger, elapsed, previous, current, error });
var logDirectory = Path.Combine(Path.GetTempPath(), "codex-usage-log-check-" + Guid.NewGuid());
try {
    var logger = CreateLog(logDirectory);
    var before = new Usage("pro", new() { new("Codex · Primary", 97, 2000000000, 18000) }, 0, null, DateTimeOffset.UtcNow);
    var after = new Usage("pro", new() { new("Codex · Primary", 40, 2000001000, 18000) }, 0, null, DateTimeOffset.UtcNow);
    RecordFetch(logger, "manual", TimeSpan.FromMilliseconds(125), before, after, null);
    var logPath = Path.Combine(logDirectory, "activity.jsonl");
    var lines = File.ReadAllLines(logPath);
    using (var successEvent = JsonDocument.Parse(lines[0])) {
        var root = successEvent.RootElement;
        Check(root.GetProperty("timestampUtc").GetDateTimeOffset().Offset == TimeSpan.Zero && root.GetProperty("trigger").GetString() == "manual" && root.GetProperty("outcome").GetString() == "success" && root.GetProperty("elapsedMilliseconds").GetInt64() == 125, "Success log records UTC time, cause, result, and duration");
        Check(root.GetProperty("previous")[0].GetProperty("remaining").GetInt32() == 97 && root.GetProperty("previous")[0].GetProperty("resetAt").GetInt64() == 2000000000, "Success log preserves old percentage and reset time");
        Check(root.GetProperty("current")[0].GetProperty("remaining").GetInt32() == 40 && root.GetProperty("current")[0].GetProperty("resetAt").GetInt64() == 2000001000, "Success log preserves new percentage and reset time");
    }
    const string secretToken = "Bearer fake-access-token-123";
    const string secretAccount = "private-account-456";
    var failure = new HttpRequestException("Authorization: " + secretToken + " account_id=" + secretAccount, null, HttpStatusCode.Unauthorized);
    RecordFetch(logger, "timer", TimeSpan.FromMilliseconds(9), after, null, failure);
    var failureLine = File.ReadAllLines(logPath).Last();
    using (var failureEvent = JsonDocument.Parse(failureLine)) {
        var root = failureEvent.RootElement;
        Check(root.GetProperty("outcome").GetString() == "failure" && root.GetProperty("httpStatus").GetInt32() == 401 && root.GetProperty("errorType").GetString() == "HttpRequestException", "Failure log records only safe status and error type");
    }
    Check(!failureLine.Contains(secretToken, StringComparison.Ordinal) && !failureLine.Contains(secretAccount, StringComparison.Ordinal) && !failureLine.Contains("Authorization:", StringComparison.Ordinal), "Failure log omits exception message and secrets");
    recordSkipped!.Invoke(logger, new object?[] { "unlock", "busy" });
    using (var skippedEvent = JsonDocument.Parse(File.ReadAllLines(logPath).Last()))
        Check(skippedEvent.RootElement.GetProperty("trigger").GetString() == "unlock" && skippedEvent.RootElement.GetProperty("outcome").GetString() == "skipped" && skippedEvent.RootElement.GetProperty("reason").GetString() == "busy", "Busy refresh signal is logged as skipped");

    var rotateDirectory = Path.Combine(logDirectory, "rotation");
    var rotateLogger = CreateLog(rotateDirectory);
    var largeUsage = new Usage("pro", new() { new(new string('w', 550_000), 80, 2000000000, 18000) }, 0, null, DateTimeOffset.UtcNow);
    for (var i = 0; i < 3; i++) RecordFetch(rotateLogger, "timer", TimeSpan.Zero, null, largeUsage, null);
    var rotatedPath = Path.Combine(rotateDirectory, "activity.1.jsonl");
    var currentLength = new FileInfo(Path.Combine(rotateDirectory, "activity.jsonl")).Length;
    var rotatedLength = new FileInfo(rotatedPath).Length;
    Check(File.Exists(rotatedPath) && currentLength <= 1_048_576 && rotatedLength <= 1_048_576, "Rotation retains one backup and bounds both files to 1 MiB");
    var oversizedUsage = new Usage("pro", new() { new(new string('x', 1_100_000), 80, 2000000000, 18000) }, 0, null, DateTimeOffset.UtcNow);
    RecordFetch(rotateLogger, "timer", TimeSpan.Zero, null, oversizedUsage, null);
    Check(new FileInfo(Path.Combine(rotateDirectory, "activity.jsonl")).Length <= 1_048_576 && new FileInfo(rotatedPath).Length <= 1_048_576, "Oversized record cannot exceed the rotation bound");

    var blocker = Path.Combine(logDirectory, "not-a-directory");
    File.WriteAllText(blocker, "x");
    var blockedLogger = CreateLog(Path.Combine(blocker, "child"));
    var bestEffort = true;
    try { RecordFetch(blockedLogger, "manual", TimeSpan.Zero, null, after, null); } catch { bestEffort = false; }
    Check(bestEffort, "Log I/O failure does not escape into refresh");
} finally { if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, true); }
Console.WriteLine($"PASS: {checks} checks");

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
