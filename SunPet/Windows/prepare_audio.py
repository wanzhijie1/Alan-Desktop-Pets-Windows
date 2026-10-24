#!/usr/bin/env python3
"""Prepare the optional Windows PCM audio cache without changing original M4A files.

From the repository root:
    python Windows/prepare_audio.py --ffmpeg /path/to/ffmpeg

An explicit executable is optional if ffmpeg is on PATH, IMAGEIO_FFMPEG_EXE is
set, or the imageio-ffmpeg Python package is installed. The script downloads
nothing. One way to obtain a local tool is to install imageio-ffmpeg from its
official PyPI project: https://pypi.org/project/imageio-ffmpeg/ (version 0.6.0
was used to prepare the included cache). FFmpeg is needed only to regenerate
the cache; the finished desktop app does not invoke it.

Output: Assets/PCM/<original-stem>.wav, 44100 Hz / mono / signed PCM16, plus
manifest.json with source/output hashes and decoder version. No silence is
trimmed here: runtime preprocessing retains control of safe boundary padding.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import wave


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def resolve_ffmpeg(explicit):
    candidate = explicit or os.environ.get("IMAGEIO_FFMPEG_EXE") or shutil.which("ffmpeg")
    if not candidate:
        try:
            import imageio_ffmpeg
            candidate = imageio_ffmpeg.get_ffmpeg_exe()
        except ImportError:
            pass
    if not candidate:
        raise RuntimeError("FFmpeg not found; pass --ffmpeg PATH or install imageio-ffmpeg.")
    candidate = shutil.which(candidate) or candidate
    executable = Path(candidate).expanduser().resolve()
    if not executable.is_file():
        raise RuntimeError("FFmpeg executable does not exist: " + str(executable))
    return str(executable)


def inspect_pcm(path):
    with wave.open(str(path), "rb") as audio:
        if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate(), audio.getcomptype()) != (1, 2, 44100, "NONE"):
            raise RuntimeError("Unexpected PCM format: " + str(path))
        frames = audio.getnframes()
        if frames <= 0:
            raise RuntimeError("Empty PCM file: " + str(path))
        if len(audio.readframes(frames)) != frames * 2:
            raise RuntimeError("Truncated PCM file: " + str(path))
        return frames


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ffmpeg", help="Path to an existing ffmpeg executable")
    parser.add_argument("--assets-dir", type=Path, default=Path(__file__).resolve().parents[1] / "Assets")
    parser.add_argument("--verify-only", action="store_true", help="Check existing PCM files and source hashes without decoding")
    args = parser.parse_args()
    assets = args.assets_dir.resolve()
    clips = json.loads((assets / "clips.json").read_text(encoding="utf-8-sig"))
    output = assets / "PCM"
    output.mkdir(parents=True, exist_ok=True)
    manifest_path = output / "manifest.json"
    old_manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if args.verify_only else None
    old_entries = {entry["id"]: entry for entry in old_manifest["clips"]} if old_manifest else {}
    ffmpeg = None if args.verify_only else resolve_ffmpeg(args.ffmpeg)
    version = None if args.verify_only else subprocess.check_output([ffmpeg, "-version"], text=True, encoding="utf-8").splitlines()[0]
    entries = []
    used_names = set()
    for clip in clips:
        filename = clip["file"]
        if Path(filename).name != filename or Path(filename).suffix.lower() != ".m4a" or "\\" in filename:
            raise RuntimeError("Unsafe or unsupported clip filename: " + filename)
        source = assets / "Clips" / filename
        target = output / (Path(filename).stem + ".wav")
        if target.name.lower() in used_names:
            raise RuntimeError("Duplicate output filename: " + target.name)
        used_names.add(target.name.lower())
        if not args.verify_only:
            temporary = target.with_suffix(".partial.wav")
            try:
                subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-i", str(source), "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "44100",
                    "-c:a", "pcm_s16le", "-map_metadata", "-1", str(temporary)], check=True)
                inspect_pcm(temporary)
                os.replace(str(temporary), str(target))
            finally:
                if temporary.exists():
                    temporary.unlink()
        frames = inspect_pcm(target)
        entry = {"id": clip["id"], "source": "Clips/" + filename, "pcm": "PCM/" + target.name,
            "source_sha256": sha256(source), "pcm_sha256": sha256(target),
            "sample_rate": 44100, "channels": 1, "bits_per_sample": 16, "frames": frames,
            "duration_seconds": frames / 44100.0}
        if args.verify_only and old_entries.get(clip["id"]) != entry:
            raise RuntimeError("PCM manifest/source mismatch: " + filename)
        entries.append(entry)
        print("OK {} -> {} ({:.3f}s)".format(filename, target.name, frames / 44100.0))
    if not args.verify_only:
        manifest = {"format": "mono PCM16 44100 Hz", "silence_trimmed": False,
            "decoder": version, "clips": entries}
        temporary = manifest_path.with_suffix(".partial.json")
        temporary.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(str(temporary), str(manifest_path))
    elif len(entries) != len(old_entries):
        raise RuntimeError("Manifest contains an unexpected clip count")
    print("PASS: {} PCM clips {}".format(len(entries), "verified" if args.verify_only else "prepared"))


if __name__ == "__main__":
    main()
