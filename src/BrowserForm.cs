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
        const int PhoneWindowHeight = 900; // 750 + 20%
        const int BarHeight = 36;        // navigation row
        const int TitleRowHeight = 30;   // extra top row in phone size: title + window buttons, free to drag
        const int TitleButtonHeight = 24;
        const int ButtonWidth = 26;
        const int ButtonHeight = 28;
        const string Brand = "Fedatather";
        const string HomeUrl = "https://www.google.com/";
        const string SearchUrl = "https://www.google.com/search?q=";

        readonly LaunchOptions options;
        Action<string> openExternalLink = delegate(string url) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); };
        readonly bool fixedSize;      // web app saved with a fixed window size: no size switching
        readonly string windowName;
        readonly float scale;
        readonly Theme theme;
        readonly AppSettings settings;
        readonly ToolTip tips = new ToolTip();
        readonly ToolBar bar;
        readonly AddressBox address;
        readonly IconButton backBtn, fwdBtn, reloadBtn, devBtn, splitBtn, settingsBtn, modeBtn, pinBtn, minBtn, closeBtn, bossBtn;
        SettingsForm settingsWindow;
        bool restoreSettings;
        readonly Panel host;
        readonly TerminalPanel terminal;
        readonly TerminalLauncher termFab;
        readonly PasswordManager passwords;
        readonly string startUrl;
        readonly Panel tabBar;
        readonly FlowLayoutPanel tabButtons;
        readonly Button newTabButton, foldedButton;
        readonly List<BrowserTab> tabs = new List<BrowserTab>();
        readonly SemaphoreSlim tabGate = new SemaphoreSlim(1, 1);
        readonly FoldedTabs folded;
        BrowserTab activeTab;
        bool closing, bossMode, restoreDevTools, terminalOpen, restoreTerminalFocus;
        readonly PictureBox bossCover;

        // Split screen: three panes side by side in full screen, two stacked top and bottom in phone size.
        // A pane shows a tab or is empty. activeTab is the tab in the current pane, so it is null while an empty
        // pane is current. A dual web app is a phone window that is always split.
        const int MaxPanes = 3;
        int PaneCount { get { return phoneMode ? 2 : 3; } }
        bool splitMode;
        readonly BrowserTab[] panes = new BrowserTab[MaxPanes];
        int currentPane;
        readonly PaneHeader[] paneHeaders = new PaneHeader[MaxPanes];
        readonly EmptyPane[] emptyPanes = new EmptyPane[MaxPanes];
        Rectangle markedPane;  // the current pane's column: its frame is drawn red

        sealed class BrowserTab
        {
            public CoreWebView2Controller Controller;
            public CoreWebView2 Web;
            public string Url, Title;
            public bool Loading, InitialNavigationComplete;
            public DevToolsLite Tools; // this page's DevTools Lite, shown while the page is the current one
            public CoreWebView2MemoryUsageTargetLevel MemoryLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }

        CoreWebView2Environment env;
        CoreWebView2Controller controller;
        CoreWebView2 web;
        DevToolsLite devTools;         // the one on screen: the current page's
        bool toolsOn, toolsClosingQuietly;
        Rectangle toolsBounds;         // where DevTools Lite was last, so a pane's own one opens in the same place
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
            host.Resize += delegate { LayoutViews(); };
            // Only the frame around the current pane's page shows: the page and header cover the rest.
            host.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (markedPane.IsEmpty) return;
                using (SolidBrush b = new SolidBrush(theme.Target)) e.Graphics.FillRectangle(b, markedPane);
            };
            for (int i = 0; i < MaxPanes; i++)
            {
                int pane = i;
                PaneHeader header = new PaneHeader(theme, scale) { Number = i + 1, Visible = false };
                header.Click += delegate { FocusPane(pane, true); };
                header.CloseButton.Click += delegate { ClearPane(pane); };
                SetTip(header.CloseButton, "Empty this pane (the tab stays open)");
                EmptyPane empty = new EmptyPane(theme, scale) { Visible = false };
                empty.Click += delegate { FocusPane(pane, true); };
                paneHeaders[i] = header;
                emptyPanes[i] = empty;
                host.Controls.Add(header);
                host.Controls.Add(empty);
            }

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
            settingsBtn = AddButton(Icons.Settings, "Settings (Ctrl+,)", delegate { ShowSettings(); });
            splitBtn = AddButton("", SplitTip(), delegate { ToggleSplit(); });
            splitBtn.Glyph = Icons.DrawSplit;
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

            // Saved passwords live next to the browser data and are shared with saved web apps, like logins.
            passwords = new PasswordManager(Path.Combine(Program.DataDir, "passwords.dat"), theme, scale);
            passwords.BossKey = delegate { if (!bossMode) ToggleBossMode(); };
            passwords.SitesUpdated += delegate { UpdateKeyButton(); };
            address.KeyButton.Click += delegate { ShowPasswordMenu(); };
            SetTip(address.KeyButton, "Fill a saved password for this site (Ctrl+Shift+K)");

            tabBar = new Panel { Dock = DockStyle.Top, Height = S(32), BackColor = theme.TabBar, Visible = !options.IsApp };
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

            // The terminal floats over the page: a round button in the bottom-right corner unfolds into its window.
            terminal = new TerminalPanel(theme, scale, SetTip) { Visible = false };
            string[] place = settings.Get("terminal", "").Split(',');
            int tw, th, tx, ty;
            if (place.Length == 4 && int.TryParse(place[0], out tw) && int.TryParse(place[1], out th) &&
                int.TryParse(place[2], out tx) && int.TryParse(place[3], out ty))
            {
                terminal.RestoreSize = new Size(tw, th);
                terminal.Offset = new Point(tx, ty);
            }
            terminal.Area = delegate { return host.Bounds; };
            termFab = new TerminalLauncher(theme, scale);
            SetTip(termFab, "Terminal (Ctrl+`)");
            termFab.Click += delegate { ShowTerminal(); };
            host.SizeChanged += delegate { LayoutFloating(); };
            host.LocationChanged += delegate { LayoutFloating(); };
            terminal.HideButton.Click += delegate { HideTerminal(); };
            terminal.KillButton.Click += delegate { terminal.View.Stop(); HideTerminal(); };
            terminal.View.ShellExited += delegate { HideTerminal(); };
            terminal.View.GotFocus += delegate { webHadFocus = false; };

            // Docking runs from the end of the collection: bar claims the top, host fills the rest.
            Controls.Add(host);
            Controls.Add(tabBar);
            Controls.Add(bar);
            bossCover = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White, Visible = false, TabStop = false };
            bossCover.Resize += delegate { LayoutBossButton(); };
            Controls.Add(bossCover);
            // Not docked: they float above the page (in front of it in the z-order).
            Controls.Add(termFab);
            Controls.Add(terminal);
            termFab.BringToFront();
            terminal.BringToFront();
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
        //   [ page title ..... (drag anywhere) .....  boss  terminal  devtools  size  pin  settings | min  close ]
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
            settingsBtn.SetBounds(r -= bw, winY, bw, winH);
            pinBtn.SetBounds(r -= bw, winY, bw, winH);
            if (!fixedSize) modeBtn.SetBounds(r -= bw, winY, bw, winH);
            splitBtn.Visible = !options.IsApp;
            if (splitBtn.Visible) splitBtn.SetBounds(r -= bw, winY, bw, winH);
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
            settings.Set("terminal", terminal.RestoreSize.Width + "," + terminal.RestoreSize.Height + "," +
                terminal.Offset.X + "," + terminal.Offset.Y);
            // A web app always reopens at its own address; the browser reopens where you left off.
            if (!options.IsApp && web != null && !string.IsNullOrEmpty(web.Source)) settings.Set("url", web.Source);
            settings.Save();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            CloseAllTools();
            if (settingsWindow != null) settingsWindow.Close();
            terminal.View.Stop();
            passwords.Dispose(); // locks: the key leaves memory
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
            LayoutViews();
            LayoutFloating();
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
            if (phone != phoneMode) ExitSplit(); // the panes differ: two rows in phone size, three columns in full screen
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
            if (phone) splitBtn.Glyph = Icons.DrawSplitRows; else splitBtn.Glyph = Icons.DrawSplit;
            splitBtn.Invalidate();
            if (!splitMode) SetTip(splitBtn, SplitTip());
            LayoutViews();
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
            tabBar.Visible = !on && !bossMode && !options.IsApp;
            UpdateTerminalVisibility();
            if (!phoneMode)
            {
                Screen screen = Screen.FromRectangle(Bounds);
                Bounds = on ? screen.Bounds : screen.WorkingArea;
            }
            LayoutViews(); // in split screen, a fullscreen page takes over the whole page area
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
                restoreSettings = settingsWindow != null && settingsWindow.Visible;
                if (restoreSettings) settingsWindow.Hide();
                restoreTerminalFocus = terminal.ContainsFocus;
                passwords.Vault.Lock(); // coming back asks for the PIN again
                foreach (BrowserTab tab in tabs) tab.Controller.IsVisible = false;
                host.Visible = bar.Visible = tabBar.Visible = false;
                UpdateTerminalVisibility(); // the shell keeps running behind the cover
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
                bar.Visible = !htmlFullscreen;
                tabBar.Visible = !htmlFullscreen && !options.IsApp;
                UpdateTerminalVisibility();
                LayoutBar();
                LayoutViews();
                UpdateTitle();
                if (restoreDevTools && devTools != null) devTools.Show(this);
                if (restoreSettings && settingsWindow != null) settingsWindow.Show(this); // locked by now: it asks for the PIN again
                if (restoreTerminalFocus && terminal.Visible) terminal.View.Focus();
                else if (webHadFocus) FocusWeb();
                else FocusAddress();
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
            if (bossMode) return;
            if (toolsOn) { CloseAllTools(); return; }
            if (web == null) return;
            toolsOn = true;
            devBtn.Active = true;
            ShowTools(activeTab, true);
        }

        // Shows the tab's own DevTools Lite, creating it if needed. In split screen each pane's page keeps its own,
        // so what was collected from one page survives looking at another; only the current pane's is on screen.
        void ShowTools(BrowserTab tab, bool activate)
        {
            if (tab == null || !toolsOn || bossMode) { UpdatePanes(); return; }
            DevToolsLite tools = tab.Tools;
            if (tools == null)
            {
                tools = new DevToolsLite(this, env, tab.Web, theme, scale);
                if (!toolsBounds.IsEmpty) tools.Bounds = toolsBounds;
                DevToolsLite created = tools;
                created.FormClosed += delegate
                {
                    if (tab.Tools == created) tab.Tools = null;
                    if (devTools == created) devTools = null;
                    if (!toolsClosingQuietly) CloseAllTools(); // closed with its own ✕: DevTools Lite is off
                };
                tab.Tools = tools;
            }
            tools.OpenInBackground = !activate;
            tools.Text = ToolsTitle(tab);
            devTools = tools;
            if (!tools.Visible) tools.Show(this); // owned: stays above this window (and above it when pinned)
            UpdatePanes();
        }

        // Says which page it reads; in split screen, which pane too (the one with the red dot).
        string ToolsTitle(BrowserTab tab)
        {
            int pane = splitMode ? Array.IndexOf(panes, tab) : -1;
            return pane < 0 ? "DevTools Lite" : "DevTools Lite — reading pane " + (pane + 1) + " ● " + tab.Title;
        }

        // The current page changed. In split screen DevTools Lite follows it (the old page's is kept, hidden);
        // otherwise it closes, as switching tabs always did.
        void SwitchTools(BrowserTab tab)
        {
            if (!toolsOn) return;
            if (!splitMode) { CloseAllTools(); return; }
            if (devTools != null)
            {
                toolsBounds = devTools.Bounds;
                if (devTools.Visible) devTools.Hide();
                devTools = null;
            }
            ShowTools(tab, false);
        }

        // One page's DevTools Lite, leaving DevTools Lite on (the page closed or left split screen).
        void CloseTools(BrowserTab tab)
        {
            DevToolsLite tools = tab.Tools;
            if (tools == null) return;
            tab.Tools = null;
            if (devTools == tools) devTools = null;
            toolsClosingQuietly = true;
            try { tools.Close(); }
            finally { toolsClosingQuietly = false; }
        }

        void CloseAllTools()
        {
            toolsOn = false;
            devBtn.Active = false;
            foreach (BrowserTab tab in tabs.ToArray()) CloseTools(tab);
            devTools = null;
            UpdatePanes();
        }

        // ---------------------------------------------------------------- saved passwords

        // The key appears in the address bar only on sites with saved logins, to fill them. Knowing that needs no PIN;
        // the logins do. The password manager itself is in Settings.
        void UpdateKeyButton()
        {
            address.ShowKeyButton = web != null && passwords.Vault.HasLoginsFor(web.Source);
        }

        // On a site with saved logins: unlock, then pick the login to fill. Elsewhere (Ctrl+Shift+K): the passwords
        // in Settings.
        void ShowPasswordMenu()
        {
            if (bossMode || closing) return;
            BrowserTab tab = activeTab;
            if (tab == null || !passwords.Vault.HasLoginsFor(tab.Web.Source)) { ShowSettings(); return; }
            if (!passwords.EnsureUnlocked(this) || activeTab != tab) return;
            List<SavedLogin> found;
            try { found = passwords.Vault.FindFor(tab.Web.Source); }
            catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, Brand); return; }
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            Uri page;
            string site = Uri.TryCreate(tab.Web.Source, UriKind.Absolute, out page) ? page.Host : tab.Web.Source;
            menu.Items.Add("Fill a saved login on " + site.Replace("&", "&&")).Enabled = false;
            menu.Items.Add(new ToolStripSeparator());
            foreach (SavedLogin item in found)
            {
                SavedLogin login = item;
                string text = login.Username.Length > 0 ? login.Username : "(no username)";
                if (login.Name.Length > 0 && login.Name != login.Site) text += "   — " + login.Name;
                ToolStripItem fill = menu.Items.Add(text.Replace("&", "&&"));
                fill.ToolTipText = login.Url;
                fill.Click += async delegate { await FillLogin(tab, login); };
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Manage saved passwords…").Click += delegate { BeginInvoke(new Action(ShowSettings)); };
            menu.Closed += delegate { BeginInvoke(new Action(menu.Dispose)); };
            menu.Show(address.KeyButton, new Point(0, address.KeyButton.Height));
        }

        // ---------------------------------------------------------------- settings

        // The gear (Ctrl+,). Passwords is a section in here rather than a button of its own.
        void ShowSettings() { OpenSettings(true, Rectangle.Empty); }

        void OpenSettings(bool activate, Rectangle bounds)
        {
            if (bossMode || closing || env == null) return;
            if (settingsWindow != null)
            {
                if (settingsWindow.WindowState == FormWindowState.Minimized) settingsWindow.WindowState = FormWindowState.Normal;
                if (activate) settingsWindow.Activate();
                return;
            }
            settingsWindow = new SettingsForm(this, env, passwords, theme, scale,
                delegate(string url) { BeginInvoke(new Action(async delegate { Activate(); await OpenTabAsync(url, null); })); });
            settingsWindow.BossKey = delegate { if (!bossMode) ToggleBossMode(); };
            settingsWindow.OpenInBackground = !activate;
            if (!bounds.IsEmpty) settingsWindow.Bounds = bounds;
            settingsWindow.FormClosed += delegate { settingsWindow = null; settingsBtn.Active = false; };
            settingsWindow.Show(this); // owned: stays above the browser, even when it's pinned
            settingsBtn.Active = true;
        }

        // Fills only into the page the login was picked for, and only while that page is still on the login's site.
        async Task<int> FillLogin(BrowserTab tab, SavedLogin login)
        {
            if (!tabs.Contains(tab) || !PasswordVault.Matches(login.Url, tab.Web.Source)) return 0;
            passwords.Vault.Touch();
            string result = await tab.Web.ExecuteScriptAsync(LoginFill.Script(login.Username, login.Password));
            int filled;
            if (!int.TryParse(result, out filled)) filled = 0;
            if (filled == 0 && !IsDisposed)
                tips.Show("No login fields found on this page.", address.KeyButton, 0, address.KeyButton.Height + S(4), 2500);
            return filled;
        }

        // ---------------------------------------------------------------- split screen

        string SplitTip()
        {
            return phoneMode ? "Split screen: two pages, top and bottom (Ctrl+\\)"
                             : "Split screen: three pages side by side (Ctrl+\\)";
        }

        void ToggleSplit()
        {
            if (options.IsApp) return;
            if (bossMode) return;
            if (splitMode) ExitSplit(); else EnterSplit();
        }

        // Three panes in full screen, two in phone size: the current page and the most recently opened other tabs,
        // in tab-strip order. With fewer tabs open, the remaining panes stay empty until something is opened in them.
        // A dual web app is the only saved app that splits.
        void EnterSplit()
        {
            if ((options.IsApp && !options.IsDual) || splitMode || bossMode) return;
            List<BrowserTab> shown = new List<BrowserTab>();
            if (activeTab != null) shown.Add(activeTab);
            for (int i = tabs.Count - 1; i >= 0 && shown.Count < PaneCount; i--)
                if (!shown.Contains(tabs[i])) shown.Add(tabs[i]);
            shown.Sort(delegate(BrowserTab a, BrowserTab b) { return tabs.IndexOf(a).CompareTo(tabs.IndexOf(b)); });
            Array.Clear(panes, 0, MaxPanes);
            for (int i = 0; i < shown.Count; i++) panes[i] = shown[i];
            currentPane = Math.Max(0, Array.IndexOf(panes, activeTab));
            splitMode = true;
            splitBtn.Active = true;
            SetTip(splitBtn, "Leave split screen (Ctrl+\\)");
            SetActive(activeTab, false);
        }

        // Back to one page: the current pane's tab, or the nearest shown one when the current pane is empty.
        void ExitSplit()
        {
            if (!splitMode) return;
            BrowserTab keep = activeTab;
            for (int i = 0; keep == null && i < PaneCount; i++) keep = panes[(currentPane + i) % PaneCount];
            if (keep == null && tabs.Count > 0) keep = tabs[tabs.Count - 1];
            splitMode = false;
            Array.Clear(panes, 0, MaxPanes);
            // One page on screen again: only its DevTools Lite stays (if it is the current page's).
            if (keep != activeTab) CloseAllTools();
            else foreach (BrowserTab tab in tabs.ToArray()) if (tab != keep) CloseTools(tab);
            splitBtn.Active = false;
            SetTip(splitBtn, SplitTip());
            bool focusWeb = webHadFocus;
            SetActive(keep, false);
            if (focusWeb) FocusWeb();
        }

        int TargetPane()
        {
            if (panes[currentPane] == null) return currentPane;
            for (int i = 0; i < PaneCount; i++) if (panes[i] == null) return i;
            return currentPane;
        }

        void FocusPane(int pane, bool moveFocus)
        {
            if (!splitMode || pane < 0 || pane >= PaneCount) return;
            currentPane = pane;
            SetActive(panes[pane], moveFocus);
        }

        void ShowInPane(int pane, BrowserTab tab)
        {
            if (!splitMode || !tabs.Contains(tab)) return;
            int old = Array.IndexOf(panes, tab);
            if (old >= 0) panes[old] = null;
            panes[pane] = tab;
            currentPane = pane;
            SetActive(tab, true);
        }

        // The pane's ✕: the pane empties, its tab stays open in the tab strip.
        void ClearPane(int pane)
        {
            if (!splitMode || panes[pane] == null) return;
            panes[pane] = null;
            currentPane = pane;
            SetActive(null, false);
        }

        // Positions the page views: one filling the page area, or the three split-screen panes (each with its header,
        // and an empty pane's placeholder where no page is shown). A page in fullscreen takes the whole area.
        void LayoutViews()
        {
            if (host == null || paneHeaders[MaxPanes - 1] == null) return; // still constructing
            Rectangle area = host.ClientRectangle;
            bool show = !bossMode && WindowState != FormWindowState.Minimized;
            bool split = splitMode && !(htmlFullscreen && activeTab != null);
            int count = PaneCount;
            bool rows = phoneMode; // phone size stacks the panes top and bottom; full screen puts them side by side
            int gap = Math.Max(1, S(1)), headerH = S(28);
            int size = ((rows ? area.Height : area.Width) - gap * (count - 1)) / count;
            // Every pane's page sits inside a frame, so marking the current one never resizes a page.
            int frame = Math.Max(2, S(2));
            Rectangle[] content = new Rectangle[MaxPanes];
            Rectangle marked = Rectangle.Empty;
            for (int i = 0; i < MaxPanes; i++)
            {
                bool used = split && i < count;
                paneHeaders[i].Visible = used;
                emptyPanes[i].Visible = used && panes[i] == null;
                if (!used) continue;
                Rectangle column;
                if (rows)
                {
                    int y = area.Y + i * (size + gap);
                    column = new Rectangle(area.X, y, area.Width, i == count - 1 ? area.Bottom - y : size);
                }
                else
                {
                    int x = area.X + i * (size + gap);
                    column = new Rectangle(x, area.Y, i == count - 1 ? area.Right - x : size, area.Height);
                }
                content[i] = new Rectangle(column.X + frame, column.Y + headerH,
                    Math.Max(0, column.Width - frame * 2), Math.Max(0, column.Height - headerH - frame));
                if (i == currentPane) marked = column;
                paneHeaders[i].SetBounds(column.X, column.Y, column.Width, headerH);
                emptyPanes[i].Bounds = content[i];
            }
            foreach (BrowserTab tab in tabs)
            {
                int pane = split ? Array.IndexOf(panes, tab) : -1;
                bool visible = show && (split ? pane >= 0 : tab == activeTab);
                if (visible) tab.Controller.Bounds = split ? content[pane] : area;
                tab.Controller.IsVisible = visible;
                // Pages out of sight keep running but let WebView2 trim their memory (Microsoft's guidance: only
                // ever lower it for hidden pages). Set only on change: it's a call into the engine.
                CoreWebView2MemoryUsageTargetLevel level = visible
                    ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
                if (tab.MemoryLevel != level)
                {
                    try { tab.Web.MemoryUsageTargetLevel = level; tab.MemoryLevel = level; }
                    catch (Exception) { } // an older WebView2 Runtime without it: the page just keeps its memory
                }
            }
            host.BackColor = split ? theme.Border : theme.Page; // the gaps and frames between panes
            if (marked != markedPane) { markedPane = marked; host.Invalidate(); }
        }

        // Pane headers' titles and current-pane highlight, and what each empty pane offers.
        void UpdatePanes()
        {
            if (!splitMode || paneHeaders[MaxPanes - 1] == null) return;
            List<BrowserTab> offscreen = new List<BrowserTab>();
            foreach (BrowserTab tab in tabs) if (Array.IndexOf(panes, tab) < 0) offscreen.Add(tab);
            for (int i = 0; i < PaneCount; i++)
            {
                int pane = i;
                BrowserTab shown = panes[i];
                paneHeaders[i].Title = shown == null ? "Empty" : shown.Title;
                paneHeaders[i].Current = i == currentPane;
                paneHeaders[i].Reading = devTools != null && shown != null && devTools == shown.Tools;
                paneHeaders[i].CloseButton.Visible = shown != null && !options.IsDual; // a dual app keeps both pages
                if (shown != null) continue;
                List<PaneChoice> choices = new List<PaneChoice>();
                choices.Add(new PaneChoice { Text = "+   New tab", Pick = async delegate
                {
                    if (!splitMode || panes[pane] != null) return;
                    currentPane = pane;
                    await OpenTabAsync(HomeUrl, null);
                } });
                foreach (BrowserTab item in offscreen)
                {
                    BrowserTab tab = item;
                    choices.Add(new PaneChoice { Text = tab.Title, Tip = tab.Url, Pick = delegate { ShowInPane(pane, tab); } });
                }
                emptyPanes[i].SetChoices(choices);
            }
        }

        // ---------------------------------------------------------------- terminal

        void UpdateTerminalVisibility()
        {
            bool room = !bossMode && !htmlFullscreen;
            terminal.Visible = terminalOpen && room;
            termFab.Visible = !terminalOpen && room;
        }

        // Keeps the terminal button in the page's bottom-right corner and the terminal window inside the page.
        void LayoutFloating()
        {
            if (termFab == null) return; // still constructing
            Rectangle a = host.Bounds;
            int margin = S(10);
            termFab.Location = new Point(a.Right - termFab.Width - margin, a.Bottom - termFab.Height - margin);
            if (terminal.Visible) terminal.Fit();
        }

        // Show the terminal window (starting PowerShell the first time) or fold it back into its button. Folding
        // keeps the shell running, as in VS Code.
        void ToggleTerminal()
        {
            if (bossMode) return;
            if (terminalOpen) HideTerminal(); else ShowTerminal();
        }

        // Ctrl+`: a terminal that is open but not focused gets the focus; otherwise toggle it.
        void TerminalShortcut()
        {
            if (bossMode) return;
            if (terminalOpen && terminal.Visible && !terminal.View.Focused) terminal.View.Focus();
            else ToggleTerminal();
        }

        void ShowTerminal()
        {
            terminalOpen = true;
            UpdateTerminalVisibility();
            terminal.Fit(); // size the terminal before PowerShell starts, so it starts at the right width
            terminal.PerformLayout();
            if (!terminal.View.Running)
            {
                try { terminal.View.Start(); }
                catch (Exception ex)
                {
                    terminalOpen = false;
                    UpdateTerminalVisibility();
                    string why = ex is EntryPointNotFoundException
                        ? "The terminal needs Windows 10 version 1809 or later." : ex.Message;
                    MessageBox.Show(this, "Could not start PowerShell:\n\n" + why, Brand,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                terminal.Invalidate(); // header shows the shell's name
            }
            if (terminal.Visible) terminal.View.Focus();
        }

        void HideTerminal()
        {
            if (!terminalOpen) return;
            if (terminal.ContainsFocus) FocusWeb(); // before hiding, so focus doesn't land in the address bar
            terminalOpen = false;
            UpdateTerminalVisibility();
        }

        // ---------------------------------------------------------------- web apps

        void SaveAsApp()
        {
            if (web == null || bossMode || options.IsApp || saveDialogOpen) return;
            saveDialogOpen = true;
            try
            {
                // Phone size split top and bottom with two pages: offer saving both as one dual app.
                string url = web.Source, url2 = null;
                if (splitMode && phoneMode && panes[0] != null && panes[1] != null)
                {
                    url = panes[0].Web.Source;
                    url2 = panes[1].Web.Source;
                }
                var iconTask = WebApps.FetchIconAsync(web, web.Settings.UserAgent);
                using (SaveAppDialog dlg = new SaveAppDialog(theme, scale,
                    WebApps.SuggestName(web.DocumentTitle, url), url, url2, pinned, iconTask))
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
                if (options.IsDual && !closing)
                {
                    // Dual app: the first page on top, the second below it, both live at once.
                    EnterSplit();
                    await OpenTabAsync(options.Url2, null);
                    if (splitMode && !closing) FocusPane(0, true);
                }
                if (!closing) newTabButton.Enabled = !options.IsApp;
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
                // Saved apps own exactly one page (a dual app two), including concurrent and indirect open requests.
                if (options.IsApp && tabs.Count >= (options.IsDual ? 2 : 1)) return null;
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
                // Before the first navigation, so the selection bar is in every page from the start.
                try { await c.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(SelectionActions.Script); }
                catch (Exception) { } // the page still works without it
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
                if (activeTab == tab) { ShowUrl(); UpdateKeyButton(); }
            };
            page.HistoryChanged += delegate { if (activeTab == tab) UpdateNavButtons(); };
            page.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                if (options.IsApp && tab.InitialNavigationComplete && e.IsUserInitiated && !e.IsRedirected &&
                    e.NavigationKind == CoreWebView2NavigationKind.NewDocument)
                {
                    e.Cancel = true;
                    OpenAppLink(e.Uri);
                    return;
                }
                tab.Loading = true;
                if (activeTab == tab) SetLoading(true);
            };
            page.NavigationCompleted += delegate
            {
                tab.InitialNavigationComplete = true;
                tab.Loading = false;
                if (tab.Tools != null) tab.Tools.PageChanged();
                if (activeTab != tab) return;
                SetLoading(false);
                UpdateNavButtons();
            };
            page.FaviconChanged += delegate { passwords.CaptureIcon(page); }; // only kept for sites with saved logins
            page.DocumentTitleChanged += delegate
            {
                tab.Title = string.IsNullOrEmpty(page.DocumentTitle) ? tab.Url : page.DocumentTitle;
                if (activeTab == tab) UpdateTitle();
                UpdateTabs();
            };
            page.NewWindowRequested += OnNewWindowRequested;
            page.WebMessageReceived += OnPageMessage;
            page.ContainsFullScreenElementChanged += delegate
            {
                if (activeTab == tab) SetHtmlFullscreen(page.ContainsFullScreenElement);
            };
            tab.Controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
            tab.Controller.GotFocus += delegate
            {
                // Clicking into a split-screen pane makes it the current one.
                int pane = splitMode ? Array.IndexOf(panes, tab) : -1;
                if (pane >= 0 && activeTab != tab)
                    BeginInvoke(new Action(delegate { if (splitMode && panes[pane] == tab) FocusPane(pane, false); }));
                if (activeTab == tab || pane >= 0) webHadFocus = true;
            };
        }

        // Shows the tab: in split screen, in its pane if it has one, otherwise in the current pane when that's empty,
        // else in the first empty pane, else in place of the current pane's tab (which stays open).
        void ActivateTab(BrowserTab tab)
        {
            if (splitMode)
            {
                int pane = Array.IndexOf(panes, tab);
                if (pane < 0)
                {
                    pane = TargetPane();
                    panes[pane] = tab;
                }
                currentPane = pane;
            }
            SetActive(tab, true);
        }

        // Makes tab (null = the current split-screen pane is empty) the one the toolbar and address bar act on.
        void SetActive(BrowserTab tab, bool moveFocus)
        {
            if (activeTab != tab) SwitchTools(tab);
            activeTab = tab;
            controller = tab == null ? null : tab.Controller;
            web = tab == null ? null : tab.Web;
            SetHtmlFullscreen(web != null && web.ContainsFullScreenElement);
            SetLoading(tab != null && tab.Loading);
            UpdateNavButtons();
            address.Box.Text = tab == null || tab.Url == "about:blank" ? "" : tab.Url;
            UpdateTitle();
            UpdateTabs();
            UpdateKeyButton();
            LayoutViews();
            if (!moveFocus) return;
            if (tab != null) FocusWeb();
            else if (!bossMode) FocusAddress(); // an empty pane: type the address to show there
        }

        void UpdateTitle()
        {
            Text = bossMode || activeTab == null || string.IsNullOrEmpty(activeTab.Title) ? windowName : activeTab.Title;
            bar.Title = Text;
        }

        void RemoveTab(BrowserTab tab)
        {
            bool wasActive = activeTab == tab;
            int pane = Array.IndexOf(panes, tab);
            if (pane >= 0) panes[pane] = null; // its pane stays, empty
            CloseTools(tab);
            if (wasActive)
            {
                activeTab = null; controller = null; web = null;
                SetHtmlFullscreen(false);
            }
            tabs.Remove(tab);
            tab.Controller.Close();
            if (wasActive)
            {
                if (splitMode) SetActive(null, false);
                else if (tabs.Count > 0) ActivateTab(tabs[tabs.Count - 1]);
            }
            UpdateTabs();
            LayoutViews();
        }

        void CloseActiveTab()
        {
            if (activeTab == null) return; // an empty split-screen pane
            if (tabs.Count <= 1 || options.IsDual) { Close(); return; }
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
                int pane = splitMode ? Array.IndexOf(panes, tab) : -1;
                SafariTabButton b = new SafariTabButton(theme) { Text = tab.Title, Font = Font,
                    Active = tab == activeTab, Margin = new Padding(1), Height = S(30),
                    Badge = pane >= 0 ? (pane + 1).ToString() : null, Target = pane >= 0 && pane == currentPane };
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
            if (devTools != null && activeTab != null) devTools.Text = ToolsTitle(activeTab);
            UpdatePanes();
        }

        void LayoutTabs()
        {
            if (tabButtons == null || tabs.Count == 0) return;
            int width = Math.Max(1, tabButtons.ClientSize.Width / tabs.Count - 2);
            foreach (Control b in tabButtons.Controls) b.Width = width;
        }

        void ShowFoldedTabs()
        {
            if (options.IsApp) return;
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
            string url = ToUrl(input);
            if (url == null) return;
            if (web == null)
            {
                // An empty split-screen pane is current: the address opens there in a new tab.
                if (splitMode) BeginInvoke(new Action(async delegate { await OpenTabAsync(url, null); }));
                return;
            }
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
            if (options.IsApp)
            {
                e.Handled = true;
                OpenAppLink(e.Uri);
                return;
            }
            // Sized sign-in popups retain WebView2's popup behavior and window.opener.
            CoreWebView2WindowFeatures f = e.WindowFeatures;
            if (f != null && (f.HasSize || f.HasPosition)) return;
            e.Handled = true;
            string url = e.Uri;
            // Leave the engine callback before folding: its originating view may be the oldest tab.
            BeginInvoke(new Action(async delegate { await OpenTabAsync(url, null); }));
        }

        // The selection bar's Translate / Ask AI buttons: open the service with the selected text in a new tab
        // (a saved web app has only its own page, so there it opens in the default browser).
        void OnPageMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = Json.Parse(e.WebMessageAsJson) as Dictionary<string, object>; }
            catch (FormatException) { return; }
            string action = Json.Str(m, "selAction"), text = Json.Str(m, "text");
            string url = SelectionActions.UrlFor(action, text);
            if (url == null) return;
            // Leave the engine callback first: opening a tab may fold the page that sent the message.
            BeginInvoke(new Action(async delegate
            {
                if (closing || IsDisposed || bossMode) return;
                if (SelectionActions.CopiesText(action))
                {
                    try { Clipboard.SetText(SelectionActions.Clip(text.Trim(), 100000)); }
                    catch (Exception) { } // the clipboard is busy: the address still carries the question
                }
                if (options.IsApp) OpenAppLink(url);
                else await OpenTabAsync(url, null);
            }));
        }

        void OpenAppLink(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
            BeginInvoke(new Action(delegate
            {
                if (closing || IsDisposed) return;
                try { openExternalLink(uri.AbsoluteUri); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not open the default browser:\n\n" + ex.Message,
                        windowName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }));
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
            if (key == (Keys.Control | Keys.Oemtilde))
            {
                BeginInvoke(new Action(TerminalShortcut));
                return true;
            }
            // While the terminal has focus the shell gets every key except these window-level shortcuts, as in VS Code.
            if (!fromWeb && terminal.View.Focused && key != Keys.F11 && key != Keys.F12 &&
                key != (Keys.Control | Keys.Shift | Keys.P) &&
                key != (Keys.Control | Keys.Tab) && key != (Keys.Control | Keys.Shift | Keys.Tab))
                return false;
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
                case Keys.Control | Keys.Oemcomma:
                    BeginInvoke(new Action(ShowSettings));
                    return true;
                case Keys.Control | Keys.Shift | Keys.K:
                    BeginInvoke(new Action(ShowPasswordMenu)); // don't open a modal dialog inside a key callback
                    return true;
                case Keys.Control | Keys.OemPipe: // Ctrl+\, as VS Code's split editor
                    if (options.IsApp) return true;
                    BeginInvoke(new Action(ToggleSplit));
                    return true;
                case Keys.Control | Keys.D1:
                case Keys.Control | Keys.D2:
                case Keys.Control | Keys.D3:
                    if (!splitMode) return false;
                    int pane = (int)(key & Keys.KeyCode) - (int)Keys.D1;
                    BeginInvoke(new Action(delegate { if (splitMode) FocusPane(pane, true); }));
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
                    if (options.IsApp) return true;
                    BeginInvoke(new Action(async delegate { await OpenTabAsync(HomeUrl, null); }));
                    return true;
                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.Shift | Keys.Tab:
                    if (options.IsApp) return true;
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
            backBtn.Enabled = web != null && web.CanGoBack;
            fwdBtn.Enabled = web != null && web.CanGoForward;
        }

        void ShowUrl()
        {
            if (web == null || address.Box.Focused) return;
            address.Box.Text = web.Source == "about:blank" ? "" : web.Source;
            address.ScrollToStart();
        }
    }
}
