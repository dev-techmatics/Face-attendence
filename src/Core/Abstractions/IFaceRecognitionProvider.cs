using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;

namespace FaceAttendance.Core.Abstractions;

/// <summary>
/// The single seam between the application and whatever actually recognises faces.
/// </summary>
/// <remarks>
/// Implemented three ways in this solution:
/// <list type="bullet">
///   <item><c>OnnxFaceRecognitionProvider</c> - self-hosted SCRFD + ArcFace, embeddings in your
///   own SQL Server. No cloud account, no per-call cost.</item>
///   <item><c>RekognitionFaceRecognitionProvider</c> - AWS Rekognition, for teams that would
///   rather pay per call than own a model.</item>
///   <item><c>DevelopmentFaceRecognitionProvider</c> - deterministic fake so the API, database
///   and front end can be built and tested before any model file exists.</item>
/// </list>
/// Keep this interface free of provider-specific concepts; that is what makes the swap cheap.
/// </remarks>
public interface IFaceRecognitionProvider
{
    /// <summary>
    /// Stable identity of engine + model version, e.g. <c>onnx:scrfd500m+arcface-r100-v1</c>.
    /// Persisted on every template; a change invalidates existing enrolments.
    /// </summary>
    string ProviderId { get; }

    FaceTemplateKind TemplateKind { get; }

    /// <summary>Detect and score a frame without enrolling it. Drives capture-screen feedback.</summary>
    Task<FaceCaptureAnalysis> AnalyzeAsync(FaceImage image, CancellationToken ct = default);

    /// <summary>
    /// Turn a frame into a storable template. Throws <see cref="FaceProcessingException"/>
    /// when the frame is not usable.
    /// </summary>
    Task<FaceTemplateMaterial> CreateTemplateAsync(
        FaceImage image,
        FacePose pose,
        CancellationToken ct = default);

    /// <summary>
    /// Score a probe frame against a candidate set. A single-person candidate set is a 1:1
    /// verification; the whole enrolled population is a 1:N identification.
    /// </summary>
    Task<FaceMatchResult> MatchAsync(
        FaceImage probe,
        IReadOnlyList<FaceCandidate> candidates,
        double threshold,
        int topN = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Compare two stored templates. Used to cross-check that the three registration poses
    /// belong to the same face, and to detect somebody enrolling twice.
    /// </summary>
    /// <returns>Similarity, or null when the provider cannot compare stored templates offline.</returns>
    double? CompareTemplates(byte[] a, byte[] b);

    /// <summary>Release provider-side resources (e.g. delete faces from a Rekognition collection).</summary>
    Task DeleteTemplatesAsync(IReadOnlyList<byte[]> templateData, CancellationToken ct = default);
}
