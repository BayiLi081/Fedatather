using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Fedatather
{
    // What the command line asked for. Web-app shortcuts launch:
    //   Fedatather.exe --app=<id> "--name=<name>" "--url=<url>" [--size=phone|full] [--pin]
    sealed class LaunchOptions
    {
        public string Url, AppId, AppName;
        public string Size; // "phone" | "full" = fixed size; null = switchable
        public bool Pin;

        public bool IsApp { get { return AppId != null; } }

        public static LaunchOptions Parse(string[] args)
        {
            LaunchOptions o = new LaunchOptions();
            foreach (string a in args)
            {
                if (a.StartsWith("--app=")) o.AppId = WebApps.CleanId(a.Substring(6));
                else if (a.StartsWith("--url=")) o.Url = a.Substring(6);
                else if (a.StartsWith("--name=")) o.AppName = a.Substring(7);
                else if (a == "--size=phone") o.Size = "phone";
                else if (a == "--size=full") o.Size = "full";
                else if (a == "--pin") o.Pin = true;
                else if (!a.StartsWith("--") && o.Url == null) o.Url = a;
            }
            return o;
        }
    }

    sealed class WebAppSpec
    {
        public string Name, Url, Size;
        public bool Pin, Desktop, StartMenu;
        public Bitmap Icon;
    }

    sealed class SavedApp
    {
        public string Id, Arguments;
        public List<string> Places = new List<string>();
    }

    static class WebApps
    {
        public static string AppsDir { get { return Path.Combine(Program.DataDir, "apps"); } }
        public static string IconPath(string id) { return Path.Combine(AppsDir, id + ".ico"); }
        public static string SettingsPath(string id) { return Path.Combine(AppsDir, id + ".ini"); }
        public static string AppUserModelId(string id) { return "Fedatather.App." + id; }

        public static string CleanId(string s)
        {
            s = Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9-]", "");
            if (s.Length > 60) s = s.Substring(0, 60);
            return s.Length == 0 ? null : s;
        }

        static string MakeId(string name, string url)
        {
            string slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 40) slug = slug.Substring(0, 40).Trim('-');
            if (slug.Length == 0) slug = "app";
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(url)) { h ^= b; h *= 16777619; }
            return slug + "-" + h.ToString("x8").Substring(0, 6);
        }

        // "Inbox (3) - me@x.com - Gmail" -> "Gmail";  "rednote - Your lifestyle guide" -> "rednote"
        public static string SuggestName(string title, string url)
        {
            string best = null;
            title = Regex.Replace(title ?? "", @"^\(\d+\)\s*", "");
            foreach (string part in Regex.Split(title, @"\s+[-|–—·]\s+"))
            {
                string p = part.Trim();
                if (p.Length > 0 && (best == null || p.Length < best.Length)) best = p;
            }
            if (string.IsNullOrEmpty(best))
            {
                try { best = Regex.Replace(new Uri(url).Host, @"^www\.", ""); }
                catch (UriFormatException) { best = ""; }
            }
            if (best.Length == 0) best = "Web App";
            return best.Length > 40 ? best.Substring(0, 40).Trim() : best;
        }

        public static SavedApp Save(WebAppSpec spec)
        {
            SavedApp saved = new SavedApp();
            saved.Id = MakeId(spec.Name, spec.Url);
            Directory.CreateDirectory(AppsDir);

            string icon = null;
            if (spec.Icon != null)
            {
                try { icon = IconPath(saved.Id); WriteIco(spec.Icon, icon); }
                catch { icon = null; }
            }

            saved.Arguments = "--app=" + saved.Id +
                " \"--name=" + spec.Name.Replace("\"", "") + "\"" +
                " \"--url=" + spec.Url.Replace("\"", "%22") + "\"" +
                (spec.Size != null ? " --size=" + spec.Size : "") +
                (spec.Pin ? " --pin" : "");

            string file = SafeFileName(spec.Name) + ".lnk";
            string description = spec.Name + " (Fedatather web app)";
            if (spec.Desktop)
            {
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), file),
                    saved.Arguments, icon, description, saved.Id);
                saved.Places.Add("Desktop");
            }
            if (spec.StartMenu)
            {
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), file),
                    saved.Arguments, icon, description, saved.Id);
                saved.Places.Add("Start menu");
            }
            return saved;
        }

        static string SafeFileName(string name)
        {
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in name) sb.Append(Array.IndexOf(bad, c) >= 0 ? ' ' : c);
            string s = sb.ToString().Trim().TrimEnd('.');
            if (s.Length > 80) s = s.Substring(0, 80).Trim();
            return s.Length == 0 ? "Web App" : s;
        }

        // ------------------------------------------------------------ icons

        // Largest raster icon the page declares (apple-touch-icon beats a 16px favicon), else /favicon.ico.
        const string IconScript = @"(function () {
  var best = null, bestSize = -1;
  var links = document.querySelectorAll('link[rel~=icon], link[rel~=apple-touch-icon], link[rel~=apple-touch-icon-precomposed]');
  for (var i = 0; i < links.length; i++) {
    var l = links[i];
    if (!l.href || /\.svg(\?|#|$)/i.test(l.href) || l.type === 'image/svg+xml') continue;
    var m = /(\d+)x(\d+)/i.exec(l.getAttribute('sizes') || '');
    var s = m ? parseInt(m[1], 10) : (/apple-touch/i.test(l.rel) ? 180 : 32);
    if (s > bestSize) { best = l.href; bestSize = s; }
  }
  return best || (location.origin + '/favicon.ico');
})()";

        public static async Task<Bitmap> FetchIconAsync(CoreWebView2 web, string userAgent)
        {
            Bitmap found = null;
            try
            {
                string json = await web.ExecuteScriptAsync(IconScript);
                string href = json != null && json.Length > 1 && json[0] == '"'
                    ? Regex.Unescape(json.Substring(1, json.Length - 2)) : null;
                if (href != null && href.StartsWith("data:image/") && href.Contains(";base64,"))
                {
                    found = Decode(Convert.FromBase64String(href.Substring(href.IndexOf(";base64,") + 8)));
                }
                else if (href != null && (href.StartsWith("https://") || href.StartsWith("http://")))
                {
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = userAgent;
                        Task<byte[]> download = wc.DownloadDataTaskAsync(new Uri(href));
                        if (await Task.WhenAny(download, Task.Delay(6000)) == download) found = Decode(download.Result);
                        else wc.CancelAsync();
                    }
                }
            }
            catch { }
            if (found != null && found.Width >= 64) return found;

            // Fall back to (or compare with) the favicon WebView2 already has.
            try
            {
                using (Stream s = await web.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
                {
                    if (s != null)
                    {
                        MemoryStream ms = new MemoryStream();
                        s.CopyTo(ms);
                        Bitmap fav = Decode(ms.ToArray());
                        if (fav != null && (found == null || fav.Width > found.Width))
                        {
                            if (found != null) found.Dispose();
                            found = fav;
                        }
                    }
                }
            }
            catch { }
            return found;
        }

        public static Bitmap Decode(byte[] data)
        {
            if (data == null || data.Length < 8) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                {
                    Bitmap b;
                    if (data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
                    {
                        using (Icon ico = new Icon(ms, 256, 256)) b = ico.ToBitmap();
                    }
                    else
                    {
                        using (Image img = Image.FromStream(ms)) b = new Bitmap(img);
                    }
                    if (b.Width >= 16 && b.Height >= 16) return b;
                    b.Dispose();
                }
            }
            catch { }
            return null;
        }

        static void WriteIco(Image src, string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 256 };
            byte[][] frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++) frames[i] = RenderPng(src, sizes[i]);

            using (FileStream fs = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] % 256); // 0 means 256
                    w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32);
                    w.Write(frames[i].Length); w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] f in frames) w.Write(f);
            }
        }

        static byte[] RenderPng(Image src, int n)
        {
            using (Bitmap bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    float k = Math.Min((float)n / src.Width, (float)n / src.Height);
                    float w = src.Width * k, h = src.Height * k;
                    g.DrawImage(src, (n - w) / 2, (n - h) / 2, w, h);
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        // ------------------------------------------------------------ shortcuts (IShellLink)

        static void CreateShortcut(string lnkPath, string args, string iconPath, string description, string id)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                string exe = Application.ExecutablePath;
                link.SetPath(exe);
                link.SetArguments(args);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe));
                link.SetDescription(description);
                link.SetIconLocation(iconPath ?? exe, 0);

                // Same AppUserModelID as the running app window, so the taskbar groups / pins it as its own app.
                IPropertyStore store = (IPropertyStore)link;
                PropertyKey key = new PropertyKey();
                key.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
                key.pid = 5;
                PropVariant v = new PropVariant();
                v.vt = 31; // VT_LPWSTR
                v.pointer = Marshal.StringToCoTaskMemUni(AppUserModelId(id));
                try { store.SetValue(ref key, ref v); store.Commit(); }
                finally { Marshal.FreeCoTaskMem(v.pointer); }

                ((IPersistFile)link).Save(lnkPath, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relPath, int reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, IntPtr value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PropertyKey { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pointer;
        }
    }

    sealed class SaveAppDialog : Form
    {
        readonly Theme theme;
        readonly float scale;
        readonly TextBox nameBox, urlBox;
        readonly ComboBox sizeBox;
        readonly CheckBox pinBox, desktopBox, startBox;
        readonly PictureBox iconBox;
        readonly Button saveBtn, cancelBtn;
        readonly Task<Bitmap> iconTask;
        Bitmap chosenIcon;
        bool saving;

        public WebAppSpec Result;

        static readonly string[] SizeLabels = { "Phone size (fixed)", "Full screen (fixed)", "Switchable (remembers last)" };
        static readonly string[] SizeValues = { "phone", "full", null };

        public SaveAppDialog(Theme theme, float scale, string name, string url, bool pinned, Task<Bitmap> iconTask)
        {
            this.theme = theme;
            this.scale = scale;
            this.iconTask = iconTask;

            Text = "Save as web app";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f);
            BackColor = theme.Bar;
            ForeColor = theme.Fore;
            ClientSize = new Size(S(360), S(330));

            ToolTip tips = new ToolTip();
            iconBox = new PictureBox();
            iconBox.SetBounds(S(16), S(18), S(48), S(48));
            iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            iconBox.Cursor = Cursors.Hand;
            iconBox.Paint += PaintIconPlaceholder;
            iconBox.Click += delegate { PickIconFile(); };
            tips.SetToolTip(iconBox, "Click to choose a different icon");
            Controls.Add(iconBox);

            AddLabel("Name", S(76), S(14));
            nameBox = AddTextBox(name, S(76), S(34), S(268));

            AddLabel("Address", S(16), S(80));
            urlBox = AddTextBox(url, S(16), S(100), S(328));

            AddLabel("Window size", S(16), S(136));
            sizeBox = new ComboBox();
            sizeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            sizeBox.FlatStyle = FlatStyle.Flat;
            sizeBox.BackColor = theme.Field;
            sizeBox.ForeColor = theme.Fore;
            sizeBox.Items.AddRange(SizeLabels);
            sizeBox.SelectedIndex = 0;
            sizeBox.SetBounds(S(16), S(156), S(328), S(26));
            Controls.Add(sizeBox);

            pinBox = AddCheck("Start pinned on top", pinned, S(16), S(194));
            AddLabel("Create shortcut on", S(16), S(226));
            desktopBox = AddCheck("Desktop", true, S(16), S(246));
            startBox = AddCheck("Start menu", true, S(130), S(246));

            saveBtn = AddButton("Save", true, S(180), S(286));
            cancelBtn = AddButton("Cancel", false, S(264), S(286));
            saveBtn.Click += OnSave;
            cancelBtn.DialogResult = DialogResult.Cancel;
            AcceptButton = saveBtn;
            CancelButton = cancelBtn;

            ShowFetchedIcon();
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetDarkTitleBar(Handle, theme.Dark);
        }

        void AddLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = theme.Fore;
            l.Location = new Point(x, y);
            Controls.Add(l);
        }

        TextBox AddTextBox(string text, int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.Text = text;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = theme.Field;
            t.ForeColor = theme.Fore;
            t.SetBounds(x, y, w, S(24));
            Controls.Add(t);
            return t;
        }

        CheckBox AddCheck(string text, bool on, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = on;
            c.AutoSize = true;
            c.ForeColor = theme.Fore;
            c.FlatStyle = FlatStyle.Flat;
            c.FlatAppearance.BorderColor = theme.FieldBorder;
            c.FlatAppearance.CheckedBackColor = theme.Field;
            c.BackColor = theme.Bar;
            c.Location = new Point(x, y);
            Controls.Add(c);
            return c;
        }

        Button AddButton(string text, bool primary, int x, int y)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = primary ? theme.Accent : theme.Hover;
            b.ForeColor = primary ? theme.AccentFore : theme.Fore;
            b.SetBounds(x, y, S(80), S(30));
            Controls.Add(b);
            return b;
        }

        void PaintIconPlaceholder(object sender, PaintEventArgs e)
        {
            if (iconBox.Image != null) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Gfx.RoundRect(new RectangleF(0, 0, iconBox.Width - 1, iconBox.Height - 1), S(10)))
            using (SolidBrush b = new SolidBrush(theme.Hover))
                e.Graphics.FillPath(b, p);
            TextRenderer.DrawText(e.Graphics, Icons.InstallApp, Icons.Font, iconBox.ClientRectangle, theme.ForeDisabled,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        async void ShowFetchedIcon()
        {
            Bitmap b = null;
            try { b = await iconTask; } catch { }
            if (IsDisposed || b == null || chosenIcon != null) return;
            iconBox.Image = b;
        }

        void PickIconFile()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose an icon";
                dlg.Filter = "Images (*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Bitmap b = null;
                try { b = WebApps.Decode(File.ReadAllBytes(dlg.FileName)); } catch { }
                if (b == null)
                {
                    MessageBox.Show(this, "That file could not be read as an image.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                chosenIcon = b;
                iconBox.Image = b;
            }
        }

        async void OnSave(object sender, EventArgs e)
        {
            if (saving) return;
            string name = nameBox.Text.Trim();
            string url = BrowserForm.ToUrl(urlBox.Text);
            if (name.Length == 0) { Warn("Give the app a name.", nameBox); return; }
            if (url == null || !Regex.IsMatch(url, "^(https?|file):", RegexOptions.IgnoreCase))
            {
                Warn("Enter a web address (http or https).", urlBox);
                return;
            }
            if (!desktopBox.Checked && !startBox.Checked)
            {
                Warn("Pick at least one place for the shortcut.", desktopBox);
                return;
            }

            // The site icon may still be downloading; it is bounded by a short timeout.
            saving = true;
            saveBtn.Enabled = false;
            saveBtn.Text = "Saving…";
            Bitmap icon = chosenIcon;
            if (icon == null) { try { icon = await iconTask; } catch { } }
            if (IsDisposed) return;

            Result = new WebAppSpec();
            Result.Name = name;
            Result.Url = url;
            Result.Size = SizeValues[sizeBox.SelectedIndex];
            Result.Pin = pinBox.Checked;
            Result.Desktop = desktopBox.Checked;
            Result.StartMenu = startBox.Checked;
            Result.Icon = icon;
            DialogResult = DialogResult.OK;
        }

        void Warn(string message, Control focus)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            focus.Focus();
        }
    }
}
