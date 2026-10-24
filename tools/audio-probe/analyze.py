"""Align exact playable PCM to restricted WASAPI capture, independently of submission logs."""
import argparse
import json
import re
import subprocess
import wave
from pathlib import Path

import numpy as np
from scipy.signal import correlate


def ncc_find(recorded, reference, lo, hi):
    reference = reference - reference.mean()
    n = len(reference)
    section = recorded[lo:hi + n]
    numerator = correlate(section, reference, mode="valid", method="fft")
    squares = np.concatenate(([0.0], np.cumsum(section * section)))
    values = np.concatenate(([0.0], np.cumsum(section)))
    variance = squares[n:] - squares[:-n] - (values[n:] - values[:-n]) ** 2 / n
    score = numerator / np.sqrt(np.maximum(variance, 1e-15) * np.dot(reference, reference))
    index = int(np.argmax(score))
    return lo + index, float(score[index])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("wave", type=Path)
    parser.add_argument("--sunpet", type=Path, required=True)
    parser.add_argument("--fengge", type=Path, required=True)
    parser.add_argument("--shaolan", type=Path, required=True)
    args = parser.parse_args()
    with wave.open(str(args.wave), "rb") as wav:
        assert wav.getframerate() == 44100 and wav.getsampwidth() == 2
        recording = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2").reshape(-1, wav.getnchannels()).mean(axis=1) / 32768.0
    meta = dict(line.split("=", 1) for line in Path(str(args.wave) + ".meta.txt").read_text().splitlines())
    first_qpc = int(meta["first_qpc_100ns"]) / 1e7
    frequency = int(meta["stopwatch_frequency"])
    prefix = args.wave.with_suffix("")
    events = []
    for role in ("shaolan", "sunpet", "fengge"):
        text = Path(str(prefix) + "." + role + ".log").read_text(encoding="utf-8-sig")
        for line in text.splitlines():
            if "playing clip=" not in line:
                continue
            fields = dict(re.findall(r"([A-Za-z]+)=([0-9.]+)", line))
            events.append({"role": role, "clip": int(fields["clip"]), "hop": int(fields["hop"]),
                           "submitted": int(fields["startQpc"]) / frequency - first_qpc,
                           "submitted_qpc": int(fields["startQpc"]), "play_ms": float(fields["playMs"])})
    events.sort(key=lambda event: event["submitted_qpc"])
    if not events:
        raise RuntimeError("No playback traces found")
    references = []
    for index, event in enumerate(events):
        executable = getattr(args, event["role"]).resolve()
        pcm_folder = "PCM" if event["role"] == "sunpet" else "Clips"
        audio = executable.parent / "Assets" / pcm_folder / f"{event['clip']:02d}.wav"
        ref_prefix = Path(str(prefix) + f".ref{index}")
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        str(Path(__file__).with_name("export_reference.ps1")),
                        "-AssemblyPath", str(executable), "-AudioPath", str(audio), "-OutputPrefix", str(ref_prefix)], check=True)
        ref = np.fromfile(str(ref_prefix) + ".pcm", dtype="<i2").astype(np.float64) / 32768.0
        details = json.loads(Path(str(ref_prefix) + ".json").read_text(encoding="utf-8-sig"))
        lo = max(0, int((event["submitted"] - 0.35) * 44100))
        hi = min(len(recording) - len(ref), int((event["submitted"] + 0.60) * 44100))
        if hi <= lo:
            raise RuntimeError("Playback is outside recorded interval")
        start, score = ncc_find(recording, ref, lo, hi)
        event.update(details)
        event["matched_start_sample"] = start
        event["matched_start_s"] = start / 44100.0
        event["match_correlation"] = score
        event["output_after_submission_ms"] = (start / 44100.0 - event["submitted"]) * 1000.0
        event["activity_start_s"] = start / 44100.0 + details["speech_start"]
        event["activity_end_s"] = start / 44100.0 + details["speech_end"]
        if score < 0.75:
            event["warning"] = "Weak reference match; do not use this timing without inspection"
        references.append(ref)

    # Fit the recorded mix to exact PCM references. This can expose lost output chunks,
    # unlike a log-only timing report. Resampling/enhancements can also increase residual.
    design = np.zeros((len(recording), len(events)), dtype=np.float64)
    for index, (event, ref) in enumerate(zip(events, references)):
        start = event["matched_start_sample"]
        design[start:start + len(ref), index] = ref
    gains, _, _, _ = np.linalg.lstsq(design, recording, rcond=None)
    modeled = design @ gains
    residual = recording - modeled
    relative_residual = float(np.sqrt(np.dot(residual, residual) / max(np.dot(recording, recording), 1e-15)))
    for event, gain in zip(events, gains):
        event["fitted_gain"] = float(gain)
    gaps = []
    for previous, current in zip(events, events[1:]):
        gaps.append({"from": previous["role"], "to": current["role"],
                     "activity_gap_ms": (current["activity_start_s"] - previous["activity_end_s"]) * 1000.0,
                     "full_pcm_gap_ms": (current["matched_start_s"] - previous["matched_start_s"] - previous["duration"]) * 1000.0})

    block = 441  # 10 ms, including both channels' measured mixed output.
    count = len(recording) // block
    observed_rms = np.sqrt(np.mean(recording[:count * block].reshape(-1, block) ** 2, axis=1))
    expected_rms = np.sqrt(np.mean(modeled[:count * block].reshape(-1, block) ** 2, axis=1))
    suspects = np.flatnonzero((expected_rms > .005) & (observed_rms < expected_rms * .25))
    report = {"method": "Process-tree WASAPI PCM; exact engine-trimmed reference NCC; activity boundaries are energy estimates, not phonetic labels",
              "capture": meta, "events": events, "transitions": gaps,
              "relative_residual_rms": relative_residual,
              "possible_output_dropout_10ms_blocks": [round(int(i) * .01, 2) for i in suspects]}
    output = Path(str(prefix) + ".analysis.json")
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        figure, axes = plt.subplots(2, 1, figsize=(14, 6), sharex=True)
        t = np.arange(len(recording)) / 44100.0
        axes[0].plot(t[::44], recording[::44], linewidth=.55, color="#324d6b")
        axes[0].set_ylabel("Captured amplitude")
        axes[1].plot(np.arange(count) * .01, observed_rms, label="Captured RMS", color="#324d6b")
        axes[1].plot(np.arange(count) * .01, expected_rms, label="Reference fit", color="#d88d36", alpha=.8)
        colors = ["#66a382", "#d88d36", "#8d7cc3", "#d88d36"]
        for index, event in enumerate(events):
            for axis in axes:
                axis.axvspan(event["activity_start_s"], event["activity_end_s"], alpha=.13, color=colors[index % len(colors)])
            axes[0].text(event["activity_start_s"], .82, f"{event['role']} {event['clip']:02d}", transform=axes[0].get_xaxis_transform(), fontsize=9)
        axes[1].set_ylabel("10 ms RMS")
        axes[1].set_xlabel("Seconds in captured output")
        axes[1].legend(loc="upper right")
        axes[0].set_xlim(max(0, events[0]["matched_start_s"] - .25), events[-1]["matched_start_s"] + events[-1]["duration"] + .25)
        figure.suptitle("Actual process-tree audio output and reference alignment")
        figure.tight_layout()
        figure.savefig(str(prefix) + ".waveform.png", dpi=150)
        plt.close(figure)
    except ImportError:
        pass


if __name__ == "__main__":
    main()
