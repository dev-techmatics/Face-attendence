using FaceAttendance.Core.Common;
using Xunit;

namespace FaceAttendance.Tests;

public class VectorMathTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_IsOne()
    {
        float[] v = [0.1f, 0.5f, -0.3f, 0.8f];

        Assert.Equal(1.0, VectorMath.CosineSimilarity(v, v), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_IsMinusOne()
    {
        float[] a = [1f, 0f, 0f];
        float[] b = [-1f, 0f, 0f];

        Assert.Equal(-1.0, VectorMath.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_IsZero()
    {
        float[] a = [1f, 0f];
        float[] b = [0f, 1f];

        Assert.Equal(0.0, VectorMath.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_IgnoresMagnitude()
    {
        // Direction is what carries identity; a scaled copy must score the same.
        float[] a = [1f, 2f, 3f];
        float[] b = [10f, 20f, 30f];

        Assert.Equal(1.0, VectorMath.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_ZeroVector_IsZeroNotNaN()
    {
        float[] a = [0f, 0f, 0f];
        float[] b = [1f, 2f, 3f];

        // A NaN here would propagate into a threshold comparison and quietly reject everybody.
        double score = VectorMath.CosineSimilarity(a, b);

        Assert.False(double.IsNaN(score));
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void CosineSimilarity_MismatchedLengths_Throws()
    {
        // Comparing templates from two different models is a bug, not a low score.
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => VectorMath.CosineSimilarity([1f, 2f], [1f, 2f, 3f]));

        Assert.Contains("length mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToBytes_FromBytes_RoundTripsExactly()
    {
        float[] original = [0.123456f, -0.987654f, 0f, 1f, -1f];

        float[] restored = VectorMath.FromBytes(VectorMath.ToBytes(original));

        Assert.Equal(original, restored);
    }

    [Fact]
    public void ToBytes_Produces4BytesPerDimension()
    {
        // 512 dimensions must occupy exactly 2048 bytes, which is what the column is sized for.
        float[] embedding = new float[512];

        Assert.Equal(2048, VectorMath.ToBytes(embedding).Length);
    }

    [Fact]
    public void FromBytes_PartialFloat_Throws() =>
        Assert.Throws<ArgumentException>(() => VectorMath.FromBytes(new byte[7]));

    [Fact]
    public void L2Normalize_ProducesUnitLength()
    {
        float[] v = [3f, 4f];

        VectorMath.L2NormalizeInPlace(v);

        Assert.Equal(1.0, Math.Sqrt((v[0] * v[0]) + (v[1] * v[1])), precision: 5);
        Assert.Equal(0.6f, v[0], precision: 4);
        Assert.Equal(0.8f, v[1], precision: 4);
    }

    [Fact]
    public void L2Normalize_ZeroVector_LeavesItAlone()
    {
        float[] v = [0f, 0f];

        VectorMath.L2NormalizeInPlace(v);

        Assert.Equal([0f, 0f], v);
    }

    [Fact]
    public void Centroid_OfIdenticalVectors_IsThatVector()
    {
        float[] v = [0.6f, 0.8f];

        float[] centroid = VectorMath.Centroid([v, v, v]);

        Assert.Equal(0.6f, centroid[0], precision: 4);
        Assert.Equal(0.8f, centroid[1], precision: 4);
    }

    [Fact]
    public void Centroid_IsUnitLength()
    {
        float[] centroid = VectorMath.Centroid([[1f, 0f], [0f, 1f]]);

        Assert.Equal(1.0, Math.Sqrt((centroid[0] * centroid[0]) + (centroid[1] * centroid[1])), precision: 5);
    }

    [Fact]
    public void Centroid_MismatchedDimensions_Throws() =>
        Assert.Throws<ArgumentException>(() => VectorMath.Centroid([[1f, 2f], [1f, 2f, 3f]]));
}
