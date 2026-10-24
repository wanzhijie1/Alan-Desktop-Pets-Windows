Process-tree audio probe

Build with Windows PowerShell:
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1

Check COM activation without starting a recording:
AudioProbe.exe --check

Capture only three fresh, isolated test children:
AudioProbe.exe --output capture.wav --seconds 14 --clip 7 --sunpet <SunPet.exe> --fengge <GoodThingPet.exe> --shaolan <ShaolanPet.exe>

The harness starts its own new process tree with a unique --bridge-test scope,
--audible-test, --trace and automatic exit. Only Shaolan gets --test-play 7.
It never accepts an arbitrary process ID, never opens a microphone or a default
render endpoint, and never enables exclude-tree capture. Unrelated running apps
are outside the captured tree. Child logs identify exactly which processes ran.

Capture uses ActivateAudioInterfaceAsync with VAD\Process_Loopback and
PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE. It requires x64 Windows build
20348 or later. This diagnostics-only requirement does not change app requirements.
Reference: https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/

Output is 44100 Hz stereo PCM16 WAV, plus per-packet QPC timestamps, metadata,
and three role logs. Process-loopback reports device position zero on this system;
QPC gaps larger than 2 ms are retained as silence instead of shortening the timeline.
Inserted startup silence is not, by itself, an output dropout.

Analyze actual audio against each app's exact playable PCM:
python analyze.py capture.wav --sunpet <SunPet.exe> --fengge <GoodThingPet.exe> --shaolan <ShaolanPet.exe>

Analysis requires NumPy and SciPy; Matplotlib optionally renders a waveform.
It calls each compiled app's WaveClip.Load/CopySamples using reflection to reproduce
the actual trim/fade reference. Trace timestamps provide a bounded search window;
normalized waveform correlation measures the recorded alignment. A fitted sum of
the references is compared with the captured mix to flag possible 10 ms dropouts.

Positive activity_gap_ms means a gap; negative means overlap. Activity boundaries
are energy-based estimates, not human phonetic labels. This captures digital audio
from the Windows audio engine; it is not a microphone/acoustic round-trip test.
Inspect correlation quality and packet continuity before quoting timing results.
