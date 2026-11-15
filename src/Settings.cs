using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Fedatather
{
    // The Settings window. Passwords is the saved-password manager, laid out like Edge's:
    // sites with their icon and number of accounts; a site's accounts as cards with copy, show, open, edit and delete.
    // The page is local (served as https://settings.fedatather/) and never holds passwords it isn't showing: it gets
    // sites, usernames and notes, and asks for one password when you click the eye or Edit. Copying happens here.
    // Privacy clears browsing data (cookies and site sign-ins, history, cache, autofill) from the shared WebView2 profile.
    sealed class SettingsForm : Form
    {
        const string Host = "https://settings.fedatather/";

        readonly CoreWebView2Environment env;
        readonly PasswordManager passwords;
        readonly Theme theme;
        readonly float scale;
        readonly Action<string> openUrl;
        readonly EventHandler vaultChanged;
        CoreWebView2Controller controller;
        CoreWebView2 ui;
        bool closed, askedToUnlock;
        List<SavedLogin> sent = new List<SavedLogin>(); // the logins in the page, by id (their index)
        public Action BossKey;
        public bool OpenInBackground;

        public SettingsForm(Form owner, CoreWebView2Environment env, PasswordManager passwords, Theme theme, float scale,
            Action<string> openUrl)
        {
            this.env = env;
            this.passwords = passwords;
            this.theme = theme;
            this.scale = scale;
            this.openUrl = openUrl;
            Text = "Settings";
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = theme.Dark ? Color.FromArgb(32, 32, 35) : Color.FromArgb(247, 247, 247);
            Icon = owner.Icon;
            MinimumSize = new Size(S(560), S(420));
            Rectangle wa = Screen.FromRectangle(owner.Bounds).WorkingArea;
            Size size = new Size(Math.Min(S(1040), wa.Width - S(40)), Math.Min(S(720), wa.Height - S(40)));
            Point centre = new Point(owner.Left + owner.Width / 2, owner.Top + owner.Height / 2);
            Bounds = new Rectangle(Math.Max(wa.Left, Math.Min(centre.X - size.Width / 2, wa.Right - size.Width)),
                Math.Max(wa.Top, Math.Min(centre.Y - size.Height / 2, wa.Bottom - size.Height)), size.Width, size.Height);
            vaultChanged = delegate { if (!closed && IsHandleCreated) BeginInvoke(new Action(SendState)); };
            passwords.Vault.Locked += vaultChanged;
            passwords.SitesUpdated += vaultChanged;
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        protected override bool ShowWithoutActivation { get { return OpenInBackground; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetDarkTitleBar(Handle, theme.Dark);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            InitUi();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (controller != null) controller.Bounds = ClientRectangle;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closed = true;
            passwords.Vault.Locked -= vaultChanged;
            passwords.SitesUpdated -= vaultChanged;
            if (controller != null) { controller.Close(); controller = null; ui = null; }
            sent.Clear();
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Ui.IsBossKey(keyData)) { Boss(); return true; }
            if (keyData == (Keys.Control | Keys.W)) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Boss()
        {
            if (BossKey != null) BeginInvoke(BossKey);
        }

        async void InitUi()
        {
            try
            {
                CoreWebView2Controller c = await env.CreateCoreWebView2ControllerAsync(Handle);
                if (IsDisposed || closed) { c.Close(); return; }
                controller = c;
            }
            catch (Exception ex)
            {
                if (IsDisposed || closed) return; // closed while the view was being created
                MessageBox.Show(this, "Could not open Settings:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            controller.Bounds = ClientRectangle;
            controller.DefaultBackgroundColor = BackColor;
            ui = controller.CoreWebView2;
            ui.Settings.AreDefaultContextMenusEnabled = false;
            ui.Settings.AreDevToolsEnabled = false;
            ui.Settings.IsStatusBarEnabled = false;
            ui.Settings.IsPasswordAutosaveEnabled = false;
            ui.Settings.IsGeneralAutofillEnabled = false;
            ui.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
            ui.WebResourceRequested += OnResource;
            ui.WebMessageReceived += OnMessage;
            // The page is ours alone: it never navigates anywhere or opens windows.
            ui.NavigationStarting += delegate(object s, CoreWebView2NavigationStartingEventArgs e)
            {
                if (!e.Uri.StartsWith(Host, StringComparison.Ordinal)) e.Cancel = true;
            };
            ui.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
            controller.AcceleratorKeyPressed += delegate(object s, CoreWebView2AcceleratorKeyPressedEventArgs e)
            {
                if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown && e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown) return;
                Keys key = (Keys)(int)e.VirtualKey | ModifierKeys;
                if (Ui.IsBossKey(key)) { e.Handled = true; Boss(); }
                else if (key == (Keys.Control | Keys.W)) { e.Handled = true; BeginInvoke(new Action(Close)); }
                else if (key == Keys.F5 || key == (Keys.Control | Keys.R)) e.Handled = true; // reloading would only flash
            };
            ui.Navigate(Host);
        }

        void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string path = e.Request.Uri.Length > Host.Length ? e.Request.Uri.Substring(Host.Length) : "";
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path.Substring(0, q);
            string file = path.StartsWith("icon/", StringComparison.Ordinal)
                ? passwords.IconFile(Uri.UnescapeDataString(path.Substring(5))) : null;
            if (path.Length == 0) // dark from the first paint, not after the first message
                e.Response = Respond(Encoding.UTF8.GetBytes(theme.Dark ? PageHtml.Replace("<html lang='en'>", "<html lang='en' class='dark'>") : PageHtml),
                    "text/html; charset=utf-8");
            else if (file != null && File.Exists(file))
                e.Response = Respond(File.ReadAllBytes(file), "image/png");
            else
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
        }

        CoreWebView2WebResourceResponse Respond(byte[] data, string mime)
        {
            return env.CreateWebResourceResponse(new MemoryStream(data, false), 200, "OK",
                "Content-Type: " + mime + "\r\nCache-Control: no-store");
        }

        void Send(string json)
        {
            if (ui == null || closed) return;
            try { ui.PostWebMessageAsJson(json); }
            catch (Exception) { } // the view is closing
        }

        void Toast(string text) { Send("{\"type\":\"toast\",\"text\":" + Json.Quote(text) + "}"); }

        // Everything the page shows, except passwords.
        void SendState()
        {
            if (ui == null || closed) return;
            PasswordVault v = passwords.Vault;
            string mode = !v.Exists ? "setup" : v.IsUnlocked ? "open" : "locked";
            sent = new List<SavedLogin>();
            if (mode == "open")
            {
                try { sent = v.Logins; }
                catch (InvalidOperationException) { mode = v.Exists ? "locked" : "setup"; }
            }
            sent.Sort(delegate(SavedLogin a, SavedLogin b)
            {
                int c = string.CompareOrdinal(SiteGroup.Of(a.Url).ToLowerInvariant(), SiteGroup.Of(b.Url).ToLowerInvariant());
                return c != 0 ? c : string.Compare(a.Username, b.Username, StringComparison.OrdinalIgnoreCase);
            });
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"type\":\"state\",\"mode\":").Append(Json.Quote(mode))
              .Append(",\"dark\":").Append(theme.Dark ? "true" : "false")
              .Append(",\"attemptsLeft\":").Append(mode == "locked" ? v.AttemptsLeft : PasswordVault.MaxAttempts)
              .Append(",\"maxAttempts\":").Append(PasswordVault.MaxAttempts)
              .Append(",\"logins\":[");
            for (int i = 0; i < sent.Count; i++)
            {
                SavedLogin l = sent[i];
                string group = SiteGroup.Of(l.Url);
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(i)
                  .Append(",\"group\":").Append(Json.Quote(group))
                  .Append(",\"site\":").Append(Json.Quote(l.Site))
                  .Append(",\"url\":").Append(Json.Quote(l.Url))
                  .Append(",\"username\":").Append(Json.Quote(l.Username))
                  .Append(",\"name\":").Append(Json.Quote(l.Name))
                  .Append(",\"note\":").Append(Json.Quote(l.Note))
                  .Append(",\"icon\":").Append(File.Exists(passwords.IconFile(group)) ? "true" : "false").Append('}');
            }
            sb.Append("]}");
            Send(sb.ToString());
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = Json.Parse(e.WebMessageAsJson) as Dictionary<string, object>; }
            catch (FormatException) { return; }
            if (m == null) return;
            // Dialogs and the clipboard: after the web view's callback has returned.
            BeginInvoke(new Action(delegate { if (!closed) HandleMessage(m); }));
        }

        void HandleMessage(Dictionary<string, object> m)
        {
            string cmd = Json.Str(m, "cmd");
            PasswordVault v = passwords.Vault;
            switch (cmd)
            {
                case "ready":
                    SendState();
                    // Opening Settings while locked asks for the PIN once; the page offers Unlock after that.
                    if (v.Exists && !v.IsUnlocked && !askedToUnlock && !OpenInBackground)
                    {
                        askedToUnlock = true;
                        passwords.EnsureUnlocked(this);
                        SendState();
                    }
                    return;
                case "setup":
                case "unlock":
                    passwords.EnsureUnlocked(this);
                    SendState();
                    return;
                case "lock":
                    v.Lock();
                    SendState();
                    return;
                case "import":
                    passwords.ImportCsv(this);
                    SendState();
                    return;
                case "clear":
                    ClearBrowsingData(m);
                    return;
                case "sites":
                    SendSites();
                    return;
                case "clearSite":
                    ClearSite(Json.Str(m, "site"));
                    return;
                case "erase":
                    if (MessageBox.Show(this, "Erase all saved passwords? This can't be undone.", "Saved passwords",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    {
                        v.Erase();
                        passwords.SitesChanged();
                    }
                    SendState();
                    return;
            }

            if (!v.IsUnlocked) { SendState(); return; }
            v.Touch();
            int id = Id(m);
            SavedLogin login = id >= 0 && id < sent.Count ? sent[id] : null;
            try
            {
                switch (cmd)
                {
                    case "reveal":
                        if (login != null)
                            Send("{\"type\":\"password\",\"id\":" + id + ",\"value\":" + Json.Quote(login.Password) + "}");
                        break;
                    case "copy":
                        if (login == null) break;
                        bool secret = Json.Str(m, "field") == "password";
                        passwords.Copy(secret ? login.Password : login.Username, secret);
                        Toast(secret ? "Password copied. The clipboard is cleared in 30 seconds." : "Account name copied");
                        break;
                    case "open":
                        if (login != null && openUrl != null) openUrl(login.Url);
                        break;
                    case "delete":
                        if (login == null) break;
                        if (MessageBox.Show(this, "Delete the saved password for " +
                                (login.Username.Length > 0 ? login.Username : "this account") + " on " + login.Site + "?",
                                "Saved passwords", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                                MessageBoxDefaultButton.Button2) != DialogResult.Yes) break;
                        v.Delete(login);
                        passwords.SitesChanged();
                        SendState();
                        Toast("Password deleted");
                        break;
                    case "save":
                        Save(m, login, id);
                        break;
                }
            }
            catch (InvalidOperationException ex) // locked or changed meanwhile
            {
                Result(false, ex.Message);
                SendState();
            }
        }

        static bool Flag(Dictionary<string, object> m, string key)
        {
            object v;
            return m.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        // What each choice on the Privacy page clears. The browser, every saved web app and this window share one
        // WebView2 profile, so this clears them all.
        internal static CoreWebView2BrowsingDataKinds KindsFor(bool cookies, bool history, bool cache, bool autofill)
        {
            CoreWebView2BrowsingDataKinds kinds = 0;
            if (cookies) kinds |= CoreWebView2BrowsingDataKinds.AllSite; // cookies and site storage: signs you out of sites
            if (history) kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory;
            if (cache) kinds |= CoreWebView2BrowsingDataKinds.DiskCache;
            if (autofill) kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill | CoreWebView2BrowsingDataKinds.PasswordAutosave;
            return kinds;
        }

        // Settings > Privacy: clear browsing data from the chosen time range (hours back from now; 0 = all time).
        async void ClearBrowsingData(Dictionary<string, object> m)
        {
            CoreWebView2BrowsingDataKinds kinds = KindsFor(Flag(m, "cookies"), Flag(m, "history"), Flag(m, "cache"), Flag(m, "autofill"));
            if (kinds == 0) { Result(false, "Choose what to clear."); return; }
            object h;
            int hours = m.TryGetValue("hours", out h) && h is double ? (int)(double)h : 0;
            try
            {
                CoreWebView2Profile profile = ui.Profile;
                if (hours > 0) await profile.ClearBrowsingDataAsync(kinds, DateTime.Now.AddHours(-hours), DateTime.Now);
                else await profile.ClearBrowsingDataAsync(kinds);
            }
            catch (Exception ex)
            {
                if (!closed) Result(false, "Could not clear browsing data: " + ex.Message);
                return;
            }
            if (closed) return;
            Result(true, "Browsing data cleared");
            SendSites();
        }

        // The site a cookie belongs to, grouped as Passwords groups sites: "mail.example.com" -> "example.com".
        static string CookieSite(CoreWebView2Cookie c) { return SiteGroup.Of("https://" + c.Domain.TrimStart('.') + "/"); }

        static bool InSite(string host, string site)
        {
            host = host.TrimStart('.').ToLowerInvariant();
            return host == site || host.EndsWith("." + site, StringComparison.Ordinal);
        }

        // "https://www.example.com/a" or "example.com" -> "example.com"; null when it isn't a web address.
        internal static string SiteOf(string text)
        {
            string url = NormalizeUrl(text);
            return url == null ? null : SiteGroup.Of(url);
        }

        // Settings > Privacy: the sites that have cookies, with how many.
        async void SendSites()
        {
            List<CoreWebView2Cookie> all;
            try { all = await ui.CookieManager.GetCookiesAsync(null); }
            catch (Exception) { return; } // closing
            SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (CoreWebView2Cookie c in all)
            {
                string site = CookieSite(c);
                int n;
                counts.TryGetValue(site, out n);
                counts[site] = n + 1;
            }
            StringBuilder sb = new StringBuilder("{\"type\":\"sites\",\"sites\":[");
            bool first = true;
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"site\":").Append(Json.Quote(kv.Key)).Append(",\"cookies\":").Append(kv.Value).Append('}');
            }
            Send(sb.Append("]}").ToString());
        }

        // Clears one site's cookies and site storage (local storage, IndexedDB, service workers, cache storage...),
        // including its subdomains: what signs you out of it. WebView2 can't clear one site's history or HTTP cache.
        async void ClearSite(string text)
        {
            string site = SiteOf(text);
            if (site == null) { Result(false, "Enter a site, for example example.com"); return; }
            try
            {
                HashSet<string> hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { site, "www." + site };
                foreach (CoreWebView2Cookie c in await ui.CookieManager.GetCookiesAsync(null))
                {
                    if (!InSite(c.Domain, site)) continue;
                    hosts.Add(c.Domain.TrimStart('.'));
                    ui.CookieManager.DeleteCookie(c);
                }
                foreach (string host in hosts)
                    foreach (string scheme in new[] { "https://", "http://" })
                        await ui.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin",
                            "{\"origin\":" + Json.Quote(scheme + host) + ",\"storageTypes\":\"all\"}");
            }
            catch (Exception ex)
            {
                if (!closed) Result(false, "Could not clear " + site + ": " + ex.Message);
                return;
            }
            if (closed) return;
            Result(true, "Cookies and site data cleared for " + site);
            SendSites();
        }

        static int Id(Dictionary<string, object> m)
        {
            object v;
            return m.TryGetValue("id", out v) && v is double ? (int)(double)v : -1;
        }

        void Save(Dictionary<string, object> m, SavedLogin old, int id)
        {
            string url = NormalizeUrl(Json.Str(m, "url"));
            if (url == null) { Result(false, "Enter the site's address, for example https://example.com"); return; }
            string password = Json.Str(m, "password");
            if (password.Length == 0) { Result(false, "Enter the password."); return; }
            SavedLogin login = new SavedLogin { Url = url, Username = Json.Str(m, "username").Trim(), Password = password,
                Note = Json.Str(m, "note"), Name = old != null && old.Name.Length > 0 ? old.Name : "" };
            if (login.Name.Length == 0) login.Name = login.Site;
            try
            {
                if (id >= 0 && old == null) throw new InvalidOperationException("That saved password no longer exists.");
                if (old == null) passwords.Vault.Add(login);
                else passwords.Vault.Update(old, login);
            }
            catch (InvalidOperationException ex) { Result(false, ex.Message); return; }
            passwords.SitesChanged();
            SendState();
            Result(true, old == null ? "Password saved" : "Changes saved");
        }

        void Result(bool ok, string text)
        {
            Send("{\"type\":\"result\",\"ok\":" + (ok ? "true" : "false") + ",\"text\":" + Json.Quote(text) + "}");
        }

        // "example.com/login" -> "https://example.com/login"; null when it isn't a web address.
        internal static string NormalizeUrl(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return null;
            if (text.IndexOf("://", StringComparison.Ordinal) < 0) text = "https://" + text;
            Uri u;
            if (!Uri.TryCreate(text, UriKind.Absolute, out u) || u.Host.Length == 0 ||
                (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return null;
            return u.AbsoluteUri;
        }

        // ---------------------------------------------------------------- the page

        static readonly string PageHtml = Embedded.Text("settings.html");
    }
}
