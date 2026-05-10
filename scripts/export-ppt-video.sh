#!/usr/bin/env bash
set -euo pipefail
current_step="initializing"
log=""
on_error() {
  local code="$?"
  echo "PPT video export failed at step: ${current_step} (exit ${code})." >&2
  if [[ -n "${log:-}" && -f "$log" ]]; then
    echo "Recent diagnostic log:" >&2
    tail -n 120 "$log" >&2 || true
  fi
}
trap on_error ERR

usage() {
  cat <<'EOF'
Usage:
  export-ppt-video <deck.pptx> [output-dir] [seconds-per-slide] [voice] [speed] [resolution] [bgm] [bgm-volume]

Requires: libreoffice, pdftoppm, ffmpeg, ffprobe, espeak-ng, python3.
This does not call any AI service. It renders PPT slides, reads speaker notes,
uses local espeak-ng TTS for narration, and combines everything into an MP4.
If a slide has no notes, it becomes a silent segment.
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" || $# -lt 1 ]]; then
  usage
  exit 0
fi

pptx="$(realpath "$1")"
out_dir="${2:-}"
seconds_per_slide="${3:-5}"
voice="${4:-zh}"
speed="${5:-1.0}"
resolution="${6:-720p}"
bgm="${7:-}"
bgm_volume="${8:-30}"

if [[ ! -f "$pptx" ]]; then
  echo "PPTX not found: $pptx" >&2
  exit 1
fi

for bin in libreoffice pdftoppm ffmpeg ffprobe espeak-ng python3; do
  if ! command -v "$bin" >/dev/null 2>&1; then
    echo "Missing dependency: $bin" >&2
    exit 1
  fi
done

base="$(basename "$pptx" .pptx)"
if [[ -z "$out_dir" ]]; then
  out_dir="$(dirname "$pptx")/${base}-video"
fi
mkdir -p "$out_dir"/{slides,audio,segments,tmp}
log="$out_dir/tmp/export.log"
: > "$log"
run_logged() {
  local label="$1"
  shift
  current_step="$label"
  echo "[$(date '+%Y-%m-%d %H:%M:%S')] $label" >>"$log"
  if ! "$@" >>"$log" 2>&1; then
    echo "$label failed. Details:" >&2
    tail -n 120 "$log" >&2 || true
    exit 1
  fi
}

case "$speed" in
  0.75*) speech_wpm="130" ;;
  1.25*) speech_wpm="200" ;;
  1.5*) speech_wpm="240" ;;
  *) speech_wpm="165" ;;
esac

case "$resolution" in
  1080p*) video_size="1920:1080" ;;
  480p*) video_size="854:480" ;;
  *) video_size="1280:720" ;;
esac

notes_json="$out_dir/tmp/notes.json"
current_step="Reading PPT speaker notes"
python3 - "$pptx" "$notes_json" <<'PY'
import json
import posixpath
import sys
import zipfile
import xml.etree.ElementTree as ET

pptx, out = sys.argv[1], sys.argv[2]
ns = {
    "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
    "p": "http://schemas.openxmlformats.org/presentationml/2006/main",
    "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
    "rel": "http://schemas.openxmlformats.org/package/2006/relationships",
}

def normalize_part(base, target):
    if target.startswith("/"):
        return target.lstrip("/")
    return posixpath.normpath(posixpath.join(posixpath.dirname(base), target))

def rels_path(part):
    return posixpath.join(posixpath.dirname(part), "_rels", posixpath.basename(part) + ".rels")

def read_rels(z, part):
    rels = {}
    path = rels_path(part)
    if path not in z.namelist():
        return rels
    root = ET.fromstring(z.read(path))
    for item in root.findall("rel:Relationship", ns):
        rid = item.attrib.get("Id")
        target = item.attrib.get("Target")
        rel_type = item.attrib.get("Type", "")
        if rid and target:
            rels[rid] = (normalize_part(part, target), rel_type)
    return rels

def read_text(z, part):
    if part not in z.namelist():
        return ""
    root = ET.fromstring(z.read(part))
    return "\n".join(t.text or "" for t in root.findall(".//a:t", ns)).strip()

notes = {}
with zipfile.ZipFile(pptx) as z:
    presentation = "ppt/presentation.xml"
    presentation_rels = read_rels(z, presentation)
    ordered_slide_parts = []
    if presentation in z.namelist():
        root = ET.fromstring(z.read(presentation))
        for slide_id in root.findall(".//p:sldIdLst/p:sldId", ns):
            rid = slide_id.attrib.get("{" + ns["r"] + "}id")
            if rid and rid in presentation_rels:
                ordered_slide_parts.append(presentation_rels[rid][0])

    for slide_no, slide_part in enumerate(ordered_slide_parts, start=1):
        slide_rels = read_rels(z, slide_part)
        note_part = ""
        for target, rel_type in slide_rels.values():
            if rel_type.endswith("/notesSlide"):
                note_part = target
                break
        notes[str(slide_no)] = read_text(z, note_part) if note_part else ""

with open(out, "w", encoding="utf-8") as f:
    json.dump(notes, f, ensure_ascii=False)
PY

rm -f "$out_dir"/tmp/*.pdf
run_logged "LibreOffice PDF export" libreoffice --headless --nologo --nofirststartwizard --convert-to pdf --outdir "$out_dir/tmp" "$pptx"
pdf="$(find "$out_dir/tmp" -maxdepth 1 -type f -iname '*.pdf' | head -n 1)"
if [[ ! -f "$pdf" ]]; then
  echo "LibreOffice did not produce a PDF in: $out_dir/tmp" >&2
  tail -n 80 "$log" >&2 || true
  exit 1
fi

run_logged "PDF slide rendering" pdftoppm -png -r 180 "$pdf" "$out_dir/slides/slide"

segment_list="$out_dir/segments.txt"
: > "$segment_list"

slide_count=0
for image in "$out_dir"/slides/slide-*.png; do
  [[ -e "$image" ]] || continue
  slide_count=$((slide_count + 1))
  index="$(printf "%03d" "$slide_count")"
  normalized_image="$out_dir/slides/slide-${index}.png"
  mv "$image" "$normalized_image"

  note_file="$out_dir/tmp/note-${index}.txt"
  current_step="Preparing notes for slide ${slide_count}"
  python3 - "$notes_json" "$slide_count" "$note_file" <<'PY'
import json
import sys

notes_path, slide_no, out = sys.argv[1], sys.argv[2], sys.argv[3]
with open(notes_path, "r", encoding="utf-8") as f:
    notes = json.load(f)
text = notes.get(slide_no, "").strip()
with open(out, "w", encoding="utf-8") as f:
    f.write(text)
PY

  audio="$out_dir/audio/slide-${index}.wav"
  segment="$out_dir/segments/segment-${index}.mp4"
  if [[ -s "$note_file" ]]; then
    current_step="Generating narration for slide ${slide_count}"
    if ! espeak-ng -v "$voice" -s "$speech_wpm" -f "$note_file" -w "$audio" >>"$log" 2>&1; then
      espeak-ng -s "$speech_wpm" -f "$note_file" -w "$audio" >>"$log" 2>&1 || true
    fi
    duration="$(ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$audio" || true)"
    if [[ -z "$duration" || ! -s "$audio" ]]; then
      duration="$seconds_per_slide"
      run_logged "Silent segment rendering" ffmpeg -y -loop 1 -framerate 30 -i "$normalized_image" -t "$duration" \
        -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 \
        -vf "scale=${video_size}:force_original_aspect_ratio=decrease,pad=${video_size}:(ow-iw)/2:(oh-ih)/2" \
        -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$segment"
    else
      run_logged "Narrated segment rendering" ffmpeg -y -loop 1 -framerate 30 -i "$normalized_image" -i "$audio" -t "$duration" -vf "scale=${video_size}:force_original_aspect_ratio=decrease,pad=${video_size}:(ow-iw)/2:(oh-ih)/2" \
        -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$segment"
    fi
  else
    run_logged "Silent segment rendering" ffmpeg -y -loop 1 -framerate 30 -i "$normalized_image" -t "$seconds_per_slide" \
      -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 \
      -vf "scale=${video_size}:force_original_aspect_ratio=decrease,pad=${video_size}:(ow-iw)/2:(oh-ih)/2" \
      -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$segment"
  fi
  printf "file '%s'\n" "$segment" >> "$segment_list"
done

if [[ "$slide_count" -eq 0 ]]; then
  echo "No slides rendered." >&2
  exit 1
fi

output="$out_dir/${base}.mp4"
merged="$out_dir/tmp/${base}-merged.mp4"
run_logged "Video merging" ffmpeg -y -f concat -safe 0 -i "$segment_list" \
  -c:v libx264 -pix_fmt yuv420p -c:a aac -ar 44100 -ac 2 \
  "$merged"

if [[ -n "$bgm" && -f "$bgm" ]]; then
  bgm_gain="$(python3 - "$bgm_volume" <<'PY'
import sys
try:
    value = max(0, min(100, int(float(sys.argv[1]))))
except Exception:
    value = 30
print(value / 100)
PY
)"
  run_logged "BGM mixing" ffmpeg -y -i "$merged" -stream_loop -1 -i "$bgm" \
    -filter_complex "[1:a]volume=${bgm_gain}[bgm];[0:a][bgm]amix=inputs=2:duration=first:dropout_transition=2[a]" \
    -map 0:v -map "[a]" -c:v copy -c:a aac -ar 44100 -ac 2 "$output"
else
  mv "$merged" "$output"
fi
echo "$output"
