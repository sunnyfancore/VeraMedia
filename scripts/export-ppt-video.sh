#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  export-ppt-video <deck.pptx> [output-dir] [seconds-per-slide] [voice]

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

notes_json="$out_dir/tmp/notes.json"
python3 - "$pptx" "$notes_json" <<'PY'
import json
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

pptx, out = sys.argv[1], sys.argv[2]
ns = {"a": "http://schemas.openxmlformats.org/drawingml/2006/main"}

def idx(name):
    match = re.search(r"notesSlides/notesSlide(\d+)\.xml$", name)
    return int(match.group(1)) if match else 0

notes = {}
with zipfile.ZipFile(pptx) as z:
    names = sorted(
        (n for n in z.namelist() if n.startswith("ppt/notesSlides/notesSlide") and n.endswith(".xml")),
        key=idx,
    )
    for name in names:
        root = ET.fromstring(z.read(name))
        text = "\n".join(t.text or "" for t in root.findall(".//a:t", ns)).strip()
        notes[str(idx(name))] = text

with open(out, "w", encoding="utf-8") as f:
    json.dump(notes, f, ensure_ascii=False)
PY

libreoffice --headless --convert-to pdf --outdir "$out_dir/tmp" "$pptx" >/dev/null
pdf="$out_dir/tmp/${base}.pdf"
if [[ ! -f "$pdf" ]]; then
  echo "LibreOffice did not produce PDF: $pdf" >&2
  exit 1
fi

pdftoppm -png -r 180 "$pdf" "$out_dir/slides/slide" >/dev/null

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
    espeak-ng -v "$voice" -f "$note_file" -w "$audio" >/dev/null 2>&1 || espeak-ng -f "$note_file" -w "$audio" >/dev/null 2>&1
    duration="$(ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$audio" || true)"
    if [[ -z "$duration" ]]; then duration="$seconds_per_slide"; fi
    ffmpeg -y -loop 1 -framerate 30 -i "$normalized_image" -i "$audio" -t "$duration" \
      -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$segment" >/dev/null 2>&1
  else
    ffmpeg -y -loop 1 -framerate 30 -i "$normalized_image" -t "$seconds_per_slide" \
      -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 \
      -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$segment" >/dev/null 2>&1
  fi
  printf "file '%s'\n" "$segment" >> "$segment_list"
done

if [[ "$slide_count" -eq 0 ]]; then
  echo "No slides rendered." >&2
  exit 1
fi

output="$out_dir/${base}.mp4"
ffmpeg -y -f concat -safe 0 -i "$segment_list" \
  -c:v libx264 -pix_fmt yuv420p -c:a aac -ar 44100 -ac 2 \
  "$output" >/dev/null 2>&1
echo "$output"
