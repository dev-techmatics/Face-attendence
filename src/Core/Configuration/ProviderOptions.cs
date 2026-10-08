namespace FaceAttendance.Core.Configuration;

public sealed class OnnxProviderOptions
{
    /// <summary>SCRFD face-detection model (boxes + 5 landmarks).</summary>
    public string DetectorModelPath { get; set; } = "models/scrfd_500m_bnkps.onnx";

    /// <summary>ArcFace recognition model (112x112 input, 512-d output).</summary>
    public string RecognitionModelPath { get; set; } = "models/arcface_r100.onnx";

    /// <summary>Recorded inside ProviderId. Bump it whenever you replace a model file.</summary>
    public string ModelVersion { get; set; } = "v1";

    public float DetectorConfidenceThreshold { get; set; } = 0.5f;

    public float DetectorNmsThreshold { get; set; } = 0.4f;

    /// <summary>Square size the detector input is letterboxed to.</summary>
    public int DetectorInputSize { get; set; } = 640;

    /// <summary>Threads per inference session. 0 lets onnxruntime decide.</summary>
    public int IntraOpThreads { get; set; }

    /// <summary>Concurrent inference permits. Caps CPU burn during the shift-change rush.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// Set when the recognition model expects BGR channel order instead of RGB. Some
    /// ONNX-zoo ArcFace conversions do. Wrong value does not crash, it just lowers every
    /// score, so verify against a known pair after swapping models.
    /// </summary>
    public bool RecognitionInputBgr { get; set; }

    /// <summary>
    /// Set when frames arrive mirrored, as a front-facing phone camera preview usually
    /// does. This flips the sign of the yaw estimate; get it wrong and every left capture
    /// is rejected for being a right one.
    /// </summary>
    public bool MirroredInput { get; set; }
}

public sealed class AwsProviderOptions
{
    public string Region { get; set; } = "ap-south-1";

    /// <summary>Rekognition collection id. Created on startup when missing.</summary>
    public string CollectionId { get; set; } = "face-attendance";

    /// <summary>The 0-100 similarity floor handed to Rekognition itself.</summary>
    public float SearchThresholdPercent { get; set; } = 80f;

    public int MaxFaces { get; set; } = 5;
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>"local" or "s3".</summary>
    public string Provider { get; set; } = "local";

    /// <summary>Root directory for the local store. Relative paths resolve from ContentRoot.</summary>
    public string LocalRootPath { get; set; } = "storage/faces";

    public S3StorageOptions S3 { get; set; } = new();
}

public sealed class S3StorageOptions
{
    public string BucketName { get; set; } = string.Empty;

    public string Region { get; set; } = "ap-south-1";

    public string KeyPrefix { get; set; } = "faces/";

    /// <summary>Biometric images should not sit in a bucket unencrypted.</summary>
    public bool UseServerSideEncryption { get; set; } = true;

    public string? KmsKeyId { get; set; }
}
