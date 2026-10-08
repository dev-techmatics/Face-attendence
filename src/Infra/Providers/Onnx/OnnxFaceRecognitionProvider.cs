using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Common;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace FaceAttendance.Infrastructure.Providers.Onnx;

/// <summary>
/// Self-hosted recognition: SCRFD for detection and landmarks, ArcFace for the embedding,
/// cosine similarity for the decision. No cloud account, no per-call charge, and no face image
/// leaving the building.
/// </summary>
public sealed class OnnxFaceRecognitionProvider : IFaceRecognitionProvider, IDisposable
{
    private readonly ScrfdFaceDetector _detector;
    private readonly ArcFaceEmbedder _embedder;
    private readonly OnnxProviderOptions _options;
    private readonly FaceRecognitionOptions _root;
    private readonly SemaphoreSlim _concurrency;
    private readonly ILogger<OnnxFaceRecognitionProvider> _logger;

    public OnnxFaceRecognitionProvider(
        IOptions<FaceRecognitionOptions> options,
        ILogger<OnnxFaceRecognitionProvider> logger)
    {
        _root = options.Value;
        _options = options.Value.Onnx;
        _logger = logger;
        _detector = new ScrfdFaceDetector(_options);
        _embedder = new ArcFaceEmbedder(_options);

        // Inference is CPU-bound. Without a cap, a shift-change rush would spawn as many
        // parallel inferences as there are requests and starve the whole process.
        _concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));

        ProviderId = $"onnx:scrfd+arcface:{_options.ModelVersion}:d{_embedder.Dimension}";
        _logger.LogInformation(
            "Local face recognition ready: {ProviderId}, {Dimension}-d embeddings, max {Concurrency} concurrent inferences",
            ProviderId, _embedder.Dimension, _options.MaxConcurrency);
    }

    public string ProviderId { get; }

    public FaceTemplateKind TemplateKind => FaceTemplateKind.Embedding;

    public async Task<FaceCaptureAnalysis> AnalyzeAsync(FaceImage image, CancellationToken ct = default)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using SKBitmap bitmap = ImageOps.Decode(image.Content);
            return Analyze(bitmap).Analysis;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public async Task<FaceTemplateMaterial> CreateTemplateAsync(
        FaceImage image,
        FacePose pose,
        CancellationToken ct = default)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using SKBitmap bitmap = ImageOps.Decode(image.Content);
            (FaceCaptureAnalysis analysis, DetectedFace? face) = Analyze(bitmap);

            if (face is null)
            {
                throw new FaceProcessingException(FaceError.NoFaceDetected, "No face was found in the frame.");
            }

            float[] embedding = EmbedFace(bitmap, face);

            return new FaceTemplateMaterial
            {
                ProviderId = ProviderId,
                Kind = FaceTemplateKind.Embedding,
                Dimension = embedding.Length,
                Data = VectorMath.ToBytes(embedding),
                QualityScore = analysis.QualityScore,
                DetectionScore = analysis.DetectionScore,
                Analysis = analysis
            };
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public async Task<FaceMatchResult> MatchAsync(
        FaceImage probe,
        IReadOnlyList<FaceCandidate> candidates,
        double threshold,
        int topN = 5,
        CancellationToken ct = default)
    {
        float[] probeEmbedding;
        FaceCaptureAnalysis analysis;

        await _concurrency.WaitAsync(ct);
        try
        {
            using SKBitmap bitmap = ImageOps.Decode(probe.Content);
            (analysis, DetectedFace? face) = Analyze(bitmap);

            if (face is null)
            {
                throw new FaceProcessingException(FaceError.NoFaceDetected, "No face was found in the frame.");
            }

            probeEmbedding = EmbedFace(bitmap, face);
        }
        finally
        {
            _concurrency.Release();
        }

        return ScoreCandidates(probeEmbedding, candidates, threshold, topN, analysis);
    }

    /// <summary>
    /// Pure arithmetic, no model involved: this is the step that would otherwise be a paid API
    /// call, and it is why the whole thing works against an ordinary relational database.
    /// </summary>
    private static FaceMatchResult ScoreCandidates(
        float[] probeEmbedding,
        IReadOnlyList<FaceCandidate> candidates,
        double threshold,
        int topN,
        FaceCaptureAnalysis analysis)
    {
        List<FaceScore> scores = new(candidates.Count);

        foreach (FaceCandidate candidate in candidates)
        {
            float[] stored = VectorMath.FromBytes(candidate.TemplateData);

            if (stored.Length != probeEmbedding.Length)
            {
                // A template from a different model. Skipped rather than thrown, so one stale
                // row cannot take down attendance for everybody.
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
            return FaceMatchResult.NoMatch(threshold, analysis: analysis);
        }

        // A person is represented by three poses; their score is the best of the three, since a
        // profile probe should be allowed to match the profile template and ignore the others.
        List<FaceScore> bestPerPerson =
        [
            .. scores
                .GroupBy(s => s.PersonId)
                .Select(g => g.MaxBy(s => s.Score)!)
                .OrderByDescending(s => s.Score)
        ];

        FaceScore winner = bestPerPerson[0];
        double? margin = bestPerPerson.Count > 1
            ? winner.Score - bestPerPerson[1].Score
            : null;

        bool isMatch = winner.Score >= threshold;

        return new FaceMatchResult
        {
            IsMatch = isMatch,
            PersonId = isMatch ? winner.PersonId : null,
            TemplateId = isMatch ? winner.TemplateId : null,
            Pose = isMatch ? winner.Pose : null,
            Score = winner.Score,
            Threshold = threshold,
            TopScores = [.. bestPerPerson.Take(Math.Max(1, topN))],
            Margin = margin,
            ProbeAnalysis = analysis
        };
    }

    public double? CompareTemplates(byte[] a, byte[] b) =>
        VectorMath.CosineSimilarity(VectorMath.FromBytes(a), VectorMath.FromBytes(b));

    /// <summary>Nothing to release: embeddings live only in your database.</summary>
    public Task DeleteTemplatesAsync(IReadOnlyList<byte[]> templateData, CancellationToken ct = default) =>
        Task.CompletedTask;

    public void Dispose()
    {
        _detector.Dispose();
        _embedder.Dispose();
        _concurrency.Dispose();
    }

    /// <summary>
    /// Detect, pick the subject, and score the frame. When several faces are present the
    /// largest wins, on the assumption that the person at the kiosk is nearer than passers-by.
    /// </summary>
    private (FaceCaptureAnalysis Analysis, DetectedFace? Face) Analyze(SKBitmap bitmap)
    {
        IReadOnlyList<DetectedFace> faces = _detector.Detect(bitmap);

        if (faces.Count == 0)
        {
            return (new FaceCaptureAnalysis
            {
                FaceDetected = false,
                FaceCount = 0,
                ImageWidth = bitmap.Width,
                ImageHeight = bitmap.Height,
                Warnings = ["No face was detected in the frame."]
            }, null);
        }

        DetectedFace primary = faces.MaxBy(f => f.Box.Area)!;
        List<string> warnings = [];

        if (faces.Count > 1)
        {
            warnings.Add($"{faces.Count} faces were detected; the largest was used.");
        }

        ImageOps.Rgb crop = ImageOps.Crop(bitmap, ClampToImage(primary.Box, bitmap), 160);

        double laplacianVariance = ImageOps.LaplacianVariance(crop);
        double brightness = ImageOps.MeanBrightness(crop);

        // Heuristic normalisations. The divisors are empirical and worth re-tuning against your
        // own cameras; what matters is that they are monotonic and stable.
        double sharpness = Math.Clamp(laplacianVariance / 500d, 0d, 1d);
        double exposure = 1d - Math.Clamp(Math.Abs(brightness - 0.5d) * 2d, 0d, 1d);
        double faceSize = Math.Clamp(Math.Min(primary.Box.Width, primary.Box.Height) / 200d, 0d, 1d);
        double quality = (0.4d * sharpness) + (0.3d * exposure) + (0.3d * faceSize);

        if (sharpness < 0.3d)
        {
            warnings.Add("The frame looks soft or motion-blurred.");
        }

        if (brightness < 0.25d)
        {
            warnings.Add("The frame is underexposed.");
        }
        else if (brightness > 0.85d)
        {
            warnings.Add("The frame is overexposed.");
        }

        HeadPose head = FaceAligner.EstimateHeadPose(primary.Landmarks, _options.MirroredInput);

        FacePose inferred = head.YawDegrees >= _root.MinProfileYawDegrees
            ? FacePose.Left
            : head.YawDegrees <= -_root.MinProfileYawDegrees
                ? FacePose.Right
                : FacePose.Front;

        FaceCaptureAnalysis analysis = new()
        {
            FaceDetected = true,
            FaceCount = faces.Count,
            DetectionScore = primary.Score,
            QualityScore = quality,
            Box = primary.Box,
            Landmarks = primary.Landmarks,
            HeadPose = head,
            InferredPose = inferred,
            Sharpness = sharpness,
            Brightness = brightness,
            ImageWidth = bitmap.Width,
            ImageHeight = bitmap.Height,
            Warnings = warnings
        };

        return (analysis, primary);
    }

    private float[] EmbedFace(SKBitmap bitmap, DetectedFace face)
    {
        SKMatrix transform = FaceAligner.ComputeTransform(face.Landmarks);
        ImageOps.Rgb aligned = ImageOps.WarpAligned(bitmap, transform, FaceAligner.OutputSize);
        return _embedder.Embed(aligned);
    }

    /// <summary>
    /// Keep a box inside the image. Anchor decoding can place an edge slightly outside the
    /// frame for a face at the border, which would make the crop fail.
    /// </summary>
    private static FaceBox ClampToImage(FaceBox box, SKBitmap bitmap)
    {
        float x = Math.Clamp(box.X, 0f, bitmap.Width - 1f);
        float y = Math.Clamp(box.Y, 0f, bitmap.Height - 1f);
        float width = Math.Clamp(box.Width, 1f, bitmap.Width - x);
        float height = Math.Clamp(box.Height, 1f, bitmap.Height - y);

        return new FaceBox(x, y, width, height);
    }
}
