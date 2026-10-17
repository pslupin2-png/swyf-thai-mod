using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Whisper;

namespace SWYF.ThaiMod
{
    // Thai speech recognition through Groq's hosted Whisper large-v3 (free API key from console.groq.com).
    // com.whisper.unity.dll is patched so WhisperManager.GetTextAsync(float[],int,int) first calls TryTranscribe;
    // a non-null Task replaces the local whisper pass. Without a key, or for other languages, nothing changes.
    public static class ThaiCloudStt
    {
        private const string Endpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
        private const string Model = "whisper-large-v3";
        private const int ThaiLanguageId = 30; // whisper.cpp language id for "th"

        [ThreadStatic] private static bool bypass;
        private static string keyPath;
        private static string cachedKey;
        private static DateTime keyStamp = DateTime.MinValue;
        private static DateTime authFailedStamp = DateTime.MaxValue; // key file version Groq rejected (401/403)
        private static DateTime cooldownUntil = DateTime.MinValue;
        private static readonly object KeyGate = new object();

        public static void Setup()
        {
            keyPath = Path.Combine(ThaiModEntry.ModDir, "groq_key.txt");
            if (!File.Exists(keyPath))
            {
                Directory.CreateDirectory(ThaiModEntry.ModDir);
                File.WriteAllText(keyPath, "# Paste your Groq API key (starts with gsk_) on the line below, save, done.\n# วางคีย์ Groq (ขึ้นต้นด้วย gsk_) ในบรรทัดล่างนี้ แล้วกดบันทึก\n\n", new UTF8Encoding(false));
            }
            ThaiModEntry.Log("Groq speech recognition " + (HasKey ? "enabled" : "off (no key in groq_key.txt)"));
        }

        public static bool HasKey { get { string k = Key; return !string.IsNullOrEmpty(k) && keyStamp != authFailedStamp; } }

        private static string Key
        {
            get
            {
                lock (KeyGate)
                {
                    try
                    {
                        if (keyPath == null || !File.Exists(keyPath)) return null;
                        var stamp = File.GetLastWriteTimeUtc(keyPath);
                        if (stamp != keyStamp)
                        {
                            keyStamp = stamp;
                            cachedKey = null;
                            foreach (var raw in File.ReadAllText(keyPath).Split('\n'))
                            {
                                var line = raw.Trim();
                                if (line.Length > 10 && !line.StartsWith("#")) { cachedKey = line; break; }
                            }
                        }
                        return cachedKey;
                    }
                    catch { return null; }
                }
            }
        }

        // The caller's last line, given to Whisper as context ("prompt") so topic words are spelled right.
        private static string lastCallerLine;
        private static long lastCallerTicks;

        public static void NoteCallerLine(string text)
        {
            if (string.IsNullOrEmpty(text) || ThaiText.IsFiller(text)) return;
            string t = text.Trim();
            if (t.Length > 150)
            {
                int cut = t.IndexOf(' ', t.Length - 150); // keep the end (the question the player answers), from a word boundary
                t = cut >= 0 && cut < t.Length - 20 ? t.Substring(cut + 1) : t.Substring(t.Length - 150);
            }
            lastCallerLine = t;
            System.Threading.Interlocked.Exchange(ref lastCallerTicks, DateTime.UtcNow.Ticks);
        }

        private static string Context()
        {
            string t = lastCallerLine;
            if (t == null) return null;
            if (DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref lastCallerTicks) > TimeSpan.FromMinutes(2).Ticks) return null;
            return t;
        }

        private sealed class Job
        {
            public WhisperManager Mgr; public float[] Samples; public int Freq, Ch; public byte[] Body; public string Boundary, Key;
            public double Seconds; public DateTime Started; public string Prompt, Level;
            public TaskCompletionSource<WhisperResult> Tcs = new TaskCompletionSource<WhisperResult>();
        }
        private static readonly Queue<Job> pending = new Queue<Job>();

        // Patched into the start of WhisperManager.GetTextAsync(float[] samples, int frequency, int channels).
        public static Task<WhisperResult> TryTranscribe(WhisperManager mgr, float[] samples, int frequency, int channels)
        {
            if (bypass || mgr == null || samples == null || mgr.language != "th") return null;
            ThaiVoices.Warm("player spoke"); // the caller's answer is next: open the voice connection now
            if (DateTime.UtcNow < cooldownUntil) return null;
            if (!HasKey) return null;
            string key = Key;
            var job = new Job { Mgr = mgr, Samples = samples, Freq = frequency, Ch = channels, Key = key,
                                Seconds = samples.Length / (double)Math.Max(1, frequency * Math.Max(1, channels)) };
            job.Boundary = "----swyf" + Guid.NewGuid().ToString("N");
            float peak = 0f; double sum = 0; int clipped = 0;
            foreach (float v in samples) { float a = v < 0 ? -v : v; if (a > peak) peak = a; sum += v * v; if (a >= 0.99f) clipped++; }
            double rms = Math.Sqrt(sum / Math.Max(1, samples.Length));
            job.Level = "peak " + peak.ToString("0.00") + ", rms " + rms.ToString("0.000") + (clipped > 0 ? ", clipped " + (100.0 * clipped / samples.Length).ToString("0.0") + "%" : "");
            // Whisper misses a first syllable that starts right at the edge of the clip: add 0.3 s of quiet on both sides.
            job.Prompt = rms > 0.004 && job.Seconds >= 0.6 ? Context() : null; // no context for near-silent clips (Whisper may echo it)
            job.Body = BuildBody(job.Boundary, EncodeWav(Pad(samples, frequency, channels, 0.3), frequency, channels), job.Prompt);
            lock (pending) pending.Enqueue(job);
            return job.Tcs.Task;
        }

        // Main thread (ThaiModBehaviour.Update): start queued uploads with UnityWebRequest.
        public static void Pump(MonoBehaviour host)
        {
            while (true)
            {
                Job job;
                lock (pending) { if (pending.Count == 0) return; job = pending.Dequeue(); }
                host.StartCoroutine(Send(job));
            }
        }

        private static System.Collections.IEnumerator Send(Job job)
        {
            job.Started = DateTime.UtcNow;
            var req = new UnityWebRequest(Endpoint, "POST");
            req.uploadHandler = new UploadHandlerRaw(job.Body);
            req.uploadHandler.contentType = "multipart/form-data; boundary=" + job.Boundary;
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", "Bearer " + job.Key);
            req.timeout = 15;
            yield return req.SendWebRequest();
            string body = req.downloadHandler != null ? req.downloadHandler.text : null;
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string text = ExtractText(body ?? "");
                    if (job.Prompt != null && Same(text, job.Prompt)) { ThaiModEntry.Warn("Groq echoed the context instead of hearing speech; ignored"); text = ""; }
                    ThaiModEntry.Log("Groq " + job.Seconds.ToString("0.0") + " s audio (" + job.Level + (job.Prompt != null ? ", with context" : "") + ") -> " +
                                     (DateTime.UtcNow - job.Started).TotalSeconds.ToString("0.00") + " s: " + text);
                    var seg = new WhisperSegment(0, text, 0UL, (ulong)(job.Seconds * 100));
                    job.Tcs.TrySetResult(new WhisperResult(new List<WhisperSegment> { seg }, ThaiLanguageId));
                }
                catch (Exception e) { Fallback(job, e.Message); }
            }
            else
            {
                string err = (body ?? "").Length > 300 ? body.Substring(0, 300) : body;
                Fallback(job, req.responseCode + " " + req.error + " " + err);
            }
            req.Dispose();
        }

        private static void Fallback(Job job, string why)
        {
            if (why != null && (why.StartsWith("401") || why.StartsWith("403")))
            {
                authFailedStamp = keyStamp;
                ThaiModEntry.Warn("Groq rejected the key in groq_key.txt (invalid or deleted). Using the local large Whisper model until the key file changes.");
            }
            ThaiModEntry.Warn("Groq failed (" + why + "); using local whisper for 30 s");
            cooldownUntil = DateTime.UtcNow.AddSeconds(30);
            try
            {
                Task<WhisperResult> t;
                bypass = true;
                try { t = job.Mgr.GetTextAsync(job.Samples, job.Freq, job.Ch); }
                finally { bypass = false; }
                t.ContinueWith(x =>
                {
                    if (x.IsFaulted) job.Tcs.TrySetException(x.Exception.InnerException ?? x.Exception);
                    else if (x.IsCanceled) job.Tcs.TrySetCanceled();
                    else job.Tcs.TrySetResult(t.Result);
                });
            }
            catch (Exception e) { job.Tcs.TrySetException(e); }
        }

        private static float[] Pad(float[] samples, int frequency, int channels, double seconds)
        {
            int pad = (int)(Math.Max(1, frequency) * seconds) * Math.Max(1, channels);
            var res = new float[samples.Length + 2 * pad];
            Array.Copy(samples, 0, res, pad, samples.Length);
            return res;
        }

        private static byte[] BuildBody(string boundary, byte[] wav, string prompt)
        {
            var body = new MemoryStream();
            void Field(string name, string value)
            {
                Write(body, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n\r\n" + value + "\r\n");
            }
            Field("model", Model);
            Field("language", "th");
            Field("temperature", "0");
            Field("response_format", "json");
            if (!string.IsNullOrEmpty(prompt)) Field("prompt", prompt);
            Write(body, "--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"speech.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
            body.Write(wav, 0, wav.Length);
            Write(body, "\r\n--" + boundary + "--\r\n");
            return body.ToArray();
        }

        private static void Write(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        private static byte[] EncodeWav(float[] samples, int rate, int channels)
        {
            int n = samples.Length;
            var ms = new MemoryStream(44 + n * 2);
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels);
            w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                float v = samples[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                w.Write((short)(v * 32767f));
            }
            w.Flush();
            return ms.ToArray();
        }

        private static bool Same(string a, string b)
        {
            string x = (a ?? "").Replace(" ", "").TrimEnd('.', '?', '!'), y = (b ?? "").Replace(" ", "").TrimEnd('.', '?', '!');
            return x.Length > 0 && (x == y || (x.Length >= 12 && y.EndsWith(x, StringComparison.Ordinal)));
        }

        // Minimal JSON: return the value of the top-level "text" string.
        private static string ExtractText(string json)
        {
            int k = json.IndexOf("\"text\"", StringComparison.Ordinal);
            if (k < 0) throw new IOException("no text in response: " + (json.Length > 200 ? json.Substring(0, 200) : json));
            int i = json.IndexOf(':', k) + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return "";
            var sb = new StringBuilder();
            for (i++; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') break;
                if (c != '\\' || i + 1 >= json.Length) { sb.Append(c); continue; }
                char e = json[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < json.Length) { sb.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16)); i += 4; }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString().Trim();
        }
    }
}
