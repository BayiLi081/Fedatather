using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Fedatather
{
    sealed class BrowserForm : Form
    {
        // Phone window size in actual screen pixels, toolbar included (not scaled with DPI).
        const int PhoneWindowWidth = 500;
        const int PhoneWindowHeight = 750;
        const int BarHeight = 36;        // navigation row
        const int TitleRowHeight = 30;   // extra top row in phone size: title + window buttons, free to drag
        const int TitleButtonHeight = 24;
        const int ButtonWidth = 26;
        const int ButtonHeight = 28;
        const string Brand = "Fedatather";
        const string HomeUrl = "https://www.google.com/";
        const string SearchUrl = "https://www.google.com/search?q=";

        readonly LaunchOptions options;
        readonly bool fixedSize;      // web app saved with a fixed window size: no size switching
        readonly string windowName;
        readonly float scale;
        readonly Theme theme;
        readonly AppSettings settings;
        readonly ToolTip tips = new ToolTip();
        readonly ToolBar bar;
        readonly AddressBox address;
        readonly IconButton backBtn, fwdBtn, reloadBtn, devBtn, modeBtn, pinBtn, minBtn, closeBtn, bossBtn;
        readonly Panel host;
        readonly string startUrl;
        readonly Panel tabBar;
        readonly FlowLayoutPanel tabButtons;
        readonly Button newTabButton, foldedButton;
        readonly List<BrowserTab> tabs = new List<BrowserTab>();
        readonly SemaphoreSlim tabGate = new SemaphoreSlim(1, 1);
        readonly FoldedTabs folded;
        BrowserTab activeTab;
        bool closing, bossMode, restoreDevTools;
        readonly PictureBox bossCover;

        sealed class BrowserTab
        {
            public CoreWebView2Controller Controller;
            public CoreWebView2 Web;
            public string Url, Title;
            public bool Loading;
        }

        CoreWebView2Environment env;
        CoreWebView2Controller controller;
        CoreWebView2 web;
        DevToolsLite devTools;
        bool phoneMode, pinned, loading, htmlFullscreen, shown, saveDialogOpen;
        bool webHadFocus = true, selectAllOnMouseUp, dragArmed;
        Point dragStart;
        Point? phoneLocation;

        public BrowserForm(LaunchOptions options)
        {
            this.options = options;
            fixedSize = options.Size != null;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            theme = Theme.FromSystem();

            // The main browser and every saved web app keep their own window state.
            settings = AppSettings.Load(options.IsApp
                ? WebApps.SettingsPath(options.AppId)
                : Path.Combine(Program.DataDir, "settings.ini"));
            phoneMode = fixedSize ? options.Size == "phone" : settings.Get("mode", "phone") != "full";
            pinned = options.Pin || settings.Get("pinned", "0") == "1";
            phoneLocation = settings.GetPoint("phone");
            if (!string.IsNullOrEmpty(options.Url)) startUrl = options.Url;
            else startUrl = options.IsApp ? HomeUrl : settings.Get("url", HomeUrl);
            folded = new FoldedTabs((options.IsApp ? WebApps.SettingsPath(options.AppId)
                : Path.Combine(Program.DataDir, "settings.ini")) + ".folded.json");
            windowName = options.IsApp && !string.IsNullOrEmpty(options.AppName) ? options.AppName : Brand;

            Text = windowName;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            MaximizeBox = false;
            BackColor = theme.Bar;
            if (phoneLocation.HasValue) Location = phoneLocation.Value;
            Icon = LoadWindowIcon();

            host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = theme.Page;
            host.Resize += delegate { foreach (BrowserTab tab in tabs) tab.Controller.Bounds = host.ClientRectangle; };

            bar = new ToolBar(theme);
            bar.Dock = DockStyle.Top;
            bar.Height = BarHeightFor(phoneMode);
            bar.Title = windowName;
            bar.MouseDown += OnBarMouseDown;
            bar.MouseMove += OnBarMouseMove;
            bar.MouseUp += delegate { dragArmed = false; };
            bar.Layout += delegate { LayoutBar(); };

            backBtn = AddButton(Icons.Back, "Back (Alt+Left)", delegate { if (web != null && web.CanGoBack) web.GoBack(); });
            fwdBtn = AddButton(Icons.Forward, "Forward (Alt+Right)", delegate { if (web != null && web.CanGoForward) web.GoForward(); });
            reloadBtn = AddButton(Icons.Refresh, "Reload (F5)", delegate { ReloadOrStop(); });
            bossBtn = AddButton(Icons.Person, "Boss mode (Ctrl+Shift+B)", delegate { ToggleBossMode(); });
            devBtn = AddButton(Icons.DevTools, "DevTools Lite: save images & tables (F12)", delegate { ToggleDevTools(); });
            modeBtn = AddButton(Icons.FullScreen, "", delegate { ToggleMode(); });
            pinBtn = AddButton(Icons.Pin, "", delegate { ApplyPin(!pinned); });
            minBtn = AddButton(Icons.Minimize, "Minimize", delegate { WindowState = FormWindowState.Minimized; });
            closeBtn = AddButton(Icons.Close, "Close window (Alt+F4)", delegate { Close(); });
            closeBtn.Danger = true;
            modeBtn.Visible = !fixedSize;
            backBtn.Enabled = fwdBtn.Enabled = false;

            address = new AddressBox(theme);
            address.ShowAppButton = !options.IsApp;
            address.AppButton.Click += delegate { SaveAsApp(); };
            SetTip(address.AppButton, "Save this site as an app (Ctrl+D)");
            address.Box.KeyDown += OnAddressKeyDown;
            address.Box.MouseDown += delegate { webHadFocus = false; };
            address.Box.GotFocus += delegate
            {
                if (MouseButtons == MouseButtons.None) address.Box.SelectAll();
                else selectAllOnMouseUp = true;
            };
            address.Box.MouseUp += delegate
            {
                if (selectAllOnMouseUp && address.Box.SelectionLength == 0) address.Box.SelectAll();
                selectAllOnMouseUp = false;
            };
            address.Box.LostFocus += delegate { selectAllOnMouseUp = false; ShowUrl(); };
            address.Box.Text = startUrl;
            bar.Controls.Add(address);

            tabBar = new Panel { Dock = DockStyle.Top, Height = S(32), BackColor = theme.TabBar };
            foldedButton = new Button { Dock = DockStyle.Right, Width = S(108), FlatStyle = FlatStyle.Flat,
                ForeColor = theme.Muted, BackColor = theme.TabBar, Text = "Folded tabs" };
            foldedButton.FlatAppearance.BorderSize = 0;
            foldedButton.FlatAppearance.MouseOverBackColor = theme.TabHover;
            foldedButton.FlatAppearance.MouseDownBackColor = theme.TabHover;
            foldedButton.Click += delegate { ShowFoldedTabs(); };
            newTabButton = new Button { Dock = DockStyle.Right, Width = S(30), FlatStyle = FlatStyle.Flat,
                ForeColor = theme.Fore, BackColor = theme.TabBar, Text = "+", Enabled = false };
            newTabButton.FlatAppearance.BorderSize = 0;
            newTabButton.FlatAppearance.MouseOverBackColor = theme.TabHover;
            newTabButton.FlatAppearance.MouseDownBackColor = theme.TabHover;
            SetTip(newTabButton, "New tab (Ctrl+T)");
            newTabButton.Click += async delegate { await OpenTabAsync(HomeUrl, null); };
            tabButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false };
            tabButtons.Resize += delegate { LayoutTabs(); };
            tabBar.Controls.Add(tabButtons);
            tabBar.Controls.Add(newTabButton);
            tabBar.Controls.Add(foldedButton);
            UpdateTabs();

            // Docking runs from the end of the collection: bar claims the top, host fills the rest.
            Controls.Add(host);
            Controls.Add(tabBar);
            Controls.Add(bar);
            bossCover = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White, Visible = false, TabStop = false };
            bossCover.Resize += delegate { LayoutBossButton(); };
            Controls.Add(bossCover);
            LayoutBar();
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        Icon LoadWindowIcon()
        {
            if (options.IsApp)
            {
                string ico = WebApps.IconPath(options.AppId);
                if (File.Exists(ico)) { try { return new Icon(ico); } catch { } }
            }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return null; }
        }

        // Tooltip and accessible name stay in sync.
        void SetTip(Control c, string tip)
        {
            tips.SetToolTip(c, tip);
            c.AccessibleName = tip;
        }

        IconButton AddButton(string symbol, string tip, EventHandler click)
        {
            IconButton b = new IconButton(theme, symbol);
            b.Size = new Size(S(ButtonWidth), S(ButtonHeight));
            b.Round = true;
            b.Click += click;
            if (tip.Length > 0) SetTip(b, tip);
            bar.Controls.Add(b);
            return b;
        }

        int BarHeightFor(bool phone) { return S(phone ? TitleRowHeight + BarHeight : BarHeight); }

        // Full screen: one row.  Phone size: two rows —
        //   [ page title ..... (drag anywhere) .....  devtools  size  pin | min  close ]
        //   [ back  fwd  reload  [ address ........................... ] ]
        void LayoutBar()
        {
            if (closeBtn == null || address == null) return;
            int bw = S(ButtonWidth), bh = S(ButtonHeight), pad = S(4);
            int titleRow = phoneMode ? S(TitleRowHeight) : 0;
            int navY = titleRow + (S(BarHeight) - 1 - bh) / 2;
            int winH = phoneMode ? S(TitleButtonHeight) : bh;
            int winY = phoneMode ? (titleRow - winH) / 2 : navY;

            int r = bar.Width - pad;
            closeBtn.SetBounds(r -= bw, winY, bw, winH);
            minBtn.SetBounds(r -= bw, winY, bw, winH);
            r -= pad;
            pinBtn.SetBounds(r -= bw, winY, bw, winH);
            if (!fixedSize) modeBtn.SetBounds(r -= bw, winY, bw, winH);
            devBtn.SetBounds(r -= bw, winY, bw, winH);
            if (!bossMode) bossBtn.SetBounds(r -= bw, winY, bw, winH);

            int x = pad;
            foreach (IconButton b in new[] { backBtn, fwdBtn, reloadBtn }) { b.SetBounds(x, navY, bw, bh); x += bw; }

            if (phoneMode)
            {
                bar.TitleBounds = new Rectangle(S(12), 0, Math.Max(0, r - S(12) - pad), titleRow);
                address.SetBounds(x + pad, navY, Math.Max(0, bar.Width - x - pad * 3), bh);
            }
            else
            {
                bar.TitleBounds = Rectangle.Empty;
                int available = Math.Max(0, r - x - pad * 2);
                int width = Math.Min(available, S(680));
                int left = x + pad + (available - width) / 2;
                address.SetBounds(left, navY, width, bh);
            }
        }

        // ---------------------------------------------------------------- window

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= Native.WS_MINIMIZEBOX; // lets the taskbar button minimize / restore a borderless window
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyMode(phoneMode);
            ApplyPin(pinned);
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            InitWebView();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            shown = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            Rectangle nb = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (phoneMode) phoneLocation = nb.Location;
            if (!fixedSize) settings.Set("mode", phoneMode ? "phone" : "full");
            settings.Set("pinned", pinned ? "1" : "0");
            if (phoneLocation.HasValue) settings.Set("phone", phoneLocation.Value.X + "," + phoneLocation.Value.Y);
            // A web app always reopens at its own address; the browser reopens where you left off.
            if (!options.IsApp && web != null && !string.IsNullOrEmpty(web.Source)) settings.Set("url", web.Source);
            settings.Save();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (devTools != null) devTools.Close();
            foreach (BrowserTab tab in tabs) tab.Controller.Close();
            tabs.Clear();
            controller = null; web = null;
            tips.Dispose();
            if (bossCover.Image != null) bossCover.Image.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (controller != null) controller.IsVisible = !bossMode && WindowState != FormWindowState.Minimized;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            foreach (BrowserTab tab in tabs) tab.Controller.NotifyParentWindowPositionChanged();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // WinForms restores focus to the address box on activation; give it back to the page if that's where it was.
            if (webHadFocus) BeginInvoke(new Action(delegate { if (webHadFocus) FocusWeb(); }));
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            BeginInvoke(new Action(delegate
            {
                if (WindowState == FormWindowState.Normal) ApplyMode(phoneMode);
            }));
        }

        void OnBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks >= 2) { dragArmed = false; ToggleMode(); return; }
            dragArmed = phoneMode && !htmlFullscreen;
            dragStart = e.Location;
        }

        void OnBarMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragArmed || e.Button != MouseButtons.Left) return;
            Size d = SystemInformation.DragSize;
            if (Math.Abs(e.X - dragStart.X) < d.Width && Math.Abs(e.Y - dragStart.Y) < d.Height) return;
            dragArmed = false;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
        }

        // ---------------------------------------------------------------- modes

        void ToggleMode()
        {
            if (!fixedSize) ApplyMode(!phoneMode);
        }

        // Only the window size changes; sites always get the normal desktop request.
        void ApplyMode(bool phone)
        {
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            if (phoneMode && shown) phoneLocation = Location; // remember where the phone window sat
            phoneMode = phone;
            bar.Height = BarHeightFor(phone);
            LayoutBar();

            Screen screen = Screen.FromRectangle(Bounds);
            if (phone)
            {
                Rectangle wa = screen.WorkingArea;
                int w = PhoneWindowWidth, h = PhoneWindowHeight;
                Point p = phoneLocation.HasValue
                    ? phoneLocation.Value
                    : new Point(wa.Right - w - S(24), wa.Top + (wa.Height - h) / 2);
                Bounds = KeepOnScreen(new Rectangle(p, new Size(w, h)));
            }
            else
            {
                Bounds = htmlFullscreen ? screen.Bounds : screen.WorkingArea;
            }

            Native.SetRoundCorners(Handle, phone);
            modeBtn.Symbol = phone ? Icons.FullScreen : Icons.Phone;
            SetTip(modeBtn, phone ? "Full screen (F11)" : "Phone size (F11)");
        }

        static Rectangle KeepOnScreen(Rectangle r)
        {
            Rectangle wa = Screen.FromRectangle(r).WorkingArea;
            r.Width = Math.Min(r.Width, wa.Width);
            r.Height = Math.Min(r.Height, wa.Height);
            r.X = Math.Max(wa.Left, Math.Min(r.X, wa.Right - r.Width));
            r.Y = Math.Max(wa.Top, Math.Min(r.Y, wa.Bottom - r.Height));
            return r;
        }

        void ApplyPin(bool on)
        {
            pinned = on;
            TopMost = on;
            pinBtn.Active = on;
            pinBtn.Symbol = on ? Icons.Pinned : Icons.Pin;
            SetTip(pinBtn, on ? "Unpin from top (Ctrl+Shift+P)" : "Pin on top (Ctrl+Shift+P)");
        }

        // A page asked for fullscreen (e.g. a video): hide the toolbar; in full mode also cover the taskbar.
        void SetHtmlFullscreen(bool on)
        {
            htmlFullscreen = on;
            bar.Visible = !on && !bossMode;
            tabBar.Visible = !on && !bossMode;
            if (!phoneMode)
            {
                Screen screen = Screen.FromRectangle(Bounds);
                Bounds = on ? screen.Bounds : screen.WorkingArea;
            }
        }

        // The cover is a native control; hide WebView2 controllers explicitly because they own child HWNDs.
        void ToggleBossMode()
        {
            if (!bossMode && bossCover.Image == null)
            {
                try
                {
                    using (Stream stream = typeof(BrowserForm).Assembly.GetManifestResourceStream("Fedatather.BossImage"))
                    using (Image image = Image.FromStream(stream)) bossCover.Image = new Bitmap(image);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not load the boss image:\n\n" + ex.Message, Brand,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            bossMode = !bossMode;
            if (bossMode)
            {
                restoreDevTools = devTools != null && devTools.Visible;
                if (restoreDevTools) devTools.Hide();
                foreach (BrowserTab tab in tabs) tab.Controller.IsVisible = false;
                host.Visible = bar.Visible = tabBar.Visible = false;
                // Close tab/context menus so their titles cannot remain over the cover.
                foreach (Control button in tabButtons.Controls)
                    if (button.ContextMenuStrip != null) button.ContextMenuStrip.Close();
                bossCover.Visible = true;
                bossCover.BringToFront();
                bossCover.Controls.Add(bossBtn);
                bossBtn.Active = true;
                LayoutBossButton();
                bossBtn.BringToFront();
                Text = windowName;
                ActiveControl = null;
            }
            else
            {
                bar.Controls.Add(bossBtn);
                bossBtn.Active = false;
                bossCover.Visible = false;
                host.Visible = true;
                bar.Visible = tabBar.Visible = !htmlFullscreen;
                LayoutBar();
                if (controller != null) controller.IsVisible = WindowState != FormWindowState.Minimized;
                UpdateTitle();
                if (restoreDevTools && devTools != null) devTools.Show(this);
                if (webHadFocus) FocusWeb(); else FocusAddress();
            }
            SetTip(bossBtn, bossMode ? "Restore browser (Ctrl+Shift+B)" : "Boss mode (Ctrl+Shift+B)");
        }

        void LayoutBossButton()
        {
            if (bossMode && bossCover != null)
                bossBtn.SetBounds(Math.Max(0, bossCover.ClientSize.Width - S(ButtonWidth + 4)), S(4),
                    S(ButtonWidth), S(ButtonHeight));
        }

        // ---------------------------------------------------------------- devtools lite

        void ToggleDevTools()
        {
            if (web == null || bossMode) return;
            if (devTools != null) { devTools.Close(); return; }
            devTools = new DevToolsLite(this, env, web, theme, scale);
            devTools.FormClosed += delegate { devTools = null; devBtn.Active = false; };
            devTools.Show(this); // owned: stays above this window (and above it when pinned)
            devBtn.Active = true;
        }

        // ---------------------------------------------------------------- web apps

        void SaveAsApp()
        {
            if (web == null || bossMode || options.IsApp || saveDialogOpen) return;
            saveDialogOpen = true;
            try
            {
                string url = web.Source;
                var iconTask = WebApps.FetchIconAsync(web, web.Settings.UserAgent);
                using (SaveAppDialog dlg = new SaveAppDialog(theme, scale,
                    WebApps.SuggestName(web.DocumentTitle, url), url, pinned, iconTask))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    WebAppSpec spec = dlg.Result;

                    SavedApp saved;
                    try { saved = WebApps.Save(spec); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Could not create the shortcut:\n\n" + ex.Message, Brand,
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    string where = string.Join(" and ", saved.Places.ToArray());
                    if (MessageBox.Show(this, "\"" + spec.Name + "\" is saved as an app (" + where + ").\n\nOpen it now?",
                            Brand, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        Process.Start(Application.ExecutablePath, saved.Arguments);
                }
            }
            finally { saveDialogOpen = false; }
        }

        // ---------------------------------------------------------------- web view

        async void InitWebView()
        {
            try
            {
                env = await CoreWebView2Environment.CreateAsync(
                    null, Path.Combine(Program.DataDir, "WebView2"));
                if (IsDisposed) return;
                await OpenTabAsync(startUrl, null);
                if (!closing) newTabButton.Enabled = true;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                if (IsDisposed) return;
                if (MessageBox.Show(this, "Fedatather needs the Microsoft Edge WebView2 Runtime.\n\nOpen the download page?",
                        Brand, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                Close();
                return;
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                MessageBox.Show(this, "Could not start the web view:\n\n" + ex.Message, Brand,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

        }

        async Task<BrowserTab> OpenTabAsync(string url, FoldedPage restore)
        {
            await tabGate.WaitAsync();
            try
            {
                if (closing || IsDisposed || env == null) return null;
                // Serialize opens, including popup requests, so there are never more than five controllers.
                if (tabs.Count >= FoldedTabs.MaxOpenTabs)
                {
                    BrowserTab oldest = tabs[0];
                    // Save before releasing the view: a failed disk write must not lose the page.
                    folded.Add(new FoldedPage { Url = oldest.Url, Title = oldest.Title });
                    RemoveTab(oldest);
                }
                CoreWebView2Controller c = await env.CreateCoreWebView2ControllerAsync(host.Handle);
                if (closing || IsDisposed) { c.Close(); return null; }
                BrowserTab tab = new BrowserTab { Controller = c, Web = c.CoreWebView2, Url = url,
                    Title = restore == null ? "New tab" : restore.Title };
                tabs.Add(tab);
                c.IsVisible = false;
                c.Bounds = host.ClientRectangle;
                WireTab(tab);
                ActivateTab(tab);
                tab.Web.Navigate(url);
                if (restore != null) folded.Remove(restore);
                UpdateTabs();
                return tab;
            }
            catch (Exception ex)
            {
                if (!closing && !IsDisposed)
                    MessageBox.Show(this, "Could not open the tab:\n\n" + ex.Message, Brand,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
            finally { tabGate.Release(); }
        }

        void WireTab(BrowserTab tab)
        {
            CoreWebView2 page = tab.Web;
            page.SourceChanged += delegate
            {
                tab.Url = page.Source;
                if (activeTab == tab) ShowUrl();
            };
            page.HistoryChanged += delegate { if (activeTab == tab) UpdateNavButtons(); };
            page.NavigationStarting += delegate
            {
                tab.Loading = true;
                if (activeTab == tab) SetLoading(true);
            };
            page.NavigationCompleted += delegate
            {
                tab.Loading = false;
                if (activeTab != tab) return;
                SetLoading(false);
                UpdateNavButtons();
                if (devTools != null) devTools.PageChanged();
            };
            page.DocumentTitleChanged += delegate
            {
                tab.Title = string.IsNullOrEmpty(page.DocumentTitle) ? tab.Url : page.DocumentTitle;
                if (activeTab == tab) UpdateTitle();
                UpdateTabs();
            };
            page.NewWindowRequested += OnNewWindowRequested;
            page.ContainsFullScreenElementChanged += delegate
            {
                if (activeTab == tab) SetHtmlFullscreen(page.ContainsFullScreenElement);
            };
            tab.Controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
            tab.Controller.GotFocus += delegate { if (activeTab == tab) webHadFocus = true; };
        }

        void ActivateTab(BrowserTab tab)
        {
            if (activeTab != tab && devTools != null) devTools.Close();
            if (controller != null) controller.IsVisible = false;
            activeTab = tab;
            controller = tab.Controller;
            web = tab.Web;
            controller.Bounds = host.ClientRectangle;
            controller.IsVisible = !bossMode && WindowState != FormWindowState.Minimized;
            SetHtmlFullscreen(web.ContainsFullScreenElement);
            SetLoading(tab.Loading);
            UpdateNavButtons();
            address.Box.Text = tab.Url == "about:blank" ? "" : tab.Url;
            UpdateTitle();
            UpdateTabs();
            FocusWeb();
        }

        void UpdateTitle()
        {
            Text = bossMode || activeTab == null || string.IsNullOrEmpty(activeTab.Title) ? windowName : activeTab.Title;
            bar.Title = Text;
        }

        void RemoveTab(BrowserTab tab)
        {
            bool wasActive = activeTab == tab;
            if (wasActive)
            {
                if (devTools != null) devTools.Close();
                activeTab = null; controller = null; web = null;
                SetHtmlFullscreen(false);
            }
            tabs.Remove(tab);
            tab.Controller.Close();
            if (wasActive && tabs.Count > 0) ActivateTab(tabs[tabs.Count - 1]);
            UpdateTabs();
        }

        void CloseActiveTab()
        {
            if (tabs.Count <= 1) { Close(); return; }
            RemoveTab(activeTab);
        }

        void UpdateTabs()
        {
            if (tabButtons == null) return;
            tabButtons.SuspendLayout();
            while (tabButtons.Controls.Count > 0)
            {
                Control old = tabButtons.Controls[0];
                tips.SetToolTip(old, null);
                old.Dispose();
            }
            foreach (BrowserTab item in tabs)
            {
                BrowserTab tab = item;
                SafariTabButton b = new SafariTabButton(theme) { Text = tab.Title, Font = Font,
                    Active = tab == activeTab, Margin = new Padding(1), Height = S(30) };
                SetTip(b, tab.Title + "\n" + tab.Url + "\nMiddle-click or right-click to close (Ctrl+W)");
                b.Click += delegate { ActivateTab(tab); };
                b.MouseUp += delegate(object sender, MouseEventArgs e)
                {
                    if (e.Button != MouseButtons.Middle) return;
                    if (tabs.Count == 1) Close(); else RemoveTab(tab);
                };
                ContextMenuStrip actions = new ContextMenuStrip();
                actions.Items.Add("Close tab (Ctrl+W)", null, delegate
                {
                    if (tabs.Count == 1) Close(); else RemoveTab(tab);
                });
                b.ContextMenuStrip = actions;
                b.Disposed += delegate { actions.Dispose(); };
                tabButtons.Controls.Add(b);
            }
            foldedButton.Text = "Folded (" + folded.Pages.Count + ")";
            LayoutTabs();
            tabButtons.ResumeLayout();
        }

        void LayoutTabs()
        {
            if (tabButtons == null || tabs.Count == 0) return;
            int width = Math.Max(1, tabButtons.ClientSize.Width / tabs.Count - 2);
            foreach (Control b in tabButtons.Controls) b.Width = width;
        }

        void ShowFoldedTabs()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Items.Add("Folded tabs — click a page to restore").Enabled = false;
            menu.Items.Add(new ToolStripSeparator());
            if (folded.Pages.Count == 0) menu.Items.Add("No folded tabs").Enabled = false;
            for (int i = folded.Pages.Count - 1; i >= 0; i--)
            {
                FoldedPage page = folded.Pages[i];
                ToolStripMenuItem item = new ToolStripMenuItem((page.Title ?? page.Url).Replace("&", "&&"));
                item.ToolTipText = page.Url;
                item.Click += async delegate { await OpenTabAsync(page.Url, page); };
                menu.Items.Add(item);
            }
            menu.Closed += delegate { BeginInvoke(new Action(menu.Dispose)); };
            menu.Show(foldedButton, new Point(0, foldedButton.Height));
        }

        void Navigate(string input)
        {
            if (web == null) return;
            string url = ToUrl(input);
            if (url == null) return;
            try { web.Navigate(url); }
            catch (ArgumentException) { web.Navigate(SearchUrl + Uri.EscapeDataString(input.Trim())); }
        }

        static readonly Regex SchemeRx = new Regex(@"^(https?|file|about|edge|data|view-source|ftp|mailto|blob):", RegexOptions.IgnoreCase);
        static readonly Regex LocalRx = new Regex(@"^(localhost|127(\.\d{1,3}){3}|\d{1,3}(\.\d{1,3}){3})(:\d+)?([/?#].*)?$", RegexOptions.IgnoreCase);
        static readonly Regex HostRx = new Regex(@"^[\w-]+(\.[\w-]+)+(:\d+)?([/?#].*)?$");

        // Turn whatever was typed into something navigable: URL, host, local file, or a web search.
        internal static string ToUrl(string input)
        {
            string t = (input ?? "").Trim();
            if (t.Length == 0) return null;
            if (SchemeRx.IsMatch(t)) return t;
            if (Regex.IsMatch(t, @"^([a-zA-Z]:\\|\\\\)"))
            {
                try { return new Uri(t).AbsoluteUri; } catch (UriFormatException) { }
            }
            if (t.IndexOf(' ') < 0)
            {
                if (LocalRx.IsMatch(t)) return "http://" + t;
                if (HostRx.IsMatch(t)) return "https://" + t;
            }
            return SearchUrl + Uri.EscapeDataString(t);
        }

        void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Sized sign-in popups retain WebView2's popup behavior and window.opener.
            CoreWebView2WindowFeatures f = e.WindowFeatures;
            if (f != null && (f.HasSize || f.HasPosition)) return;
            e.Handled = true;
            string url = e.Uri;
            // Leave the engine callback before folding: its originating view may be the oldest tab.
            BeginInvoke(new Action(async delegate { await OpenTabAsync(url, null); }));
        }

        void OnAcceleratorKeyPressed(object sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown &&
                e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown) return;
            Keys key = (Keys)(int)e.VirtualKey | ModifierKeys;
            if (HandleShortcut(key, true)) e.Handled = true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (HandleShortcut(keyData, false)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool HandleShortcut(Keys key, bool fromWeb)
        {
            if (key == (Keys.Control | Keys.Shift | Keys.B))
            {
                BeginInvoke(new Action(ToggleBossMode));
                return true;
            }
            if (bossMode) return key != (Keys.Alt | Keys.F4);
            switch (key)
            {
                case Keys.F11:
                    ToggleMode();
                    return true;
                case Keys.F12: // Ctrl+Shift+I still opens the full Edge DevTools
                    BeginInvoke(new Action(ToggleDevTools));
                    return true;
                case Keys.Control | Keys.Shift | Keys.P:
                    ApplyPin(!pinned);
                    return true;
                case Keys.Control | Keys.D:
                    if (options.IsApp) return false;
                    BeginInvoke(new Action(SaveAsApp)); // don't open a modal dialog inside a key callback
                    return true;
                case Keys.Control | Keys.L:
                case Keys.Alt | Keys.D:
                case Keys.F6:
                    FocusAddress();
                    return true;
                case Keys.Control | Keys.T:
                    BeginInvoke(new Action(async delegate { await OpenTabAsync(HomeUrl, null); }));
                    return true;
                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.Shift | Keys.Tab:
                    if (tabs.Count > 0)
                    {
                        int delta = (key & Keys.Shift) != 0 ? -1 : 1;
                        BrowserTab next = tabs[(tabs.IndexOf(activeTab) + delta + tabs.Count) % tabs.Count];
                        BeginInvoke(new Action(delegate { if (tabs.Contains(next)) ActivateTab(next); }));
                    }
                    return true;
                case Keys.Control | Keys.W:
                    BeginInvoke(new Action(CloseActiveTab));
                    return true;
                case Keys.Escape:
                    if (fromWeb || !address.Box.Focused) return false;
                    address.Box.Text = web != null ? web.Source : "";
                    FocusWeb();
                    return true;
            }
            if (fromWeb || web == null) return false; // the web view handles these itself
            switch (key)
            {
                case Keys.F5:
                case Keys.Control | Keys.R:
                    web.Reload();
                    return true;
                case Keys.Alt | Keys.Left:
                    if (web.CanGoBack) web.GoBack();
                    return true;
                case Keys.Alt | Keys.Right:
                    if (web.CanGoForward) web.GoForward();
                    return true;
            }
            return false;
        }

        void OnAddressKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Navigate(address.Box.Text);
            FocusWeb();
        }

        void FocusAddress()
        {
            webHadFocus = false;
            address.Box.Focus();
            address.Box.SelectAll();
        }

        void FocusWeb()
        {
            if (bossMode) return;
            webHadFocus = true;
            if (controller != null) controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }

        void ReloadOrStop()
        {
            if (web == null) return;
            if (loading) web.Stop(); else web.Reload();
        }

        void SetLoading(bool on)
        {
            loading = on;
            reloadBtn.Symbol = on ? Icons.Stop : Icons.Refresh;
            SetTip(reloadBtn, on ? "Stop" : "Reload (F5)");
        }

        void UpdateNavButtons()
        {
            if (web == null) return;
            backBtn.Enabled = web.CanGoBack;
            fwdBtn.Enabled = web.CanGoForward;
        }

        void ShowUrl()
        {
            if (web == null || address.Box.Focused) return;
            address.Box.Text = web.Source == "about:blank" ? "" : web.Source;
            address.ScrollToStart();
        }
    }
}
