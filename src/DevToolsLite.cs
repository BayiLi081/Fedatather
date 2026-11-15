using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Fedatather
{
    // A deliberately tiny "devtools": lists the images the page has received and the data tables in it,
    // lets you tick what you want, and saves them to a remembered folder with one click.
    // Images come straight from the page's own resources (DevTools protocol), so nothing is re-downloaded.
    // The panel UI is a local HTML page in a second web view, so every format the browser can show
    // (webp, avif, svg, ...) gets a thumbnail.
    sealed class DevToolsLite : Form
    {
        const string Host = "https://devtools.lite/";
        const int MaxImages = 2000;   // per page (scans add up)
        const int MaxRows = 20000;    // per collected table / list

        sealed class ImageAsset { public string Url, Mime, Name; public byte[] Data; }

        // A table or list gathered over one or more scans; later scans append new rows to it.
        sealed class Collected
        {
            public string Sig, Caption, Kind, KeyHeader;
            public string SavedPath; // CSV written for this collection; saving again updates it
            public readonly List<string> Headers = new List<string>();
            public readonly List<string[]> Rows = new List<string[]>();
            public readonly Dictionary<string, int> RowIndex = new Dictionary<string, int>();
        }

        readonly CoreWebView2Environment env;
        readonly CoreWebView2 page;
        readonly Theme theme;
        readonly float scale;
        readonly AppSettings prefs;
        readonly Dictionary<string, ImageAsset> images = new Dictionary<string, ImageAsset>();
        readonly HashSet<string> seenImageUrls = new HashSet<string>();
        readonly List<Collected> collected = new List<Collected>();
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1); // one scan at a time
        readonly HashSet<string> savedImages = new HashSet<string>(); // "folder|id" already written this collection
        string pageTitle = "";
        string session = ""; // subfolder for this collection: "<site> <yyyy-MM-dd HH-mm>"

        CoreWebView2Controller controller;
        CoreWebView2 ui;
        bool uiReady, autoScrolling, closed;
        int scanId, imageSeq;

        public DevToolsLite(Form owner, CoreWebView2Environment env, CoreWebView2 page, Theme theme, float scale)
        {
            this.env = env;
            this.page = page;
            this.theme = theme;
            this.scale = scale;
            prefs = AppSettings.Load(Path.Combine(Program.DataDir, "devtools.ini"));

            Text = "DevTools Lite";
            ShowInTaskbar = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = theme.Page;
            Icon = owner.Icon;
            MinimumSize = new Size(S(300), S(320));
            Bounds = PlaceBeside(owner.Bounds);
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        // Split screen: when it follows a click into another pane, it appears without taking the focus from that page.
        public bool OpenInBackground;
        protected override bool ShowWithoutActivation { get { return OpenInBackground; } }

        // Beside a phone-size window if there is room, otherwise floating at the right of the screen.
        Rectangle PlaceBeside(Rectangle o)
        {
            Rectangle wa = Screen.FromRectangle(o).WorkingArea;
            int w = Math.Min(S(440), wa.Width), h = Math.Min(S(640), wa.Height), gap = S(8);
            int x, y;
            if (o.Width < wa.Width * 3 / 4)
            {
                if (o.Left - gap - w >= wa.Left) x = o.Left - gap - w;
                else if (o.Right + gap + w <= wa.Right) x = o.Right + gap;
                else x = wa.Right - w;
                y = Math.Max(wa.Top, Math.Min(o.Top, wa.Bottom - h));
            }
            else
            {
                x = wa.Right - w - S(16);
                y = wa.Top + S(64);
            }
            return new Rectangle(x, y, w, h);
        }

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
            scanId++; // stops a running scan
            autoScrolling = false;
            if (controller != null) { controller.Close(); controller = null; ui = null; }
            images.Clear();
            collected.Clear();
            base.OnFormClosed(e);
        }

        // Called by the browser after it navigates to another page: start collecting from scratch.
        public async void PageChanged()
        {
            autoScrolling = false;
            await Task.Delay(800); // let the new page fetch its first images
            if (!IsDisposed) Rescan(true);
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
                // Closed while the view was still being created (closing can complete this await before the window
                // counts as disposed): nothing to report.
                if (IsDisposed || closed) return;
                MessageBox.Show(this, "Could not open DevTools Lite:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            controller.Bounds = ClientRectangle;
            ui = controller.CoreWebView2;
            ui.Settings.AreDefaultContextMenusEnabled = false;
            ui.Settings.AreDevToolsEnabled = false;
            ui.Settings.IsStatusBarEnabled = false;
            ui.Settings.IsZoomControlEnabled = false;
            ui.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
            ui.WebResourceRequested += OnUiResource;
            ui.WebMessageReceived += OnUiMessage;
            controller.AcceleratorKeyPressed += delegate (object s, CoreWebView2AcceleratorKeyPressedEventArgs e)
            {
                if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown) return;
                Keys k = (Keys)(int)e.VirtualKey;
                if (k == Keys.Escape || k == Keys.F12) { e.Handled = true; BeginInvoke(new Action(Close)); }
                else if (k == Keys.F5) { e.Handled = true; Rescan(false); }
            };
            ui.Navigate(Host);
        }

        // ---------------------------------------------------------------- local "server" for the panel

        void OnUiResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string path = e.Request.Uri.Length > Host.Length ? e.Request.Uri.Substring(Host.Length) : "";
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path.Substring(0, q);

            ImageAsset a;
            if (path.Length == 0)
                e.Response = Respond(Encoding.UTF8.GetBytes(PanelHtml), "text/html; charset=utf-8");
            else if (path.StartsWith("img/") && images.TryGetValue(Uri.UnescapeDataString(path.Substring(4)), out a))
                e.Response = Respond(a.Data, a.Mime);
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
            if (ui == null) return;
            try { ui.PostWebMessageAsJson(json); } catch { }
        }

        void OnUiMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = Json.Parse(e.WebMessageAsJson) as Dictionary<string, object>; }
            catch (FormatException) { return; }
            if (m == null) return;

            string kind = Json.Str(m, "kind") == "tables" ? "tables" : "images";
            switch (Json.Str(m, "cmd"))
            {
                case "ready":
                    uiReady = true;
                    Send("{\"type\":\"init\",\"folders\":{\"images\":" + Json.Quote(Folder("images")) +
                         ",\"tables\":" + Json.Quote(Folder("tables")) + "}}");
                    Rescan(true);
                    break;
                case "refresh":
                    Rescan(false);
                    break;
                case "clear":
                    autoScrolling = false;
                    Rescan(true);
                    break;
                case "auto":
                    ToggleAutoScroll();
                    break;
                case "pick":
                    PickFolder(kind);
                    break;
                case "open":
                    OpenFolder(kind);
                    break;
                case "save":
                    List<string> ids = new List<string>();
                    object raw;
                    if (m.TryGetValue("ids", out raw) && raw is List<object>)
                        foreach (object o in (List<object>)raw) ids.Add(Convert.ToString(o));
                    Save(kind, ids);
                    break;
            }
        }

        // ---------------------------------------------------------------- scanning the page
        //
        // Scans add up. A table or list found again is merged into what was already collected (new rows
        // appended, known rows refreshed) and only images not seen before are added. That keeps the items
        // masonry / infinite-scroll feeds drop from the page once they scroll out of view.
        // "Clear", or navigating to another page, starts from scratch.

        async void Rescan(bool reset)
        {
            try { await ScanAsync(reset); } catch { }
        }

        // Returns { new rows, new images }, or null if the scan was abandoned.
        async Task<int[]> ScanAsync(bool reset)
        {
            if (!uiReady || IsDisposed) return null;
            if (reset)
            {
                scanId++; // abandons a scan in progress
                images.Clear();
                seenImageUrls.Clear();
                collected.Clear();
                Send("{\"type\":\"reset\"}");
                StartSession();
            }
            await gate.WaitAsync();
            try { return IsDisposed ? null : await ScanOnce(); }
            finally { gate.Release(); }
        }

        // ExecuteScriptAsync can fail to ever complete (for example when a result can't be serialized). Give up
        // after a while instead of leaving the scan lock held and every later scan waiting.
        async Task<string> RunScript(string script)
        {
            Task<string> run = page.ExecuteScriptAsync(script);
            if (await Task.WhenAny(run, Task.Delay(20000)) != run) throw new TimeoutException("The page script didn't finish.");
            return await run;
        }

        async Task<int[]> ScanOnce()
        {
            int id = scanId, newRows = 0, newImages = 0;
            pageTitle = page.DocumentTitle ?? "";
            Send("{\"type\":\"scan\"}");

            // Tables and repeated lists, read from the page's DOM.
            try
            {
                string opened = await RunScript(ExpandScript);
                if (id != scanId || IsDisposed) return null;
                if (opened != "0" && opened != "null") await Task.Delay(500); // let expanded text render
                string json = await RunScript(TableScript);
                if (id != scanId || IsDisposed) return null;
                List<object> found = Json.Parse(json) as List<object>;
                if (found != null) newRows = MergeTables(found);
            }
            catch { if (id != scanId || IsDisposed) return null; }
            SendTables();

            // Images: anything of type Image in the page's resource tree (img tags, CSS backgrounds, iframes)
            // that earlier scans haven't picked up.
            List<string[]> fresh = new List<string[]>();
            try
            {
                await page.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                string tree = await page.CallDevToolsProtocolMethodAsync("Page.getResourceTree", "{}");
                if (id != scanId || IsDisposed) return null;
                Dictionary<string, object> root = Json.Parse(tree) as Dictionary<string, object>;
                object frameTree;
                if (root != null && root.TryGetValue("frameTree", out frameTree))
                    CollectImages(frameTree, fresh, seenImageUrls);
            }
            catch { if (id != scanId || IsDisposed) return null; }

            int done = 0;
            foreach (string[] r in fresh)
            {
                if (images.Count >= MaxImages) break;
                try
                {
                    string res = await page.CallDevToolsProtocolMethodAsync("Page.getResourceContent",
                        "{\"frameId\":" + Json.Quote(r[0]) + ",\"url\":" + Json.Quote(r[1]) + "}");
                    if (id != scanId || IsDisposed) return null;
                    Dictionary<string, object> d = Json.Parse(res) as Dictionary<string, object>;
                    string content = Json.Str(d, "content");
                    object b64;
                    bool isBase64 = d != null && d.TryGetValue("base64Encoded", out b64) && b64 is bool && (bool)b64;
                    byte[] data = isBase64 ? Convert.FromBase64String(content) : Encoding.UTF8.GetBytes(content);
                    if (data.Length > 0)
                    {
                        ImageAsset a = new ImageAsset();
                        a.Url = r[1];
                        a.Mime = r[2].Length > 0 ? r[2] : "application/octet-stream";
                        a.Data = data;
                        a.Name = ImageFileName(r[1], a.Mime);
                        string key = (++imageSeq).ToString();
                        images[key] = a;
                        newImages++;
                        Send("{\"type\":\"image\",\"id\":" + Json.Quote(key) + ",\"name\":" + Json.Quote(a.Name) +
                             ",\"url\":" + Json.Quote(a.Url) + ",\"mime\":" + Json.Quote(a.Mime) + ",\"size\":" + data.Length + "}");
                    }
                }
                catch { if (id != scanId || IsDisposed) return null; }
                done++;
                if (done % 10 == 0) Send("{\"type\":\"progress\",\"done\":" + done + ",\"total\":" + fresh.Count + "}");
            }

            string summary = newRows == 0 && newImages == 0 ? "Nothing new."
                : "+" + newRows + (newRows == 1 ? " row, +" : " rows, +") + newImages + (newImages == 1 ? " image" : " images");
            Send("{\"type\":\"done\",\"message\":" + Json.Quote(summary) + "}");
            return new[] { newRows, newImages };
        }

        // Merges one scan's tables/lists into the collection; returns how many rows were new.
        int MergeTables(List<object> found)
        {
            int added = 0;
            foreach (object o in found)
            {
                Dictionary<string, object> t = o as Dictionary<string, object>;
                object raw;
                if (t == null || !t.TryGetValue("rows", out raw) || !(raw is List<object>)) continue;
                List<object> rows = (List<object>)raw;
                if (rows.Count < 2) continue;

                string kind = Json.Str(t, "kind") == "list" ? "list" : "table";
                string caption = Json.Str(t, "caption");
                List<string> header = Cells(rows[0]);
                string sig = Json.Str(t, "sig");
                if (sig.Length == 0) sig = kind + ":" + caption + "\t" + string.Join("\t", header.ToArray());

                Collected c = collected.Find(x => x.Sig == sig);
                if (c == null)
                {
                    c = new Collected();
                    c.Sig = sig;
                    c.Kind = kind;
                    // The page's own item id when it has one (data-review-id...), else a unique link column.
                    string key = Json.Str(t, "key");
                    c.KeyHeader = kind != "list" ? null : key.Length > 0 && header.Contains(key) ? key : PickKey(header, rows);
                    collected.Add(c);
                }
                c.Caption = caption;

                // Lists line columns up by name (fields can appear later); tables by position.
                int[] map = new int[header.Count];
                for (int i = 0; i < header.Count; i++)
                {
                    int at = kind == "list" ? c.Headers.IndexOf(header[i]) : (i < c.Headers.Count ? i : -1);
                    if (at < 0) { c.Headers.Add(header[i]); at = c.Headers.Count - 1; }
                    map[i] = at;
                }
                int keyCol = c.KeyHeader != null ? header.IndexOf(c.KeyHeader) : -1;

                for (int r = 1; r < rows.Count; r++)
                {
                    List<string> cells = Cells(rows[r]);
                    string[] row = new string[c.Headers.Count];
                    for (int i = 0; i < cells.Count && i < map.Length; i++) row[map[i]] = cells[i];
                    // A list item is identified by its own link when it has one, otherwise by its whole content.
                    string key = keyCol >= 0 && keyCol < cells.Count && cells[keyCol].Length > 0
                        ? "k\t" + cells[keyCol]
                        : "r\t" + string.Join("\t", cells.ToArray());
                    int existing;
                    if (c.RowIndex.TryGetValue(key, out existing))
                        c.Rows[existing] = Overlay(c.Rows[existing], row); // refresh, e.g. like counts
                    else if (c.Rows.Count < MaxRows)
                    {
                        c.RowIndex[key] = c.Rows.Count;
                        c.Rows.Add(row);
                        added++;
                    }
                }
            }
            return added;
        }

        static List<string> Cells(object row)
        {
            List<string> cells = new List<string>();
            List<object> list = row as List<object>;
            if (list != null) foreach (object v in list) cells.Add(Convert.ToString(v) ?? "");
            return cells;
        }

        static string[] Overlay(string[] old, string[] fresh)
        {
            string[] r = new string[Math.Max(old.Length, fresh.Length)];
            for (int i = 0; i < r.Length; i++)
            {
                string f = i < fresh.Length ? fresh[i] : null;
                r[i] = !string.IsNullOrEmpty(f) ? f : (i < old.Length ? old[i] : null);
            }
            return r;
        }

        // The link column that is (nearly) unique per item, e.g. a post's own URL.
        static string PickKey(List<string> header, List<object> rows)
        {
            int n = rows.Count - 1, bestDistinct = 0;
            string best = null;
            for (int i = 0; i < header.Count; i++)
            {
                if (!Regex.IsMatch(header[i], @"(^|\s)link( \d+)?$")) continue;
                HashSet<string> distinct = new HashSet<string>();
                int filled = 0;
                for (int r = 1; r < rows.Count; r++)
                {
                    List<string> cells = Cells(rows[r]);
                    if (i < cells.Count && cells[i].Length > 0) { filled++; distinct.Add(cells[i]); }
                }
                if (filled >= n * 0.9 && distinct.Count >= n * 0.9 && distinct.Count > bestDistinct)
                {
                    best = header[i];
                    bestDistinct = distinct.Count;
                }
            }
            return best;
        }

        // The panel only needs a preview: header + 5 rows (a list's newest rows, so you can watch it grow).
        void SendTables()
        {
            StringBuilder sb = new StringBuilder("{\"type\":\"tables\",\"tables\":[");
            for (int i = 0; i < collected.Count; i++)
            {
                Collected c = collected[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(i)
                  .Append(",\"kind\":").Append(Json.Quote(c.Kind))
                  .Append(",\"caption\":").Append(Json.Quote(c.Caption))
                  .Append(",\"total\":").Append(c.Rows.Count)
                  .Append(",\"cols\":").Append(c.Headers.Count)
                  .Append(",\"preview\":[");
                AppendPreviewRow(sb, c.Headers.ToArray());
                int count = Math.Min(5, c.Rows.Count), start = c.Kind == "list" ? c.Rows.Count - count : 0;
                for (int r = start; r < start + count; r++) { sb.Append(','); AppendPreviewRow(sb, c.Rows[r]); }
                sb.Append("]}");
            }
            sb.Append("]}");
            Send(sb.ToString());
        }

        static void AppendPreviewRow(StringBuilder sb, string[] row)
        {
            sb.Append('[');
            for (int i = 0; i < row.Length && i < 8; i++)
            {
                if (i > 0) sb.Append(',');
                string v = row[i] ?? "";
                sb.Append(Json.Quote(v.Length > 200 ? v.Substring(0, 200) : v));
            }
            sb.Append(']');
        }

        // ---------------------------------------------------------------- auto-scroll

        // Scrolls the page (or its main scrolling panel) down about one screen at a time, collecting as it goes.
        // Stops when you press Stop, the page navigates, or the page stops producing anything new.
        async void ToggleAutoScroll()
        {
            if (autoScrolling) { autoScrolling = false; return; }
            autoScrolling = true;
            Send("{\"type\":\"auto\",\"on\":true}");
            int rows = 0, imgs = 0, idle = 0, stuck = 0, round = 0;
            try
            {
                while (autoScrolling && !IsDisposed && round < 500)
                {
                    round++;
                    bool atEnd = false, moved = true;
                    try
                    {
                        Dictionary<string, object> s = Json.Parse(await RunScript(ScrollScript)) as Dictionary<string, object>;
                        object v;
                        if (s != null && s.TryGetValue("atEnd", out v) && v is bool) atEnd = (bool)v;
                        if (s != null && s.TryGetValue("moved", out v) && v is double) moved = (double)v > 0;
                    }
                    catch { }

                    await Task.Delay(1200); // let the page load the next batch
                    if (!autoScrolling || IsDisposed) break;
                    int[] got = await ScanAsync(false);
                    if (got == null) break; // page changed or panel closed

                    rows += got[0];
                    imgs += got[1];
                    if (got[0] + got[1] > 0) { idle = 0; stuck = 0; }
                    else { idle++; if (atEnd || !moved) stuck++; }
                    if (stuck >= 3 || idle >= 15) break; // reached the end, or nothing loads any more

                    Send("{\"type\":\"status\",\"message\":" +
                         Json.Quote("Auto-scrolling… +" + rows + " rows, +" + imgs + " images so far") + "}");
                }
            }
            finally
            {
                autoScrolling = false;
                Send("{\"type\":\"auto\",\"on\":false}");
                Send("{\"type\":\"status\",\"message\":" +
                     Json.Quote("Auto-scroll finished: +" + rows + " rows, +" + imgs + " images.") + "}");
            }
        }

        static readonly string ScrollScript = Embedded.Text("devtools-scroll.js");

        static void CollectImages(object node, List<string[]> found, HashSet<string> seen)
        {
            Dictionary<string, object> n = node as Dictionary<string, object>;
            if (n == null) return;
            object v;
            string frameId = n.TryGetValue("frame", out v) ? Json.Str(v as Dictionary<string, object>, "id") : "";
            if (n.TryGetValue("resources", out v) && v is List<object>)
            {
                foreach (object o in (List<object>)v)
                {
                    Dictionary<string, object> r = o as Dictionary<string, object>;
                    if (r == null || Json.Str(r, "type") != "Image") continue;
                    string url = Json.Str(r, "url");
                    if (url.Length == 0 || url.StartsWith("data:") || !seen.Add(url)) continue;
                    found.Add(new[] { frameId, url, Json.Str(r, "mimeType") });
                }
            }
            if (n.TryGetValue("childFrames", out v) && v is List<object>)
                foreach (object child in (List<object>)v) CollectImages(child, found, seen);
        }

        // 1. Real <table>s and 2. ARIA grids (skipping layout tables: < 2 rows / 2 filled columns, or wrapping
        //    other tables).
        // 3. Repeated lists: post feeds, reviews, product grids, search results... Look-alike siblings (same tag +
        //    class) become a table: one row per item, one column per repeated field (text, link, image, rating),
        //    named after the field's class name, or after what it holds when the class name is machine-made.
        static readonly string TableScript = Embedded.Text("devtools-table.js");

        // Reviews and posts often cut long text short behind a "More" button. Before reading the page, expand
        // those: only buttons that declare themselves collapsed in-place disclosures (aria-expanded=false, no
        // popup) labelled More / See more / Read more / 展开... Links are never clicked, so this can't navigate.
        static readonly string ExpandScript = Embedded.Text("devtools-expand.js");

        // ---------------------------------------------------------------- saving
        //
        // Each collection saves into its own subfolder of the chosen folder, named after the website and the
        // time collecting started, so data gathered at different times never mixes:
        //   Pictures\Fedatather\xiaohongshu.com 2026-11-10 12-45\...

        void StartSession()
        {
            string site = "page";
            try
            {
                Uri u = new Uri(page.Source);
                if (u.IsFile) site = "local file";
                else if (u.Host.Length > 0) site = Regex.Replace(u.Host, @"^www\.", "", RegexOptions.IgnoreCase);
            }
            catch (UriFormatException) { }
            session = SafeName(site + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture), "collection");
            savedImages.Clear();
            Send("{\"type\":\"session\",\"name\":" + Json.Quote(session) + "}");
        }

        string SessionFolder(string kind)
        {
            return session.Length > 0 ? Path.Combine(Folder(kind), session) : Folder(kind);
        }

        string Folder(string kind)
        {
            string fallback = kind == "tables"
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fedatather Tables")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Fedatather");
            return prefs.Get(kind, fallback);
        }

        void PickFolder(string kind)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Save " + kind + " under this folder (each collection gets its own subfolder):";
                dlg.ShowNewFolderButton = true;
                string current = Folder(kind);
                if (Directory.Exists(current)) dlg.SelectedPath = current;
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                prefs.Set(kind, dlg.SelectedPath);
                prefs.Save();
                Send("{\"type\":\"folder\",\"kind\":" + Json.Quote(kind) + ",\"path\":" + Json.Quote(dlg.SelectedPath) + "}");
            }
        }

        // Opens this collection's subfolder once something is saved there, otherwise the chosen folder.
        void OpenFolder(string kind)
        {
            string dir = Directory.Exists(SessionFolder(kind)) ? SessionFolder(kind) : Folder(kind);
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { SendSaved("Could not open the folder: " + ex.Message); }
        }

        void Save(string kind, List<string> ids)
        {
            string dir = SessionFolder(kind);
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { SendSaved("Could not use the folder: " + ex.Message); return; }

            int ok = 0, already = 0, failed = 0;
            foreach (string id in ids)
            {
                try
                {
                    if (kind == "images")
                    {
                        ImageAsset a;
                        if (!images.TryGetValue(id, out a)) { failed++; continue; }
                        // saving again in the same collection only writes the images that are new
                        if (!savedImages.Add(dir + "|" + id)) { already++; continue; }
                        File.WriteAllBytes(UniquePath(dir, a.Name), a.Data);
                    }
                    else
                    {
                        int index;
                        if (!int.TryParse(id, out index) || index < 0 || index >= collected.Count) { failed++; continue; }
                        Collected c = collected[index];
                        // a table only grows during a collection, so saving it again updates the same file
                        if (c.SavedPath == null || !string.Equals(Path.GetDirectoryName(c.SavedPath), dir, StringComparison.OrdinalIgnoreCase))
                        {
                            string caption = c.Caption ?? "";
                            string name = SafeName((pageTitle.Length > 0 ? pageTitle + " - " : "") +
                                                   (caption.Length > 0 ? caption : "Table " + (index + 1)), "table");
                            c.SavedPath = UniquePath(dir, name + ".csv");
                        }
                        // UTF-8 with BOM so Excel opens non-English text correctly.
                        File.WriteAllText(c.SavedPath, ToCsv(c.Headers, c.Rows), new UTF8Encoding(true));
                    }
                    ok++;
                }
                catch { failed++; }
            }

            string noun = kind == "images" ? (ok == 1 ? "image" : "images") : (ok == 1 ? "table" : "tables");
            string msg = ok > 0 || already == 0
                ? "Saved " + ok + " " + noun + " to \"" + session + "\""
                : "Nothing new to save";
            if (already > 0) msg += " (" + already + " already saved)";
            if (failed > 0) msg += " (" + failed + " failed)";
            SendSaved(msg + ".");
        }

        void SendSaved(string message)
        {
            Send("{\"type\":\"saved\",\"message\":" + Json.Quote(message) + "}");
        }

        static readonly Regex PlainNumber = new Regex(@"^[+-]?\d[\d.,\s%]*$");

        static string ToCsv(List<string> headers, List<string[]> rows)
        {
            StringBuilder sb = new StringBuilder();
            AppendCsvRow(sb, headers.ToArray(), headers.Count);
            foreach (string[] r in rows) AppendCsvRow(sb, r, headers.Count);
            return sb.ToString();
        }

        static void AppendCsvRow(StringBuilder sb, string[] cells, int width)
        {
            for (int i = 0; i < width; i++)
            {
                if (i > 0) sb.Append(',');
                string v = (i < cells.Length ? cells[i] : null) ?? "";
                // Don't let page text become a spreadsheet formula when the CSV is opened.
                if (v.Length > 0 && "=+-@".IndexOf(v[0]) >= 0 && !PlainNumber.IsMatch(v)) v = "'" + v;
                if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
                sb.Append(v);
            }
            sb.Append("\r\n");
        }

        static string ImageFileName(string url, string mime)
        {
            string name = "";
            try
            {
                Uri u = new Uri(url);
                string[] segs = u.Segments;
                if (segs.Length > 0) name = Uri.UnescapeDataString(segs[segs.Length - 1]).Trim('/');
            }
            catch { }
            name = SafeName(name, "");
            string ext = ExtFromMime(mime) ?? Path.GetExtension(name);
            string baseName = name.Length > 0 ? Path.GetFileNameWithoutExtension(name) : "";
            if (baseName.Length == 0) baseName = "image";
            if (baseName.Length > 80) baseName = baseName.Substring(0, 80);
            return baseName + (string.IsNullOrEmpty(ext) ? ".img" : ext);
        }

        static string ExtFromMime(string mime)
        {
            switch ((mime ?? "").ToLowerInvariant().Split(';')[0].Trim())
            {
                case "image/jpeg": case "image/jpg": case "image/pjpeg": return ".jpg";
                case "image/png": case "image/apng": return ".png";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                case "image/avif": return ".avif";
                case "image/svg+xml": return ".svg";
                case "image/bmp": return ".bmp";
                case "image/x-icon": case "image/vnd.microsoft.icon": return ".ico";
                case "image/tiff": return ".tif";
                default: return null;
            }
        }

        static string SafeName(string s, string fallback)
        {
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in s ?? "") sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string r = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.');
            if (r.Length > 100) r = r.Substring(0, 100).Trim();
            return r.Length == 0 ? fallback : r;
        }

        static string UniquePath(string dir, string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
            string path = Path.Combine(dir, fileName);
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, name + " (" + n + ")" + ext);
            return path;
        }

        // ---------------------------------------------------------------- panel UI (served as https://devtools.lite/)

        static readonly string PanelHtml = Embedded.Text("devtools.html");
    }
}
