using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;

namespace FaceAttendance.Core.Services;

public sealed record CreatePersonCommand(
    string TenantId,
    string ExternalId,
    string FullName,
    string? Email = null,
    string? Department = null);

/// <summary>One pose upload. All three uploads for a person carry the same PersonId.</summary>
public sealed record CaptureFaceCommand(
    string TenantId,
    int PersonId,
    FacePose Pose,
    FaceImage Image);

public sealed class CaptureFaceResult
{
    public required int PersonId { get; init; }

    public required FacePose Pose { get; init; }

    public required int TemplateId { get; init; }

    public required int Version { get; init; }

    public required string ImageObjectKey { get; init; }

    public FaceCaptureAnalysis? Analysis { get; init; }

    public required EnrollmentStatus EnrollmentStatus { get; init; }

    public IReadOnlyList<FacePose> CapturedPoses { get; init; } = [];

    public IReadOnlyList<FacePose> MissingPoses { get; init; } = [];

    /// <summary>True on the upload that completed the set and flipped the person to Enrolled.</summary>
    public bool EnrollmentCompleted { get; init; }

    /// <summary>Replaced an earlier capture of the same pose.</summary>
    public bool ReplacedPrevious { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class EnrollmentSummary
{
    public required int PersonId { get; init; }

    public required string ExternalId { get; init; }

    public required string FullName { get; init; }

    public required PersonStatus Status { get; init; }

    public required EnrollmentStatus EnrollmentStatus { get; init; }

    public DateTimeOffset? EnrolledAtUtc { get; init; }

    public IReadOnlyList<CapturedPoseInfo> Poses { get; init; } = [];

    public IReadOnlyList<FacePose> MissingPoses { get; init; } = [];

    /// <summary>Pairwise similarity between the stored poses. Null for provider-managed templates.</summary>
    public IReadOnlyList<PoseSimilarity> PoseSimilarities { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record CapturedPoseInfo(
    FacePose Pose,
    int TemplateId,
    int Version,
    double QualityScore,
    double DetectionScore,
    string ProviderId,
    bool ProviderMatchesCurrent,
    DateTimeOffset CapturedAtUtc);

public sealed record PoseSimilarity(FacePose A, FacePose B, double Similarity);

public sealed record MarkAttendanceCommand
{
    public required string TenantId { get; init; }

    /// <summary>
    /// Supply PersonId or ExternalId for a 1:1 verification (fast, and much safer). Leave both
    /// null for a 1:N identification across the tenant.
    /// </summary>
    public int? PersonId { get; init; }

    public string? ExternalId { get; init; }

    public required FaceImage Image { get; init; }

    /// <summary>Null lets the server infer In/Out from the previous event.</summary>
    public AttendanceDirection? Direction { get; init; }

    public string? DeviceId { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public DateTimeOffset? CapturedAtUtc { get; init; }
}

public sealed class MarkAttendanceResult
{
    public required bool Success { get; init; }

    public required AttendanceDecision Decision { get; init; }

    public required int EventId { get; init; }

    public int? PersonId { get; init; }

    public string? ExternalId { get; init; }

    public string? FullName { get; init; }

    public AttendanceDirection? Direction { get; init; }

    public double Score { get; init; }

    public double Threshold { get; init; }

    public FacePose? MatchedPose { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }

    public string? Message { get; init; }

    public int ProcessingMs { get; init; }

    /// <summary>Runner-up scores. Returned only when diagnostics are enabled.</summary>
    public IReadOnlyList<FaceScore> TopScores { get; init; } = [];
}
