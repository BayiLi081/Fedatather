using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace Fedatather
{
    // ================================================================ shell process

    // PowerShell on a Windows pseudoconsole (ConPTY, Windows 10 1809+), the mechanism VS Code and Windows Terminal
    // use: the shell's screen arrives as VT text, and keys and pastes go back as VT text.
    sealed class PtySession : IDisposable
    {
        public readonly string Name;           // "pwsh" or "powershell"
        public event Action<string> Output;    // background thread
        public event Action Exited;            // background thread, once, after the last output

        readonly FileStream input, output;
        readonly ProcessHandle process;
        readonly object pcLock = new object(), writeLock = new object();
        IntPtr pc;
        RegisteredWaitHandle exitWait;
        int disposed, exited;

        sealed class ProcessHandle : WaitHandle
        {
            public ProcessHandle(IntPtr handle) { SafeWaitHandle = new SafeWaitHandle(handle, true); }
        }

        PtySession(string name, IntPtr pc, FileStream input, FileStream output, IntPtr process)
        {
            Name = name;
            this.pc = pc;
            this.input = input;
            this.output = output;
            this.process = new ProcessHandle(process);
        }

        // PowerShell 7 when it's installed (as VS Code picks it), otherwise the Windows PowerShell every Windows has.
        static string FindShell(out string name)
        {
            List<string> dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7"));
            foreach (string dir in dirs)
            {
                string d = dir.Trim().Trim('"');
                if (d.Length == 0) continue;
                try
                {
                    string pwsh = Path.Combine(d, "pwsh.exe");
                    if (File.Exists(pwsh)) { name = "pwsh"; return pwsh; }
                }
                catch (ArgumentException) { } // malformed PATH entry
            }
            name = "powershell";
            return Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        }

        public static PtySession Start(string workingDir, int cols, int rows)
        {
            string name, shell = FindShell(out name);
            SafeFileHandle inRead = null, inWrite = null, outRead = null, outWrite = null;
            IntPtr pc = IntPtr.Zero, attrs = IntPtr.Zero;
            bool attrsReady = false;
            try
            {
                if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0) || !CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0))
                    throw new Win32Exception();
                int hr = CreatePseudoConsole(Coord(cols, rows), inRead, outWrite, 0, out pc);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                inRead.Dispose();   // the pseudoconsole keeps its own copies of its pipe ends
                outWrite.Dispose();

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attrs = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
                attrsReady = true;
                if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, pc,
                        (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();

                STARTUPINFOEX si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES; // none of our std handles: the shell uses the pseudoconsole
                si.lpAttributeList = attrs;
                PROCESS_INFORMATION pi;
                if (!CreateProcess(null, new StringBuilder("\"" + shell + "\" -NoLogo"), IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDir, ref si, out pi))
                    throw new Win32Exception();
                CloseHandle(pi.hThread);

                PtySession session = new PtySession(name, pc, new FileStream(inWrite, FileAccess.Write, 1, false),
                    new FileStream(outRead, FileAccess.Read, 1, false), pi.hProcess);
                pc = IntPtr.Zero;
                inWrite = outRead = null;
                return session;
            }
            finally
            {
                if (attrsReady) DeleteProcThreadAttributeList(attrs);
                if (attrs != IntPtr.Zero) Marshal.FreeHGlobal(attrs);
                if (pc != IntPtr.Zero) ClosePseudoConsole(pc);
                foreach (SafeFileHandle h in new[] { inRead, inWrite, outRead, outWrite })
                    if (h != null) h.Dispose();
            }
        }

        // Starts delivering Output and Exited; subscribe first.
        public void Begin()
        {
            Thread reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Name = "Terminal output";
            reader.Start();
            // When the shell exits, closing the pseudoconsole ends the output stream, which raises Exited.
            exitWait = ThreadPool.RegisterWaitForSingleObject(process, delegate { Close(); }, null, Timeout.Infinite, true);
        }

        void ReadLoop()
        {
            Decoder utf8 = new UTF8Encoding(false).GetDecoder(); // keeps characters split across reads intact
            byte[] bytes = new byte[16384];
            char[] chars = new char[bytes.Length + 16];
            try
            {
                int n;
                while ((n = output.Read(bytes, 0, bytes.Length)) > 0)
                {
                    int count = utf8.GetChars(bytes, 0, n, chars, 0, false);
                    Action<string> handler = Output;
                    if (count > 0 && handler != null) handler(new string(chars, 0, count));
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                output.Dispose();
                if (Interlocked.Exchange(ref exited, 1) == 0)
                {
                    Action handler = Exited;
                    if (handler != null) handler();
                }
            }
        }

        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text) || disposed != 0) return;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            try { lock (writeLock) input.Write(bytes, 0, bytes.Length); } // unbuffered (buffer size 1)
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public void Resize(int cols, int rows)
        {
            lock (pcLock) if (pc != IntPtr.Zero) ResizePseudoConsole(pc, Coord(cols, rows));
        }

        // Ends the shell if it's still running, and then the output stream.
        void Close()
        {
            IntPtr h;
            lock (pcLock) { h = pc; pc = IntPtr.Zero; }
            if (h == IntPtr.Zero) return;
            ClosePseudoConsole(h);
            lock (writeLock) input.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Output = null;
            Exited = null;
            if (exitWait != null) exitWait.Unregister(null);
            // On older Windows 10, ClosePseudoConsole waits until the output pipe is drained. The reader thread keeps
            // draining it, so close on a worker thread rather than blocking the window.
            ThreadPool.QueueUserWorkItem(delegate { Close(); process.Dispose(); });
        }

        static COORD Coord(int cols, int rows)
        {
            COORD c;
            c.X = (short)Math.Max(1, Math.Min(cols, short.MaxValue));
            c.Y = (short)Math.Max(1, Math.Min(rows, short.MaxValue));
            return c;
        }

        const int STARTF_USESTDHANDLES = 0x100;
        const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000;
        const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x20016;

        [StructLayout(LayoutKind.Sequential)]
        struct COORD { public short X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

        [DllImport("kernel32.dll")]
        static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pc);

        [DllImport("kernel32.dll")]
        static extern int ResizePseudoConsole(IntPtr pc, COORD size);

        [DllImport("kernel32.dll")]
        static extern void ClosePseudoConsole(IntPtr pc);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
            IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
            ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
    }

    // ================================================================ screen + VT parser

    struct Cell
    {
        public int Ch;      // code point; 0 = blank, -1 = right half of a wide character
        public int Fg, Bg;  // 0 = default, 1..256 = palette index + 1, VtScreen.TrueColor | rgb
        public int Attr;    // VtScreen.Bold, Underline, ...
    }

    sealed class TermLine
    {
        public Cell[] Cells;
        public bool Wrapped; // soft-wrapped: the text continues on the next line

        public TermLine(int cols, Cell fill)
        {
            Cells = new Cell[cols];
            if (fill.Bg != 0) for (int i = 0; i < cols; i++) Cells[i] = fill;
        }
    }

    // Scrollback + visible rows, fed by the xterm-style VT text ConPTY writes: cursor movement, erase, insert/delete,
    // scroll regions, SGR colours (16 / 256 / true colour), wide characters and the alternate screen.
    sealed class VtScreen
    {
        public const int TrueColor = 0x1000000;
        public const int Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16, Hidden = 32, Strike = 64;
        public const int MaxScrollback = 5000;

        enum State { Ground, Escape, Skip, Csi, Osc, OscEsc, Str, StrEsc }

        readonly List<TermLine> main = new List<TermLine>();
        List<TermLine> alt;
        int cols, rows, x, y, top, bottom;
        bool wrapPending, autoWrap = true, originMode, insertMode;
        Cell pen;
        bool[] tabs;
        int savedX, savedY;
        Cell savedPen;
        bool savedOrigin;
        State state;
        readonly List<int> args = new List<int>();
        int param = -1;
        char prefix, inter, high;
        readonly StringBuilder osc = new StringBuilder();
        int lastChar = ' ';

        public bool CursorVisible = true, AppCursorKeys, BracketedPaste;
        public long Trimmed;  // lines dropped off the top of the scrollback so far
        public long Scrolled; // lines pushed into the scrollback so far
        public string Title = "";
        public event Action<string> Reply; // answers to status queries, for the shell

        public VtScreen(int cols, int rows)
        {
            this.cols = Math.Max(1, cols);
            this.rows = Math.Max(1, rows);
            for (int i = 0; i < this.rows; i++) main.Add(new TermLine(this.cols, new Cell()));
            tabs = new bool[this.cols];
            for (int i = 8; i < this.cols; i += 8) tabs[i] = true;
            bottom = this.rows - 1;
        }

        public int Cols { get { return cols; } }
        public int Rows { get { return rows; } }
        public int CursorX { get { return x; } }
        public int CursorY { get { return y; } }
        public bool AltActive { get { return alt != null; } }
        List<TermLine> Buf { get { return alt ?? main; } }
        public int LineCount { get { return Buf.Count; } }                    // scrollback + screen
        public long LineBase { get { return alt != null ? 0 : Trimmed; } }    // absolute number of GetLine(0)
        public TermLine GetLine(int index) { return Buf[index]; }
        TermLine Row(int r) { List<TermLine> b = Buf; return b[b.Count - rows + r]; }
        public string RowText(int r) { return LineText(Row(r), 0, cols).TrimEnd(); }

        Cell Blank { get { Cell c = new Cell(); c.Bg = pen.Bg; return c; } } // erased cells keep the background colour
        TermLine NewLine() { return new TermLine(cols, Blank); }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        public static string LineText(TermLine line, int from, int to)
        {
            StringBuilder sb = new StringBuilder();
            Cell[] c = line.Cells;
            for (int i = Math.Max(0, from); i < Math.Min(to, c.Length); i++)
            {
                int ch = c[i].Ch;
                if (ch == -1) continue;
                if (ch == 0) sb.Append(' ');
                else if (ch < 0x10000) sb.Append((char)ch);
                else sb.Append(char.ConvertFromUtf32(ch));
            }
            return sb.ToString();
        }

        // Terminal cell width: 2 for East Asian wide / fullwidth characters and emoji, 0 for combining marks.
        public static int Width(int cp)
        {
            if (cp < 0x300) return 1;
            if (cp <= 0x36F || (cp >= 0x200B && cp <= 0x200F) || (cp >= 0x20D0 && cp <= 0x20FF) ||
                (cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF)) return 0;
            if ((cp >= 0x1100 && cp <= 0x115F) || (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F) ||
                (cp >= 0xAC00 && cp <= 0xD7A3) || (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFE30 && cp <= 0xFE6F) ||
                (cp >= 0xFF00 && cp <= 0xFF60) || (cp >= 0xFFE0 && cp <= 0xFFE6) || (cp >= 0x1F300 && cp <= 0x1F64F) ||
                (cp >= 0x1F900 && cp <= 0x1F9FF) || (cp >= 0x20000 && cp <= 0x3FFFD)) return 2;
            return 1;
        }

        // ---------------------------------------------------------------- parser

        public void Feed(string text)
        {
            for (int i = 0; i < text.Length; i++) Step(text[i]);
        }

        void Step(char c)
        {
            switch (state)
            {
                case State.Osc:
                    if (c == '\a') { OscDone(); state = State.Ground; }
                    else if (c == '\u001b') state = State.OscEsc;
                    else if (osc.Length < 4096) osc.Append(c);
                    return;
                case State.OscEsc:
                    if (c == '\\') { OscDone(); state = State.Ground; }
                    else { state = State.Escape; EscapeChar(c); }
                    return;
                case State.Str: // DCS / SOS / PM / APC payloads are ignored
                    if (c == '\u001b') state = State.StrEsc;
                    return;
                case State.StrEsc:
                    if (c == '\\') state = State.Ground;
                    else { state = State.Escape; EscapeChar(c); }
                    return;
            }
            if (c == '\u001b') { state = State.Escape; return; }
            if (c == '\u0018' || c == '\u001a') { state = State.Ground; return; } // CAN / SUB abort a sequence
            if (c < ' ') { Control(c); return; }
            if (c == '\u007f' || (c >= '\u0080' && c < ' ')) return;
            switch (state)
            {
                case State.Ground: Print(c); break;
                case State.Escape: EscapeChar(c); break;
                case State.Skip: state = State.Ground; break; // ESC ( B and friends: ConPTY already sends Unicode
                case State.Csi: CsiChar(c); break;
            }
        }

        void Control(char c)
        {
            switch (c)
            {
                case '\b': wrapPending = false; if (x > 0) x--; break;
                case '\t': wrapPending = false; x = NextTab(x); break;
                case '\n': case '\v': case '\f': LineFeed(); break;
                case '\r': wrapPending = false; x = 0; break;
            }
        }

        void EscapeChar(char c)
        {
            state = State.Ground;
            switch (c)
            {
                case '[': state = State.Csi; args.Clear(); param = -1; prefix = inter = '\0'; break;
                case ']': state = State.Osc; osc.Length = 0; break;
                case 'P': case 'X': case '^': case '_': state = State.Str; break;
                case '(': case ')': case '*': case '+': case '-': case '.': case '/':
                case '#': case '%': case ' ': state = State.Skip; break;
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': LineFeed(); break;
                case 'E': x = 0; LineFeed(); break;
                case 'H': tabs[x] = true; break;
                case 'M': ReverseIndex(); break;
                case 'c': FullReset(); break;
            }
        }

        void CsiChar(char c)
        {
            if (c >= '0' && c <= '9') { param = Math.Min(99999, Math.Max(0, param) * 10 + (c - '0')); return; }
            if (c == ';' || c == ':') { args.Add(param); param = -1; return; }
            if (c >= '<' && c <= '?') { prefix = c; return; }
            if (c >= ' ' && c <= '/') { inter = c; return; }
            args.Add(param);
            state = State.Ground;
            Csi(c);
        }

        int Arg(int i, int fallback)
        {
            int v = i < args.Count ? args[i] : -1;
            return v <= 0 ? fallback : v;
        }

        void Send(string s)
        {
            Action<string> handler = Reply;
            if (handler != null) handler(s);
        }

        void OscDone()
        {
            string s = osc.ToString();
            if (s.StartsWith("0;") || s.StartsWith("2;")) Title = s.Substring(2);
            osc.Length = 0;
        }

        void Csi(char f)
        {
            if (prefix == '?')
            {
                if (inter == '\0' && (f == 'h' || f == 'l')) foreach (int a in args) PrivateMode(a, f == 'h');
                return;
            }
            if (prefix != '\0' || inter != '\0')
            {
                if (prefix == '\0' && inter == '!' && f == 'p') SoftReset();
                return; // cursor style (CSI SP q), secondary DA (CSI > c)... nothing to do
            }
            int n = Arg(0, 1);
            switch (f)
            {
                case '@': InsertChars(n); break;
                case 'A': y = Math.Max(y >= top ? top : 0, y - n); wrapPending = false; break;
                case 'B': case 'e': y = Math.Min(y <= bottom ? bottom : rows - 1, y + n); wrapPending = false; break;
                case 'C': case 'a': x = Math.Min(cols - 1, x + n); wrapPending = false; break;
                case 'D': x = Math.Max(0, x - n); wrapPending = false; break;
                case 'E': y = Math.Min(y <= bottom ? bottom : rows - 1, y + n); x = 0; wrapPending = false; break;
                case 'F': y = Math.Max(y >= top ? top : 0, y - n); x = 0; wrapPending = false; break;
                case 'G': case '`': x = Clamp(n - 1, 0, cols - 1); wrapPending = false; break;
                case 'H': case 'f': MoveTo(Arg(1, 1) - 1, n - 1); break;
                case 'I': for (int i = 0; i < n; i++) x = NextTab(x); wrapPending = false; break;
                case 'J': EraseDisplay(Arg(0, 0)); break;
                case 'K': EraseLine(Arg(0, 0)); break;
                case 'L': InsertLines(n); break;
                case 'M': DeleteLines(n); break;
                case 'P': DeleteChars(n); break;
                case 'S': ScrollUp(n); break;
                case 'T': if (args.Count <= 1) ScrollDown(n); break;
                case 'X': Erase(Row(y), x, x + n); break;
                case 'Z': for (int i = 0; i < n; i++) x = PrevTab(x); wrapPending = false; break;
                case 'b': for (int i = Math.Min(n, cols * rows); i > 0; i--) PutChar(lastChar, Width(lastChar)); break;
                case 'c': if (Arg(0, 0) == 0) Send("\u001b[?1;2c"); break;
                case 'd': MoveTo(x, n - 1); break;
                case 'g':
                    if (Arg(0, 0) == 0) tabs[x] = false;
                    else if (Arg(0, 0) == 3) Array.Clear(tabs, 0, tabs.Length);
                    break;
                case 'h': case 'l': if (Arg(0, 0) == 4) insertMode = f == 'h'; break;
                case 'm': Sgr(); break;
                case 'n':
                    if (Arg(0, 0) == 5) Send("\u001b[0n");
                    else if (Arg(0, 0) == 6) Send("\u001b[" + (y + 1 - (originMode ? top : 0)) + ";" + (x + 1) + "R");
                    break;
                case 'r': SetMargins(n - 1, Arg(1, rows) - 1); break;
                case 's': SaveCursor(); break;
                case 'u': RestoreCursor(); break;
            }
        }

        void PrivateMode(int mode, bool on)
        {
            switch (mode)
            {
                case 1: AppCursorKeys = on; break;
                case 6: originMode = on; MoveTo(0, 0); break;
                case 7: autoWrap = on; break;
                case 25: CursorVisible = on; break;
                case 47: case 1047: UseAlt(on, false); break;
                case 1049: UseAlt(on, true); break;
                case 2004: BracketedPaste = on; break;
            }
        }

        void Sgr()
        {
            for (int i = 0; i < args.Count; i++)
            {
                int a = Math.Max(0, args[i]);
                if (a == 0) pen = new Cell();
                else if (a == 1) pen.Attr |= Bold;
                else if (a == 2) pen.Attr |= Dim;
                else if (a == 3) pen.Attr |= Italic;
                else if (a == 4 || a == 21) pen.Attr |= Underline;
                else if (a == 7) pen.Attr |= Inverse;
                else if (a == 8) pen.Attr |= Hidden;
                else if (a == 9) pen.Attr |= Strike;
                else if (a == 22) pen.Attr &= ~(Bold | Dim);
                else if (a == 23) pen.Attr &= ~Italic;
                else if (a == 24) pen.Attr &= ~Underline;
                else if (a == 27) pen.Attr &= ~Inverse;
                else if (a == 28) pen.Attr &= ~Hidden;
                else if (a == 29) pen.Attr &= ~Strike;
                else if (a >= 30 && a <= 37) pen.Fg = a - 30 + 1;
                else if (a == 38) pen.Fg = ExtendedColor(ref i, pen.Fg);
                else if (a == 39) pen.Fg = 0;
                else if (a >= 40 && a <= 47) pen.Bg = a - 40 + 1;
                else if (a == 48) pen.Bg = ExtendedColor(ref i, pen.Bg);
                else if (a == 49) pen.Bg = 0;
                else if (a == 58) ExtendedColor(ref i, 0); // underline colour: not drawn
                else if (a >= 90 && a <= 97) pen.Fg = a - 90 + 9;
                else if (a >= 100 && a <= 107) pen.Bg = a - 100 + 9;
            }
        }

        // 38;5;n (palette) or 38;2;r;g;b (true colour); leaves i on the last parameter it used.
        int ExtendedColor(ref int i, int current)
        {
            int kind = i + 1 < args.Count ? args[i + 1] : -1;
            if (kind == 5 && i + 2 < args.Count)
            {
                int n = Clamp(args[i + 2], 0, 255);
                i += 2;
                return n + 1;
            }
            if (kind == 2 && i + 4 < args.Count)
            {
                int r = Clamp(args[i + 2], 0, 255), g = Clamp(args[i + 3], 0, 255), b = Clamp(args[i + 4], 0, 255);
                i += 4;
                return TrueColor | (r << 16) | (g << 8) | b;
            }
            i = args.Count;
            return current;
        }

        // ---------------------------------------------------------------- screen operations

        void Print(char c)
        {
            int cp = c;
            if (char.IsHighSurrogate(c)) { high = c; return; }
            if (char.IsLowSurrogate(c))
            {
                if (high == '\0') return;
                cp = char.ConvertToUtf32(high, c);
            }
            high = '\0';
            int w = Width(cp);
            if (w > 0) PutChar(cp, w);
        }

        void PutChar(int cp, int w)
        {
            if (wrapPending)
            {
                wrapPending = false;
                if (autoWrap) { Row(y).Wrapped = true; x = 0; LineFeed(); }
            }
            if (w > cols) return;
            if (x + w > cols) // a wide character never splits across lines
            {
                if (!autoWrap) x = cols - w;
                else { Erase(Row(y), x, cols); Row(y).Wrapped = true; x = 0; LineFeed(); }
            }
            Cell[] c = Row(y).Cells;
            if (insertMode)
            {
                Split(c, x, x);
                Array.Copy(c, x, c, x + w, cols - x - w);
                FixLastColumn(c);
            }
            Split(c, x, x + w);
            Cell cell = pen;
            cell.Ch = cp;
            c[x] = cell;
            if (w == 2) { cell.Ch = -1; c[x + 1] = cell; }
            lastChar = cp;
            x += w;
            if (x >= cols) { x = cols - 1; wrapPending = true; }
        }

        // Writing over half of a wide character blanks its other half.
        static void Split(Cell[] c, int from, int to)
        {
            if (from > 0 && from < c.Length && c[from].Ch == -1) c[from - 1].Ch = 0;
            if (to > 0 && to < c.Length && c[to].Ch == -1) c[to].Ch = 0;
        }

        void FixLastColumn(Cell[] c)
        {
            if (c[cols - 1].Ch > 0 && Width(c[cols - 1].Ch) == 2) c[cols - 1].Ch = 0; // its right half fell off
        }

        void Erase(TermLine line, int from, int to)
        {
            from = Math.Max(0, from);
            to = Math.Min(cols, to);
            if (from >= to) return;
            Cell[] c = line.Cells;
            Split(c, from, to);
            Cell b = Blank;
            for (int i = from; i < to; i++) c[i] = b;
            if (to == cols) line.Wrapped = false;
        }

        void LineFeed()
        {
            wrapPending = false;
            if (y == bottom) ScrollUp(1);
            else if (y < rows - 1) y++;
        }

        void ReverseIndex()
        {
            wrapPending = false;
            if (y == top) ScrollDown(1);
            else if (y > 0) y--;
        }

        void ScrollUp(int n)
        {
            List<TermLine> b = Buf;
            n = Math.Min(n, bottom - top + 1);
            if (n <= 0) return;
            if (alt == null && top == 0 && bottom == rows - 1)
            {
                // The whole main screen scrolls: its top lines move into the scrollback.
                for (int i = 0; i < n; i++) b.Add(NewLine());
                Scrolled += n;
                int extra = b.Count - rows - MaxScrollback;
                if (extra >= 256) { b.RemoveRange(0, extra); Trimmed += extra; } // trim in batches
            }
            else
            {
                int first = b.Count - rows;
                for (int i = 0; i < n; i++) { b.RemoveAt(first + top); b.Insert(first + bottom, NewLine()); }
            }
        }

        void ScrollDown(int n)
        {
            List<TermLine> b = Buf;
            int first = b.Count - rows;
            n = Math.Min(n, bottom - top + 1);
            for (int i = 0; i < n; i++) { b.RemoveAt(first + bottom); b.Insert(first + top, NewLine()); }
        }

        void InsertLines(int n)
        {
            if (y < top || y > bottom) return;
            List<TermLine> b = Buf;
            int first = b.Count - rows;
            n = Math.Min(n, bottom - y + 1);
            for (int i = 0; i < n; i++) { b.RemoveAt(first + bottom); b.Insert(first + y, NewLine()); }
            x = 0;
            wrapPending = false;
        }

        void DeleteLines(int n)
        {
            if (y < top || y > bottom) return;
            List<TermLine> b = Buf;
            int first = b.Count - rows;
            n = Math.Min(n, bottom - y + 1);
            for (int i = 0; i < n; i++) { b.RemoveAt(first + y); b.Insert(first + bottom, NewLine()); }
            x = 0;
            wrapPending = false;
        }

        void InsertChars(int n)
        {
            Cell[] c = Row(y).Cells;
            n = Math.Min(n, cols - x);
            Split(c, x, x);
            Array.Copy(c, x, c, x + n, cols - x - n);
            Cell b = Blank;
            for (int i = x; i < x + n; i++) c[i] = b;
            FixLastColumn(c);
            wrapPending = false;
        }

        void DeleteChars(int n)
        {
            Cell[] c = Row(y).Cells;
            n = Math.Min(n, cols - x);
            Split(c, x, x + n);
            Array.Copy(c, x + n, c, x, cols - x - n);
            Cell b = Blank;
            for (int i = cols - n; i < cols; i++) c[i] = b;
            wrapPending = false;
        }

        void EraseLine(int mode)
        {
            if (mode == 0) Erase(Row(y), x, cols);
            else if (mode == 1) Erase(Row(y), 0, x + 1);
            else if (mode == 2) Erase(Row(y), 0, cols);
        }

        void EraseDisplay(int mode)
        {
            if (mode == 0)
            {
                Erase(Row(y), x, cols);
                for (int r = y + 1; r < rows; r++) Erase(Row(r), 0, cols);
            }
            else if (mode == 1)
            {
                for (int r = 0; r < y; r++) Erase(Row(r), 0, cols);
                Erase(Row(y), 0, x + 1);
            }
            else if (mode == 2)
            {
                for (int r = 0; r < rows; r++) Erase(Row(r), 0, cols);
            }
            else if (mode == 3 && alt == null)
            {
                int lines = main.Count - rows; // clear the scrollback
                if (lines > 0) { main.RemoveRange(0, lines); Trimmed += lines; }
            }
        }

        void MoveTo(int col, int row)
        {
            y = originMode ? Clamp(row + top, top, bottom) : Clamp(row, 0, rows - 1);
            x = Clamp(col, 0, cols - 1);
            wrapPending = false;
        }

        void SetMargins(int t, int b)
        {
            t = Math.Max(0, t);
            b = Math.Min(rows - 1, b);
            if (t >= b) return;
            top = t;
            bottom = b;
            MoveTo(0, 0);
        }

        int NextTab(int from)
        {
            for (int i = from + 1; i < cols; i++) if (tabs[i]) return i;
            return cols - 1;
        }

        int PrevTab(int from)
        {
            for (int i = from - 1; i > 0; i--) if (tabs[i]) return i;
            return 0;
        }

        void SaveCursor() { savedX = x; savedY = y; savedPen = pen; savedOrigin = originMode; }

        void RestoreCursor()
        {
            x = Math.Min(savedX, cols - 1);
            y = Math.Min(savedY, rows - 1);
            pen = savedPen;
            originMode = savedOrigin;
            wrapPending = false;
        }

        void UseAlt(bool on, bool withCursor)
        {
            if (on == (alt != null)) return;
            if (on)
            {
                if (withCursor) SaveCursor();
                alt = new List<TermLine>();
                for (int i = 0; i < rows; i++) alt.Add(new TermLine(cols, new Cell()));
            }
            else
            {
                alt = null;
                if (withCursor) RestoreCursor();
            }
            top = 0;
            bottom = rows - 1;
            wrapPending = false;
        }

        void SoftReset()
        {
            CursorVisible = autoWrap = true;
            originMode = insertMode = AppCursorKeys = false;
            top = 0;
            bottom = rows - 1;
            pen = savedPen = new Cell();
            savedX = savedY = 0;
        }

        void FullReset()
        {
            SoftReset();
            alt = null;
            BracketedPaste = false;
            Title = "";
            EraseDisplay(2);
            x = y = 0;
            wrapPending = false;
            tabs = new bool[cols];
            for (int i = 8; i < cols; i += 8) tabs[i] = true;
        }

        // No reflow. Shrinking drops blank rows under the cursor first, then moves top rows into the scrollback;
        // growing adds blank rows at the bottom, as the console does. ConPTY repaints the screen afterwards.
        public void Resize(int newCols, int newRows)
        {
            newCols = Math.Max(1, newCols);
            newRows = Math.Max(1, newRows);
            if (newCols == cols && newRows == rows) return;

            List<TermLine> b = Buf;
            int cursor = b.Count - rows + y;
            if (newRows < rows)
            {
                int drop = rows - newRows;
                while (drop > 0 && b.Count - 1 > cursor && IsBlank(b[b.Count - 1])) { b.RemoveAt(b.Count - 1); drop--; }
                if (alt != null && drop > 0) { b.RemoveRange(0, drop); cursor -= drop; } // no scrollback here
            }
            else
            {
                for (int i = rows; i < newRows; i++) b.Add(new TermLine(cols, new Cell()));
                if (alt != null) for (int i = rows; i < newRows; i++) main.Add(new TermLine(cols, new Cell()));
            }
            y = Clamp(cursor - (b.Count - newRows), 0, newRows - 1);

            // Only the visible rows change width; scrollback lines keep theirs.
            foreach (List<TermLine> buf in alt != null ? new[] { main, alt } : new[] { main })
                for (int i = Math.Max(0, buf.Count - newRows); i < buf.Count; i++)
                {
                    TermLine line = buf[i];
                    if (line.Cells.Length == newCols) continue;
                    if (newCols < line.Cells.Length && line.Cells[newCols].Ch == -1) line.Cells[newCols - 1].Ch = 0;
                    Array.Resize(ref line.Cells, newCols);
                }

            bool[] t = new bool[newCols];
            Array.Copy(tabs, t, Math.Min(tabs.Length, newCols));
            for (int i = Math.Max(8, (tabs.Length + 7) / 8 * 8); i < newCols; i += 8) t[i] = true;
            tabs = t;
            cols = newCols;
            rows = newRows;
            top = 0;
            bottom = rows - 1;
            x = Math.Min(x, cols - 1);
            savedX = Math.Min(savedX, cols - 1);
            savedY = Math.Min(savedY, rows - 1);
            wrapPending = false;
        }

        static bool IsBlank(TermLine line)
        {
            foreach (Cell c in line.Cells) if (c.Ch > 0 || c.Bg != 0) return false;
            return true;
        }
    }

    // ================================================================ terminal view

    // Draws the screen and turns keys, mouse and clipboard actions into input for the shell.
    sealed class TerminalView : Control
    {
        const string WordSeparators = " ()[]{}',\"`|;<>";

        readonly Theme theme;
        readonly float scale;
        readonly Font font, boldFont;
        readonly int[] palette = new int[256];
        readonly int fore, back, selectionBack;
        readonly int cw, ch, padX, padY, barW;
        readonly StringBuilder pending = new StringBuilder(), run = new StringBuilder();
        VtScreen screen;
        PtySession session;
        bool flushQueued, hover, selecting, draggingThumb;
        int offset, wheel; // offset: lines scrolled back from the bottom
        long selAnchor = -1, selCaret = -1; // (absolute line << 16) | column, so a selection stays put while output scrolls
        char highSurrogate;

        public event EventHandler ShellExited;
        public string ShellName { get; private set; }
        public bool Running { get { return session != null; } }

        public TerminalView(Theme theme, float scale)
        {
            this.theme = theme;
            this.scale = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            ImeMode = ImeMode.NoControl; // typed Chinese / Japanese / Korean goes to the shell
            Cursor = Cursors.IBeam;
            AccessibleName = "Terminal";
            AccessibleRole = AccessibleRole.Text;

            font = new Font("Cascadia Mono", 10f);
            if (font.Name != "Cascadia Mono") { font.Dispose(); font = new Font("Consolas", 10.5f); }
            boldFont = new Font(font, FontStyle.Bold);
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            cw = Math.Max(1, TextRenderer.MeasureText("MMMMMMMMMM", font, new Size(int.MaxValue, int.MaxValue), flags).Width / 10);
            ch = Math.Max(1, font.Height);
            padX = S(10);
            padY = S(4);
            barW = S(12);

            // VS Code's default terminal colours.
            int[] ansi = theme.Dark
                ? new[] { 0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
                          0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xE5E5E5 }
                : new[] { 0x000000, 0xCD3131, 0x00BC00, 0x949800, 0x0451A5, 0xBC05BC, 0x0598BC, 0x555555,
                          0x666666, 0xCD3131, 0x14CE14, 0xB5BA00, 0x0451A5, 0xBC05BC, 0x0598BC, 0xA5A5A5 };
            for (int i = 0; i < 16; i++) palette[i] = Opaque(ansi[i]);
            int[] steps = { 0, 95, 135, 175, 215, 255 };
            for (int i = 16; i < 232; i++)
            {
                int v = i - 16;
                palette[i] = Opaque((steps[v / 36] << 16) | (steps[v / 6 % 6] << 8) | steps[v % 6]);
            }
            for (int i = 232; i < 256; i++) { int g = 8 + (i - 232) * 10; palette[i] = Opaque((g << 16) | (g << 8) | g); }
            fore = Opaque(theme.Dark ? 0xCCCCCC : 0x333333);
            back = theme.Page.ToArgb();
            selectionBack = Opaque(theme.Dark ? 0x264F78 : 0xADD6FF);
            BackColor = theme.Page;
        }

        int S(int px) { return (int)Math.Round(px * scale); }
        static int Opaque(int rgb) { return unchecked((int)0xFF000000) | rgb; }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        static long Pos(long line, int col) { return (line << 16) | (uint)col; }

        // ---------------------------------------------------------------- session

        bool GridFor(Size size, out int cols, out int rows)
        {
            cols = (size.Width - padX * 2 - barW) / cw;
            rows = (size.Height - padY * 2) / ch;
            return cols >= 4 && rows >= 1; // hidden / minimized: keep the last size
        }

        public void Start()
        {
            if (session != null) return;
            int cols, rows;
            if (!GridFor(ClientSize, out cols, out rows)) { cols = 80; rows = 24; }
            VtScreen s = new VtScreen(cols, rows);
            PtySession p = PtySession.Start(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cols, rows);
            screen = s;
            session = p;
            ShellName = p.Name;
            offset = 0;
            selAnchor = selCaret = -1;
            s.Reply += delegate(string reply) { if (session == p) p.Write(reply); };
            p.Output += delegate(string text) { OnOutput(p, text); };
            p.Exited += delegate { Post(delegate { if (session == p) { Stop(); if (ShellExited != null) ShellExited(this, EventArgs.Empty); } }); };
            p.Begin();
            Invalidate();
        }

        // Ends the shell (and everything it started in this terminal).
        public void Stop()
        {
            PtySession p = session;
            if (p == null) return;
            lock (pending) { session = null; pending.Length = 0; }
            p.Dispose();
        }

        public void Write(string text)
        {
            if (session == null) return;
            if (offset != 0) { offset = 0; Invalidate(); }
            ClearSelection();
            session.Write(text);
        }

        // For tests and the panel: the visible text of a screen row.
        public string RowText(int row) { return screen == null || row >= screen.Rows ? "" : screen.RowText(row); }
        public int RowCount { get { return screen == null ? 0 : screen.Rows; } }

        void OnOutput(PtySession from, string text) // reader thread
        {
            lock (pending)
            {
                if (session != from) return;
                pending.Append(text);
                if (flushQueued) return;
                flushQueued = true;
            }
            if (!Post(Flush)) lock (pending) flushQueued = false;
        }

        bool Post(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return false;
                BeginInvoke(action);
                return true;
            }
            catch (InvalidOperationException) { return false; } // the window is going away
        }

        void Flush()
        {
            string text;
            lock (pending)
            {
                text = pending.ToString();
                pending.Length = 0;
                flushQueued = false;
            }
            if (screen == null || text.Length == 0) return;
            long scrolled = screen.Scrolled;
            bool alt = screen.AltActive;
            try { screen.Feed(text); }
            catch (Exception) { } // a sequence this parser gets wrong must not take the browser down with it
            if (offset > 0) offset += (int)(screen.Scrolled - scrolled); // a scrolled-back view stays where it is
            if (alt != screen.AltActive) { offset = 0; selAnchor = selCaret = -1; }
            ClampOffset();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int cols, rows;
            if (screen == null || !GridFor(ClientSize, out cols, out rows)) return;
            if (cols == screen.Cols && rows == screen.Rows) return;
            screen.Resize(cols, rows);
            if (session != null) session.Resize(cols, rows);
            ClampOffset();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
                font.Dispose();
                boldFont.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- keyboard

        protected override bool IsInputKey(Keys keyData) { return true; }   // arrows, Tab, Esc... belong to the shell
        protected override bool IsInputChar(char charCode) { return true; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || screen == null) return;
            Keys k = e.KeyData;
            if (k == (Keys.Alt | Keys.F4)) return; // still closes the window
            if (k == (Keys.Control | Keys.Shift | Keys.C) || k == (Keys.Control | Keys.Insert) ||
                (k == (Keys.Control | Keys.C) && HasSelection))
                CopySelection();
            else if (k == (Keys.Control | Keys.V) || k == (Keys.Control | Keys.Shift | Keys.V) || k == (Keys.Shift | Keys.Insert))
                Paste();
            else if (k == (Keys.Shift | Keys.PageUp)) ScrollBy(screen.Rows - 1);
            else if (k == (Keys.Shift | Keys.PageDown)) ScrollBy(1 - screen.Rows);
            else
            {
                string seq = KeySequence(e.KeyCode, e.Modifiers);
                if (seq == null) return; // characters arrive through KeyPress, which knows the keyboard layout
                Write(seq);
            }
            e.Handled = true;
            // Drop the character these keys would also type (Enter, Backspace, Tab, Esc, Ctrl+V...). Not for arrows
            // and the like: they type nothing, and suppressing would discard characters already queued behind them.
            Keys code = e.KeyCode;
            e.SuppressKeyPress = e.Control || code == Keys.Enter || code == Keys.Back || code == Keys.Tab ||
                                 code == Keys.Escape || code == Keys.Space;
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled) return;
            e.Handled = true; // also stops the "ding" for Alt+letter
            char c = e.KeyChar;
            if (char.IsHighSurrogate(c)) { highSurrogate = c; return; }
            string text = c.ToString();
            if (char.IsLowSurrogate(c))
            {
                if (highSurrogate == '\0') return;
                text = new string(new[] { highSurrogate, c });
            }
            highSurrogate = '\0';
            Keys mods = ModifierKeys;
            if ((mods & Keys.Alt) != 0 && (mods & Keys.Control) == 0) text = "\u001b" + text; // Alt = Esc prefix; AltGr = Ctrl+Alt
            Write(text);
        }

        string KeySequence(Keys key, Keys mods)
        {
            bool shift = (mods & Keys.Shift) != 0, ctrl = (mods & Keys.Control) != 0, alt = (mods & Keys.Alt) != 0;
            int m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0); // xterm modifier parameter
            switch (key)
            {
                case Keys.Up: return CursorKey('A', m);
                case Keys.Down: return CursorKey('B', m);
                case Keys.Right: return CursorKey('C', m);
                case Keys.Left: return CursorKey('D', m);
                case Keys.Home: return CursorKey('H', m);
                case Keys.End: return CursorKey('F', m);
                case Keys.Insert: return TildeKey(2, m);
                case Keys.Delete: return TildeKey(3, m);
                case Keys.PageUp: return TildeKey(5, m);
                case Keys.PageDown: return TildeKey(6, m);
                case Keys.F1: case Keys.F2: case Keys.F3: case Keys.F4:
                    char f = "PQRS"[key - Keys.F1];
                    return m == 1 ? "\u001bO" + f : "\u001b[1;" + m + f;
                case Keys.F5: return TildeKey(15, m);
                case Keys.F6: return TildeKey(17, m);
                case Keys.F7: return TildeKey(18, m);
                case Keys.F8: return TildeKey(19, m);
                case Keys.F9: return TildeKey(20, m);
                case Keys.F10: return TildeKey(21, m);
                case Keys.F11: return TildeKey(23, m);
                case Keys.F12: return TildeKey(24, m);
                case Keys.Enter: return alt ? "\u001b\r" : "\r";
                case Keys.Back: return ctrl ? "\b" : alt ? "\u001b\u007f" : "\u007f";
                case Keys.Tab: return shift ? "\u001b[Z" : "\t";
                case Keys.Escape: return "\u001b";
                case Keys.Space: return ctrl ? "\0" : null;
            }
            return null;
        }

        string CursorKey(char c, int m)
        {
            if (m > 1) return "\u001b[1;" + m + c;
            return (screen.AppCursorKeys ? "\u001bO" : "\u001b[") + c;
        }

        static string TildeKey(int n, int m) { return "\u001b[" + n + (m > 1 ? ";" + m : "") + "~"; }

        // ---------------------------------------------------------------- clipboard and selection

        bool HasSelection { get { return selAnchor >= 0 && selAnchor != selCaret; } }

        void ClearSelection()
        {
            if (selAnchor < 0) return;
            selAnchor = selCaret = -1;
            Invalidate();
        }

        void SelectionRange(out long start, out long end)
        {
            if (!HasSelection) { start = end = -1; return; }
            start = Math.Min(selAnchor, selCaret);
            end = Math.Max(selAnchor, selCaret);
        }

        // Returns false when nothing is selected.
        public bool CopySelection()
        {
            if (!HasSelection) return false;
            long start, end;
            SelectionRange(out start, out end);
            StringBuilder sb = new StringBuilder();
            for (long line = start >> 16; line <= end >> 16; line++)
            {
                int index = (int)(line - screen.LineBase);
                if (index < 0 || index >= screen.LineCount) continue;
                TermLine l = screen.GetLine(index);
                bool last = line == end >> 16;
                string text = VtScreen.LineText(l, line == start >> 16 ? (int)(start & 0xFFFF) : 0,
                    last ? (int)(end & 0xFFFF) : int.MaxValue);
                if (l.Wrapped && !last) sb.Append(text); // soft-wrapped: the same line of output
                else { sb.Append(text.TrimEnd()); if (!last) sb.Append("\r\n"); }
            }
            try { if (sb.Length > 0) Clipboard.SetText(sb.ToString()); }
            catch (ExternalException) { }
            ClearSelection();
            return true;
        }

        public void Paste()
        {
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch (ExternalException) { }
            if (string.IsNullOrEmpty(text) || screen == null) return;
            text = text.Replace("\r\n", "\r").Replace('\n', '\r');
            if (screen.BracketedPaste) text = "\u001b[200~" + text.Replace("\u001b[201~", "") + "\u001b[201~";
            Write(text);
        }

        void SelectWord(long line, int col)
        {
            int index = (int)(line - screen.LineBase);
            if (index < 0 || index >= screen.LineCount) return;
            Cell[] c = screen.GetLine(index).Cells;
            if (col >= c.Length) return;
            int s = col, e = col + 1;
            if (IsWordCell(c[col]))
            {
                while (s > 0 && IsWordCell(c[s - 1])) s--;
                while (e < c.Length && IsWordCell(c[e])) e++;
            }
            selAnchor = Pos(line, s);
            selCaret = Pos(line, e);
        }

        static bool IsWordCell(Cell c)
        {
            return c.Ch == -1 || c.Ch > 0xFFFF || (c.Ch > 0 && WordSeparators.IndexOf((char)c.Ch) < 0);
        }

        // ---------------------------------------------------------------- mouse

        int FirstVisible { get { return screen.LineCount - screen.Rows - offset; } }
        int MaxOffset { get { return Math.Max(0, screen.LineCount - screen.Rows); } }
        void ClampOffset() { offset = Clamp(offset, 0, MaxOffset); }

        void ScrollBy(int lines)
        {
            int old = offset;
            offset += lines;
            ClampOffset();
            if (offset != old) Invalidate();
        }

        void HitTest(Point p, bool nearestEdge, out long line, out int col)
        {
            int row = Clamp((int)Math.Floor((p.Y - padY) / (double)ch), 0, screen.Rows - 1);
            double fx = (p.X - padX) / (double)cw;
            col = Clamp(nearestEdge ? (int)Math.Round(fx) : (int)Math.Floor(fx), 0, screen.Cols);
            line = screen.LineBase + FirstVisible + row;
        }

        bool OnScrollbar(Point p) { return p.X >= ClientSize.Width - barW && MaxOffset > 0; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (screen == null) return;
            if (e.Button == MouseButtons.Right) // Windows terminals: right-click copies a selection, otherwise pastes
            {
                if (!CopySelection()) Paste();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (OnScrollbar(e.Location)) { draggingThumb = true; DragThumb(e.Y); return; }
            long line;
            int col;
            if (e.Clicks == 2)
            {
                HitTest(e.Location, false, out line, out col);
                SelectWord(line, col);
            }
            else
            {
                HitTest(e.Location, true, out line, out col);
                selAnchor = selCaret = Pos(line, col);
                selecting = true;
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (screen == null) return;
            if (draggingThumb) { DragThumb(e.Y); return; }
            if (selecting)
            {
                if (e.Y < 0) ScrollBy(1);
                else if (e.Y > ClientSize.Height) ScrollBy(-1);
                long line;
                int col;
                HitTest(e.Location, true, out line, out col);
                if (Pos(line, col) != selCaret) { selCaret = Pos(line, col); Invalidate(); }
                return;
            }
            Cursor = OnScrollbar(e.Location) ? Cursors.Default : Cursors.IBeam;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            selecting = draggingThumb = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (screen == null) return;
            wheel += e.Delta;
            int notches = wheel / 120;
            wheel -= notches * 120;
            if (notches == 0) return;
            int perNotch = SystemInformation.MouseWheelScrollLines;
            int lines = notches * (perNotch <= 0 ? screen.Rows : perNotch);
            if (screen.AltActive) // full-screen programs have no scrollback: scroll them with arrow keys instead
            {
                string key = CursorKey(lines > 0 ? 'A' : 'B', 1);
                StringBuilder sb = new StringBuilder();
                for (int i = Math.Abs(lines); i > 0; i--) sb.Append(key);
                Write(sb.ToString());
            }
            else ScrollBy(lines);
        }

        Rectangle Thumb()
        {
            int total = screen.LineCount, h = ClientSize.Height;
            int th = Math.Max(S(24), (int)((long)h * screen.Rows / total));
            int ty = (int)((long)(h - th) * FirstVisible / Math.Max(1, MaxOffset));
            return new Rectangle(ClientSize.Width - barW + S(3), ty, barW - S(6), th);
        }

        void DragThumb(int mouseY)
        {
            Rectangle t = Thumb();
            int range = Math.Max(1, ClientSize.Height - t.Height);
            int first = (int)Math.Round((double)(mouseY - t.Height / 2) * MaxOffset / range);
            int old = offset;
            offset = MaxOffset - Clamp(first, 0, MaxOffset);
            if (offset != old) Invalidate();
        }

        // ---------------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(back));
            if (screen == null) return;
            long selStart, selEnd;
            SelectionRange(out selStart, out selEnd);
            int first = FirstVisible;
            for (int r = 0; r < screen.Rows; r++)
            {
                int index = first + r;
                if (index >= 0 && index < screen.LineCount)
                    DrawLine(g, screen.GetLine(index), screen.LineBase + index, padY + r * ch, selStart, selEnd);
            }
            if (offset == 0 && screen.CursorVisible && session != null) DrawCursor(g);
            if (MaxOffset > 0 && (hover || offset > 0 || draggingThumb))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(theme.Dark ? 70 : 60, theme.Muted)))
                    g.FillRectangle(b, Thumb());
        }

        void Colors(Cell c, out int fg, out int bg)
        {
            fg = c.Fg == 0 ? fore : ColorOf(c.Fg, (c.Attr & VtScreen.Bold) != 0);
            bg = c.Bg == 0 ? back : ColorOf(c.Bg, false);
            if ((c.Attr & VtScreen.Inverse) != 0) { int t = fg; fg = bg; bg = t; }
            if ((c.Attr & VtScreen.Dim) != 0) fg = Blend(fg, bg);
            if ((c.Attr & VtScreen.Hidden) != 0) fg = bg;
        }

        int ColorOf(int v, bool bold)
        {
            if ((v & VtScreen.TrueColor) != 0) return Opaque(v & 0xFFFFFF);
            int i = v - 1;
            if (bold && i < 8) i += 8; // bold text in bright colours, as VS Code does
            return palette[i];
        }

        static int Blend(int a, int b)
        {
            Color x = Color.FromArgb(a), y = Color.FromArgb(b);
            return Color.FromArgb((x.R + y.R) / 2, (x.G + y.G) / 2, (x.B + y.B) / 2).ToArgb();
        }

        void DrawLine(Graphics g, TermLine line, long abs, int top, long selStart, long selEnd)
        {
            Cell[] cells = line.Cells;
            int n = Math.Min(cells.Length, screen.Cols), runX = 0, runFg = 0, runBg = 0, runAttr = 0;
            run.Length = 0;
            for (int x = 0; x < n; x++)
            {
                Cell c = cells[x];
                if (c.Ch == -1) continue; // right half of a wide character: drawn with its left half
                int width = x + 1 < n && cells[x + 1].Ch == -1 ? 2 : 1;
                int fg, bg;
                Colors(c, out fg, out bg);
                long pos = Pos(abs, x);
                if (pos >= selStart && pos < selEnd) bg = selectionBack;
                const int fontAttrs = VtScreen.Bold | VtScreen.Underline | VtScreen.Strike;
                // Plain ASCII shares runs; anything else is drawn in its own cells so fallback fonts can't shift the grid.
                bool ascii = width == 1 && (c.Ch == 0 || (c.Ch >= 0x20 && c.Ch < 0x7F));
                if (run.Length > 0 && (!ascii || fg != runFg || bg != runBg || ((c.Attr ^ runAttr) & fontAttrs) != 0))
                {
                    DrawText(g, run.ToString(), runX, top, run.Length, runFg, runBg, runAttr);
                    run.Length = 0;
                }
                if (ascii)
                {
                    if (run.Length == 0) { runX = x; runFg = fg; runBg = bg; runAttr = c.Attr; }
                    run.Append(c.Ch == 0 ? ' ' : (char)c.Ch);
                }
                else DrawText(g, char.ConvertFromUtf32(c.Ch), x, top, width, fg, bg, c.Attr);
            }
            if (run.Length > 0) DrawText(g, run.ToString(), runX, top, run.Length, runFg, runBg, runAttr);
        }

        void DrawText(Graphics g, string text, int col, int top, int cells, int fg, int bg, int attr)
        {
            Rectangle r = new Rectangle(padX + col * cw, top, cells * cw, ch);
            if (bg != back) using (SolidBrush b = new SolidBrush(Color.FromArgb(bg))) g.FillRectangle(b, r);
            if (text.Trim().Length > 0)
                TextRenderer.DrawText(g, text, (attr & VtScreen.Bold) != 0 ? boldFont : font, r.Location, Color.FromArgb(fg),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if ((attr & (VtScreen.Underline | VtScreen.Strike)) != 0)
                using (Pen p = new Pen(Color.FromArgb(fg), Math.Max(1, S(1))))
                {
                    if ((attr & VtScreen.Underline) != 0) g.DrawLine(p, r.Left, r.Bottom - S(2), r.Right - 1, r.Bottom - S(2));
                    if ((attr & VtScreen.Strike) != 0) g.DrawLine(p, r.Left, r.Top + ch / 2, r.Right - 1, r.Top + ch / 2);
                }
        }

        void DrawCursor(Graphics g)
        {
            int x = screen.CursorX, y = screen.CursorY;
            int index = screen.LineCount - screen.Rows + y;
            Cell[] cells = screen.GetLine(index).Cells;
            Cell c = x < cells.Length ? cells[x] : new Cell();
            int width = x + 1 < cells.Length && cells[x + 1].Ch == -1 ? 2 : 1;
            Rectangle r = new Rectangle(padX + x * cw, padY + y * ch, width * cw, ch);
            if (Focused)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(fore))) g.FillRectangle(b, r);
                if (c.Ch > 0)
                    TextRenderer.DrawText(g, char.ConvertFromUtf32(c.Ch), (c.Attr & VtScreen.Bold) != 0 ? boldFont : font,
                        r.Location, Color.FromArgb(back), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            else
                using (Pen p = new Pen(Color.FromArgb(fore))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        // ---------------------------------------------------------------- IME

        // Opens the IME's composition window at the cursor instead of the window's corner.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_IME_STARTCOMPOSITION && screen != null)
            {
                IntPtr imc = ImmGetContext(Handle);
                if (imc != IntPtr.Zero)
                {
                    COMPOSITIONFORM form = new COMPOSITIONFORM();
                    form.dwStyle = CFS_POINT;
                    form.x = padX + screen.CursorX * cw;
                    form.y = padY + screen.CursorY * ch;
                    ImmSetCompositionWindow(imc, ref form);
                    ImmReleaseContext(Handle, imc);
                }
            }
            base.WndProc(ref m);
        }

        const int WM_IME_STARTCOMPOSITION = 0x10D, CFS_POINT = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct COMPOSITIONFORM { public int dwStyle, x, y, left, top, right, bottom; }

        [DllImport("imm32.dll")]
        static extern IntPtr ImmGetContext(IntPtr hwnd);

        [DllImport("imm32.dll")]
        static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr imc);

        [DllImport("imm32.dll")]
        static extern bool ImmSetCompositionWindow(IntPtr imc, ref COMPOSITIONFORM form);
    }

    // ================================================================ floating window

    // The terminal as a floating window over the page: a header (TERMINAL, kill, maximize, fold) over the terminal.
    // Drag the header to move it, drag any edge or corner to resize it, double-click the header to maximize it.
    // Its place is kept as a gap from the page's bottom-right corner, so it stays put when the browser resizes.
    sealed class TerminalPanel : Control
    {
        [Flags]
        enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

        readonly Theme theme;
        readonly float scale;
        readonly Font labelFont = new Font("Segoe UI", 8.25f);
        readonly Action<Control, string> setTip;
        public readonly TerminalView View;
        public readonly IconButton KillButton, MaximizeButton, HideButton;
        public Func<Rectangle> Area = delegate { return Rectangle.Empty; }; // the page area it floats over, in parent coordinates
        public Size RestoreSize { get; set; } // the size chosen by resizing; empty = default
        public Point Offset { get; set; }     // gap between the window's bottom-right corner and the area's
        bool maximized, moving;
        Edge resizing;
        Point dragStart;
        Rectangle dragBounds;

        public TerminalPanel(Theme theme, float scale, Action<Control, string> setTip)
        {
            this.theme = theme;
            this.scale = scale;
            this.setTip = setTip;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = theme.Bar;
            Offset = new Point(S(16), S(16));
            View = new TerminalView(theme, scale);
            Controls.Add(View);
            KillButton = HeaderButton(Icons.Delete, "Kill terminal");
            MaximizeButton = HeaderButton(Icons.ChevronUp, "Maximize");
            HideButton = HeaderButton(Icons.ChevronDown, "Fold into the button (Ctrl+`)");
            MaximizeButton.Click += delegate { Maximized = !Maximized; };
        }

        int S(int px) { return (int)Math.Round(px * scale); }
        int HeaderHeight { get { return S(30); } }
        int Frame { get { return Math.Max(4, S(5)); } } // the resize border around the terminal
        int MinWidth { get { return S(260); } }
        int MinHeight { get { return S(120); } }

        static int Clamp(int v, int min, int max) { return v < min ? min : v > max ? max : v; }

        IconButton HeaderButton(string symbol, string tip)
        {
            IconButton b = new IconButton(theme, symbol);
            b.Round = true;
            b.Size = new Size(S(26), S(24));
            setTip(b, tip);
            Controls.Add(b);
            return b;
        }

        public bool Maximized
        {
            get { return maximized; }
            set
            {
                maximized = value;
                MaximizeButton.Symbol = value ? Icons.ChevronDown : Icons.ChevronUp;
                setTip(MaximizeButton, value ? "Restore size" : "Maximize");
                Fit();
            }
        }

        // Places the window inside the page area; called whenever the area or the window changes.
        public void Fit()
        {
            Rectangle a = Area();
            if (a.Width <= 0 || a.Height <= 0) return; // minimized, or no room yet
            Rectangle r = a;
            if (!maximized)
            {
                Size want = RestoreSize.Width > 0 && RestoreSize.Height > 0 ? RestoreSize : new Size(S(640), S(360));
                // RestoreSize itself stays as chosen, so a larger window comes back when the browser grows again.
                int w = Clamp(want.Width, Math.Min(MinWidth, a.Width), a.Width);
                int h = Clamp(want.Height, Math.Min(MinHeight, a.Height), a.Height);
                int right = Clamp(a.Right - Offset.X, a.Left + w, a.Right);
                int bottom = Clamp(a.Bottom - Offset.Y, a.Top + h, a.Bottom);
                r = new Rectangle(right - w, bottom - h, w, h);
            }
            if (Bounds != r) Bounds = r;
        }

        Edge EdgeAt(Point p)
        {
            if (maximized) return Edge.None;
            int f = Frame, corner = S(14);
            Edge e = Edge.None;
            if (p.X < f) e |= Edge.Left; else if (p.X >= Width - f) e |= Edge.Right;
            if (p.Y < f) e |= Edge.Top; else if (p.Y >= Height - f) e |= Edge.Bottom;
            // Near a corner, the side edges count as the corner too, so corners are easy to grab.
            if (e == Edge.Left || e == Edge.Right)
            {
                if (p.Y < corner) e |= Edge.Top; else if (p.Y >= Height - corner) e |= Edge.Bottom;
            }
            else if (e == Edge.Top || e == Edge.Bottom)
            {
                if (p.X < corner) e |= Edge.Left; else if (p.X >= Width - corner) e |= Edge.Right;
            }
            return e;
        }

        static Cursor CursorFor(Edge e)
        {
            switch (e)
            {
                case Edge.Left: case Edge.Right: return Cursors.SizeWE;
                case Edge.Top: case Edge.Bottom: return Cursors.SizeNS;
                case Edge.Left | Edge.Top: case Edge.Right | Edge.Bottom: return Cursors.SizeNWSE;
                case Edge.Right | Edge.Top: case Edge.Left | Edge.Bottom: return Cursors.SizeNESW;
                default: return Cursors.Default;
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (HideButton == null) return; // layout fires while the constructor is still adding children
            int bw = S(26), bh = S(24), y = (HeaderHeight - bh) / 2 + S(1), r = Width - Frame - S(2);
            HideButton.SetBounds(r -= bw, y, bw, bh);
            MaximizeButton.SetBounds(r -= bw, y, bw, bh);
            KillButton.SetBounds(r -= bw, y, bw, bh);
            View.SetBounds(Frame, HeaderHeight, Math.Max(0, Width - Frame * 2), Math.Max(0, Height - HeaderHeight - Frame));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(theme.Bar);
            using (Pen p = new Pen(theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            const string label = "TERMINAL";
            int x = S(14), labelW = TextRenderer.MeasureText(g, label, labelFont, Size.Empty, flags).Width;
            Rectangle text = new Rectangle(x, S(1), labelW, HeaderHeight - S(1));
            TextRenderer.DrawText(g, label, labelFont, text, theme.Fore, flags);
            using (SolidBrush b = new SolidBrush(theme.Accent))
                g.FillRectangle(b, x, HeaderHeight - S(6), labelW, Math.Max(1, S(1)));
            if (View.Running)
            {
                int right = KillButton.Left - S(8), left = x + labelW + S(14);
                if (right > left)
                    TextRenderer.DrawText(g, View.ShellName, labelFont, new Rectangle(left, S(1), right - left, HeaderHeight - S(1)),
                        theme.Muted, flags | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Edge edge = EdgeAt(e.Location);
            if (edge == Edge.None && e.Clicks == 2) { Maximized = !Maximized; return; }
            if (edge == Edge.None && (maximized || e.Y >= HeaderHeight)) return;
            resizing = edge;
            moving = edge == Edge.None;
            dragStart = MousePosition;
            dragBounds = Bounds;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!moving && resizing == Edge.None)
            {
                Cursor = CursorFor(EdgeAt(e.Location));
                return;
            }
            int dx = MousePosition.X - dragStart.X, dy = MousePosition.Y - dragStart.Y;
            int left = dragBounds.Left, top = dragBounds.Top, right = dragBounds.Right, bottom = dragBounds.Bottom;
            if (moving) { left += dx; right += dx; top += dy; bottom += dy; }
            if ((resizing & Edge.Left) != 0) left = Math.Min(left + dx, right - MinWidth);
            if ((resizing & Edge.Right) != 0) right = Math.Max(right + dx, left + MinWidth);
            if ((resizing & Edge.Top) != 0) top = Math.Min(top + dy, bottom - MinHeight);
            if ((resizing & Edge.Bottom) != 0) bottom = Math.Max(bottom + dy, top + MinHeight);
            Rectangle a = Area();
            if (!moving) RestoreSize = new Size(right - left, bottom - top);
            Offset = new Point(a.Right - right, a.Bottom - bottom);
            Fit();
            Offset = new Point(a.Right - Right, a.Bottom - Bottom); // forget any part of the drag that went past the edge
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            moving = false;
            resizing = Edge.None;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) labelFont.Dispose();
            base.Dispose(disposing);
        }
    }

    // ================================================================ launcher

    // The round button in the page's bottom-right corner that unfolds into the terminal window.
    sealed class TerminalLauncher : Control
    {
        readonly Theme theme;
        readonly float scale;
        bool hover, pressed;

        public TerminalLauncher(Theme theme, float scale)
        {
            this.theme = theme;
            this.scale = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Cursor = Cursors.Hand;
            AccessibleName = "Terminal";
            AccessibleRole = AccessibleRole.PushButton;
            Size = new Size(S(22), S(22));
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // A window region clips the native control, so the page shows around the circle.
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            using (System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath())
            {
                p.AddEllipse(0, 0, Width, Height);
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color fill = pressed ? Mix(theme.Accent, Color.Black, 0.15f) : hover ? Mix(theme.Accent, Color.White, 0.15f) : theme.Accent;
            g.Clear(fill);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // A ">_" prompt.
            float u = Width / 44f, cx = Width / 2f, cy = Height / 2f;
            using (Pen pen = new Pen(theme.AccentFore, Math.Max(1.5f, 2.2f * u)))
            {
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                g.DrawLines(pen, new[] { new PointF(cx - 9 * u, cy - 6 * u), new PointF(cx - 3 * u, cy), new PointF(cx - 9 * u, cy + 6 * u) });
                g.DrawLine(pen, cx + 1 * u, cy + 6 * u, cx + 9 * u, cy + 6 * u);
            }
        }
    }
}
