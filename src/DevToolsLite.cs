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
        bool uiReady, autoScrolling;
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
                if (IsDisposed) { c.Close(); return; }
                controller = c;
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
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

        async Task<int[]> ScanOnce()
        {
            int id = scanId, newRows = 0, newImages = 0;
            pageTitle = page.DocumentTitle ?? "";
            Send("{\"type\":\"scan\"}");

            // Tables and repeated lists, read from the page's DOM.
            try
            {
                string json = await page.ExecuteScriptAsync(TableScript);
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
                    c.KeyHeader = kind == "list" ? PickKey(header, rows) : null;
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
                if (!header[i].EndsWith(" link")) continue;
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
                        Dictionary<string, object> s = Json.Parse(await page.ExecuteScriptAsync(ScrollScript)) as Dictionary<string, object>;
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

        const string ScrollScript = @"(function () {
  var se = document.scrollingElement || document.documentElement, t = se;
  if (se.scrollHeight - se.clientHeight < 50) {
    var best = null, area = 0, all = document.body ? document.body.getElementsByTagName('*') : [];
    for (var i = 0; i < all.length; i++) {
      var el = all[i];
      if (el.scrollHeight - el.clientHeight > 50 && el.clientHeight > 100) {
        var oy = getComputedStyle(el).overflowY;
        if (oy === 'auto' || oy === 'scroll' || oy === 'overlay') {
          var a = el.clientWidth * el.clientHeight;
          if (a > area) { area = a; best = el; }
        }
      }
    }
    if (best) t = best;
  }
  var before = t.scrollTop;
  t.scrollTo({ top: before + Math.round(t.clientHeight * 0.85), behavior: 'instant' });
  return { moved: Math.round(t.scrollTop - before), atEnd: t.scrollTop + t.clientHeight >= t.scrollHeight - 4 };
})()";

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

        // 1. Real <table>s and 2. ARIA grids (skipping layout tables: < 2 rows / 2 columns, or wrapping other tables).
        // 3. Repeated lists: post feeds, product grids, search results... A container whose children are many
        //    look-alike items (same tag + class) becomes a table: one row per item, one column per repeated field
        //    (text, link, image), headed by the field's class name.
        const string TableScript = @"(function () {
  function clean(s) { return (s || '').replace(/\s+/g, ' ').trim(); }
  var out = [];
  function add(caption, rows, kind, sig) {
    var cols = 0, filled = 0;
    rows.forEach(function (r) { cols = Math.max(cols, r.length); r.forEach(function (c) { if (c) filled++; }); });
    if (rows.length < 2 || cols < 2 || filled < 4) return false;
    out.push({ caption: caption, rows: rows, kind: kind || 'table', sig: sig || '' });
    return true;
  }
  var tables = document.querySelectorAll('table');
  for (var i = 0; i < tables.length && out.length < 100; i++) {
    var t = tables[i];
    if (t.querySelector('table')) continue;
    var rows = [];
    for (var r = 0; r < t.rows.length && r < 5000; r++) {
      var row = [], cells = t.rows[r].cells;
      for (var c = 0; c < cells.length; c++) {
        row.push(clean(cells[c].innerText));
        for (var k = 1; k < Math.min(cells[c].colSpan || 1, 50); k++) row.push('');
      }
      rows.push(row);
    }
    add(t.caption ? clean(t.caption.innerText) : clean(t.getAttribute('aria-label') || t.getAttribute('title')), rows);
  }
  var grids = document.querySelectorAll('[role=table], [role=grid], [role=treegrid]');
  for (var g = 0; g < grids.length && out.length < 100; g++) {
    var el = grids[g];
    if (el.tagName === 'TABLE') continue;
    var rws = el.querySelectorAll('[role=row]'), data = [];
    for (var j = 0; j < rws.length && j < 5000; j++) {
      var cs = rws[j].querySelectorAll('[role=cell], [role=gridcell], [role=columnheader], [role=rowheader]'), rr = [];
      for (var q = 0; q < cs.length; q++) rr.push(clean(cs[q].innerText));
      data.push(rr);
    }
    add(clean(el.getAttribute('aria-label')), data);
  }

  // ---- 3. repeated lists
  var SKIP = /^(script|style|svg|template|noscript|select|option|head|table|thead|tbody|tfoot|tr)$/i;
  // class names generated by CSS-in-JS / CSS modules make poor column names
  var HASHY = /^(css|sc|jsx|svelte|emotion)-|[_-](?=[A-Za-z]*\d)[A-Za-z0-9]{5,}$|^(?=[a-z]*\d)[a-z0-9]{6,}$|___[A-Za-z0-9_-]{4,}$/i;
  function named(el) {
    for (var i = 0; i < el.classList.length; i++) if (!HASHY.test(el.classList[i])) return el.classList[i];
    return '';
  }
  function pretty(s) { return clean((s || '').replace(/[-_]+/g, ' ')); }
  function shown(el) { return el.getClientRects().length > 0; }

  var cands = [], all = document.body ? document.body.getElementsByTagName('*') : [];
  for (var a = 0; a < all.length; a++) {
    var p = all[a], kids = p.children;
    if (kids.length < 3 || SKIP.test(p.tagName)) continue;
    // already covered by tables, or site chrome (menus, headers, footers) rather than content
    if (p.closest('table, [role=table], [role=grid], [role=treegrid], nav, header, footer, aside, ' +
                  '[role=navigation], [role=banner], [role=contentinfo], [role=menu], [role=menubar], [role=tablist]')) continue;
    var groups = {}, best = null;
    for (var b = 0; b < kids.length; b++) {
      var ch = kids[b];
      if (SKIP.test(ch.tagName)) continue;
      var sg = ch.tagName + '.' + (ch.classList[0] || '');
      (groups[sg] = groups[sg] || []).push(ch);
      if (!best || groups[sg].length > groups[best].length) best = sg;
    }
    var items = best ? groups[best].filter(shown) : [];
    if (items.length < 3 || items.length > 3000) continue;
    if (/^(P|BR|HR|H[1-6]|BLOCKQUOTE)$/.test(items[0].tagName)) continue;   // runs of prose, not records
    var text = 0, links = 0, imgs = 0;
    for (var d = 0; d < items.length; d++) {
      var it = items[d];
      text += Math.min((it.textContent || '').trim().length, 300);
      if (it.tagName === 'A' || it.querySelector('a[href]')) links++;
      if (it.tagName === 'IMG' || it.querySelector('img')) imgs++;
    }
    var n = items.length, avg = text / n;
    if (avg < 4 && imgs < n / 2) continue;
    cands.push({ el: p, items: items, score: n * (Math.min(avg, 120) / 4 + 10 * links / n + 10 * imgs / n) });
  }
  cands.sort(function (x, y) { return y.score - x.score; });

  function listRows(items) {
    var order = [], cols = {}, data = [];
    items.forEach(function (item) {
      var rec = {};
      function put(key, v, label) {
        v = clean(v);
        if (!v || v.length > 2000 || rec[key] !== undefined) return;
        rec[key] = v;
        if (!cols[key]) { cols[key] = { n: 0, label: label }; order.push(key); }
        cols[key].n++;
      }
      function visit(el, key, label) {
        var t = el.tagName;
        if (t === 'IMG') { put(key + '@img', el.currentSrc || el.src, pretty(label || 'image') + ' image'); return; }
        if (t === 'A' && el.href && !/^javascript:/i.test(el.href)) put(key + '@link', el.href, pretty(label || 'link') + ' link');
        if (el.children.length === 0) { put(key, el.innerText || el.textContent, pretty(label || t.toLowerCase())); return; }
        var own = '';
        for (var i = 0; i < el.childNodes.length; i++) if (el.childNodes[i].nodeType === 3) own += el.childNodes[i].nodeValue;
        put(key + '@text', own, pretty(label || t.toLowerCase()));
        var seen = {};
        for (var j = 0; j < el.children.length; j++) {
          var c = el.children[j];
          if (SKIP.test(c.tagName)) continue;
          var seg = c.tagName + '.' + (c.classList[0] || '');
          seen[seg] = (seen[seg] || 0) + 1;
          visit(c, key + '>' + seg + '#' + seen[seg], named(c) || label);
        }
      }
      visit(item, '', named(item));
      data.push(rec);
    });

    var n = data.length, min = Math.max(2, Math.ceil(n * 0.25)), kept = [], same = {};
    order.forEach(function (k) {
      if (cols[k].n < min) return;
      var vals = data.map(function (r) { return r[k] || ''; }), distinct = {}, count = 0;
      vals.forEach(function (v) { if (!distinct[v]) { distinct[v] = 1; count++; } });
      if (count < 2) return;                       // identical everywhere: icons, fixed labels
      var whole = vals.join('|~|');
      if (same[whole]) return;                     // duplicate of an earlier column (same link twice)
      same[whole] = 1;
      kept.push(k);
    });
    if (kept.length < 2) return null;
    // readable text first (title, name, count...), then links, then images
    function rank(k) { return /@link$/.test(k) ? 1 : /@img$/.test(k) ? 2 : 0; }
    kept = kept.map(function (k, i) { return { k: k, i: i }; })
      .sort(function (x, y) { return rank(x.k) - rank(y.k) || x.i - y.i; })
      .map(function (o) { return o.k; })
      .slice(0, 20);
    var used = {}, header = kept.map(function (k) {
      var h = cols[k].label || 'field';
      used[h] = (used[h] || 0) + 1;
      return used[h] > 1 ? h + ' ' + used[h] : h;
    });
    return [header].concat(data.map(function (r) { return kept.map(function (k) { return r[k] || ''; }); }));
  }

  var chosen = [];
  for (var e = 0; e < cands.length && chosen.length < 3; e++) {
    var cd = cands[e];
    if (chosen.some(function (o) { return o.contains(cd.el) || cd.el.contains(o); })) continue;
    var rows = listRows(cd.items.slice(0, 1000));
    if (!rows) continue;
    var label = clean(cd.el.getAttribute('aria-label'));
    var prev = cd.el.previousElementSibling;
    if (!label && prev && /^H[1-6]$/.test(prev.tagName)) label = clean(prev.textContent);
    var first = cd.items[0], what = pretty(named(first) || first.tagName.toLowerCase());
    // identifies the same list across scans, so rows found later are appended to it
    var sig = 'list:' + cd.el.tagName + '.' + (cd.el.classList[0] || '') + '#' + (cd.el.id || '') +
              '>' + first.tagName + '.' + (first.classList[0] || '');
    if (add((label ? label + ' - ' : '') + what + ' list', rows, 'list', sig)) chosen.push(cd.el);
  }
  return out;
})()";

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

        const string PanelHtml = @"<!doctype html>
<html><head><meta charset='utf-8'><meta name='color-scheme' content='light dark'><title>DevTools Lite</title>
<style>
:root { --bg:#ffffff; --panel:#f3f3f3; --line:#dedede; --fg:#1b1b1b; --muted:#666666; --accent:#0067c0; --accent-fg:#ffffff; --card:#fafafa; --sel:rgba(0,103,192,.10); }
@media (prefers-color-scheme: dark) { :root { --bg:#1b1b1b; --panel:#202020; --line:#333333; --fg:#e6e6e6; --muted:#9c9c9c; --accent:#4cc2ff; --accent-fg:#0b0b0b; --card:#262626; --sel:rgba(76,194,255,.14); } }
* { box-sizing:border-box; }
html, body { margin:0; height:100%; background:var(--bg); color:var(--fg); font:13px 'Segoe UI', system-ui, sans-serif; user-select:none; }
body { display:flex; flex-direction:column; }
header, .bar, footer { background:var(--panel); display:flex; align-items:center; gap:8px; padding:8px 10px; }
header { border-bottom:1px solid var(--line); }
.bar { color:var(--muted); border-bottom:1px solid var(--line); padding:6px 10px; min-height:34px; }
footer { border-top:1px solid var(--line); flex-wrap:wrap; }
.tab { border:0; background:transparent; color:var(--muted); font:inherit; font-weight:600; padding:6px 10px; border-radius:6px; cursor:pointer; }
.tab.on { color:var(--fg); background:var(--bg); }
.tab .n { font-weight:400; margin-left:6px; color:var(--muted); }
.grow { flex:1; }
button.ghost { border:1px solid var(--line); background:var(--bg); color:var(--fg); border-radius:6px; padding:5px 10px; font:inherit; cursor:pointer; white-space:nowrap; }
button.ghost:hover { border-color:var(--muted); }
button.ghost.active { background:var(--accent); color:var(--accent-fg); border-color:var(--accent); }
header { flex-wrap:wrap; }
button.primary { border:0; background:var(--accent); color:var(--accent-fg); border-radius:6px; padding:8px 16px; font:inherit; font-weight:600; cursor:pointer; white-space:nowrap; }
button:disabled { opacity:.45; cursor:default; }
label { display:flex; align-items:center; gap:6px; cursor:pointer; white-space:nowrap; }
input[type=checkbox] { accent-color:var(--accent); width:15px; height:15px; margin:0; }
#status { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; min-width:0; }
main { flex:1; overflow:auto; padding:10px; }
.grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(118px, 1fr)); gap:8px; }
.card { position:relative; background:var(--card); border:1px solid var(--line); border-radius:8px; overflow:hidden; cursor:pointer; }
.card.sel { border-color:var(--accent); background:var(--sel); }
.card input { position:absolute; top:6px; left:6px; z-index:1; }
.thumb { height:104px; display:flex; align-items:center; justify-content:center; padding:6px;
  background-image:conic-gradient(rgba(128,128,128,.14) 25%, transparent 0 50%, rgba(128,128,128,.14) 0 75%, transparent 0); background-size:14px 14px; }
.thumb img { max-width:100%; max-height:100%; object-fit:contain; }
.meta { padding:5px 7px; font-size:11px; color:var(--muted); white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.tcard { background:var(--card); border:1px solid var(--line); border-radius:8px; margin-bottom:8px; overflow:hidden; }
.tcard.sel { border-color:var(--accent); }
.tcard .head { display:flex; align-items:center; gap:8px; padding:8px 10px; cursor:pointer; }
.tcard.sel .head { background:var(--sel); }
.tcard .title { flex:1; font-weight:600; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.tcard .dims { color:var(--muted); font-size:12px; white-space:nowrap; }
.tcard .badge { font-size:11px; font-weight:600; color:var(--muted); border:1px solid var(--line); border-radius:4px; padding:0 5px; }
.preview { overflow:auto; max-height:150px; border-top:1px solid var(--line); }
.preview table { border-collapse:collapse; font-size:11px; }
.preview td { border:1px solid var(--line); padding:3px 6px; white-space:nowrap; max-width:160px; overflow:hidden; text-overflow:ellipsis; }
.preview tr:first-child td { font-weight:600; }
.folder { display:flex; align-items:center; gap:6px; flex:1 1 100%; min-width:0; }
.folder .lbl { color:var(--muted); white-space:nowrap; }
.folder .path { flex:1; min-width:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
#empty { color:var(--muted); text-align:center; padding:48px 16px; }
.hidden { display:none !important; }
</style></head>
<body>
<header>
  <button class='tab on' data-tab='images'>Images<span class='n' id='nImages'>0</span></button>
  <button class='tab' data-tab='tables'>Tables<span class='n' id='nTables'>0</span></button>
  <span class='grow'></span>
  <button class='ghost' id='refresh' title='Scan again and add anything new to what is collected (F5)'>Rescan</button>
  <button class='ghost' id='auto' title='Scroll the page down step by step and keep collecting'>Auto-scroll</button>
  <button class='ghost' id='clear' title='Forget everything collected and start over'>Clear</button>
</header>
<div class='bar'>
  <label><input type='checkbox' id='all' checked> All</label>
  <label id='smallOpt' title='Hide icons, spacers and tracking pixels'><input type='checkbox' id='hideSmall' checked> Hide small (&lt; 64 px)</label>
  <span class='grow'></span>
  <span id='status'></span>
</div>
<main>
  <div id='images' class='grid'></div>
  <div id='tables' class='hidden'></div>
  <div id='empty' class='hidden'></div>
</main>
<footer>
  <div class='folder'><span class='lbl'>Save to</span><span class='path' id='path'></span></div>
  <button class='ghost' id='change' title='Choose the folder; each collection gets its own subfolder in it'>Change&hellip;</button>
  <button class='ghost' id='open' title='Open this collection&#39;s folder'>Open</button>
  <span class='grow'></span>
  <button class='primary' id='save'>Save</button>
</footer>
<script>
(function () {
  var tab = 'images', folders = { images: '', tables: '' }, images = [], tables = [], scanning = true, saving = false, autoOn = false;
  var session = ''; // this collection's subfolder: '<site> <date time>'
  // keep the end of a long path visible: ...\Fedatather\xiaohongshu.com 2026-11-10 12-45
  function shortPath(p) { var parts = p.split('\\'); return parts.length > 3 ? '…\\' + parts.slice(-2).join('\\') : p; }
  function $(id) { return document.getElementById(id); }
  function el(tag, cls) { var e = document.createElement(tag); if (cls) e.className = cls; return e; }
  function post(o) { window.chrome.webview.postMessage(o); }
  function bytes(n) { return n < 1024 ? n + ' B' : n < 1048576 ? Math.round(n / 1024) + ' KB' : (n / 1048576).toFixed(1) + ' MB'; }
  function type(mime) {
    var m = /^image\/([a-z0-9.+-]+)/i.exec(mime || ''), k = m ? m[1].toLowerCase() : 'file';
    return k === 'svg+xml' ? 'svg' : k === 'jpeg' ? 'jpg' : (k === 'x-icon' || k === 'vnd.microsoft.icon') ? 'ico' : k;
  }
  function shown(it) { return !(it.kind === 'images' && $('hideSmall').checked && it.small); }
  function list() { return tab === 'images' ? images : tables; }
  function select(it, on) { it.sel = on; it.cb.checked = on; it.card.classList.toggle('sel', on); }
  function clickable(it, target) {
    target.addEventListener('click', function (e) { select(it, e.target === it.cb ? it.cb.checked : !it.sel); update(); });
  }

  function addImage(m) {
    var it = { kind: 'images', id: m.id, sel: true, small: false };
    var card = el('div', 'card sel'), cb = el('input'), th = el('div', 'thumb'), img = el('img'), meta = el('div', 'meta');
    var info = type(m.mime) + ' · ' + bytes(m.size);
    cb.type = 'checkbox'; cb.checked = true;
    card.title = m.name + '\n' + m.url;
    meta.textContent = info;
    img.onload = function () {
      var w = img.naturalWidth, h = img.naturalHeight;
      if (w && h) { meta.textContent = w + '×' + h + ' · ' + info; it.small = w < 64 || h < 64; update(); }
    };
    img.src = 'img/' + encodeURIComponent(m.id);
    th.appendChild(img); card.appendChild(cb); card.appendChild(th); card.appendChild(meta);
    it.card = card; it.cb = cb; clickable(it, card);
    images.push(it); $('images').appendChild(card);
  }

  // Tables keep growing between scans; ticks are remembered by table id until the page is cleared.
  var tableSel = {};
  function setTables(arr) {
    tables.forEach(function (it) { tableSel[it.id] = it.sel; });
    tables = []; $('tables').textContent = '';
    var listSeen = false;
    (arr || []).forEach(function (t, i) {
      var rows = t.preview || [], cols = t.cols || 0, total = t.total || 0;
      // default: tick every real table but only the best list (lists arrive best first)
      var isList = t.kind === 'list', on = tableSel.hasOwnProperty(t.id) ? tableSel[t.id] : (!isList || !listSeen);
      if (isList) listSeen = true;
      var it = { kind: 'tables', id: t.id, sel: on };
      var card = el('div', on ? 'tcard sel' : 'tcard'), head = el('div', 'head'), cb = el('input'), title = el('div', 'title'), dims = el('div', 'dims');
      cb.type = 'checkbox'; cb.checked = on;
      var badge = el('span', 'badge');
      badge.textContent = isList ? 'List' : 'Table';
      title.textContent = t.caption || ('Table ' + (i + 1));
      dims.textContent = isList ? total + ' items × ' + cols + ' fields' : total + ' rows × ' + cols + ' cols';
      head.appendChild(cb); head.appendChild(badge); head.appendChild(title); head.appendChild(dims);
      var pv = el('div', 'preview'), tb = el('table');
      rows.slice(0, 6).forEach(function (r) {
        var tr = el('tr');
        r.slice(0, 8).forEach(function (c) { var td = el('td'); td.textContent = c; td.title = c; tr.appendChild(td); });
        tb.appendChild(tr);
      });
      pv.appendChild(tb); card.appendChild(head); card.appendChild(pv);
      it.card = card; it.cb = cb; clickable(it, head);
      tables.push(it); $('tables').appendChild(card);
    });
  }

  function update() {
    var vis = 0, sel = 0;
    images.forEach(function (it) { it.card.classList.toggle('hidden', !shown(it)); });
    list().forEach(function (it) { if (shown(it)) { vis++; if (it.sel) sel++; } });
    $('nImages').textContent = images.filter(shown).length;
    $('nTables').textContent = tables.length;
    var tabs = document.querySelectorAll('.tab');
    for (var i = 0; i < tabs.length; i++) tabs[i].classList.toggle('on', tabs[i].getAttribute('data-tab') === tab);
    $('images').classList.toggle('hidden', tab !== 'images');
    $('tables').classList.toggle('hidden', tab !== 'tables');
    $('smallOpt').classList.toggle('hidden', tab !== 'images');
    $('all').checked = vis > 0 && sel === vis;
    $('all').disabled = vis === 0;
    var noun = tab === 'images' ? (sel === 1 ? 'image' : 'images') : (sel === 1 ? 'table' : 'tables');
    $('save').textContent = 'Save ' + sel + ' ' + noun;
    $('save').disabled = sel === 0 || saving;
    $('empty').textContent = scanning ? 'Scanning the page…'
      : tab === 'images' ? 'No images found. Scroll the page to load more, then Rescan.' : 'No tables or repeated lists found on this page.';
    $('empty').classList.toggle('hidden', vis > 0);
    var full = folders[tab] ? folders[tab] + (session ? '\\' + session : '') : '';
    $('path').textContent = shortPath(full);
    $('path').title = full;
  }

  var tabBtns = document.querySelectorAll('.tab');
  for (var i = 0; i < tabBtns.length; i++) {
    tabBtns[i].onclick = function () { tab = this.getAttribute('data-tab'); $('status').textContent = ''; update(); };
  }
  $('hideSmall').onchange = update;
  $('all').onchange = function () { var on = this.checked; list().forEach(function (it) { if (shown(it)) select(it, on); }); update(); };
  $('refresh').onclick = function () { post({ cmd: 'refresh' }); };
  $('auto').onclick = function () { post({ cmd: 'auto' }); };
  $('clear').onclick = function () { post({ cmd: 'clear' }); };
  $('change').onclick = function () { post({ cmd: 'pick', kind: tab }); };
  $('open').onclick = function () { post({ cmd: 'open', kind: tab }); };
  $('save').onclick = function () {
    var ids = list().filter(function (it) { return shown(it) && it.sel; }).map(function (it) { return String(it.id); });
    if (!ids.length) return;
    saving = true; $('status').textContent = 'Saving…'; update();
    post({ cmd: 'save', kind: tab, ids: ids });
  };
  document.addEventListener('contextmenu', function (e) { e.preventDefault(); });

  window.chrome.webview.addEventListener('message', function (e) {
    var m = e.data;
    switch (m.type) {
      case 'init': folders = m.folders; break;
      case 'reset': images = []; tables = []; tableSel = {}; $('images').textContent = ''; $('tables').textContent = ''; scanning = true; break;
      case 'scan': scanning = true; if (!autoOn) $('status').textContent = 'Scanning…'; break;
      case 'auto': autoOn = m.on; $('auto').textContent = m.on ? 'Stop' : 'Auto-scroll'; $('auto').classList.toggle('active', m.on); break;
      case 'status': $('status').textContent = m.message; $('status').title = m.message; break;
      case 'tables': setTables(m.tables); break;
      case 'image': addImage(m); break;
      case 'progress': if (!autoOn) $('status').textContent = 'Scanning… ' + m.done + ' / ' + m.total; break;
      case 'done': scanning = false; if (!autoOn) { $('status').textContent = m.message || ''; $('status').title = m.message || ''; } break;
      case 'folder': folders[m.kind] = m.path; break;
      case 'session': session = m.name; break;
      case 'saved': saving = false; $('status').textContent = m.message; $('status').title = m.message; break;
    }
    update();
  });

  update();
  post({ cmd: 'ready' });
})();
</script>
</body></html>";
    }
}
