using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using DesktopPetLink;
using DesktopPetAudio;
using System.Diagnostics;

namespace ShaolanPet
{
    // Interaction and playback design adapted from SunPet; see LICENSE-SunPet.
    internal sealed class PetWindow : Window
    {
        private readonly AssetCatalog catalog;
        private readonly Settings settings;
        private readonly string settingsPath;
        private readonly bool smokeTest;
        private readonly bool uiTest;
        private readonly PlaybackSelector selector;
        private readonly Image face;
        private readonly TextBlock captionText;
        private readonly Border caption;
        private readonly ScaleTransform squash = new ScaleTransform(1, 1);
        private readonly DispatcherTimer captionTimer = new DispatcherTimer();
        private readonly DispatcherTimer playbackWatchdog = new DispatcherTimer();
        private PetBridge bridge;
        private string activeEventId;
        private Forms.NotifyIcon tray;
        private Forms.ContextMenuStrip openMenu;
        private Drawing.Icon trayIcon;
        private PlaybackHandle player;
        private Clip playingClip;
        private WaveOutput audio;
        private readonly Dictionary<int, WaveClip> audioClips = new Dictionary<int, WaveClip>();
        private IntPtr handle;
        private Native.Point mouseStart;
        private Native.Rect windowStart;
        private bool pressed, dragged, speaking, disposed;
        private int playGeneration;
        private readonly double imageRatio;
        private string playbackError;
        private int openedCount, endedCount;
        private int lastPlayedClipId;

        public PetWindow(AssetCatalog catalog, Settings settings, string settingsPath, bool smokeTest, bool uiTest)
        {
            this.catalog = catalog;
            this.settings = settings;
            this.settingsPath = settingsPath;
            this.smokeTest = smokeTest;
            this.uiTest = uiTest;
            selector = new PlaybackSelector(catalog.Clips, settings);
            Title = "薄肌桌宠 · ShaolanPet";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            // Diagnostic mode exposes the same controls to desktop UI test tools.
            ShowInTaskbar = uiTest;
            ShowActivated = uiTest;
            IsHitTestVisible = !smokeTest && !Program.IsBridgeTest;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;

            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(catalog.ImagePath);
            bitmap.EndInit();
            bitmap.Freeze();
            imageRatio = (double)bitmap.PixelHeight / bitmap.PixelWidth;
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            face = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8, 0, 8, 8),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 1),
                ToolTip = "点击随机播放一句 · 拖动移动 · 右键设置"
            };
            face.RenderTransform = squash;
            AutomationProperties.SetName(face, "薄肌桌宠 · ShaolanPet，点击随机播放，拖动移动，右键设置");
            RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.HighQuality);
            Grid.SetRow(face, 1);
            root.Children.Add(face);
            captionText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            caption = new Border
            {
                Child = captionText,
                Background = new SolidColorBrush(Color.FromArgb(208, 26, 27, 30)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(3, 0, 3, 5),
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Hidden,
                IsHitTestVisible = false
            };
            root.Children.Add(caption);
            Content = root;
            ApplySize();

            face.MouseLeftButtonDown += OnMouseDown;
            face.MouseMove += OnMouseMove;
            face.MouseLeftButtonUp += OnMouseUp;
            face.LostMouseCapture += delegate { pressed = false; };
            face.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                ShowMenu();
                e.Handled = true;
            };
            captionTimer.Tick += delegate { captionTimer.Stop(); caption.Visibility = Visibility.Hidden; };
            playbackWatchdog.Tick += delegate
            {
                StopPlayback();
                playbackError = "音频播放超时";
                ShowCaption("这句暂时无法播放", 4);
            };
            SourceInitialized += OnSourceInitialized;
            Loaded += delegate
            {
                InitializeAudio();
                RestorePosition();
                if (smokeTest) RunSmokeTest();
                else if (Program.IsBridgeTest) { StartBridge(); RunBridgeProbe(); }
                else { CreateTray(); StartBridge(); UpdateToolTip(); ShowCaption("点我说一句 · 右键设置", 5); }
            };
            Closing += delegate { SaveSettings(); };
            Closed += delegate { DisposeResources(); };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            handle = new WindowInteropHelper(this).Handle;
            int style = Native.GetWindowLong(handle, -20);
            if (!uiTest) Native.SetWindowLong(handle, -20, style | 0x80 | 0x08000000); // tool window, no activation
            HwndSource.FromHwnd(handle).AddHook(delegate(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
            {
                if (!uiTest && message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
                if (message == 0x02E0) Dispatcher.BeginInvoke(new Action(delegate { if (!pressed) EnsureVisible(); }), DispatcherPriority.Loaded);
                return IntPtr.Zero;
            });
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            Native.GetCursorPos(out mouseStart);
            Native.GetWindowRect(handle, out windowStart);
            dragged = false;
            pressed = face.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            Native.Point current;
            Native.GetCursorPos(out current);
            int dx = current.X - mouseStart.X, dy = current.Y - mouseStart.Y;
            if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4) dragged = true;
            if (dragged) Native.Move(handle, windowStart.Left + dx, windowStart.Top + dy);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            pressed = false;
            face.ReleaseMouseCapture();
            if (dragged) { EnsureVisible(); SaveSettings(); }
            else PlayNext();
            e.Handled = true;
        }

        private void ApplySize()
        {
            Width = settings.Size;
            Height = (settings.Size - 16) * imageRatio + 52;
        }

        private void ChangeSize(double value)
        {
            Native.Rect before;
            Native.GetWindowRect(handle, out before);
            settings.Size = value;
            ApplySize();
            Dispatcher.BeginInvoke(new Action(delegate
            {
                Native.Rect after;
                Native.GetWindowRect(handle, out after);
                Native.Move(handle, before.Right - (after.Right - after.Left), before.Bottom - (after.Bottom - after.Top));
                EnsureVisible();
                SaveSettings();
            }), DispatcherPriority.Loaded);
        }

        private void RestorePosition()
        {
            if (settings.HasPosition)
            {
                Native.Move(handle, (int)settings.X, (int)settings.Y);
                EnsureVisible();
            }
            else MoveToCorner();
        }

        private void MoveToCorner()
        {
            Drawing.Rectangle area = Forms.Screen.PrimaryScreen.WorkingArea;
            Native.Rect rect;
            Native.GetWindowRect(handle, out rect);
            int offset;
            if (!Int32.TryParse(Program.Option("--corner-offset", "0"), out offset)) offset = 0;
            HwndSource source = HwndSource.FromHwnd(handle);
            double scale = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
            int offsetPixels = (int)(Math.Max(0, Math.Min(2000, offset)) * scale);
            Native.Move(handle, Math.Max(area.Left, area.Right - (rect.Right - rect.Left) - 32 - offsetPixels),
                Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top) - 24));
            EnsureVisible();
        }

        private void EnsureVisible()
        {
            if (handle == IntPtr.Zero || disposed) return;
            Native.Rect rect;
            Native.GetWindowRect(handle, out rect);
            Drawing.Rectangle bounds = Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            Drawing.Rectangle area = Forms.Screen.FromRectangle(bounds).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(rect.Left, area.Right - bounds.Width));
            int y = Math.Max(area.Top, Math.Min(rect.Top, area.Bottom - bounds.Height));
            Native.Move(handle, x, y);
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (!disposed) Dispatcher.BeginInvoke(new Action(delegate { EnsureVisible(); SaveSettings(); }));
        }

        private void SaveSettings()
        {
            if (smokeTest || Program.IsBridgeTest || handle == IntPtr.Zero) return;
            Native.Rect rect;
            if (!Native.GetWindowRect(handle, out rect)) return;
            settings.X = rect.Left;
            settings.Y = rect.Top;
            settings.HasPosition = true;
            try { settings.Save(settingsPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (!disposed) ShowCaption("设置暂时无法保存", 4);
            }
        }

        private void PlayNext()
        {
            Clip clip = selector.Next();
            if (clip != null) Play(clip);
        }

        private void PlayRandom()
        {
            Clip clip = selector.RandomOnce();
            if (clip != null) Play(clip);
        }

        private void ChooseClip(int id)
        {
            Clip clip = selector.Choose(id);
            if (clip == null) return;
            SaveSettings();
            UpdateToolTip();
            Play(clip);
        }

        private void ChangeMode(bool random)
        {
            selector.SetRandomMode(random);
            SaveSettings();
            UpdateToolTip();
            ShowCaption(random ? "播放方式：随机一句" : "已固定：" + selector.PinnedClip.Label, 4);
        }

        private void UpdateToolTip()
        {
            face.ToolTip = settings.RandomMode ? "点击随机播放一句 · 拖动移动 · 右键设置" : "点击播放固定那句 · 拖动移动 · 右键设置";
        }

        private void ShowClipFilter()
        {
            ClipFilterWindow filter = new ClipFilterWindow(catalog, selector, SaveSettings);
            filter.Owner = this;
            filter.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            filter.ShowDialog();
        }

        private void Trace(string message)
        {
            if (Program.IsBridgeTest || Array.IndexOf(Environment.GetCommandLineArgs(), "--trace") >= 0)
            {
                Console.WriteLine(DateTime.UtcNow.ToString("O") + " [ShaolanPet] " + message);
                Console.Out.Flush();
            }
        }

        private void StartBridge()
        {
            try
            {
                bridge = new PetBridge("shaolan", Program.BridgeTestScope ?? (uiTest ? "ui-test" : null));
                bridge.Start();
            }
            catch (Exception ex)
            {
                Trace("bridge unavailable: " + ex.Message);
                if (bridge != null) { bridge.Dispose(); bridge = null; }
            }
        }

        private void ToggleBridge()
        {
            settings.BridgeEnabled = !settings.BridgeEnabled;
            if (!settings.BridgeEnabled && bridge != null && activeEventId != null)
            {
                bridge.AnnounceStopped(activeEventId);
                activeEventId = null;
            }
            SaveSettings();
            ShowCaption(settings.BridgeEnabled ? "桌宠联动：开" : "桌宠联动：关", 3);
        }

        private void InitializeAudio()
        {
            // Load and trim once. No file access, codec or device creation between sentences.
            foreach (Clip clip in catalog.Clips)
                audioClips.Add(clip.Id, WaveClip.Load(Path.Combine(catalog.DirectoryPath, "Clips", clip.File)));
            audio = new WaveOutput();
            audio.Failed += delegate(PlaybackHandle failed, Exception error)
            {
                if (disposed || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate
                {
                    if (disposed || player != failed) return;
                    StopPlayback();
                    playbackError = error.Message;
                    Trace("audio failed: " + error.Message);
                    ShowCaption("音频播放失败", 4);
                }));
            };
            audio.Ended += delegate(PlaybackHandle completed)
            {
                if (disposed || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate
                {
                    if (disposed || player != completed) return;
                    endedCount++;
                    Trace("ended clip=" + playingClip.Id);
                    playbackWatchdog.Stop();
                    SetSpeaking(false);
                    ShowCaption(playingClip.Label, 0.4);
                    player = null;
                    activeEventId = null;
                }));
            };
        }

        private void Play(Clip clip, bool announce = true)
        {
            StopPlayback();
            playbackError = null;
            try
            {
                WaveClip wave = audioClips[clip.Id];
                // Announce the estimated voice ending, preserving all audio and limiting tail compensation to 180ms.
                double replyAt = Math.Max(wave.SpeechEnd, wave.Duration - 0.18);
                long requested = Stopwatch.GetTimestamp();
                bool audibleTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--audible-test") >= 0;
                player = audio.Play(wave, smokeTest || (Program.IsBridgeTest && !audibleTest) ? 0 : settings.Volume);
                playingClip = clip;
                openedCount++;
                lastPlayedClipId = clip.Id;
                selector.RecordPlayed(clip.Id);
                if (announce && settings.BridgeEnabled && bridge != null)
                    activeEventId = bridge.AnnounceSpeaking(clip.Id, replyAt, player.StartedTimestamp, player.StartedUtc);
                int hop = 0;
                Trace("playing clip=" + clip.Id + " hop=" + hop + " duration=" + wave.Duration.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
                    + " replyAt=" + replyAt.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " speechStart=" + wave.SpeechStart.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " speechEnd=" + wave.SpeechEnd.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " startQpc=" + player.StartedTimestamp + " playMs=" + ((Stopwatch.GetTimestamp() - requested) * 1000.0 / Stopwatch.Frequency).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                SetSpeaking(true);
                ShowCaption(clip.Label, 0);
                playbackWatchdog.Interval = TimeSpan.FromSeconds(wave.Duration + 5);
                playbackWatchdog.Start();
            }
            catch (Exception ex)
            {
                StopPlayback();
                playbackError = ex.Message;
                ShowCaption("这句暂时无法播放", 4);
            }
        }

        private void SetSpeaking(bool enabled)
        {
            speaking = enabled;
            squash.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            squash.ScaleY = 1;
            if (enabled && !settings.ReduceMotion && SystemParameters.ClientAreaAnimation)
            {
                DoubleAnimation animation = new DoubleAnimation(1, 0.88, TimeSpan.FromSeconds(0.18));
                animation.AutoReverse = true;
                animation.RepeatBehavior = RepeatBehavior.Forever;
                animation.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
                squash.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            }
        }

        private void ShowCaption(string text, double seconds)
        {
            captionTimer.Stop();
            captionText.Text = text;
            caption.Visibility = Visibility.Visible;
            if (seconds > 0) { captionTimer.Interval = TimeSpan.FromSeconds(seconds); captionTimer.Start(); }
        }

        private void StopPlayback()
        {
            if (bridge != null && activeEventId != null) bridge.AnnounceStopped(activeEventId);
            activeEventId = null;
            playGeneration++;
            playbackWatchdog.Stop();
            player = null;
            if (audio != null) audio.Stop();
            SetSpeaking(false);
            captionTimer.Stop();
            caption.Visibility = Visibility.Hidden;
        }

        private void CreateTray()
        {
            tray = new Forms.NotifyIcon();
            tray.Text = "薄肌桌宠 · ShaolanPet · 右键设置";
            try { trayIcon = Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); }
            catch { trayIcon = (Drawing.Icon)Drawing.SystemIcons.Application.Clone(); }
            tray.Icon = trayIcon;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e)
            {
                Dispatcher.BeginInvoke(new Action(ShowMenu));
            };
            tray.Visible = true;
        }

        private Forms.ToolStripMenuItem Item(string label, Action action)
        {
            Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem(label);
            if (action != null) item.Click += delegate { action(); };
            return item;
        }

        private Forms.ContextMenuStrip BuildMenu()
        {
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Font = new Drawing.Font("Microsoft YaHei UI", 9);
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Items.Add(new Forms.ToolStripMenuItem("邵艾伦 · 薄肌（" + catalog.Clips.Length + " 句）") { Enabled = false });
            menu.Items.Add(new Forms.ToolStripSeparator());
            Forms.ToolStripMenuItem mode = Item("播放方式", null);
            mode.Name = "PlaybackMode";
            Forms.ToolStripMenuItem randomMode = Item("随机一句（一轮不重复）", delegate { ChangeMode(true); });
            randomMode.Checked = settings.RandomMode;
            string fixedTitle = selector.PinnedClip == null ? "固定播一句" : "固定播一句（" + selector.PinnedClip.Id.ToString("00") + " " + selector.PinnedClip.Label + "）";
            Forms.ToolStripMenuItem fixedMode = Item(fixedTitle, delegate { ChangeMode(false); });
            fixedMode.Checked = !settings.RandomMode;
            mode.DropDownItems.Add(randomMode);
            mode.DropDownItems.Add(fixedMode);
            menu.Items.Add(mode);
            Forms.ToolStripMenuItem choose = Item("挑一句", null);
            choose.Name = "Choose";
            foreach (Clip clip in catalog.Clips)
            {
                Clip selected = clip;
                Forms.ToolStripMenuItem item = Item(clip.Id.ToString("00") + "  " + clip.Label, delegate { ChooseClip(selected.Id); });
                item.Checked = !settings.RandomMode && settings.PinnedId == clip.Id;
                choose.DropDownItems.Add(item);
            }
            menu.Items.Add(choose);
            menu.Items.Add(Item("轮播内容（已选 " + selector.AllowedCount + " / " + catalog.Clips.Length + "）…", ShowClipFilter));
            menu.Items.Add(Item("随机播一次", PlayRandom));
            menu.Items.Add(Item("停止播放", StopPlayback));
            menu.Items.Add(new Forms.ToolStripSeparator());
            Forms.ToolStripMenuItem linked = Item(settings.BridgeEnabled ? "关闭桌宠联动" : "开启桌宠联动", ToggleBridge);
            linked.Checked = settings.BridgeEnabled;
            menu.Items.Add(linked);
            string linkStatus = bridge == null ? "联动：本机通道不可用" : bridge.IsPeerRunning("sunpet") ? "联动：太对了在场 · 薄肌只说不接" : "联动：等待太对了（三宠新版）";
            menu.Items.Add(new Forms.ToolStripMenuItem(linkStatus) { Enabled = false });
            menu.Items.Add(new Forms.ToolStripSeparator());
            Forms.ToolStripMenuItem volume = Item("音量", null);
            foreach (int percent in new int[] { 0, 25, 50, 65, 80, 100 })
            {
                int selected = percent;
                Forms.ToolStripMenuItem option = Item(percent == 0 ? "静音" : percent + "%", delegate
                {
                    settings.Volume = selected / 100.0;
                    if (audio != null) audio.Volume = settings.Volume;
                    SaveSettings();
                });
                option.Checked = Math.Abs(settings.Volume - percent / 100.0) < 0.01;
                volume.DropDownItems.Add(option);
            }
            menu.Items.Add(volume);
            Forms.ToolStripMenuItem size = Item("人物大小", null);
            string[] names = { "小", "中", "大" };
            int[] sizes = { 190, 260, 340 };
            for (int i = 0; i < sizes.Length; i++)
            {
                int value = sizes[i];
                Forms.ToolStripMenuItem option = Item(names[i], delegate { ChangeSize(value); });
                option.Checked = Math.Abs(settings.Size - value) < 1;
                size.DropDownItems.Add(option);
            }
            menu.Items.Add(size);
            Forms.ToolStripMenuItem motion = Item("减少动态效果", delegate
            {
                settings.ReduceMotion = !settings.ReduceMotion;
                SetSpeaking(speaking);
                SaveSettings();
            });
            motion.Checked = settings.ReduceMotion;
            menu.Items.Add(motion);
            menu.Items.Add(Item("移回屏幕右下角", delegate { MoveToCorner(); SaveSettings(); }));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item("退出桌宠", Close));
            return menu;
        }

        private void ShowMenu()
        {
            if (openMenu != null) { openMenu.Close(); openMenu.Dispose(); }
            openMenu = BuildMenu();
            Native.Point point;
            Native.GetCursorPos(out point);
            openMenu.Show(point.X, point.Y);
        }

        private void DisposeResources()
        {
            if (disposed) return;
            disposed = true;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            StopPlayback();
            if (audio != null) { audio.Dispose(); audio = null; }
            if (bridge != null) { bridge.Dispose(); bridge = null; }
            if (openMenu != null) openMenu.Dispose();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (trayIcon != null) trayIcon.Dispose();
        }

        private async Task WaitUntil(Func<bool> predicate, int milliseconds, string failure)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (!predicate())
            {
                if (playbackError != null) throw new InvalidOperationException(playbackError);
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException(failure);
                await Task.Delay(25);
            }
        }

        private async void RunBridgeProbe()
        {
            try
            {
                if (bridge == null) throw new InvalidOperationException("Bridge test endpoint failed to start");
                int clipId = Int32.Parse(Program.Option("--test-play", "0"));
                int seconds = Int32.Parse(Program.Option("--test-exit-after", "12"));
                if (seconds < 2 || seconds > 60) throw new ArgumentOutOfRangeException("--test-exit-after");
                Trace("bridge ready role=shaolan scope=" + Program.BridgeTestScope);
                await Task.Delay(1000);
                if (clipId > 0)
                {
                    Clip clip = selector.Choose(clipId);
                    if (clip == null) throw new ArgumentOutOfRangeException("--test-play");
                    Play(clip);
                }
                int stopAfter = Int32.Parse(Program.Option("--test-stop-after", "0"));
                if (stopAfter < 0 || stopAfter > (seconds - 1) * 1000) throw new ArgumentOutOfRangeException("--test-stop-after");
                if (stopAfter > 0)
                {
                    await Task.Delay(stopAfter);
                    StopPlayback();
                    Trace("test playback stopped");
                }
                await Task.Delay((seconds - 1) * 1000 - stopAfter);
                if (playbackError != null) throw new InvalidOperationException(playbackError);
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                Application.Current.Shutdown(1);
            }
        }

        private async void RunSmokeTest()
        {
            try
            {
                if (!AllowsTransparency || !Topmost || ShowInTaskbar || ShowActivated)
                    throw new InvalidOperationException("Window flags are incorrect");
                int nativeStyle = Native.GetWindowLong(handle, -20);
                if ((nativeStyle & 0x08000080) != 0x08000080) throw new InvalidOperationException("Native window must not steal focus");
                Console.WriteLine("OK transparent, topmost, no activation, no taskbar window");
                using (Forms.ContextMenuStrip menu = BuildMenu())
                {
                    Forms.ToolStripMenuItem choose = (Forms.ToolStripMenuItem)menu.Items["Choose"];
                    if (choose.DropDownItems.Count != catalog.Clips.Length) throw new InvalidOperationException("Clip menu is incomplete");
                    Forms.ToolStripMenuItem mode = (Forms.ToolStripMenuItem)menu.Items["PlaybackMode"];
                    if (mode.DropDownItems.Count != 2) throw new InvalidOperationException("Playback modes are incomplete");
                }
                Console.WriteLine("OK all clips available in context menu");
                foreach (Clip clip in catalog.Clips)
                {
                    int initialOpened = openedCount, initialEnded = endedCount;
                    Play(clip);
                    await WaitUntil(delegate { return openedCount > initialOpened; }, 6000, "Media did not open: " + clip.File);
                    if (!speaking || captionText.Text != clip.Label) throw new InvalidOperationException("Caption/animation did not start for " + clip.File + "; speaking=" + speaking + "; last=" + lastPlayedClipId + "; caption=" + captionText.Text);
                    await WaitUntil(delegate { return endedCount > initialEnded; }, 6000, "Media did not finish: " + clip.File);
                    if (speaking) throw new InvalidOperationException("Animation did not end");
                    Console.WriteLine("OK preloaded PCM play/end: " + clip.File);
                }
                int previousOpened = openedCount;
                PlayNext();
                await WaitUntil(delegate { return openedCount > previousOpened; }, 6000, "Initial playback did not start");
                PlaybackHandle oldPlayer = player;
                PlayNext();
                if (player == oldPlayer || !oldPlayer.WasStopped) throw new InvalidOperationException("Playback handle was not replaced");
                StopPlayback();
                await Task.Delay(150);
                if (player != null || speaking || caption.Visibility != Visibility.Hidden)
                    throw new InvalidOperationException("Stop did not clear playback state");
                Console.WriteLine("OK rapid replacement and stop clear old audio, caption and animation");
                selector.SetRandomMode(false);
                int fixedOpened = openedCount;
                ChooseClip(13);
                await WaitUntil(delegate { return openedCount > fixedOpened; }, 6000, "Fixed clip did not open");
                if (lastPlayedClipId != 13 || settings.PinnedId != 13) throw new InvalidOperationException("Choice did not update fixed playback");
                fixedOpened = openedCount;
                PlayNext();
                await WaitUntil(delegate { return openedCount > fixedOpened; }, 6000, "Repeated fixed clip did not open");
                if (lastPlayedClipId != 13) throw new InvalidOperationException("Fixed mode drifted to another clip");
                StopPlayback();
                selector.SetRandomMode(true);
                Console.WriteLine("OK fixed mode menu selection and repeated playback use the pinned clip");

                Console.WriteLine("PASS");
                Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                Application.Current.Shutdown(1);
            }
        }

    }
}
