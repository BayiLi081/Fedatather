using System;
using System.IO;
using System.Windows.Forms;

// csc doesn't stamp this by itself; without it the runtime falls back to .NET 4.0-era defaults
// (no TLS 1.2 for icon downloads, no PNG frames in .ico files).
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace Fedatather
{
    static class Program
    {
        // Cookies, cache and settings live here, so the app folder itself can be read-only / portable.
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fedatather");

        [STAThread]
        static void Main(string[] args)
        {
            LaunchOptions options = LaunchOptions.Parse(args);

            // Each saved web app gets its own taskbar identity (icon, grouping, pinning).
            // Must happen before any window exists.
            if (options.IsApp)
            {
                try { Native.SetCurrentProcessExplicitAppUserModelID(WebApps.AppUserModelId(options.AppId)); }
                catch { }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(DataDir);
            Application.Run(new BrowserForm(options));
        }
    }
}
