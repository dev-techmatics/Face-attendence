using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using Microsoft.Extensions.Options;
using Xunit;

namespace FaceAttendance.Tests;

public class OptionsValidationTests
{
    private static FaceRecognitionOptions Valid() => new()
    {
        Provider = "onnx",
        RequiredPoses = [FacePose.Front, FacePose.Left, FacePose.Right],
        VerificationThreshold = 0.42,
        IdentificationThreshold = 0.52,
        DuplicateEnrollmentThreshold = 0.6,
        MinEnrollmentPairSimilarity = 0.2,
        MinQualityScore = 0.35,
        MinDetectionScore = 0.6,
        MinImageDimension = 240,
        MinFaceDimension = 80,
        MaxImageBytes = 8 * 1024 * 1024
    };

    [Fact]
    public void ValidDefaults_Pass() =>
        Assert.True(new FaceRecognitionOptionsValidator().Validate(null, Valid()).Succeeded);

    [Fact]
    public void ThresholdAbove1_Rejected()
    {
        // The classic typo: 42 meant as a percentage would reject every single employee.
        FaceRecognitionOptions options = Valid();
        options.VerificationThreshold = 42;

        ValidateOptionsResult result = new FaceRecognitionOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("VerificationThreshold", result.FailureMessage);
    }

    [Fact]
    public void IdentificationLooserThanVerification_Rejected()
    {
        FaceRecognitionOptions options = Valid();
        options.IdentificationThreshold = 0.30;
        options.VerificationThreshold = 0.50;

        Assert.True(new FaceRecognitionOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void NoRequiredPoses_Rejected()
    {
        FaceRecognitionOptions options = Valid();
        options.RequiredPoses = [];

        Assert.True(new FaceRecognitionOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void UnknownProvider_Rejected()
    {
        FaceRecognitionOptions options = Valid();
        options.Provider = "magic";

        Assert.True(new FaceRecognitionOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void FaceLargerThanImage_Rejected()
    {
        FaceRecognitionOptions options = Valid();
        options.MinFaceDimension = 500;
        options.MinImageDimension = 240;

        Assert.True(new FaceRecognitionOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void PostConfigure_EmptyPoses_GetsTheStandardThree()
    {
        FaceRecognitionOptions options = new();

        new FaceRecognitionOptionsSetup().PostConfigure(null, options);

        Assert.Equal([FacePose.Front, FacePose.Left, FacePose.Right], options.RequiredPoses);
    }

    [Fact]
    public void PostConfigure_DedupesPoses()
    {
        // Configuration binding appends to array defaults, so duplicates are a real scenario
        // rather than a hypothetical one.
        FaceRecognitionOptions options = new()
        {
            RequiredPoses = [FacePose.Front, FacePose.Left, FacePose.Right, FacePose.Front, FacePose.Left]
        };

        new FaceRecognitionOptionsSetup().PostConfigure(null, options);

        Assert.Equal(3, options.RequiredPoses.Length);
    }

    [Fact]
    public void PostConfigure_NormalisesContentTypes()
    {
        FaceRecognitionOptions options = new()
        {
            AllowedContentTypes = [" IMAGE/JPEG ", "image/jpeg", "image/png", ""]
        };

        new FaceRecognitionOptionsSetup().PostConfigure(null, options);

        Assert.Equal(["image/jpeg", "image/png"], options.AllowedContentTypes);
    }

    [Fact]
    public void PostConfigure_EmptyContentTypes_GetsDefaults()
    {
        FaceRecognitionOptions options = new();

        new FaceRecognitionOptionsSetup().PostConfigure(null, options);

        Assert.Contains("image/jpeg", options.AllowedContentTypes);
    }
}
