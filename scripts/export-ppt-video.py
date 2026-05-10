#!/usr/bin/env python3
"""Convert PPT/PPTX to MP4 video with Edge TTS narration.

Usage:
    export-ppt-video <deck.pptx> [output-dir] [seconds-per-slide] [voice] [speed] [resolution] [bgm] [bgm-volume]

Requires: libreoffice, pdftoppm, ffmpeg, ffprobe, python3 with edge-tts.
"""

import asyncio
import json
import os
import posixpath
import re
import shutil
import subprocess
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Optional

try:
    import edge_tts
except ImportError:
    print("Missing dependency: edge-tts. Run: pip install edge-tts", file=sys.stderr)
    sys.exit(1)

# ---------------------------------------------------------------------------
# Voice mapping: short names → Edge TTS voice identifiers
# ---------------------------------------------------------------------------
VOICE_MAP = {
    "zh": "zh-CN-XiaoxiaoNeural",
    "zh-f": "zh-CN-XiaoxiaoNeural",
    "zh-m": "zh-CN-YunxiNeural",
    "zh-news": "zh-CN-YunyangNeural",
    "zh-story": "zh-CN-XiaoyiNeural",
    "en": "en-US-JennyNeural",
    "en-f": "en-US-JennyNeural",
    "en-m": "en-US-GuyNeural",
    "en-gb": "en-GB-SoniaNeural",
    "ja": "ja-JP-NanamiNeural",
    "ko": "ko-KR-SunHiNeural",
}

SPEED_MAP = {
    "0.75": "-25%",
    "0.5": "-50%",
    "1.0": "+0%",
    "1.25": "+25%",
    "1.5": "+50%",
    "2.0": "+100%",
}

RESOLUTION_MAP = {
    "480p": "854:480",
    "720p": "1280:720",
    "1080p": "1920:1080",
}

current_step = "initializing"


def log_progress(stage: str, slide: int = 0, total: int = 0, detail: str = ""):
    """Write structured progress to stderr for backend parsing."""
    msg = json.dumps({"stage": stage, "slide": slide, "total": total, "detail": detail}, ensure_ascii=False)
    print(msg, file=sys.stderr, flush=True)


def die(stage: str, message: str):
    print(f"PPT video export failed at step: {stage}.", file=sys.stderr)
    print(message, file=sys.stderr)
    sys.exit(1)


def require_binaries(*names: str):
    for name in names:
        if not shutil.which(name):
            die("dependency-check", f"Missing dependency: {name}")


# ---------------------------------------------------------------------------
# PPT speaker notes extraction (pure Python, no COM/libreoffice needed)
# ---------------------------------------------------------------------------
OOXML_NS = {
    "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
    "p": "http://schemas.openxmlformats.org/presentationml/2006/main",
    "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
    "rel": "http://schemas.openxmlformats.org/package/2006/relationships",
}


def _normalize_part(base: str, target: str) -> str:
    if target.startswith("/"):
        return target.lstrip("/")
    return posixpath.normpath(posixpath.join(posixpath.dirname(base), target))


def _rels_path(part: str) -> str:
    return posixpath.join(posixpath.dirname(part), "_rels", posixpath.basename(part) + ".rels")


def _read_rels(z: zipfile.ZipFile, part: str) -> dict:
    path = _rels_path(part)
    if path not in z.namelist():
        return {}
    root = ET.fromstring(z.read(path))
    rels = {}
    for item in root.findall("rel:Relationship", OOXML_NS):
        rid = item.attrib.get("Id")
        target = item.attrib.get("Target")
        rel_type = item.attrib.get("Type", "")
        if rid and target:
            rels[rid] = (_normalize_part(part, target), rel_type)
    return rels


def _read_text(z: zipfile.ZipFile, part: str) -> str:
    if part not in z.namelist():
        return ""
    root = ET.fromstring(z.read(part))
    return "\n".join(t.text or "" for t in root.findall(".//a:t", OOXML_NS)).strip()


def extract_notes(pptx_path: str) -> dict[str, str]:
    """Return {slide_number_str: speaker_notes_text}."""
    notes: dict[str, str] = {}
    with zipfile.ZipFile(pptx_path) as z:
        presentation = "ppt/presentation.xml"
        presentation_rels = _read_rels(z, presentation)
        ordered = []
        if presentation in z.namelist():
            root = ET.fromstring(z.read(presentation))
            for slide_id in root.findall(".//p:sldIdLst/p:sldId", OOXML_NS):
                rid = slide_id.attrib.get("{" + OOXML_NS["r"] + "}id")
                if rid and rid in presentation_rels:
                    ordered.append(presentation_rels[rid][0])

        for idx, slide_part in enumerate(ordered, start=1):
            slide_rels = _read_rels(z, slide_part)
            note_part = ""
            for target, rel_type in slide_rels.values():
                if rel_type.endswith("/notesSlide"):
                    note_part = target
                    break
            notes[str(idx)] = _read_text(z, note_part) if note_part else ""

    return notes


# ---------------------------------------------------------------------------
# LibreOffice PDF export
# ---------------------------------------------------------------------------
def convert_to_pdf(pptx_path: str, out_dir: str) -> str:
    tmp = os.path.join(out_dir, "tmp")
    os.makedirs(tmp, exist_ok=True)
    result = subprocess.run(
        ["libreoffice", "--headless", "--nologo", "--nofirststartwizard", "--convert-to", "pdf", "--outdir", tmp, pptx_path],
        capture_output=True, text=True, timeout=120,
    )
    if result.returncode != 0:
        die("LibreOffice PDF export", result.stderr or result.stdout or "LibreOffice returned non-zero exit code.")

    pdfs = sorted(Path(tmp).glob("*.pdf"))
    if not pdfs:
        die("LibreOffice PDF export", "LibreOffice did not produce a PDF.")
    return str(pdfs[0])


# ---------------------------------------------------------------------------
# PDF → PNG rendering
# ---------------------------------------------------------------------------
def render_slides(pdf_path: str, out_dir: str, dpi: int = 180) -> list[str]:
    slides_dir = os.path.join(out_dir, "slides")
    os.makedirs(slides_dir, exist_ok=True)
    prefix = os.path.join(slides_dir, "slide")

    result = subprocess.run(
        ["pdftoppm", "-png", "-r", str(dpi), pdf_path, prefix],
        capture_output=True, text=True, timeout=120,
    )
    if result.returncode != 0:
        die("PDF slide rendering", result.stderr or "pdftoppm failed.")

    images = sorted(Path(slides_dir).glob("slide-*.png"))
    # Normalize names to slide-001.png, slide-002.png, ...
    normalized = []
    for i, img in enumerate(images, start=1):
        new_name = img.parent / f"slide-{i:03d}.png"
        if img.name != new_name.name:
            img.rename(new_name)
        normalized.append(str(new_name))

    return normalized


# ---------------------------------------------------------------------------
# Edge TTS: concurrent audio generation
# ---------------------------------------------------------------------------
async def generate_audio(text: str, voice: str, rate: str, output_path: str) -> bool:
    """Generate TTS audio file. Returns True on success."""
    if not text.strip():
        return False
    try:
        communicate = edge_tts.Communicate(text, voice, rate=rate)
        await communicate.save(output_path)
        return os.path.getsize(output_path) > 0
    except Exception as e:
        print(f"Edge TTS error: {e}", file=sys.stderr)
        return False


async def generate_all_audios(
    notes: dict[str, str],
    total_slides: int,
    voice: str,
    rate: str,
    audio_dir: str,
) -> dict[int, str]:
    """Generate audio for all slides concurrently. Returns {slide_index: audio_path}."""
    os.makedirs(audio_dir, exist_ok=True)
    tasks = []
    for i in range(1, total_slides + 1):
        text = notes.get(str(i), "").strip()
        audio_path = os.path.join(audio_dir, f"slide-{i:03d}.mp3")
        if text:
            tasks.append((i, text, audio_path))
        else:
            tasks.append((i, "", audio_path))

    audios: dict[int, str] = {}
    sem = asyncio.Semaphore(5)  # Limit concurrent Edge TTS requests

    async def _gen(idx: int, txt: str, path: str):
        if not txt:
            return
        async with sem:
            ok = await generate_audio(txt, voice, rate, path)
            if ok:
                audios[idx] = path

    await asyncio.gather(*[_gen(idx, txt, path) for idx, txt, path in tasks])
    return audios


def get_audio_duration(path: str) -> Optional[float]:
    try:
        result = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries", "format=duration",
             "-of", "default=noprint_wrappers=1:nokey=1", path],
            capture_output=True, text=True, timeout=10,
        )
        return float(result.stdout.strip())
    except Exception:
        return None


# ---------------------------------------------------------------------------
# FFmpeg video segment creation and merging
# ---------------------------------------------------------------------------
def create_segment(
    image_path: str,
    audio_path: Optional[str],
    duration: float,
    video_size: str,
    output_path: str,
) -> None:
    cmd = ["ffmpeg", "-y", "-loglevel", "error"]
    cmd += ["-loop", "1", "-framerate", "30", "-i", image_path]

    if audio_path and os.path.isfile(audio_path) and os.path.getsize(audio_path) > 0:
        cmd += ["-i", audio_path, "-t", str(duration)]
    else:
        cmd += ["-t", str(duration), "-f", "lavfi", "-i", f"anullsrc=channel_layout=stereo:sample_rate=44100"]

    vf = f"scale={video_size}:force_original_aspect_ratio=decrease,pad={video_size}:(ow-iw)/2:(oh-ih)/2"
    cmd += [
        "-vf", vf,
        "-c:v", "libx264", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-shortest",
        output_path,
    ]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
    if result.returncode != 0:
        die("segment rendering", f"ffmpeg failed: {result.stderr}")


def merge_segments(segment_list_path: str, output_path: str) -> None:
    cmd = [
        "ffmpeg", "-y", "-loglevel", "error",
        "-f", "concat", "-safe", "0", "-i", segment_list_path,
        "-c:v", "libx264", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-ar", "44100", "-ac", "2",
        output_path,
    ]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
    if result.returncode != 0:
        die("video merging", f"ffmpeg concat failed: {result.stderr}")


def mix_bgm(video_path: str, bgm_path: str, bgm_volume: int, output_path: str) -> None:
    gain = max(0, min(100, bgm_volume)) / 100.0
    cmd = [
        "ffmpeg", "-y", "-loglevel", "error",
        "-i", video_path, "-stream_loop", "-1", "-i", bgm_path,
        "-filter_complex",
        f"[1:a]volume={gain:.2f}[bgm];[0:a][bgm]amix=inputs=2:duration=first:dropout_transition=2[a]",
        "-map", "0:v", "-map", "[a]",
        "-c:v", "copy", "-c:a", "aac", "-ar", "44100", "-ac", "2",
        output_path,
    ]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
    if result.returncode != 0:
        die("BGM mixing", f"ffmpeg BGM mix failed: {result.stderr}")


# ---------------------------------------------------------------------------
# Main pipeline
# ---------------------------------------------------------------------------
async def main():
    global current_step

    if len(sys.argv) < 2 or sys.argv[1] in ("-h", "--help"):
        print(__doc__.strip())
        sys.exit(0)

    pptx_path = os.path.realpath(sys.argv[1])
    out_dir = sys.argv[2] if len(sys.argv) > 2 else ""
    seconds_per_slide = float(sys.argv[3]) if len(sys.argv) > 3 else 5.0
    voice_key = sys.argv[4].strip().lower() if len(sys.argv) > 4 else "zh"
    speed_key = sys.argv[5].strip() if len(sys.argv) > 5 else "1.0"
    resolution_key = sys.argv[6].strip().lower() if len(sys.argv) > 6 else "720p"
    bgm_path = sys.argv[7] if len(sys.argv) > 7 else ""
    bgm_volume = int(sys.argv[8]) if len(sys.argv) > 8 else 30

    if not os.path.isfile(pptx_path):
        die("input validation", f"PPTX not found: {pptx_path}")

    require_binaries("libreoffice", "pdftoppm", "ffmpeg", "ffprobe")

    base = Path(pptx_path).stem
    if not out_dir:
        out_dir = str(Path(pptx_path).parent / f"{base}-video")
    os.makedirs(out_dir, exist_ok=True)

    voice = VOICE_MAP.get(voice_key, VOICE_MAP["zh"])
    rate = SPEED_MAP.get(speed_key, "+0%")
    video_size = RESOLUTION_MAP.get(resolution_key, "1280:720")

    # Step 1: Extract speaker notes
    current_step = "Reading speaker notes"
    log_progress("notes", detail=current_step)
    try:
        notes = extract_notes(pptx_path)
    except Exception as e:
        die(current_step, str(e))

    # Step 2: Convert PPT to PDF via LibreOffice
    current_step = "LibreOffice PDF export"
    log_progress("pdf", detail=current_step)
    pdf_path = convert_to_pdf(pptx_path, out_dir)

    # Step 3: Render PDF pages to PNG
    current_step = "Rendering slides"
    log_progress("render", detail=current_step)
    slide_images = render_slides(pdf_path, out_dir)
    total_slides = len(slide_images)
    if total_slides == 0:
        die("Rendering slides", "No slides rendered from PDF.")

    log_progress("render", total=total_slides, detail=f"Rendered {total_slides} slides")

    # Step 4: Generate TTS audio (concurrent)
    current_step = "Generating narration"
    log_progress("tts", total=total_slides, detail=current_step)
    audio_dir = os.path.join(out_dir, "audio")
    audios = await generate_all_audios(notes, total_slides, voice, rate, audio_dir)
    log_progress("tts", total=total_slides, slide=total_slides, detail=f"Generated {len(audios)} audio tracks")

    # Step 5: Create video segments
    current_step = "Creating video segments"
    segments_dir = os.path.join(out_dir, "segments")
    os.makedirs(segments_dir, exist_ok=True)
    segment_list_path = os.path.join(out_dir, "segments.txt")

    with open(segment_list_path, "w", encoding="utf-8") as f:
        for i in range(1, total_slides + 1):
            idx_str = f"{i:03d}"
            image = os.path.join(out_dir, "slides", f"slide-{idx_str}.png")
            segment = os.path.join(segments_dir, f"segment-{idx_str}.mp4")
            audio = audios.get(i)

            if audio:
                duration = get_audio_duration(audio) or seconds_per_slide
            else:
                duration = seconds_per_slide

            log_progress("segments", slide=i, total=total_slides, detail=f"Slide {i}/{total_slides}")
            create_segment(image, audio, duration, video_size, segment)
            f.write(f"file '{segment}'\n")

    # Step 6: Merge all segments
    current_step = "Merging video"
    log_progress("merge", detail=current_step)
    merged_path = os.path.join(out_dir, "tmp", f"{base}-merged.mp4")
    os.makedirs(os.path.dirname(merged_path), exist_ok=True)
    merge_segments(segment_list_path, merged_path)

    # Step 7: Mix BGM if provided
    output_path = os.path.join(out_dir, f"{base}.mp4")
    if bgm_path and os.path.isfile(bgm_path):
        current_step = "Mixing background music"
        log_progress("bgm", detail=current_step)
        mix_bgm(merged_path, bgm_path, bgm_volume, output_path)
    else:
        import shutil as sh
        sh.move(merged_path, output_path)

    log_progress("done", detail=output_path)
    # Output the final video path on stdout (consumed by the .NET backend)
    print(output_path)


if __name__ == "__main__":
    asyncio.run(main())
