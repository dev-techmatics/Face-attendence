using FaceAttendance.Core.Domain;

namespace FaceAttendance.Core.Faces;

/// <summary>
/// What the provider saw in a frame, before any decision is taken. Returned to the capture
/// screen so the front end can coach the user ("move closer", "turn further left").
/// </summary>
public sealed class FaceCaptureAnalysis
{
    public bool FaceDetected { get; init; }

    public int FaceCount { get; init; }

    public double DetectionScore { get; init; }

    /// <summary>0..1 composite of sharpness, exposure and face size.</summary>
    public double QualityScore { get; init; }

    public FaceBox? Box { get; init; }

    public FaceLandmarks? Landmarks { get; init; }

    public HeadPose? HeadPose { get; init; }

    /// <summary>Pose bucket inferred from yaw, when the provider can estimate it.</summary>
    public FacePose? InferredPose { get; init; }

    public double Sharpness { get; init; }

    public double Brightness { get; init; }

    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    /// <summary>Non-fatal observations worth surfacing to the operator.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
