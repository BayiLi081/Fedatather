using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Fedatather
{
    // Segoe Fluent Icons (Win 11) / Segoe MDL2 Assets (Win 10) share these code points.
    static class Icons
    {
        public const string Back = "";
        public const string Forward = "";
        public const string Refresh = "";
        public const string Stop = "";
        public const string Pin = "";
        public const string Pinned = "";
        public const string Phone = "";
        public const string FullScreen = "";
        public const string Minimize = "";
        public const string Close = "";
        public const string InstallApp = "";
        public const string Person = "\uE77B";
        public const string DevTools = "";

        static Font font;

        public static Font Font
        {
            get
            {
                if (font == null)
                {
                    font = new Font("Segoe Fluent Icons", 10f);
                    if (font.Name != "Segoe Fluent Icons")
                    {
                        font.Dispose();
                        font = new Font("Segoe MDL2 Assets", 10f);
                    }
                }
                return font;
            }
        }
    }

    sealed class Theme
    {
        public bool Dark;
        public Color Bar, TabBar, TabHover, TabActive, Border, Fore, Muted, ForeDisabled, Hover, Press, Field, FieldBorder, Accent, AccentFore, Danger, Page;

        public static Theme FromSystem()
        {
            bool dark = false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    dark = v is int && (int)v == 0;
                }
            }
            catch { }

            Theme t = new Theme();
            t.Dark = dark;
            if (dark)
            {
                t.Bar = Hex(0x29292C); t.TabBar = Hex(0x202023);
                t.TabHover = Hex(0x303034); t.TabActive = Hex(0x3A3A3E);
                t.Border = Hex(0x3A3A3E);
                t.Fore = Hex(0xF2F2F4); t.Muted = Hex(0xA6A6AB); t.ForeDisabled = Hex(0x69696E);
                t.Hover = Hex(0x3A3A3E); t.Press = Hex(0x45454A);
                t.Field = Hex(0x38383C); t.FieldBorder = Hex(0x45454A);
                t.Accent = Hex(0x4CC2FF); t.AccentFore = Hex(0x0B0B0B);
                t.Page = Hex(0x1B1B1D);
            }
            else
            {
                t.Bar = Hex(0xF5F5F7); t.TabBar = Hex(0xE9E9ED);
                t.TabHover = Hex(0xDEDEE3); t.TabActive = Color.White;
                t.Border = Hex(0xD9D9DE);
                t.Fore = Hex(0x26262B); t.Muted = Hex(0x717178); t.ForeDisabled = Hex(0xA5A5AB);
                t.Hover = Hex(0xE5E5E9); t.Press = Hex(0xD8D8DE);
                t.Field = Color.White; t.FieldBorder = Hex(0xDCDCE1);
                t.Accent = Hex(0x0878D1); t.AccentFore = Color.White;
                t.Page = Color.White;
            }
            t.Danger = Hex(0xC42B1C);
            return t;
        }

        static Color Hex(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }
    }

    sealed class SafariTabButton : Control
    {
        readonly Theme theme;
        bool hover;
        public bool Active;

        public SafariTabButton(Theme theme)
        {
            this.theme = theme;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PageTab;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(theme.TabBar);
            RectangleF r = new RectangleF(1, 2, Width - 3, Height - 4);
            Color fill = Active ? theme.TabActive : (hover ? theme.TabHover : Color.Empty);
            if (!fill.IsEmpty)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Gfx.RoundRect(r, Math.Min(8, r.Height / 2)))
                using (SolidBrush brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
            }
            Rectangle textBounds = Rectangle.Inflate(ClientRectangle, -8, 0);
            TextRenderer.DrawText(g, Text, Font, textBounds, Active ? theme.Fore : theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    static class Gfx
    {
        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            GraphicsPath p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Painted toolbar. Its empty areas (including the title text) are drag handles for the window.
    sealed class ToolBar : Control
    {
        readonly Theme theme;
        readonly Font titleFont = new Font("Segoe UI", 9f);
        string title = "";
        Rectangle titleBounds;

        public ToolBar(Theme theme)
        {
            this.theme = theme;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        public string Title
        {
            get { return title; }
            set { value = value ?? ""; if (title != value) { title = value; Invalidate(titleBounds); } }
        }

        // Where the title is drawn; empty = no title (single-row full-screen bar).
        public Rectangle TitleBounds
        {
            get { return titleBounds; }
            set { if (titleBounds != value) { titleBounds = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(theme.Bar);
            if (!titleBounds.IsEmpty && title.Length > 0)
                TextRenderer.DrawText(e.Graphics, title, titleFont, titleBounds, theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (Pen p = new Pen(theme.Border)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    sealed class IconButton : Control
    {
        readonly Theme theme;
        string symbol;
        bool hover, pressed, active;
        public bool Danger;
        public bool Round;     // circular highlight instead of a rounded square
        public Color Surface;  // colour behind the button

        public IconButton(Theme theme, string symbol)
        {
            this.theme = theme;
            this.symbol = symbol;
            Surface = theme.Bar;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        public string Symbol
        {
            get { return symbol; }
            set { if (symbol != value) { symbol = value; Invalidate(); } }
        }

        public bool Active
        {
            get { return active; }
            set { if (active != value) { active = value; Invalidate(); } }
        }

        public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

        // Exposed to screen readers / UI Automation as a real push button.
        protected override AccessibleObject CreateAccessibilityInstance() { return new ButtonAccessible(this); }

        sealed class ButtonAccessible : ControlAccessibleObject
        {
            public ButtonAccessible(IconButton owner) : base(owner) { }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override string DefaultAction { get { return "Press"; } }
            // Queued: the accessibility call arrives cross-process, and a click may open windows / start COM work.
            public override void DoDefaultAction()
            {
                IconButton b = (IconButton)Owner;
                b.BeginInvoke(new Action(b.PerformClick));
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Surface);

            Color fg = Enabled ? theme.Fore : theme.ForeDisabled;
            Color bg = Color.Empty;
            if (active)
            {
                bg = theme.Hover;
                fg = theme.Accent;
            }
            else if (Enabled && (hover || pressed))
            {
                if (Danger) { bg = theme.Danger; fg = Color.White; }
                else bg = pressed ? theme.Press : theme.Hover;
            }

            if (!bg.IsEmpty)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = Gfx.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), Round ? Height / 2f : Height * 0.18f))
                using (SolidBrush b = new SolidBrush(bg))
                    g.FillPath(b, p);
            }

            TextRenderer.DrawText(g, symbol, Icons.Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    // Pill-shaped address field: a borderless TextBox centred inside a painted rounded rect,
    // with the "save as web app" button tucked into its right end.
    sealed class AddressBox : Control
    {
        readonly Theme theme;
        public readonly TextBox Box;
        public readonly IconButton AppButton;
        bool showAppButton = true;

        public AddressBox(Theme theme)
        {
            this.theme = theme;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.IBeam;

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.Font = new Font("Segoe UI", 9.75f);
            Box.BackColor = theme.Field;
            Box.ForeColor = theme.Fore;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);

            AppButton = new IconButton(theme, Icons.InstallApp);
            AppButton.Surface = theme.Field;
            AppButton.Round = true;
            AppButton.Cursor = Cursors.Default;
            Controls.Add(AppButton);
        }

        public bool ShowAppButton
        {
            get { return showAppButton; }
            set { showAppButton = value; AppButton.Visible = value; PerformLayout(); }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (AppButton == null) return; // layout fires while the constructor is still adding children
            int pad = Math.Max(6, Height / 2 - 2);
            int right = Width - pad;
            if (showAppButton)
            {
                int s = Height - 6;
                AppButton.SetBounds(Width - s - 3, 3, s, s);
                right = Width - s - 5;
            }
            Box.SetBounds(pad, (Height - Box.Height) / 2, Math.Max(0, right - pad), Box.Height);
            if (!Box.Focused) ScrollToStart(); // an edit control keeps its old scroll offset when widened
        }

        public void ScrollToStart()
        {
            Box.Select(0, 0);
            Box.ScrollToCaret();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Box.Focus();
            Box.SelectAll();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(theme.Bar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath p = Gfx.RoundRect(r, r.Height / 2))
            {
                using (SolidBrush b = new SolidBrush(theme.Field)) g.FillPath(b, p);
                using (Pen pen = new Pen(Box.Focused ? theme.Accent : theme.FieldBorder, Box.Focused ? 1.5f : 1f)) g.DrawPath(pen, p);
            }
        }
    }

    sealed class AppSettings
    {
        readonly string path;
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AppSettings(string path) { this.path = path; }

        public static AppSettings Load(string path)
        {
            AppSettings s = new AppSettings(path);
            try
            {
                foreach (string line in File.ReadAllLines(s.path))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) s.values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return s;
        }

        public string Get(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
        }

        public void Set(string key, string value) { values[key] = value ?? ""; }

        public Point? GetPoint(string key)
        {
            string[] p = Get(key, "").Split(',');
            int x, y;
            if (p.Length == 2 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y)) return new Point(x, y);
            return null;
        }

        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in values) lines.Add(kv.Key + "=" + kv.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines.ToArray());
            }
            catch { }
        }
    }

    static class Native
    {
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 2;
        public const int WS_MINIMIZEBOX = 0x20000;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Windows 11 rounded corners; silently ignored on Windows 10.
        public static void SetRoundCorners(IntPtr hwnd, bool round)
        {
            int pref = round ? 2 /* DWMWCP_ROUND */ : 1 /* DWMWCP_DONOTROUND */;
            try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
            catch { }
        }

        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            int on = dark ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int)); }
            catch { }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    }
}
