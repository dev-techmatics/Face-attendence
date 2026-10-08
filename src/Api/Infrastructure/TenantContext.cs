namespace FaceAttendance.Api.Infrastructure;

/// <summary>
/// Resolves which organisation a request belongs to. Single-site deployments can ignore this
/// entirely and everything lands in the default tenant.
/// </summary>
/// <remarks>
/// The header is trusted because an API key is already required to reach any endpoint. If you
/// later issue keys to parties who must not see each other data, bind the tenant to the key
/// instead of reading it from a header a caller controls.
/// </remarks>
public interface ITenantContext
{
    string TenantId { get; }
}

public sealed class HeaderTenantContext(IHttpContextAccessor accessor, IConfiguration configuration) : ITenantContext
{
    public const string HeaderName = "X-Tenant-Id";

    public string TenantId
    {
        get
        {
            string? header = accessor.HttpContext?.Request.Headers[HeaderName].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(header))
            {
                return Normalize(header);
            }

            return Normalize(configuration["DefaultTenantId"] ?? "default");
        }
    }

    /// <summary>
    /// Constrains the value to a safe character set and length: it ends up in object storage
    /// keys, so an unsanitised tenant id would be a path-traversal vector.
    /// </summary>
    private static string Normalize(string value)
    {
        string trimmed = value.Trim();

        if (trimmed.Length > 64)
        {
            trimmed = trimmed[..64];
        }

        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
            }
        });
    }
}
