using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Fedatather
{
    sealed class SavedLogin
    {
        public string Name = "", Url = "", Username = "", Password = "", Note = "";

        public string Site
        {
            get
            {
                Uri u;
                return Uri.TryCreate(Url, UriKind.Absolute, out u) && u.Host.Length > 0 ? u.Host : Url;
            }
        }
    }

    enum UnlockResult { Unlocked, WrongPin, Erased }

    // Saved passwords, in one file (%LOCALAPPDATA%\Fedatather\passwords.dat), encrypted twice:
    //  1. with a key derived from the 6-digit PIN (PBKDF2-SHA256, then AES-256-CBC and HMAC-SHA256). The PIN itself is
    //     never stored, so it can't be recovered, and nobody can read the passwords without it;
    //  2. with Windows DPAPI for the current user, so a copy of the file is useless on another computer or Windows
    //     account, where a 6-digit PIN alone could simply be guessed.
    // 10 wrong PINs in a row erase the file. The sites' addresses (not usernames or passwords) sit outside the PIN
    // layer, so the address bar can show the key on sites with saved logins before you unlock.
    sealed class PasswordVault
    {
        public const int MaxAttempts = 10;
        public static int Iterations = 150000; // about half a second per PIN try; tests lower it
        public static readonly TimeSpan AutoLock = TimeSpan.FromMinutes(5);
        const string Format = "fedatather-passwords";

        sealed class Envelope
        {
            public int Failures, Iterations;
            public string Vault; // base64 of the DPAPI-protected inner part
            public byte[] Salt, Iv, Data, Mac;
            public List<string> Sites = new List<string>();
        }

        readonly string path;
        byte[] encKey, macKey, salt; // while unlocked
        int iterations;
        List<SavedLogin> logins = new List<SavedLogin>();
        DateTime lastUse, loadedStamp;
        List<string> sitesCache;
        DateTime sitesStamp;

        public event EventHandler Locked, Erased;

        public PasswordVault(string path) { this.path = path; }

        public string FilePath { get { return path; } }
        public bool Exists { get { return File.Exists(path); } }
        public bool IsUnlocked { get { return encKey != null; } }

        public static bool ValidPin(string pin)
        {
            if (pin == null || pin.Length != 6) return false;
            foreach (char c in pin) if (c < '0' || c > '9') return false;
            return true;
        }

        public int AttemptsLeft
        {
            get
            {
                try { return Math.Max(0, MaxAttempts - Read(false).Failures); }
                catch (Exception) { return MaxAttempts; }
            }
        }

        // ---------------------------------------------------------------- lock state

        public void Create(string pin)
        {
            if (!ValidPin(pin)) throw new ArgumentException("The PIN must be 6 digits.");
            salt = RandomBytes(16);
            iterations = Iterations;
            DeriveKeys(pin, salt, iterations, out encKey, out macKey);
            logins = new List<SavedLogin>();
            Save();
            Touch();
        }

        public UnlockResult Unlock(string pin, out int attemptsLeft)
        {
            Envelope e = Read(true);
            byte[] enc, mac;
            DeriveKeys(pin ?? "", e.Salt, e.Iterations, out enc, out mac);
            if (!FixedTimeEquals(Hmac(mac, MacInput(e.Iterations, e.Salt, e.Iv, e.Data)), e.Mac))
            {
                Array.Clear(enc, 0, enc.Length);
                Array.Clear(mac, 0, mac.Length);
                e.Failures++;
                if (e.Failures >= MaxAttempts) { Erase(); attemptsLeft = 0; return UnlockResult.Erased; }
                WriteOuter(e);
                attemptsLeft = MaxAttempts - e.Failures;
                return UnlockResult.WrongPin;
            }
            logins = Decrypt(enc, e);
            encKey = enc;
            macKey = mac;
            salt = e.Salt;
            iterations = e.Iterations;
            loadedStamp = Stamp();
            if (e.Failures != 0) { e.Failures = 0; WriteOuter(e); }
            Touch();
            attemptsLeft = MaxAttempts;
            return UnlockResult.Unlocked;
        }

        public void Lock()
        {
            if (encKey == null) return;
            Array.Clear(encKey, 0, encKey.Length);
            Array.Clear(macKey, 0, macKey.Length);
            encKey = macKey = null;
            logins = new List<SavedLogin>();
            if (Locked != null) Locked(this, EventArgs.Empty);
        }

        public void LockIfIdle()
        {
            if (IsUnlocked && DateTime.UtcNow - lastUse > AutoLock) Lock();
        }

        public void Touch() { lastUse = DateTime.UtcNow; }

        // Destroys the saved passwords for good: 10 wrong PINs, or "Forgot PIN".
        public void Erase()
        {
            Lock();
            try
            {
                if (File.Exists(path))
                {
                    long length = new FileInfo(path).Length;
                    using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                        f.Write(new byte[length], 0, (int)length); // best effort: no readable copy left behind
                    File.Delete(path);
                }
            }
            catch (IOException) { File.Delete(path); }
            sitesCache = null;
            if (Erased != null) Erased(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- reading (unlocked)

        public List<SavedLogin> Logins
        {
            get
            {
                Refresh();
                Touch();
                return new List<SavedLogin>(logins);
            }
        }

        public List<SavedLogin> FindFor(string pageUrl)
        {
            List<SavedLogin> found = new List<SavedLogin>();
            foreach (SavedLogin l in Logins) if (Matches(l.Url, pageUrl)) found.Add(l);
            return found;
        }

        // Another Fedatather window (a web app) may have changed the file: read it again with the same key.
        void Refresh()
        {
            if (!IsUnlocked) throw new InvalidOperationException("Saved passwords are locked.");
            if (!File.Exists(path)) { Lock(); throw new InvalidOperationException("Saved passwords were erased."); }
            if (Stamp() == loadedStamp) return;
            Envelope e = Read(true);
            if (e.Iterations != iterations || !FixedTimeEquals(e.Salt, salt) ||
                !FixedTimeEquals(Hmac(macKey, MacInput(e.Iterations, e.Salt, e.Iv, e.Data)), e.Mac))
            {
                Lock(); // replaced by a new vault with another PIN
                throw new InvalidOperationException("Saved passwords were reset in another window. Unlock them again.");
            }
            logins = Decrypt(encKey, e);
            loadedStamp = Stamp();
        }

        // ---------------------------------------------------------------- changes (unlocked)

        // Adds new logins and updates the password of ones already saved (same site address and username).
        public void Import(List<SavedLogin> items, out int added, out int updated, out int unchanged)
        {
            Refresh();
            added = updated = unchanged = 0;
            Dictionary<string, SavedLogin> index = new Dictionary<string, SavedLogin>(StringComparer.OrdinalIgnoreCase);
            foreach (SavedLogin l in logins) index[Key(l)] = l;
            foreach (SavedLogin item in items)
            {
                SavedLogin old;
                if (!index.TryGetValue(Key(item), out old))
                {
                    logins.Add(item);
                    index[Key(item)] = item;
                    added++;
                }
                else if (old.Password != item.Password || old.Name != item.Name || old.Note != item.Note)
                {
                    old.Password = item.Password;
                    if (item.Name.Length > 0) old.Name = item.Name;
                    if (item.Note.Length > 0) old.Note = item.Note;
                    updated++;
                }
                else unchanged++;
            }
            if (added + updated > 0) Save();
            Touch();
        }

        public void Delete(SavedLogin login)
        {
            Refresh();
            logins.RemoveAll(delegate(SavedLogin l) { return Key(l) == Key(login); });
            Save();
            Touch();
        }

        public void Add(SavedLogin login)
        {
            Refresh();
            if (logins.Exists(delegate(SavedLogin l) { return Key(l) == Key(login); }))
                throw new InvalidOperationException("A password for this account on " + login.Site + " is already saved.");
            logins.Add(login);
            Save();
            Touch();
        }

        // Replaces a saved login (found by its site address and username) with an edited one.
        public void Update(SavedLogin old, SavedLogin changed)
        {
            Refresh();
            int i = logins.FindIndex(delegate(SavedLogin l) { return Key(l) == Key(old); });
            if (i < 0) throw new InvalidOperationException("That saved password no longer exists.");
            for (int j = 0; j < logins.Count; j++)
                if (j != i && Key(logins[j]) == Key(changed))
                    throw new InvalidOperationException("A password for this account on " + changed.Site + " is already saved.");
            logins[i] = changed;
            Save();
            Touch();
        }

        static string Key(SavedLogin l) { return Origin(l.Url) + "\n" + l.Username; }

        void Save()
        {
            StringBuilder sb = new StringBuilder("[");
            foreach (SavedLogin l in logins)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append("{\"name\":").Append(Json.Quote(l.Name))
                  .Append(",\"url\":").Append(Json.Quote(l.Url))
                  .Append(",\"username\":").Append(Json.Quote(l.Username))
                  .Append(",\"password\":").Append(Json.Quote(l.Password))
                  .Append(",\"note\":").Append(Json.Quote(l.Note)).Append('}');
            }
            sb.Append(']');
            byte[] plain = Encoding.UTF8.GetBytes(sb.ToString());
            Envelope e = new Envelope();
            e.Iterations = iterations;
            e.Salt = salt;
            e.Iv = RandomBytes(16);
            e.Data = Crypt(true, encKey, e.Iv, plain);
            Array.Clear(plain, 0, plain.Length);
            e.Mac = Hmac(macKey, MacInput(e.Iterations, e.Salt, e.Iv, e.Data));
            HashSet<string> sites = new HashSet<string>();
            foreach (SavedLogin l in logins) { string o = Origin(l.Url); if (o.Length > 0) sites.Add(o); }
            e.Sites.AddRange(sites);
            Write(e);
            loadedStamp = Stamp();
        }

        // ---------------------------------------------------------------- sites (no PIN needed)

        public bool HasLoginsFor(string pageUrl)
        {
            if (string.IsNullOrEmpty(pageUrl) || !File.Exists(path)) return false;
            try
            {
                if (sitesCache == null || Stamp() != sitesStamp)
                {
                    sitesCache = Read(true).Sites;
                    sitesStamp = Stamp();
                }
            }
            catch (Exception) { return false; }
            foreach (string site in sitesCache) if (Matches(site, pageUrl)) return true;
            return false;
        }

        // A login saved for https://example.com fills on example.com and its subdomains, never on plain http, never
        // on look-alikes such as example.com.evil.net or notexample.com.
        public static bool Matches(string savedUrl, string pageUrl)
        {
            Uri s, p;
            if (!Uri.TryCreate(savedUrl, UriKind.Absolute, out s) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out p)) return false;
            if (p.Scheme != Uri.UriSchemeHttps && p.Scheme != Uri.UriSchemeHttp) return false;
            if (s.Scheme != Uri.UriSchemeHttps && s.Scheme != Uri.UriSchemeHttp) return false;
            if (s.Scheme == Uri.UriSchemeHttps && p.Scheme != Uri.UriSchemeHttps) return false;
            if (!s.IsDefaultPort && s.Port != p.Port) return false;
            string sh = Bare(s.Host), ph = Bare(p.Host);
            return sh.Length > 0 && (ph == sh || ph.EndsWith("." + sh, StringComparison.Ordinal));
        }

        static string Bare(string host)
        {
            host = host.ToLowerInvariant().TrimEnd('.');
            return host.StartsWith("www.", StringComparison.Ordinal) ? host.Substring(4) : host;
        }

        static string Origin(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Host.Length == 0) return (url ?? "").Trim().ToLowerInvariant();
            return u.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }

        // ---------------------------------------------------------------- file

        DateTime Stamp()
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (IOException) { return DateTime.MinValue; }
        }

        // The outer file is plain JSON holding the wrong-PIN count and the DPAPI-protected rest.
        Envelope Read(bool inner)
        {
            Dictionary<string, object> outer = Json.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            if (outer == null || Json.Str(outer, "format") != Format) throw new InvalidDataException("Not a Fedatather passwords file.");
            Envelope e = new Envelope();
            object v;
            if (outer.TryGetValue("failures", out v) && v is double) e.Failures = (int)(double)v;
            e.Vault = Json.Str(outer, "vault");
            if (!inner) return e;
            byte[] raw = Dpapi.Unprotect(Convert.FromBase64String(e.Vault));
            Dictionary<string, object> o = Json.Parse(Encoding.UTF8.GetString(raw)) as Dictionary<string, object>;
            if (o == null) throw new InvalidDataException("The passwords file is damaged.");
            if (o.TryGetValue("iterations", out v) && v is double) e.Iterations = (int)(double)v;
            e.Salt = Convert.FromBase64String(Json.Str(o, "salt"));
            e.Iv = Convert.FromBase64String(Json.Str(o, "iv"));
            e.Data = Convert.FromBase64String(Json.Str(o, "data"));
            e.Mac = Convert.FromBase64String(Json.Str(o, "mac"));
            if (o.TryGetValue("sites", out v) && v is List<object>)
                foreach (object site in (List<object>)v) if (site is string) e.Sites.Add((string)site);
            if (e.Iterations < 1 || e.Salt.Length != 16 || e.Iv.Length != 16 || e.Mac.Length != 32)
                throw new InvalidDataException("The passwords file is damaged.");
            return e;
        }

        void Write(Envelope e)
        {
            StringBuilder sites = new StringBuilder("[");
            foreach (string s in e.Sites) { if (sites.Length > 1) sites.Append(','); sites.Append(Json.Quote(s)); }
            sites.Append(']');
            string inner = "{\"iterations\":" + e.Iterations +
                ",\"salt\":" + Json.Quote(Convert.ToBase64String(e.Salt)) +
                ",\"iv\":" + Json.Quote(Convert.ToBase64String(e.Iv)) +
                ",\"data\":" + Json.Quote(Convert.ToBase64String(e.Data)) +
                ",\"mac\":" + Json.Quote(Convert.ToBase64String(e.Mac)) +
                ",\"sites\":" + sites + "}";
            e.Vault = Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(inner)));
            e.Failures = 0;
            WriteOuter(e);
        }

        void WriteOuter(Envelope e)
        {
            string text = "{\"format\":" + Json.Quote(Format) + ",\"version\":1,\"failures\":" + e.Failures +
                          ",\"vault\":" + Json.Quote(e.Vault) + "}";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            sitesCache = null;
        }

        // ---------------------------------------------------------------- crypto

        static List<SavedLogin> Decrypt(byte[] key, Envelope e)
        {
            byte[] plain = Crypt(false, key, e.Iv, e.Data);
            string json = Encoding.UTF8.GetString(plain);
            Array.Clear(plain, 0, plain.Length);
            List<SavedLogin> list = new List<SavedLogin>();
            List<object> items = Json.Parse(json) as List<object>;
            if (items != null)
                foreach (object item in items)
                {
                    Dictionary<string, object> o = item as Dictionary<string, object>;
                    if (o == null) continue;
                    list.Add(new SavedLogin { Name = Json.Str(o, "name"), Url = Json.Str(o, "url"),
                        Username = Json.Str(o, "username"), Password = Json.Str(o, "password"), Note = Json.Str(o, "note") });
                }
            return list;
        }

        static void DeriveKeys(string pin, byte[] salt, int iterations, out byte[] enc, out byte[] mac)
        {
            byte[] secret = Encoding.UTF8.GetBytes(pin);
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(secret, salt, iterations, HashAlgorithmName.SHA256))
            {
                byte[] k = kdf.GetBytes(64);
                enc = new byte[32];
                mac = new byte[32];
                Buffer.BlockCopy(k, 0, enc, 0, 32);
                Buffer.BlockCopy(k, 32, mac, 0, 32);
                Array.Clear(k, 0, k.Length);
            }
            Array.Clear(secret, 0, secret.Length);
        }

        static byte[] Crypt(bool encrypt, byte[] key, byte[] iv, byte[] data)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;
                using (ICryptoTransform t = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor())
                    return t.TransformFinalBlock(data, 0, data.Length);
            }
        }

        static byte[] MacInput(int iterations, byte[] salt, byte[] iv, byte[] data)
        {
            byte[] head = Encoding.ASCII.GetBytes("FDP1");
            byte[] count = BitConverter.GetBytes(iterations);
            byte[] all = new byte[head.Length + count.Length + salt.Length + iv.Length + data.Length];
            int at = 0;
            foreach (byte[] part in new[] { head, count, salt, iv, data })
            {
                Buffer.BlockCopy(part, 0, all, at, part.Length);
                at += part.Length;
            }
            return all;
        }

        static byte[] Hmac(byte[] key, byte[] data)
        {
            using (HMACSHA256 h = new HMACSHA256(key)) return h.ComputeHash(data);
        }

        static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        static byte[] RandomBytes(int n)
        {
            byte[] b = new byte[n];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }
    }

    // How the password page groups logins, as Edge does: auth.example.com and www.example.com are both example.com.
    // Without the full public-suffix list, two-label endings common enough to matter (com.cn, co.uk...) are known here.
    static class SiteGroup
    {
        static readonly HashSet<string> TwoLabelSuffixes = new HashSet<string>(new[] {
            "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "ac.cn", "com.hk", "org.hk", "edu.hk", "com.tw", "org.tw",
            "edu.tw", "idv.tw", "com.sg", "edu.sg", "com.my", "co.uk", "org.uk", "ac.uk", "gov.uk", "co.jp", "ne.jp",
            "or.jp", "ac.jp", "co.kr", "or.kr", "com.au", "net.au", "org.au", "edu.au", "co.nz", "com.br", "com.mx",
            "co.in", "co.za", "com.tr", "com.ar", "co.id", "com.vn", "com.ph" });

        public static string Of(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Host.Length == 0) return (url ?? "").Trim();
            string host = u.Host.ToLowerInvariant().TrimEnd('.');
            if ((u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) ||
                u.HostNameType == UriHostNameType.IPv4 || u.HostNameType == UriHostNameType.IPv6) return host;
            string[] p = host.Split('.');
            if (p.Length <= 2) return host;
            int keep = TwoLabelSuffixes.Contains(p[p.Length - 2] + "." + p[p.Length - 1]) ? 3 : 2;
            return string.Join(".", p, p.Length - keep, keep);
        }
    }

    // Windows DPAPI for the current user (what ProtectedData wraps, without needing System.Security.dll).
    static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref DATA_BLOB dataIn, string description, ref DATA_BLOB entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);

        const int CRYPTPROTECT_UI_FORBIDDEN = 1;
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Fedatather saved passwords");

        public static byte[] Protect(byte[] data) { return Run(data, true); }
        public static byte[] Unprotect(byte[] data) { return Run(data, false); }

        static byte[] Run(byte[] data, bool protect)
        {
            GCHandle d = GCHandle.Alloc(data, GCHandleType.Pinned), e = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            DATA_BLOB input = new DATA_BLOB { cbData = data.Length, pbData = d.AddrOfPinnedObject() };
            DATA_BLOB entropy = new DATA_BLOB { cbData = Entropy.Length, pbData = e.AddrOfPinnedObject() };
            DATA_BLOB output = new DATA_BLOB();
            try
            {
                bool ok = protect
                    ? CryptProtectData(ref input, "Fedatather", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
                if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
                byte[] result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                d.Free();
                e.Free();
                if (output.pbData != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[output.cbData], 0, output.pbData, output.cbData);
                    LocalFree(output.pbData);
                }
            }
        }
    }

    // Password exports: Microsoft Edge and Chrome (name, url, username, password, note), Firefox (url, username,
    // password, ...), Bitwarden (login_uri, login_username, login_password, ...), 1Password, and similar.
    static class LoginCsv
    {
        public static List<SavedLogin> Parse(string text, out int skipped)
        {
            skipped = 0;
            List<List<string>> rows = Rows(text ?? "");
            while (rows.Count > 0 && IsBlank(rows[0])) rows.RemoveAt(0);
            if (rows.Count == 0) throw new FormatException("The file is empty.");
            List<string> header = new List<string>();
            foreach (string h in rows[0]) header.Add(h.Trim().ToLowerInvariant());
            int url = Find(header, "url", "login_uri", "website", "web site", "origin", "origin_url", "uri", "address");
            int user = Find(header, "username", "login_username", "user name", "user", "email", "login", "username_value");
            int pass = Find(header, "password", "login_password", "password_value");
            int name = Find(header, "name", "title");
            int note = Find(header, "note", "notes", "extra", "comments");
            if (url < 0 || pass < 0)
                throw new FormatException("This doesn't look like a password export: it needs a column for the website and " +
                    "one for the password. (Microsoft Edge exports name, url, username, password and note.)");
            List<SavedLogin> list = new List<SavedLogin>();
            for (int r = 1; r < rows.Count; r++)
            {
                List<string> row = rows[r];
                if (IsBlank(row)) continue;
                SavedLogin l = new SavedLogin { Url = Cell(row, url).Trim(), Username = Cell(row, user), Password = Cell(row, pass),
                    Name = Cell(row, name).Trim(), Note = Cell(row, note) };
                if (l.Url.Length == 0 || l.Password.Length == 0) { skipped++; continue; }
                if (l.Name.Length == 0) l.Name = l.Site;
                list.Add(l);
            }
            return list;
        }

        static int Find(List<string> header, params string[] names)
        {
            foreach (string n in names) { int i = header.IndexOf(n); if (i >= 0) return i; }
            return -1;
        }

        static string Cell(List<string> row, int i) { return i >= 0 && i < row.Count ? row[i] : ""; }

        static bool IsBlank(List<string> row)
        {
            foreach (string c in row) if (c.Trim().Length > 0) return false;
            return true;
        }

        // RFC 4180: fields in quotes may hold commas, line breaks and doubled quotes.
        static List<List<string>> Rows(string text)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> row = new List<string>();
            StringBuilder field = new StringBuilder();
            bool quoted = false, fresh = true; // fresh: nothing read into this field yet
            int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c != '"') field.Append(c);
                    else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else if (c == '"' && fresh) { quoted = true; fresh = false; }
                else if (c == ',') { row.Add(field.ToString()); field.Length = 0; fresh = true; }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(field.ToString());
                    field.Length = 0;
                    fresh = true;
                    rows.Add(row);
                    row = new List<string>();
                }
                else { field.Append(c); fresh = false; } // a quote inside an unquoted field is just a character
            }
            if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
            return rows;
        }
    }

    // Fills a username and password into the page: the visible password field, and the field for the username
    // (autocomplete="username", else an email field, else the text field just before the password). Values are set
    // the way typing does, so sites built with React and similar frameworks notice them.
    static class LoginFill
    {
        public static string Script(string username, string password)
        {
            return @"(function (user, pass) {
  function visible(e) {
    var r = e.getBoundingClientRect(), s = getComputedStyle(e);
    return r.width > 0 && r.height > 0 && s.visibility != 'hidden' && s.display != 'none';
  }
  function usable(e) { return visible(e) && !e.disabled && !e.readOnly; }
  function set(e, v) {
    var setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    e.focus();
    setter.call(e, v);
    e.dispatchEvent(new Event('input', { bubbles: true }));
    e.dispatchEvent(new Event('change', { bubbles: true }));
  }
  var inputs = [].slice.call(document.querySelectorAll('input'));
  var pw = inputs.filter(function (e) { return e.type == 'password' && usable(e); })[0] || null;
  var texts = inputs.filter(function (e) {
    var t = (e.getAttribute('type') || 'text').toLowerCase();
    return (t == 'text' || t == 'email' || t == 'tel') && usable(e);
  });
  var u = texts.filter(function (e) { return (e.autocomplete || '').indexOf('username') >= 0; })[0] ||
          texts.filter(function (e) { return e.type == 'email'; })[0] || null;
  if (!u && pw) {
    var before = texts.filter(function (e) {
      return pw.compareDocumentPosition(e) & Node.DOCUMENT_POSITION_PRECEDING;
    });
    u = before[before.length - 1] || null;
  }
  if (!u && !pw) u = texts.filter(function (e) { return /user|email|login|account/i.test(e.name + ' ' + e.id); })[0] || null;
  if (u && user) set(u, user);
  if (pw) set(pw, pass);
  return (u && user ? 1 : 0) + (pw ? 2 : 0);
})(" + Json.Quote(username) + ", " + Json.Quote(password) + ")";
        }
    }

    // ================================================================ dialogs

    static class Ui
    {
        public static Button FlatButton(Theme theme, string text, bool primary)
        {
            Button b = new Button();
            b.FlatStyle = FlatStyle.Flat;
            b.Text = text;
            b.UseMnemonic = false;
            b.Cursor = Cursors.Hand;
            b.BackColor = primary ? theme.Accent : theme.Field;
            b.ForeColor = primary ? theme.AccentFore : theme.Fore;
            b.FlatAppearance.BorderColor = primary ? theme.Accent : theme.FieldBorder;
            b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(theme.Accent, 0.2f) : theme.Hover;
            b.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(theme.Accent, 0.05f) : theme.Press;
            if (primary) // a disabled flat button keeps its colour: make it look disabled
                b.EnabledChanged += delegate
                {
                    b.BackColor = b.Enabled ? theme.Accent : theme.Field;
                    b.FlatAppearance.BorderColor = b.Enabled ? theme.Accent : theme.FieldBorder;
                };
            return b;
        }

        public static Label Text(Theme theme, string text, Font font, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font;
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.UseMnemonic = false;
            return l;
        }

        // A dialog's Ctrl+Shift+B: close the dialog and hand boss mode to the browser.
        public static bool IsBossKey(Keys key) { return key == (Keys.Control | Keys.Shift | Keys.B); }
    }

    // Sets the PIN the first time, or asks for it.
    sealed class PinDialog : Form
    {
        readonly Theme theme;
        readonly float scale;
        readonly bool create;
        readonly TextBox first, second;
        readonly Label error;
        readonly Button ok;
        readonly Font pinFont = new Font("Segoe UI", 18f), textFont = new Font("Segoe UI", 9.5f),
                      headingFont = new Font("Segoe UI Semibold", 12f);
        public Action BossKey;
        public bool ForgotRequested { get; private set; }
        public string Pin { get { return first.Text; } }

        public PinDialog(Theme theme, float scale, bool create, int attemptsLeft, string message)
        {
            this.theme = theme;
            this.scale = scale;
            this.create = create;
            Text = create ? "Set a PIN for saved passwords" : "Unlock saved passwords";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = theme.Page;
            ForeColor = theme.Fore;
            Font = textFont;

            int w = S(380), pad = S(24), y = pad, inner = w - pad * 2;
            Label heading = Ui.Text(theme, create ? "Choose a 6-digit PIN" : "Enter your 6-digit PIN", headingFont, theme.Fore);
            heading.SetBounds(pad, y, inner, S(28));
            Controls.Add(heading);
            y += S(32);

            string info = create
                ? "It protects the passwords you import. Write it down somewhere safe: it can't be recovered. " +
                  "If you forget it, your saved passwords are lost, and " + PasswordVault.MaxAttempts +
                  " wrong PINs in a row erase them for good."
                : (attemptsLeft < PasswordVault.MaxAttempts
                    ? attemptsLeft + (attemptsLeft == 1 ? " attempt" : " attempts") + " left. After " + PasswordVault.MaxAttempts +
                      " wrong PINs in a row, your saved passwords are erased for good."
                    : "Your saved passwords are locked.");
            Label infoLabel = Ui.Text(theme, info, textFont, create || attemptsLeft < PasswordVault.MaxAttempts ? theme.Target : theme.Muted);
            int infoH = TextRenderer.MeasureText(info, textFont, new Size(inner, 0), TextFormatFlags.WordBreak).Height + S(4);
            infoLabel.SetBounds(pad, y, inner, infoH);
            Controls.Add(infoLabel);
            y += infoH + S(12);

            first = PinBox();
            first.SetBounds(pad, y, inner, first.Height);
            first.AccessibleName = create ? "New PIN" : "PIN";
            Controls.Add(first);
            y += first.Height + S(10);
            if (create)
            {
                Label again = Ui.Text(theme, "Type it again", textFont, theme.Muted);
                again.SetBounds(pad, y, inner, S(20));
                Controls.Add(again);
                y += S(22);
                second = PinBox();
                second.SetBounds(pad, y, inner, second.Height);
                second.AccessibleName = "Confirm PIN";
                Controls.Add(second);
                y += second.Height + S(10);
            }

            error = Ui.Text(theme, message ?? "", textFont, theme.Target);
            error.SetBounds(pad, y, inner, S(40));
            Controls.Add(error);
            y += S(44);

            if (!create)
            {
                LinkLabel forgot = new LinkLabel();
                forgot.Text = "Forgot your PIN? Erase saved passwords…";
                forgot.Font = textFont;
                forgot.LinkColor = forgot.ActiveLinkColor = theme.Accent;
                forgot.BackColor = Color.Transparent;
                forgot.AutoSize = true;
                forgot.Location = new Point(pad, y);
                forgot.LinkClicked += delegate
                {
                    ForgotRequested = true;
                    DialogResult = DialogResult.Abort;
                };
                Controls.Add(forgot);
                y += S(30);
            }

            Button cancel = Ui.FlatButton(theme, "Cancel", false);
            ok = Ui.FlatButton(theme, create ? "Set PIN" : "Unlock", true);
            int bw = S(100), bh = S(34);
            cancel.SetBounds(w - pad - bw, y, bw, bh);
            ok.SetBounds(w - pad - bw * 2 - S(8), y, bw, bh);
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate { Submit(); };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(w, y + bh + pad);
            UpdateOk();
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        TextBox PinBox()
        {
            TextBox b = new TextBox();
            b.Font = pinFont;
            b.MaxLength = 6;
            b.UseSystemPasswordChar = true;
            b.TextAlign = HorizontalAlignment.Center;
            b.BackColor = theme.Field;
            b.ForeColor = theme.Fore;
            b.BorderStyle = BorderStyle.FixedSingle;
            b.ImeMode = ImeMode.Disable;
            b.ShortcutsEnabled = true;
            b.KeyPress += delegate(object sender, KeyPressEventArgs e)
            {
                if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9')) e.Handled = true; // digits only
            };
            b.TextChanged += delegate
            {
                StringBuilder digits = new StringBuilder();
                foreach (char c in b.Text) if (c >= '0' && c <= '9') digits.Append(c);
                if (digits.ToString() != b.Text) { b.Text = digits.ToString(); b.SelectionStart = b.Text.Length; } // pasted text
                UpdateOk();
                // Unlocking goes ahead as soon as the 6th digit is typed.
                if (!create && PasswordVault.ValidPin(b.Text)) BeginInvoke(new Action(Submit));
            };
            return b;
        }

        void UpdateOk()
        {
            if (ok == null) return;
            ok.Enabled = PasswordVault.ValidPin(first.Text) && (!create || PasswordVault.ValidPin(second.Text));
        }

        void Submit()
        {
            if (DialogResult != DialogResult.None || !PasswordVault.ValidPin(first.Text)) return;
            if (create && second.Text != first.Text)
            {
                error.Text = "The two PINs don't match.";
                second.Text = "";
                second.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetDarkTitleBar(Handle, theme.Dark);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            first.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Ui.IsBossKey(keyData))
            {
                DialogResult = DialogResult.Cancel;
                if (BossKey != null) BeginInvoke(BossKey);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { pinFont.Dispose(); textFont.Dispose(); headingFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // What the browser and its Settings window use: unlocking (setting the PIN the first time), importing, the
    // clipboard, and site icons.
    sealed class PasswordManager : IDisposable
    {
        public readonly PasswordVault Vault;
        readonly Theme theme;
        readonly float scale;
        readonly Timer idle = new Timer { Interval = 15000 };
        readonly Timer clipboard = new Timer { Interval = 30000 };
        string copied;
        public event EventHandler SitesUpdated; // the key icon may need to change
        public Action BossKey;

        public PasswordManager(string path, Theme theme, float scale)
        {
            Vault = new PasswordVault(path);
            this.theme = theme;
            this.scale = scale;
            idle.Tick += delegate { Vault.LockIfIdle(); };
            idle.Start();
            Vault.Erased += delegate { DeleteIcons(); };
            clipboard.Tick += delegate
            {
                clipboard.Stop();
                try { if (copied != null && Clipboard.ContainsText() && Clipboard.GetText() == copied) Clipboard.Clear(); }
                catch (ExternalException) { }
                copied = null;
            };
        }

        public void SitesChanged() { if (SitesUpdated != null) SitesUpdated(this, EventArgs.Empty); }

        // A copied password is cleared from the clipboard after 30 seconds (unless something else was copied since).
        public void Copy(string text, bool secret)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (ExternalException) { return; }
            Vault.Touch();
            if (!secret) return;
            copied = text;
            clipboard.Stop();
            clipboard.Start();
        }

        // The first time, this sets the PIN. Returns false if the passwords stay locked.
        public bool EnsureUnlocked(IWin32Window owner)
        {
            if (Vault.IsUnlocked) { Vault.Touch(); return true; }
            if (!Vault.Exists)
            {
                using (PinDialog set = new PinDialog(theme, scale, true, PasswordVault.MaxAttempts, null))
                {
                    set.BossKey = BossKey;
                    if (set.ShowDialog(owner) != DialogResult.OK) return false;
                    Vault.Create(set.Pin);
                    return true;
                }
            }
            string message = null;
            while (true)
            {
                using (PinDialog ask = new PinDialog(theme, scale, false, Vault.AttemptsLeft, message))
                {
                    ask.BossKey = BossKey;
                    DialogResult r = ask.ShowDialog(owner);
                    if (ask.ForgotRequested)
                    {
                        if (MessageBox.Show(owner, "Erase all saved passwords? This can't be undone.\n\n" +
                                "After that you can set a new PIN and import your passwords again.", "Forgot PIN",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        {
                            Vault.Erase();
                            SitesChanged();
                            return false;
                        }
                        continue;
                    }
                    if (r != DialogResult.OK) return false;
                    int left;
                    UnlockResult result;
                    Cursor.Current = Cursors.WaitCursor;
                    try { result = Vault.Unlock(ask.Pin, out left); }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException || ex is InvalidDataException || ex is FormatException || ex is CryptographicException)) throw;
                        MessageBox.Show(owner, "Could not open the saved passwords:\n\n" + ex.Message, "Saved passwords",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return false;
                    }
                    if (result == UnlockResult.Unlocked) return true;
                    if (result == UnlockResult.Erased)
                    {
                        SitesChanged();
                        MessageBox.Show(owner, PasswordVault.MaxAttempts + " wrong PINs in a row: your saved passwords were erased.",
                            "Saved passwords", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                    message = "Wrong PIN.";
                }
            }
        }

        // Asks for a CSV export and imports it; reports what happened. Returns true if anything changed.
        public bool ImportCsv(IWin32Window owner)
        {
            if (!EnsureUnlocked(owner)) return false;
            string file;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Import passwords from a CSV file";
                dlg.Filter = "Password exports (*.csv)|*.csv|All files (*.*)|*.*";
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                file = dlg.FileName;
            }
            int skipped, added, updated, unchanged;
            try { Vault.Import(LoginCsv.Parse(File.ReadAllText(file), out skipped), out added, out updated, out unchanged); }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is FormatException || ex is UnauthorizedAccessException || ex is InvalidOperationException)) throw;
                MessageBox.Show(owner, "Could not import " + Path.GetFileName(file) + ":\n\n" + ex.Message, "Import passwords",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            SitesChanged();
            StringBuilder msg = new StringBuilder();
            msg.Append("Imported from ").Append(Path.GetFileName(file)).Append(":\n\n")
               .Append(added).Append(" new, ").Append(updated).Append(" updated, ").Append(unchanged).Append(" already saved.");
            if (skipped > 0) msg.Append("\n").Append(skipped).Append(skipped == 1 ? " row was" : " rows were").Append(" skipped (no website or no password).");
            msg.Append("\n\nThat CSV file still holds every password in plain text. Delete it once you no longer need it:\n").Append(file);
            MessageBox.Show(owner, msg.ToString(), "Import passwords", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return added + updated > 0;
        }

        // ---------------------------------------------------------------- site icons

        // The password page shows each site's icon. Rather than fetching icons from the sites (or an icon service)
        // whenever the page opens, the browser keeps the icon of a site that has saved logins when you visit it.
        // Erasing the saved passwords removes these too.
        public string IconFolder { get { return Path.Combine(Path.GetDirectoryName(Vault.FilePath), "site-icons"); } }

        public string IconFile(string group)
        {
            StringBuilder name = new StringBuilder();
            foreach (char c in group) name.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
            return Path.Combine(IconFolder, name + ".png");
        }

        public async void CaptureIcon(CoreWebView2 page)
        {
            try
            {
                string source = page.Source;
                if (!Vault.HasLoginsFor(source)) return;
                using (Stream icon = await page.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
                using (MemoryStream png = new MemoryStream())
                {
                    if (icon == null) return;
                    icon.CopyTo(png);
                    if (png.Length == 0 || png.Length > 256 * 1024 || page.Source != source) return;
                    Directory.CreateDirectory(IconFolder);
                    File.WriteAllBytes(IconFile(SiteGroup.Of(source)), png.ToArray());
                }
            }
            catch (Exception) { } // the page or its view went away meanwhile: no icon this time
        }

        void DeleteIcons()
        {
            try { if (Directory.Exists(IconFolder)) Directory.Delete(IconFolder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            idle.Dispose();
            clipboard.Dispose();
            Vault.Lock();
        }
    }
}
