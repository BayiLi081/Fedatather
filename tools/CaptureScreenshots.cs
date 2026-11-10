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
    // Separate entry point: never loads the real user's browser profile or settings.
    static class Program
    {
        public static readonly string DataDir = Path.Combine(Path.GetTempPath(), "Fedatather-demo-" + Guid.NewGuid().ToString("N"));
        static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
        static object Field(object obj, string name) { return obj.GetType().GetField(name, Flags).GetValue(obj); }
        static object Call(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, Flags).Invoke(obj, args); }
        static async Task Wait(Func<bool> ready)
        {
            for (int i = 0; i < 200; i++) { if (ready()) return; await Task.Delay(100); }
            throw new Exception("Screenshot setup timed out");
        }
        static void Capture(Form form, string path)
        {
            // Capture only this window, never the user's desktop or neighboring windows.
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                IntPtr dc = g.GetHdc();
                try { if (!PrintWindow(form.Handle, dc, 2)) throw new Exception("Window capture failed"); }
                finally { g.ReleaseHdc(dc); }
                bitmap.Save(path, ImageFormat.Png);
            }
        }
        [STAThread]
        static int Main(string[] args)
        {
            string root = args[0];
            string output = Path.Combine(root, "docs", "screenshots");
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "devtools.ini"), "images=C:\\Demo\\Images\ntables=C:\\Demo\\Tables\n");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            BrowserForm form = new BrowserForm(new LaunchOptions { Url = "about:blank" });
            form.ShowInTaskbar = false;
            int result = 1;
            Timer timeout = new Timer { Interval = 90000 };
            timeout.Tick += delegate { Console.Error.WriteLine("Screenshot timeout"); Environment.Exit(1); };
            timeout.Start();
            form.Shown += async delegate
            {
                try
                {
                    await Wait(delegate { return ((Button)Field(form, "newTabButton")).Enabled; });
                    string[] titles = { "Archive", "Reading", "Projects", "Ideas", "Journal", "Field Notes" };
                    for (int i = 0; i < titles.Length; i++)
                    {
                        if (i > 0) await (Task)Call(form, "OpenTabAsync", "about:blank", null);
                        CoreWebView2 page = (CoreWebView2)Field(form, "web");
                        Console.WriteLine("Preparing demo: " + titles[i]);
                        page.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
                        { if (!e.IsSuccess) Console.Error.WriteLine("Demo navigation: " + e.WebErrorStatus); };
                        page.SetVirtualHostNameToFolderMapping("demo.fedatather.test", Path.Combine(root, "docs", "demo"), CoreWebView2HostResourceAccessKind.DenyCors);
                        page.Navigate("https://demo.fedatather.test/index.html?page=" + Uri.EscapeDataString(titles[i]));
                        string title = titles[i];
                        await Wait(delegate { return page.DocumentTitle == title; });
                    }
                    await Task.Delay(800);
                    Capture(form, Path.Combine(output, "phone-tabs.png"));
                    Call(form, "ApplyMode", false);
                    await Task.Delay(600);
                    Capture(form, Path.Combine(output, "desktop.png"));
                    Call(form, "ApplyMode", true);
                    Call(form, "ToggleDevTools");
                    DevToolsLite dev = (DevToolsLite)Field(form, "devTools");
                    await Wait(delegate { return (bool)Field(dev, "uiReady"); });
                    await Task.Delay(1600);
                    // Show the extracted demo table; no filenames, user folders, or login pages.
                    CoreWebView2 panel = (CoreWebView2)Field(dev, "ui");
                    await panel.ExecuteScriptAsync("document.querySelector('[data-tab=\"tables\"]').click()");
                    await Task.Delay(400);
                    Capture(dev, Path.Combine(output, "devtools.png"));
                    dev.Close();
                    Call(form, "ToggleBossMode");
                    await Task.Delay(400);
                    Capture(form, Path.Combine(output, "boss-mode.png"));
                    result = 0;
                    Console.WriteLine("Captured four isolated demo screenshots.");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { form.Close(); }
            };
            Application.Run(form);
            timeout.Dispose();
            return result;
        }
    }
}
