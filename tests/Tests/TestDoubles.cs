using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Common;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using FaceAttendance.Infrastructure.Storage;

namespace FaceAttendance.Tests;

/// <summary>In-memory image store that also lets a test assert what was written and deleted.</summary>
public sealed class FakeImageStore : IFaceImageStore
{
    public Dictionary<string, byte[]> Saved { get; } = [];

    public Task<string> SaveAsync(string objectKey, byte[] content, string contentType, CancellationToken ct = default)
    {
        Saved[objectKey] = content;
        return Task.FromResult(objectKey);
    }

    public Task<byte[]?> ReadAsync(string objectKey, CancellationToken ct = default) =>
        Task.FromResult(Saved.TryGetValue(objectKey, out byte[]? v) ? v : null);

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        Saved.Remove(objectKey);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default) =>
        Task.FromResult(Saved.ContainsKey(objectKey));

    public string BuildEnrollmentKey(string tenantId, Guid personId, string pose, int version, string contentType) =>
        ObjectKeys.Enrollment(tenantId, personId, pose, version, contentType);

    public string BuildProbeKey(string tenantId, DateTimeOffset whenUtc, Guid eventId, string contentType) =>
        ObjectKeys.Probe(tenantId, whenUtc, eventId, contentType);
}

/// <summary>
/// A provider whose output a test controls directly: set <see cref="Identity"/> and every
/// template produced belongs to that notional person. Lets the enrolment rules be tested
/// without any model, image decoding, or randomness.
/// </summary>
public sealed class ScriptedProvider : IFaceRecognitionProvider
{
    private const int Dimension = 128;

    /// <summary>Whose face the next capture represents.</summary>
    public string Identity { get; set; } = "default";

    /// <summary>When set, captures for this pose throw, to exercise failure paths.</summary>
    public FacePose? FailOn { get; set; }

    public string ProviderId => "test:scripted:v1";

    public FaceTemplateKind TemplateKind => FaceTemplateKind.Embedding;

    public Task<FaceCaptureAnalysis> AnalyzeAsync(FaceImage image, CancellationToken ct = default) =>
        Task.FromResult(Analysis());

    public Task<FaceTemplateMaterial> CreateTemplateAsync(
        FaceImage image,
        FacePose pose,
        CancellationToken ct = default)
    {
        if (FailOn == pose)
        {
            throw new FaceProcessingException(FaceError.LowQuality, $"Scripted failure on {pose}.");
        }

        float[] embedding = EmbeddingFor(Identity);

        return Task.FromResult(new FaceTemplateMaterial
        {
            ProviderId = ProviderId,
            Kind = FaceTemplateKind.Embedding,
            Dimension = embedding.Length,
            Data = VectorMath.ToBytes(embedding),
            QualityScore = 0.9,
            DetectionScore = 0.95,
            Analysis = Analysis()
        });
    }

    public Task<FaceMatchResult> MatchAsync(
        FaceImage probe,
        IReadOnlyList<FaceCandidate> candidates,
        double threshold,
        int topN = 5,
        CancellationToken ct = default)
    {
        float[] probeEmbedding = EmbeddingFor(Identity);

        List<FaceScore> scores =
        [
            .. candidates.Select(c => new FaceScore(
                c.PersonId,
                c.TemplateId,
                c.Pose,
                VectorMath.CosineSimilarity(probeEmbedding, VectorMath.FromBytes(c.TemplateData))))
        ];

        if (scores.Count == 0)
        {
            return Task.FromResult(FaceMatchResult.NoMatch(threshold, analysis: Analysis()));
        }

        List<FaceScore> best =
        [
            .. scores.GroupBy(s => s.PersonId).Select(g => g.MaxBy(s => s.Score)!).OrderByDescending(s => s.Score)
        ];

        FaceScore winner = best[0];
        bool isMatch = winner.Score >= threshold;

        return Task.FromResult(new FaceMatchResult
        {
            IsMatch = isMatch,
            PersonId = isMatch ? winner.PersonId : null,
            TemplateId = isMatch ? winner.TemplateId : null,
            Pose = isMatch ? winner.Pose : null,
            Score = winner.Score,
            Threshold = threshold,
            TopScores = [.. best.Take(topN)],
            Margin = best.Count > 1 ? winner.Score - best[1].Score : null,
            ProbeAnalysis = Analysis()
        });
    }

    public double? CompareTemplates(byte[] a, byte[] b) =>
        VectorMath.CosineSimilarity(VectorMath.FromBytes(a), VectorMath.FromBytes(b));

    public Task DeleteTemplatesAsync(IReadOnlyList<byte[]> templateData, CancellationToken ct = default) =>
        Task.CompletedTask;

    private static FaceCaptureAnalysis Analysis() => new()
    {
        FaceDetected = true,
        FaceCount = 1,
        DetectionScore = 0.95,
        QualityScore = 0.9,
        Box = new FaceBox(100, 100, 200, 200),
        HeadPose = new HeadPose(0, 0, 0),
        Sharpness = 0.8,
        Brightness = 0.5,
        ImageWidth = 640,
        ImageHeight = 640,
        Warnings = []
    };

    /// <summary>
    /// One fixed orthogonal-ish direction per identity. Same identity scores 1.0 against
    /// itself, different identities score near 0, which is exactly the property the enrolment
    /// rules are written against.
    /// </summary>
    private static float[] EmbeddingFor(string identity)
    {
        float[] vector = new float[Dimension];
        int slot = Math.Abs(identity.GetHashCode(StringComparison.Ordinal)) % Dimension;
        vector[slot] = 1f;
        return vector;
    }
}
