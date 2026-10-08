# Front-end integration

How to capture the three registration angles in a browser or mobile app and talk to this API.

## The capture flow

Registration is a three-step wizard. The API is designed so each step can give immediate
feedback rather than failing at the end:

```
1. POST /api/v1/persons                  -> personId
2. For each pose in [Front, Left, Right]:
     a. show the prompt and a live preview
     b. optionally POST /api/v1/diagnostics/analyze to coach the user
     c. POST /api/v1/persons/{personId}/faces   (pose + image)
     d. read enrollmentStatus / missingPoses from the response
3. When enrollmentStatus === "Enrolled", registration is done
```

If you would rather capture all three frames locally and submit once,
`POST /api/v1/registrations` creates the person and stores all three in one call, and rolls the
person back if any image is rejected.

## Capturing a frame in the browser

```js
const stream = await navigator.mediaDevices.getUserMedia({
  video: { width: 1280, height: 720, facingMode: 'user' }
});
video.srcObject = stream;

function grabFrame(video) {
  const canvas = document.createElement('canvas');
  canvas.width = video.videoWidth;
  canvas.height = video.videoHeight;
  canvas.getContext('2d').drawImage(video, 0, 0);

  // Quality 0.85 at 720p is ample. Recognition gains nothing from a 4MB frame, and the
  // API rejects anything over MaxImageBytes anyway.
  return new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.85));
}
```

### The mirroring trap

A front-facing camera preview is usually **mirrored**. If you draw the mirrored preview to the
canvas, left and right are swapped, so every "turn left" capture arrives looking like a right
turn and the pose check rejects it.

Two ways out, and you must pick one deliberately:

- Mirror only the CSS preview (`transform: scaleX(-1)`) and draw the **unmirrored** video to the
  canvas. This is the better option: the stored image then matches reality.
- Or send the mirrored frame and set `FaceRecognition:Onnx:MirroredInput = true` on the server.

Do not do both. Doing neither, or both, inverts the pose check.

## Uploading one pose

```js
async function capturePose(personId, pose, blob) {
  const form = new FormData();
  form.append('pose', pose);                                 // "Front" | "Left" | "Right"
  form.append('image', blob, `${pose.toLowerCase()}.jpg`);

  const response = await fetch(`${API}/api/v1/persons/${personId}/faces`, {
    method: 'POST',
    headers: { 'X-Api-Key': apiKey },
    body: form
  });

  const payload = await response.json();

  if (!response.ok) {
    // RFC 9457 problem document: branch on `code`, never on `detail`.
    throw Object.assign(new Error(payload.detail), {
      code: payload.code,
      hints: payload.hints ?? []
    });
  }

  return payload;   // { enrollmentStatus, capturedPoses, missingPoses, analysis, ... }
}
```

Do not set `Content-Type` yourself. The browser adds the multipart boundary; overriding it breaks
the upload.

## Turning rejections into useful prompts

Every failure carries a stable `code`, and often `hints`. Map codes to your own copy rather than
showing server text verbatim:

```js
const MESSAGES = {
  no_face_detected:        'We cannot see a face. Move closer and face the camera.',
  multiple_faces_detected: 'More than one person is in frame. Please stand alone.',
  low_quality:             'The photo is too blurry or too dark. Hold still and try again.',
  pose_mismatch:           'Please turn your head further in the direction shown.',
  image_too_small:         'The camera resolution is too low for registration.',
  duplicate_frame:         'That is the same photo as another angle. Take a fresh one.',
  duplicate_identity:      'This face is already registered. Please speak to an administrator.',
  unsupported_format:      'That file is not a supported image.',
  external_id_exists:      'That employee id is already registered.'
};

function toMessage(error) {
  return MESSAGES[error.code] ?? error.message;
}
```

`duplicate_identity` deserves its own screen rather than a toast: it means somebody is registering
a face that already belongs to another record, which is a situation for a human.

## Live coaching before the capture

`POST /api/v1/diagnostics/analyze` scores a frame without storing anything, so you can keep a
"ready" indicator on screen. Throttle it - each call runs a real inference.

```js
async function readiness(blob) {
  const form = new FormData();
  form.append('image', blob, 'frame.jpg');

  const r = await fetch(`${API}/api/v1/diagnostics/analyze`, {
    method: 'POST', headers: { 'X-Api-Key': apiKey }, body: form
  });

  if (!r.ok) return { ready: false, reason: (await r.json()).code };

  const a = await r.json();
  return {
    ready: a.faceDetected && a.faceCount === 1 && a.qualityScore >= 0.5,
    quality: a.qualityScore,
    yaw: a.yawDegrees,          // drives a "turn further" arrow
    inferredPose: a.inferredPose,
    warnings: a.warnings
  };
}
```

Call it at most once or twice a second, and only while the user is actually positioned.

## The attendance screen

```js
async function markAttendance(blob, { externalId, deviceId }) {
  const form = new FormData();
  form.append('image', blob, 'frame.jpg');
  if (externalId) form.append('externalId', externalId);   // makes this a 1:1 check
  if (deviceId) form.append('deviceId', deviceId);

  const r = await fetch(`${API}/api/v1/attendance/mark`, {
    method: 'POST',
    headers: { 'X-Api-Key': deviceKey, 'X-Device-Id': deviceId },
    body: form
  });

  return r.json();   // 200 even when it did not match - check `success` and `decision`
}
```

Two things to get right here.

**Send the identity when you have it.** A badge scan, a PIN pad or a name picker turns this into a
1:1 verification, whose accuracy does not degrade as the workforce grows. Pure 1:N is convenient,
but its error rate climbs with headcount.

**A non-match is a 200.** Switch on `decision`, not on HTTP status:

```js
switch (result.decision) {
  case 'Accepted':       return show(`${result.fullName} - ${result.direction}`);
  case 'Duplicate':      return show('Already recorded a moment ago.');
  case 'Rejected':       return show('Not recognised. Please try again.');
  case 'NotEnrolled':    return show('You are not registered yet.');
  case 'NoFaceDetected': return show('Hold still and face the camera.');
  case 'LowQuality':     return show('Too dark or too blurry.');
  default:               return show('Something went wrong. Call an administrator.');
}
```

Do not show the user their score or the threshold. It tells anyone probing the system exactly how
close they got.

## Keys, and where they must not be

The two roles exist to limit the damage when a device is compromised:

- **Admin key** - enrolment, stored images, reports. Belongs on a server you control.
- **Device key** - `POST /attendance/mark` and nothing else.

**Never put an admin key in a browser bundle or a mobile app.** Anything shipped to a device is
readable. For a web registration console, proxy through your own backend and keep the admin key
server-side. A device key in a kiosk app is an accepted risk - it can only mark attendance, which
is exactly why the roles are split this way.

## React Native

`react-native-vision-camera` plus a `FormData` upload works the same way:

```js
const photo = await camera.current.takePhoto({ qualityPrioritization: 'balanced' });

const form = new FormData();
form.append('image', { uri: `file://${photo.path}`, type: 'image/jpeg', name: 'front.jpg' });
form.append('pose', 'Front');
```

Watch two platform details: front-camera mirroring, as above, and EXIF rotation. If frames arrive
rotated the detector will usually still find the face, but the pose estimate will be wrong - apply
or strip the EXIF orientation before uploading.

## Rate limiting

`/attendance/mark` allows 60 requests per minute, partitioned by `X-Device-Id` and falling back to
the client IP. Send a stable device id so one busy kiosk cannot consume the budget for every other
door. On a 429, back off rather than retrying immediately.

## Suggested UX for the three-pose wizard

What tends to work, from the shape of the API:

1. **One prompt at a time.** "Look straight at the camera", then "Turn your head slowly to your
   left", then right. Showing all three at once produces three frontal shots.
2. **Capture on a tap, not continuously.** A deliberate tap gives a sharper frame than grabbing
   whatever is on screen, and `low_quality` rejections drop sharply.
3. **Show progress from the server, not local state.** `capturedPoses` and `missingPoses` come
   back on every upload, so a refresh or a crash mid-wizard resumes correctly.
4. **Allow a retake per pose.** Re-posting supersedes the previous capture, so this needs no
   special handling.
5. **Do not block on warnings.** `warnings` is advisory; the capture was accepted. Only `hints` on
   an error response mean the user must redo something.
