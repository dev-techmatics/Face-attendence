using FaceAttendance.Core.Domain;

namespace FaceAttendance.Core.Faces;

/// <summary>One stored template to compare a probe frame against.</summary>
public sealed class FaceCandidate
{
    public required int PersonId { get; init; }

    public required int TemplateId { get; init; }

    public required FacePose Pose { get; init; }

    public required byte[] TemplateData { get; init; }

    /// <summary>
    /// Needed by providers that compare images rather than vectors (AWS CompareFaces).
    /// </summary>
    public string? ImageObjectKey { get; init; }
}
