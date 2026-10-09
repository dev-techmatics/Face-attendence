namespace FaceAttendance.Core.Abstractions;

/// <summary>
/// Where the actual JPEG/PNG bytes live. Deliberately not the database: face images are large,
/// and a relational table is the wrong place for binary blobs you never query on.
/// </summary>
public interface IFaceImageStore
{
    /// <summary>Persist bytes and return the key used to read them back.</summary>
    Task<string> SaveAsync(
        string objectKey,
        byte[] content,
        string contentType,
        CancellationToken ct = default);

    Task<byte[]?> ReadAsync(string objectKey, CancellationToken ct = default);

    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default);

    /// <summary>
    /// Build the canonical key for a registration capture. Keys are derived, never taken from
    /// client input, so a crafted file name cannot escape the store root.
    /// </summary>
    string BuildEnrollmentKey(string tenantId, int personId, string pose, int version, string contentType);

    string BuildProbeKey(string tenantId, DateTimeOffset whenUtc, Guid fileId, string contentType);
}
