using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DesktopPetAudio
{
    // Decode once at startup. The original asset is never changed.
    public sealed class WaveClip
    {
        internal readonly short[] Samples;
        public int SampleRate { get { return 44100; } }
        public int Channels { get { return 1; } }
        public double Duration { get { return Samples.Length / 44100.0; } }
        public double OriginalDuration { get; private set; }
        public double LeadingTrim { get; private set; }
        public double TrailingTrim { get; private set; }
        public double SpeechStart { get; private set; }
        public double SpeechEnd { get; private set; }
        public string SourcePath { get; private set; }
        public short[] CopySamples() { return (short[])Samples.Clone(); }

        private WaveClip(short[] samples, double original, double leading, double trailing, string path)
        {
            Samples = samples; OriginalDuration = original; LeadingTrim = leading; TrailingTrim = trailing; SourcePath = path;
            AnalyzeSpeech();
        }

        private void AnalyzeSpeech()
        {
            int peak = 0;
            foreach (short sample in Samples) peak = Math.Max(peak, Math.Abs((int)sample));
            double threshold = Math.Max(32768 * .0015, peak * .02);
            double weakThreshold = Math.Max(32768 * .001, peak * .015);
            const int block = 88, requiredBlocks = 6, guard = 662;
            int run = 0, first = -1, last = -1;
            int weakRun = 0, weakLast = -1;
            for (int position = 0; position < Samples.Length; position += block)
            {
                int count = Math.Min(block, Samples.Length - position);
                double squares = 0;
                for (int i = 0; i < count; i++) { double sample = Samples[position + i]; squares += sample * sample; }
                double rms = Math.Sqrt(squares / count);
                if (rms >= threshold)
                {
                    run++;
                    if (run >= requiredBlocks)
                    {
                        if (first < 0) first = position - (requiredBlocks - 1) * block;
                        last = position + count;
                    }
                }
                else run = 0;
                if (rms >= weakThreshold)
                {
                    weakRun++;
                    if (weakRun >= requiredBlocks) weakLast = position + count;
                }
                else weakRun = 0;
            }
            SpeechStart = first < 0 ? 0 : Math.Max(0, first - guard) / 44100.0;
            SpeechEnd = last < 0 ? Duration : Math.Min(Samples.Length, last + guard) / 44100.0;
            // A threshold-sensitive quiet word ending must not be mistaken for a long pause.
            // This affects reply scheduling only; no additional audio samples are removed.
            double weakEnd = weakLast < 0 ? Duration : Math.Min(Samples.Length, weakLast + guard) / 44100.0;
            if (weakEnd - SpeechEnd > .060) SpeechEnd = weakEnd;
        }

        public static WaveClip Load(string path, bool trimSilence = true)
        {
            short[] samples;
            using (BinaryReader reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII))
            {
                Stream file = reader.BaseStream;
                if (file.Length < 44 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a PCM WAV: " + path);
                long end = reader.ReadUInt32() + 8L;
                if (end > file.Length || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Truncated WAV: " + path);
                int channels = 0;
                List<byte[]> data = new List<byte[]>();
                long totalBytes = 0;
                while (file.Position + 8 <= end)
                {
                    string chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    uint length = reader.ReadUInt32();
                    long next = file.Position + length;
                    if (next > end) throw new InvalidDataException("Truncated WAV chunk: " + path);
                    if (chunk == "fmt ")
                    {
                        if (length < 16) throw new InvalidDataException("Invalid WAV format: " + path);
                        ushort format = reader.ReadUInt16();
                        channels = reader.ReadUInt16();
                        uint rate = reader.ReadUInt32(), byteRate = reader.ReadUInt32();
                        ushort align = reader.ReadUInt16(), bits = reader.ReadUInt16();
                        if (format != 1 || (channels != 1 && channels != 2) || rate != 44100 || bits != 16 || align != channels * 2 || byteRate != rate * align)
                            throw new InvalidDataException("Expected PCM16 44100 Hz mono/stereo WAV: " + path);
                    }
                    else if (chunk == "data")
                    {
                        totalBytes += length;
                        if (totalBytes > 44100 * 4 * 120) throw new InvalidDataException("Audio clip exceeds the 120 second memory limit.");
                        data.Add(reader.ReadBytes((int)length));
                    }
                    file.Position = next + (length & 1);
                }
                if (channels == 0 || totalBytes == 0 || totalBytes % (channels * 2) != 0) throw new InvalidDataException("Missing or incomplete PCM data: " + path);
                byte[] bytes = new byte[(int)totalBytes];
                int offset = 0;
                foreach (byte[] chunk in data) { Buffer.BlockCopy(chunk, 0, bytes, offset, chunk.Length); offset += chunk.Length; }
                samples = new short[bytes.Length / (channels * 2)];
                for (int i = 0; i < samples.Length; i++)
                {
                    int index = i * channels * 2;
                    int left = (short)(bytes[index] | bytes[index + 1] << 8);
                    samples[i] = channels == 1 ? (short)left : (short)((left + (short)(bytes[index + 2] | bytes[index + 3] << 8)) / 2);
                }
            }
            int first = 0, last = samples.Length;
            if (trimSilence)
            {
                int peak = 0;
                foreach (short sample in samples) peak = Math.Max(peak, Math.Abs((int)sample));
                double threshold = Math.Max(24, Math.Min(90, peak * .008));
                const int block = 88; // approximately 2 ms at 44.1 kHz
                int activeFirst = -1, activeLast = -1;
                for (int position = 0; position < samples.Length; position += block)
                {
                    int count = Math.Min(block, samples.Length - position), maximum = 0;
                    double squares = 0;
                    for (int j = 0; j < count; j++) { int value = samples[position + j]; squares += (double)value * value; maximum = Math.Max(maximum, Math.Abs(value)); }
                    if (Math.Sqrt(squares / count) >= threshold || maximum >= threshold * 4)
                    {
                        if (activeFirst < 0) activeFirst = position;
                        activeLast = position + count;
                    }
                }
                // A wholly silent/very quiet recording remains intact. Do not manufacture an empty clip.
                if (activeFirst >= 0)
                {
                    const int guard = 662; // 15 ms of protection for weak consonants and releases
                    first = Math.Max(0, activeFirst - guard);
                    last = Math.Min(samples.Length, activeLast + guard);
                    if (last - first < 1764) { first = 0; last = samples.Length; }
                }
            }
            short[] playable = new short[last - first];
            Array.Copy(samples, first, playable, 0, playable.Length);
            // Only faded at a new cut boundary; an untrimmed load is bit-for-bit apart from stereo downmix.
            int fade = Math.Min(88, playable.Length / 2);
            if (first > 0) for (int i = 0; i < fade; i++) playable[i] = (short)(playable[i] * (i / (double)fade));
            if (last < samples.Length) for (int i = 0; i < fade; i++) playable[playable.Length - 1 - i] = (short)(playable[playable.Length - 1 - i] * (i / (double)fade));
            return new WaveClip(playable, samples.Length / 44100.0, first / 44100.0, (samples.Length - last) / 44100.0, Path.GetFullPath(path));
        }
    }

    public sealed class PlaybackHandle
    {
        public long Id { get; internal set; }
        public WaveClip Clip { get; internal set; }
        public DateTime StartedUtc { get; internal set; }
        public long StartedTimestamp { get; internal set; }
        public long SubmittedTimestamp { get { return StartedTimestamp; } }
        public double Duration { get { return Clip.Duration; } }
        internal volatile bool completed, stopped;
        public bool IsCompleted { get { return completed; } }
        public bool WasStopped { get { return stopped; } }
    }

    // WinMM event callbacks only signal a kernel event; no managed code is invoked inside a driver callback.
    // Four persistent prepared blocks provide 40 ms refill headroom without holding entire clips in native queues.
    public sealed class WaveOutput : IDisposable
    {
        private const int FramesPerBlock = 441;
        private readonly object gate = new object();
        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private readonly NativeBuffer[] buffers = new NativeBuffer[4];
        private readonly int headerSize = Marshal.SizeOf(typeof(WaveHeader));
        private readonly int flagsOffset = (int)Marshal.OffsetOf(typeof(WaveHeader), "Flags");
        private readonly int lengthOffset = (int)Marshal.OffsetOf(typeof(WaveHeader), "BufferLength");
        private IntPtr device;
        private Thread worker;
        private volatile bool disposed;
        private PlaybackHandle current;
        private int nextFrame, queued;
        private long sequence;
        private double volume = .65, appliedGain = .65;
        // If an exceptional driver refuses reset/unprepare/close, preserve its memory rather than freeing live pointers.
        private static readonly List<WaveOutput> quarantined = new List<WaveOutput>();
        public event Action<PlaybackHandle> Ended;
        public event Action<PlaybackHandle, Exception> Failed;

        public WaveOutput()
        {
            try
            {
                WaveFormat format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSecond = 44100, AverageBytesPerSecond = 88200, BlockAlign = 2, BitsPerSample = 16 };
                Check(NativeAudio.waveOutOpen(out device, UInt32.MaxValue, ref format, changed.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x00050000), "open audio output");
                for (int i = 0; i < buffers.Length; i++)
                {
                    NativeBuffer buffer = new NativeBuffer(); buffers[i] = buffer;
                    buffer.Data = Marshal.AllocHGlobal(FramesPerBlock * 2);
                    buffer.Header = Marshal.AllocHGlobal(headerSize);
                    WaveHeader header = new WaveHeader { Data = buffer.Data, BufferLength = FramesPerBlock * 2 };
                    Marshal.StructureToPtr(header, buffer.Header, false);
                    Check(NativeAudio.waveOutPrepareHeader(device, buffer.Header, (uint)headerSize), "prepare audio buffer");
                    buffer.Prepared = true;
                }
                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Desktop pet PCM output", Priority = ThreadPriority.AboveNormal };
                worker.Start();
            }
            catch { try { Dispose(); } catch { } throw; }
        }

        public double Volume
        {
            get { lock (gate) return volume; }
            set { lock (gate) { ThrowIfDisposed(); volume = ClampVolume(value); } }
        }
        public bool IsPlaying { get { lock (gate) return current != null; } }
        public PlaybackHandle Current { get { lock (gate) return current; } }
        public double PositionSeconds
        {
            get
            {
                lock (gate)
                {
                    if (disposed || device == IntPtr.Zero) return 0;
                    MultimediaTime time = new MultimediaTime { Type = 2 };
                    if (NativeAudio.waveOutGetPosition(device, ref time, 12) != 0) return 0;
                    return time.Type == 2 ? time.Value / 44100.0 : time.Type == 4 ? time.Value / 88200.0 : time.Type == 1 ? time.Value / 1000.0 : 0;
                }
            }
        }

        public PlaybackHandle Play(WaveClip clip, double gain)
        {
            if (clip == null) throw new ArgumentNullException("clip");
            lock (gate)
            {
                ThrowIfDisposed();
                StopInternal();
                volume = appliedGain = ClampVolume(gain);
                current = new PlaybackHandle { Id = ++sequence, Clip = clip };
                nextFrame = 0;
                try
                {
                    Check(NativeAudio.waveOutPause(device), "pause output while filling initial buffers");
                    foreach (NativeBuffer buffer in buffers) FillAndQueue(buffer);
                    current.StartedUtc = DateTime.UtcNow;
                    current.StartedTimestamp = Stopwatch.GetTimestamp();
                    Check(NativeAudio.waveOutRestart(device), "start audio output");
                    return current;
                }
                catch { StopInternal(); throw; }
            }
        }

        public void Stop()
        {
            lock (gate) { if (!disposed) StopInternal(); }
        }

        private void StopInternal()
        {
            if (device != IntPtr.Zero && (current != null || queued != 0)) Check(NativeAudio.waveOutReset(device), "stop audio output");
            if (current != null) { current.stopped = true; current.completed = true; current = null; }
            queued = 0; nextFrame = 0;
            foreach (NativeBuffer buffer in buffers) if (buffer != null) buffer.Queued = false;
        }

        private void FillAndQueue(NativeBuffer buffer)
        {
            if (current == null || buffer.Queued || nextFrame >= current.Clip.Samples.Length) return;
            short[] samples = current.Clip.Samples;
            int count = Math.Min(FramesPerBlock, samples.Length - nextFrame);
            double target = volume, initial = appliedGain;
            int ramp = Math.Min(88, count);
            for (int i = 0; i < count; i++)
            {
                double gain = i < ramp ? initial + (target - initial) * ((i + 1) / (double)ramp) : target;
                buffer.Work[i] = (short)Math.Round(samples[nextFrame + i] * gain);
            }
            appliedGain = target;
            Marshal.Copy(buffer.Work, 0, buffer.Data, count);
            // Never add zeros to the final partial block; only its actual PCM bytes are submitted.
            Marshal.WriteInt32(buffer.Header, lengthOffset, count * 2);
            Check(NativeAudio.waveOutWrite(device, buffer.Header, (uint)headerSize), "submit audio buffer");
            buffer.Queued = true; queued++; nextFrame += count;
        }

        private void WorkerLoop()
        {
            while (!disposed)
            {
                changed.WaitOne();
                PlaybackHandle completed = null, failed = null;
                Exception failure = null;
                lock (gate)
                {
                    if (disposed) return;
                    try
                    {
                        foreach (NativeBuffer buffer in buffers)
                        {
                            if (buffer.Queued && (Marshal.ReadInt32(buffer.Header, flagsOffset) & 1) != 0)
                            { buffer.Queued = false; queued--; }
                        }
                        if (current != null)
                        {
                            foreach (NativeBuffer buffer in buffers) FillAndQueue(buffer);
                            if (queued == 0 && nextFrame >= current.Clip.Samples.Length)
                            { completed = current; completed.completed = true; current = null; }
                        }
                    }
                    catch (Exception error)
                    {
                        failed = current; failure = error;
                        try { StopInternal(); } catch (Exception stopError) { failure = new AggregateException(error, stopError); }
                    }
                }
                if (completed != null)
                {
                    Action<PlaybackHandle> handler = Ended;
                    if (handler != null) try { handler(completed); } catch (Exception error) { Debug.WriteLine(error); }
                }
                if (failed != null)
                {
                    Action<PlaybackHandle, Exception> handler = Failed;
                    if (handler != null) try { handler(failed, failure); } catch (Exception error) { Debug.WriteLine(error); }
                }
            }
        }

        public void Dispose()
        {
            Exception failure = null;
            bool keepAlive = false;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (device != IntPtr.Zero)
                {
                    uint reset = NativeAudio.waveOutReset(device);
                    if (reset != 0) failure = Error(reset, "reset audio output during shutdown");
                    if (current != null) { current.stopped = true; current.completed = true; current = null; }
                    foreach (NativeBuffer buffer in buffers)
                    {
                        if (buffer == null || !buffer.Prepared) continue;
                        uint result = NativeAudio.waveOutUnprepareHeader(device, buffer.Header, (uint)headerSize);
                        if (result != 0) { failure = Error(result, "release audio buffer"); keepAlive = true; }
                        else buffer.Prepared = false;
                    }
                    uint closed = NativeAudio.waveOutClose(device);
                    if (closed != 0) { failure = Error(closed, "close audio output"); keepAlive = true; }
                    else device = IntPtr.Zero;
                }
                if (!keepAlive)
                {
                    foreach (NativeBuffer buffer in buffers)
                    {
                        if (buffer == null) continue;
                        if (buffer.Header != IntPtr.Zero) { Marshal.FreeHGlobal(buffer.Header); buffer.Header = IntPtr.Zero; }
                        if (buffer.Data != IntPtr.Zero) { Marshal.FreeHGlobal(buffer.Data); buffer.Data = IntPtr.Zero; }
                    }
                }
                changed.Set();
            }
            if (worker != null && worker != Thread.CurrentThread) worker.Join(1000);
            if (keepAlive) lock (quarantined) quarantined.Add(this);
            else changed.Dispose();
            if (failure != null) throw failure;
        }

        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException("WaveOutput"); }
        private static double ClampVolume(double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentOutOfRangeException("volume");
            return Math.Max(0, Math.Min(1, value));
        }
        private static void Check(uint result, string operation) { if (result != 0) throw Error(result, operation); }
        private static Exception Error(uint result, string operation)
        {
            StringBuilder text = new StringBuilder(256);
            NativeAudio.waveOutGetErrorText(result, text, (uint)text.Capacity);
            return new IOException("Cannot " + operation + ": " + text + " (" + result + ")");
        }

        private sealed class NativeBuffer
        {
            internal IntPtr Data, Header;
            internal readonly short[] Work = new short[FramesPerBlock];
            internal bool Prepared, Queued;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort FormatTag, Channels;
            public uint SamplesPerSecond, AverageBytesPerSecond;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength, BytesRecorded;
            public UIntPtr User;
            public uint Flags, Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }
        [StructLayout(LayoutKind.Explicit, Size = 12)]
        private struct MultimediaTime
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(4)] public uint Value;
            [FieldOffset(8)] public uint Extra;
        }
        private static class NativeAudio
        {
            [DllImport("winmm.dll")] internal static extern uint waveOutOpen(out IntPtr output, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
            [DllImport("winmm.dll")] internal static extern uint waveOutPrepareHeader(IntPtr output, IntPtr header, uint size);
            [DllImport("winmm.dll")] internal static extern uint waveOutUnprepareHeader(IntPtr output, IntPtr header, uint size);
            [DllImport("winmm.dll")] internal static extern uint waveOutWrite(IntPtr output, IntPtr header, uint size);
            [DllImport("winmm.dll")] internal static extern uint waveOutReset(IntPtr output);
            [DllImport("winmm.dll")] internal static extern uint waveOutClose(IntPtr output);
            [DllImport("winmm.dll")] internal static extern uint waveOutPause(IntPtr output);
            [DllImport("winmm.dll")] internal static extern uint waveOutRestart(IntPtr output);
            [DllImport("winmm.dll")] internal static extern uint waveOutGetPosition(IntPtr output, ref MultimediaTime position, uint size);
            [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern uint waveOutGetErrorText(uint error, StringBuilder text, uint capacity);
        }
    }
}
