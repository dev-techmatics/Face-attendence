using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using FaceAttendance.Core.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace FaceAttendance.Tests;

/// <summary>
/// The pose gate is what stops three frontal snapshots being accepted as a complete three-angle
/// enrolment, which would quietly discard the whole point of capturing three images.
/// </summary>
public class PoseValidationTests
{
    private static FaceCaptureValidator Build(bool enforce = true) =>
        new(Options.Create(new FaceRecognitionOptions
        {
            EnforcePoseCheck = enforce,
            MinProfileYawDegrees = 12,
            MaxFrontalYawDegrees = 18
        }));

    private static FaceCaptureAnalysis WithYaw(double yaw) => new()
    {
        FaceDetected = true,
        FaceCount = 1,
        DetectionScore = 0.95,
        QualityScore = 0.8,
        HeadPose = new HeadPose(yaw, 0, 0),
        ImageWidth = 640,
        ImageHeight = 640
    };

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-10)]
    [InlineData(17.9)]
    public void Front_WithinTolerance_Accepted(double yaw) =>
        Build().ValidatePose(WithYaw(yaw), FacePose.Front);

    [Theory]
    [InlineData(25)]
    [InlineData(-25)]
    public void Front_TurnedTooFar_Rejected(double yaw)
    {
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidatePose(WithYaw(yaw), FacePose.Front));

        Assert.Equal(FaceError.PoseMismatch, ex.Error);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(40)]
    public void Left_TurnedEnough_Accepted(double yaw) =>
        Build().ValidatePose(WithYaw(yaw), FacePose.Left);

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-30)]
    public void Left_NotTurnedEnough_Rejected(double yaw)
    {
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidatePose(WithYaw(yaw), FacePose.Left));

        Assert.Equal(FaceError.PoseMismatch, ex.Error);
    }

    [Theory]
    [InlineData(-15)]
    [InlineData(-40)]
    public void Right_TurnedEnough_Accepted(double yaw) =>
        Build().ValidatePose(WithYaw(yaw), FacePose.Right);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(30)]
    public void Right_NotTurnedEnough_Rejected(double yaw)
    {
        FaceProcessingException ex = Assert.Throws<FaceProcessingException>(
            () => Build().ValidatePose(WithYaw(yaw), FacePose.Right));

        Assert.Equal(FaceError.PoseMismatch, ex.Error);
    }

    [Fact]
    public void ThreeFrontalFrames_WouldAllBeRejectedForTheProfilePoses()
    {
        FaceCaptureValidator validator = Build();
        FaceCaptureAnalysis frontal = WithYaw(2);

        validator.ValidatePose(frontal, FacePose.Front);

        Assert.Throws<FaceProcessingException>(() => validator.ValidatePose(frontal, FacePose.Left));
        Assert.Throws<FaceProcessingException>(() => validator.ValidatePose(frontal, FacePose.Right));
    }

    [Fact]
    public void PoseCheckDisabled_AcceptsAnything()
    {
        // Useful for cameras whose yaw estimate cannot be trusted, at the cost of the guarantee.
        Build(enforce: false).ValidatePose(WithYaw(0), FacePose.Left);
    }

    [Fact]
    public void NoHeadPoseAvailable_SkipsTheCheck()
    {
        // A provider that cannot estimate yaw must not block enrolment outright.
        Build().ValidatePose(new FaceCaptureAnalysis
        {
            FaceDetected = true,
            FaceCount = 1,
            HeadPose = null
        }, FacePose.Left);
    }
}
