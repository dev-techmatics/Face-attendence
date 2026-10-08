using System.Security.Cryptography;
using System.Text;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Common;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Infrastructure.Providers.Development;

/// <summary>
/// A deterministic stand-in that performs no recognition whatsoever. It exists so the API, the
/// database schema and the front-end capture flow can be built and tested before anyone
/// downloads a model file.
/// </summary>
/// <remarks>
/// <para>
/// <b>It identifies files, not faces.</b> The embedding is derived from the upload file name, up
/// to the first dash, dot or underscore: alice-front.jpg, alice-left.jpg and alice-right.jpg all
/// produce near-identical vectors, so they enrol as one person and verify against each other.
/// bob-front.jpg will not match alice. Rename the files and the identity changes; the pixels are
/// irrelevant.
/// </para>
/// <para>
/// Selecting this provider outside Development is refused at startup. Treat any attendance row
/// it produces as test data, never as evidence that somebody was present.
/// </para>
/// </remarks>
public sealed class DevelopmentFaceRecognitionProvider : IFaceRecognitionProvider
{
    private const int EmbeddingDimension = 512;

    private readonly FaceRecognitionOptions _options;

    public DevelopmentFaceRecognitionProvider(
        IOptions<FaceRecognitionOptions> options,
        ILogger<DevelopmentFaceRecognitionProvider> logger)
    {
        _options = options.Value;

        logger.LogWarning(
            "FACE RECOGNITION IS STUBBED. The development provider matches on upload file name, " +
            "not on faces. Set FaceRecognition:Provider to onnx or aws before any real use.");
    }

    public string ProviderId => "development:filename-stub:v1";

    public FaceTemplateKind TemplateKind => FaceTemplateKind.Embedding;

    public Task<FaceCaptureAnalysis> AnalyzeAsync(FaceImage image, CancellationToken ct = default) =>
        Task.FromResult(BuildAnalysis(null));

    public Task<FaceTemplateMaterial> CreateTemplateAsync(
        FaceImage image,
        FacePose pose,
        CancellationToken ct = default)
    {
        FaceCaptureAnalysis analysis = BuildAnalysis(pose);
        float[] embedding = BuildEmbedding(IdentitySeedOf(image), pose);

        return Task.FromResult(new FaceTemplateMaterial
        {
            ProviderId = ProviderId,
            Kind = FaceTemplateKind.Embedding,
            Dimension = embedding.Length,
            Data = VectorMath.ToBytes(embedding),
            QualityScore = analysis.QualityScore,
            DetectionScore = analysis.DetectionScore,
            Analysis = analysis
        });
    }

    public Task<FaceMatchResult> MatchAsync(
        FaceImage probe,
        IReadOnlyList<FaceCandidate> candidates,
        double threshold,
        int topN = 5,
        CancellationToken ct = default)
    {
        FaceCaptureAnalysis analysis = BuildAnalysis(null);
        string seed = IdentitySeedOf(probe);
        List<FaceScore> scores = [];

        // Score against each candidate using that candidate pose variant, mirroring how the
        // real provider treats three templates as three chances to match one person.
        foreach (FaceCandidate candidate in candidates)
        {
            float[] stored = VectorMath.FromBytes(candidate.TemplateData);
            float[] probeEmbedding = BuildEmbedding(seed, candidate.Pose);

            if (stored.Length != probeEmbedding.Length)
            {
                continue;
            }

            scores.Add(new FaceScore(
                candidate.PersonId,
                candidate.TemplateId,
                candidate.Pose,
                VectorMath.CosineSimilarity(probeEmbedding, stored)));
        }

        if (scores.Count == 0)
        {
            return Task.FromResult(FaceMatchResult.NoMatch(threshold, analysis: analysis));
        }

        List<FaceScore> bestPerPerson =
        [
            .. scores
                .GroupBy(s => s.PersonId)
                .Select(g => g.MaxBy(s => s.Score)!)
                .OrderByDescending(s => s.Score)
        ];

        FaceScore winner = bestPerPerson[0];
        bool isMatch = winner.Score >= threshold;

        return Task.FromResult(new FaceMatchResult
        {
            IsMatch = isMatch,
            PersonId = isMatch ? winner.PersonId : null,
            TemplateId = isMatch ? winner.TemplateId : null,
            Pose = isMatch ? winner.Pose : null,
            Score = winner.Score,
            Threshold = threshold,
            TopScores = [.. bestPerPerson.Take(Math.Max(1, topN))],
            Margin = bestPerPerson.Count > 1 ? winner.Score - bestPerPerson[1].Score : null,
            ProbeAnalysis = analysis
        });
    }

    public double? CompareTemplates(byte[] a, byte[] b) =>
        VectorMath.CosineSimilarity(VectorMath.FromBytes(a), VectorMath.FromBytes(b));

    public Task DeleteTemplatesAsync(IReadOnlyList<byte[]> templateData, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Reports a healthy, correctly-posed face for any upload so validation gates do not block
    /// a flow test. Quality and dimensions are pinned above the configured floors on purpose.
    /// </summary>
    private FaceCaptureAnalysis BuildAnalysis(FacePose? pose)
    {
        double yaw = pose switch
        {
            FacePose.Left => _options.MinProfileYawDegrees + 10d,
            FacePose.Right => -(_options.MinProfileYawDegrees + 10d),
            _ => 0d
        };

        return new FaceCaptureAnalysis
        {
            FaceDetected = true,
            FaceCount = 1,
            DetectionScore = 0.99d,
            QualityScore = Math.Max(0.9d, _options.MinQualityScore),
            Box = new FaceBox(100f, 100f, 200f, 200f),
            HeadPose = new HeadPose(yaw, 0d, 0d),
            InferredPose = pose ?? FacePose.Front,
            Sharpness = 0.9d,
            Brightness = 0.5d,
            ImageWidth = Math.Max(640, _options.MinImageDimension),
            ImageHeight = Math.Max(640, _options.MinImageDimension),
            Warnings = ["Development provider: no real face analysis was performed."]
        };
    }

    /// <summary>
    /// Identity key for the fake: the file name up to the first dash, dot or underscore. Falls
    /// back to the content hash, which makes each distinct upload its own person.
    /// </summary>
    private static string IdentitySeedOf(FaceImage image)
    {
        if (string.IsNullOrWhiteSpace(image.FileName))
        {
            return image.Sha256;
        }

        string name = Path.GetFileName(image.FileName);
        int cut = name.IndexOfAny(['-', '.', '_']);

        return (cut > 0 ? name[..cut] : name).ToLowerInvariant();
    }

    /// <summary>
    /// Deterministic unit vector for an identity, nudged per pose so the three captures are
    /// similar but not identical: close enough to pass the consistency check, different enough
    /// to exercise per-pose storage.
    /// </summary>
    private static float[] BuildEmbedding(string seed, FacePose pose)
    {
        float[] vector = Fill(seed);
        float[] jitter = Fill($"{seed}:{pose}");

        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] += 0.12f * jitter[i];
        }

        VectorMath.L2NormalizeInPlace(vector);
        return vector;
    }

    /// <summary>Expands a seed into a unit vector by chaining SHA-256 blocks.</summary>
    private static float[] Fill(string seed)
    {
        float[] vector = new float[EmbeddingDimension];
        byte[] source = Encoding.UTF8.GetBytes(seed);
        int written = 0;
        int counter = 0;

        while (written < EmbeddingDimension)
        {
            byte[] block = SHA256.HashData([.. source, .. BitConverter.GetBytes(counter++)]);

            for (int i = 0; i + 1 < block.Length && written < EmbeddingDimension; i += 2)
            {
                vector[written++] = BitConverter.ToInt16(block, i) / (float)short.MaxValue;
            }
        }

        VectorMath.L2NormalizeInPlace(vector);
        return vector;
    }
}
