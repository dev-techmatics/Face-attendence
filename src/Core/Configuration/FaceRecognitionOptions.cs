using FaceAttendance.Core.Domain;

namespace FaceAttendance.Core.Configuration;

public sealed class FaceRecognitionOptions
{
    public const string SectionName = "FaceRecognition";

    /// <summary>"onnx" (self-hosted), "aws" (Rekognition), or "development" (deterministic fake).</summary>
    public string Provider { get; set; } = "onnx";

    /// <summary>Poses the capture flow must collect before a person counts as enrolled.</summary>
    /// <summary>
    /// Left empty on purpose. Configuration binding APPENDS to a non-empty array default, so a
    /// default here plus the same values in appsettings.json yields six poses, not three.
    /// <see cref="FaceRecognitionOptionsSetup"/> fills in the standard three when this is empty.
    /// </summary>
    public FacePose[] RequiredPoses { get; set; } = [];

    // ---- Decision thresholds -------------------------------------------------------------
    // Cosine similarity on L2-normalised ArcFace embeddings. Calibrate against your own
    // population before go-live. Rekognition reports 0-100 and its provider rescales to 0-1,
    // so these numbers stay comparable across engines.

    /// <summary>1:1 check, when the device already knows who is claiming to be there.</summary>
    public double VerificationThreshold { get; set; } = 0.42;

    /// <summary>1:N search. Stricter on purpose: more candidates, more chances to be wrong.</summary>
    public double IdentificationThreshold { get; set; } = 0.52;

    /// <summary>
    /// Minimum lead the winner must have over the runner-up person in a 1:N search. Guards
    /// against look-alikes and siblings.
    /// </summary>
    public double IdentificationMargin { get; set; } = 0.06;

    /// <summary>Score above which a new registration is treated as an already-enrolled person.</summary>
    public double DuplicateEnrollmentThreshold { get; set; } = 0.60;

    /// <summary>
    /// Floor for "the three poses are the same person". Front-to-profile similarity is
    /// genuinely low even for one face, so this is a sanity check, not a tight match.
    /// </summary>
    public double MinEnrollmentPairSimilarity { get; set; } = 0.20;

    public bool RejectDuplicateEnrollments { get; set; } = true;

    public bool ValidateEnrollmentConsistency { get; set; } = true;

    /// <summary>Reject the same byte-identical frame submitted for two different poses.</summary>
    public bool RejectDuplicateFrames { get; set; } = true;

    /// <summary>Require the detected yaw to agree with the requested pose.</summary>
    public bool EnforcePoseCheck { get; set; } = true;

    /// <summary>Yaw magnitude, in degrees, that a left/right capture must reach.</summary>
    public double MinProfileYawDegrees { get; set; } = 12.0;

    /// <summary>Yaw magnitude a frontal capture must stay under.</summary>
    public double MaxFrontalYawDegrees { get; set; } = 18.0;

    // ---- Input validation ----------------------------------------------------------------

    public double MinDetectionScore { get; set; } = 0.60;

    public double MinQualityScore { get; set; } = 0.35;

    /// <summary>Shortest acceptable image side, in pixels.</summary>
    public int MinImageDimension { get; set; } = 240;

    /// <summary>Smallest acceptable detected face side, in pixels. Too small means too far away.</summary>
    public int MinFaceDimension { get; set; } = 80;

    public int MaxImageBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Empty means the defaults applied by <see cref="FaceRecognitionOptionsSetup"/>.</summary>
    public string[] AllowedContentTypes { get; set; } = [];

    // ---- Attendance rules ----------------------------------------------------------------

    /// <summary>
    /// Any repeat punch inside this window is collapsed into the first one, whichever direction
    /// it would have been. Set to 0 to allow back-to-back punches.
    /// </summary>
    public int DuplicatePunchWindowMinutes { get; set; } = 2;

    /// <summary>Infer In/Out from the previous event instead of trusting the client.</summary>
    public bool InferDirection { get; set; } = true;

    public int TopN { get; set; } = 5;

    // ---- Retention -----------------------------------------------------------------------

    /// <summary>Keep the attendance probe frame. Needed for disputes; costs storage.</summary>
    public bool StoreProbeImages { get; set; } = true;

    /// <summary>Days to keep probe frames. 0 disables the cleanup job.</summary>
    public int ProbeImageRetentionDays { get; set; } = 30;

    public OnnxProviderOptions Onnx { get; set; } = new();

    public AwsProviderOptions Aws { get; set; } = new();
}
