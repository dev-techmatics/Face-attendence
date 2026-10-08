#!/usr/bin/env bash
# Downloads the two ONNX models the self-hosted face recognition provider needs.
#
#   1. SCRFD   - face detection plus five landmarks. The landmarks are required: without them
#                a face cannot be aligned, and an unaligned crop costs far more accuracy than
#                any amount of threshold tuning can recover. Use a variant with "kps".
#   2. ArcFace - aligned 112x112 crop in, 512-float embedding out.
#
# Both files are large and must not be committed; .gitignore excludes models/*.onnx.
set -euo pipefail

OUT_DIR="${1:-$(dirname "$0")/../src/Api/models}"
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

echo "Target directory: $OUT_DIR"

fetch() {
  local name="$1" url="$2" role="$3"
  local dest="$OUT_DIR/$name"

  if [ -f "$dest" ]; then
    echo "  [skip] $name already present ($(du -h "$dest" | cut -f1))"
    return 0
  fi

  echo "  [get ] $name - $role"
  if curl -fL --progress-bar -o "$dest.part" "$url"; then
    mv "$dest.part" "$dest"
    echo "  [ok  ] $name ($(du -h "$dest" | cut -f1))"
  else
    rm -f "$dest.part"
    echo "  [fail] $name - download it by hand and save it as $dest" >&2
    echo "         Any SCRFD build with landmarks, and any ArcFace model taking 112x112 and" >&2
    echo "         emitting 512 floats, will work. Update FaceRecognition:Onnx paths to match." >&2
  fi
}

fetch "scrfd_500m_bnkps.onnx" \
  "https://github.com/deepinsight/insightface/releases/download/v0.7/scrfd_500m_bnkps.onnx" \
  "face detection + 5 landmarks"

fetch "arcface_r100.onnx" \
  "https://github.com/onnx/models/raw/main/validated/vision/body_analysis/arcface/model/arcfaceresnet100-11.onnx" \
  "face recognition (512-d embedding)"

cat <<'EOF'

Next steps:
  1. Set FaceRecognition:Provider to "onnx" in appsettings.Development.json
  2. dotnet run --project src/Api
  3. GET /api/v1/diagnostics/provider to confirm the models loaded

Re-enrol everyone after changing a model file: embeddings from different models are not
comparable, and the ProviderId change will mark existing rows Stale.
EOF
