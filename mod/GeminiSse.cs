using System;
using System.IO;
using System.Text;

namespace SWYF.ThaiMod
{
    // Reads Gemini's streamGenerateContent?alt=sse response as it arrives: lines "data: {json}", each event carrying
    // base64 16-bit PCM in candidates[].content.parts[].inlineData.data. No Unity types here, so it can be tested alone.
    internal sealed class GeminiSse
    {
        private readonly MemoryStream line = new MemoryStream();
        private readonly Action<byte[]> onPcm;
        private bool first = true;
        public volatile int Rate = 24000;
        public string LastError;

        public GeminiSse(Action<byte[]> onPcm) { this.onPcm = onPcm; }

        public void Feed(byte[] data, int count)
        {
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                if (data[i] != (byte)'\n') continue;
                line.Write(data, start, i - start);
                Line();
                start = i + 1;
            }
            if (start < count) line.Write(data, start, count - start);
        }

        // end of the response: an event without a trailing newline
        public void End() { if (line.Length > 0) Line(); }

        private void Line()
        {
            string s = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
            line.SetLength(0);
            if (s.StartsWith("data:", StringComparison.Ordinal)) Event(s.Substring(5));
        }

        public void Event(string json)
        {
            int k = Find(json, 0);
            if (k < 0)
            {
                if (json.IndexOf("\"error\"", StringComparison.Ordinal) >= 0) LastError = json.Length > 300 ? json.Substring(0, 300) : json;
                return;
            }
            int r = json.IndexOf("rate=", StringComparison.Ordinal);
            if (r > 0)
            {
                int e = r + 5;
                while (e < json.Length && char.IsDigit(json[e])) e++;
                if (int.TryParse(json.Substring(r + 5, e - r - 5), out int rate) && rate >= 8000 && rate <= 48000) Rate = rate;
            }
            while (k >= 0)
            {
                int d = json.IndexOf("\"data\"", k, StringComparison.Ordinal);
                if (d < 0) return;
                int colon = json.IndexOf(':', d + 6);
                int q = colon < 0 ? -1 : json.IndexOf('"', colon + 1);
                int end = q < 0 ? -1 : json.IndexOf('"', q + 1);
                if (end < 0) return;
                byte[] pcm = Convert.FromBase64String(Unescape64(json.Substring(q + 1, end - q - 1)));
                if (first)
                {
                    first = false;
                    int rate = Rate;
                    pcm = StripWav(pcm, ref rate);
                    Rate = rate;
                }
                if (pcm.Length > 0) onPcm(pcm);
                k = Find(json, end + 1);
            }
        }

        private static int Find(string json, int from)
        {
            int a = json.IndexOf("\"inlineData\"", from, StringComparison.Ordinal);
            int b = json.IndexOf("\"inline_data\"", from, StringComparison.Ordinal);
            return a < 0 ? b : b < 0 ? a : Math.Min(a, b);
        }

        // JSON may escape '=', '+' or '/' inside the base64 text
        public static string Unescape64(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            return s.Replace("\\u003d", "=").Replace("\\u003D", "=").Replace("\\u002b", "+").Replace("\\u002B", "+").Replace("\\/", "/");
        }

        // Gemini 3.8 returns a WAV file for whole-line requests (raw PCM when streaming). Drop the header, keep the rate.
        public static byte[] StripWav(byte[] b, ref int rate)
        {
            if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
                return b;
            int p = 12;
            while (p + 8 <= b.Length)
            {
                uint size = (uint)(b[p + 4] | b[p + 5] << 8 | b[p + 6] << 16 | b[p + 7] << 24);
                bool isFmt = b[p] == 'f' && b[p + 1] == 'm' && b[p + 2] == 't';
                bool isData = b[p] == 'd' && b[p + 1] == 'a' && b[p + 2] == 't' && b[p + 3] == 'a';
                if (isFmt && p + 16 <= b.Length)
                {
                    int r = b[p + 12] | b[p + 13] << 8 | b[p + 14] << 16 | b[p + 15] << 24;
                    if (r >= 8000 && r <= 48000) rate = r;
                }
                if (isData)
                {
                    int start = p + 8;
                    long len = Math.Min((long)size, b.Length - start); // streaming WAVs may say 0xFFFFFFFF
                    if (size == 0) len = b.Length - start;
                    var res = new byte[len];
                    Buffer.BlockCopy(b, start, res, 0, (int)len);
                    return res;
                }
                long next = p + 8L + size + (size & 1);
                if (next > b.Length) break;
                p = (int)next;
            }
            if (b.Length <= 44) return new byte[0];
            var tail = new byte[b.Length - 44];
            Buffer.BlockCopy(b, 44, tail, 0, tail.Length);
            return tail;
        }
    }
}
