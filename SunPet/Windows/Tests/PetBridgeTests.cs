using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using DesktopPetLink;

internal static class PetBridgeTests
{
    private static BridgeMessage CancellationMessage(string chain, int hop, string kind)
    {
        return new BridgeMessage { Version = 2, Kind = kind, ChainId = chain, Hop = hop, EventId = Guid.NewGuid().ToString("N") };
    }

    private static void TestCancellationCache()
    {
        long now = 0;
        BridgeCancellation cache = new BridgeCancellation(delegate { return now; }, 1000, TimeSpan.FromSeconds(15), 128);
        string chain = Guid.NewGuid().ToString("N"), unrelated = Guid.NewGuid().ToString("N");
        for (int hop = 0; hop <= 3; hop++)
            Check(!cache.IsCancelled(CancellationMessage(chain, hop, "started")), "natural chain hop " + hop + " remains allowed");
        cache.RecordStopped(CancellationMessage(chain, 1, "stopped"));
        Check(cache.IsCancelled(CancellationMessage(chain, 2, "started")), "stop-before-start blocks a delayed hop 2 from another sender queue");
        Check(cache.IsCancelled(CancellationMessage(chain, 1, "started")) && cache.IsCancelled(CancellationMessage(chain, 3, "started")), "cancel applies from its recorded hop through descendants");
        Check(!cache.IsCancelled(CancellationMessage(chain, 0, "started")), "cancel does not rewrite earlier chain stages");
        Check(!cache.IsCancelled(CancellationMessage(unrelated, 2, "started")), "unrelated chains remain playable");
        cache.RecordStopped(CancellationMessage(chain, 3, "stopped"));
        Check(cache.IsCancelled(CancellationMessage(chain, 2, "started")), "later descendant stop cannot weaken an ancestor cancellation");
        cache.RecordStopped(CancellationMessage(chain, 0, "stopped"));
        Check(cache.IsCancelled(CancellationMessage(chain, 0, "started")) && cache.Count == 1, "minimum cancellation hop wins without adding duplicate entries");
        now = 14999;
        Check(cache.IsCancelled(CancellationMessage(chain, 2, "started")), "cancellation remains valid throughout its 15 second lifetime");
        now = 15000;
        Check(!cache.IsCancelled(CancellationMessage(chain, 2, "started")) && cache.Count == 0, "cancellation expires on monotonic time without blocking new playback");
        cache.RecordStopped(CancellationMessage(chain, 1, "stopped"));
        now += 10000;
        cache.RecordStopped(CancellationMessage(chain, 3, "stopped"));
        now += 6000;
        Check(cache.IsCancelled(CancellationMessage(chain, 2, "started")), "a repeated stop refreshes expiry while retaining its minimum hop");
        string earliest = Guid.NewGuid().ToString("N"), latest = null;
        cache = new BridgeCancellation(delegate { return now; }, 1000, TimeSpan.FromSeconds(15), 128);
        cache.RecordStopped(CancellationMessage(earliest, 0, "stopped"));
        for (int i = 0; i < 128; i++)
        {
            now++;
            latest = Guid.NewGuid().ToString("N");
            cache.RecordStopped(CancellationMessage(latest, 0, "stopped"));
        }
        Check(cache.Count == 128 && !cache.IsCancelled(CancellationMessage(earliest, 2, "started")) && cache.IsCancelled(CancellationMessage(latest, 2, "started")), "cache stays bounded at 128 and evicts its oldest expiry");
        cache.RecordStopped(null);
        cache.RecordStopped(CancellationMessage("", 0, "stopped"));
        cache.RecordStopped(CancellationMessage(unrelated, 0, "started"));
        Check(cache.Count == 128, "invalid or non-stop inputs cannot populate the cache");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("OK " + label);
    }

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "listen") return Listen(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "send")
        {
            double duration = Double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            using (PetBridge bridge = new PetBridge("fengge", args[1]))
            {
                bridge.Start();
                Console.WriteLine("peer=" + bridge.IsPeerRunning("sunpet"));
                string id = bridge.AnnounceSpeaking(3, duration);
                Console.WriteLine("started=" + id);
                if (args.Length > 3)
                {
                    Thread.Sleep(Int32.Parse(args[3]));
                    bridge.AnnounceStopped(id);
                    Console.WriteLine("stopped=" + id);
                }
                Thread.Sleep((int)(duration * 1000) + 2000);
            }
            return 0;
        }
        string scope = "test-" + Guid.NewGuid().ToString("N");
        string folder = Path.Combine(Path.GetTempPath(), "PetBridge-" + scope);
        Directory.CreateDirectory(folder);
        Process receiver = null;
        try
        {
            TestCancellationCache();
            Check(!BridgeRules.ShouldReply("fengge", new BridgeMessage { Kind = "started", FromRole = "sunpet", RootRole = "fengge", Hop = 1 }), "FengGe does not extend a FengGe-rooted chain");
            Check(!BridgeRules.ShouldReply("sunpet", new BridgeMessage { Kind = "started", FromRole = "fengge", RootRole = "shaolan", Hop = 3 }), "hop limit rejects further replies");
            Check(!BridgeRules.ShouldReply("shaolan", new BridgeMessage { Kind = "started", FromRole = "sunpet", RootRole = "shaolan", Hop = 1 }), "Shaolan never replies");
            using (PetBridge fresh = new PetBridge("fengge", scope + "-legacy"))
            {
                string legacyName = "Local\\DesktopPetLink.v1." + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value + "." + Process.GetCurrentProcess().SessionId + "." + scope + "-legacy.sunpet.present";
                using (EventWaitHandle legacy = new EventWaitHandle(false, EventResetMode.ManualReset, legacyName))
                    Check(!fresh.IsPeerRunning("sunpet"), "v1 endpoints are isolated from v2");
            }
            ProcessStartInfo start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "listen " + scope + " \"" + folder + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            receiver = Process.Start(start);
            using (PetBridge bridge = new PetBridge("fengge", scope))
            {
                bridge.Start();
                DateTime limit = DateTime.UtcNow.AddSeconds(5);
                while (!bridge.IsPeerRunning("sunpet") && DateTime.UtcNow < limit) Thread.Sleep(20);
                Check(bridge.IsPeerRunning("sunpet"), "independent receiver process is present");
                bool probes = true;
                for (int i = 0; i < 30; i++) probes &= bridge.IsPeerRunning("sunpet");
                Check(probes, "30 presence probes leave the receiving pipe available");
                using (PetBridge isolated = new PetBridge("fengge", scope + "-other"))
                    Check(!isolated.IsPeerRunning("sunpet"), "test scopes do not share events or presence");
                string id = bridge.AnnounceSpeaking(7, 1.23);
                while (!File.Exists(Path.Combine(folder, "started")) && DateTime.UtcNow < limit) Thread.Sleep(20);
                Check(File.Exists(Path.Combine(folder, "started")), "started event crossed process boundary");
                bridge.AnnounceStopped(id);
                bridge.AnnounceStopped(id);
                Check(receiver.WaitForExit(6000), "receiver exits without blocked pipe threads");
                string output = receiver.StandardOutput.ReadToEnd();
                Console.Write(output);
                Check(receiver.ExitCode == 0, "event order, identity and duplicate suppression");
                Check(!bridge.IsPeerRunning("sunpet"), "presence disappears when receiver exits");
                Stopwatch timer = Stopwatch.StartNew();
                bridge.AnnounceSpeaking(1, .8);
                Check(timer.ElapsedMilliseconds < 100, "absent peer never blocks playback thread");
            }
            receiver.Dispose();
            File.Delete(Path.Combine(folder, "started"));
            receiver = Process.Start(start);
            PetBridge closingBridge = new PetBridge("fengge", scope);
            try
            {
                closingBridge.Start();
                DateTime limit = DateTime.UtcNow.AddSeconds(5);
                while (!closingBridge.IsPeerRunning("sunpet") && DateTime.UtcNow < limit) Thread.Sleep(20);
                if (!closingBridge.IsPeerRunning("sunpet"))
                {
                    Console.Error.WriteLine("Second receiver exited=" + receiver.HasExited);
                    if (receiver.HasExited) Console.Error.WriteLine(receiver.StandardError.ReadToEnd());
                }
                Check(closingBridge.IsPeerRunning("sunpet"), "second receiver present");
                string id = closingBridge.AnnounceSpeaking(7, 1.23);
                while (!File.Exists(Path.Combine(folder, "started")) && DateTime.UtcNow < limit) Thread.Sleep(20);
                Check(File.Exists(Path.Combine(folder, "started")), "receiver observed playback before sender closes");
                closingBridge.AnnounceStopped(id);
                closingBridge.Dispose();
                Check(receiver.WaitForExit(6000) && receiver.ExitCode == 0, "immediate Dispose flushes the queued stop event");
            }
            finally { closingBridge.Dispose(); }
            Console.WriteLine("PASS");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
        finally
        {
            if (receiver != null)
            {
                if (!receiver.HasExited) receiver.Kill();
                receiver.Dispose();
            }
            // This uniquely named folder belongs exclusively to this test invocation.
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static int Listen(string scope, string folder)
    {
        int received = 0;
        string id = null;
        string error = null;
        using (ManualResetEvent done = new ManualResetEvent(false))
        using (PetBridge bridge = new PetBridge("sunpet", scope))
        {
            bridge.MessageReceived += delegate(BridgeMessage message)
            {
                if (Interlocked.Increment(ref received) == 1)
                {
                    if (message.Kind != "started" || message.FromRole != "fengge" || message.ClipId != 7 || Math.Abs(message.Duration - 1.23) > .001)
                        error = "Invalid started event";
                    id = message.EventId;
                    File.WriteAllText(Path.Combine(folder, "started"), id);
                }
                else
                {
                    if (received != 2 || message.Kind != "stopped" || message.EventId != id) error = "Order or duplicate failure";
                    done.Set();
                }
            };
            bridge.Start();
            if (!done.WaitOne(8000)) error = "No stopped event";
            Thread.Sleep(300); // Allow the deliberately duplicated stop to arrive.
            if (error != null) { Console.Error.WriteLine(error); return 1; }
            Check(received == 2, "exactly one started and one stopped event received");
            return 0;
        }
    }
}
