using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using FaceAttendance.Core.Services;

namespace FaceAttendance.Api.Contracts;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record PersonResponse(
    Guid Id,
    string ExternalId,
    string FullName,
    string? Email,
    string? Department,
    PersonStatus Status,
    EnrollmentStatus EnrollmentStatus,
    DateTimeOffset? EnrolledAtUtc,
    DateTimeOffset CreatedAtUtc)
{
    public static PersonResponse From(Person p) => new(
        p.Id, p.ExternalId, p.FullName, p.Email, p.Department,
        p.Status, p.EnrollmentStatus, p.EnrolledAtUtc, p.CreatedAtUtc);
}

/// <summary>What the detector saw. Drives the coaching text on the capture screen.</summary>
public sealed record AnalysisResponse(
    bool FaceDetected,
    int FaceCount,
    double DetectionScore,
    double QualityScore,
    double Sharpness,
    double Brightness,
    int ImageWidth,
    int ImageHeight,
    double? YawDegrees,
    double? PitchDegrees,
    double? RollDegrees,
    FacePose? InferredPose,
    BoxResponse? Box,
    IReadOnlyList<string> Warnings)
{
    public static AnalysisResponse? From(FaceCaptureAnalysis? a) => a is null
        ? null
        : new AnalysisResponse(
            a.FaceDetected,
            a.FaceCount,
            Math.Round(a.DetectionScore, 4),
            Math.Round(a.QualityScore, 4),
            Math.Round(a.Sharpness, 4),
            Math.Round(a.Brightness, 4),
            a.ImageWidth,
            a.ImageHeight,
            a.HeadPose is { } h ? Math.Round(h.YawDegrees, 2) : null,
            a.HeadPose is { } h2 ? Math.Round(h2.PitchDegrees, 2) : null,
            a.HeadPose is { } h3 ? Math.Round(h3.RollDegrees, 2) : null,
            a.InferredPose,
            a.Box is { } b ? new BoxResponse(b.X, b.Y, b.Width, b.Height) : null,
            a.Warnings);
}

public sealed record BoxResponse(float X, float Y, float Width, float Height);

public sealed record CaptureFaceResponse(
    Guid PersonId,
    FacePose Pose,
    Guid TemplateId,
    int Version,
    bool ReplacedPrevious,
    EnrollmentStatus EnrollmentStatus,
    IReadOnlyList<FacePose> CapturedPoses,
    IReadOnlyList<FacePose> MissingPoses,
    bool EnrollmentCompleted,
    AnalysisResponse? Analysis,
    IReadOnlyList<string> Warnings)
{
    public static CaptureFaceResponse From(CaptureFaceResult r) => new(
        r.PersonId, r.Pose, r.TemplateId, r.Version, r.ReplacedPrevious,
        r.EnrollmentStatus, r.CapturedPoses, r.MissingPoses, r.EnrollmentCompleted,
        AnalysisResponse.From(r.Analysis), r.Warnings);
}

public sealed record EnrollmentResponse(
    Guid PersonId,
    string ExternalId,
    string FullName,
    PersonStatus Status,
    EnrollmentStatus EnrollmentStatus,
    DateTimeOffset? EnrolledAtUtc,
    IReadOnlyList<PoseResponse> Poses,
    IReadOnlyList<FacePose> MissingPoses,
    IReadOnlyList<PoseSimilarityResponse> PoseSimilarities,
    IReadOnlyList<string> Warnings)
{
    public static EnrollmentResponse From(EnrollmentSummary s) => new(
        s.PersonId, s.ExternalId, s.FullName, s.Status, s.EnrollmentStatus, s.EnrolledAtUtc,
        [.. s.Poses.Select(p => new PoseResponse(
            p.Pose, p.TemplateId, p.Version, Math.Round(p.QualityScore, 4),
            Math.Round(p.DetectionScore, 4), p.ProviderId, p.ProviderMatchesCurrent, p.CapturedAtUtc))],
        s.MissingPoses,
        [.. s.PoseSimilarities.Select(p => new PoseSimilarityResponse(p.A, p.B, Math.Round(p.Similarity, 4)))],
        s.Warnings);
}

public sealed record PoseResponse(
    FacePose Pose,
    Guid TemplateId,
    int Version,
    double QualityScore,
    double DetectionScore,
    string ProviderId,
    bool ProviderMatchesCurrent,
    DateTimeOffset CapturedAtUtc);

public sealed record PoseSimilarityResponse(FacePose A, FacePose B, double Similarity);

public sealed record RegisterPersonResponse(
    PersonResponse Person,
    IReadOnlyList<CaptureFaceResponse> Captures,
    EnrollmentStatus EnrollmentStatus,
    bool EnrollmentCompleted);

public sealed record MarkAttendanceResponse(
    bool Success,
    AttendanceDecision Decision,
    Guid EventId,
    Guid? PersonId,
    string? ExternalId,
    string? FullName,
    AttendanceDirection? Direction,
    double Score,
    double Threshold,
    FacePose? MatchedPose,
    DateTimeOffset RecordedAtUtc,
    string? Message,
    int ProcessingMs,
    IReadOnlyList<ScoreResponse> TopScores)
{
    public static MarkAttendanceResponse From(MarkAttendanceResult r) => new(
        r.Success, r.Decision, r.EventId, r.PersonId, r.ExternalId, r.FullName, r.Direction,
        Math.Round(r.Score, 4), Math.Round(r.Threshold, 4), r.MatchedPose, r.RecordedAtUtc,
        r.Message, r.ProcessingMs,
        [.. r.TopScores.Select(s => new ScoreResponse(s.PersonId, s.Pose, Math.Round(s.Score, 4)))]);
}

public sealed record ScoreResponse(Guid PersonId, FacePose Pose, double Score);

public sealed record AttendanceEventResponse(
    Guid Id,
    Guid? PersonId,
    string? ExternalId,
    string? FullName,
    AttendanceDirection Direction,
    AttendanceDecision Decision,
    double MatchScore,
    double Threshold,
    FacePose? MatchedPose,
    string? DeviceId,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    int ProcessingMs)
{
    public static AttendanceEventResponse From(AttendanceEvent e) => new(
        e.Id, e.PersonId, e.Person?.ExternalId, e.Person?.FullName, e.Direction, e.Decision,
        Math.Round(e.MatchScore, 4), Math.Round(e.Threshold, 4), e.MatchedPose, e.DeviceId,
        e.FailureReason, e.CreatedAtUtc, e.ProcessingMs);
}

public sealed record PreviewMatchResponse(
    bool IsMatch,
    Guid? PersonId,
    FacePose? Pose,
    double Score,
    double Threshold,
    double? Margin,
    IReadOnlyList<ScoreResponse> TopScores,
    AnalysisResponse? Analysis);

public sealed record ProviderInfoResponse(
    string ProviderId,
    FaceTemplateKind TemplateKind,
    bool CanCompareTemplatesOffline,
    FacePose[] RequiredPoses,
    double VerificationThreshold,
    double IdentificationThreshold,
    double IdentificationMargin,
    double DuplicateEnrollmentThreshold,
    double MinQualityScore,
    double MinDetectionScore,
    int MinImageDimension,
    int MinFaceDimension,
    bool PoseCheckEnabled,
    bool StoreProbeImages,
    int DuplicatePunchWindowMinutes);
