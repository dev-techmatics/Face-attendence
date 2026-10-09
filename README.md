# Face Attendance API

A .NET 10 Web API for face-based attendance. Register a person by capturing **three angles**
(front, left, right) under one id; later verify a live frame against those registrations and
record the punch.

Face recognition sits behind a single interface with three implementations, so you can run it
**entirely on your own SQL Server with no cloud account**, or switch to AWS Rekognition with one
config value. See [ARCHITECTURE.md](ARCHITECTURE.md) for that decision in full - it answers
"do we need AWS, or will a traditional DB do?" directly.

## Status

Built and verified against a local SQL Server Express instance. 68 unit and integration tests
pass, the solution builds with zero warnings, and the registration and attendance flows have been
exercised end to end over HTTP.

## Quick start

```bash
# 1. Database (SQL Server Express on the default .\SQLEXPRESS instance)
powershell -ExecutionPolicy Bypass -File scripts\setup-database.ps1

# 2. Run with the stub provider - no model files needed
dotnet run --project src/Api
```

Then open `http://localhost:5080/scalar/v1` for the interactive API reference.

The Development profile starts with `FaceRecognition:Provider = "development"`, which performs
**no real recognition** - it matches on the upload file name so the API, schema and front end can
be built before any model exists. `alice-front.jpg`, `alice-left.jpg` and `alice-right.jpg` enrol
as one person and verify against each other; `bob-front.jpg` will not match them.

To switch on real recognition:

```bash
powershell -ExecutionPolicy Bypass -File scripts\download-models.ps1   # SCRFD + ArcFace, ~255MB
# set FaceRecognition:Provider to "onnx" in src/Api/appsettings.Development.json
dotnet run --project src/Api
```

Confirm which engine is live with `GET /api/v1/diagnostics/provider`.

## Checking it works

Three ways, cheapest first.

**1. The smoke test — one command, 28 checks.** Start the API, then:

```
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
```

It registers two people from three poses each, verifies a punch, then deliberately tries the
things that should fail: an imposter, an unknown face, a double tap, a face already registered
under another id, a device key trying to enrol, and a text file renamed to `.jpg`. Every check
prints PASS or FAIL with the reason. It is idempotent — run it as often as you like.

It needs no PowerShell 7; it talks to `System.Net.Http` directly so it works on the Windows
PowerShell 5.1 that ships with Windows.

**2. The interactive reference.** With the API running in Development, open:

```
http://localhost:5080/scalar/v1
```

Every endpoint is listed with its schema and a Try It button. Paste `dev-admin-key` into the
`X-Api-Key` header to call anything.

**3. Hand-written requests.** [docs/api-examples.http](docs/api-examples.http) holds a runnable
request per endpoint for the VS Code REST Client or the JetBrains HTTP client, including a
section of calls that are *supposed* to fail. It uses the images in
[docs/samples](docs/samples/), so it works without edits.

For a single quick check that the service is alive and which engine is behind it:

```
curl http://localhost:5080/health
curl -H "X-Api-Key: dev-admin-key" http://localhost:5080/api/v1/diagnostics/provider
```

If `providerId` starts with `development:` then **no real face recognition is running** — it is
matching on upload file names. That is the default so the front end can be built first.

## Opening the project

You do not need an IDE at all - `dotnet build`, `dotnet test` and `dotnet run` are the whole
story. If you want an editor:

- **VS Code** - open the *folder*, not the solution file: `code .` from here. Install the C#
  Dev Kit for IntelliSense and debugging: `code --install-extension ms-dotnettools.csdevkit`.
- **Visual Studio / Rider** - open `FaceAttendance.sln`.

Note that double-clicking a `.sln` on a machine with no Visual Studio installed makes Windows
ask which application to use, because the extension is registered to a Visual Studio launcher
that is not there. That is a file-association quirk, not a problem with the solution.

The solution is deliberately in the classic `.sln` format rather than the newer `.slnx` that the
.NET 10 SDK defaults to, because `.slnx` is only readable by very recent Visual Studio and Rider
builds.

### If a build fails with "the file is locked by: FaceAttendance.Api"

```
error MSB3027: Could not copy "FaceAttendance.Core.dll" ... The file is locked by:
"FaceAttendance.Api (24524)"
```

This is not a code error. The API is still running and holding its own DLLs open, so MSBuild
cannot overwrite them. One failure produces four errors and twenty warnings - MSBuild retries
each file ten times before giving up - which makes it look far worse than it is.

Stop the app and rebuild. In VS Code, Shift+F5 ends a debug session cleanly; for a stray
process there is a **stop running API** task, or:

```
powershell -NoProfile -Command "Get-Process FaceAttendance.Api -ErrorAction SilentlyContinue | Stop-Process -Force"
```

Starting the app with F5 rather than a detached `dotnet run` avoids this: stopping the debugger
terminates the process.

## Registering a person

The endpoint most capture screens want is a single multipart call that creates the person and
stores all three poses together:

```bash
curl -X POST http://localhost:5080/api/v1/registrations \
  -H "X-Api-Key: dev-admin-key" \
  -F "externalId=EMP-1001" \
  -F "fullName=Alice Kumar" \
  -F "front=@alice-front.jpg" \
  -F "left=@alice-left.jpg" \
  -F "right=@alice-right.jpg"
```

```json
{
  "person": { "id": 10001, "externalId": "EMP-1001", "enrollmentStatus": "Enrolled" },
  "captures": [ { "pose": "Front", "version": 1, "analysis": { "qualityScore": 0.82 } }, "..." ],
  "enrollmentCompleted": true
}
```

It is atomic in the way that matters: if any of the three images is rejected, the new person
record is removed again. A person row holding no usable face is worse than no row at all, because
it occupies the `ExternalId` and reads as enrolled in a list.

Prefer capturing one pose at a time (a wizard with live feedback)? Create the person first, then
post each pose to the same id:

```
POST /api/v1/persons                     -> { "id": "..." }
POST /api/v1/persons/{id}/faces          -> pose=Front, image=@front.jpg
POST /api/v1/persons/{id}/faces          -> pose=Left,  image=@left.jpg
POST /api/v1/persons/{id}/faces          -> pose=Right, image=@right.jpg
```

Each response reports `enrollmentStatus`, `capturedPoses` and `missingPoses`, so the UI always
knows what is left. Re-posting a pose supersedes the previous capture rather than duplicating it,
so a retry is always safe.

## Marking attendance

```bash
curl -X POST http://localhost:5080/api/v1/attendance/mark \
  -H "X-Api-Key: dev-device-key" \
  -F "externalId=EMP-1001" \
  -F "deviceId=GATE-A" \
  -F "image=@live-frame.jpg"
```

```json
{
  "success": true,
  "decision": "Accepted",
  "direction": "In",
  "fullName": "Alice Kumar",
  "score": 0.71,
  "threshold": 0.42,
  "matchedPose": "Front",
  "processingMs": 148
}
```

**Send `externalId` or `personId` whenever the device can obtain it.** That makes the call a 1:1
verification, whose accuracy does not degrade as you add employees. Omitting both performs a 1:N
search across the tenant: convenient, but the false-accept rate grows with headcount, so the
threshold is stricter and a near-tie between two candidates is refused rather than guessed.

A non-match returns **200 with `success: false`** and a `decision` code, not a 4xx. The request
was processed correctly; it simply did not match. 4xx is reserved for malformed requests.

Decisions: `Accepted`, `Rejected`, `Duplicate`, `NotEnrolled`, `NoFaceDetected`, `LowQuality`,
`SpoofSuspected`, `ProviderError`.

Direction is inferred from the previous event by default, so a mis-tapped button cannot corrupt
the day, and a new day always starts with an `In`. Any repeat punch within
`DuplicatePunchWindowMinutes` (default 2) is collapsed into the first.

## Endpoints

Every route, with the key it needs. `/scalar/v1` renders the same list interactively.

| Method | Route | Key | Purpose |
|---|---|---|---|
| POST | `/api/v1/registrations` | admin | Create person + all three poses in one call |
| POST | `/api/v1/persons` | admin | Create a person |
| GET | `/api/v1/persons` | admin | Search, paged |
| GET | `/api/v1/persons/{id}` | admin | One person |
| GET | `/api/v1/persons/by-external-id/{externalId}` | admin | Look up by your own id |
| GET | `/api/v1/persons/{id}/enrollment` | admin | Poses held, poses missing, pose similarities |
| DELETE | `/api/v1/persons/{id}/biometrics` | admin | Erase all templates and images |
| POST | `/api/v1/persons/{id}/faces` | admin | Store one pose |
| POST | `/api/v1/persons/{id}/faces/batch` | admin | Store all three poses |
| DELETE | `/api/v1/persons/{id}/faces/{pose}` | admin | Retire one pose |
| GET | `/api/v1/persons/{id}/faces/{pose}/image` | admin | Download a stored capture |
| POST | `/api/v1/attendance/mark` | device | Verify and record |
| POST | `/api/v1/attendance/preview` | admin | Score a frame, record nothing |
| GET | `/api/v1/attendance` | admin | Register, including failures |
| GET | `/api/v1/diagnostics/provider` | admin | Active engine and thresholds |
| POST | `/api/v1/diagnostics/analyze` | admin | Detection and quality for one frame |
| GET | `/health` | none | Liveness, including database |

Authentication is an `X-Api-Key` header with two roles. **Device keys can only mark attendance** -
they cannot enrol a face or download a photograph. A device key is the one most likely to be
extracted from a kiosk, so it is deliberately the least useful thing to steal.

Multi-site deployments pass `X-Tenant-Id`; single-site ones can ignore it.

## What the API refuses, and why

These checks exist because each one corresponds to a way a face attendance system goes wrong in
production.

| Guard | Response | Why it is there |
|---|---|---|
| File content is not a real JPEG/PNG/WebP | 415 `unsupported_format` | The declared content type is attacker-controlled, so the magic bytes decide |
| No face, or more than one | 422 `no_face_detected` / `multiple_faces_detected` | A bystander in frame is how the wrong face gets enrolled |
| Face too small, blurred or badly exposed | 422 `low_quality` + hints | A bad template silently degrades every future match |
| Head not turned as the pose asked | 422 `pose_mismatch` | Three frontal shots would otherwise pass as a complete three-angle enrolment |
| Same image submitted for two poses | 409 `duplicate_frame` | Same as above, by a different route |
| Face already registered under another id | 409 `duplicate_identity` | One person holding two identities can cover for an absent colleague |
| Three poses that are not the same person | stays `InProgress`, with warnings | A complete-but-incoherent enrolment is worse than an incomplete one |
| Repeat punch inside the window | 200 `Duplicate` | One arrival should not become two punches |
| 1:N winner barely beats the runner-up | 200 `Rejected` with the margin | Two people who look alike to the model is a coin toss, not a decision |

Errors are RFC 9457 problem documents with a stable `code` and actionable `hints`:

```json
{
  "title": "The image could not be used",
  "status": 422,
  "detail": "Quality score 0.22 is below the required 0.35.",
  "code": "low_quality",
  "hints": ["The frame is blurry - hold steady, or capture on a tap rather than continuously."]
}
```

Branch on `code`, never on prose.

## Configuration

Everything lives under `FaceRecognition` in `appsettings.json`. The settings you are most likely
to change:

```jsonc
{
  "FaceRecognition": {
    "Provider": "onnx",               // onnx | aws | development
    "VerificationThreshold": 0.42,    // 1:1
    "IdentificationThreshold": 0.52,  // 1:N, stricter on purpose
    "IdentificationMargin": 0.06,     // minimum lead over the runner-up in a 1:N search
    "DuplicateEnrollmentThreshold": 0.60,
    "MinQualityScore": 0.35,
    "MinFaceDimension": 80,           // pixels; smaller means the subject is too far away
    "EnforcePoseCheck": true,
    "DuplicatePunchWindowMinutes": 2,
    "ProbeImageRetentionDays": 30,
    "Onnx": {
      "MirroredInput": false,         // true for a front-facing phone camera preview
      "RecognitionInputBgr": false,   // some ONNX-zoo ArcFace conversions want BGR
      "MaxConcurrency": 4             // caps CPU during the shift-change rush
    }
  }
}
```

Config is validated at startup, not on first use. A threshold typo such as `42` where `0.42` was
meant stops the process rather than rejecting every employee all day.

Two settings deserve a warning:

- **`MirroredInput`** - front-facing phone cameras usually hand back a mirrored preview, which
  flips the sign of the yaw estimate. Get it wrong and every left capture is rejected for being a
  right one.
- **`RecognitionInputBgr`** - the wrong value does not crash, it just quietly lowers every score.
  Verify against a known pair after changing models.

## Project layout

```
src/Core            Domain, provider port, services. No infrastructure dependencies.
  Domain/           Person, FaceTemplate, AttendanceEvent
  Abstractions/     IFaceRecognitionProvider, IFaceImageStore, repositories
  Services/         EnrollmentService, AttendanceService, FaceCaptureValidator
  Common/           VectorMath (cosine, serialisation, centroid)

src/Infra           EF Core, image stores, the three providers
  Persistence/      DbContext, configurations, migrations, retention sweep
  Providers/Onnx/   SCRFD detector, ArcFace embedder, 5-point aligner
  Providers/Aws/    Rekognition
  Providers/Development/  Deterministic stub

src/Api             Controllers, DTOs, auth, problem details
tests/Tests         68 tests (SQLite in-memory for the repository ones)
scripts/            setup-database, download-models, smoke-test
docs/samples/       synthetic placeholder images - dev provider only, not real faces
```

## Database

Three tables. The indexes are not decoration - each one backs a query on a hot path.

```
People           (TenantId, ExternalId) unique
FaceTemplates    (PersonId, Pose) unique WHERE IsActive = 1
AttendanceEvents (PersonId, CreatedAtUtc)
```

A 512-float embedding is exactly 2048 bytes, stored inline on the template row. Superseded
templates are kept with `IsActive = 0` so a capture history survives, and the filtered unique
index guarantees one live template per pose even under a concurrent double upload.

`AttendanceEvents` holds **every** attempt, accepted or not. Those failures are the audit trail
you need when somebody says the machine did not recognise them.

```bash
dotnet ef migrations add <Name> \
  --project src/Infra/FaceAttendance.Infrastructure.csproj \
  --startup-project src/Api/FaceAttendance.Api.csproj \
  --output-dir Persistence/Migrations
```

Migrations run automatically in Development only. In production put them in your deploy pipeline:
two instances starting at once would otherwise race through the same schema change.

## Tests

```bash
dotnet test
```

68 tests covering vector maths and serialisation round-trips, upload and quality validation, the
pose gate, options validation, and the enrolment rules end to end against a real database.

## Before you go live

1. **Replace the API keys.** The Development ones are `dev-admin-key` and `dev-device-key`. Use
   user secrets or environment variables, never `appsettings.json`.
2. **Set `FaceRecognition:Provider`** to `onnx` or `aws`. Starting with `development` outside the
   Development environment is refused at startup, but check anyway.
3. **Calibrate thresholds** against your own cameras using `/attendance/preview`.
4. **Set `Cors:AllowedOrigins`** if a browser calls the API directly. Empty means no origins, not
   all origins.
5. **Move to S3 or Azure Blob** if you run more than one instance - the local store is not shared.
6. **Decide about liveness.** Nothing here stops a printed photograph. See
   [ARCHITECTURE.md](ARCHITECTURE.md#what-this-system-deliberately-does-not-do).
7. **Write down a retention policy** and set `ProbeImageRetentionDays` to match.
