using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace YnabMcp.Server;

public enum TokenValidationStatus
{
    Valid,
    Invalid,
    Unavailable,
}

public sealed class TokenValidator
{
    private const string ValidatePath = "budgets";
    private const string BearerPrefix = "Bearer ";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, DateTime> Validated = new();

    private readonly IHttpClientFactory _factory;

    public TokenValidator(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    public string? ExtractBearer(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var values))
        {
            return null;
        }

        var auth = values.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = auth[BearerPrefix.Length..].Trim();
        return token.Length > 0 ? token : null;
    }

    public async Task<TokenValidationStatus> ValidateAsync(string token, CancellationToken ct)
    {
        var key = Hash(token);
        if (Validated.TryGetValue(key, out var expires) && expires > DateTime.UtcNow)
        {
            return TokenValidationStatus.Valid;
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, ValidatePath);
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var client = _factory.CreateClient("ynab");
            using var response = await client.SendAsync(message, ct);

            if (response.IsSuccessStatusCode)
            {
                Validated[key] = DateTime.UtcNow.Add(CacheTtl);
                return TokenValidationStatus.Valid;
            }

            return (int)response.StatusCode is 401 or 403
                ? TokenValidationStatus.Invalid
                : TokenValidationStatus.Unavailable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return TokenValidationStatus.Unavailable;
        }
    }

    public static bool TryGetToken(HttpContext? context, out string? token)
    {
        token = context?.Items["YnabToken"] as string;
        return token is not null;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
