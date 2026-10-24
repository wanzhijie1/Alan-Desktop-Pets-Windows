using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Restricted test harness: capture only this new process and the three pets it starts.
// No microphone, default-endpoint capture, arbitrary PID, or exclude-tree mode exists here.
internal static class AudioProbe
{
    [MTAThread]
    private static int Main(string[] args)
    {
        List<Child> children = new List<Child>();
        try
        {
            if (IntPtr.Size != 8) throw new InvalidOperationException("Build this diagnostic as x64.");
            using (ProcessCapture capture = new ProcessCapture((uint)Process.GetCurrentProcess().Id))
            {
                if (Array.IndexOf(args, "--check") >= 0)
                {
                    Console.WriteLine("PASS process-tree WASAPI activation and initialization; no recording started.");
                    return 0;
                }
                string output = Path.GetFullPath(Option(args, "--output", null));
                int seconds = Int32.Parse(Option(args, "--seconds", "14"), CultureInfo.InvariantCulture);
                int clip = Int32.Parse(Option(args, "--clip", "7"), CultureInfo.InvariantCulture);
                if (seconds < 5 || seconds > 60 || clip < 1 || clip > 37) throw new ArgumentException("Invalid capture seconds or Shaolan clip.");
                string sunpet = PetPath(args, "--sunpet", "SunPet.exe");
                string fengge = PetPath(args, "--fengge", "GoodThingPet.exe");
                string shaolan = PetPath(args, "--shaolan", "ShaolanPet.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                string scope = "audio-probe-" + Guid.NewGuid().ToString("N");
                string common = "--bridge-test " + scope + " --audible-test --trace --test-exit-after " + (seconds - 1);
                string logPrefix = Path.Combine(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output));
                capture.Begin(output);
                Console.WriteLine("Capture target: only newly launched process tree PID " + Process.GetCurrentProcess().Id);
                children.Add(new Child(sunpet, common, logPrefix + ".sunpet.log"));
                children.Add(new Child(fengge, common, logPrefix + ".fengge.log"));
                // Start peers first so the root clip does not race their bridge startup.
                Thread.Sleep(350);
                children.Add(new Child(shaolan, common + " --test-play " + clip, logPrefix + ".shaolan.log"));
                capture.Pump(seconds);
                capture.Finish();
                bool failed = false;
                foreach (Child child in children)
                {
                    child.WaitForExit();
                    if (child.ExitCode != 0) failed = true;
                }
                Console.WriteLine("WAV: " + output);
                Console.WriteLine("Frames=" + capture.Frames + " packets=" + capture.Packets + " nonzero=" + capture.NonZeroSamples + " discontinuities=" + capture.Discontinuities);
                if (capture.NonZeroSamples == 0) throw new InvalidOperationException("Capture contains no audible samples; check test flags and child logs.");
                if (failed) throw new InvalidOperationException("A test child failed; inspect role logs.");
                Console.WriteLine("PASS process-tree audio captured. Measure waveform boundaries separately; logs are not latency measurements.");
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            return 1;
        }
        finally { foreach (Child child in children) child.Dispose(); }
    }

    private static string Option(string[] args, string name, string fallback)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0)
        {
            if (fallback == null) throw new ArgumentException("Required option: " + name);
            return fallback;
        }
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException("Missing value: " + name);
        return args[index + 1];
    }

    private static string PetPath(string[] args, string option, string expectedName)
    {
        string path = Path.GetFullPath(Option(args, option, null));
        if (!File.Exists(path) || !String.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected existing " + expectedName + " for " + option);
        return path;
    }

    private sealed class Child : IDisposable
    {
        private readonly Process process;
        private readonly StreamWriter log;
        private readonly object gate = new object();
        private bool disposed;
        public int ExitCode { get { return process.ExitCode; } }
        public Child(string executable, string arguments, string logPath)
        {
            log = new StreamWriter(logPath, false, new UTF8Encoding(false));
            log.AutoFlush = true;
            ProcessStartInfo start = new ProcessStartInfo(executable, arguments);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.WorkingDirectory = Path.GetDirectoryName(executable);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            process = new Process { StartInfo = start };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { Write(e.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { Write(e.Data == null ? null : "STDERR " + e.Data); };
            if (!process.Start()) throw new InvalidOperationException("Could not start test pet: " + executable);
            Write("PROBE child_pid=" + process.Id + " exe=" + executable + " args=" + arguments);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        private void Write(string line)
        {
            if (line == null) return;
            lock (gate) { if (!disposed) log.WriteLine(line); }
        }
        public void WaitForExit()
        {
            if (!process.WaitForExit(3000))
            {
                process.Kill();
                process.WaitForExit();
                Write("PROBE timed out; this harness child was terminated.");
            }
            else process.WaitForExit(); // Flush asynchronous stdout/stderr events.
        }
        public void Dispose()
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
            lock (gate) { disposed = true; log.Dispose(); }
            process.Dispose();
        }
    }
}

internal sealed class ProcessCapture : IDisposable
{
    private IAudioClient client;
    private IAudioCaptureClient capture;
    private IActivateAudioInterfaceAsyncOperation operation;
    private readonly AutoResetEvent ready = new AutoResetEvent(false);
    private readonly ActivationHandler completion = new ActivationHandler();
    private BinaryWriter writer;
    private StreamWriter packets;
    private string outputPath;
    private bool running;
    private bool havePosition;
    private ulong firstDevicePosition;
    private long firstQpc;
    private DateTime beginUtc;
    private long beginQpc;
    public long Frames { get; private set; }
    public long Packets { get; private set; }
    public long NonZeroSamples { get; private set; }
    public long Discontinuities { get; private set; }
    public long InsertedSilentFrames { get; private set; }

    internal ProcessCapture(uint processId)
    {
        ActivationParams parameters = new ActivationParams { ActivationType = 1, TargetProcessId = processId, ProcessLoopbackMode = 0 };
        IntPtr blob = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ActivationParams)));
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            PropVariant variant = new PropVariant { Type = 65, BlobSize = Marshal.SizeOf(typeof(ActivationParams)), BlobData = blob };
            Guid iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, ref variant, completion, out operation), "ActivateAudioInterfaceAsync");
            if (!completion.Done.WaitOne(10000)) throw new TimeoutException("Process-loopback activation timed out.");
            if (completion.Error != null) throw completion.Error;
            client = (IAudioClient)completion.Result;
            WaveFormat format = new WaveFormat { FormatTag = 1, Channels = 2, SamplesPerSec = 44100, AvgBytesPerSec = 176400, BlockAlign = 4, BitsPerSample = 16, ExtraSize = 0 };
            Check(client.Initialize(0, 0x80060000, 0, 0, ref format, IntPtr.Zero), "IAudioClient.Initialize process loopback");
            Guid captureId = typeof(IAudioCaptureClient).GUID;
            object service;
            Check(client.GetService(ref captureId, out service), "IAudioClient.GetService");
            capture = (IAudioCaptureClient)service;
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    internal void Begin(string path)
    {
        outputPath = path;
        writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read));
        WriteHeader(0);
        packets = new StreamWriter(path + ".packets.csv", false, new UTF8Encoding(false));
        packets.WriteLine("packet,output_frame,frames,device_position,qpc_100ns,flags");
        beginUtc = DateTime.UtcNow;
        beginQpc = Stopwatch.GetTimestamp();
        Check(client.Start(), "IAudioClient.Start");
        running = true;
    }

    internal void Pump(int seconds)
    {
        Stopwatch deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < seconds)
        {
            ready.WaitOne(50);
            Drain();
        }
    }

    private void Drain()
    {
        uint available;
        Check(capture.GetNextPacketSize(out available), "GetNextPacketSize");
        while (available > 0)
        {
            IntPtr data;
            uint frames, flags;
            ulong devicePosition, qpc;
            Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpc), "GetBuffer");
            try
            {
                if (!havePosition)
                {
                    firstDevicePosition = devicePosition;
                    firstQpc = (long)qpc;
                    havePosition = true;
                }
                else if ((flags & 1) != 0) Discontinuities++;
                // Process-loopback currently reports devicePosition=0. QPC still identifies
                // the first sample of each packet; retain real silent gaps between epochs.
                long desiredFrame = (long)Math.Round(((long)qpc - firstQpc) * (44100.0 / 10000000.0));
                if ((flags & 4) == 0 && desiredFrame > Frames + 88)
                {
                    long gap = desiredFrame - Frames;
                    if (gap > 44100 * 60) throw new InvalidDataException("Unexpected process-capture timestamp jump.");
                    WriteSilence(gap);
                    InsertedSilentFrames += gap;
                }
                packets.WriteLine(Packets + "," + Frames + "," + frames + "," + devicePosition + "," + qpc + "," + flags);
                if ((flags & 2) != 0 || data == IntPtr.Zero) WriteSilence(frames);
                else
                {
                    byte[] bytes = new byte[checked((int)frames * 4)];
                    Marshal.Copy(data, bytes, 0, bytes.Length);
                    for (int index = 0; index < bytes.Length; index += 2)
                        if (bytes[index] != 0 || bytes[index + 1] != 0) NonZeroSamples++;
                    writer.Write(bytes);
                    Frames += frames;
                }
                Packets++;
            }
            finally { Check(capture.ReleaseBuffer(frames), "ReleaseBuffer"); }
            Check(capture.GetNextPacketSize(out available), "GetNextPacketSize");
        }
    }

    private void WriteSilence(long frames)
    {
        byte[] silence = new byte[16384];
        long bytes = frames * 4;
        while (bytes > 0)
        {
            int count = (int)Math.Min(bytes, silence.Length);
            writer.Write(silence, 0, count);
            bytes -= count;
        }
        Frames += frames;
    }

    private void WriteHeader(uint dataBytes)
    {
        writer.Seek(0, SeekOrigin.Begin);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(dataBytes + 36);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)2); writer.Write(44100); writer.Write(176400);
        writer.Write((ushort)4); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataBytes);
    }

    internal void Finish()
    {
        if (running) { Check(client.Stop(), "IAudioClient.Stop"); running = false; Drain(); }
        if (writer != null)
        {
            WriteHeader(checked((uint)(Frames * 4)));
            writer.Dispose(); writer = null;
        }
        if (packets != null) { packets.Dispose(); packets = null; }
        if (outputPath != null)
        {
            DateTime firstSampleUtc = beginUtc.AddSeconds(firstQpc / 10000000.0 - beginQpc / (double)Stopwatch.Frequency);
            string metadata = "capture=process_tree_include_only\r\npid=" + Process.GetCurrentProcess().Id +
                "\r\nsample_rate=44100\r\nchannels=2\r\nbits=16\r\nframes=" + Frames +
                "\r\npackets=" + Packets + "\r\nnonzero_samples=" + NonZeroSamples +
                "\r\ndiscontinuities=" + Discontinuities + "\r\ninserted_silent_frames=" + InsertedSilentFrames +
                "\r\nbegin_utc=" + beginUtc.ToString("O") + "\r\nfirst_sample_utc=" + firstSampleUtc.ToString("O") +
                "\r\nfirst_qpc_100ns=" + firstQpc + "\r\nstopwatch_frequency=" + Stopwatch.Frequency + "\r\n";
            File.WriteAllText(outputPath + ".meta.txt", metadata, new UTF8Encoding(false));
            outputPath = null;
        }
    }

    public void Dispose()
    {
        try { Finish(); } catch { }
        if (capture != null) { Marshal.ReleaseComObject(capture); capture = null; }
        if (client != null) { Marshal.ReleaseComObject(client); client = null; }
        if (operation != null) { Marshal.ReleaseComObject(operation); operation = null; }
        ready.Dispose(); completion.Done.Dispose();
        GC.KeepAlive(completion);
    }

    internal static void Check(int hr, string action)
    {
        if (hr < 0) throw new COMException(action + " failed: 0x" + hr.ToString("X8"), hr);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid riid, ref PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ActivationParams { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public int BlobSize; [FieldOffset(16)] public IntPtr BlobData; }
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    public ushort FormatTag, Channels;
    public uint SamplesPerSec, AvgBytesPerSec;
    public ushort BlockAlign, BitsPerSample, ExtraSize;
}

[ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}
[ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileObject { }
[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    internal readonly ManualResetEvent Done = new ManualResetEvent(false);
    internal object Result;
    internal Exception Error;
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        try
        {
            int status; object result;
            ProcessCapture.Check(operation.GetActivateResult(out status, out result), "GetActivateResult");
            ProcessCapture.Check(status, "Process-loopback activation");
            Result = result;
        }
        catch (Exception ex) { Error = ex; }
        finally { Done.Set(); }
        return 0;
    }
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormat format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormat format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}
[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
