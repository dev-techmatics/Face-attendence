# Do you need AWS, or will an ordinary database do?

Short answer: **an ordinary database is fine. You do not need AWS.** But the question hides a
second one that matters more, so it is worth separating the two.

## The thing a database cannot do for you

Storing three JPEGs per person in SQL Server gives you *storage*. It does not give you
*recognition*. A relational database can tell you whether two rows are equal; it cannot tell you
whether two photographs are the same person.

What closes that gap is a model that converts a face into a vector of numbers - an **embedding**.
Faces of the same person land close together in that vector space; different people land far
apart. Once you have embeddings, "is this the same person?" becomes:

```
similarity = cosine(probe_embedding, stored_embedding)
accepted   = similarity >= threshold
```

That is arithmetic over 512 floats. It runs in microseconds, in C#, with no network call.

So the real decision is not *database vs AWS*. It is **who runs the model**.

## The two honest options

### Option A - self-hosted model, your own database (the default here)

| | |
|---|---|
| Detection | SCRFD, ONNX, ~5MB |
| Recognition | ArcFace, ONNX, ~250MB, outputs 512 floats |
| Storage | a 2048-byte `varbinary` column next to the person row |
| Comparison | cosine similarity, in-process |
| Cost | none per call; one-off CPU |
| Latency | roughly 100-250ms per frame on a modern CPU core |
| Data residency | face images and embeddings never leave your server |

Run the two models with `Microsoft.ML.OnnxRuntime` and your SQL Server holds everything. For an
attendance system this is almost always the right answer: the cost is fixed, the data stays put,
and it keeps working when the internet does not - which matters when the thing is bolted to a
wall next to a factory door.

### Option B - AWS Rekognition

| | |
|---|---|
| Detection + recognition | managed by AWS |
| Storage | an opaque face id in your database, the face itself in an AWS collection |
| Comparison | `SearchFacesByImage`, one API call per punch |
| Cost | roughly 1 USD per 1,000 calls, plus storage per face |
| Latency | a network round trip, plus AWS processing |
| Data residency | every face image is sent to AWS |

Worth it if you have no appetite for owning a model, want someone else accuracy guarantees, and
are comfortable with a third party processing biometric data. Three consequences are easy to miss:

1. **Templates are not portable.** You get a face id, not a vector. You cannot compare two
   enrolments offline, so the duplicate-registration check and the pose-consistency check cannot
   run under this provider. They are no-ops by design, not by oversight.
2. **Deleting a row is not deleting the face.** The indexed face stays in the collection -
   billable, unreachable, and invisible to an erasure request - unless you call `DeleteFaces`.
   `PurgeBiometricsAsync` does this; a manual `DELETE` in SQL will not.
3. **Per-call cost scales with traffic, not headcount.** 300 employees punching twice a day is
   about 18,000 calls a month. Cheap. A kiosk that submits a frame every second while someone
   stands in front of it is a different bill entirely - which is why the attendance endpoint is
   rate limited.

### What about a vector database?

Not needed at this scale, and worth being specific about why. A 1:N search is one cosine per
stored template:

| Enrolled people | Templates (3 each) | Comparisons | Time |
|---|---|---|---|
| 100 | 300 | 300 | sub-millisecond |
| 1,000 | 3,000 | 3,000 | about 1ms |
| 10,000 | 30,000 | 30,000 | about 10ms |
| 100,000 | 300,000 | 300,000 | about 100ms |

Under roughly 10,000 people the comparison is free next to the ~150ms the model already spent.
Reach for pgvector, Qdrant or a Rekognition collection somewhere past that, or when 1:N latency
starts showing up in your traces - not before.

## How this repository keeps the choice reversible

Everything recognition-related sits behind one interface, `IFaceRecognitionProvider`:

```
AnalyzeAsync         -> detect + score a frame (drives capture-screen feedback)
CreateTemplateAsync  -> frame in, storable template out
MatchAsync           -> probe + candidates -> scored result
CompareTemplates     -> two stored templates (null when the provider cannot)
DeleteTemplatesAsync -> release provider-side state
```

Three implementations ship:

- `OnnxFaceRecognitionProvider` - self-hosted, the default
- `RekognitionFaceRecognitionProvider` - AWS
- `DevelopmentFaceRecognitionProvider` - a deterministic fake for building the front end

Switching is one config value. Nothing in the controllers, the services or the schema changes.

### The catch you must plan for

Embeddings from different models are **not comparable**. Swapping the recognition model, or even
a model version, invalidates every stored template.

That is why `ProviderId` is stored on every `FaceTemplate` row, and why `EnrollmentStatus` has a
`Stale` value. Change the model and enrolments become `Stale` rather than silently scoring near
zero and rejecting your entire workforce on a Monday morning. Everyone has to re-register. Decide
your provider before you enrol 500 people, not after.

## Where the images go

Images and embeddings are separate concerns, stored separately:

- **Embeddings** live in SQL Server, inline on the template row. Small, fixed-size, and read on
  every verification.
- **Images** live in a blob store behind `IFaceImageStore` - the local filesystem by default, S3
  if you prefer. Large, and read almost never.

Putting megabytes of JPEG in a relational table bloats backups and slows the candidate scan for
no benefit. Note also that the local filesystem store is *not* shared state: the moment you run
more than one API instance, move to S3 or Azure Blob.

Keeping the registration images at all is a deliberate choice. You need them to regenerate
embeddings after a model upgrade - without them, a model change means photographing everybody
again. The cost is that you are storing identifiable photographs, which is a data-protection
obligation rather than a technical one.

## Accuracy is a threshold problem, not a vendor problem

The numbers that decide who gets in live in `appsettings.json`, not in the model:

```jsonc
"VerificationThreshold":   0.42,  // 1:1 - the device already claims an identity
"IdentificationThreshold": 0.52,  // 1:N - stricter, because the search space is larger
"IdentificationMargin":    0.06   // refuse a 1:N win that barely beats the runner-up
```

Two points are worth internalising before go-live.

**1:1 beats 1:N, and not by a little.** If the device can learn who the person claims to be - a
badge tap, a PIN, a name on a tablet - the face only has to *confirm* it. Error rates stay flat
as you add employees. With 1:N, every extra enrolled person is another chance to be wrong, so the
false-accept rate climbs with headcount and the threshold has to climb with it. Both modes are
implemented; prefer 1:1 wherever the workflow allows it.

**These defaults are a starting point, not a calibration.** Thresholds depend on your cameras,
your lighting and your population. Use `POST /api/v1/attendance/preview`, which scores a frame
without recording anything, to gather real distributions from your own site, then put the
threshold where your tolerance actually sits. For attendance, a false accept (somebody punching in
for a colleague) is usually worse than a false reject (somebody trying again).

## Why three poses, and what they do not buy you

Three angles give the matcher a better chance when the live frame is not straight-on: someone
glancing sideways at a wall-mounted camera scores far better against a profile template than
against a frontal one. A probe is compared against all three and the best score wins.

What three poses do **not** give you is spoof resistance. See the gaps below.

## What this system deliberately does not do

Being clear about the gaps is more useful than implying there are none.

- **No liveness detection.** A printed photograph or a phone screen held up to the camera will
  pass. This is the single biggest weakness of any face-based attendance system, and three-pose
  enrolment does nothing to fix it - it improves matching across angles, not spoof resistance. If
  attendance drives payroll, you need a liveness model, a depth or infrared camera, or a
  supervised kiosk. Treat this as a known, accepted risk or close it before go-live.
- **No multi-instance image store by default.** The local store assumes one server.
- **No per-user authentication.** API keys separate admin from device. Put a real identity
  provider in front of anything an end user touches.
- **Pose is estimated from landmark geometry**, not a 6DoF model. Good to roughly plus or minus
  10 degrees - enough to sort a capture into front/left/right, not enough to trust finer numbers.

## Legal footing, briefly

A face template is biometric data under GDPR Article 9, the Indian DPDP Act, Illinois BIPA and a
growing list of others. Common obligations: explicit consent before enrolment, a stated retention
period, erasure on request, and encryption at rest. The mechanisms are here -
`PurgeBiometricsAsync` for erasure, `ProbeImageRetentionDays` plus a background sweep for
retention, SSE-KMS for S3 - but the policy decisions are yours, and in several jurisdictions
written consent is not optional. This is not legal advice; get some before deploying.
