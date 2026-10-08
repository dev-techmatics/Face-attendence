using System.Buffers.Binary;
using System.Numerics;
using System.Numerics.Tensors;

namespace FaceAttendance.Core.Common;

/// <summary>
/// Embedding helpers. Templates are stored as float32 little-endian so the same bytes can be
/// read back on any CPU and by any other language talking to the same database.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Cosine similarity in [-1, 1]. Inputs are expected to be L2-normalised already, in which
    /// case this is a plain dot product; it still normalises defensively so a caller passing
    /// raw vectors gets a correct answer rather than a silently inflated one.
    /// </summary>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Embedding length mismatch: {a.Length} vs {b.Length}. This usually means two " +
                "different models produced these templates.");
        }

        if (a.Length == 0)
        {
            return 0d;
        }

        double dot = TensorPrimitives.Dot(a, b);
        double normA = Math.Sqrt(TensorPrimitives.SumOfSquares(a));
        double normB = Math.Sqrt(TensorPrimitives.SumOfSquares(b));

        if (normA <= 1e-8 || normB <= 1e-8)
        {
            return 0d;
        }

        return Math.Clamp(dot / (normA * normB), -1d, 1d);
    }

    /// <summary>Scale a vector to unit length, in place.</summary>
    public static void L2NormalizeInPlace(Span<float> vector)
    {
        float norm = MathF.Sqrt(TensorPrimitives.SumOfSquares(vector));
        if (norm <= 1e-8f)
        {
            return;
        }

        TensorPrimitives.Divide(vector, norm, vector);
    }

    /// <summary>Serialise an embedding to float32 little-endian bytes.</summary>
    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        byte[] bytes = new byte[vector.Length * sizeof(float)];
        for (int i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    /// <summary>Deserialise float32 little-endian bytes back into an embedding.</summary>
    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"Template payload of {bytes.Length} bytes is not a whole number of float32 values.",
                nameof(bytes));
        }

        float[] vector = new float[bytes.Length / sizeof(float)];
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        }

        return vector;
    }

    /// <summary>
    /// Element-wise mean of several embeddings, re-normalised. A per-person centroid like this
    /// is a cheap way to cut a 1:N search down to one comparison per person; it is a coarser
    /// signal than per-pose matching, so use it to shortlist, not to decide.
    /// </summary>
    public static float[] Centroid(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
        {
            return [];
        }

        int dim = vectors[0].Length;
        float[] sum = new float[dim];

        foreach (float[] v in vectors)
        {
            if (v.Length != dim)
            {
                throw new ArgumentException("All embeddings must share one dimension.", nameof(vectors));
            }

            TensorPrimitives.Add(sum, v, sum);
        }

        TensorPrimitives.Divide(sum, vectors.Count, sum);
        L2NormalizeInPlace(sum);
        return sum;
    }
}
