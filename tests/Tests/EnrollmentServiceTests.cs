using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Common;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Domain;
using FaceAttendance.Core.Faces;
using FaceAttendance.Core.Services;
using FaceAttendance.Infrastructure.Persistence;
using FaceAttendance.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FaceAttendance.Tests;

/// <summary>
/// Exercises the enrolment rules against a real (in-memory SQLite) database and a scripted
/// provider, so the repository queries are covered rather than mocked away.
/// </summary>
public sealed class EnrollmentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly FakeImageStore _store = new();
    private readonly ScriptedProvider _provider = new();
    private readonly FaceRecognitionOptions _options;
    private readonly EnrollmentService _service;

    public EnrollmentServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options);

        _db.Database.EnsureCreated();

        _options = new FaceRecognitionOptions
        {
            RequiredPoses = [FacePose.Front, FacePose.Left, FacePose.Right],
            AllowedContentTypes = ["image/jpeg"],
            MaxImageBytes = 1024 * 1024,
            MinImageDimension = 240,
            MinFaceDimension = 80,
            MinDetectionScore = 0.6,
            MinQualityScore = 0.35,
            EnforcePoseCheck = false,
            DuplicateEnrollmentThreshold = 0.6,
            MinEnrollmentPairSimilarity = 0.2,
            RejectDuplicateFrames = true,
            RejectDuplicateEnrollments = true
        };

        IOptions<FaceRecognitionOptions> wrapped = Options.Create(_options);

        _service = new EnrollmentService(
            new PersonRepository(_db),
            new FaceTemplateRepository(_db),
            _store,
            _provider,
            new FaceCaptureValidator(wrapped),
            _db,
            new SystemClock(),
            wrapped,
            NullLogger<EnrollmentService>.Instance);
    }

    private static FaceImage Jpeg(string marker)
    {
        byte[] bytes = new byte[512];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;

        byte[] seed = System.Text.Encoding.UTF8.GetBytes(marker);
        Array.Copy(seed, 0, bytes, 16, Math.Min(seed.Length, 64));

        return new FaceImage(bytes, "image/jpeg", $"{marker}.jpg");
    }

    private Task<Person> CreateAsync(string externalId = "EMP-1", string name = "Test Person") =>
        _service.CreatePersonAsync(new CreatePersonCommand("default", externalId, name));

    private Task<CaptureFaceResult> CaptureAsync(Person person, FacePose pose, string marker) =>
        _service.CaptureFaceAsync(new CaptureFaceCommand("default", person.Id, pose, Jpeg(marker)));

    [Fact]
    public async Task CreatePerson_StartsNotEnrolled()
    {
        Person person = await CreateAsync();

        Assert.Equal(EnrollmentStatus.NotEnrolled, person.EnrollmentStatus);
        Assert.Null(person.EnrolledAtUtc);
    }

    [Fact]
    public async Task CreatePerson_DuplicateExternalId_Conflicts()
    {
        await CreateAsync("EMP-7");

        DomainException ex = await Assert.ThrowsAsync<DomainException>(() => CreateAsync("EMP-7"));

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.Equal("external_id_exists", ex.Code);
    }

    [Fact]
    public async Task EnrollmentCompletes_OnlyAfterAllThreePoses()
    {
        Person person = await CreateAsync();
        _provider.Identity = "alice";

        CaptureFaceResult front = await CaptureAsync(person, FacePose.Front, "f");
        Assert.Equal(EnrollmentStatus.InProgress, front.EnrollmentStatus);
        Assert.Equal([FacePose.Left, FacePose.Right], front.MissingPoses);

        CaptureFaceResult left = await CaptureAsync(person, FacePose.Left, "l");
        Assert.Equal(EnrollmentStatus.InProgress, left.EnrollmentStatus);

        CaptureFaceResult right = await CaptureAsync(person, FacePose.Right, "r");

        Assert.Equal(EnrollmentStatus.Enrolled, right.EnrollmentStatus);
        Assert.True(right.EnrollmentCompleted);
        Assert.Empty(right.MissingPoses);
    }

    [Fact]
    public async Task RecapturingAPose_SupersedesInsteadOfDuplicating()
    {
        Person person = await CreateAsync();
        _provider.Identity = "alice";

        await CaptureAsync(person, FacePose.Front, "f1");
        CaptureFaceResult second = await CaptureAsync(person, FacePose.Front, "f2");

        Assert.Equal(2, second.Version);
        Assert.True(second.ReplacedPrevious);

        // Exactly one active Front, with the retired row kept for audit.
        Assert.Equal(1, await _db.FaceTemplates.CountAsync(t => t.PersonId == person.Id && t.IsActive));
        Assert.Equal(2, await _db.FaceTemplates.CountAsync(t => t.PersonId == person.Id));
    }

    [Fact]
    public async Task SameFrameForTwoPoses_Rejected()
    {
        Person person = await CreateAsync();
        _provider.Identity = "alice";

        await CaptureAsync(person, FacePose.Front, "same");

        FaceProcessingException ex = await Assert.ThrowsAsync<FaceProcessingException>(
            () => CaptureAsync(person, FacePose.Left, "same"));

        Assert.Equal(FaceError.DuplicateFrame, ex.Error);
    }

    [Fact]
    public async Task SecondPersonWithTheSameFace_Rejected()
    {
        Person alice = await CreateAsync("EMP-1", "Alice");
        _provider.Identity = "alice";
        await CaptureAsync(alice, FacePose.Front, "a");

        Person impostor = await CreateAsync("EMP-2", "Impostor");

        FaceProcessingException ex = await Assert.ThrowsAsync<FaceProcessingException>(
            () => CaptureAsync(impostor, FacePose.Front, "b"));

        Assert.Equal(FaceError.DuplicateIdentity, ex.Error);
    }

    [Fact]
    public async Task DifferentPeople_BothEnrolFine()
    {
        Person alice = await CreateAsync("EMP-1", "Alice");
        _provider.Identity = "alice";
        await CaptureAsync(alice, FacePose.Front, "a");

        Person bob = await CreateAsync("EMP-2", "Bob");
        _provider.Identity = "bob";

        Assert.Equal(FacePose.Front, (await CaptureAsync(bob, FacePose.Front, "b")).Pose);
    }

    [Fact]
    public async Task InconsistentPoses_DoNotCompleteEnrollment()
    {
        // Three different faces submitted under one id: the set is complete but incoherent, so
        // it must not be marked Enrolled.
        Person person = await CreateAsync();
        _options.RejectDuplicateEnrollments = false;

        _provider.Identity = "alice";
        await CaptureAsync(person, FacePose.Front, "f");
        _provider.Identity = "bob";
        await CaptureAsync(person, FacePose.Left, "l");
        _provider.Identity = "carol";
        CaptureFaceResult last = await CaptureAsync(person, FacePose.Right, "r");

        Assert.Equal(EnrollmentStatus.InProgress, last.EnrollmentStatus);
        Assert.NotEmpty(last.Warnings);
    }

    [Fact]
    public async Task DeletingAPose_UnEnrolsThePerson()
    {
        Person person = await CreateAsync();
        _provider.Identity = "alice";

        await CaptureAsync(person, FacePose.Front, "f");
        await CaptureAsync(person, FacePose.Left, "l");
        await CaptureAsync(person, FacePose.Right, "r");

        await _service.DeletePoseAsync("default", person.Id, FacePose.Left);

        EnrollmentSummary summary = await _service.GetEnrollmentAsync("default", person.Id);

        Assert.Equal(EnrollmentStatus.InProgress, summary.EnrollmentStatus);
        Assert.Equal([FacePose.Left], summary.MissingPoses);
    }

    [Fact]
    public async Task Purge_RemovesTemplatesAndImages()
    {
        Person person = await CreateAsync();
        _provider.Identity = "alice";
        await CaptureAsync(person, FacePose.Front, "f");

        Assert.Single(_store.Saved);

        await _service.PurgeBiometricsAsync("default", person.Id);

        Assert.Empty(_store.Saved);
        Assert.Equal(0, await _db.FaceTemplates.CountAsync(t => t.PersonId == person.Id));
    }

    [Fact]
    public async Task RegisterAsync_RollsBackThePersonWhenACaptureFails()
    {
        _provider.Identity = "alice";
        _provider.FailOn = FacePose.Right;

        await Assert.ThrowsAsync<FaceProcessingException>(() => _service.RegisterAsync(
            new CreatePersonCommand("default", "EMP-50", "Doomed"),
            [
                (FacePose.Front, Jpeg("f")),
                (FacePose.Left, Jpeg("l")),
                (FacePose.Right, Jpeg("r"))
            ]));

        // No orphan person holding the ExternalId, and no leftover images in the store.
        Assert.False(await _db.People.AnyAsync(p => p.ExternalId == "EMP-50"));
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public async Task CaptureForAnotherTenant_IsReportedAsNotFound()
    {
        Person person = await CreateAsync();

        DomainException ex = await Assert.ThrowsAsync<DomainException>(
            () => _service.CaptureFaceAsync(
                new CaptureFaceCommand("other-tenant", person.Id, FacePose.Front, Jpeg("f"))));

        Assert.Equal(DomainErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task RegisterAsync_SamePictureInAllThreeSlots_NamesThemAndSavesNothing()
    {
        _provider.Identity = "alice";
        FaceImage same = Jpeg("one-picture");

        FaceProcessingException ex = await Assert.ThrowsAsync<FaceProcessingException>(() =>
            _service.RegisterAsync(
                new CreatePersonCommand("default", "EMP-60", "Copied Thrice"),
                [(FacePose.Front, same), (FacePose.Left, same), (FacePose.Right, same)]));

        Assert.Equal(FaceError.DuplicateFrame, ex.Error);
        Assert.Contains("Front, Left and Right", ex.Message);

        // Refused before anything was written: no person, no photo.
        Assert.False(await _db.People.AnyAsync());
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public async Task RegisterAsync_SamePictureInTwoSlots_NamesExactlyThoseTwo()
    {
        _provider.Identity = "alice";
        FaceImage same = Jpeg("one-picture");

        FaceProcessingException ex = await Assert.ThrowsAsync<FaceProcessingException>(() =>
            _service.RegisterAsync(
                new CreatePersonCommand("default", "EMP-61", "Copied Twice"),
                [(FacePose.Front, same), (FacePose.Left, Jpeg("different")), (FacePose.Right, same)]));

        Assert.Equal(FaceError.DuplicateFrame, ex.Error);
        Assert.Contains("Front and Right", ex.Message);
        Assert.False(await _db.People.AnyAsync());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
