# Sample images

Nine synthetic placeholder images, three "people" by three poses. They exist so
`docs/api-examples.http` and `scripts/smoke-test.ps1` run out of the box.

**These are not faces.** They are coloured ovals with a filename caption. They work only with
the **development** provider, which derives identity from the file name up to the first dash -
so `alice-front.jpg`, `alice-left.jpg` and `alice-right.jpg` enrol as one person, and
`bob-*.jpg` will not match them.

Under the `onnx` or `aws` provider every one of these is rejected with `no_face_detected`, which
is the correct behaviour. To exercise a real provider, use real photographs and name them to the
same pattern:

```
<name>-front.jpg
<name>-left.jpg
<name>-right.jpg
```

Do not commit real photographs of real people. `.gitignore` does not cover this folder, because
these placeholders are meant to be in version control - which is exactly why real faces must not
be dropped in beside them.
