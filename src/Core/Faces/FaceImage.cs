using System.Security.Cryptography;

namespace FaceAttendance.Core.Faces;

/// <summary>An uploaded image plus the metadata the pipeline needs about it.</summary>
public sealed class FaceImage
{
    public FaceImage(byte[] content, string contentType, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.ToLowerInvariant();
        FileName = fileName;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public byte[] Content { get; }

    public string ContentType { get; }

    public string? FileName { get; }

    /// <summary>Lowercase hex SHA-256 of <see cref="Content"/>.</summary>
    public string Sha256 { get; }

    public int SizeBytes => Content.Length;
}
