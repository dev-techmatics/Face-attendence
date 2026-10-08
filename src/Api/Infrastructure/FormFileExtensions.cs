using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Faces;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Api.Infrastructure;

public static class FormFileExtensions
{
    /// <summary>
    /// Read an upload into a <see cref="FaceImage"/>, refusing anything oversized before it is
    /// buffered.
    /// </summary>
    /// <remarks>
    /// The size is checked against the declared length first so a hostile 2GB upload is rejected
    /// without being read into memory, and the copy is then bounded anyway in case the declared
    /// length was a lie.
    /// </remarks>
    public static async Task<FaceImage> ToFaceImageAsync(
        this IFormFile file,
        FaceRecognitionOptions options,
        CancellationToken ct = default)
    {
        if (file.Length <= 0)
        {
            throw new FaceProcessingException(
                FaceError.CorruptImage,
                $"The uploaded file '{file.Name}' is empty.");
        }

        if (file.Length > options.MaxImageBytes)
        {
            throw new FaceProcessingException(
                FaceError.ImageTooLarge,
                $"'{file.FileName}' is {file.Length / 1024}KB; the limit is {options.MaxImageBytes / 1024}KB.");
        }

        using MemoryStream buffer = new(capacity: (int)file.Length);
        await using Stream source = file.OpenReadStream();

        byte[] chunk = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            total += read;

            if (total > options.MaxImageBytes)
            {
                throw new FaceProcessingException(
                    FaceError.ImageTooLarge,
                    "The upload exceeded the size limit while being read.");
            }

            buffer.Write(chunk, 0, read);
        }

        return new FaceImage(
            buffer.ToArray(),
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            file.FileName);
    }

    public static Task<FaceImage> ToFaceImageAsync(
        this IFormFile file,
        IOptions<FaceRecognitionOptions> options,
        CancellationToken ct = default) =>
        file.ToFaceImageAsync(options.Value, ct);
}
