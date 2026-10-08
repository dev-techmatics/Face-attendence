using System.Text;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Core.Services;

/// <summary>
/// Gatekeeper for everything that arrives from a capture screen. Runs before any model does
/// work, so bad frames cost a few microseconds instead of an inference.
/// </summary>
public sealed class FaceCaptureValidator(IOptions<FaceRecognitionOptions> options)
{
    private readonly FaceRecognitionOptions _options = options.Value;

    /// <summary>Cheap checks on the raw upload: size, declared type, and actual file signature.</summary>
    public void ValidateUpload(FaceImage image)
    {
        if (image.SizeBytes == 0)
        {
            throw new FaceProcessingException(FaceError.CorruptImage, "The uploaded image is empty.");
        }

        if (image.SizeBytes > _options.MaxImageBytes)
        {
            throw new FaceProcessingException(
                FaceError.ImageTooLarge,
                $"Image is {image.SizeBytes / 1024}KB; the limit is {_options.MaxImageBytes / 1024}KB.")
            {
                Hints = ["Capture at 720p and encode as JPEG quality 80 - that is ample for recognition."]
            };
        }

        if (!_options.AllowedContentTypes.Contains(image.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new FaceProcessingException(
                FaceError.UnsupportedFormat,
                $"Content type '{image.ContentType}' is not accepted. Allowed: {string.Join(", ", _options.AllowedContentTypes)}.");
        }

        // Never trust the declared content type: a client can label anything image/jpeg.
        if (DetectSignature(image.Content) is null)
        {
            throw new FaceProcessingException(
                FaceError.UnsupportedFormat,
                "The file content is not a JPEG, PNG or WebP image, whatever the content type says.");
        }
    }

    /// <summary>Checks that need the detector output.</summary>
    public void ValidateAnalysis(FaceCaptureAnalysis analysis)
    {
        if (analysis.ImageWidth < _options.MinImageDimension || analysis.ImageHeight < _options.MinImageDimension)
        {
            throw new FaceProcessingException(
                FaceError.ImageTooSmall,
                $"Image is {analysis.ImageWidth}x{analysis.ImageHeight}; both sides must be at least {_options.MinImageDimension}px.");
        }

        if (!analysis.FaceDetected)
        {
            throw new FaceProcessingException(FaceError.NoFaceDetected, "No face was found in the frame.")
            {
                Hints =
                [
                    "Fill roughly a third of the frame with the face.",
                    "Light the face from the front; avoid a bright window behind the subject."
                ]
            };
        }

        if (analysis.FaceCount > 1)
        {
            throw new FaceProcessingException(
                FaceError.MultipleFacesDetected,
                $"{analysis.FaceCount} faces were found. Registration needs exactly one person in frame.");
        }

        if (analysis.DetectionScore < _options.MinDetectionScore)
        {
            throw new FaceProcessingException(
                FaceError.LowQuality,
                $"Detector confidence {analysis.DetectionScore:F2} is below the required {_options.MinDetectionScore:F2}.");
        }

        if (analysis.Box is { } box &&
            Math.Min(box.Width, box.Height) < _options.MinFaceDimension)
        {
            throw new FaceProcessingException(
                FaceError.LowQuality,
                $"The face occupies only {box.Width:F0}x{box.Height:F0}px; at least {_options.MinFaceDimension}px is needed.")
            {
                Hints = ["Ask the subject to move closer to the camera."]
            };
        }

        if (analysis.QualityScore < _options.MinQualityScore)
        {
            List<string> hints = [];
            if (analysis.Sharpness < 0.3)
            {
                hints.Add("The frame is blurry - hold steady, or capture on a tap rather than continuously.");
            }

            if (analysis.Brightness < 0.25)
            {
                hints.Add("The frame is underexposed - add light on the face.");
            }
            else if (analysis.Brightness > 0.85)
            {
                hints.Add("The frame is overexposed - reduce backlight or move out of direct sun.");
            }

            throw new FaceProcessingException(
                FaceError.LowQuality,
                $"Quality score {analysis.QualityScore:F2} is below the required {_options.MinQualityScore:F2}.")
            {
                Hints = hints
            };
        }
    }

    /// <summary>
    /// Confirms the subject actually turned their head the way the prompt asked. Without this,
    /// three frontal shots sail through as a complete three-pose enrolment and you lose the
    /// angular coverage that made the three captures worth collecting.
    /// </summary>
    public void ValidatePose(FaceCaptureAnalysis analysis, FacePose expected)
    {
        if (!_options.EnforcePoseCheck || analysis.HeadPose is not { } head)
        {
            return;
        }

        double yaw = head.YawDegrees;

        switch (expected)
        {
            case FacePose.Front when Math.Abs(yaw) > _options.MaxFrontalYawDegrees:
                throw new FaceProcessingException(
                    FaceError.PoseMismatch,
                    $"This is meant to be a straight-on capture but the head is turned {Math.Abs(yaw):F0} degrees.")
                {
                    Hints = ["Look directly at the camera."]
                };

            // Yaw sign convention: positive yaw means the subject turned to their own left.
            case FacePose.Left when yaw < _options.MinProfileYawDegrees:
                throw new FaceProcessingException(
                    FaceError.PoseMismatch,
                    $"Expected a left-turned capture of at least {_options.MinProfileYawDegrees:F0} degrees; measured {yaw:F0}.")
                {
                    Hints = ["Ask the subject to turn their head to their left, keeping both eyes visible."]
                };

            case FacePose.Right when yaw > -_options.MinProfileYawDegrees:
                throw new FaceProcessingException(
                    FaceError.PoseMismatch,
                    $"Expected a right-turned capture of at least {_options.MinProfileYawDegrees:F0} degrees; measured {yaw:F0}.")
                {
                    Hints = ["Ask the subject to turn their head to their right, keeping both eyes visible."]
                };
        }
    }

    /// <summary>Returns the normalised content type implied by the file bytes, or null.</summary>
    public static string? DetectSignature(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (content.Length >= 8 &&
            content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47 &&
            content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A)
        {
            return "image/png";
        }

        if (content.Length >= 12 &&
            Encoding.ASCII.GetString(content[..4]) == "RIFF" &&
            Encoding.ASCII.GetString(content[8..12]) == "WEBP")
        {
            return "image/webp";
        }

        return null;
    }
}
