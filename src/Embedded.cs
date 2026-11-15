using System;
using System.IO;
using System.Text;

namespace Fedatather
{
    // The page scripts and HTML in src\scripts\, built into the exe as UTF-8 (build.ps1 embeds each file as
    // "Fedatather.Script.<file name>"). As C# string literals they would take twice the space, stored as UTF-16.
    static class Embedded
    {
        public static string Text(string file)
        {
            using (Stream s = typeof(Embedded).Assembly.GetManifestResourceStream("Fedatather.Script." + file))
            {
                if (s == null) throw new InvalidOperationException("Missing built-in script " + file + ": rebuild with build.ps1.");
                using (StreamReader r = new StreamReader(s, new UTF8Encoding(false))) return r.ReadToEnd();
            }
        }
    }
}
