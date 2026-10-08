using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using FaceAttendance.Core.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace FaceAttendance.Tests;

public class FaceCaptureValidatorTests
{
    private static FaceCaptureValidator Build(Action<FaceRecognitionOptions>? configure = null)
    {
        FaceRecognitionOptions options = new()
        {
            AllowedContentTypes = ["image/jpeg", "image/png"],
            MaxImageBytes = 1024 * 1024,
            MinImageDimension = 240,
            MinFaceDimension = 80,
            MinDetectionScore = 0.6,
            MinQualityScore = 0.35,
            MinProfileYawDegrees = 12,
            MaxFrontalYawDegrees = 18,
            EnforcePoseCheck = true
        };

        configure?.Invoke(options);
        return new FaceCaptureValidator(Options.Create(options));
    }

    /// <summary>A minimal valid JPEG header, enough to satisfy signature sniffing.</summary>
    private static byte[] JpegBytes(int length = 2048)
    {
        byte[] bytes = new byte[length];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        return bytes;
    }

    private static FaceCaptureAnalysis GoodAnalysis(double yaw = 0) => new()
    {
        FaceDetected = true,
        FaceCount = 1,
        DetectionScore = 0.95,
        QualityScore = 0.8,
        Box = new FaceBox(50, 50, 200, 200),
        HeadPose = new HeadPose(yaw, 0, 0),
        Sharpness = 0.7,
        Brightness = 0.5,
        ImageWidth = 640,
        ImageHeight = 640
    };

    [Fact]
    public void ValidateUpload_GoodJpeg_Passes() =>
        Build().ValidateUpload(new FaceImage(JpegBytes(), "image/jpeg", "a.jpg"));

    [Fact]
    public void ValidateUpload_EmptyFile_Rejected()
    {
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidateUpload(new FaceImage([], "image/jpeg", "a.jpg")));

        Assert.Equal(FaceError.CorruptImage, ex.Error);
    }

    [Fact]
    public void ValidateUpload_OversizedFile_Rejected()
    {
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build(o => o.MaxImageBytes = 1024)
                .ValidateUpload(new FaceImage(JpegBytes(4096), "image/jpeg", "a.jpg")));

        Assert.Equal(FaceError.ImageTooLarge, ex.Error);
    }

    [Fact]
    public void ValidateUpload_DisallowedContentType_Rejected()
    {
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidateUpload(new FaceImage(JpegBytes(), "image/gif", "a.gif")));

        Assert.Equal(FaceError.UnsupportedFormat, ex.Error);
    }

    [Fact]
    public void ValidateUpload_TextFileLabelledAsJpeg_Rejected()
    {
        // The declared content type is attacker-controlled, so the bytes decide.
        byte[] notAnImage = System.Text.Encoding.UTF8.GetBytes("<?php echo 1; ?>aaaaaaaaaaaa");

        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidateUpload(new FaceImage(notAnImage, "image/jpeg", "shell.jpg")));

        Assert.Equal(FaceError.UnsupportedFormat, ex.Error);
    }

    [Theory]
    [InlineData("image/jpeg", (byte)0xFF, (byte)0xD8, (byte)0xFF)]
    public void DetectSignature_Jpeg_Recognised(string expected, byte b0, byte b1, byte b2) =>
        Assert.Equal(expected, FaceCaptureValidator.DetectSignature([b0, b1, b2, 0, 0]));

    [Fact]
    public void DetectSignature_Png_Recognised() =>
        Assert.Equal("image/png",
            FaceCaptureValidator.DetectSignature([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));

    [Fact]
    public void DetectSignature_Garbage_IsNull() =>
        Assert.Null(FaceCaptureValidator.DetectSignature([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));

    [Fact]
    public void ValidateAnalysis_GoodFrame_Passes() => Build().ValidateAnalysis(GoodAnalysis());

    [Fact]
    public void ValidateAnalysis_NoFace_Rejected()
    {
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidateAnalysis(new FaceCaptureAnalysis
            {
                FaceDetected = false,
                ImageWidth = 640,
                ImageHeight = 640
            }));

        Assert.Equal(FaceError.NoFaceDetected, ex.Error);
        Assert.NotEmpty(ex.Hints);
    }

    [Fact]
    public void ValidateAnalysis_TwoFaces_Rejected()
    {
        // Registering with a bystander in frame is how the wrong face gets enrolled.
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidateAnalysis(new FaceCaptureAnalysis
            {
                FaceDetected = true,
                FaceCount = 2,
                DetectionScore = 0.9,
                QualityScore = 0.8,
                Box = new FaceBox(0, 0, 200, 200),
                ImageWidth = 640,
                ImageHeight = 640
            }));

        Assert.Equal(FaceError.MultipleFacesDetected, ex.Error);
    }

    [Fact]
    public void ValidateAnalysis_TinyImage_Rejected()
    {
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidateAnalysis(new FaceCaptureAnalysis
            {
                FaceDetected = true,
                FaceCount = 1,
                DetectionScore = 0.9,
                QualityScore = 0.9,
                ImageWidth = 100,
                ImageHeight = 100
            }));

        Assert.Equal(FaceError.ImageTooSmall, ex.Error);
    }

    [Fact]
    public void ValidateAnalysis_FaceTooFarAway_Rejected()
    {
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidateAnalysis(new FaceCaptureAnalysis
            {
                FaceDetected = true,
                FaceCount = 1,
                DetectionScore = 0.9,
                QualityScore = 0.9,
                Box = new FaceBox(0, 0, 40, 40),
                ImageWidth = 640,
                ImageHeight = 640
            }));

        Assert.Equal(FaceError.LowQuality, ex.Error);
    }
}
