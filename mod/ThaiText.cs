using System;
using System.Collections.Generic;
using System.IO;
using Kolkata.Speech;

namespace SWYF.ThaiMod
{
    // Thai for text the game hard-codes in English (not in its localization tables).
    // The launcher patches the game so that:
    //   - each listed English literal goes through T() right where the game loads it,
    //   - LanguageManager's table lookup calls Missing() when an English key has no table entry,
    //   - CallerResponseFillers.Get calls Filler() first.
    public static class ThaiText
    {
        private static Dictionary<string, string> map;
        private static readonly object Gate = new object();
        private static volatile int uiState; // 0 unknown, 1 Thai, 2 other

        internal static void SetUi(bool thai) { uiState = thai ? 1 : 2; }

        private static bool UiThai()
        {
            int s = uiState;
            if (s == 0)
            {
                try { s = LanguageManager.EffectiveCode == "th" ? 1 : 2; uiState = s; }
                catch { return false; }
            }
            return s == 1;
        }

        private static Dictionary<string, string> Map()
        {
            var m = map;
            if (m != null) return m;
            lock (Gate)
            {
                if (map != null) return map;
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                string dir = ThaiModEntry.ModDir;
                if (dir == null) return d; // too early; try again later
                try
                {
                    string path = Path.Combine(dir, "ui_th.tsv");
                    if (File.Exists(path))
                        foreach (var raw in File.ReadAllText(path).Split('\n'))
                        {
                            string line = raw.TrimEnd('\r');
                            if (line.Length == 0 || line[0] == '#') continue;
                            int tab = line.IndexOf('\t');
                            if (tab <= 0) continue;
                            d[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1));
                        }
                    ThaiModEntry.Log("hard-coded text: " + d.Count + " Thai entries");
                }
                catch (Exception e) { ThaiModEntry.Warn("ui_th.tsv: " + e.Message); }
                map = d;
                return d;
            }
        }

        // Patched in after selected ldstr instructions.
        public static string T(string s)
        {
            if (string.IsNullOrEmpty(s) || !UiThai()) return s;
            try
            {
                var m = Map();
                if (m.Count == 0) return s;
                if (m.TryGetValue(s, out var th)) return th;
                string t = s.Trim();
                if (t.Length == 0 || t.Length == s.Length || !m.TryGetValue(t, out th)) return s;
                int lead = s.Length - s.TrimStart().Length;
                int trail = s.Length - s.TrimEnd().Length;
                return s.Substring(0, lead) + th + s.Substring(s.Length - trail);
            }
            catch { return s; }
        }

        // LanguageManager.GetCachedLocalizedText: result == key means "no table entry".
        public static string Missing(string result, string key)
        {
            if (result == null || key == null || !string.Equals(result, key, StringComparison.Ordinal)) return result;
            return T(key);
        }

        // CallerResponseFillers.Get: short "thinking" sounds while the AI answers.
        public static string Filler(string languageCode, int variant)
        {
            try
            {
                var lang = CallLanguage.Resolve(languageCode);
                if (lang == null || lang.Code != "th") return null;
            }
            catch { return null; }
            switch (variant)
            {
                case 0: return "อืม ได้...";
                case 1: return "อืม เข้าใจแล้ว...";
                default: return "เอ่อ โอเค...";
            }
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char e = s[++i];
                sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e);
            }
            return sb.ToString();
        }
    }
}
