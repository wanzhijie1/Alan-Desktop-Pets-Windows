using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace GoodThingPet
{
    internal static class Program
    {
        internal static string ArgumentValue(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            bool selfTest = Array.IndexOf(args, "--self-test") >= 0;
            bool smokeTest = Array.IndexOf(args, "--smoke-test") >= 0;
            bool uiTest = Array.IndexOf(args, "--ui-test") >= 0;
            bool bridgeTest = Array.IndexOf(args, "--bridge-test") >= 0;
            string assetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            try
            {
                if (selfTest) return CoreTests.Run(assetsPath);
                bool created;
                using (Mutex mutex = new Mutex(true, bridgeTest ? "Local\\GoodThingPet.Windows.BridgeTest." + ArgumentValue("--bridge-test", "test") : smokeTest ? "Local\\GoodThingPet.Windows.SmokeTest" : uiTest ? "Local\\GoodThingPet.Windows.UiTest" : "Local\\GoodThingPet.Windows", out created))
                {
                    if (!created) return 0;
                    try
                    {
                        AssetCatalog catalog = AssetCatalog.Load(assetsPath);
                        string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoodThingPet", "settings.json");
                        if (uiTest) settingsPath = Path.Combine(Path.GetTempPath(), "GoodThingPet-UiTest", "settings.json");
                        Settings settings = (smokeTest || bridgeTest) ? new Settings() : Settings.Load(settingsPath);
                        Application app = new Application();
                        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                        app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                        {
                            e.Handled = true;
                            if (smokeTest || bridgeTest) Console.Error.WriteLine(e.Exception);
                            else MessageBox.Show("桌宠遇到了问题：\n" + e.Exception.Message, "好事桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
                            app.Shutdown(1);
                        };
                        return app.Run(new PetWindow(catalog, settings, settingsPath, smokeTest, uiTest));
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                if (selfTest || smokeTest || bridgeTest) Console.Error.WriteLine("FAIL: " + ex);
                else MessageBox.Show("好事桌宠无法启动。请先完整解压程序，再双击 GoodThingPet.exe。\n\n" + ex.Message,
                    "好事桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
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

