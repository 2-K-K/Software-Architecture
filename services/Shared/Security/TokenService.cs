using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

/// <summary>Розібраний і перевірений токен: хто, яка роль, до якого часу дійсний.</summary>
public sealed record TokenPrincipal(string UserId, string Role, DateTimeOffset ExpiresAt);

/// <summary>
/// Токен доступу (ЛР6): base64url(payload) + "." + base64url(HMAC-SHA256(payload, ключ)).
/// payload містить sub (користувач), role (роль) і exp (час закінчення, Unix-секунди).
/// Видає лише Gateway (вхід користувача) і Sales (службовий токен). Перевіряє кожен сервіс самостійно.
/// Ключ задається змінною середовища Auth__Key і в репозиторій не потрапляє.
/// </summary>
public static class TokenService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    public static string Issue(string userId, string role, byte[] key, DateTimeOffset now, TimeSpan lifetime)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = userId,
            role,
            exp = now.Add(lifetime).ToUnixTimeSeconds()
        });
        return Base64Url(payload) + "." + Base64Url(Sign(payload, key));
    }

    /// <summary>Повертає принципала, якщо підпис вірний і токен не прострочений; інакше null.</summary>
    public static TokenPrincipal? Validate(string? token, byte[] key, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;

        byte[] payload, signature;
        try
        {
            payload = FromBase64Url(parts[0]);
            signature = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        // Порівняння за постійний час, щоб не витікала інформація про підпис
        if (!CryptographicOperations.FixedTimeEquals(Sign(payload, key), signature)) return null;

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var expires = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64());
        if (expires <= now) return null;

        return new TokenPrincipal(root.GetProperty("sub").GetString()!, root.GetProperty("role").GetString()!, expires);
    }

    public static TokenPrincipal? FromRequest(HttpRequest request, byte[] key, DateTimeOffset now)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return Validate(header[prefix.Length..].Trim(), key, now);
    }

    /// <summary>
    /// Перевіряє токен і роль. Повертає відповідь 401 (немає дійсного токена) або 403 (роль не та),
    /// або null, якщо доступ дозволено; principal заповнюється лише при успіху.
    /// </summary>
    public static IResult? RequireRole(HttpRequest request, byte[] key, string role, out TokenPrincipal principal)
    {
        principal = null!;
        var p = FromRequest(request, key, DateTimeOffset.UtcNow);
        if (p is null)
            return Results.Json(new { error = "Потрібен дійсний токен доступу." }, statusCode: StatusCodes.Status401Unauthorized);
        if (p.Role != role)
            return Results.Json(new { error = "Недостатньо прав для цієї дії." }, statusCode: StatusCodes.Status403Forbidden);
        principal = p;
        return null;
    }

    /// <summary>Читає ключ з конфігурації. Без ключа сервіс не запускається: секрет не береться з коду.</summary>
    public static byte[] RequireKey(IConfiguration config)
    {
        var key = config["Auth:Key"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException(
                "Задайте ключ підпису токенів у змінній середовища Auth__Key (не менше 32 символів).");
        return Encoding.UTF8.GetBytes(key);
    }

    private static byte[] Sign(byte[] payload, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(payload);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var base64 = s.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }
}

/// <summary>Додає службовий токен (роль service) до кожного вихідного запиту між сервісами.</summary>
public sealed class ServiceTokenHandler(byte[] key) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = TokenService.Issue("sales", "service", key, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, ct);
    }
}