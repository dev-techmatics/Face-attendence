using FaceAttendance.Core.Faces;
using SkiaSharp;

namespace FaceAttendance.Infrastructure.Providers.Onnx;

/// <summary>
/// Maps the five detected landmarks onto the canonical 112x112 arrangement that ArcFace-family
/// models were trained on.
/// </summary>
internal static class FaceAligner
{
    /// <summary>
    /// The ArcFace reference landmarks for a 112x112 crop, in the InsightFace convention
    /// (left eye, right eye, nose, left mouth corner, right mouth corner). These exact numbers
    /// are what make an embedding comparable with any other ArcFace embedding.
    /// </summary>
    private static readonly (float X, float Y)[] Reference =
    [
        (38.2946f, 51.6963f),
        (73.5318f, 51.5014f),
        (56.0252f, 71.7366f),
        (41.5493f, 92.3655f),
        (70.7299f, 92.2041f)
    ];

    public const int OutputSize = 112;

    /// <summary>
    /// Least-squares similarity transform (rotation, uniform scale, translation) taking the
    /// detected landmarks to the reference ones.
    /// </summary>
    /// <remarks>
    /// A full affine fit would also absorb shear and non-uniform scale, which distorts the face
    /// and moves the embedding; restricting to a similarity keeps facial proportions intact.
    /// This is the closed-form 2D solution, so no SVD is needed.
    /// </remarks>
    public static SKMatrix ComputeTransform(FaceLandmarks landmarks)
    {
        (float X, float Y)[] source =
        [
            landmarks.LeftEye,
            landmarks.RightEye,
            landmarks.Nose,
            landmarks.MouthLeft,
            landmarks.MouthRight
        ];

        int n = source.Length;

        float sourceMeanX = 0f, sourceMeanY = 0f, targetMeanX = 0f, targetMeanY = 0f;
        for (int i = 0; i < n; i++)
        {
            sourceMeanX += source[i].X;
            sourceMeanY += source[i].Y;
            targetMeanX += Reference[i].X;
            targetMeanY += Reference[i].Y;
        }

        sourceMeanX /= n;
        sourceMeanY /= n;
        targetMeanX /= n;
        targetMeanY /= n;

        // a accumulates the aligned (dot) component, b the perpendicular (cross) component;
        // together they give scale*cos and scale*sin directly.
        float a = 0f, b = 0f, sourceVariance = 0f;

        for (int i = 0; i < n; i++)
        {
            float sx = source[i].X - sourceMeanX;
            float sy = source[i].Y - sourceMeanY;
            float tx = Reference[i].X - targetMeanX;
            float ty = Reference[i].Y - targetMeanY;

            a += (sx * tx) + (sy * ty);
            b += (sx * ty) - (sy * tx);
            sourceVariance += (sx * sx) + (sy * sy);
        }

        if (sourceVariance < 1e-6f)
        {
            // Degenerate landmarks (all coincident) - nothing sensible to fit.
            throw new FaceProcessingException(
                FaceError.LowQuality,
                "The detected landmarks are degenerate; the face is probably too small or occluded.");
        }

        float scaleCos = a / sourceVariance;
        float scaleSin = b / sourceVariance;

        float translateX = targetMeanX - ((scaleCos * sourceMeanX) - (scaleSin * sourceMeanY));
        float translateY = targetMeanY - ((scaleSin * sourceMeanX) + (scaleCos * sourceMeanY));

        return new SKMatrix
        {
            ScaleX = scaleCos,
            SkewX = -scaleSin,
            TransX = translateX,
            SkewY = scaleSin,
            ScaleY = scaleCos,
            TransY = translateY,
            Persp0 = 0f,
            Persp1 = 0f,
            Persp2 = 1f
        };
    }

    /// <summary>
    /// Yaw estimate from landmark geometry: how far the nose has slid off the midpoint between
    /// the eyes, in units of inter-eye distance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Convention: <b>positive yaw means the subject turned towards their own left</b>, which in
    /// a non-mirrored frame moves the nose towards the right of the image.
    /// </para>
    /// <para>
    /// Front-facing phone cameras usually hand back a mirrored preview, which flips that sign.
    /// That is what <c>MirroredInput</c> is for - get it wrong and every left capture is
    /// rejected as a right one.
    /// </para>
    /// <para>
    /// This is a monocular heuristic, good to roughly +/-10 degrees and plenty for sorting a
    /// capture into front/left/right. It is not a substitute for a real 6DoF pose model.
    /// </para>
    /// </remarks>
    public static HeadPose EstimateHeadPose(FaceLandmarks landmarks, bool mirroredInput)
    {
        float eyeMidX = (landmarks.LeftEye.X + landmarks.RightEye.X) / 2f;
        float eyeMidY = (landmarks.LeftEye.Y + landmarks.RightEye.Y) / 2f;

        float eyeDeltaX = landmarks.RightEye.X - landmarks.LeftEye.X;
        float eyeDeltaY = landmarks.RightEye.Y - landmarks.LeftEye.Y;
        float eyeDistance = MathF.Sqrt((eyeDeltaX * eyeDeltaX) + (eyeDeltaY * eyeDeltaY));

        if (eyeDistance < 1e-3f)
        {
            return new HeadPose(0d, 0d, 0d);
        }

        double yaw = Math.Clamp((landmarks.Nose.X - eyeMidX) / eyeDistance, -1d, 1d) * 90d;

        if (mirroredInput)
        {
            yaw = -yaw;
        }

        float mouthMidY = (landmarks.MouthLeft.Y + landmarks.MouthRight.Y) / 2f;
        float eyeToMouth = mouthMidY - eyeMidY;

        // Nose sitting high or low between the eye line and the mouth line implies pitch.
        double pitch = eyeToMouth > 1e-3f
            ? Math.Clamp(((landmarks.Nose.Y - eyeMidY) / eyeToMouth) - 0.5d, -1d, 1d) * 90d
            : 0d;

        // Roll comes straight out of the eye-line angle.
        double roll = Math.Atan2(eyeDeltaY, eyeDeltaX) * 180d / Math.PI;

        return new HeadPose(yaw, pitch, roll);
    }
}
