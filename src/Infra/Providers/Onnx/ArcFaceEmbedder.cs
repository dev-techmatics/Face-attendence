using FaceAttendance.Core.Common;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Faces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FaceAttendance.Infrastructure.Providers.Onnx;

/// <summary>
/// Turns an aligned 112x112 face crop into an L2-normalised embedding. Two embeddings of the
/// same person land close together on the unit sphere; cosine similarity measures how close.
/// </summary>
internal sealed class ArcFaceEmbedder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly OnnxProviderOptions _options;
    private readonly string _inputName;

    public ArcFaceEmbedder(OnnxProviderOptions options)
    {
        _options = options;

        if (!File.Exists(options.RecognitionModelPath))
        {
            throw new FaceProcessingException(
                FaceError.ProviderNotConfigured,
                $"Recognition model not found at {Path.GetFullPath(options.RecognitionModelPath)}. " +
                "Run scripts/download-models.ps1, or set FaceRecognition:Provider to development.");
        }

        SessionOptions sessionOptions = new();
        if (options.IntraOpThreads > 0)
        {
            sessionOptions.IntraOpNumThreads = options.IntraOpThreads;
        }

        _session = new InferenceSession(options.RecognitionModelPath, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();

        NodeMetadata output = _session.OutputMetadata.Values.First();
        Dimension = output.Dimensions.Length > 0 && output.Dimensions[^1] > 0
            ? output.Dimensions[^1]
            : 512;
    }

    /// <summary>Embedding length reported by the model, falling back to the usual 512.</summary>
    public int Dimension { get; }

    /// <summary>
    /// Embed an aligned crop. Input must already be the canonical 112x112 produced by
    /// <see cref="FaceAligner"/>; feeding a raw crop here is the single most common cause of
    /// "recognition does not work".
    /// </summary>
    public float[] Embed(ImageOps.Rgb aligned)
    {
        int size = FaceAligner.OutputSize;

        if (aligned.Width != size || aligned.Height != size)
        {
            throw new InvalidOperationException(
                $"Expected a {size}x{size} aligned crop but received {aligned.Width}x{aligned.Height}.");
        }

        // ArcFace preprocessing: (pixel - 127.5) / 127.5 into [-1, 1], NCHW.
        DenseTensor<float> tensor = new([1, 3, size, size]);
        int plane = size * size;
        Span<float> buffer = tensor.Buffer.Span;
        ReadOnlySpan<byte> pixels = aligned.Pixels;

        // Most InsightFace exports take RGB; a few ONNX-zoo conversions expect BGR. Guessing
        // wrong does not crash, it just quietly degrades every score, so it is a setting.
        int redPlane = _options.RecognitionInputBgr ? 2 * plane : 0;
        int bluePlane = _options.RecognitionInputBgr ? 0 : 2 * plane;

        for (int i = 0, p = 0; i < plane; i++, p += 3)
        {
            buffer[redPlane + i] = (pixels[p] - 127.5f) / 127.5f;
            buffer[plane + i] = (pixels[p + 1] - 127.5f) / 127.5f;
            buffer[bluePlane + i] = (pixels[p + 2] - 127.5f) / 127.5f;
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(
            [NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);

        float[] embedding = [.. results[0].AsEnumerable<float>()];

        // Normalising here means every stored template is a unit vector, so comparison is a
        // plain dot product and thresholds stay comparable across captures.
        VectorMath.L2NormalizeInPlace(embedding);
        return embedding;
    }

    public void Dispose() => _session.Dispose();
}
