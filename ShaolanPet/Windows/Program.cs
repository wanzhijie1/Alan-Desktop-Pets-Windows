using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace ShaolanPet
{
    internal static class Program
    {
        internal static string BridgeTestScope;
        internal static bool IsBridgeTest { get { return BridgeTestScope != null; } }
        internal static string Option(string name, string fallback)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name);
            if (index < 0) return fallback;
            if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--"))
                throw new ArgumentException("Missing value for " + name);
            return arguments[index + 1];
        }
        [STAThread]
        public static int Main(string[] args)
        {
            bool selfTest = Array.IndexOf(args, "--self-test") >= 0;
            bool smokeTest = Array.IndexOf(args, "--smoke-test") >= 0;
            bool uiTest = Array.IndexOf(args, "--ui-test") >= 0;
            string assetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            try
            {
                BridgeTestScope = Option("--bridge-test", null);
                if (selfTest) return CoreTests.Run(assetsPath);
                if (Array.IndexOf(args, "--dump-mode") >= 0)
                {
                    AssetCatalog dumpCatalog = AssetCatalog.Load(assetsPath);
                    Settings dumpSettings = Settings.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaolanPet", "settings.json"));
                    PlaybackSelector dumpSelector = new PlaybackSelector(dumpCatalog.Clips, dumpSettings);
                    Console.WriteLine("mode=" + (dumpSettings.RandomMode ? "random" : "fixed") + " pinned=" + dumpSettings.PinnedId + " pool=" + dumpSelector.AllowedCount);
                    return 0;
                }
                bool created;
                using (Mutex mutex = new Mutex(true, IsBridgeTest ? "Local\\ShaolanPet.Windows.BridgeTest." + BridgeTestScope : smokeTest ? "Local\\ShaolanPet.Windows.SmokeTest" : uiTest ? "Local\\ShaolanPet.Windows.UiTest" : "Local\\ShaolanPet.Windows", out created))
                {
                    if (!created) return 0;
                    try
                    {
                        AssetCatalog catalog = AssetCatalog.Load(assetsPath);
                        string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaolanPet", "settings.json");
                        if (uiTest) settingsPath = Path.Combine(Path.GetTempPath(), "ShaolanPet-UiTest", "settings.json");
                        Settings settings = smokeTest || IsBridgeTest ? new Settings() : Settings.Load(settingsPath);
                        Application app = new Application();
                        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                        app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                        {
                            e.Handled = true;
                            if (smokeTest || IsBridgeTest) Console.Error.WriteLine(e.Exception);
                            else MessageBox.Show("桌宠遇到了问题：\n" + e.Exception.Message, "邵艾伦桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
                            app.Shutdown(1);
                        };
                        return app.Run(new PetWindow(catalog, settings, settingsPath, smokeTest, uiTest));
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                if (selfTest || smokeTest || IsBridgeTest) Console.Error.WriteLine("FAIL: " + ex);
                else MessageBox.Show("邵艾伦桌宠无法启动。请先完整解压程序，再双击 ShaolanPet.exe。\n\n" + ex.Message,
                    "邵艾伦桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(IntPtr window, int index, int value);
        internal static void Move(IntPtr window, int x, int y)
        {
            SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, 0x0015); // no size, z-order or activation change
        }
    }
}
