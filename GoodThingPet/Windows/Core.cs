using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Media.Imaging;

namespace GoodThingPet
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
                    !String.Equals(Path.GetExtension(clip.File), ".wav", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(clip.File) != clip.File || !files.Add(clip.File))
                    throw new InvalidDataException("语音文件名无效：" + clip.File);
                double duration = WaveDuration(Path.Combine(directory, "Clips", clip.File));
                if (!IsFinite(clip.Duration) || clip.Duration <= 0 || Math.Abs(duration - clip.Duration) > .15)
                    throw new InvalidDataException("语音时长与列表不一致：" + clip.File);
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

        public static double WaveDuration(string path)
        {
            using (BinaryReader reader = new BinaryReader(System.IO.File.OpenRead(path), Encoding.ASCII))
            {
                Stream stream = reader.BaseStream;
                if (stream.Length < 44 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
                    throw new InvalidDataException("无效 WAV 文件：" + path);
                long riffEnd = reader.ReadUInt32() + 8L;
                if (riffEnd > stream.Length || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
                    throw new InvalidDataException("WAV 文件不完整：" + path);
                uint bytesPerSecond = 0;
                ushort blockAlign = 0;
                long dataBytes = 0;
                bool hasFormat = false;
                while (stream.Position + 8 <= riffEnd)
                {
                    string chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    uint length = reader.ReadUInt32();
                    long end = stream.Position + length;
                    if (end > riffEnd) throw new InvalidDataException("WAV 数据块不完整：" + path);
                    if (chunk == "fmt ")
                    {
                        if (length < 16) throw new InvalidDataException("WAV 格式块无效：" + path);
                        ushort encoding = reader.ReadUInt16();
                        ushort channels = reader.ReadUInt16();
                        uint sampleRate = reader.ReadUInt32();
                        bytesPerSecond = reader.ReadUInt32();
                        blockAlign = reader.ReadUInt16();
                        ushort bits = reader.ReadUInt16();
                        if (encoding != 1 || channels == 0 || channels > 2 || sampleRate == 0 ||
                            (bits != 8 && bits != 16 && bits != 24 && bits != 32) ||
                            blockAlign != channels * bits / 8 || bytesPerSecond != (long)sampleRate * blockAlign)
                            throw new InvalidDataException("不支持的 WAV 格式：" + path);
                        hasFormat = true;
                    }
                    else if (chunk == "data") dataBytes += length;
                    stream.Position = end + (length % 2);
                }
                if (!hasFormat || bytesPerSecond == 0 || dataBytes == 0 || dataBytes % blockAlign != 0)
                    throw new InvalidDataException("WAV 文件没有有效音频：" + path);
                return (double)dataBytes / bytesPerSecond;
            }
        }

        internal static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
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
        [DataMember(Name = "size")] public double Size = 260;
        [DataMember(Name = "x")] public double X;
        [DataMember(Name = "y")] public double Y;
        [DataMember(Name = "hasPosition")] public bool HasPosition;
        [DataMember(Name = "reduceMotion")] public bool ReduceMotion;
        [DataMember(Name = "bridgeEnabled")] public bool BridgeEnabled = true;

        [OnDeserializing]
        private void BeforeDeserialization(StreamingContext context)
        {
            Volume = .65; Size = 260; BridgeEnabled = true;
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
            Size = AssetCatalog.IsFinite(Size) ? Math.Max(160, Math.Min(400, Size)) : 260;
            if (!AssetCatalog.IsFinite(X) || !AssetCatalog.IsFinite(Y))
            {
                X = 0; Y = 0; HasPosition = false;
            }
        }
    }
}
