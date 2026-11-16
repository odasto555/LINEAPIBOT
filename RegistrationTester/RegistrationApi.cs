using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RegistrationTester;

internal sealed record ConsoleSession(string AccessToken, string Username, string Role);
internal sealed record RegistrationRequest(string PhoneNumber, string CountryCode, string DisplayName, string Password,
    string DeviceId, string DeviceModel);
internal sealed record AcceptedJob(Guid JobId);
internal sealed record JobResult(string? AccountId);
internal sealed record JobError(string Code);
internal sealed record LineLogEntry(DateTimeOffset Timestamp, string Operation, string Phase, string? Host, string? Path,
    int? HttpStatus, long? ElapsedMilliseconds, int? RpcCode, string? Outcome);
internal sealed record RegistrationJob(Guid Id, string Kind, string State, string Stage, DateTimeOffset UpdatedAt,
    DateTimeOffset? OtpExpiresAt, JobResult? Result, JobError? Error)
{
    public string Label => Id.ToString()[..8] + " · " + UpdatedAt.ToLocalTime().ToString("HH:mm:ss");
}

internal sealed class ApiRequestException(HttpStatusCode statusCode, string message) : Exception(message)
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
}

internal sealed class RegistrationApi(Uri baseAddress) : IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        BaseAddress = baseAddress,
        Timeout = TimeSpan.FromSeconds(60)
    };

    internal Uri MonitorUrl => new(baseAddress, "monitor");
    internal Uri BaseAddress => baseAddress;

    internal Task<ConsoleSession> LoginAsync(string username, string password, CancellationToken ct) =>
        ReadAsync<ConsoleSession>(HttpMethod.Post, "v1/auth/login", ct, body: new { username, password });

    internal async Task LogoutAsync(ConsoleSession session, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "v1/auth/logout", ct, session.AccessToken);
    }

    internal Task<RegistrationJob[]> JobsAsync(ConsoleSession session, CancellationToken ct) =>
        ReadAsync<RegistrationJob[]>(HttpMethod.Get, "v1/jobs?limit=50", ct, session.AccessToken);

    internal Task<RegistrationJob> JobAsync(ConsoleSession session, Guid id, CancellationToken ct) =>
        ReadAsync<RegistrationJob>(HttpMethod.Get, $"v1/jobs/{id}", ct, session.AccessToken);

    internal Task<LineLogEntry[]> LineLogAsync(ConsoleSession session, Guid id, CancellationToken ct) =>
        ReadAsync<LineLogEntry[]>(HttpMethod.Get, $"v1/jobs/{id}/line-log", ct, session.AccessToken);

    internal Task<AcceptedJob> RegisterAsync(ConsoleSession session, RegistrationRequest request, Guid requestId, CancellationToken ct) =>
        ReadAsync<AcceptedJob>(HttpMethod.Post, "v1/jobs/registration", ct, session.AccessToken, request, requestId);

    internal async Task SubmitOtpAsync(ConsoleSession session, Guid id, string code, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"v1/jobs/{id}/otp", ct, session.AccessToken, new { code });
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string path, CancellationToken ct, string? token = null,
        object? body = null, Guid? requestId = null)
    {
        using var response = await SendAsync(method, path, ct, token, body, requestId);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new JsonException("API ตอบกลับโดยไม่มีข้อมูล");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct, string? token,
        object? body = null, Guid? requestId = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (requestId is { } id)
            request.Headers.Add("Idempotency-Key", id.ToString());

        var response = await _client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            throw new ApiRequestException(response.StatusCode, await ErrorMessageAsync(response, ct));
        }
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.Ordinal) == true)
        {
            using var problem = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
            if (problem?.RootElement is { ValueKind: JsonValueKind.Object } root &&
                root.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() switch
                {
                    "invalidCredentials" => "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง",
                    "accountBusy" => "มีงานของเบอร์นี้อยู่ในคิวแล้ว",
                    "queueFull" => "คิวเต็ม กรุณาลองอีกครั้ง",
                    "otpExpired" => "OTP หมดอายุแล้ว",
                    "jobNotAwaitingOtp" => "งานนี้ไม่ได้รอ OTP",
                    var code => code ?? "API ตอบกลับข้อผิดพลาด"
                };
            }
        }

        return $"คำขอไม่สำเร็จ (HTTP {(int)response.StatusCode})";
    }

    public void Dispose() => _client.Dispose();
}

