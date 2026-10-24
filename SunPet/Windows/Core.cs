using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Media.Imaging;

namespace SunPet
{
    [DataContract]
    public sealed class Clip
    {
        [DataMember(Name = "id", IsRequired = true)] public int Id;
        [DataMember(Name = "label", IsRequired = true)] public string Label;
        [DataMember(Name = "file", IsRequired = true)] public string File;
        [DataMember(Name = "duration", IsRequired = true)] public double Duration;
    }

    public sealed class AssetCatalog
    {
        public Clip[] Clips { get; private set; }
        public string DirectoryPath { get; private set; }
        public string ImagePath { get; private set; }

        public static AssetCatalog Load(string assetsDirectory)
        {
            string directory = Path.GetFullPath(assetsDirectory);
            Clip[] clips;
            using (FileStream stream = System.IO.File.OpenRead(Path.Combine(directory, "clips.json")))
                clips = (Clip[])new DataContractJsonSerializer(typeof(Clip[])).ReadObject(stream);
            if (clips == null || clips.Length == 0)
                throw new InvalidDataException("语音列表为空。");
            HashSet<int> ids = new HashSet<int>();
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Clip clip in clips)
            {
                if (clip == null || clip.Id <= 0 || !ids.Add(clip.Id) || String.IsNullOrWhiteSpace(clip.Label))
                    throw new InvalidDataException("语音编号或字幕无效。");
                if (String.IsNullOrWhiteSpace(clip.File) ||
                    clip.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    !String.Equals(Path.GetExtension(clip.File), ".m4a", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(clip.File) != clip.File || !files.Add(clip.File))
                    throw new InvalidDataException("语音文件名无效：" + clip.File);
                ValidateM4a(Path.Combine(directory, "Clips", clip.File));
                // This is the supplied caption duration, not an independently decoded duration.
                // Actual audio decoding is exercised by the application's smoke test.
                if (!IsFinite(clip.Duration) || clip.Duration <= 0)
                    throw new InvalidDataException("语音列表中的时长无效：" + clip.File);
            }
            string imagePath = Path.Combine(directory, "face.png");
            using (FileStream stream = System.IO.File.OpenRead(imagePath))
            {
                BitmapDecoder image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (image.Frames.Count == 0 || image.Frames[0].PixelWidth <= 0 || image.Frames[0].PixelHeight <= 0)
                    throw new InvalidDataException("人物图片无法读取。");
            }
            return new AssetCatalog { Clips = clips, DirectoryPath = directory, ImagePath = imagePath };
        }

        public static void ValidateM4a(string path)
        {
            using (BinaryReader reader = new BinaryReader(System.IO.File.OpenRead(path), Encoding.ASCII))
            {
                Stream stream = reader.BaseStream;
                bool hasType = false, hasMovie = false, hasData = false;
                while (stream.Position < stream.Length)
                {
                    long start = stream.Position;
                    if (stream.Length - start < 8) throw new InvalidDataException("M4A 容器已截断：" + path);
                    ulong length = ReadUInt32BigEndian(reader);
                    string type = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    long header = 8;
                    if (length == 1)
                    {
                        if (stream.Length - stream.Position < 8) throw new InvalidDataException("M4A 容器已截断：" + path);
                        length = ((ulong)ReadUInt32BigEndian(reader) << 32) | ReadUInt32BigEndian(reader);
                        header = 16;
                    }
                    else if (length == 0) length = (ulong)(stream.Length - start);
                    if (length < (ulong)header || length > (ulong)(stream.Length - start))
                        throw new InvalidDataException("M4A 容器数据块无效：" + path);
                    if (type == "ftyp") hasType |= length >= (ulong)(header + 8);
                    if (type == "moov") hasMovie |= length > (ulong)header;
                    if (type == "mdat") hasData |= length > (ulong)header;
                    stream.Position = start + (long)length;
                }
                if (!hasType || !hasMovie || !hasData)
                    throw new InvalidDataException("M4A 缺少类型、媒体索引或音频数据：" + path);
            }
        }

        private static uint ReadUInt32BigEndian(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(4);
            if (bytes.Length != 4) throw new InvalidDataException("M4A 容器已截断。");
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }

        internal static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
    }

    // Selection has no UI/audio dependencies, so all playback modes can be tested directly.
    public sealed class PlaybackSelector
    {
        private readonly Clip[] clips;
        private readonly Settings settings;
        private readonly Random random;
        private readonly Dictionary<int, Clip> byId = new Dictionary<int, Clip>();
        private readonly List<Clip> remaining = new List<Clip>();
        private HashSet<int> excluded = new HashSet<int>();
        private int lastId;

        public PlaybackSelector(Clip[] clips, Settings settings) : this(clips, settings, new Random()) { }
        public PlaybackSelector(Clip[] clips, Settings settings, int seed) : this(clips, settings, new Random(seed)) { }
        private PlaybackSelector(Clip[] clips, Settings settings, Random random)
        {
            if (clips == null || clips.Length == 0) throw new ArgumentException("语音列表不能为空。", "clips");
            if (settings == null) throw new ArgumentNullException("settings");
            this.clips = (Clip[])clips.Clone(); this.settings = settings; this.random = random;
            foreach (Clip clip in this.clips)
            {
                if (clip == null || clip.Id <= 0 || byId.ContainsKey(clip.Id))
                    throw new ArgumentException("语音列表包含无效或重复编号。", "clips");
                byId.Add(clip.Id, clip);
            }
            Validate();
        }

        public void Validate()
        {
            HashSet<int> cleaned = new HashSet<int>();
            foreach (int id in settings.ExcludedIds ?? new int[0]) if (byId.ContainsKey(id)) cleaned.Add(id);
            // A damaged configuration must never leave the desktop pet permanently silent.
            if (cleaned.Count == clips.Length) cleaned.Clear();
            if (!excluded.SetEquals(cleaned)) remaining.Clear();
            excluded = cleaned;
            List<int> canonical = new List<int>();
            foreach (Clip clip in clips) if (excluded.Contains(clip.Id)) canonical.Add(clip.Id);
            settings.ExcludedIds = canonical.ToArray();
            if (!byId.ContainsKey(settings.PinnedId))
            {
                settings.PinnedId = 0;
                if (!settings.RandomMode) settings.RandomMode = true;
            }
        }

        public int AllowedCount { get { Validate(); return clips.Length - excluded.Count; } }
        public Clip PinnedClip
        {
            get { Validate(); Clip clip; return byId.TryGetValue(settings.PinnedId, out clip) ? clip : null; }
        }
        public bool IsExcluded(int id) { Validate(); return excluded.Contains(id); }

        // Automatic companion replies bypass selection, but still become the last spoken clip.
        public void RecordPlayed(int id)
        {
            if (byId.ContainsKey(id)) lastId = id;
        }

        public Clip Next()
        {
            Validate();
            if (!settings.RandomMode)
            {
                Clip clip = byId[settings.PinnedId];
                lastId = clip.Id;
                return clip;
            }
            return RandomOnce();
        }

        public Clip RandomOnce()
        {
            Validate();
            if (remaining.Count == 0)
            {
                foreach (Clip clip in clips) if (!excluded.Contains(clip.Id)) remaining.Add(clip);
                for (int i = remaining.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    Clip saved = remaining[i]; remaining[i] = remaining[j]; remaining[j] = saved;
                }
                int end = remaining.Count - 1;
                if (remaining.Count > 1 && remaining[end].Id == lastId)
                {
                    int j = random.Next(end);
                    Clip saved = remaining[end]; remaining[end] = remaining[j]; remaining[j] = saved;
                }
            }
            Clip result = remaining[remaining.Count - 1];
            remaining.RemoveAt(remaining.Count - 1);
            lastId = result.Id;
            return result;
        }

        public Clip Choose(int id)
        {
            Validate();
            Clip selected;
            if (!byId.TryGetValue(id, out selected)) return null;
            if (!settings.RandomMode) settings.PinnedId = selected.Id;
            lastId = selected.Id;
            return selected;
        }

        public void SetRandomMode(bool randomMode)
        {
            Validate();
            if (!randomMode && settings.PinnedId == 0)
                settings.PinnedId = byId.ContainsKey(lastId) ? lastId : clips[0].Id;
            settings.RandomMode = randomMode;
        }

        public bool ToggleExcluded(int id)
        {
            Validate();
            if (!byId.ContainsKey(id)) return false;
            if (excluded.Contains(id)) excluded.Remove(id);
            else
            {
                if (excluded.Count == clips.Length - 1) return false;
                excluded.Add(id);
            }
            List<int> changed = new List<int>();
            foreach (Clip clip in clips) if (excluded.Contains(clip.Id)) changed.Add(clip.Id);
            settings.ExcludedIds = changed.ToArray();
            remaining.Clear();
            return true;
        }

        public void EnableAll()
        {
            settings.ExcludedIds = new int[0];
            Validate();
            remaining.Clear();
        }
    }

    public sealed class ShuffleBag
    {
        private readonly Random random;
        private readonly List<int> remaining = new List<int>();
        private int previous = -1;
        private int previousCount = -1;

        public ShuffleBag() : this(new Random()) { }
        public ShuffleBag(int seed) : this(new Random(seed)) { }
        private ShuffleBag(Random random) { this.random = random; }

        public int Next(int count)
        {
            if (count <= 0)
            {
                remaining.Clear();
                previousCount = count;
                return -1;
            }
            if (previousCount != count) remaining.Clear();
            previousCount = count;
            if (remaining.Count == 0)
            {
                for (int i = 0; i < count; i++) remaining.Add(i);
                for (int i = count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    int saved = remaining[i]; remaining[i] = remaining[j]; remaining[j] = saved;
                }
                if (count > 1 && remaining[count - 1] == previous)
                {
                    int j = random.Next(count - 1);
                    int saved = remaining[count - 1]; remaining[count - 1] = remaining[j]; remaining[j] = saved;
                }
            }
            previous = remaining[remaining.Count - 1];
            remaining.RemoveAt(remaining.Count - 1);
            return previous;
        }
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember(Name = "volume")] public double Volume = .65;
        [DataMember(Name = "size")] public double Size = 180;
        [DataMember(Name = "x")] public double X;
        [DataMember(Name = "y")] public double Y;
        [DataMember(Name = "hasPosition")] public bool HasPosition;
        [DataMember(Name = "reduceMotion")] public bool ReduceMotion;
        [DataMember(Name = "randomMode")] public bool RandomMode = true;
        [DataMember(Name = "pinnedId")] public int PinnedId;
        [DataMember(Name = "excludedIds")] public int[] ExcludedIds = new int[0];
        [DataMember(Name = "bridgeEnabled")] public bool BridgeEnabled = true;

        [OnDeserializing]
        private void BeforeDeserialization(StreamingContext context)
        {
            Volume = .65; Size = 180; RandomMode = true; BridgeEnabled = true; ExcludedIds = new int[0];
        }

        public static Settings Load(string path)
        {
            try
            {
                using (FileStream stream = System.IO.File.OpenRead(path))
                {
                    Settings result = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                    if (result == null) return new Settings();
                    result.Normalize();
                    return result;
                }
            }
            catch (IOException) { return new Settings(); }
            catch (UnauthorizedAccessException) { return new Settings(); }
            catch (SerializationException) { return new Settings(); }
            catch (System.Xml.XmlException) { return new Settings(); }
            catch (FormatException) { return new Settings(); }
            catch (OverflowException) { return new Settings(); }
        }

        public void Save(string path)
        {
            Normalize();
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            System.IO.Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, this);
                    stream.Flush(true);
                }
                if (System.IO.File.Exists(fullPath)) System.IO.File.Replace(temporary, fullPath, null);
                else System.IO.File.Move(temporary, fullPath);
            }
            finally
            {
                if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
            }
        }

        private void Normalize()
        {
            Volume = AssetCatalog.IsFinite(Volume) ? Math.Max(0, Math.Min(1, Volume)) : .65;
            Size = AssetCatalog.IsFinite(Size) ? Math.Max(120, Math.Min(260, Size)) : 180;
            if (PinnedId < 0) PinnedId = 0;
            if (ExcludedIds == null) ExcludedIds = new int[0];
            if (!AssetCatalog.IsFinite(X) || !AssetCatalog.IsFinite(Y))
            {
                X = 0; Y = 0; HasPosition = false;
            }
        }
    }
}
