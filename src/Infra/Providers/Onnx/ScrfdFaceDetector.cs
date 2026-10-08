using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Faces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace FaceAttendance.Infrastructure.Providers.Onnx;

/// <summary>A detected face in original-image coordinates.</summary>
internal sealed record DetectedFace(FaceBox Box, FaceLandmarks Landmarks, float Score);

/// <summary>
/// SCRFD face detector. Returns boxes plus the five landmarks that alignment needs, which is
/// why a bnkps model variant is required rather than a plain box detector.
/// </summary>
internal sealed class ScrfdFaceDetector : IDisposable
{
    /// <summary>SCRFD heads are emitted at these three strides.</summary>
    private static readonly int[] Strides = [8, 16, 32];

    private readonly InferenceSession _session;
    private readonly OnnxProviderOptions _options;
    private readonly string _inputName;
    private readonly string[] _scoreOutputs;
    private readonly string[] _boxOutputs;
    private readonly string[] _keypointOutputs;

    public ScrfdFaceDetector(OnnxProviderOptions options)
    {
        _options = options;

        if (!File.Exists(options.DetectorModelPath))
        {
            throw new FaceProcessingException(
                FaceError.ProviderNotConfigured,
                $"Detector model not found at {Path.GetFullPath(options.DetectorModelPath)}. " +
                "Run scripts/download-models.ps1, or set FaceRecognition:Provider to development.");
        }

        SessionOptions sessionOptions = new();
        if (options.IntraOpThreads > 0)
        {
            sessionOptions.IntraOpNumThreads = options.IntraOpThreads;
        }

        _session = new InferenceSession(options.DetectorModelPath, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();

        // Group the heads by their last dimension rather than by name: SCRFD exports use wildly
        // different output names (score_8, 447, onnx::Concat_1), but the shapes are stable -
        // 1 value per anchor for scores, 4 for box distances, 10 for five landmark pairs. Within
        // each group the model emits strides 8, 16, 32 in order.
        List<string> scores = [];
        List<string> boxes = [];
        List<string> keypoints = [];

        foreach ((string name, NodeMetadata metadata) in _session.OutputMetadata)
        {
            int lastDimension = metadata.Dimensions.Length > 0 ? metadata.Dimensions[^1] : -1;

            switch (lastDimension)
            {
                case 1:
                    scores.Add(name);
                    break;
                case 4:
                    boxes.Add(name);
                    break;
                case 10:
                    keypoints.Add(name);
                    break;
            }
        }

        if (scores.Count < Strides.Length || boxes.Count < Strides.Length)
        {
            throw new FaceProcessingException(
                FaceError.ProviderNotConfigured,
                $"The detector at {options.DetectorModelPath} exposes {scores.Count} score and " +
                $"{boxes.Count} box heads; an SCRFD model with three of each was expected.");
        }

        if (keypoints.Count < Strides.Length)
        {
            throw new FaceProcessingException(
                FaceError.ProviderNotConfigured,
                "This detector emits no facial landmarks, so faces cannot be aligned and " +
                "recognition accuracy would collapse. Use an SCRFD variant with kps, such as " +
                "scrfd_500m_bnkps.onnx.");
        }

        _scoreOutputs = [.. scores];
        _boxOutputs = [.. boxes];
        _keypointOutputs = [.. keypoints];
    }

    /// <summary>Detect faces, strongest first.</summary>
    public IReadOnlyList<DetectedFace> Detect(SKBitmap bitmap)
    {
        int size = _options.DetectorInputSize;
        ImageOps.Letterboxed input = ImageOps.Letterbox(bitmap, size);

        // SCRFD preprocessing: RGB, (pixel - 127.5) / 128, NCHW.
        DenseTensor<float> tensor = new([1, 3, size, size]);
        int plane = size * size;
        ReadOnlySpan<byte> pixels = input.Pixels;
        Span<float> buffer = tensor.Buffer.Span;

        for (int i = 0, p = 0; i < plane; i++, p += 3)
        {
            buffer[i] = (pixels[p] - 127.5f) / 128f;
            buffer[plane + i] = (pixels[p + 1] - 127.5f) / 128f;
            buffer[(2 * plane) + i] = (pixels[p + 2] - 127.5f) / 128f;
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(
            [NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);

        Dictionary<string, float[]> outputs = results.ToDictionary(
            r => r.Name,
            r => r.AsEnumerable<float>().ToArray());

        List<DetectedFace> candidates = [];

        for (int level = 0; level < Strides.Length; level++)
        {
            DecodeLevel(level, size, input, outputs, candidates);
        }

        return NonMaximumSuppression(candidates, _options.DetectorNmsThreshold);
    }

    private void DecodeLevel(
        int level,
        int size,
        ImageOps.Letterboxed input,
        Dictionary<string, float[]> outputs,
        List<DetectedFace> candidates)
    {
        int stride = Strides[level];
        float[] scores = outputs[_scoreOutputs[level]];
        float[] boxes = outputs[_boxOutputs[level]];
        float[] keypoints = outputs[_keypointOutputs[level]];

        int grid = size / stride;
        int locations = grid * grid;

        // Most SCRFD exports place two anchors per cell, a few place one. Infer it rather than
        // assume: getting it wrong silently halves or doubles the decoded grid.
        int anchorsPerLocation = locations == 0 ? 0 : scores.Length / locations;
        if (anchorsPerLocation is not (1 or 2))
        {
            return;
        }

        float padX = input.PadX;
        float padY = input.PadY;
        float scale = input.Scale;

        // Allocated once, outside the loop. The stride-8 head has 12800 anchors, so a stackalloc
        // per iteration would reserve hundreds of kilobytes of stack and could overflow it:
        // stack memory from a stackalloc is not reclaimed until the method returns.
        Span<float> points = stackalloc float[10];

        for (int index = 0; index < scores.Length; index++)
        {
            if (scores[index] < _options.DetectorConfidenceThreshold)
            {
                continue;
            }

            int location = index / anchorsPerLocation;
            float centerX = (location % grid) * stride;
            float centerY = (location / grid) * stride;

            // The box head predicts distances from the anchor centre to each edge, in strides.
            int b = index * 4;
            float x1 = centerX - (boxes[b] * stride);
            float y1 = centerY - (boxes[b + 1] * stride);
            float x2 = centerX + (boxes[b + 2] * stride);
            float y2 = centerY + (boxes[b + 3] * stride);

            int k = index * 10;
            for (int j = 0; j < 5; j++)
            {
                points[j * 2] = centerX + (keypoints[k + (j * 2)] * stride);
                points[(j * 2) + 1] = centerY + (keypoints[k + (j * 2) + 1] * stride);
            }

            // Undo the letterbox so every coordinate refers to the original image.
            FaceBox box = new(
                (x1 - padX) / scale,
                (y1 - padY) / scale,
                ((x2 - padX) / scale) - ((x1 - padX) / scale),
                ((y2 - padY) / scale) - ((y1 - padY) / scale));

            FaceLandmarks landmarks = new(
                ((points[0] - padX) / scale, (points[1] - padY) / scale),
                ((points[2] - padX) / scale, (points[3] - padY) / scale),
                ((points[4] - padX) / scale, (points[5] - padY) / scale),
                ((points[6] - padX) / scale, (points[7] - padY) / scale),
                ((points[8] - padX) / scale, (points[9] - padY) / scale));

            candidates.Add(new DetectedFace(box, landmarks, scores[index]));
        }
    }

    private static List<DetectedFace> NonMaximumSuppression(List<DetectedFace> faces, float iouThreshold)
    {
        List<DetectedFace> kept = [];

        foreach (DetectedFace face in faces.OrderByDescending(f => f.Score))
        {
            if (!kept.Any(k => IntersectionOverUnion(k.Box, face.Box) > iouThreshold))
            {
                kept.Add(face);
            }
        }

        return kept;
    }

    private static float IntersectionOverUnion(FaceBox a, FaceBox b)
    {
        float x1 = Math.Max(a.X, b.X);
        float y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.Right, b.Right);
        float y2 = Math.Min(a.Bottom, b.Bottom);

        float intersection = Math.Max(0f, x2 - x1) * Math.Max(0f, y2 - y1);
        float union = a.Area + b.Area - intersection;

        return union <= 0f ? 0f : intersection / union;
    }

    public void Dispose() => _session.Dispose();
}
