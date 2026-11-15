using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
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
        static void SetField(object obj, string name, object value) { obj.GetType().GetField(name, Private | BindingFlags.Public).SetValue(obj, value); }
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

        static VtScreen Screen(int cols, int rows, string output)
        {
            VtScreen s = new VtScreen(cols, rows);
            s.Feed(output);
            return s;
        }
        static bool Settings_KindsCheck()
        {
            return SettingsForm.KindsFor(true, false, false, false) == CoreWebView2BrowsingDataKinds.AllSite
                && (SettingsForm.KindsFor(false, true, false, false) & CoreWebView2BrowsingDataKinds.BrowsingHistory) != 0
                && SettingsForm.KindsFor(false, false, true, false) == CoreWebView2BrowsingDataKinds.DiskCache
                && (SettingsForm.KindsFor(false, false, false, false)) == 0
                && (SettingsForm.KindsFor(true, true, true, true) & CoreWebView2BrowsingDataKinds.GeneralAutofill) != 0;
        }
        static CoreWebView2Controller ViewOf(object tab) { return (CoreWebView2Controller)Field(tab, "Controller"); }
        static int VisibleViews(IList tabs)
        {
            int n = 0;
            foreach (object tab in tabs) if (ViewOf(tab).IsVisible) n++;
            return n;
        }
        static bool ScreenHas(TerminalView view, string text)
        {
            for (int i = 0; i < view.RowCount; i++) if (view.RowText(i).Contains(text)) return true;
            return false;
        }
        static void TerminalParserTests()
        {
            VtScreen s = Screen(20, 5, "hello\r\nworld");
            Check(s.RowText(0) == "hello" && s.RowText(1) == "world" && s.CursorX == 5 && s.CursorY == 1,
                "Terminal prints text and handles CR LF");
            s = Screen(20, 3, "abcdef\u001b[1;3H\u001b[K\u001b[2;5HX");
            Check(s.RowText(0) == "ab" && s.RowText(1) == "    X", "Terminal cursor positioning and erase in line");
            s = Screen(20, 3, "\u001b[31;1mR\u001b[0mN\u001b[38;2;1;2;3mT\u001b[48;5;200mP");
            Cell[] c = s.GetLine(0).Cells;
            Check(c[0].Fg == 2 && (c[0].Attr & VtScreen.Bold) != 0 && c[1].Fg == 0 && c[1].Attr == 0 &&
                c[2].Fg == (VtScreen.TrueColor | 0x010203) && c[3].Bg == 201, "Terminal SGR: 16 colours, true colour, 256 colours, reset");
            s = Screen(10, 3, "中文a");
            c = s.GetLine(0).Cells;
            Check(c[0].Ch == 0x4E2D && c[1].Ch == -1 && c[2].Ch == 0x6587 && c[3].Ch == -1 && c[4].Ch == 'a' && s.CursorX == 5,
                "Terminal gives Chinese characters two cells");
            s = Screen(10, 3, "0123456789AB");
            Check(s.RowText(0) == "0123456789" && s.GetLine(0).Wrapped && s.RowText(1) == "AB", "Terminal wraps at the last column");
            s = Screen(10, 3, "1\r\n2\r\n3\r\n4\r\n5");
            Check(s.LineCount == 5 && s.Scrolled == 2 && s.RowText(0) == "3" && VtScreen.LineText(s.GetLine(0), 0, 10).Trim() == "1",
                "Terminal keeps lines scrolled off the top as scrollback");
            s = Screen(10, 5, "a\r\nb\r\nc\u001b[2;3r\u001b[3;1H\n\nx\u001b[r");
            Check(s.RowText(0) == "a" && s.RowText(1) == "" && s.RowText(2) == "x" && s.LineCount == 5,
                "Terminal scroll regions scroll only their own rows");
            s = Screen(10, 3, "main\u001b[?1049h\u001b[2J\u001b[Halt");
            Check(s.AltActive && s.RowText(0) == "alt", "Terminal switches to the alternate screen");
            s.Feed("\u001b[?1049l");
            Check(!s.AltActive && s.RowText(0) == "main", "Terminal returns to the main screen intact");
            string reply = null;
            s = new VtScreen(10, 3);
            s.Reply += delegate(string r) { reply = r; };
            s.Feed("ab\r\nc\u001b[6n");
            Check(reply == "\u001b[2;2R", "Terminal answers cursor position queries");
            s = Screen(10, 5, "prompt> ");
            s.Resize(6, 2);
            Check(s.Cols == 6 && s.Rows == 2 && s.RowText(0) == "prompt" && s.CursorY == 0 && s.CursorX == 5,
                "Terminal resize keeps the cursor line on screen");
        }

        static void PasswordTests()
        {
            PasswordVault.Iterations = 1000; // the real 150,000 would make ten PIN tries take several seconds
            int skipped;
            string csv = "﻿name,url,username,password,note\r\n" +
                "example.com,https://example.com/login,alice,\"p,a\"\"ss\nword\",first\r\n" +
                "Shop,https://www.shop.test/,bob@mail.test,s3cret,\r\n" +
                "No password,https://none.test/,carol,,\r\n" +
                "中文,https://cn.test/,用户,密码,\r\n";
            List<SavedLogin> rows = LoginCsv.Parse(csv, out skipped);
            Check(rows.Count == 3 && skipped == 1 && rows[0].Password == "p,a\"ss\nword" && rows[0].Username == "alice" &&
                rows[0].Note == "first" && rows[2].Username == "用户" && rows[2].Password == "密码",
                "Edge password CSV: quotes, commas, line breaks, Chinese; rows without a password skipped");
            List<SavedLogin> firefox = LoginCsv.Parse("\"url\",\"username\",\"password\",\"httpRealm\"\n\"https://ff.test\",\"dan\",\"pw\",\"\"\n", out skipped);
            Check(firefox.Count == 1 && firefox[0].Username == "dan" && firefox[0].Name == "ff.test", "Firefox password CSV");
            bool refused = false;
            try { LoginCsv.Parse("a,b\n1,2\n", out skipped); }
            catch (FormatException) { refused = true; }
            Check(refused, "A CSV without website and password columns is refused");

            Check(PasswordVault.Matches("https://example.com/login", "https://example.com/other") &&
                PasswordVault.Matches("https://example.com", "https://accounts.example.com/") &&
                PasswordVault.Matches("https://www.example.com", "https://example.com/") &&
                !PasswordVault.Matches("https://example.com", "http://example.com/") &&
                !PasswordVault.Matches("https://example.com", "https://notexample.com/") &&
                !PasswordVault.Matches("https://example.com", "https://example.com.evil.test/") &&
                !PasswordVault.Matches("android://key@com.app/", "https://com.app/"),
                "Logins match only their own site and its subdomains: never plain http or look-alike sites");

            Check(SiteGroup.Of("https://auth.1point3acres.com/login") == "1point3acres.com" &&
                SiteGroup.Of("https://www.1point3acres.com/bbs/member.php") == "1point3acres.com" &&
                SiteGroup.Of("https://mail.163.com/") == "163.com" && SiteGroup.Of("https://kyfw.12306.cn/") == "12306.cn" &&
                SiteGroup.Of("https://www.example.com.cn/") == "example.com.cn" && SiteGroup.Of("https://bbc.co.uk/") == "bbc.co.uk" &&
                SiteGroup.Of("http://192.168.1.1/") == "192.168.1.1" && SiteGroup.Of("android://key@com.tencent.mm/") == "com.tencent.mm",
                "Sites are grouped as Edge does: subdomains under their site, two-part endings like com.cn kept");
            Check(SettingsForm.NormalizeUrl("example.com/login") == "https://example.com/login" &&
                SettingsForm.NormalizeUrl("http://a.test") == "http://a.test/" && SettingsForm.NormalizeUrl("ftp://a.test") == null &&
                SettingsForm.NormalizeUrl("  ") == null, "Typed site addresses get https:// and must be web addresses");

            string path = Path.Combine(DataDir, "vault-test.dat");
            PasswordVault vault = new PasswordVault(path);
            vault.Create("123456");
            int added, updated, unchanged;
            vault.Import(rows, out added, out updated, out unchanged);
            Check(added == 3 && updated == 0, "Import adds new logins");
            vault.Import(LoginCsv.Parse(csv.Replace("s3cret", "changed"), out skipped), out added, out updated, out unchanged);
            Check(added == 0 && updated == 1 && unchanged == 2, "Importing again updates changed passwords without duplicates");
            SavedLogin extra = new SavedLogin { Url = "https://example.com/", Username = "zed", Password = "z" };
            vault.Add(extra);
            bool duplicate = false;
            try { vault.Add(new SavedLogin { Url = "https://example.com/other", Username = "zed", Password = "y" }); }
            catch (InvalidOperationException) { duplicate = true; }
            vault.Update(extra, new SavedLogin { Url = "https://example.com/", Username = "zed", Password = "zz", Note = "edited" });
            bool collision = false;
            try { vault.Update(vault.FindFor("https://example.com/").Find(delegate(SavedLogin l) { return l.Username == "zed"; }),
                new SavedLogin { Url = "https://example.com/", Username = "alice", Password = "q" }); }
            catch (InvalidOperationException) { collision = true; }
            SavedLogin zed = vault.Logins.Find(delegate(SavedLogin l) { return l.Username == "zed"; });
            vault.Delete(zed);
            Check(duplicate && collision && zed.Password == "zz" && zed.Note == "edited" && vault.Logins.Count == 3,
                "Add, edit and delete; the same account can't be saved twice for a site");
            string raw = File.ReadAllText(path);
            Check(!raw.Contains("changed") && !raw.Contains("alice") && !raw.Contains("shop.test") && !raw.Contains("123456"),
                "The passwords file holds no readable passwords, usernames, sites or PIN");
            vault.Lock();
            Check(!vault.IsUnlocked && vault.HasLoginsFor("https://shop.test/cart") && !vault.HasLoginsFor("https://other.test/"),
                "While locked, the key icon still knows which sites have logins");
            int left;
            Check(vault.Unlock("000000", out left) == UnlockResult.WrongPin && left == 9 && !vault.IsUnlocked,
                "A wrong PIN is refused and counted");
            PasswordVault reopened = new PasswordVault(path);
            Check(reopened.AttemptsLeft == 9, "The wrong-PIN count survives a restart");
            Check(reopened.Unlock("123456", out left) == UnlockResult.Unlocked && reopened.AttemptsLeft == 10 &&
                reopened.Logins.Count == 3 && reopened.FindFor("https://www.shop.test/login")[0].Password == "changed",
                "The right PIN unlocks the logins and resets the count");
            reopened.Lock();
            UnlockResult result = UnlockResult.Unlocked;
            for (int i = 0; i < PasswordVault.MaxAttempts; i++) result = reopened.Unlock("999999", out left);
            Check(result == UnlockResult.Erased && !File.Exists(path) && !reopened.Exists,
                "Ten wrong PINs in a row erase the saved passwords for good");
        }

        static async Task WebAppTests()
        {
            string[] scripts = { "selection.js", "devtools-scroll.js", "devtools-table.js", "devtools-expand.js",
                "devtools.html", "settings.html", "app-icon.js" };
            bool scriptsOk = true;
            foreach (string name in scripts)
            {
                string text = Embedded.Text(name);
                if (text.Length < 200 || text.Contains("\"\"\"")) scriptsOk = false;
            }
            Check(scriptsOk && Embedded.Text("settings.html").StartsWith("<!doctype html>"),
                "Every built-in page script loads from the exe as UTF-8");

            BrowserForm app = new BrowserForm(new LaunchOptions { AppId = "single-page-test", Url = Page(1), Size = "full" });
            app.ShowInTaskbar = false;
            app.Opacity = 0;
            try
            {
                app.Show();
                IList tabs = (IList)Field(app, "tabs");
                await Wait(delegate { return tabs.Count == 1 && (string)Field(tabs[0], "Title") == "Page 1"; });
                Panel strip = (Panel)Field(app, "tabBar");
                Check(!strip.Visible && !((Button)Field(app, "newTabButton")).Enabled &&
                    !((IconButton)Field(app, "splitBtn")).Visible, "Saved app hides tabs and split screen controls");
                object original = tabs[0];
                Check((bool)Call(app, "HandleShortcut", Keys.Control | Keys.T, false) &&
                    (bool)Call(app, "HandleShortcut", Keys.Control | Keys.T, true), "Saved app consumes Ctrl+T from toolbar and webpage");
                await Task.WhenAll(Open(app, Page(2), null), Open(app, Page(3), null));
                Call(app, "HandleShortcut", Keys.Control | Keys.OemPipe, true);
                Call(app, "ToggleSplit");
                await Task.Delay(150);
                Check(tabs.Count == 1 && tabs[0] == original && !(bool)Field(app, "splitMode"),
                    "Saved app rejects direct and concurrent extra tabs and split screen");
                CoreWebView2 page = (CoreWebView2)Field(app, "web");
                List<string> external = new List<string>();
                SetField(app, "openExternalLink", new Action<string>(delegate(string url) { external.Add(url); }));
                await page.ExecuteScriptAsync("document.querySelector('a').href='https://example.com/new-tab'");
                await page.ExecuteScriptAsync("document.querySelector('a').click()");
                await Wait(delegate { return external.Count == 1; });
                Check(external[0] == "https://example.com/new-tab" && page.Source == Page(1),
                    "Saved app target blank link opens default browser and leaves app unchanged");
                await page.ExecuteScriptAsync("window.open('https://example.com/popup','_blank','width=400,height=300')");
                await Wait(delegate { return external.Count == 2; });
                Check(external[1] == "https://example.com/popup" && tabs.Count == 1,
                    "Saved app sized popup opens default browser without an app tab");
                await page.ExecuteScriptAsync("document.body.innerHTML='<a href=\"https://example.com/same-window\" style=\"position:fixed;left:0;top:0;width:200px;height:100px\">Link</a>'");
                await page.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mousePressed\",\"x\":20,\"y\":20,\"button\":\"left\",\"clickCount\":1}");
                await page.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseReleased\",\"x\":20,\"y\":20,\"button\":\"left\",\"clickCount\":1}");
                await Wait(delegate { return external.Count == 3; });
                Check(external[2] == "https://example.com/same-window" && page.Source == Page(1),
                    "Saved app ordinary link opens default browser without navigating app");
                Call(app, "OpenAppLink", "file:///C:/Windows/notepad.exe");
                await Task.Delay(150);
                Check(external.Count == 3 && ((FoldedTabs)Field(app, "folded")).Pages.Count == 0,
                    "Saved app external link handler rejects local executable paths and never folds tabs");
                Call(app, "SetHtmlFullscreen", true);
                Call(app, "SetHtmlFullscreen", false);
                Call(app, "ToggleBossMode");
                Call(app, "ToggleBossMode");
                Check(!strip.Visible, "Saved app keeps tabs hidden after fullscreen and boss mode");
                Call(app, "CloseActiveTab");
                Check(app.IsDisposed, "Closing a saved app's only page closes its window");
            }
            finally { if (!app.IsDisposed) app.Close(); }

            LaunchOptions dualArgs = LaunchOptions.Parse(new[] { "--app=dual-test", "--url=" + Page(1), "--url2=" + Page(2) });
            Check(dualArgs.IsDual && dualArgs.Size == "phone" && dualArgs.Url2 == Page(2),
                "A dual app shortcut is parsed as two pages in the fixed phone window");
            BrowserForm dual = new BrowserForm(dualArgs);
            dual.ShowInTaskbar = false;
            dual.Opacity = 0;
            try
            {
                dual.Show();
                IList tabs = (IList)Field(dual, "tabs");
                object[] panes = (object[])Field(dual, "panes");
                await Wait(delegate { return tabs.Count == 2 && panes[1] != null && (string)Field(panes[1], "Title") == "Page 2"; });
                Check((bool)Field(dual, "splitMode") && (string)Field(panes[0], "Url") == Page(1) &&
                    VisibleViews(tabs) == 2 && ViewOf(panes[0]).Bounds.Bottom <= ViewOf(panes[1]).Bounds.Top,
                    "A dual app opens both pages at once, the first on top and the second below");
                await Open(dual, Page(3), null);
                Check(tabs.Count == 2, "A dual app never opens a third page");
                Call(dual, "CloseActiveTab");
                Check(dual.IsDisposed, "Closing a page of a dual app closes its window");
            }
            finally { if (!dual.IsDisposed) dual.Close(); }
        }

        [STAThread]
        static int Main()
        {
            Directory.CreateDirectory(DataDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            StoreTests();
            TerminalParserTests();
            Check(SelectionActions.UrlFor("translate", " 你好 & bye ") ==
                "https://translate.google.com/?sl=auto&tl=zh-CN&op=translate&text=%E4%BD%A0%E5%A5%BD%20%26%20bye"
                && SelectionActions.UrlFor("chatgpt", "hi") == "https://chatgpt.com/?q=hi"
                && SelectionActions.UrlFor("gemini", "hi") == "https://gemini.google.com/app?q=hi"
                && SelectionActions.UrlFor("chatgpt", "   ") == null && SelectionActions.UrlFor("other", "hi") == null,
                "Selection actions build Google Translate (to Chinese) and AI addresses");
            Check(SelectionActions.UrlFor("translate", new string('a', 4999) + "\U0001F600").EndsWith("a"),
                "Long selections are clipped without splitting a character");
            PasswordTests();
            File.WriteAllText(Path.Combine(DataDir, "login.html"), "<!doctype html><title>Sign in</title>" +
                "<form><input id='u' name='user'><input id='p' type='password'></form>");
            for (int i = 1; i <= 10; i++)
                File.WriteAllText(Path.Combine(DataDir, "page" + i + ".html"),
                    "<!doctype html><title>Page " + i + "</title><input id='draft'><a target='_blank' href='page7.html'>Open</a>");
            BrowserForm form = new BrowserForm(new LaunchOptions { Url = Page(1) });
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            int result = 1;
            Timer timeout = new Timer { Interval = 120000 };
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

                    TerminalLauncher termButton = (TerminalLauncher)Field(form, "termFab");
                    TerminalPanel terminal = (TerminalPanel)Field(form, "terminal");
                    Panel host = (Panel)Field(form, "host");
                    int pageHeight = host.Height;
                    Check(termButton.Visible && !terminal.Visible && host.Bounds.Contains(termButton.Bounds)
                        && termButton.Right > host.Right - termButton.Width * 2 && termButton.Bottom > host.Bottom - termButton.Height * 2,
                        "Terminal is a floating button in the page's bottom-right corner");
                    termButton.PerformClick();
                    Check(terminal.Visible && !termButton.Visible && terminal.View.Running && host.Height == pageHeight
                        && host.Bounds.Contains(terminal.Bounds) && form.Controls.GetChildIndex(terminal) < form.Controls.GetChildIndex(host),
                        "Clicking the button unfolds a floating PowerShell window over the page");
                    await Wait(delegate { return ScreenHas(terminal.View, "PS "); });
                    terminal.View.Write("Write-Output ('fed' + 'atather-ok')\r");
                    await Wait(delegate { return ScreenHas(terminal.View, "fedatather-ok"); });
                    Check(ScreenHas(terminal.View, "fedatather-ok"), "Commands typed in the terminal run in PowerShell");
                    boss.PerformClick();
                    Check(!terminal.Visible && terminal.View.Running, "Boss mode hides the terminal and keeps the shell running");
                    boss.PerformClick();
                    Check(terminal.Visible, "Leaving boss mode shows the terminal again");
                    Check(!termButton.Visible, "Leaving boss mode keeps the button folded away while the window is open");
                    terminal.HideButton.PerformClick();
                    Check(!terminal.Visible && termButton.Visible && terminal.View.Running,
                        "Folding the window brings the button back and keeps the shell running");
                    termButton.PerformClick();
                    Check(terminal.Visible && ScreenHas(terminal.View, "fedatather-ok"), "Reopening shows the same session");
                    terminal.View.Write("exit\r");
                    await Wait(delegate { return !terminal.View.Running; });
                    Check(!terminal.Visible && termButton.Visible, "Typing exit folds the terminal back into its button");

                    CoreWebView2 selWeb = (CoreWebView2)Field(form, "web");
                    await selWeb.ExecuteScriptAsync("(function(){var a=document.querySelector('a'),r=document.createRange();" +
                        "r.selectNodeContents(a);var s=getSelection();s.removeAllRanges();s.addRange(r);" +
                        "a.dispatchEvent(new MouseEvent('mouseup',{bubbles:true,button:0,clientX:20,clientY:40}));})()");
                    bool barShown = false;
                    for (int i = 0; i < 50 && !barShown; i++)
                    {
                        barShown = await selWeb.ExecuteScriptAsync("!!document.querySelector('fedatather-selection')") == "true";
                        if (!barShown) await Task.Delay(100);
                    }
                    Check(barShown, "Selecting text on a page shows the floating Translate / Ask AI bar");
                    await selWeb.ExecuteScriptAsync("getSelection().removeAllRanges()");
                    await Task.Delay(300); // selectionchange arrives asynchronously
                    Check(await selWeb.ExecuteScriptAsync("!!document.querySelector('fedatather-selection')") == "false",
                        "Clearing the selection hides the bar");
                    int tabsBefore = tabs.Count;
                    await selWeb.ExecuteScriptAsync("chrome.webview.postMessage({selAction:'claude',text:' what is 2+2? '})");
                    await Wait(delegate { return ((string)Field(Field(form, "activeTab"), "Url")).StartsWith("https://claude.ai/"); });
                    Check((string)Field(Field(form, "activeTab"), "Url") == "https://claude.ai/new?q=what%20is%202%2B2%3F",
                        "Ask AI opens the chosen AI in a new tab with the selected text as the question");
                    Call(form, "CloseActiveTab");
                    await selWeb.ExecuteScriptAsync("chrome.webview.postMessage({selAction:'nope',text:'x'})");
                    await Task.Delay(300);
                    Check(tabs.Count <= tabsBefore, "Unknown selection actions are ignored");

                    IconButton splitButton = (IconButton)Field(form, "splitBtn");
                    object[] panes = (object[])Field(form, "panes");
                    EmptyPane[] empties = (EmptyPane[])Field(form, "emptyPanes");
                    Call(form, "ApplyMode", true);
                    if (tabs.Count < 2) await Open(form, Page(2), null);
                    Check(splitButton.Visible && form.Height == Math.Min(900, System.Windows.Forms.Screen.FromControl(form).WorkingArea.Height),
                        "Phone size is 900 pixels tall (screen permitting) and offers split screen");
                    Call(form, "EnterSplit");
                    Check((bool)Field(form, "splitMode") && panes[0] != null && panes[1] != null && panes[2] == null &&
                        VisibleViews(tabs) == 2 && !empties[2].Visible, "Phone size splits into two panes");
                    Rectangle top = ViewOf(panes[0]).Bounds, bottom = ViewOf(panes[1]).Bounds;
                    Check(top.Bottom <= bottom.Top && top.Left == bottom.Left && top.Width == bottom.Width &&
                        Math.Abs(top.Height - bottom.Height) <= 2, "Phone split stacks two equal panes top and bottom");
                    Call(form, "ApplyMode", false);
                    Check(!(bool)Field(form, "splitMode") && VisibleViews(tabs) == 1, "Switching to full screen leaves the phone split");
                    Check(splitButton.Visible, "Split screen button appears in full screen");
                    object current = Field(form, "activeTab");
                    splitButton.PerformClick();
                    Check((bool)Field(form, "splitMode") && panes[0] != null && panes[1] != null && panes[2] != null &&
                        Array.IndexOf(panes, current) >= 0 && Field(form, "activeTab") == current,
                        "Split screen shows three tabs, including the current page");
                    Rectangle a = ViewOf(panes[0]).Bounds, b = ViewOf(panes[1]).Bounds, c = ViewOf(panes[2]).Bounds;
                    Check(VisibleViews(tabs) == 3 && ViewOf(panes[0]).IsVisible && ViewOf(panes[1]).IsVisible && ViewOf(panes[2]).IsVisible &&
                        a.Right <= b.Left && b.Right <= c.Left && Math.Abs(a.Width - c.Width) <= 2 && a.Top == b.Top,
                        "Three equal panes side by side; other tabs stay hidden");
                    Call(form, "FocusPane", 1, false);
                    Check(Field(form, "activeTab") == panes[1] && Field(form, "web") == Field(panes[1], "Web"),
                        "Choosing a pane makes the toolbar act on it");
                    object third = panes[2];
                    Call(form, "ClearPane", 2);
                    Check(panes[2] == null && empties[2].Visible && !ViewOf(third).IsVisible && tabs.Contains(third) &&
                        Field(form, "activeTab") == null, "Emptying a pane keeps its tab open and shows an empty pane");
                    Call(form, "Navigate", Page(2));
                    await Wait(delegate { return panes[2] != null && (string)Field(panes[2], "Title") == "Page 2"; });
                    Check(Field(form, "activeTab") == panes[2] && !empties[2].Visible && ViewOf(panes[2]).IsVisible,
                        "An address typed while an empty pane is current opens there");
                    Call(form, "ExitSplit");
                    Check(!(bool)Field(form, "splitMode") && Field(form, "activeTab") != null && VisibleViews(tabs) == 1 && !empties[0].Visible,
                        "Leaving split screen shows one page");
                    while (tabs.Count > 2) Call(form, "RemoveTab", tabs[0]);
                    Call(form, "EnterSplit");
                    Check(panes[0] != null && panes[1] != null && panes[2] == null && empties[2].Visible && VisibleViews(tabs) == 2,
                        "With only two tabs, the third pane stays empty");
                    Call(form, "FocusPane", 0, false);
                    await Open(form, Page(4), null);
                    Check(panes[2] != null && (string)Field(panes[2], "Url") == Page(4) && tabs.Count == 3 && VisibleViews(tabs) == 3,
                        "Opening a third link fills the empty third pane");
                    Call(form, "ActivateTab", panes[0]);
                    Check((int)Field(form, "currentPane") == 0 && panes[0] != null && panes[2] != null,
                        "Clicking a shown tab selects its pane without moving it");
                    ViewOf(panes[1]).MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    await Wait(delegate { return (int)Field(form, "currentPane") == 1; });
                    Check(Field(form, "activeTab") == panes[1], "Focusing a page inside a pane makes that pane current");

                    // DevTools Lite opened off screen and without activation: the test never puts a window in front of the user.
                    SetField(form, "toolsBounds", new Rectangle(-32000, -32000, 440, 640));
                    SetField(form, "toolsOn", true);
                    Call(form, "ShowTools", panes[1], false);
                    Form toolsOne = (Form)Field(panes[1], "Tools");
                    PaneHeader[] headers = (PaneHeader[])Field(form, "paneHeaders");
                    Rectangle marked = (Rectangle)Field(form, "markedPane");
                    Check(toolsOne != null && Field(form, "devTools") == toolsOne && headers[1].Current && headers[1].Reading &&
                        !headers[0].Reading && toolsOne.Text.Contains("pane 2") &&
                        marked.Contains(ViewOf(panes[1]).Bounds) && !marked.IntersectsWith(ViewOf(panes[0]).Bounds),
                        "The current pane is marked in red and DevTools Lite says it reads that pane");
                    Call(form, "FocusPane", 0, false);
                    Form toolsZero = (Form)Field(panes[0], "Tools");
                    Check(toolsZero != null && Field(form, "devTools") == toolsZero && toolsZero.Visible && !toolsOne.Visible &&
                        !toolsOne.IsDisposed && headers[0].Reading && !headers[1].Reading && toolsZero.Text.Contains("pane 1") &&
                        ((Rectangle)Field(form, "markedPane")).Contains(ViewOf(panes[0]).Bounds),
                        "Choosing another pane moves the red mark and DevTools Lite to it; the first pane's is kept");
                    Call(form, "FocusPane", 1, false);
                    Check(Field(form, "devTools") == toolsOne && toolsOne.Visible && !toolsZero.Visible,
                        "Going back shows that pane's same DevTools Lite, with what it collected");
                    Call(form, "ToggleDevTools");
                    Check(!(bool)Field(form, "toolsOn") && toolsOne.IsDisposed && toolsZero.IsDisposed && Field(panes[0], "Tools") == null,
                        "Turning DevTools Lite off closes every pane's");
                    boss.PerformClick();
                    Check(VisibleViews(tabs) == 0, "Boss mode hides all three panes");
                    boss.PerformClick();
                    Check(VisibleViews(tabs) == 3, "Leaving boss mode brings all three panes back");
                    Call(form, "ApplyMode", true);
                    Check(!(bool)Field(form, "splitMode") && VisibleViews(tabs) == 1,
                        "Switching to phone size leaves split screen");

                    // Saved passwords on a real https page (a local folder served as https://login.test/).
                    PasswordManager passwords = (PasswordManager)Field(form, "passwords");
                    AddressBox addressBar = (AddressBox)Field(form, "address");
                    object loginTab = Field(form, "activeTab");
                    CoreWebView2 loginWeb = (CoreWebView2)Field(loginTab, "Web");
                    loginWeb.SetVirtualHostNameToFolderMapping("login.test", DataDir, CoreWebView2HostResourceAccessKind.Allow);
                    loginWeb.Navigate("https://login.test/login.html");
                    await Wait(delegate { return (string)Field(loginTab, "Title") == "Sign in"; });
                    Check(!addressBar.ShowKeyButton, "No key in the address bar on a site without saved logins");
                    passwords.Vault.Create("123456");
                    int added, updated, unchanged;
                    passwords.Vault.Import(LoginCsv.Parse("name,url,username,password\nTest,https://login.test/,zoe,päss \"1\"\n" +
                        "Elsewhere,https://other.test/,yan,nope\n", out added), out added, out updated, out unchanged);
                    passwords.SitesChanged();
                    Check(addressBar.ShowKeyButton && addressBar.KeyButton.Visible, "The key appears on a site with a saved login");
                    List<SavedLogin> found = passwords.Vault.FindFor(loginWeb.Source);
                    Check(found.Count == 1 && found[0].Username == "zoe", "Only that site's logins are offered");
                    int filled = await (Task<int>)Call(form, "FillLogin", loginTab, found[0]);
                    Check(filled == 3 && await loginWeb.ExecuteScriptAsync("document.getElementById('u').value") == "\"zoe\"" &&
                        await loginWeb.ExecuteScriptAsync("document.getElementById('p').value") == "\"päss \\\"1\\\"\"",
                        "Filling puts the username and password into the page's login form");
                    await loginWeb.ExecuteScriptAsync("document.getElementById('p').value=''");
                    SavedLogin wrongSite = passwords.Vault.Logins.Find(delegate(SavedLogin l) { return l.Username == "yan"; });
                    Check(await (Task<int>)Call(form, "FillLogin", loginTab, wrongSite) == 0 &&
                        await loginWeb.ExecuteScriptAsync("document.getElementById('p').value") == "\"\"",
                        "A login is never filled into another site's page");
                    boss.PerformClick();
                    Check(!passwords.Vault.IsUnlocked, "Boss mode locks saved passwords");
                    boss.PerformClick();

                    // Settings > Passwords, opened off screen and without activation (no window in front of the user).
                    int left;
                    passwords.Vault.Unlock("123456", out left);
                    Call(form, "OpenSettings", false, new Rectangle(-32000, -32000, 1000, 700));
                    SettingsForm settingsWindow = (SettingsForm)Field(form, "settingsWindow");
                    await Wait(delegate { return Field(settingsWindow, "ui") != null; });
                    CoreWebView2 page = (CoreWebView2)Field(settingsWindow, "ui");
                    Func<string, Task<string>> js = delegate(string script) { return page.ExecuteScriptAsync(script); };
                    for (int i = 0; i < 100 && await js("document.querySelectorAll('.row.group').length") != "2"; i++) await Task.Delay(100);
                    Check(await js("[...document.querySelectorAll('.row.group .name')].map(e => e.textContent).join()") == "\"login.test,other.test\"" &&
                        await js("document.querySelector('.row.group .count').textContent") == "\"1 account\"",
                        "Settings > Passwords lists saved logins by site with their number of accounts");
                    Check(await js("document.body.innerText.includes('nope') || JSON.stringify(S).includes('nope')") == "false",
                        "The password page holds no passwords until one is shown");
                    await js("document.querySelector('.row.group').click()");
                    Check(await js("document.querySelectorAll('.account').length") == "1" &&
                        await js("document.querySelector('.account .title').textContent") == "\"login.test\"",
                        "Opening a site shows a card for each of its accounts");
                    await js("document.querySelector('.account [title=\"Show password\"]').click()");
                    for (int i = 0; i < 50 && await js("document.querySelector('.account .v.secret') !== null") == "true"; i++) await Task.Delay(100);
                    Check(await js("document.querySelector('.account').innerText.includes('ss \"1\"')") == "true",
                        "The eye shows that password");
                    await js("document.querySelector('.account .btn').click()"); // Edit
                    await js("document.querySelector('[aria-label=\"Account name\"]').value = 'zoe2';" +
                        "document.querySelector('.edit-input[aria-label=\"Password\"]').value = 'new-pass'; document.querySelector('.save').click()");
                    await Wait(delegate
                    {
                        List<SavedLogin> now = passwords.Vault.FindFor("https://login.test/");
                        return now.Count == 1 && now[0].Username == "zoe2" && now[0].Password == "new-pass";
                    });
                    Check(true, "Editing on the page saves the new account name and password");
                    await js("window.chrome.webview.postMessage({cmd:'save', id:-1, url:'login.test/signup', username:'amy', password:'x1', note:'n'})");
                    await Wait(delegate { return passwords.Vault.FindFor("https://login.test/").Count == 2; });
                    SavedLogin amy = passwords.Vault.FindFor("https://login.test/").Find(delegate(SavedLogin l) { return l.Username == "amy"; });
                    for (int i = 0; i < 50 && await js("document.querySelectorAll('.account').length") != "2"; i++) await Task.Delay(100);
                    Check(amy != null && amy.Url == "https://login.test/signup" && amy.Note == "n" &&
                        await js("document.querySelectorAll('.account').length") == "2",
                        "Adding a password (address without https://) saves it under that site");
                    await js("window.chrome.webview.postMessage({cmd:'save', id:-1, url:'https://login.test/signup', username:'amy', password:'x2', note:''})");
                    for (int i = 0; i < 50 && await js("document.getElementById('toast').textContent.includes('already saved')") != "true"; i++) await Task.Delay(100);
                    Check(passwords.Vault.FindFor("https://login.test/").Count == 2, "Adding the same account twice is refused");
                    await js("window.chrome.webview.postMessage({cmd:'lock'})");
                    for (int i = 0; i < 50 && await js("(document.querySelector('.notice h3') || {}).textContent || ''") != "\"Saved passwords are locked\""; i++) await Task.Delay(100);
                    Check(!passwords.Vault.IsUnlocked && await js("document.querySelectorAll('.account, .row.group').length") == "0",
                        "Locking from the page clears it");

                    CoreWebView2CookieManager cookies = ((CoreWebView2)Field(form, "web")).CookieManager;
                    cookies.AddOrUpdateCookie(cookies.CreateCookie("session", "signed-in", "clear.test", "/"));
                    Check((await cookies.GetCookiesAsync("https://clear.test/")).Count == 1, "A site sign-in cookie is set");
                    await js("[...document.querySelectorAll('nav .item')].find(b => b.dataset.section === 'privacy').click()");
                    Check(await js("document.querySelector('header h2').textContent") == "\"Privacy\"" &&
                        await js("document.querySelectorAll('.check input:checked').length") == "2" &&
                        await js("document.getElementById('clear-range').value") == "\"0\"",
                        "Settings > Privacy offers Clear browsing data, with cookies and history ticked for all time");
                    await js("document.querySelector('.clear-now').click()");
                    for (int i = 0; i < 100 && await js("document.getElementById('toast').textContent") != "\"Browsing data cleared\""; i++) await Task.Delay(100);
                    Check((await cookies.GetCookiesAsync("https://clear.test/")).Count == 0,
                        "Clear now removes site cookies, signing you out of websites");
                    await js("document.querySelectorAll('.check input:checked').forEach(c => c.click())");
                    Check(await js("document.querySelector('.clear-now').disabled") == "true", "Clear now is off when nothing is ticked");
                    Check(Settings_KindsCheck(), "Each Privacy choice clears the matching kind of browsing data");

                    cookies.AddOrUpdateCookie(cookies.CreateCookie("a", "1", "mail.clear.test", "/"));
                    cookies.AddOrUpdateCookie(cookies.CreateCookie("b", "2", ".clear.test", "/"));
                    cookies.AddOrUpdateCookie(cookies.CreateCookie("c", "3", "keep.test", "/"));
                    await js("[...document.querySelectorAll('nav .item')].find(b => b.dataset.section === 'privacy').click()");
                    string siteNames = "[...document.querySelectorAll('.row.site .name')].map(e => e.textContent).join()";
                    for (int i = 0; i < 50 && await js(siteNames + ".includes('keep.test')") != "true"; i++) await Task.Delay(100);
                    Check(await js(siteNames + ".includes('clear.test')") == "true" &&
                        await js("[...document.querySelectorAll('.row.site')].find(r => r.querySelector('.name').textContent === 'clear.test').querySelector('.count').textContent") == "\"2 cookies\"",
                        "Clear data for one site lists sites with cookies, subdomains grouped under their site");
                    await js("[...document.querySelectorAll('.row.site')].find(r => r.querySelector('.name').textContent === 'clear.test').querySelector('button').click()");
                    for (int i = 0; i < 50 && await js(siteNames + ".includes('clear.test')") != "false"; i++) await Task.Delay(100);
                    Check((await cookies.GetCookiesAsync("https://mail.clear.test/")).Count == 0 &&
                        (await cookies.GetCookiesAsync("https://keep.test/")).Count == 1,
                        "Clearing one site removes its cookies (subdomains too) and keeps other sites'");
                    Check(SettingsForm.SiteOf("https://www.Example.co.uk/login") == "example.co.uk" && SettingsForm.SiteOf("  ") == null,
                        "A typed site or address is cleared as its whole site");
                    settingsWindow.Close();
                    await WebAppTests();
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
