using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

// This exact file is shared by all three Windows desktop pets.
// It transports local events only. Reply selection and timing belong to the UI.
namespace DesktopPetLink
{
    [DataContract]
    public sealed class BridgeMessage
    {
        [DataMember] public int Version;
        [DataMember] public string Kind;
        [DataMember] public string FromRole;
        [DataMember] public string EventId;
        [DataMember] public int ClipId;
        [DataMember] public double Duration;
        [DataMember] public DateTime StartedUtc;
        [DataMember(EmitDefaultValue = false)] public long StartedQpc;
        [DataMember] public int Hop;
        [DataMember] public string RootRole;
        [DataMember] public string ChainId;
        [DataMember] public string ParentEventId;
    }

    // Remember cancellation independently of message arrival order. Different pets have independent
    // send queues, so an ancestor's stop can reach us before a descendant's delayed start.
    public sealed class BridgeCancellation
    {
        private sealed class Entry { internal int MinimumHop; internal long Expires; }
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> cancelled = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Func<long> timestamp;
        private readonly long lifetimeTicks;
        private readonly int capacity;

        public BridgeCancellation() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency, TimeSpan.FromSeconds(15), 128) { }

        // An injected monotonic clock makes expiry and capacity tests deterministic, without sleeps.
        internal BridgeCancellation(Func<long> timestamp, long frequency, TimeSpan lifetime, int capacity)
        {
            if (timestamp == null) throw new ArgumentNullException("timestamp");
            if (frequency <= 0 || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException("lifetime");
            if (capacity < 1 || capacity > 128) throw new ArgumentOutOfRangeException("capacity");
            this.timestamp = timestamp;
            lifetimeTicks = Math.Max(1, (long)Math.Ceiling(lifetime.TotalSeconds * frequency));
            this.capacity = capacity;
        }

        public void RecordStopped(BridgeMessage message)
        {
            if (message == null || message.Kind != "stopped" || String.IsNullOrEmpty(message.ChainId) || message.ChainId.Length > 128 || message.Hop < 0 || message.Hop > 3) return;
            lock (gate)
            {
                long now = timestamp();
                Prune(now);
                Entry entry;
                if (cancelled.TryGetValue(message.ChainId, out entry))
                {
                    entry.MinimumHop = Math.Min(entry.MinimumHop, message.Hop);
                    entry.Expires = now + lifetimeTicks;
                    return;
                }
                if (cancelled.Count >= capacity)
                {
                    string oldest = null;
                    long expiry = Int64.MaxValue;
                    foreach (KeyValuePair<string, Entry> pair in cancelled)
                        if (oldest == null || pair.Value.Expires < expiry) { oldest = pair.Key; expiry = pair.Value.Expires; }
                    cancelled.Remove(oldest);
                }
                cancelled.Add(message.ChainId, new Entry { MinimumHop = message.Hop, Expires = now + lifetimeTicks });
            }
        }

        public bool IsCancelled(BridgeMessage message)
        {
            if (message == null || message.Kind != "started" || String.IsNullOrEmpty(message.ChainId)) return false;
            lock (gate)
            {
                Prune(timestamp());
                Entry entry;
                return cancelled.TryGetValue(message.ChainId, out entry) && message.Hop >= entry.MinimumHop;
            }
        }

        public int Count { get { lock (gate) { Prune(timestamp()); return cancelled.Count; } } }

        private void Prune(long now)
        {
            List<string> expired = null;
            foreach (KeyValuePair<string, Entry> pair in cancelled)
            {
                if (pair.Value.Expires > now) continue;
                if (expired == null) expired = new List<string>();
                expired.Add(pair.Key);
            }
            if (expired != null) foreach (string chain in expired) cancelled.Remove(chain);
        }
    }

    public static class BridgeRules
    {
        // QPC is shared by processes on this machine and is immune to clock corrections.
        // Older v2 messages retain the UTC fallback.
        public static double Elapsed(BridgeMessage message)
        {
            if (message.StartedQpc > 0)
            {
                double elapsed = (Stopwatch.GetTimestamp() - message.StartedQpc) / (double)Stopwatch.Frequency;
                if (elapsed >= -0.1 && elapsed < 30) return Math.Max(0, elapsed);
            }
            return Math.Max(0, (DateTime.UtcNow - message.StartedUtc).TotalSeconds);
        }

        public static bool ShouldReply(string role, BridgeMessage message)
        {
            if (message == null || message.Kind != "started" || message.Hop < 0 || message.Hop >= 3) return false;
            if (role == "sunpet")
                return (message.FromRole == "shaolan" && message.Hop == 0 && message.RootRole == "shaolan") ||
                    (message.FromRole == "fengge" && message.Hop == 0 && message.RootRole == "fengge") ||
                    (message.FromRole == "fengge" && message.Hop == 2 && message.RootRole == "shaolan");
            return role == "fengge" && message.FromRole == "sunpet" && message.Hop == 1 && message.RootRole == "shaolan";
        }
    }

    public sealed class PetBridge : IDisposable
    {
        private readonly string role;
        private readonly string prefix;
        private readonly SecurityIdentifier user;
        private readonly object gate = new object();
        private readonly Queue<BridgeMessage> outgoing = new Queue<BridgeMessage>();
        private readonly AutoResetEvent sendReady = new AutoResetEvent(false);
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly Queue<string> seenOrder = new Queue<string>();
        private NamedPipeServerStream server;
        private IAsyncResult waitingConnection;
        private EventWaitHandle presence;
        private Thread listener, sender;
        private bool started;
        private volatile bool stopping;
        private BridgeMessage currentEvent;
        private BridgeMessage lastStopped;
        private readonly Dictionary<string, BridgeMessage> ownEvents = new Dictionary<string, BridgeMessage>();
        private readonly Queue<string> ownEventOrder = new Queue<string>();
        public event Action<BridgeMessage> MessageReceived;

        public PetBridge(string role, string scope = "")
        {
            if (!ValidRole(role)) throw new ArgumentException("Unknown desktop pet role", "role");
            if (scope == null) scope = "";
            foreach (char c in scope)
                if (!Char.IsLetterOrDigit(c) && c != '-') throw new ArgumentException("Invalid bridge scope", "scope");
            if (scope.Length > 64) throw new ArgumentException("Bridge scope is too long", "scope");
            this.role = role;
            user = WindowsIdentity.GetCurrent().User;
            prefix = "DesktopPetLink.v2." + user.Value + "." + Process.GetCurrentProcess().SessionId + "." + (scope.Length == 0 ? "normal" : scope);
        }

        private static bool ValidRole(string value) { return value == "sunpet" || value == "fengge" || value == "shaolan"; }
        private string PipeName(string endpointRole) { return prefix + "." + endpointRole; }
        private string PresenceName(string endpointRole) { return "Local\\" + PipeName(endpointRole) + ".present"; }

        public void Start()
        {
            lock (gate)
            {
                if (stopping) throw new ObjectDisposedException("PetBridge");
                if (started) return;
                // Create the receiving endpoint before advertising availability.
                server = CreateServer();
                waitingConnection = server.BeginWaitForConnection(null, null);
                EventWaitHandleSecurity access = new EventWaitHandleSecurity();
                access.SetAccessRuleProtection(true, false);
                access.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
                bool created;
                presence = new EventWaitHandle(false, EventResetMode.ManualReset, PresenceName(role), out created, access);
                if (!created)
                {
                    presence.Dispose(); presence = null;
                    server.Dispose(); server = null;
                    throw new InvalidOperationException("This desktop pet bridge is already running.");
                }
                started = true;
                listener = new Thread(Listen) { IsBackground = true, Name = "Desktop pet receive" };
                sender = new Thread(SendLoop) { IsBackground = true, Name = "Desktop pet send" };
                listener.Start(); sender.Start();
            }
        }

        public bool IsPeerRunning(string peerRole)
        {
            if (!ValidRole(peerRole) || peerRole == role) return false;
            try
            {
                using (EventWaitHandle handle = EventWaitHandle.OpenExisting(PresenceName(peerRole), EventWaitHandleRights.Synchronize))
                    return true;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public string AnnounceSpeaking(int clipId, double duration, long startedQpc = 0, DateTime? startedUtc = null)
        {
            return Announce(clipId, duration, null, startedQpc, startedUtc);
        }

        public string AnnounceReply(int clipId, double duration, BridgeMessage parent, long startedQpc = 0, DateTime? startedUtc = null)
        {
            if (!BridgeRules.ShouldReply(role, parent)) throw new ArgumentException("This reply would exceed or violate the bounded desktop pet chain.", "parent");
            return Announce(clipId, duration, parent, startedQpc, startedUtc);
        }

        private string Announce(int clipId, double duration, BridgeMessage parent, long startedQpc, DateTime? startedUtc)
        {
            if (clipId < 1 || Double.IsNaN(duration) || Double.IsInfinity(duration) || duration <= 0 || duration >= 30)
                throw new ArgumentOutOfRangeException("duration", "A clip must have a valid id and duration below 30 seconds.");
            string id = Guid.NewGuid().ToString("N");
            BridgeMessage message = new BridgeMessage { Version = 2, Kind = "started", FromRole = role, EventId = id,
                ClipId = clipId, Duration = duration, StartedQpc = startedQpc, StartedUtc = startedUtc ?? DateTime.UtcNow, Hop = parent == null ? 0 : parent.Hop + 1,
                RootRole = parent == null ? role : parent.RootRole, ChainId = parent == null ? id : parent.ChainId,
                ParentEventId = parent == null ? null : parent.EventId };
            lock (gate)
            {
                currentEvent = message; lastStopped = null;
                ownEvents[id] = message; ownEventOrder.Enqueue(id);
                if (ownEventOrder.Count > 64) ownEvents.Remove(ownEventOrder.Dequeue());
            }
            Enqueue(message);
            return id;
        }

        public void AnnounceStopped(string eventId)
        {
            BridgeMessage original;
            if (String.IsNullOrEmpty(eventId)) return;
            lock (gate)
            {
                if (!ownEvents.TryGetValue(eventId, out original)) return;
                if (currentEvent != null && currentEvent.EventId == eventId) currentEvent = null;
                lastStopped = original;
            }
            Enqueue(StopMessage(original));
        }

        private BridgeMessage StopMessage(BridgeMessage original)
        {
            return new BridgeMessage { Version = 2, Kind = "stopped", FromRole = role, EventId = original.EventId,
                StartedUtc = DateTime.UtcNow, Hop = original.Hop, RootRole = original.RootRole,
                ChainId = original.ChainId, ParentEventId = original.ParentEventId };
        }

        private void Enqueue(BridgeMessage message)
        {
            lock (gate)
            {
                if (!started || stopping) return;
                while (outgoing.Count >= 32) outgoing.Dequeue();
                outgoing.Enqueue(message);
                sendReady.Set();
            }
        }

        private NamedPipeServerStream CreateServer()
        {
            PipeSecurity access = new PipeSecurity();
            access.SetAccessRuleProtection(true, false);
            access.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            access.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(PipeName(role), PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, access);
        }

        private void Listen()
        {
            while (!stopping)
            {
                NamedPipeServerStream active;
                IAsyncResult waiting;
                lock (gate) { active = server; waiting = waitingConnection; }
                if (active == null) return;
                try
                {
                    active.EndWaitForConnection(waiting);
                    byte[] lengthBytes = ReadExact(active, 4);
                    int length = BitConverter.ToInt32(lengthBytes, 0);
                    if (length < 1 || length > 2048) throw new InvalidDataException("Invalid event size");
                    byte[] bytes = ReadExact(active, length);
                    BridgeMessage message;
                    using (MemoryStream stream = new MemoryStream(bytes))
                        message = (BridgeMessage)new DataContractJsonSerializer(typeof(BridgeMessage)).ReadObject(stream);
                    if (Validate(message))
                    {
                        string key = message.EventId + "/" + message.Kind;
                        if (seen.Add(key))
                        {
                            seenOrder.Enqueue(key);
                            if (seenOrder.Count > 256) seen.Remove(seenOrder.Dequeue());
                            Action<BridgeMessage> callback = MessageReceived;
                            if (callback != null && !stopping)
                            {
                                try { callback(message); }
                                catch (Exception error) { Debug.WriteLine(error); }
                            }
                        }
                    }
                }
                catch (Exception error) { if (!stopping) Debug.WriteLine(error); }
                finally { active.Dispose(); }
                lock (gate)
                {
                    if (stopping) return;
                    try { server = CreateServer(); waitingConnection = server.BeginWaitForConnection(null, null); }
                    catch (Exception error)
                    {
                        Debug.WriteLine(error);
                        if (presence != null) { presence.Dispose(); presence = null; }
                        server = null;
                        return;
                    }
                }
            }
        }

        private bool Validate(BridgeMessage message)
        {
            if (message == null || message.Version != 2 || !ValidRole(message.FromRole) || message.FromRole == role ||
                (message.Kind != "started" && message.Kind != "stopped") || message.EventId == null || message.EventId.Length != 32)
                return false;
            Guid id;
            if (!Guid.TryParseExact(message.EventId, "N", out id)) return false;
            if (!Guid.TryParseExact(message.ChainId, "N", out id) || !ValidRole(message.RootRole) || message.Hop < 0 || message.Hop > 3) return false;
            if (message.Hop == 0)
            {
                if (message.RootRole != message.FromRole || message.ChainId != message.EventId || !String.IsNullOrEmpty(message.ParentEventId)) return false;
            }
            else
            {
                if (!Guid.TryParseExact(message.ParentEventId, "N", out id)) return false;
                if (message.Hop == 1 && (message.FromRole != "sunpet" || (message.RootRole != "shaolan" && message.RootRole != "fengge"))) return false;
                if (message.Hop == 2 && (message.FromRole != "fengge" || message.RootRole != "shaolan")) return false;
                if (message.Hop == 3 && (message.FromRole != "sunpet" || message.RootRole != "shaolan")) return false;
            }
            double age = (DateTime.UtcNow - message.StartedUtc.ToUniversalTime()).TotalSeconds;
            if (age < -2 || age > 10) return false;
            if (Double.IsNaN(message.Duration) || Double.IsInfinity(message.Duration)) return false;
            return message.Kind == "stopped" || (message.ClipId > 0 && message.Duration > 0 && message.Duration < 30);
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                IAsyncResult result = stream.BeginRead(buffer, offset, count - offset, null, null);
                using (WaitHandle ready = result.AsyncWaitHandle)
                {
                    if (!ready.WaitOne(500))
                    {
                        // Closing the pipe cancels the pending overlapped read before its wait handle is released.
                        stream.Dispose();
                        try { stream.EndRead(result); } catch (Exception) { }
                        throw new TimeoutException("Timed out reading a local event");
                    }
                    int read = stream.EndRead(result);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
            }
            return buffer;
        }

        private void SendLoop()
        {
            while (true)
            {
                BridgeMessage message = null;
                lock (gate)
                {
                    if (outgoing.Count > 0) message = outgoing.Dequeue();
                    else if (stopping) return;
                }
                if (message == null) { sendReady.WaitOne(); continue; }
                foreach (string peer in new string[] { "shaolan", "sunpet", "fengge" })
                {
                if (peer == role || !IsPeerRunning(peer)) continue;
                try
                {
                    byte[] payload;
                    using (MemoryStream stream = new MemoryStream())
                    {
                        new DataContractJsonSerializer(typeof(BridgeMessage)).WriteObject(stream, message);
                        payload = stream.ToArray();
                    }
                    byte[] framed = new byte[4 + payload.Length];
                    Array.Copy(BitConverter.GetBytes(payload.Length), framed, 4);
                    Array.Copy(payload, 0, framed, 4, payload.Length);
                    using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeName(peer), PipeDirection.Out, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                    {
                        pipe.Connect(150);
                        IAsyncResult result = pipe.BeginWrite(framed, 0, framed.Length, null, null);
                        using (WaitHandle ready = result.AsyncWaitHandle)
                        {
                            if (!ready.WaitOne(500))
                            {
                                pipe.Dispose();
                                try { pipe.EndWrite(result); } catch (Exception) { }
                                throw new TimeoutException("Timed out sending a local event");
                            }
                            pipe.EndWrite(result);
                        }
                    }
                }
                catch (Exception error) { Debug.WriteLine(error); }
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (stopping) return;
                stopping = true;
                outgoing.Clear();
                BridgeMessage finalEvent = currentEvent ?? lastStopped;
                if (finalEvent != null) outgoing.Enqueue(StopMessage(finalEvent));
                currentEvent = null;
                if (presence != null) { presence.Dispose(); presence = null; }
                if (server != null) { server.Dispose(); server = null; }
                sendReady.Set();
            }
            if (listener != null && listener != Thread.CurrentThread) listener.Join(700);
            if (sender != null && sender != Thread.CurrentThread) sender.Join(2800);
            // A blocked native call owns the wait handle until its background thread exits.
            if (sender == null || !sender.IsAlive) sendReady.Dispose();
        }
    }
}
