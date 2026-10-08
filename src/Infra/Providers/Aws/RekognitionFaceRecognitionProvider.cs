using System.Text;
using Amazon.Rekognition;
using Amazon.Rekognition.Model;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace FaceAttendance.Infrastructure.Providers.Aws;

/// <summary>
/// AWS Rekognition provider, for teams who would rather pay per call than own a model.
/// </summary>
/// <remarks>
/// <para>Two things differ from the self-hosted path, and both have consequences:</para>
/// <list type="number">
///   <item>The template stored in your database is an opaque Rekognition face id, not a vector.
///   Two enrolments cannot be compared offline, so the duplicate-identity and pose-consistency
///   checks become no-ops under this provider.</item>
///   <item>Every registration and every punch sends a face image to AWS, putting biometric data
///   in a third-party processor. That is a data-protection decision as much as a technical
///   one.</item>
/// </list>
/// <para>
/// Rekognition reports similarity on a 0-100 scale; it is rescaled to 0-1 here so the
/// thresholds in configuration stay comparable between providers.
/// </para>
/// </remarks>
public sealed class RekognitionFaceRecognitionProvider : IFaceRecognitionProvider
{
    private readonly IAmazonRekognition _rekognition;
    private readonly AwsProviderOptions _options;
    private readonly ILogger<RekognitionFaceRecognitionProvider> _logger;

    public RekognitionFaceRecognitionProvider(
        IAmazonRekognition rekognition,
        IOptions<FaceRecognitionOptions> options,
        ILogger<RekognitionFaceRecognitionProvider> logger)
    {
        _rekognition = rekognition;
        _options = options.Value.Aws;
        _logger = logger;
        ProviderId = $"aws:rekognition:{_options.CollectionId}";
    }

    public string ProviderId { get; }

    public FaceTemplateKind TemplateKind => FaceTemplateKind.ProviderManaged;

    public async Task<FaceCaptureAnalysis> AnalyzeAsync(FaceImage image, CancellationToken ct = default)
    {
        (int width, int height) = Dimensions(image);

        DetectFacesResponse response = await _rekognition.DetectFacesAsync(
            new DetectFacesRequest
            {
                Image = ToAwsImage(image),
                Attributes = ["DEFAULT"]
            },
            ct);

        if (response.FaceDetails.Count == 0)
        {
            return new FaceCaptureAnalysis
            {
                FaceDetected = false,
                FaceCount = 0,
                ImageWidth = width,
                ImageHeight = height,
                Warnings = ["No face was detected in the frame."]
            };
        }

        FaceDetail detail = response.FaceDetails
            .MaxBy(f => (f.BoundingBox?.Width ?? 0f) * (f.BoundingBox?.Height ?? 0f))!;

        return ToAnalysis(detail, response.FaceDetails.Count, width, height);
    }

    public async Task<FaceTemplateMaterial> CreateTemplateAsync(
        FaceImage image,
        FacePose pose,
        CancellationToken ct = default)
    {
        (int width, int height) = Dimensions(image);

        IndexFacesResponse response = await _rekognition.IndexFacesAsync(
            new IndexFacesRequest
            {
                CollectionId = _options.CollectionId,
                Image = ToAwsImage(image),
                DetectionAttributes = ["DEFAULT"],
                MaxFaces = 1,
                QualityFilter = QualityFilter.AUTO
            },
            ct);

        if (response.FaceRecords.Count == 0)
        {
            throw new FaceProcessingException(
                FaceError.NoFaceDetected,
                "Rekognition indexed no face in this frame. It may be too small, blurred, or turned too far.");
        }

        FaceRecord record = response.FaceRecords[0];

        return new FaceTemplateMaterial
        {
            ProviderId = ProviderId,
            Kind = FaceTemplateKind.ProviderManaged,
            Dimension = 0,
            // The face id IS the template. Lose this row and the indexed face is orphaned in the
            // collection: still billable, no longer reachable, and invisible to an erasure request.
            Data = Encoding.UTF8.GetBytes(record.Face.FaceId),
            QualityScore = (record.FaceDetail.Confidence ?? 0f) / 100d,
            DetectionScore = (record.FaceDetail.Confidence ?? 0f) / 100d,
            Analysis = ToAnalysis(record.FaceDetail, 1, width, height)
        };
    }

    public async Task<FaceMatchResult> MatchAsync(
        FaceImage probe,
        IReadOnlyList<FaceCandidate> candidates,
        double threshold,
        int topN = 5,
        CancellationToken ct = default)
    {
        SearchFacesByImageResponse response;

        try
        {
            response = await _rekognition.SearchFacesByImageAsync(
                new SearchFacesByImageRequest
                {
                    CollectionId = _options.CollectionId,
                    Image = ToAwsImage(probe),
                    FaceMatchThreshold = _options.SearchThresholdPercent,
                    MaxFaces = Math.Max(_options.MaxFaces, topN)
                },
                ct);
        }
        catch (InvalidParameterException ex)
        {
            // Rekognition raises this when it finds no face at all in the probe.
            throw new FaceProcessingException(
                FaceError.NoFaceDetected,
                "Rekognition found no usable face in the frame.",
                ex);
        }

        // Map provider face ids back to our people. This is what scopes a collection-wide search
        // to one tenant, or narrows it to one person for a 1:1 verification.
        Dictionary<string, FaceCandidate> byFaceId = [];
        foreach (FaceCandidate candidate in candidates)
        {
            byFaceId[Encoding.UTF8.GetString(candidate.TemplateData)] = candidate;
        }

        List<FaceScore> scores = [];
        foreach (Amazon.Rekognition.Model.FaceMatch match in response.FaceMatches)
        {
            if (match.Face?.FaceId is { } faceId &&
                byFaceId.TryGetValue(faceId, out FaceCandidate? candidate))
            {
                scores.Add(new FaceScore(
                    candidate.PersonId,
                    candidate.TemplateId,
                    candidate.Pose,
                    (match.Similarity ?? 0f) / 100d));
            }
        }

        if (scores.Count == 0)
        {
            return FaceMatchResult.NoMatch(threshold);
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

        return new FaceMatchResult
        {
            IsMatch = isMatch,
            PersonId = isMatch ? winner.PersonId : null,
            TemplateId = isMatch ? winner.TemplateId : null,
            Pose = isMatch ? winner.Pose : null,
            Score = winner.Score,
            Threshold = threshold,
            TopScores = [.. bestPerPerson.Take(Math.Max(1, topN))],
            Margin = bestPerPerson.Count > 1 ? winner.Score - bestPerPerson[1].Score : null
        };
    }

    /// <summary>
    /// Not supported: a face id carries no comparable information. Returning null makes the
    /// enrolment cross-checks skip, rather than silently producing a wrong number.
    /// </summary>
    public double? CompareTemplates(byte[] a, byte[] b) => null;

    public async Task DeleteTemplatesAsync(
        IReadOnlyList<byte[]> templateData,
        CancellationToken ct = default)
    {
        List<string> faceIds =
        [
            .. templateData.Select(Encoding.UTF8.GetString).Where(id => id.Length > 0)
        ];

        if (faceIds.Count == 0)
        {
            return;
        }

        // Deleting our row is not enough: the indexed face would stay in the collection, which
        // matters for billing and for honouring an erasure request.
        foreach (string[] batch in faceIds.Chunk(1000))
        {
            await _rekognition.DeleteFacesAsync(
                new DeleteFacesRequest
                {
                    CollectionId = _options.CollectionId,
                    FaceIds = [.. batch]
                },
                ct);
        }

        _logger.LogInformation(
            "Deleted {Count} faces from collection {Collection}", faceIds.Count, _options.CollectionId);
    }

    /// <summary>Create the collection if this is a fresh account. Safe to call repeatedly.</summary>
    public async Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        try
        {
            await _rekognition.CreateCollectionAsync(
                new CreateCollectionRequest { CollectionId = _options.CollectionId }, ct);

            _logger.LogInformation("Created Rekognition collection {Collection}", _options.CollectionId);
        }
        catch (ResourceAlreadyExistsException)
        {
            // Already there, which is the normal case after the first boot.
        }
    }

    private static Amazon.Rekognition.Model.Image ToAwsImage(FaceImage image) =>
        new() { Bytes = new MemoryStream(image.Content) };

    /// <summary>
    /// Rekognition does not report image dimensions, and its bounding boxes are ratios. Decoding
    /// locally is the cheapest way to get pixels so the shared validation rules still apply.
    /// </summary>
    private static (int Width, int Height) Dimensions(FaceImage image)
    {
        using SKBitmap? bitmap = SKBitmap.Decode(image.Content);
        return bitmap is null ? (0, 0) : (bitmap.Width, bitmap.Height);
    }

    private static FaceCaptureAnalysis ToAnalysis(
        FaceDetail detail,
        int faceCount,
        int imageWidth,
        int imageHeight) => new()
        {
            FaceDetected = true,
            FaceCount = faceCount,
            DetectionScore = (detail.Confidence ?? 0f) / 100d,
            QualityScore = (detail.Quality?.Sharpness ?? 50f) / 100d,
            // Ratios scaled to pixels so MinFaceDimension means the same thing here as elsewhere.
            Box = detail.BoundingBox is { } b
            ? new FaceBox(
                (b.Left ?? 0f) * imageWidth,
                (b.Top ?? 0f) * imageHeight,
                (b.Width ?? 0f) * imageWidth,
                (b.Height ?? 0f) * imageHeight)
            : null,
            HeadPose = detail.Pose is { } p
            ? new HeadPose(p.Yaw ?? 0f, p.Pitch ?? 0f, p.Roll ?? 0f)
            : null,
            Sharpness = (detail.Quality?.Sharpness ?? 0f) / 100d,
            Brightness = (detail.Quality?.Brightness ?? 0f) / 100d,
            ImageWidth = imageWidth,
            ImageHeight = imageHeight,
            Warnings = []
        };
}
