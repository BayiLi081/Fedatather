using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Fedatather
{
    // Separate entry point: never loads the real user's browser profile or settings. Every page shown is the local,
    // fictional demo in docs\demo (served as https://demo.fedatather.test/), and only the app's own windows are captured.
    static class Program
    {
        public static readonly string DataDir = Path.Combine(Path.GetTempPath(), "Fedatather-demo-" + Guid.NewGuid().ToString("N"));
        const string DemoHost = "demo.fedatather.test";
        static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static string root, output;
        static int captured;

        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
        static object Field(object obj, string name) { return obj.GetType().GetField(name, Flags).GetValue(obj); }
        static object Call(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, Flags).Invoke(obj, args); }

        static async Task Wait(Func<bool> ready)
        {
            for (int i = 0; i < 200; i++) { if (ready()) return; await Task.Delay(100); }
            throw new Exception("Screenshot setup timed out");
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle, out RECT rect);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out RECT rect, int size);

        static void Capture(Form form, string name)
        {
            // Capture only this window, never the user's desktop or neighboring windows.
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    IntPtr dc = g.GetHdc();
                    try { if (!PrintWindow(form.Handle, dc, 2)) throw new Exception("Window capture failed"); }
                    finally { g.ReleaseHdc(dc); }
                }
                // Windows with a normal frame have an invisible resize border, captured as black bars: keep only the
                // visible frame (DWMWA_EXTENDED_FRAME_BOUNDS).
                RECT whole, visible;
                Rectangle keep = new Rectangle(Point.Empty, bitmap.Size);
                if (GetWindowRect(form.Handle, out whole) &&
                    DwmGetWindowAttribute(form.Handle, 9, out visible, Marshal.SizeOf(typeof(RECT))) == 0)
                    keep = Rectangle.Intersect(keep, new Rectangle(visible.Left - whole.Left, visible.Top - whole.Top,
                        visible.Right - visible.Left, visible.Bottom - visible.Top));
                using (Bitmap cropped = bitmap.Clone(keep, bitmap.PixelFormat))
                    cropped.Save(Path.Combine(output, name), ImageFormat.Png);
            }
            captured++;
            Console.WriteLine("Captured " + name);
        }

        static string DemoUrl(string title) { return "https://" + DemoHost + "/index.html?page=" + Uri.EscapeDataString(title); }

        // Points a page at the local demo folder and opens the demo under the given title.
        static async Task ShowDemo(CoreWebView2 page, string title)
        {
            page.SetVirtualHostNameToFolderMapping(DemoHost, Path.Combine(root, "docs", "demo"), CoreWebView2HostResourceAccessKind.DenyCors);
            page.Navigate(DemoUrl(title));
            await Wait(delegate { return page.DocumentTitle == title; });
        }

        static CoreWebView2 Web(object tab) { return (CoreWebView2)tab.GetType().GetField("Web").GetValue(tab); }

        [STAThread]
        static int Main(string[] args)
        {
            root = args[0];
            output = Path.Combine(root, "docs", "screenshots");
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "devtools.ini"), "images=C:\\Demo\\Images\ntables=C:\\Demo\\Tables\n");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            BrowserForm form = new BrowserForm(new LaunchOptions { Url = "about:blank" });
            form.ShowInTaskbar = false;
            int result = 1;
            Timer timeout = new Timer { Interval = 240000 };
            timeout.Tick += delegate { Console.Error.WriteLine("Screenshot timeout"); Environment.Exit(1); };
            timeout.Start();
            form.Shown += async delegate
            {
                try
                {
                    await Run(form);
                    result = 0;
                    Console.WriteLine("Captured " + captured + " isolated demo screenshots.");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { if (!form.IsDisposed) form.Close(); }
            };
            Application.Run(form);
            timeout.Dispose();
            return result;
        }

        static async Task Run(BrowserForm form)
        {
            await Wait(delegate { return ((Button)Field(form, "newTabButton")).Enabled; });
            IList tabs = (IList)Field(form, "tabs");

            // Six pages: the oldest folds away, so the strip shows five tabs and "Folded (1)".
            string[] titles = { "Archive", "Reading", "Projects", "Ideas", "Journal", "Field Notes" };
            for (int i = 0; i < titles.Length; i++)
            {
                if (i > 0) await (Task)Call(form, "OpenTabAsync", "about:blank", null);
                Console.WriteLine("Preparing demo: " + titles[i]);
                await ShowDemo((CoreWebView2)Field(form, "web"), titles[i]);
            }
            await Task.Delay(800);
            Capture(form, "phone-tabs.png");

            // Selecting text shows the Translate / Ask AI bar.
            CoreWebView2 web = (CoreWebView2)Field(form, "web");
            await web.ExecuteScriptAsync(@"(function () {
  var p = document.querySelector('.hero p'), r = document.createRange();
  r.selectNodeContents(p);
  var s = getSelection(); s.removeAllRanges(); s.addRange(r);
  var b = r.getBoundingClientRect();
  window.dispatchEvent(new MouseEvent('mouseup', { button: 0, bubbles: true, clientX: b.right - 4, clientY: b.bottom - 4 }));
})()");
            await Task.Delay(700);
            Capture(form, "selection-bar.png");
            await web.ExecuteScriptAsync("getSelection().removeAllRanges(); window.dispatchEvent(new MouseEvent('mousedown', { button: 0 }))");

            // The terminal. The prompt is replaced and PSReadLine's suggestions (drawn from the real history) are
            // turned off before anything is captured, so no user name, folder or past command shows.
            Call(form, "ShowTerminal");
            TerminalPanel terminal = (TerminalPanel)Field(form, "terminal");
            await Task.Delay(2500);
            terminal.View.Write("Remove-Module PSReadLine -ErrorAction SilentlyContinue; function prompt { 'PS C:\\Demo> ' }; Set-Location \\; Clear-Host\r");
            await Task.Delay(1500);
            terminal.View.Write("'Hello from the Fedatather terminal'\r");
            await Task.Delay(500);
            terminal.View.Write("1..3 | ForEach-Object { \"Step $_ of 3\" }\r");
            await Task.Delay(1500);
            Capture(form, "terminal.png");
            Call(form, "HideTerminal");
            await Task.Delay(300);

            // Phone size split top and bottom.
            Call(form, "EnterSplit");
            await Task.Delay(1200);
            Capture(form, "split-phone.png");

            // Saving the phone split as a dual app.
            object[] panes = (object[])Field(form, "panes");
            Theme theme = (Theme)Field(form, "theme");
            float scale = (float)Field(form, "scale");
            using (SaveAppDialog dual = new SaveAppDialog(theme, scale, "Field Notes", Web(panes[0]).Source,
                Web(panes[1]).Source, false, Task.FromResult<Bitmap>(null)))
            {
                dual.StartPosition = FormStartPosition.Manual;
                dual.Location = new Point(form.Left + 40, form.Top + 120);
                dual.Show(form);
                await Task.Delay(500);
                Capture(dual, "save-dual-app.png");
                dual.Close();
            }
            Call(form, "ExitSplit");

            // Saving one page as an app.
            using (SaveAppDialog single = new SaveAppDialog(theme, scale, "Field Notes", web.Source, null, false,
                Task.FromResult<Bitmap>(null)))
            {
                single.StartPosition = FormStartPosition.Manual;
                single.Location = new Point(form.Left + 40, form.Top + 120);
                single.Show(form);
                await Task.Delay(500);
                Capture(single, "save-app.png");
                single.Close();
            }

            // Full screen, then full-screen split screen.
            Call(form, "ApplyMode", false);
            await Task.Delay(800);
            Capture(form, "desktop.png");
            Call(form, "EnterSplit");
            await Task.Delay(1500);
            Capture(form, "split-full.png");
            Call(form, "ExitSplit");
            Call(form, "ApplyMode", true);
            await Task.Delay(500);

            // DevTools Lite: the images list and the extracted table. No filenames, user folders or login pages.
            Call(form, "ToggleDevTools");
            DevToolsLite dev = (DevToolsLite)Field(form, "devTools");
            await Wait(delegate { return (bool)Field(dev, "uiReady"); });
            await Task.Delay(1600);
            Capture(dev, "devtools-images.png");
            CoreWebView2 panel = (CoreWebView2)Field(dev, "ui");
            await panel.ExecuteScriptAsync("document.querySelector('[data-tab=\"tables\"]').click()");
            await Task.Delay(400);
            Capture(dev, "devtools.png");
            Call(form, "ToggleDevTools");
            await Task.Delay(300);

            // Settings: Passwords (nothing saved in this demo profile) and Privacy.
            Call(form, "OpenSettings", true, Rectangle.Empty);
            Form settings = (Form)Field(form, "settingsWindow");
            await Wait(delegate { return Field(settings, "ui") != null; });
            CoreWebView2 settingsUi = (CoreWebView2)Field(settings, "ui");
            await Wait(delegate { return settingsUi.DocumentTitle == "Settings"; });
            await Task.Delay(1500);
            Capture(settings, "settings-passwords.png");
            await settingsUi.ExecuteScriptAsync("document.querySelector('nav [data-section=\"privacy\"]').click()");
            await Task.Delay(800);
            Capture(settings, "settings-privacy.png");
            settings.Close();
            await Task.Delay(300);

            // A saved web app, and a dual web app, as their shortcuts open them.
            await CaptureApp(new LaunchOptions { AppId = "field-notes-demo", AppName = "Field Notes", Url = "about:blank", Size = "phone" },
                new[] { "Field Notes" }, "web-app.png");
            await CaptureApp(new LaunchOptions { AppId = "dual-demo", AppName = "Notes + Reading", Url = "about:blank", Url2 = "about:blank", Size = "phone" },
                new[] { "Field Notes", "Reading" }, "dual-app.png");

            Call(form, "ToggleBossMode");
            await Task.Delay(400);
            Capture(form, "boss-mode.png");
        }

        // Opens a saved-app window, points its pages at the local demo and captures it.
        static async Task CaptureApp(LaunchOptions options, string[] titles, string name)
        {
            BrowserForm app = new BrowserForm(options);
            app.ShowInTaskbar = false;
            // A saved app hands navigations after its first page to the default browser; never do that from here.
            app.GetType().GetField("openExternalLink", Flags).SetValue(app, new Action<string>(delegate { }));
            app.Show();
            try
            {
                IList tabs = (IList)Field(app, "tabs");
                await Wait(delegate { return tabs.Count == titles.Length; });
                // Let the app's own first load (about:blank) finish, so it can't overtake the demo page.
                foreach (object tab in tabs)
                {
                    object t = tab;
                    await Wait(delegate { return (bool)t.GetType().GetField("InitialNavigationComplete").GetValue(t); });
                    // Then let the demo page count as the app's own first page rather than a link to hand off.
                    t.GetType().GetField("InitialNavigationComplete").SetValue(t, false);
                }
                for (int i = 0; i < titles.Length; i++) await ShowDemo(Web(tabs[i]), titles[i]);
                await Task.Delay(1000);
                Capture(app, name);
            }
            finally { app.Close(); }
        }
    }
}
