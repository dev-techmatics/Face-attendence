using FaceAttendance.Core.Faces;
using SkiaSharp;

namespace FaceAttendance.Infrastructure.Providers.Onnx;

/// <summary>Decode, letterbox, warp and measure. The pixel plumbing around the two models.</summary>
internal static class ImageOps
{
    /// <summary>Decoded RGB pixels plus the original dimensions.</summary>
    internal sealed record Rgb(byte[] Pixels, int Width, int Height)
    {
        public int Stride => Width * 3;
    }

    /// <summary>A letterboxed square plus what it takes to map coordinates back.</summary>
    internal sealed record Letterboxed(byte[] Pixels, int Size, float Scale, float PadX, float PadY);

    public static SKBitmap Decode(byte[] content)
    {
        SKBitmap? bitmap = SKBitmap.Decode(content) ?? throw new FaceProcessingException(
                FaceError.CorruptImage,
                "The image could not be decoded. It may be truncated or not an image at all.");
        return bitmap;
    }

    /// <summary>
    /// Resize into a square canvas preserving aspect ratio, padding the remainder. Squashing to
    /// a square instead would distort faces and cost real accuracy.
    /// </summary>
    public static Letterboxed Letterbox(SKBitmap source, int size)
    {
        float scale = Math.Min((float)size / source.Width, (float)size / source.Height);
        int scaledWidth = (int)MathF.Round(source.Width * scale);
        int scaledHeight = (int)MathF.Round(source.Height * scale);
        float padX = (size - scaledWidth) / 2f;
        float padY = (size - scaledHeight) / 2f;

        SKImageInfo info = new(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap canvasBitmap = new(info);
        using SKCanvas canvas = new(canvasBitmap);
        canvas.Clear(SKColors.Black);

        using SKImage image = SKImage.FromBitmap(source);
        SKRect destination = new(padX, padY, padX + scaledWidth, padY + scaledHeight);
        canvas.DrawImage(image, destination, Sampling);
        canvas.Flush();

        return new Letterboxed(ToRgb(canvasBitmap), size, scale, padX, padY);
    }

    /// <summary>
    /// Warp a face into the canonical square the recognition model was trained on, using the
    /// five landmarks. Alignment is not an optimisation; an unaligned crop costs far more
    /// accuracy than any threshold tuning can recover.
    /// </summary>
    public static Rgb WarpAligned(SKBitmap source, SKMatrix transform, int size)
    {
        SKImageInfo info = new(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap destination = new(info);
        using SKCanvas canvas = new(destination);
        canvas.Clear(SKColors.Black);
        canvas.SetMatrix(transform);

        using SKImage image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, new SKPoint(0, 0), Sampling);
        canvas.Flush();

        return new Rgb(ToRgb(destination), size, size);
    }

    public static Rgb Crop(SKBitmap source, FaceBox box, int size)
    {
        SKImageInfo info = new(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap destination = new(info);
        using SKCanvas canvas = new(destination);
        canvas.Clear(SKColors.Black);

        using SKImage image = SKImage.FromBitmap(source);
        SKRect sourceRect = new(box.X, box.Y, box.Right, box.Bottom);
        canvas.DrawImage(image, sourceRect, new SKRect(0, 0, size, size), Sampling);
        canvas.Flush();

        return new Rgb(ToRgb(destination), size, size);
    }

    /// <summary>Mean luma in 0..1. Flags frames that are blown out or lost in shadow.</summary>
    public static double MeanBrightness(Rgb image)
    {
        if (image.Pixels.Length == 0)
        {
            return 0d;
        }

        double sum = 0d;
        for (int i = 0; i < image.Pixels.Length; i += 3)
        {
            sum += (0.299 * image.Pixels[i]) + (0.587 * image.Pixels[i + 1]) + (0.114 * image.Pixels[i + 2]);
        }

        return sum / (image.Pixels.Length / 3d) / 255d;
    }

    /// <summary>
    /// Variance of the Laplacian - the standard cheap blur detector. A sharp face has strong
    /// second derivatives; motion blur flattens them.
    /// </summary>
    public static double LaplacianVariance(Rgb image)
    {
        if (image.Width < 3 || image.Height < 3)
        {
            return 0d;
        }

        float[] gray = new float[image.Width * image.Height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 3)
        {
            gray[i] = (0.299f * image.Pixels[p]) + (0.587f * image.Pixels[p + 1]) + (0.114f * image.Pixels[p + 2]);
        }

        double sum = 0d;
        double sumSquares = 0d;
        int count = 0;

        for (int y = 1; y < image.Height - 1; y++)
        {
            for (int x = 1; x < image.Width - 1; x++)
            {
                int i = (y * image.Width) + x;
                double value =
                    (4 * gray[i])
                    - gray[i - 1]
                    - gray[i + 1]
                    - gray[i - image.Width]
                    - gray[i + image.Width];

                sum += value;
                sumSquares += value * value;
                count++;
            }
        }

        if (count == 0)
        {
            return 0d;
        }

        double mean = sum / count;
        return Math.Max(0d, (sumSquares / count) - (mean * mean));
    }

    private static SKSamplingOptions Sampling => new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>Drop alpha and hand back tightly packed RGB triples.</summary>
    private static byte[] ToRgb(SKBitmap bitmap)
    {
        ReadOnlySpan<byte> rgba = bitmap.GetPixelSpan();
        byte[] rgb = new byte[bitmap.Width * bitmap.Height * 3];

        for (int i = 0, o = 0; o < rgb.Length; i += 4, o += 3)
        {
            rgb[o] = rgba[i];
            rgb[o + 1] = rgba[i + 1];
            rgb[o + 2] = rgba[i + 2];
        }

        return rgb;
    }
}
