using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SunPet
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
            string temporary = Path.Combine(Path.GetTempPath(), "SunPet 测试 " + Guid.NewGuid().ToString("N"));
            try
            {
                AssetCatalog catalog = AssetCatalog.Load(assetsDirectory);
                Check(catalog.Clips.Length == 28, "all 28 original clips present");
                foreach (Clip clip in catalog.Clips)
                {
                    AssetCatalog.ValidateM4a(Path.Combine(catalog.DirectoryPath, "Clips", clip.File));
                    Check(clip.Duration > 0, "M4A " + clip.Id + " complete container; positive metadata duration");
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

                Settings playback = new Settings();
                PlaybackSelector selector = new PlaybackSelector(catalog.Clips, playback, 87);
                bool selectionValid = true;
                last = -1;
                for (int cycle = 0; cycle < 1000; cycle++)
                {
                    HashSet<int> used = new HashSet<int>();
                    for (int i = 0; i < catalog.Clips.Length; i++)
                    {
                        Clip chosen = selector.Next();
                        selectionValid &= chosen != null && chosen.Id != last && used.Add(chosen.Id);
                        last = chosen.Id;
                    }
                }
                Check(selectionValid, "selector 1000 rounds: unique per round and no boundary repetition");
                selector.SetRandomMode(false);
                Check(selector.PinnedClip != null && selector.PinnedClip.Id == last && !playback.RandomMode,
                    "fixed mode initially pins the last played clip");
                bool fixedValid = true;
                for (int i = 0; i < 30; i++) fixedValid &= selector.Next().Id == last;
                Check(fixedValid, "fixed mode repeats the pinned clip");
                Clip selected = selector.Choose(catalog.Clips[2].Id);
                Check(selected.Id == catalog.Clips[2].Id && selector.PinnedClip.Id == selected.Id && selector.Next().Id == selected.Id,
                    "manual choice updates fixed pin");
                int pinned = selected.Id;
                selector.SetRandomMode(true);
                selector.Choose(catalog.Clips[4].Id);
                Check(selector.PinnedClip.Id == pinned && playback.RandomMode, "manual choice in random mode preserves saved pin");
                selector.ToggleExcluded(catalog.Clips[0].Id);
                selector.ToggleExcluded(catalog.Clips[1].Id);
                bool filterValid = selector.AllowedCount == 26;
                for (int i = 0; i < 300; i++)
                {
                    int id = selector.RandomOnce().Id;
                    filterValid &= id != catalog.Clips[0].Id && id != catalog.Clips[1].Id;
                }
                Check(filterValid, "excluded clips leave random playback immediately");
                selector.SetRandomMode(false);
                selector.Choose(catalog.Clips[0].Id);
                Check(selector.Next().Id == catalog.Clips[0].Id && selector.IsExcluded(catalog.Clips[0].Id),
                    "fixed and manual playback can intentionally play an excluded random clip");
                bool randomOnceValid = true;
                for (int i = 0; i < 100; i++)
                {
                    int id = selector.RandomOnce().Id;
                    randomOnceValid &= id != catalog.Clips[0].Id && id != catalog.Clips[1].Id;
                }
                Check(randomOnceValid && !playback.RandomMode && selector.PinnedClip.Id == catalog.Clips[0].Id,
                    "random once honors exclusions while preserving fixed mode and pin");
                selector.EnableAll();
                Check(selector.AllowedCount == 28 && playback.ExcludedIds.Length == 0, "enable all restores all clips");
                for (int i = 0; i < catalog.Clips.Length - 1; i++) selector.ToggleExcluded(catalog.Clips[i].Id);
                Check(selector.AllowedCount == 1 && !selector.ToggleExcluded(catalog.Clips[27].Id),
                    "cannot exclude the last available random clip");
                bool singleValid = true;
                for (int i = 0; i < 10; i++) singleValid &= selector.RandomOnce().Id == catalog.Clips[27].Id;
                Check(singleValid, "single remaining random clip always plays");
                Check(!selector.ToggleExcluded(-123) && selector.Choose(-123) == null,
                    "invalid menu clip IDs are safely rejected");
                playback.ExcludedIds = new int[28];
                for (int i = 0; i < 28; i++) playback.ExcludedIds[i] = catalog.Clips[i].Id;
                playback.PinnedId = 99999; playback.RandomMode = false;
                selector.Validate();
                Check(selector.AllowedCount == 28 && playback.RandomMode && selector.PinnedClip == null && selector.Next() != null,
                    "damaged all-excluded and invalid-pin configuration recovers to audible random mode");
                playback.ExcludedIds = new int[] { catalog.Clips[0].Id, catalog.Clips[0].Id, -55, 99999 };
                selector.Validate();
                Check(selector.AllowedCount == 27 && playback.ExcludedIds.Length == 1,
                    "duplicate and nonexistent exclusions are cleaned");
                playback.ExcludedIds = null;
                selector.Validate();
                Check(selector.AllowedCount == 28, "null exclusions recover safely");
                Settings fresh = new Settings();
                PlaybackSelector newSelector = new PlaybackSelector(catalog.Clips, fresh, 12);
                newSelector.SetRandomMode(false);
                Check(newSelector.PinnedClip.Id == catalog.Clips[0].Id && newSelector.Next().Id == catalog.Clips[0].Id,
                    "fixed mode without playback history selects the first clip");
                Settings companionSettings = new Settings();
                PlaybackSelector companionSelector = new PlaybackSelector(catalog.Clips, companionSettings, 13);
                companionSelector.RecordPlayed(13);
                Check(companionSelector.PinnedClip == null && companionSettings.RandomMode,
                    "recording a companion reply leaves mode and pin unchanged");
                companionSelector.SetRandomMode(false);
                Check(companionSelector.PinnedClip.Id == 13 && companionSelector.Next().Id == 13,
                    "fixed mode pins the last successfully played automatic reply");
                companionSelector.RecordPlayed(14);
                Check(companionSelector.PinnedClip.Id == 13 && companionSelector.Next().Id == 13,
                    "recording a later reply preserves an existing fixed pin");

                Directory.CreateDirectory(temporary);
                string settingsPath = Path.Combine(temporary, "设置.json");
                Settings missing = Settings.Load(settingsPath);
                Check(missing.Volume == .65 && missing.Size == 180 && missing.RandomMode && missing.BridgeEnabled &&
                    missing.PinnedId == 0 && missing.ExcludedIds.Length == 0 && !missing.HasPosition && !File.Exists(settingsPath),
                    "missing settings use defaults without writing files");
                Settings settings = new Settings { Volume = .25, Size = 230, X = -1700.5, Y = 22.75, HasPosition = true,
                    ReduceMotion = true, RandomMode = false, PinnedId = 3, ExcludedIds = new int[] { 1, 2 }, BridgeEnabled = false };
                settings.Save(settingsPath);
                Settings loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .25 && loaded.Size == 230 && loaded.X == -1700.5 && loaded.Y == 22.75 && loaded.HasPosition && loaded.ReduceMotion &&
                    !loaded.RandomMode && loaded.PinnedId == 3 && loaded.ExcludedIds.Length == 2 && !loaded.BridgeEnabled,
                    "settings round-trip on Chinese path with spaces and negative monitor coordinates");
                settings.Volume = .8;
                settings.Save(settingsPath);
                Check(Settings.Load(settingsPath).Volume == .8 && Directory.GetFiles(temporary).Length == 1,
                    "atomic settings replacement leaves no temporary files");
                File.WriteAllText(settingsPath, "{broken");
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .65 && loaded.Size == 180 && !loaded.HasPosition, "corrupt settings recover to defaults");
                File.WriteAllText(settingsPath, "{}");
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .65 && loaded.Size == 180 && loaded.RandomMode && loaded.BridgeEnabled && loaded.ExcludedIds.Length == 0,
                    "older settings retain defaults for absent fields");
                File.WriteAllText(settingsPath, "{\"volume\":2,\"size\":-4}");
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == 1 && loaded.Size == 120, "settings clamp volume and size");
                settings.Volume = Double.NaN; settings.Size = Double.PositiveInfinity;
                settings.X = Double.NegativeInfinity; settings.HasPosition = true;
                settings.Save(settingsPath);
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .65 && loaded.Size == 180 && !loaded.HasPosition,
                    "non-finite settings do not produce invalid window geometry");

                string invalidAudio = Path.Combine(temporary, "truncated.m4a");
                File.WriteAllBytes(invalidAudio, new byte[] { 0, 0, 0, 24, 102, 116, 121, 112 });
                bool rejected = false;
                try { AssetCatalog.ValidateM4a(invalidAudio); } catch (InvalidDataException) { rejected = true; }
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
