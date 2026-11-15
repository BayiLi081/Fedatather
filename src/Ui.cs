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
        public const string Delete = "";
        public const string Key = "";
        public const string Settings = "";
        public const string ChevronUp = "";
        public const string ChevronDown = "";

        static Font font;

        // Split screen: a window outline divided into three columns.
        public static void DrawSplit(Graphics g, Rectangle bounds, Color color)
        {
            int x, y, w, h;
            float stroke;
            Frame(g, bounds, out x, out y, out w, out h, out stroke);
            SmoothingMode smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath outline = Gfx.RoundRect(new RectangleF(x + stroke / 2, y + stroke / 2, w - stroke, h - stroke), stroke * 1.5f))
            using (Pen pen = new Pen(color, stroke))
                g.DrawPath(pen, outline);
            g.SmoothingMode = smoothing;
            using (SolidBrush brush = new SolidBrush(color))
                for (int i = 1; i <= 2; i++)
                    g.FillRectangle(brush, x + (float)Math.Round(w * i / 3f - stroke / 2), y, stroke, h);
        }

        // Split screen in phone size: a window outline divided into two rows.
        public static void DrawSplitRows(Graphics g, Rectangle bounds, Color color)
        {
            int x, y, w, h;
            float stroke;
            Frame(g, bounds, out x, out y, out w, out h, out stroke);
            SmoothingMode smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath outline = Gfx.RoundRect(new RectangleF(x + stroke / 2, y + stroke / 2, w - stroke, h - stroke), stroke * 1.5f))
            using (Pen pen = new Pen(color, stroke))
                g.DrawPath(pen, outline);
            g.SmoothingMode = smoothing;
            using (SolidBrush brush = new SolidBrush(color))
                g.FillRectangle(brush, x, y + (float)Math.Round(h / 2f - stroke / 2), w, stroke);
        }

        // Icon-sized box centred in bounds, matching the icon font's glyphs.
        static void Frame(Graphics g, Rectangle bounds, out int x, out int y, out int w, out int h, out float stroke)
        {
            float em = Font.SizeInPoints * g.DpiY / 72f;
            stroke = Math.Max(1f, (float)Math.Round(em / 13f));
            w = (int)Math.Round(em);
            h = (int)Math.Round(em * 0.8f);
            x = bounds.X + (bounds.Width - w) / 2;
            y = bounds.Y + (bounds.Height - h) / 2;
        }

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
        public Color Target; // split screen: marks the current pane, the one DevTools Lite reads

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
            t.Target = Hex(0xE81123);
            return t;
        }

        static Color Hex(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }
    }

    sealed class SafariTabButton : Control
    {
        readonly Theme theme;
        bool hover;
        public bool Active;
        public string Badge; // split screen: the number of the pane showing this tab
        public bool Target;  // split screen: this tab is in the current pane (red marker)

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
            if (!string.IsNullOrEmpty(Badge))
            {
                TextFormatFlags badgeFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                             TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                string badge = Target ? "● " + Badge : Badge; // ● n
                using (Font bold = new Font(Font, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, badge, bold, textBounds, Target ? theme.Target : theme.Accent, badgeFlags);
                    int bw = TextRenderer.MeasureText(g, badge, bold, Size.Empty, badgeFlags).Width + 6;
                    textBounds = new Rectangle(textBounds.X + bw, textBounds.Y, Math.Max(0, textBounds.Width - bw), textBounds.Height);
                }
            }
            TextRenderer.DrawText(g, Text, Font, textBounds, Active ? theme.Fore : theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // The title strip over a split-screen pane: its number and page title. The current pane (the one the address
    // bar, back / forward and DevTools Lite act on) gets a red dot and a red top edge.
    sealed class PaneHeader : Control
    {
        readonly Theme theme;
        readonly float scale;
        readonly Font font = new Font("Segoe UI", 8.25f), closeFont = new Font(Icons.Font.FontFamily, 7f);
        public readonly IconButton CloseButton;
        public int Number;
        string title = "";
        bool current, reading;

        // DevTools Lite is reading this pane's page: says so in the header.
        public bool Reading
        {
            get { return reading; }
            set { if (reading != value) { reading = value; Invalidate(); } }
        }

        public PaneHeader(Theme theme, float scale)
        {
            this.theme = theme;
            this.scale = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            CloseButton = new IconButton(theme, Icons.Close);
            CloseButton.Round = true;
            CloseButton.Surface = theme.TabBar;
            CloseButton.SymbolFont = closeFont;
            Controls.Add(CloseButton);
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        public string Title
        {
            get { return title; }
            set { value = value ?? ""; if (title != value) { title = value; Invalidate(); } }
        }

        public bool Current
        {
            get { return current; }
            set
            {
                if (current == value) return;
                current = value;
                CloseButton.Surface = value ? theme.Page : theme.TabBar;
                CloseButton.Invalidate();
                Invalidate();
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (CloseButton == null) return; // layout fires while the constructor is still adding children
            int s = Math.Max(0, Height - S(6));
            CloseButton.SetBounds(Width - s - S(4), (Height - s) / 2, s, s);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(current ? theme.Page : theme.TabBar);
            using (Pen p = new Pen(theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            int x = S(10);
            if (current)
                using (SolidBrush b = new SolidBrush(theme.Target))
                {
                    g.FillRectangle(b, 0, 0, Width, Math.Max(2, S(3)));
                    int d = S(9);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(b, x, (Height - d) / 2 + S(1), d, d); // the red dot
                    g.SmoothingMode = SmoothingMode.None;
                    x += d + S(6);
                }
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            string number = Number.ToString();
            using (Font bold = new Font(font, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, number, bold, new Rectangle(x, 0, Width, Height), current ? theme.Target : theme.Muted, flags);
                x += TextRenderer.MeasureText(g, number, bold, Size.Empty, flags).Width + S(8);
            }
            int right = (CloseButton.Visible ? CloseButton.Left : Width) - S(6);
            if (reading)
                using (Font bold = new Font(font, FontStyle.Bold))
                {
                    const string label = "DevTools Lite reads this page";
                    int w = TextRenderer.MeasureText(g, label, bold, Size.Empty, flags).Width;
                    if (right - w - S(12) > x + S(60)) // only when there's still room for the title
                    {
                        TextRenderer.DrawText(g, label, bold, new Rectangle(right - w, 0, w, Height), theme.Target, flags);
                        right -= w + S(12);
                    }
                }
            if (right > x)
                TextRenderer.DrawText(g, title, font, new Rectangle(x, 0, right - x, Height), current ? theme.Fore : theme.Muted,
                    flags | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { font.Dispose(); closeFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    sealed class PaneChoice
    {
        public string Text, Tip;
        public Action Pick;
    }

    // What an empty split-screen pane shows: how to fill it, and buttons for a new tab and the open tabs that aren't
    // on screen.
    sealed class EmptyPane : Control
    {
        readonly Theme theme;
        readonly float scale;
        readonly Font headingFont = new Font("Segoe UI Semilight", 14f), textFont = new Font("Segoe UI", 9f);
        readonly List<Button> buttons = new List<Button>();
        readonly ToolTip tips = new ToolTip();
        List<PaneChoice> choices = new List<PaneChoice>();
        string signature;
        Rectangle headingBounds, hintBounds;

        public EmptyPane(Theme theme, float scale)
        {
            this.theme = theme;
            this.scale = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = theme.Page;
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        string Hint
        {
            get
            {
                return choices.Count > 1 ? "Type an address in the bar above, or show one of your open tabs here."
                                         : "Type an address in the bar above, or open a new tab here.";
            }
        }

        public void SetChoices(List<PaneChoice> list)
        {
            choices = list;
            string sig = "";
            foreach (PaneChoice c in list) sig += c.Text + "\n" + c.Tip + "\n";
            if (sig == signature) return; // same buttons; their actions are looked up when clicked
            signature = sig;
            foreach (Button old in buttons) { tips.SetToolTip(old, null); old.Dispose(); }
            buttons.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                int index = i;
                Button b = new Button();
                b.FlatStyle = FlatStyle.Flat;
                b.TextAlign = ContentAlignment.MiddleLeft;
                b.AutoEllipsis = true;
                b.UseMnemonic = false;
                b.Font = textFont;
                b.ForeColor = theme.Fore;
                b.BackColor = theme.Field;
                b.Cursor = Cursors.Hand;
                b.Padding = new Padding(S(6), 0, S(6), 0);
                b.FlatAppearance.BorderColor = theme.FieldBorder;
                b.FlatAppearance.MouseOverBackColor = theme.Hover;
                b.FlatAppearance.MouseDownBackColor = theme.Press;
                b.Text = list[i].Text;
                b.AccessibleName = list[i].Text;
                if (!string.IsNullOrEmpty(list[i].Tip)) tips.SetToolTip(b, list[i].Tip);
                // Queued: picking rebuilds these buttons, which mustn't happen inside the button's own click.
                b.Click += delegate
                {
                    if (index < choices.Count) BeginInvoke(choices[index].Pick);
                };
                buttons.Add(b);
                Controls.Add(b);
            }
            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int w = Math.Max(0, Math.Min(S(320), Width - S(32))), x = (Width - w) / 2;
            int bh = S(32), gap = S(6), headingH = S(34), hintH = S(40);
            int fit = Math.Max(0, (Height - headingH - hintH - S(48)) / (bh + gap));
            int shown = Math.Min(buttons.Count, fit);
            int total = headingH + hintH + shown * (bh + gap);
            int y = Math.Max(S(16), (Height - total) * 2 / 5);
            headingBounds = new Rectangle(x, y, w, headingH);
            hintBounds = new Rectangle(x, y + headingH, w, hintH);
            y += headingH + hintH;
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].Visible = i < shown;
                buttons[i].SetBounds(x, y, w, bh);
                y += bh + gap;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(theme.Page);
            TextRenderer.DrawText(g, "Empty pane", headingFont, headingBounds, theme.Fore,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Hint, textFont, hintBounds, theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { headingFont.Dispose(); textFont.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
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
        public Action<Graphics, Rectangle, Color> Glyph; // drawn instead of the icon-font symbol when set
        public Font SymbolFont;                          // instead of Icons.Font, for smaller buttons

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

            if (Glyph != null) { Glyph(g, ClientRectangle, fg); return; }
            TextRenderer.DrawText(g, symbol, SymbolFont ?? Icons.Font, ClientRectangle, fg,
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
        public readonly IconButton KeyButton; // saved passwords: shown only on sites that have some, to fill them
        bool showAppButton = true, showKeyButton;

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

            KeyButton = new IconButton(theme, Icons.Key);
            KeyButton.Surface = theme.Field;
            KeyButton.Round = true;
            KeyButton.Cursor = Cursors.Default;
            KeyButton.Active = true; // drawn in the accent colour whenever it's there
            KeyButton.Visible = false;
            Controls.Add(KeyButton);
        }

        public bool ShowKeyButton
        {
            get { return showKeyButton; }
            set
            {
                if (showKeyButton == value) return;
                showKeyButton = value;
                KeyButton.Visible = value;
                PerformLayout();
            }
        }

        public bool ShowAppButton
        {
            get { return showAppButton; }
            set { showAppButton = value; AppButton.Visible = value; PerformLayout(); }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (AppButton == null || KeyButton == null) return; // layout fires while the constructor is still adding children
            int pad = Math.Max(6, Height / 2 - 2);
            int s = Height - 6, x = Width - 3;
            if (showAppButton)
            {
                AppButton.SetBounds(x - s, 3, s, s);
                x -= s;
            }
            if (showKeyButton)
            {
                KeyButton.SetBounds(x - s, 3, s, s);
                x -= s;
            }
            int right = showAppButton || showKeyButton ? x - 2 : Width - pad;
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
