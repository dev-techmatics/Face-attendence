using FaceAttendance.Core.Domain;

namespace FaceAttendance.Core.Faces;

public sealed record FaceScore(Guid PersonId, Guid TemplateId, FacePose Pose, double Score);

public sealed class FaceMatchResult
{
    public bool IsMatch { get; init; }

    public Guid? PersonId { get; init; }

    public Guid? TemplateId { get; init; }

    public FacePose? Pose { get; init; }

    /// <summary>Best score seen, whether or not it cleared the threshold.</summary>
    public double Score { get; init; }

    public double Threshold { get; init; }

    /// <summary>Best scores per person, descending. Useful for tuning and for audit.</summary>
    public IReadOnlyList<FaceScore> TopScores { get; init; } = [];

    public FaceCaptureAnalysis? ProbeAnalysis { get; init; }

    /// <summary>
    /// Gap between the best and second-best <i>person</i>. A tiny margin on a 1:N search means
    /// two enrolled people look alike to the model; treat it as inconclusive rather than a match.
    /// </summary>
    public double? Margin { get; init; }

    public static FaceMatchResult NoMatch(double threshold, double bestScore = 0d,
        IReadOnlyList<FaceScore>? top = null, FaceCaptureAnalysis? analysis = null) => new()
        {
            IsMatch = false,
            Score = bestScore,
            Threshold = threshold,
            TopScores = top ?? [],
            ProbeAnalysis = analysis
        };
}
