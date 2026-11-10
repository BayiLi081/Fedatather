using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Fedatather
{
    sealed class FoldedPage
    {
        public string Title, Url;
    }

    // Only titles and URLs are kept. Folded pages have no live WebView2 controller.
    sealed class FoldedTabs
    {
        public const int MaxOpenTabs = 5;
        readonly string path;
        readonly List<FoldedPage> pages = new List<FoldedPage>();
        public IList<FoldedPage> Pages { get { return pages.AsReadOnly(); } }

        public FoldedTabs(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            // Report damaged files instead of silently overwriting a user's saved links.
            List<object> entries = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as List<object>;
            if (entries == null) throw new FormatException("Invalid folded tabs file: " + path);
            foreach (object entry in entries)
            {
                Dictionary<string, object> row = entry as Dictionary<string, object>;
                string url = Json.Str(row, "url");
                if (url.Length == 0) throw new FormatException("Missing folded tab URL: " + path);
                pages.Add(new FoldedPage { Url = url, Title = Json.Str(row, "title") });
            }
        }

        public void Add(FoldedPage page)
        {
            pages.Add(page);
            try { Save(); }
            catch { pages.RemoveAt(pages.Count - 1); throw; }
        }

        public void Remove(FoldedPage page)
        {
            int index = pages.IndexOf(page);
            if (index < 0) return;
            pages.RemoveAt(index);
            try { Save(); }
            catch { pages.Insert(index, page); throw; }
        }

        void Save()
        {
            StringBuilder json = new StringBuilder("[");
            foreach (FoldedPage page in pages)
            {
                if (json.Length > 1) json.Append(',');
                json.Append("{\"title\":").Append(Json.Quote(page.Title))
                    .Append(",\"url\":").Append(Json.Quote(page.Url)).Append('}');
            }
            json.Append(']');
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, json.ToString(), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
