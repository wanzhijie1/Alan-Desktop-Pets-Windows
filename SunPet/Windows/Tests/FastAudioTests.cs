using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DesktopPetAudio;

internal static class FastAudioTests
{
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        Console.WriteLine("OK " + label);
    }

    private static void WriteWave(string path, int frames, int silenceBefore, int silenceAfter, bool stereo)
    {
        using (BinaryWriter writer = new BinaryWriter(File.Create(path)))
        {
            int channels = stereo ? 2 : 1;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * channels * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
            writer.Write(44100); writer.Write(44100 * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(frames * channels * 2);
            for (int i = 0; i < frames; i++)
            {
                short sample = i < silenceBefore || i >= frames - silenceAfter ? (short)0 : (short)(Math.Sin(i * 2 * Math.PI * 440 / 44100) * 12000);
                writer.Write(sample);
                if (stereo) writer.Write(sample);
            }
        }
    }

    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "DesktopPetAudio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mono = Path.Combine(directory, "mono.wav"), stereo = Path.Combine(directory, "stereo.wav"), silence = Path.Combine(directory, "silence.wav");
            WriteWave(mono, 17643, 4410, 4410, false); // non-multiple of 10 ms deliberately tests the final partial block
            WriteWave(stereo, 17643, 4410, 4410, true);
            WriteWave(silence, 4410, 4410, 0, false);
            WaveClip untrimmed = WaveClip.Load(mono, false), trimmed = WaveClip.Load(mono), stereoClip = WaveClip.Load(stereo), silentClip = WaveClip.Load(silence);
            Check(Math.Abs(untrimmed.Duration - 17643 / 44100.0) < 1e-10, "untrimmed PCM keeps its exact sample duration");
            Check(trimmed.LeadingTrim > .08 && trimmed.LeadingTrim < .09 && trimmed.TrailingTrim > .08 && trimmed.TrailingTrim < .09, "certain edge silence trimmed with 15 ms protection");
            Check(Math.Abs(trimmed.OriginalDuration - trimmed.Duration - trimmed.LeadingTrim - trimmed.TrailingTrim) < 1e-10, "sample-exact duration accounting");
            Check(stereoClip.CopySamples().Length == trimmed.CopySamples().Length && stereoClip.CopySamples()[1000] == trimmed.CopySamples()[1000], "stereo downmix preserves same-channel PCM");
            Check(silentClip.Duration == .1 && silentClip.SpeechStart == 0 && silentClip.SpeechEnd == .1, "silent clip is retained and receives safe speech timing fallback");
            Check(trimmed.SpeechStart >= 0 && trimmed.SpeechEnd <= trimmed.Duration && trimmed.SpeechEnd > trimmed.SpeechStart, "speech activity stays within playable audio");
            if (args.Length > 0)
            {
                foreach (string path in Directory.GetFiles(args[0], "*.wav"))
                {
                    WaveClip clip = WaveClip.Load(path);
                    Check(clip.Duration > .04 && clip.SpeechEnd <= clip.Duration, "asset " + Path.GetFileName(path) + " playable=" + clip.Duration.ToString("F3") + " speech=" + clip.SpeechStart.ToString("F3") + ".." + clip.SpeechEnd.ToString("F3"));
                }
            }
            int callerThread = Thread.CurrentThread.ManagedThreadId;
            int callbackThread = callerThread, ended = 0, failed = 0;
            List<double> submission = new List<double>();
            List<double> replacement = new List<double>();
            using (ManualResetEvent finished = new ManualResetEvent(false))
            using (WaveOutput output = new WaveOutput())
            {
                output.Ended += delegate(PlaybackHandle handle) { callbackThread = Thread.CurrentThread.ManagedThreadId; Interlocked.Increment(ref ended); finished.Set(); };
                output.Failed += delegate(PlaybackHandle handle, Exception error) { Interlocked.Increment(ref failed); Console.Error.WriteLine(error); finished.Set(); };
                Stopwatch wall = Stopwatch.StartNew();
                PlaybackHandle first = output.Play(untrimmed, 0);
                Check(first.StartedTimestamp > 0 && first.StartedUtc.Kind == DateTimeKind.Utc && Object.ReferenceEquals(output.Current, first), "synchronous start returns a stable handle and submission timestamp");
                Check(finished.WaitOne(3000), "muted playback naturally completes");
                Check(first.IsCompleted && !first.WasStopped && !output.IsPlaying && ended == 1 && failed == 0, "natural completion is reported once");
                Check(callbackThread != callerThread, "completion uses the audio worker thread");
                Check(Math.Abs(output.PositionSeconds - untrimmed.Duration) < .002, "final partial block adds no zero-padding duration");
                Console.WriteLine("MEASURE first complete wall_ms=" + wall.Elapsed.TotalMilliseconds.ToString("F3") + " duration_ms=" + (untrimmed.Duration * 1000).ToString("F3"));

                for (int i = 0; i < 100; i++)
                {
                    Stopwatch timer = Stopwatch.StartNew();
                    PlaybackHandle a = output.Play(trimmed, 0);
                    submission.Add(timer.Elapsed.TotalMilliseconds);
                    timer.Restart();
                    PlaybackHandle b = output.Play(trimmed, 0);
                    replacement.Add(timer.Elapsed.TotalMilliseconds);
                    if (!a.WasStopped || Object.ReferenceEquals(a, b)) throw new InvalidOperationException("Replacement did not invalidate its previous handle");
                    output.Stop();
                    if (!b.WasStopped || output.IsPlaying) throw new InvalidOperationException("Immediate Stop failed");
                }
                int before = ended;
                Thread.Sleep(100);
                Check(ended == before && failed == 0, "stopped/replaced clips never emit stale completion");
                Console.WriteLine("OK 100 replacement and immediate-stop cycles");
                finished.Reset();
                output.Play(trimmed, 0);
                output.Volume = .25; output.Volume = 0; // software gain changes queued future blocks only
                Check(output.Volume == 0 && finished.WaitOne(3000) && failed == 0, "dynamic software volume does not interrupt playback");
                submission.Sort();
                replacement.Sort();
                Console.WriteLine("MEASURE Play submit p50_ms=" + submission[submission.Count / 2].ToString("F3") + " p95_ms=" + submission[95].ToString("F3") + " max_ms=" + submission[99].ToString("F3"));
                Console.WriteLine("MEASURE active replacement p50_ms=" + replacement[50].ToString("F3") + " p95_ms=" + replacement[95].ToString("F3") + " max_ms=" + replacement[99].ToString("F3"));
            }
            using (ManualResetEvent disposedFromCallback = new ManualResetEvent(false))
            {
                WaveOutput output = new WaveOutput();
                output.Ended += delegate { output.Dispose(); disposedFromCallback.Set(); };
                output.Play(trimmed, 0);
                Check(disposedFromCallback.WaitOne(3000), "Dispose from completion callback has no deadlock");
                output.Dispose();
            }
            int handlesBefore = Process.GetCurrentProcess().HandleCount;
            for (int i = 0; i < 20; i++) using (WaveOutput output = new WaveOutput()) { output.Play(trimmed, 0); output.Dispose(); }
            GC.Collect(); GC.WaitForPendingFinalizers();
            int handlesAfter = Process.GetCurrentProcess().HandleCount;
            Check(handlesAfter <= handlesBefore + 6, "repeated active disposal releases native handles (" + handlesBefore + " -> " + handlesAfter + ")");
            Console.WriteLine("PASS");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
        finally { Directory.Delete(directory, true); }
    }
}
