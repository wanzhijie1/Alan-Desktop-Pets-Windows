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
using DesktopPetLink;
using DesktopPetAudio;
using System.Diagnostics;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GoodThingPet
{
    // Interaction and playback design adapted from SunPet; see LICENSE-SunPet.
    internal sealed class PetWindow : Window
    {
        private readonly AssetCatalog catalog;
        private readonly Settings settings;
        private readonly string settingsPath;
        private readonly bool smokeTest;
        private readonly bool uiTest;
        private readonly bool bridgeTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--bridge-test") >= 0;
        private readonly ShuffleBag bag = new ShuffleBag();
        private readonly Image face;
        private readonly TextBlock captionText;
        private readonly Border caption;
        private readonly ScaleTransform squash = new ScaleTransform(1, 1);
        private readonly DispatcherTimer captionTimer = new DispatcherTimer();
        private readonly DispatcherTimer playbackWatchdog = new DispatcherTimer();
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
        private PetBridge bridge;
        private readonly BridgeCancellation cancellations = new BridgeCancellation();
        private string activeBridgeEvent;
        private readonly DispatcherTimer replyTimer = new DispatcherTimer(DispatcherPriority.Send);
        private readonly ShuffleBag replyBag = new ShuffleBag();
        private BridgeMessage pendingReply;
        private Clip pendingReplyClip;
        private BridgeMessage activeReplySource;

        public PetWindow(AssetCatalog catalog, Settings settings, string settingsPath, bool smokeTest, bool uiTest)
        {
            this.catalog = catalog;
            this.settings = settings;
            this.settingsPath = settingsPath;
            this.smokeTest = smokeTest;
            this.uiTest = uiTest;
            Title = "好事桌宠";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            // Diagnostic mode exposes the same controls to desktop UI test tools.
            ShowInTaskbar = uiTest;
            ShowActivated = uiTest;
            IsHitTestVisible = !smokeTest && !bridgeTest;
            if (bridgeTest) Opacity = 0;
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
                RenderTransform = squash,
                RenderTransformOrigin = new Point(0.5, 1),
                ToolTip = "点击随机播放一句 · 拖动移动 · 右键设置"
            };
            AutomationProperties.SetName(face, "好事桌宠，点击随机播放，拖动移动，右键设置");
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
            replyTimer.Tick += delegate
            {
                replyTimer.Stop();
                if (player != null && player.IsCompleted && !player.WasStopped) CompletePlayback(player);
                BridgeMessage source = pendingReply;
                Clip clip = pendingReplyClip;
                pendingReply = null;
                pendingReplyClip = null;
                if (disposed || !settings.BridgeEnabled || player != null || source == null || clip == null) return;
                if (bridge != null && (!bridge.IsPeerRunning(source.FromRole) || !bridge.IsPeerRunning(source.RootRole))) return;
                Play(clip, source);
                Trace("companion clip=" + clip.Id + " hop=" + (source.Hop + 1) + " root=" + source.RootRole);
            };
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
                else if (bridgeTest) { StartBridge(); RunBridgeTest(); }
                else { StartBridge(); CreateTray(); ShowCaption("点我说一句 · 右键设置", 5); }
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
            double cornerOffset;
            if (!Double.TryParse(Program.ArgumentValue("--corner-offset", "0"), out cornerOffset) || Double.IsNaN(cornerOffset) || Double.IsInfinity(cornerOffset)) cornerOffset = 0;
            cornerOffset = Math.Max(0, Math.Min(2000, cornerOffset));
            HwndSource source = HwndSource.FromHwnd(handle);
            double scale = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
            int pixelOffset = (int)Math.Round(cornerOffset * scale);
            Native.Move(handle, Math.Max(area.Left, area.Right - (rect.Right - rect.Left) - 32 - pixelOffset),
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
            if (smokeTest || bridgeTest || handle == IntPtr.Zero) return;
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
            int index = bag.Next(catalog.Clips.Length);
            if (index >= 0) Play(catalog.Clips[index]);
        }

        private void RunBridgeTest()
        {
            Trace("bridge-test ready");
            double seconds;
            if (!Double.TryParse(Program.ArgumentValue("--test-exit-after", "12"), out seconds) || seconds < 2 || seconds > 60) seconds = 12;
            DispatcherTimer exitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            exitTimer.Tick += delegate { exitTimer.Stop(); Application.Current.Shutdown(0); };
            exitTimer.Start();
            int id;
            if (Int32.TryParse(Program.ArgumentValue("--test-play", "0"), out id) && id > 0)
            {
                DispatcherTimer playTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                playTimer.Tick += delegate
                {
                    playTimer.Stop();
                    foreach (Clip clip in catalog.Clips) if (clip.Id == id) { Play(clip); break; }
                    double delay;
                    if (Double.TryParse(Program.ArgumentValue("--test-stop-after", "0"), out delay) && delay > 0)
                    {
                        DispatcherTimer stopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
                        stopTimer.Tick += delegate { stopTimer.Stop(); StopPlayback(); Trace("test stop"); };
                        stopTimer.Start();
                    }
                };
                playTimer.Start();
            }
        }
        private void StartBridge()
        {
            try
            {
                bridge = new PetBridge("fengge", bridgeTest ? Program.ArgumentValue("--bridge-test", "test") : uiTest ? "ui-test" : "");
                bridge.MessageReceived += delegate(BridgeMessage message)
                {
                    if (!disposed && !Dispatcher.HasShutdownStarted)
                        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate { if (!disposed) OnBridgeMessage(message); }));
                };
                bridge.Start();
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine(error);
                if (bridge != null) bridge.Dispose();
                bridge = null;
            }
        }

        private void Trace(string message)
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--trace") >= 0)
            {
                Console.WriteLine(DateTime.UtcNow.ToString("O") + " [FengGe] " + message);
                Console.Out.Flush();
            }
        }

        private void CancelPendingReply() { replyTimer.Stop(); pendingReply = null; pendingReplyClip = null; }

        private void OnBridgeMessage(BridgeMessage message)
        {
            Trace("received " + message.Kind + " from=" + message.FromRole + " clip=" + message.ClipId + " hop=" + message.Hop + " root=" + message.RootRole);
            if (message.Kind == "stopped")
            {
                cancellations.RecordStopped(message);
                if (pendingReply != null && pendingReply.ChainId == message.ChainId && message.Hop <= pendingReply.Hop) CancelPendingReply();
                if (activeReplySource != null && activeReplySource.ChainId == message.ChainId && message.Hop <= activeReplySource.Hop) StopPlayback();
                return;
            }
            if (cancellations.IsCancelled(message)) return;
            if (!BridgeRules.ShouldReply("fengge", message)) return;
            CancelPendingReply();
            if (!settings.BridgeEnabled || player != null) return;
            double elapsed = BridgeRules.Elapsed(message);
            if (elapsed > message.Duration + .25) return;
            pendingReply = message;
            pendingReplyClip = catalog.Clips[replyBag.Next(catalog.Clips.Length)];
            // Pre-roll quiet ambience without discarding it; cap compensation to protect weak syllables.
            double preRoll = Math.Min(0.16, audioClips[pendingReplyClip.Id].SpeechStart);
            replyTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.001, message.Duration - preRoll - 0.01 - elapsed));
            replyTimer.Start();
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
                    CompletePlayback(completed);
                }));
            };
        }

        private void CompletePlayback(PlaybackHandle completed)
        {
            if (disposed || player != completed) return;
            endedCount++;
            Trace("ended clip=" + playingClip.Id);
            playbackWatchdog.Stop();
            SetSpeaking(false);
            ShowCaption(playingClip.Label, 0.4);
            player = null;
            activeBridgeEvent = null;
            activeReplySource = null;
        }

        private void Play(Clip clip, BridgeMessage replySource = null)
        {
            StopPlayback();
            activeReplySource = replySource;
            playbackError = null;
            try
            {
                WaveClip wave = audioClips[clip.Id];
                // Announce the estimated voice ending, preserving all audio and limiting tail compensation to 180ms.
                double replyAt = Math.Max(wave.SpeechEnd, wave.Duration - 0.18);
                long requested = Stopwatch.GetTimestamp();
                bool audibleTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--audible-test") >= 0;
                player = audio.Play(wave, smokeTest || (bridgeTest && !audibleTest) ? 0 : settings.Volume);
                playingClip = clip;
                openedCount++;
                if (settings.BridgeEnabled && bridge != null)
                    activeBridgeEvent = replySource == null
                        ? bridge.AnnounceSpeaking(clip.Id, replyAt, player.StartedTimestamp, player.StartedUtc)
                        : bridge.AnnounceReply(clip.Id, replyAt, replySource, player.StartedTimestamp, player.StartedUtc);
                int hop = replySource == null ? 0 : replySource.Hop + 1;
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
            CancelPendingReply();
            if (bridge != null && activeBridgeEvent != null) bridge.AnnounceStopped(activeBridgeEvent);
            activeBridgeEvent = null;
            activeReplySource = null;
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
            tray.Text = "好事桌宠 · 右键设置";
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
            menu.Items.Add(new Forms.ToolStripMenuItem("好事桌宠 · " + catalog.Clips.Length + " 句原声") { Enabled = false });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item("随机说一句", PlayNext));
            Forms.ToolStripMenuItem choose = Item("挑一句", null);
            foreach (Clip clip in catalog.Clips)
            {
                Clip selected = clip;
                choose.DropDownItems.Add(Item(clip.Id.ToString("00") + "  " + clip.Label, delegate { Play(selected); }));
            }
            menu.Items.Add(choose);
            menu.Items.Add(Item("停止播放", StopPlayback));
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
            Forms.ToolStripMenuItem linked = Item(settings.BridgeEnabled ? "关闭捧哏联动" : "开启捧哏联动", delegate
            {
                settings.BridgeEnabled = !settings.BridgeEnabled;
                CancelPendingReply();
                if (!settings.BridgeEnabled && bridge != null && activeBridgeEvent != null)
                {
                    bridge.AnnounceStopped(activeBridgeEvent);
                    activeBridgeEvent = null;
                }
                SaveSettings();
            });
            linked.Checked = settings.BridgeEnabled; menu.Items.Add(linked);
            menu.Items.Add(new Forms.ToolStripMenuItem(bridge == null ? "捧哏联动：通道未启动" : "捧哏：" + (bridge.IsPeerRunning("sunpet") ? "太对了 " : "") + (bridge.IsPeerRunning("shaolan") ? "邵艾伦" : "") + (!bridge.IsPeerRunning("sunpet") && !bridge.IsPeerRunning("shaolan") ? "等待三宠联动版" : "")) { Enabled = false });
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
                    Forms.ToolStripMenuItem choose = (Forms.ToolStripMenuItem)menu.Items[3];
                    if (choose.DropDownItems.Count != catalog.Clips.Length) throw new InvalidOperationException("Clip menu is incomplete");
                }
                Console.WriteLine("OK all clips available in context menu");
                foreach (Clip clip in catalog.Clips)
                {
                    int initialOpened = openedCount, initialEnded = endedCount;
                    Play(clip);
                    await WaitUntil(delegate { return openedCount > initialOpened; }, 6000, "Media did not open: " + clip.File);
                    if (!speaking || captionText.Text != clip.Label) throw new InvalidOperationException("Caption/animation did not start");
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


