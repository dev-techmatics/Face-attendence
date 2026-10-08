using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Api.Infrastructure;

public sealed class ApiKeyAuthOptions
{
    public const string SectionName = "ApiKeys";

    /// <summary>Keys that may do everything, including reading stored face images.</summary>
    public string[] Admin { get; set; } = [];

    /// <summary>
    /// Keys issued to kiosks and mobile apps. They can mark attendance and nothing else. A
    /// device key is the one most likely to leak, so it must not be able to enrol a new face or
    /// read a stored image.
    /// </summary>
    public string[] Device { get; set; } = [];
}

public sealed class ApiKeySchemeOptions : AuthenticationSchemeOptions;

/// <summary>
/// Header-based API key authentication. Adequate for server-to-server and kiosk traffic over
/// TLS; put a real identity provider in front of it before exposing an admin UI to end users.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeySchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ApiKeyAuthOptions> keys)
    : AuthenticationHandler<ApiKeySchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    public const string AdminRole = "admin";
    public const string DeviceRole = "device";

    private readonly ApiKeyAuthOptions _keys = keys.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out Microsoft.Extensions.Primitives.StringValues values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? presented = values.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(presented))
        {
            return Task.FromResult(AuthenticateResult.Fail("Empty API key."));
        }

        string? role = Match(presented);

        if (role is null)
        {
            // Deliberately vague: a specific message would help an attacker enumerate keys.
            Logger.LogWarning("Rejected an unrecognised API key from {Ip}", Request.HttpContext.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Unrecognised API key."));
        }

        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Name, role),
                new Claim(ClaimTypes.Role, role)
            ],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>
    /// Compares in fixed time. A plain equality check leaks key material through timing, which
    /// is cheap to avoid and awkward to explain after the fact.
    /// </summary>
    private string? Match(string presented)
    {
        foreach (string key in _keys.Admin)
        {
            if (FixedTimeEquals(presented, key))
            {
                return AdminRole;
            }
        }

        foreach (string key in _keys.Device)
        {
            if (FixedTimeEquals(presented, key))
            {
                return DeviceRole;
            }
        }

        return null;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (string.IsNullOrEmpty(b))
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));
    }
}
