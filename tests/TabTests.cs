using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Fedatather
{
    // The test executable substitutes this entry point for Program.cs so user data stays untouched.
    static class Program
    {
        public static readonly string DataDir = Path.Combine(Path.GetTempPath(), "Fedatather-tabs-" + Guid.NewGuid().ToString("N"));
        static int checks;
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        static void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks++;
            Console.WriteLine("PASS: " + message);
        }
        static object Field(object obj, string name) { return obj.GetType().GetField(name, Private | BindingFlags.Public).GetValue(obj); }
        static object Call(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, Private).Invoke(obj, args); }
        static string Page(int i) { return new Uri(Path.Combine(DataDir, "page" + i + ".html")).AbsoluteUri; }
        static async Task Wait(Func<bool> ready)
        {
            for (int i = 0; i < 200; i++)
            {
                if (ready()) return;
                await Task.Delay(100);
            }
            throw new Exception("Timed out waiting for page state");
        }
        static async Task Open(BrowserForm form, string url, FoldedPage restore)
        {
            await (Task)Call(form, "OpenTabAsync", url, restore);
        }
        static void StoreTests()
        {
            string path = Path.Combine(DataDir, "store.json");
            FoldedTabs store = new FoldedTabs(path);
            FoldedPage first = new FoldedPage { Title = "中文 & \"quoted\"\nTitle", Url = "https://example.com/?a=1&b=2#fragment" };
            store.Add(first);
            store.Add(new FoldedPage { Title = "Duplicate URL", Url = first.Url });
            FoldedTabs reloaded = new FoldedTabs(path);
            Check(reloaded.Pages.Count == 2 && reloaded.Pages[0].Title == first.Title && reloaded.Pages[0].Url == first.Url,
                "Titles, URL fragments, Unicode, and duplicate tabs survive restart");
            store.Remove(first);
            Check(new FoldedTabs(path).Pages.Count == 1, "Restoring removes exactly one saved entry durably");
            string blocker = Path.Combine(DataDir, "blocker");
            File.WriteAllText(blocker, "file blocks directory creation");
            FoldedTabs broken = new FoldedTabs(Path.Combine(blocker, "store.json"));
            bool failed = false;
            try { broken.Add(first); } catch (IOException) { failed = true; }
            Check(failed && broken.Pages.Count == 0, "Failed save rolls back instead of losing the live page");
        }

        [STAThread]
        static int Main()
        {
            Directory.CreateDirectory(DataDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            StoreTests();
            for (int i = 1; i <= 10; i++)
                File.WriteAllText(Path.Combine(DataDir, "page" + i + ".html"),
                    "<!doctype html><title>Page " + i + "</title><input id='draft'><a target='_blank' href='page7.html'>Open</a>");
            BrowserForm form = new BrowserForm(new LaunchOptions { Url = Page(1) });
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            int result = 1;
            Timer timeout = new Timer { Interval = 60000 };
            timeout.Tick += delegate { Console.Error.WriteLine("FAIL: test suite timeout"); Environment.Exit(1); };
            timeout.Start();
            form.Shown += async delegate
            {
                try
                {
                    IList tabs = (IList)Field(form, "tabs");
                    FoldedTabs folded = (FoldedTabs)Field(form, "folded");
                    await Wait(delegate { return tabs.Count == 1 && (string)Field(tabs[0], "Title") == "Page 1"; });
                    for (int i = 2; i <= 5; i++) await Open(form, Page(i), null);
                    await Wait(delegate { return (string)Field(tabs[4], "Title") == "Page 5"; });
                    Check(tabs.Count == 5 && folded.Pages.Count == 0, "Five pages stay live without folding");
                    object first = tabs[0];
                    Call(form, "ActivateTab", first);
                    Check((object)Field(form, "activeTab") == first, "Selecting an older tab activates its own view");
                    await Open(form, Page(6), null);
                    await Wait(delegate { return (string)Field(tabs[4], "Title") == "Page 6"; });
                    Check(tabs.Count == 5 && !tabs.Contains(first) && folded.Pages.Count == 1 && folded.Pages[0].Url == Page(1),
                        "Sixth page folds oldest by opening order even when it was selected");
                    Check(((CoreWebView2)Field(form, "web")).Source == Page(6), "Address and active page follow newest tab");
                    object second = tabs[0];
                    Call(form, "ActivateTab", second);
                    CoreWebView2 secondWeb = (CoreWebView2)Field(form, "web");
                    await secondWeb.ExecuteScriptAsync("document.getElementById('draft').value='kept draft'");
                    Call(form, "ActivateTab", tabs[4]);
                    Call(form, "ActivateTab", second);
                    Check(await secondWeb.ExecuteScriptAsync("document.getElementById('draft').value") == "\"kept draft\"",
                        "Switching live tabs preserves form state");
                    await secondWeb.ExecuteScriptAsync("window.open('page7.html','_blank')");
                    await Wait(delegate { return tabs.Count == 5 && folded.Pages.Count == 2 && (string)Field(tabs[4], "Title") == "Page 7"; });
                    Check(tabs.Count == 5 && folded.Pages[1].Url == Page(2), "Popup from oldest tab safely opens new tab and folds its source");
                    FoldedPage saved = folded.Pages[0];
                    await Open(form, saved.Url, saved);
                    await Wait(delegate { return (string)Field(tabs[4], "Title") == "Page 1"; });
                    Check(tabs.Count == 5 && folded.Pages.Count == 2 && folded.Pages[0].Url == Page(2) && folded.Pages[1].Url == Page(3),
                        "Restoring at capacity removes saved link and folds next oldest");
                    await Task.WhenAll(Open(form, Page(8), null), Open(form, Page(9), null), Open(form, Page(10), null));
                    Check(tabs.Count == 5 && folded.Pages.Count == 5 && (string)Field(tabs[4], "Url") == Page(10),
                        "Rapid opens serialize and retain the five most recently opened pages");
                    Check(new FoldedTabs(Path.Combine(DataDir, "settings.ini.folded.json")).Pages.Count == 5,
                        "Automatically folded pages are persisted to disk");
                    Call(form, "CloseActiveTab");
                    Check(tabs.Count == 4 && folded.Pages.Count == 5, "Manual tab close does not create a folded entry");
                    Panel strip = (Panel)Field(form, "tabBar");
                    Call(form, "SetHtmlFullscreen", true);
                    Check(!strip.Visible, "Page fullscreen hides tab strip");
                    Call(form, "SetHtmlFullscreen", false);
                    Check(strip.Visible, "Leaving page fullscreen restores tab strip");
                    await Wait(delegate { return (string)Field(Field(form, "activeTab"), "Title") == "Page 9"; });
                    object originalTab = Field(form, "activeTab");
                    CoreWebView2 originalWeb = (CoreWebView2)Field(form, "web");
                    CoreWebView2Controller originalController = (CoreWebView2Controller)Field(form, "controller");
                    await originalWeb.ExecuteScriptAsync("document.getElementById('draft').value='boss draft'");
                    IconButton boss = (IconButton)Field(form, "bossBtn");
                    boss.PerformClick();
                    PictureBox cover = (PictureBox)Field(form, "bossCover");
                    Check((bool)Field(form, "bossMode") && cover.Visible && cover.Image != null && boss.Parent == cover && boss.Visible,
                        "Human-icon boss button displays embedded image and remains available to restore");
                    Check(!strip.Visible && !((Panel)Field(form, "host")).Visible && !originalController.IsVisible,
                        "Boss mode hides tabs, toolbar, and native browser view");
                    Check(form.Text == "Fedatather" && (bool)Call(form, "HandleShortcut", Keys.Control | Keys.T, false),
                        "Boss mode hides page title and blocks browser shortcuts");
                    Call(form, "SetHtmlFullscreen", true);
                    Call(form, "SetHtmlFullscreen", false);
                    Call(form, "OnResize", EventArgs.Empty);
                    Call(form, "FocusWeb");
                    Check(cover.Visible && !strip.Visible && !originalController.IsVisible,
                        "Fullscreen, resize, and focus callbacks cannot reveal hidden browser");
                    boss.PerformClick();
                    Check(!(bool)Field(form, "bossMode") && !cover.Visible && strip.Visible && originalController.IsVisible
                        && Field(form, "activeTab") == originalTab && tabs.Count == 4 && folded.Pages.Count == 5,
                        "Clicking boss button again restores original tabs and active view");
                    Check(await originalWeb.ExecuteScriptAsync("document.getElementById('draft').value") == "\"boss draft\"",
                        "Boss mode preserves live page form contents");
                    Console.WriteLine("All " + checks + " checks passed.");
                    result = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { form.Close(); }
            };
            Application.Run(form);
            timeout.Dispose();
            Console.WriteLine("Isolated test data: " + DataDir);
            return result;
        }
    }
}
