using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShaolanPet
{
    public static class CoreTests
    {
        private static int failures;
        private static void Check(bool valid, string name)
        {
            Console.WriteLine((valid ? "OK " : "FAIL ") + name);
            if (!valid) failures++;
        }

        private static bool MatchesPool(PlaybackSelector selector, int[] expected)
        {
            HashSet<int> actual = new HashSet<int>();
            for (int i = 0; i < selector.AllowedCount; i++) actual.Add(selector.RandomOnce().Id);
            return actual.SetEquals(expected);
        }

        private static void CheckRounds(PlaybackSelector selector, int[] expected, string name)
        {
            bool valid = selector.AllowedCount == expected.Length;
            int previous = -1;
            for (int round = 0; round < 1000; round++)
            {
                HashSet<int> used = new HashSet<int>();
                for (int i = 0; i < expected.Length; i++)
                {
                    Clip clip = selector.RandomOnce();
                    valid &= clip.Id != previous && used.Add(clip.Id);
                    previous = clip.Id;
                }
                valid &= used.SetEquals(expected);
            }
            Check(valid, name);
        }

        public static int Run(string assetsDirectory)
        {
            failures = 0;
            string temporary = Path.Combine(Path.GetTempPath(), "ShaolanPet 测试 " + Guid.NewGuid().ToString("N"));
            try
            {
                AssetCatalog catalog = AssetCatalog.Load(assetsDirectory);
                Check(catalog.Clips.Length == 37, "all 37 original clips present");
                int[] defaultIds = { 7, 16, 17, 19, 26, 30, 31, 32 };
                HashSet<int> enabledMetadata = new HashSet<int>();
                int answerCount = 0;
                foreach (Clip clip in catalog.Clips)
                {
                    double duration = AssetCatalog.WaveDuration(Path.Combine(catalog.DirectoryPath, "Clips", clip.File));
                    Check(duration > .25 && Math.Abs(duration - clip.Duration) < .15,
                        "WAV " + clip.Id + " valid PCM; duration " + duration.ToString("F3") + "s");
                    if (!clip.DefaultOff) enabledMetadata.Add(clip.Id);
                    if (clip.Answer) answerCount++;
                }
                Check(enabledMetadata.SetEquals(defaultIds) && answerCount == 25,
                    "defaultOff selects the original 8 clips and answer flags preserve 25 replies");
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
                Check(selector.AllowedCount == 8 && playback.ExcludedIds.Length == 29 && MatchesPool(selector, defaultIds),
                    "first launch applies exactly the 8 catalog defaults");
                selector.Validate(); selector.Validate();
                PlaybackSelector repeated = new PlaybackSelector(catalog.Clips, playback, 88);
                Check(repeated.AllowedCount == 8 && MatchesPool(repeated, defaultIds),
                    "repeated initialization and validation preserve the default 8");
                CheckRounds(selector, defaultIds, "1000 default-pool rounds: exact 8 clips and no boundary repetition");
                selector.EnableAll();
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
                Check(selectionValid, "1000 all-enabled rounds: exact 37 clips and no boundary repetition");
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
                bool filterValid = selector.AllowedCount == 35;
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
                Check(selector.AllowedCount == 37 && playback.ExcludedIds.Length == 0, "enable all restores all clips");
                for (int i = 0; i < catalog.Clips.Length - 1; i++) selector.ToggleExcluded(catalog.Clips[i].Id);
                Check(selector.AllowedCount == 1 && !selector.ToggleExcluded(catalog.Clips[36].Id),
                    "cannot exclude the last available random clip");
                bool singleValid = true;
                for (int i = 0; i < 10; i++) singleValid &= selector.RandomOnce().Id == catalog.Clips[36].Id;
                Check(singleValid, "single remaining random clip always plays");
                Check(!selector.ToggleExcluded(-123) && selector.Choose(-123) == null,
                    "invalid menu clip IDs are safely rejected");
                playback.ExcludedIds = new int[37];
                for (int i = 0; i < 37; i++) playback.ExcludedIds[i] = catalog.Clips[i].Id;
                playback.PinnedId = 99999; playback.RandomMode = false;
                selector.Validate();
                Check(selector.AllowedCount == 8 && playback.RandomMode && selector.PinnedClip == null && MatchesPool(selector, defaultIds),
                    "damaged all-excluded and invalid-pin configuration recovers to the default 8");
                playback.ExcludedIds = new int[] { catalog.Clips[0].Id, catalog.Clips[0].Id, -55, 99999 };
                selector.Validate();
                Check(selector.AllowedCount == 36 && playback.ExcludedIds.Length == 1,
                    "duplicate and nonexistent exclusions are cleaned");
                playback.ExcludedIds = null;
                selector.Validate();
                Check(selector.AllowedCount == 8 && MatchesPool(selector, defaultIds), "null exclusions apply catalog defaults safely");
                bool rejectedEmpty = false;
                try { new PlaybackSelector(new Clip[0], new Settings()); } catch (ArgumentException) { rejectedEmpty = true; }
                Check(rejectedEmpty, "empty selector catalogs are rejected explicitly");
                PlaybackSelector single = new PlaybackSelector(new Clip[] { new Clip { Id = 101, Label = "one", DefaultOff = true } }, new Settings(), 9);
                Check(single.AllowedCount == 1 && single.Next().Id == 101 && single.Next().Id == 101 && !single.ToggleExcluded(101),
                    "a one-clip all-default-off catalog remains playable and cannot be fully excluded");
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
                Check(missing.Volume == .65 && missing.Size == 260 && missing.RandomMode && missing.BridgeEnabled &&
                    missing.PinnedId == 0 && missing.ExcludedIds == null && !missing.HasPosition && !File.Exists(settingsPath),
                    "missing settings use defaults without writing files");
                missing.Save(settingsPath);
                Settings uninitializedReload = Settings.Load(settingsPath);
                Check(uninitializedReload.ExcludedIds == null,
                    "saving before catalog initialization preserves the unconfigured null state");
                PlaybackSelector initialized = new PlaybackSelector(catalog.Clips, uninitializedReload, 11);
                Check(initialized.AllowedCount == 8 && MatchesPool(initialized, defaultIds), "unconfigured saved settings still initialize the default 8");
                uninitializedReload.Save(settingsPath);
                Settings defaultReload = Settings.Load(settingsPath);
                PlaybackSelector defaultReopened = new PlaybackSelector(catalog.Clips, defaultReload, 12);
                Check(defaultReload.ExcludedIds.Length == 29 && defaultReopened.AllowedCount == 8 && MatchesPool(defaultReopened, defaultIds),
                    "saved default selection reopens with exactly the original 8 clips");
                defaultReopened.EnableAll();
                defaultReload.Save(settingsPath);
                Settings allReload = Settings.Load(settingsPath);
                PlaybackSelector allReopened = new PlaybackSelector(catalog.Clips, allReload, 13);
                allReopened.Validate(); allReopened.Validate();
                PlaybackSelector allReinitialized = new PlaybackSelector(catalog.Clips, allReload, 14);
                Check(allReload.ExcludedIds.Length == 0 && allReopened.AllowedCount == 37 && allReinitialized.AllowedCount == 37,
                    "explicit enable-all [] survives save, restart and repeated validation");
                allReload.Save(settingsPath);
                Check(new PlaybackSelector(catalog.Clips, Settings.Load(settingsPath)).AllowedCount == 37,
                    "enable-all remains persistent across a second save and restart");
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
                Check(loaded.Volume == .65 && loaded.Size == 260 && !loaded.HasPosition &&
                    new PlaybackSelector(catalog.Clips, loaded).AllowedCount == 8, "corrupt settings recover to the default 8 clips");
                File.WriteAllText(settingsPath, "{}");
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .65 && loaded.Size == 260 && loaded.RandomMode && loaded.BridgeEnabled && loaded.ExcludedIds == null,
                    "older settings retain an unconfigured null when excludedIds is absent");
                Check(new PlaybackSelector(catalog.Clips, loaded).AllowedCount == 8,
                    "absent excludedIds initializes the original 8 rather than all 37");
                File.WriteAllText(settingsPath, "{\"excludedIds\":[]}");
                loaded = Settings.Load(settingsPath);
                Check(loaded.ExcludedIds != null && loaded.ExcludedIds.Length == 0 && new PlaybackSelector(catalog.Clips, loaded).AllowedCount == 37,
                    "explicit JSON [] is distinguished from an absent excludedIds field");
                File.WriteAllText(settingsPath, "{\"volume\":2,\"size\":-4}");
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == 1 && loaded.Size == 160, "settings clamp volume and size");
                settings.Volume = Double.NaN; settings.Size = Double.PositiveInfinity;
                settings.X = Double.NegativeInfinity; settings.HasPosition = true;
                settings.Save(settingsPath);
                loaded = Settings.Load(settingsPath);
                Check(loaded.Volume == .65 && loaded.Size == 260 && !loaded.HasPosition,
                    "non-finite settings do not produce invalid window geometry");

                string invalidAudio = Path.Combine(temporary, "truncated.wav");
                File.WriteAllBytes(invalidAudio, new byte[] { 82, 73, 70, 70 });
                bool rejected = false;
                try { AssetCatalog.WaveDuration(invalidAudio); } catch (InvalidDataException) { rejected = true; }
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
