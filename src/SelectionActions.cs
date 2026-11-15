using System;

namespace Fedatather
{
    // Selecting text on a page shows a small floating bar next to it: Translate (Google Translate, into Chinese) and
    // Ask AI, which then offers ChatGPT, Claude or Gemini. The bar lives in the page (a closed shadow root, so the
    // page's styles can't reach it) and tells the browser what was picked with a web message.
    static class SelectionActions
    {
        const int MaxTranslate = 5000; // Google Translate's own limit
        const int MaxQuestion = 4000;  // keeps the address well under the length sites accept

        // The address that opens the chosen service with the selected text, or null for an unknown action.
        public static string UrlFor(string action, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return null;
            switch (action)
            {
                case "translate": return "https://translate.google.com/?sl=auto&tl=zh-CN&op=translate&text=" + Escape(text, MaxTranslate);
                case "chatgpt": return "https://chatgpt.com/?q=" + Escape(text, MaxQuestion);
                case "claude": return "https://claude.ai/new?q=" + Escape(text, MaxQuestion);
                case "gemini": return "https://gemini.google.com/app?q=" + Escape(text, MaxQuestion);
                default: return null;
            }
        }

        // Gemini may not fill in the question from its address, so its text also goes to the clipboard to paste.
        public static bool CopiesText(string action) { return action == "gemini"; }

        public static string Clip(string text, int max)
        {
            if (text.Length <= max) return text;
            int n = max;
            if (char.IsHighSurrogate(text[n - 1])) n--; // never split a character in two
            return text.Substring(0, n);
        }

        static string Escape(string text, int max) { return Uri.EscapeDataString(Clip(text, max)); }

        public static readonly string Script = Embedded.Text("selection.js");
    }
}
