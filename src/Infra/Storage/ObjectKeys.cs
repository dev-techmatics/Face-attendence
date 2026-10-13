namespace FaceAttendance.Infrastructure.Storage;

/// <summary>
/// Canonical object-key layout, shared by every store so a deployment can move from disk to S3
/// without rewriting the keys already in the database.
/// </summary>
/// <remarks>
/// Keys are always derived from server-side values. Nothing here ever interpolates a client
/// supplied file name, which is what keeps a crafted upload name from becoming a path.
/// </remarks>
public static class ObjectKeys
{
    public static string Enrollment(string tenantId, int personId, string pose, int version, string contentType) =>
        string.Join('/',
            "enrollments",
            Sanitize(tenantId),
            personId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{Sanitize(pose).ToLowerInvariant()}-v{version}{ExtensionFor(contentType)}");

    /// <summary>
    /// Date-partitioned so the retention job can walk whole days instead of listing one huge
    /// prefix.
    /// </summary>
    public static string Probe(string tenantId, DateTimeOffset whenUtc, Guid fileId, string contentType) =>
        string.Join('/',
            "probes",
            Sanitize(tenantId),
            whenUtc.UtcDateTime.ToString("yyyy/MM/dd"),
            $"{fileId:N}{ExtensionFor(contentType)}");

    public static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "default";
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
            }
        });
    }
}
