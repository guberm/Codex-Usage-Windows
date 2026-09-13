using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace CodexUsage;
public class CodexApi(Storage storage, HttpClient? http = null)
{
    private const string Issuer = "https://auth.openai.com";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private readonly HttpClient client = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private async Task<string> Send(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            request.Headers.UserAgent.ParseAdd("codex-usage-windows/0.1.1");
            request.Headers.Accept.Add(new("application/json"));
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
            return await response.Content.ReadAsStringAsync(ct);
        }
    }
    public async Task<DeviceCode> StartLogin(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Send(new(HttpMethod.Post, Issuer + "/api/accounts/deviceauth/usercode") { Content = Json(new { client_id = ClientId }) }, ct));
        var r = doc.RootElement;
        var code = Usage.Text(r, "user_code") ?? Usage.Text(r, "usercode") ?? throw new FormatException("Missing device code.");
        var interval = r.TryGetProperty("interval", out var p) && int.TryParse(p.ToString(), out var n) ? Math.Clamp(n, 1, 60) : 5;
        return new(Usage.Text(r, "device_auth_id") ?? throw new FormatException("Missing device authorization ID."), code, interval);
    }
    public async Task FinishLogin(DeviceCode code, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(code.Interval), timeout.Token);
            string response;
            try { response = await Send(new(HttpMethod.Post, Issuer + "/api/accounts/deviceauth/token") { Content = Json(new { device_auth_id = code.Id, user_code = code.Code }) }, timeout.Token); }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { continue; }
            using var doc = JsonDocument.Parse(response);
            var r = doc.RootElement;
            var tokenJson = await Send(new(HttpMethod.Post, Issuer + "/oauth/token") { Content = new FormUrlEncodedContent(new Dictionary<string,string> {
                ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["redirect_uri"] = Issuer + "/deviceauth/callback",
                ["code"] = r.GetProperty("authorization_code").GetString()!, ["code_verifier"] = r.GetProperty("code_verifier").GetString()!
            }) }, timeout.Token);
            storage.SaveSession(Session.Parse(tokenJson));
            return;
        }
    }
    private async Task<Session> Refresh(Session session, CancellationToken ct)
    {
        var result = Session.Parse(await Send(new(HttpMethod.Post, Issuer + "/oauth/token") { Content = Json(new { client_id = ClientId, grant_type = "refresh_token", refresh_token = session.RefreshToken }) }, ct), session);
        storage.SaveSession(result);
        return result;
    }
    private async Task<string> Authorized(string path, string? requestId, CancellationToken ct)
    {
        var session = storage.LoadSession() ?? throw new InvalidOperationException("Sign in with ChatGPT first.");
        if (session.ExpiresAt is long expiry && expiry <= DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()) session = await Refresh(session, ct);
        for (var attempt = 0; ; attempt++)
        {
            var request = new HttpRequestMessage(requestId is null ? HttpMethod.Get : HttpMethod.Post, "https://chatgpt.com/backend-api/wham/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Headers.Add("ChatGPT-Account-Id", session.AccountId);
            if (requestId is not null) request.Content = Json(new { redeem_request_id = requestId });
            try { return await Send(request, ct); }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { session = await Refresh(session, ct); }
        }
    }
    public async Task<Usage> Fetch(CancellationToken cancellationToken) => Usage.Parse(await Authorized("usage", null, cancellationToken));
    public async Task<string> ConsumeReset(CancellationToken ct)
    {
        var session = storage.LoadSession() ?? throw new InvalidOperationException("Sign in first.");
        var id = storage.PendingReset(session.AccountId);
        var outcome = Usage.ResetOutcome(await Authorized("rate-limit-reset-credits/consume", id, ct));
        storage.CompleteReset();
        return outcome;
    }
}
public record DeviceCode(string Id, string Code, int Interval);
