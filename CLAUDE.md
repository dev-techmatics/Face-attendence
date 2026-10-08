# CLAUDE.md

Context for working in this repository. `README.md` is the usage guide and `ARCHITECTURE.md`
holds the self-hosted-vs-AWS reasoning; this file is the short version plus the things that cost
time to rediscover.

## What this is

A .NET 10 API for face-based attendance. A person is registered by capturing three angles
(Front, Left, Right) under one id; later a live frame is verified against those registrations and
an attendance row is written.

## Commands

```bash
dotnet build FaceAttendance.sln      # whole solution
dotnet test                          # 68 tests, xunit v2
dotnet run --project src/Api         # http://localhost:5080, Scalar UI at /scalar/v1
powershell -ExecutionPolicy Bypass -File scripts\setup-database.ps1   # migrations -> .\SQLEXPRESS
powershell -ExecutionPolicy Bypass -File scripts\download-models.ps1  # the two ONNX models (~255MB)
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1        # 28 checks against a running API
```

Local database is SQL Server Express, instance `.\SQLEXPRESS`, database `FaceAttendance`,
Windows auth. Dev API keys are `dev-admin-key` and `dev-device-key`.

```bash
dotnet ef migrations add <Name> \
  --project src/Infra/FaceAttendance.Infrastructure.csproj \
  --startup-project src/Api/FaceAttendance.Api.csproj \
  --output-dir Persistence/Migrations
```

## Structure

`src/Core` is the domain and has no infrastructure dependencies — keep it that way. `src/Infra`
holds EF Core, image stores and the three recognition providers. `src/Api` is controllers and
DTOs only; business rules belong in `Core/Services`.

## The central abstraction

`IFaceRecognitionProvider` is the only seam to anything that recognises faces. Three
implementations: `Onnx` (self-hosted, the default), `Aws` (Rekognition), `Development` (a stub).
Controllers and services must talk to the interface, never to a provider directly — that is what
keeps the engine swappable.

## Things that will bite you

- **Templates are provider-specific.** Every `FaceTemplate` stores `ProviderId`. Changing the
  recognition model invalidates every embedding, which is why `EnrollmentStatus.Stale` exists.
  Never compare templates across providers; `VectorMath.CosineSimilarity` throws on a dimension
  mismatch rather than returning a misleading low score.

- **The Development provider matches file names, not faces.** `alice-front.jpg` and
  `alice-left.jpg` enrol as one person. It is refused outside the Development environment. Never
  present an attendance row it produced as evidence of anything.

- **Config binding appends to array defaults.** `RequiredPoses` and `AllowedContentTypes` are
  deliberately initialised empty in `FaceRecognitionOptions`; defaults are applied in
  `FaceRecognitionOptionsSetup.PostConfigure`. Giving them non-empty initialisers reintroduces a
  bug where `appsettings.json` silently doubled the list.

- **The duplicate-punch check must stay before direction inference** in `AttendanceService`.
  Inference returns the opposite of the last punch, so checking afterwards lets an accidental
  double tap through as an `Out` and ends the person's shift.

- **Any new endpoint that accepts an image must call `FaceCaptureValidator.ValidateUpload`.**
  It sniffs magic bytes, because the declared content type is caller-controlled. A diagnostics
  endpoint once skipped this and happily reported "face detected" for a text file.

- **No `stackalloc` inside the detector's decode loops.** The stride-8 head has 12,800 anchors
  and stack from a `stackalloc` is not reclaimed until the method returns.

- **`InvariantGlobalization` must stay false.** `Microsoft.Data.SqlClient` refuses to run in
  globalization-invariant mode.

- **`sqlcmd` needs `-I`** for DML against these tables; the filtered unique index requires
  `QUOTED_IDENTIFIER ON`.

## Conventions

- Services throw `FaceProcessingException` (image not usable) or `DomainException` (request
  wrong). `FaceExceptionHandler` maps both to RFC 9457 problem documents with a stable `code`.
  Clients branch on `code`, never on prose. Only curated messages reach the caller; unrecognised
  exceptions get a generic detail.
- A failed face match is **200 with `success: false`** and a `decision`, not a 4xx. The request
  was valid; it did not match.
- Repositories return projections (`FaceCandidate`) and use `AsNoTracking` for read paths. The
  change tracker should never hold thousands of embeddings.
- Cross-tenant access is reported as 404, not 403 — confirming an id exists elsewhere already
  leaks more than the caller should know.
- Object keys are always built server-side via `ObjectKeys`. Never interpolate a client file name
  into a path.
- Comments explain *why*, especially where a line looks arbitrary (threshold choices, channel
  order, the alignment reference points).

## Known gaps — do not paper over these

- **No liveness detection.** A printed photo or a phone screen will pass. Three poses improve
  matching across angles, not spoof resistance. If asked whether this prevents photo spoofing,
  the answer is no.
- The local image store is not shared state; more than one API instance needs S3 or Azure Blob.
- API keys separate admin from device. There is no per-user identity.
- Head pose is a landmark-geometry heuristic, good to roughly ±10°.
- Thresholds in `appsettings.json` are starting points, not a calibration. Use
  `POST /api/v1/attendance/preview` to gather real distributions before trusting them.
