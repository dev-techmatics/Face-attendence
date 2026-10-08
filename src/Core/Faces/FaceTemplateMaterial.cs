namespace FaceAttendance.Core.Faces;

public enum FaceTemplateKind
{
    /// <summary>A numeric vector we can compare ourselves, in-process.</summary>
    Embedding = 1,

    /// <summary>An opaque handle; only the provider can compare it.</summary>
    ProviderManaged = 2
}

/// <summary>The biometric payload a provider produced for one capture.</summary>
public sealed class FaceTemplateMaterial
{
    public required string ProviderId { get; init; }

    public required FaceTemplateKind Kind { get; init; }

    /// <summary>Embedding length, or 0 when <see cref="Kind"/> is provider-managed.</summary>
    public int Dimension { get; init; }

    public required byte[] Data { get; init; }

    public double QualityScore { get; init; }

    public double DetectionScore { get; init; }

    public FaceCaptureAnalysis? Analysis { get; init; }
}
