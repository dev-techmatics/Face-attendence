namespace FaceAttendance.Core.Domain;

/// <summary>
/// One registered face capture for one person at one pose.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TemplateData"/> is the biometric payload, and its meaning depends on
/// <see cref="ProviderId"/>: for the local ONNX provider it is a 512-dimension float32
/// embedding; for AWS Rekognition it is the provider-side face identifier.
/// </para>
/// <para>
/// Templates are therefore <b>not portable between providers or model versions</b>. The
/// <see cref="ProviderId"/> is stored on every row precisely so that swapping the engine
/// marks the old enrolments <see cref="EnrollmentStatus.Stale"/> instead of silently
/// comparing incompatible vectors and rejecting everybody.
/// </para>
/// </remarks>
public class FaceTemplate
{
    /// <summary>Assigned by SQL Server on insert (IDENTITY, starting at 10001). Never set it in code.</summary>
    public int Id { get; set; }

    public int PersonId { get; set; }

    public Person? Person { get; set; }

    public FacePose Pose { get; set; }

    /// <summary>Recognition engine + model version that produced <see cref="TemplateData"/>.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Embedding length, or 0 for provider-managed templates.</summary>
    public int Dimension { get; set; }

    /// <summary>Embedding bytes (float32, little endian, L2-normalised) or provider reference.</summary>
    public byte[] TemplateData { get; set; } = [];

    public double QualityScore { get; set; }

    public double DetectionScore { get; set; }

    /// <summary>Key of the stored source image in the configured image store.</summary>
    public string ImageObjectKey { get; set; } = string.Empty;

    public string ImageContentType { get; set; } = string.Empty;

    public long ImageSizeBytes { get; set; }

    /// <summary>Hex SHA-256 of the uploaded bytes. Used to spot the same frame re-sent for 3 poses.</summary>
    public string ImageSha256 { get; set; } = string.Empty;

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    /// <summary>False once replaced by a re-capture. Superseded rows are kept for audit.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Increments each time this pose is re-captured for the person.</summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAtUtc { get; set; }
}
