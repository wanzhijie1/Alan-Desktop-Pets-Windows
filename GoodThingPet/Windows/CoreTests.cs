using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GoodThingPet
{
    public static class CoreTests
    {
        private static int failures;
        private static void Check(bool valid, string name)
        {
            Console.WriteLine((valid ? "OK " : "FAIL ") + name);
            if (!valid) failures++;
        }

        public static int Run(string assetsDirectory)
        {
            failures = 0;
            string temporary = Path.Combine(Path.GetTempPath(), "GoodThingPet 测试 " + Guid.NewGuid().ToString("N"));
            try
            {
                AssetCatalog catalog = AssetCatalog.Load(assetsDirectory);
                Check(catalog.Clips.Length == 11, "all 11 original clips present");
                foreach (Clip clip in catalog.Clips)
                {
                    double duration = AssetCatalog.WaveDuration(Path.Combine(catalog.DirectoryPath, "Clips", clip.File));
                    Check(duration > .25 && Math.Abs(duration - clip.Duration) < .15,
                        "WAV " + clip.Id + " duration " + duration.ToString("F3") + "s");
                }
                using (FileStream stream = File.OpenRead(catalog.ImagePath))
                {
                    BitmapDecoder image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    FormatConvertedBitmap rgba = new FormatConvertedBitmap(image.Frames[0], PixelFormats.Bgra32, null, 0);
                    byte[] pixels = new byte[rgba.PixelWidth * rgba.PixelHeight * 4];
                    rgba.CopyPixels(pixels, rgba.PixelWidth * 4, 0);
                    bool hasVisiblePixel = false;
                    for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 200) { hasVisiblePixel = true; break; }
                    Check(pixels[3] == 0 && hasVisiblePixel, "image contains transparent background and visible person");
                }

                ShuffleBag bag = new ShuffleBag(20261024);
                int last = -1;
                bool randomValid = true;
                for (int cycle = 0; cycle < 1000; cycle++)
                {
                    HashSet<int> used = new HashSet<int>();
                    for (int i = 0; i < catalog.Clips.Length; i++)
                    {
                        int value = bag.Next(catalog.Clips.Length);
                        randomValid &= value >= 0 && value < catalog.Clips.Length && value != last && used.Add(value);
                        last = value;
                    }
                }
                Check(randomValid, "1000 shuffle rounds: unique per round and no boundary repetition");
                Check(bag.Next(0) == -1 && bag.Next(-1) == -1, "empty shuffle is safe");
                Check(bag.Next(1) == 0 && bag.Next(1) == 0, "single-clip shuffle is safe");
                HashSet<int> resized = new HashSet<int>();
                for (int i = 0; i < 3; i++) resized.Add(bag.Next(3));
                Check(resized.SetEquals(new int[] { 0, 1, 2 }), "changing clip count rebuilds the queue");

                Directory.CreateDirectory(temporary);
                string settingsPath = Path.Combine(temporary, "设置.json");
                Settings missing = Settings.Load(settingsPath);
                Check(missing.Volume == .65 && missing.Size == 260 && !missing.HasPosition && !File.Exists(settingsPath),
                    "missing settings use defaults without writing files");
                Settings settings = new Settings { Volume = .25, Size = 340, X = -1700.5, Y = 22.75, HasPosition = true, ReduceMotion = true };
                settings.Save(settingsPath);
                Settings loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .25 && loaded.Size == 340 && loaded.X == -1700.5 && loaded.Y == 22.75 && loaded.HasPosition && loaded.ReduceMotion,
                    "settings round-trip on Chinese path with spaces and negative monitor coordinates");
                settings.Volume = .8;
                settings.Save(settingsPath);
                Check(Settings.Load(settingsPath).Volume == .8 && Directory.GetFiles(temporary).Length == 1,
                    "atomic settings replacement leaves no temporary files");
                File.WriteAllText(settingsPath, "{broken");
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .65 && loaded.Size == 260 && !loaded.HasPosition, "corrupt settings recover to defaults");
                File.WriteAllText(settingsPath, "{}");
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .65 && loaded.Size == 260, "older settings retain defaults for absent fields");
                File.WriteAllText(settingsPath, "{\"volume\":2,\"size\":-4}");
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == 1 && loaded.Size == 160, "settings clamp volume and size");
                settings.Volume = Double.NaN; settings.Size = Double.PositiveInfinity;
                settings.X = Double.NegativeInfinity; settings.HasPosition = true;
                settings.Save(settingsPath);
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .65 && loaded.Size == 260 && !loaded.HasPosition,
                    "non-finite settings do not produce invalid window geometry");

                string invalidWave = Path.Combine(temporary, "truncated.wav");
                File.WriteAllBytes(invalidWave, new byte[] { 82, 73, 70, 70 });
                bool rejected = false;
                try { AssetCatalog.WaveDuration(invalidWave); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "truncated audio is rejected");
            }
            catch (Exception error)
            {
                Check(false, error.ToString());
            }
            finally
            {
                // The test owns only this unique directory under the OS temporary directory.
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
            Console.WriteLine(failures == 0 ? "PASS" : "FAIL: " + failures + " checks");
            return failures == 0 ? 0 : 1;
        }
    }
}
