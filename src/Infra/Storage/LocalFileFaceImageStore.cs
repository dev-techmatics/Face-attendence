using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Infrastructure.Storage;

/// <summary>
/// Filesystem-backed image store. Fine for a single server or a site appliance; move to S3 or
/// Azure Blob the moment you run more than one API instance, because a local folder is not
/// shared state.
/// </summary>
public sealed class LocalFileFaceImageStore : IFaceImageStore
{
    private readonly string _root;
    private readonly ILogger<LocalFileFaceImageStore> _logger;

    public LocalFileFaceImageStore(
        IOptions<StorageOptions> options,
        ILogger<LocalFileFaceImageStore> logger)
    {
        _logger = logger;
        _root = Path.GetFullPath(options.Value.LocalRootPath);
        Directory.CreateDirectory(_root);
        _logger.LogInformation("Face images are stored under {Root}", _root);
    }

    public async Task<string> SaveAsync(
        string objectKey,
        byte[] content,
        string contentType,
        CancellationToken ct = default)
    {
        string path = ResolvePath(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temporary name and move into place, so a crash mid-write cannot leave a
        // truncated JPEG that later fails to decode during verification.
        string temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, content, ct);
        File.Move(temp, path, overwrite: true);

        return objectKey;
    }

    public async Task<byte[]?> ReadAsync(string objectKey, CancellationToken ct = default)
    {
        string path = ResolvePath(objectKey);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        string path = ResolvePath(objectKey);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete face image {Key}", objectKey);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(ResolvePath(objectKey)));

    public string BuildEnrollmentKey(
        string tenantId,
        int personId,
        string pose,
        int version,
        string contentType) =>
        ObjectKeys.Enrollment(tenantId, personId, pose, version, contentType);

    public string BuildProbeKey(
        string tenantId,
        DateTimeOffset whenUtc,
        Guid fileId,
        string contentType) =>
        ObjectKeys.Probe(tenantId, whenUtc, fileId, contentType);

    /// <summary>
    /// Maps a key to a path and refuses anything that would escape the root. Keys are built
    /// server-side, but this is the last line of defence if that ever stops being true.
    /// </summary>
    private string ResolvePath(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new ArgumentException("Object key must not be empty.", nameof(objectKey));
        }

        string combined = Path.GetFullPath(
            Path.Combine(_root, objectKey.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Object key {objectKey} resolves outside the store root.");
        }

        return combined;
    }
}
