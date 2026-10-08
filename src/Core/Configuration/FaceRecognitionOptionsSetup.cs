using FaceAttendance.Core.Domain;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Core.Configuration;

/// <summary>
/// Fills in list defaults and dedupes, after configuration binding has run.
/// </summary>
/// <remarks>
/// Array properties are left empty in <see cref="FaceRecognitionOptions"/> because the
/// configuration binder appends to a populated default rather than replacing it. Defaults
/// therefore have to be applied here, once binding is complete.
/// </remarks>
public sealed class FaceRecognitionOptionsSetup : IPostConfigureOptions<FaceRecognitionOptions>
{
    private static readonly FacePose[] DefaultPoses = [FacePose.Front, FacePose.Left, FacePose.Right];

    private static readonly string[] DefaultContentTypes =
        ["image/jpeg", "image/jpg", "image/png", "image/webp"];

    public void PostConfigure(string? name, FaceRecognitionOptions options)
    {
        options.RequiredPoses = options.RequiredPoses.Length == 0
            ? DefaultPoses
            : [.. options.RequiredPoses.Distinct()];

        options.AllowedContentTypes = options.AllowedContentTypes.Length == 0
            ? DefaultContentTypes
            : [.. options.AllowedContentTypes
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim().ToLowerInvariant())
                .Distinct()];
    }
}

/// <summary>
/// Startup validation. A threshold typo such as 42 instead of 0.42 would silently reject every
/// employee, so the process refuses to start rather than failing quietly all day.
/// </summary>
public sealed class FaceRecognitionOptionsValidator : IValidateOptions<FaceRecognitionOptions>
{
    public ValidateOptionsResult Validate(string? name, FaceRecognitionOptions options)
    {
        List<string> failures = [];

        Range01(options.VerificationThreshold, nameof(options.VerificationThreshold), failures);
        Range01(options.IdentificationThreshold, nameof(options.IdentificationThreshold), failures);
        Range01(options.DuplicateEnrollmentThreshold, nameof(options.DuplicateEnrollmentThreshold), failures);
        Range01(options.MinEnrollmentPairSimilarity, nameof(options.MinEnrollmentPairSimilarity), failures);
        Range01(options.MinQualityScore, nameof(options.MinQualityScore), failures);
        Range01(options.MinDetectionScore, nameof(options.MinDetectionScore), failures);

        if (options.IdentificationThreshold < options.VerificationThreshold)
        {
            failures.Add(
                "IdentificationThreshold is lower than VerificationThreshold. A 1:N search compares " +
                "against everybody, so it needs the stricter bar, not the looser one.");
        }

        if (options.MinImageDimension < 64)
        {
            failures.Add("MinImageDimension below 64px cannot hold a recognisable face.");
        }

        if (options.MinFaceDimension > options.MinImageDimension)
        {
            failures.Add("MinFaceDimension exceeds MinImageDimension, so no image could ever pass.");
        }

        if (options.MaxImageBytes < 32 * 1024)
        {
            failures.Add("MaxImageBytes below 32KB would reject ordinary camera output.");
        }

        if (options.RequiredPoses.Length == 0)
        {
            failures.Add("RequiredPoses is empty, so nobody could ever become enrolled.");
        }

        if (options.Provider is not ("onnx" or "aws" or "development"))
        {
            failures.Add($"Unknown provider '{options.Provider}'. Expected onnx, aws or development.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Range01(double value, string field, List<string> failures)
    {
        if (value is < 0d or > 1d)
        {
            failures.Add($"{field} is {value}; it must sit between 0 and 1.");
        }
    }
}
