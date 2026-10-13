using Amazon.S3;
using Amazon.S3.Model;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Infrastructure.Storage;

/// <summary>
/// S3-backed image store, for when the API runs on more than one instance. Note that this is
/// independent of the recognition provider: you can keep images in S3 while matching locally
/// with ONNX, or keep images on disk while matching in Rekognition.
/// </summary>
public sealed class S3FaceImageStore : IFaceImageStore
{
    private readonly IAmazonS3 _s3;
    private readonly S3StorageOptions _options;
    private readonly ILogger<S3FaceImageStore> _logger;


    public S3FaceImageStore(
        IAmazonS3 s3,
        IOptions<StorageOptions> options,
        ILogger<S3FaceImageStore> logger)
    {
        _s3 = s3;
        _options = options.Value.S3;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.BucketName))
        {
            throw new InvalidOperationException(
                "Storage:S3:BucketName must be set when Storage:Provider is s3.");
        }
    }

    public async Task<string> SaveAsync(
        string objectKey,
        byte[] content,
        string contentType,
        CancellationToken ct = default)
    {
        using MemoryStream stream = new(content, writable: false);

        PutObjectRequest request = new()
        {
            BucketName = _options.BucketName,
            Key = Prefixed(objectKey),
            InputStream = stream,
            ContentType = contentType,
            // Biometric images are personal data under GDPR, DPDP and most state biometric
            // statutes. Encrypting at rest is the minimum, not a nice-to-have.
            ServerSideEncryptionMethod = _options.UseServerSideEncryption
                ? (string.IsNullOrWhiteSpace(_options.KmsKeyId)
                    ? ServerSideEncryptionMethod.AES256
                    : ServerSideEncryptionMethod.AWSKMS)
                : null,
            ServerSideEncryptionKeyManagementServiceKeyId =
                string.IsNullOrWhiteSpace(_options.KmsKeyId) ? null : _options.KmsKeyId
        };

        await _s3.PutObjectAsync(request, ct);
        return objectKey;
    }

    public async Task<byte[]?> ReadAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            using GetObjectResponse response = await _s3.GetObjectAsync(
                _options.BucketName, Prefixed(objectKey), ct);

            using MemoryStream buffer = new();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            await _s3.DeleteObjectAsync(_options.BucketName, Prefixed(objectKey), ct);
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete S3 object {Key}", objectKey);
        }
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_options.BucketName, Prefixed(objectKey), ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public string BuildEnrollmentKey(string tenantId, int personId, string pose, int version, string contentType) =>
        ObjectKeys.Enrollment(tenantId, personId, pose, version, contentType);

    public string BuildProbeKey(string tenantId, DateTimeOffset whenUtc, Guid fileId, string contentType) =>
        ObjectKeys.Probe(tenantId, whenUtc, fileId, contentType);

    private string Prefixed(string objectKey) => $"{_options.KeyPrefix.TrimEnd('/')}/{objectKey}";
}
