# Face Attendance API — Frontend Guide

The four endpoints a frontend app needs, with exact URLs, headers and fields. Every example below
was run against a live local instance.

**Base URL (local):** `http://localhost:5080`

---

## Headers

| Header | Required | Value | Notes |
|---|---|---|---|
| `X-Api-Key` | **Yes** | `dev-admin-key` or `dev-device-key` | Which key depends on the endpoint — see each one below |
| `X-Tenant-Id` | No | e.g. `default` | Only for multi-site setups. Leave it out otherwise |
| `Content-Type` | — | — | **Do not set it yourself** for file uploads. The browser / Postman adds `multipart/form-data` with the boundary automatically; setting it by hand breaks the upload |

**Two keys, two jobs:**

| Key | Use it for |
|---|---|
| `dev-admin-key` | Registration screen, reports |
| `dev-device-key` | Attendance kiosk / mobile app. It can **only** mark attendance |

These are local development keys. Production keys will be different.

---

## 1. Register a person (all 3 photos in one call)

```
POST http://localhost:5080/api/v1/registrations
```

**Header:** `X-Api-Key: dev-admin-key`
**Body:** `form-data`

| Field | Type | Required | Rules |
|---|---|---|---|
| `externalId` | text | **Yes** | Your employee / student id. Max 64 chars. Must be unique |
| `fullName` | text | **Yes** | Max 200 chars |
| `email` | text | No | Valid email |
| `department` | text | No | Max 128 chars |
| `front` | **file** | **Yes** | Face looking straight at the camera |
| `left` | **file** | **Yes** | Head turned to the person's left |
| `right` | **file** | **Yes** | Head turned to the person's right |

**Image rules:** JPEG, PNG or WebP · max 8 MB each · at least 240 × 240 px · exactly one face in frame.

**Success — `201 Created`**

```json
{
  "person": {
    "id": "01a11f0f-3a1a-7563-9db6-9643dd9f947d",
    "externalId": "EMP-1001",
    "fullName": "Alice Kumar",
    "department": "Operations",
    "status": "Active",
    "enrollmentStatus": "Enrolled",
    "enrolledAtUtc": "2026-10-09T05:07:35.040828+00:00",
    "createdAtUtc": "2026-10-09T05:07:34.0425547+00:00"
  },
  "captures": [ "...one entry per photo..." ],
  "enrollmentStatus": "Enrolled",
  "enrollmentCompleted": true
}
```

**Save `person.id`** — the status endpoint below needs it.

All-or-nothing: if any of the three photos is rejected, the person is **not** created, so the
same `externalId` can simply be retried.

---

## 2. Check registration status

```
GET http://localhost:5080/api/v1/persons/{personId}/enrollment
```

**Header:** `X-Api-Key: dev-admin-key`
**Body:** none

**Success — `200 OK`**

```json
{
  "personId": "01a11f0f-3a1a-7563-9db6-9643dd9f947d",
  "externalId": "EMP-1001",
  "fullName": "Alice Kumar",
  "status": "Active",
  "enrollmentStatus": "Enrolled",
  "poses": [
    { "pose": "Front", "version": 1, "qualityScore": 0.9 },
    { "pose": "Left",  "version": 1, "qualityScore": 0.9 },
    { "pose": "Right", "version": 1, "qualityScore": 0.9 }
  ],
  "missingPoses": []
}
```

| `enrollmentStatus` | Meaning |
|---|---|
| `Enrolled` | Ready to mark attendance |
| `InProgress` | Some photos missing — see `missingPoses` |
| `NotEnrolled` | No photos yet |
| `Stale` | Face engine changed — the person must register again |

---

## 3. Mark attendance

```
POST http://localhost:5080/api/v1/attendance/mark
```

**Header:** `X-Api-Key: dev-device-key`
**Body:** `form-data`

| Field | Type | Required | Notes |
|---|---|---|---|
| `image` | **file** | **Yes** | Live photo from the camera |
| `externalId` | text | Recommended | Who is claiming to be there (badge / PIN / name picker). **Much more accurate** than leaving it out |
| `personId` | text | No | Alternative to `externalId` (the GUID) |
| `deviceId` | text | No | e.g. `GATE-A`. Shows up in reports |
| `direction` | text | No | `In` or `Out`. Leave empty and the server decides from the last punch |
| `latitude` / `longitude` | number | No | Device location |

If neither `externalId` nor `personId` is sent, the server searches **all** registered people for
the face. That works, but is less accurate as the number of people grows.

**Response — always `200 OK`.** Even a face that does not match returns 200.
**Check `success` and `decision`, not the HTTP status.**

```json
{
  "success": true,
  "decision": "Accepted",
  "eventId": "01a11f0f-5d8d-7f6a-96ec-a0b86e916537",
  "personId": "01a11f0f-3a1a-7563-9db6-9643dd9f947d",
  "externalId": "EMP-1001",
  "fullName": "Alice Kumar",
  "direction": "In",
  "score": 1,
  "threshold": 0.42,
  "matchedPose": "Front",
  "recordedAtUtc": "2026-10-09T05:07:43.1177823+00:00",
  "processingMs": 171
}
```

| `decision` | `success` | What to show the user |
|---|---|---|
| `Accepted` | `true` | "Welcome, {fullName} — marked {direction}" |
| `Duplicate` | `false` | "Already marked a moment ago" (repeat within 2 minutes) |
| `Rejected` | `false` | "Face not recognised, please try again" |
| `NotEnrolled` | `false` | "You are not registered yet" |
| `NoFaceDetected` | `false` | "Please face the camera" |
| `LowQuality` | `false` | "Too dark or blurry, please try again" |
| `ProviderError` | `false` | "Something went wrong — contact admin" |

When `success` is `false`, the field `message` explains why. Do **not** show `score` or
`threshold` to end users.

Rate limit: 60 calls per minute per device. Beyond that you get `429 Too Many Requests`.

---

## 4. Attendance list / report

```
GET http://localhost:5080/api/v1/attendance
```

**Header:** `X-Api-Key: dev-admin-key`
**Body:** none

| Query parameter | Required | Example | Notes |
|---|---|---|---|
| `personId` | No | `01a11f0f-...` | One person only |
| `fromUtc` | No | `2026-10-09T00:00:00Z` | Start (inclusive) |
| `toUtc` | No | `2026-10-10T00:00:00Z` | End (exclusive) |
| `decision` | No | `Accepted` | Use `Accepted` for the real register |
| `page` | No | `1` | Default 1 |
| `pageSize` | No | `50` | Default 50, max 500 |

Example — today's successful punches:

```
GET http://localhost:5080/api/v1/attendance?decision=Accepted&fromUtc=2026-10-09T00:00:00Z&toUtc=2026-10-10T00:00:00Z
```

**Success — `200 OK`**

```json
{
  "items": [
    {
      "id": "01a11f0f-5d8d-7f6a-96ec-a0b86e916537",
      "externalId": "EMP-1001",
      "fullName": "Alice Kumar",
      "direction": "In",
      "decision": "Accepted",
      "deviceId": "GATE-A",
      "createdAtUtc": "2026-10-09T05:07:43.1177823+00:00"
    }
  ],
  "page": 1,
  "pageSize": 50,
  "total": 1,
  "totalPages": 1
}
```

Without the `decision` filter the list also contains failed attempts — useful when someone says
"the machine didn't recognise me".

---

## Errors

Errors come in two shapes.

**Missing or invalid fields → `400`** with an `errors` object:

```json
{
  "status": 400,
  "title": "One or more validation errors occurred.",
  "errors": { "Left": ["The Left field is required."] }
}
```

**Image or business problems → `4xx`** with a `code`. Check `code` in your app, not the text:

```json
{
  "status": 422,
  "detail": "No face was found in the frame.",
  "code": "no_face_detected",
  "hints": ["Fill roughly a third of the frame with the face."]
}
```

| Status | `code` | Meaning |
|---|---|---|
| 401 | — | `X-Api-Key` missing or wrong |
| 403 | — | Right key, wrong role (e.g. device key on registration) |
| 404 | `person_not_found` | No such person |
| 409 | `external_id_exists` | That employee id is already registered |
| 409 | `duplicate_identity` | This **face** is already registered under another id |
| 409 | `duplicate_frame` | Same photo used for two angles |
| 413 | `image_too_large` | Image over 8 MB |
| 415 | `unsupported_format` | Not a real JPEG / PNG / WebP |
| 422 | `no_face_detected` | No face found |
| 422 | `multiple_faces_detected` | More than one person in the photo |
| 422 | `low_quality` | Blurry, dark, or face too small — see `hints` |
| 422 | `pose_mismatch` | Head not turned the way that photo requires |
| 429 | — | Too many requests from this device |

---

## Test it in 2 minutes

Start the API (`F5` in VS Code, or `dotnet run --project src/Api`), then from the
`docs/samples` folder:

```bash
# 1. Register
curl -X POST http://localhost:5080/api/v1/registrations -H "X-Api-Key: dev-admin-key" -F "externalId=EMP-1001" -F "fullName=Alice Kumar" -F "front=@alice-front.jpg" -F "left=@alice-left.jpg" -F "right=@alice-right.jpg"

# 2. Status (replace {personId} with person.id from step 1)
curl http://localhost:5080/api/v1/persons/{personId}/enrollment -H "X-Api-Key: dev-admin-key"

# 3. Mark attendance
curl -X POST http://localhost:5080/api/v1/attendance/mark -H "X-Api-Key: dev-device-key" -F "externalId=EMP-1001" -F "deviceId=GATE-A" -F "image=@alice-front.jpg"

# 4. List
curl "http://localhost:5080/api/v1/attendance?pageSize=10" -H "X-Api-Key: dev-admin-key"
```

**In Postman:** Body → **form-data**. For `front`, `left`, `right` and `image`, hover the key
column and change the type from **Text** to **File**. A ready-made collection is in
[FaceAttendance.postman_collection.json](FaceAttendance.postman_collection.json).

---

## Important while testing locally

The local setup runs a **test face engine that matches by file name, not by face.**
`alice-front.jpg`, `alice-left.jpg` and `alice-right.jpg` count as the same person;
`bob-front.jpg` does not. The sample images in `docs/samples` are placeholders, not real faces.

So locally you can test the **whole flow** — requests, responses, errors — but not real face
matching. Real recognition is switched on with `FaceRecognition:Provider = "onnx"` after
downloading the models; the endpoints, fields and responses stay exactly the same.

Interactive docs for every endpoint: `http://localhost:5080/scalar/v1`
