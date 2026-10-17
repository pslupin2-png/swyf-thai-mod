using System;
using System.Text;

namespace SWYF.ThaiMod
{
    // Turns an AI-written line into text a Thai voice reads cleanly. Only the audio uses this;
    // subtitles keep the original line. No Unity or regex dependencies (the game's System.dll is stripped).
    public static class ThaiSpeechText
    {
        public static bool IsThaiChar(char c) => c >= '฀' && c <= '๿';

        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = DropDelimited(s, '*', '*', 60);   // *sighs*
            s = DropDelimited(s, '[', ']', 60);   // [laughs]
            s = Laughs(s);                         // before commas in numbers are dropped

            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsSurrogate(c)) continue;                                   // emoji and other astral symbols
                if ((c >= '☀' && c <= '➿') || c == '️' || c == '‍' || c == '​') continue;
                if (c == '*' || c == '_' || c == '~' || c == '`' || c == '#' || c == '"' || c == '“' || c == '”') continue;
                if (c >= 'Ѐ' && c <= 'ӿ') { sb.Append(Cyrillic(c)); continue; } // "Франклин" slips from the AI
                if (c == '&') { sb.Append(" และ "); continue; }
                if (c == '%') { sb.Append(" เปอร์เซ็นต์ "); continue; }
                if (c == ',' && i > 0 && i + 1 < s.Length && char.IsDigit(s[i - 1]) && char.IsDigit(s[i + 1])) continue; // 1,000 -> 1000
                if (c == '.')
                {
                    bool dots = (i > 0 && s[i - 1] == '.') || (i + 1 < s.Length && s[i + 1] == '.');
                    bool afterThai = i > 0 && IsThaiChar(s[i - 1]);
                    bool initial = i > 0 && s[i - 1] >= 'A' && s[i - 1] <= 'Z' && (i == 1 || !char.IsLetter(s[i - 2]));
                    if (!dots && (afterThai || initial)) { sb.Append(' '); continue; } // "ดี. ไฮเปอร์" / "D. Hyper" read as one name
                }
                if ((c == '!' || c == '?') && i > 0 && s[i - 1] == c) continue;    // "!!!" -> "!"
                sb.Append(c);
            }
            string t = Currency(sb.ToString(), '$', "ดอลลาร์");
            t = Currency(t, '฿', "บาท");
            return Squeeze(t);
        }

        // Splits a long line at a space so the first phrase can start playing early.
        public static bool SplitFirst(string s, int minFirst, int maxFirst, int minTotal, out string first, out string rest)
        {
            first = s; rest = "";
            if (s == null || s.Length < minTotal) return false;
            int cut = -1;
            for (int i = minFirst; i < Math.Min(maxFirst, s.Length - 8); i++)
                if (s[i] == ' ') { cut = i; break; }
            if (cut < 0) return false;
            first = s.Substring(0, cut).Trim();
            rest = s.Substring(cut + 1).Trim();
            return first.Length > 0 && rest.Length > 0;
        }

        private static string DropDelimited(string s, char open, char close, int maxInner)
        {
            if (s.IndexOf(open) < 0) return s;
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == open)
                {
                    int j = s.IndexOf(close, i + 1);
                    if (j > i && j - i - 1 <= maxInner) { i = j + 1; sb.Append(' '); continue; }
                }
                sb.Append(s[i]); i++;
            }
            return sb.ToString();
        }

        // "$5000" / "5000$" -> "5000 ดอลลาร์"
        private static string Currency(string s, char sign, string word)
        {
            if (s.IndexOf(sign) < 0) return s;
            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != sign) { sb.Append(s[i]); continue; }
                if (i + 1 < s.Length && char.IsDigit(s[i + 1]))
                {
                    int j = i + 1;
                    while (j < s.Length && (char.IsDigit(s[j]) || (s[j] == '.' && j + 1 < s.Length && char.IsDigit(s[j + 1])))) j++;
                    sb.Append(s, i + 1, j - i - 1).Append(' ').Append(word).Append(' ');
                    i = j - 1;
                }
                else sb.Append(' ').Append(word).Append(' ');
            }
            return sb.ToString();
        }

        // Thai chat laughter "5555" -> "ฮ่า ฮ่า"
        private static string Laughs(string s)
        {
            if (s.IndexOf("555", StringComparison.Ordinal) < 0) return s;
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '5' && (i == 0 || "0123456789,.$฿".IndexOf(s[i - 1]) < 0))
                {
                    int j = i; while (j < s.Length && s[j] == '5') j++;
                    int k = j; while (k < s.Length && s[k] == ' ') k++;
                    bool endsHere = k == s.Length || !char.IsLetterOrDigit(s[k]);         // "555" alone, not "555 บาท"
                    bool numberGoesOn = j < s.Length && (char.IsDigit(s[j]) ||
                                        ((s[j] == '.' || s[j] == ',') && j + 1 < s.Length && char.IsDigit(s[j + 1])));
                    bool laugh = !numberGoesOn && (j - i >= 4 || (j - i == 3 && endsHere));
                    if (laugh) { sb.Append(" ฮ่า ฮ่า "); i = j; continue; }
                }
                sb.Append(s[i]); i++;
            }
            return sb.ToString();
        }

        private static string Squeeze(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool pendingSpace = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace && c != ',' && c != '!' && c != '?' && c != '.') sb.Append(' '); // no space before punctuation
                pendingSpace = false;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private static string Cyrillic(char c)
        {
            const string from = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
            string[] to = { "a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "kh", "ts", "ch", "sh", "shch", "", "y", "", "e", "yu", "ya" };
            char lower = char.ToLowerInvariant(c);
            int k = from.IndexOf(lower);
            if (k < 0) return "";
            string r = to[k];
            if (c != lower && r.Length > 0) r = char.ToUpperInvariant(r[0]) + r.Substring(1);
            return r;
        }
    }
}
