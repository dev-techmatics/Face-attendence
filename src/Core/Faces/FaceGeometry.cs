namespace FaceAttendance.Core.Faces;

public readonly record struct FaceBox(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public float Area => Math.Max(0f, Width) * Math.Max(0f, Height);
}

/// <summary>
/// The five landmarks ArcFace-style models align on: both eyes, nose tip, both mouth corners.
/// </summary>
public readonly record struct FaceLandmarks(
    (float X, float Y) LeftEye,
    (float X, float Y) RightEye,
    (float X, float Y) Nose,
    (float X, float Y) MouthLeft,
    (float X, float Y) MouthRight);

/// <summary>Head orientation estimate, in degrees. Yaw is the left/right turn.</summary>
public readonly record struct HeadPose(double YawDegrees, double PitchDegrees, double RollDegrees);
