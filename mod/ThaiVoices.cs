using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace SWYF.ThaiMod
{
    // Picks a Thai voice per character (gender from the caller identity JSON) and, when a Gemini or OpenAI key is
    // present in voice_keys.txt, synthesizes with Gemini TTS or OpenAI gpt-4o-mini-tts (24 kHz PCM). Otherwise ThaiTts falls
    // back to the Windows voice with a gender-appropriate pitch.
    public static class ThaiVoices
    {
        public enum Gender { Unknown, Male, Female, Boss }

        private static readonly Dictionary<string, Gender> genderByVoice = new Dictionary<string, Gender>(StringComparer.OrdinalIgnoreCase);
        private static readonly object MapGate = new object();
        private static float nextMapRefresh;

        private static string cfgPath;
        private static DateTime cfgStamp = DateTime.MinValue;
        private static string geminiKey, openaiKey;
        private static string geminiModel = "";
        private static string workingGeminiModel;
        private static DateTime cooldownUntil = DateTime.MinValue;
        private static readonly object CfgGate = new object();
        private static readonly string[] GeminiModels = { "gemini-2.5-flash-preview-tts", "gemini-3.1-flash-tts-preview", "gemini-2.5-flash-tts" };

        public static void Setup()
        {
            cfgPath = Path.Combine(ThaiModEntry.ModDir, "voice_keys.txt");
            if (!File.Exists(cfgPath))
            {
                Directory.CreateDirectory(ThaiModEntry.ModDir);
                File.WriteAllText(cfgPath,
                    "# Natural Thai voices for callers and the boss / เสียงไทยธรรมชาติ\n" +
                    "# ใส่คีย์อย่างใดอย่างหนึ่ง (ถ้าใส่ทั้งคู่ จะใช้ Gemini ก่อน) แล้วกด Ctrl+S — ไม่ต้องปิดเกม\n" +
                    "# Gemini: aistudio.google.com > Get API key (เปิด billing ถ้าต้องการใช้เยอะ)\n" +
                    "# OpenAI: platform.openai.com > API keys (ต้องเติมเครดิตก่อน)\n" +
                    "gemini=\n" +
                    "openai=\n" +
                    "# gemini_model=gemini-2.5-flash-preview-tts\n", new UTF8Encoding(false));
            }
            ThaiModEntry.Log("cloud Thai voices: " + Provider);
        }

        private static void LoadCfg()
        {
            lock (CfgGate)
            {
                try
                {
                    if (cfgPath == null || !File.Exists(cfgPath)) { geminiKey = openaiKey = null; return; }
                    var stamp = File.GetLastWriteTimeUtc(cfgPath);
                    if (stamp == cfgStamp) return;
                    cfgStamp = stamp; geminiKey = openaiKey = null; geminiModel = ""; workingGeminiModel = null;
                    foreach (var raw in File.ReadAllText(cfgPath).Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                        if (k == "gemini" && v.Length >= 20) geminiKey = v;
                        if (k == "openai" && v.Length >= 20) openaiKey = v;
                        if (k == "gemini_model" && v.Length > 0) geminiModel = v;
                    }
                    cooldownUntil = DateTime.MinValue;
                }
                catch { }
            }
        }

        public static string Provider
        {
            get { LoadCfg(); return geminiKey != null ? "gemini" : openaiKey != null ? "openai" : "off (no key in voice_keys.txt)"; }
        }

        public static bool CloudUsable
        {
            get { LoadCfg(); return (geminiKey != null || openaiKey != null) && DateTime.UtcNow >= cooldownUntil; }
        }

        // ---------- gender map ----------

        private static string Stem(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.Replace('\\', '/');
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            return s.Replace(' ', '_').Replace('-', '_').ToLowerInvariant();
        }

        // Main thread, every ~30 s.
        public static void RefreshMap()
        {
            if (Time.unscaledTime < nextMapRefresh) return;
            nextMapRefresh = Time.unscaledTime + 30f;
            try
            {
                var all = CallerIdentityLibrary.LoadAll();
                if (all == null) return;
                lock (MapGate)
                {
                    foreach (var c in all)
                    {
                        if (c == null) continue;
                        Gender g = string.Equals(c.gender, "Female", StringComparison.OrdinalIgnoreCase) ? Gender.Female
                                 : string.Equals(c.gender, "Male", StringComparison.OrdinalIgnoreCase) ? Gender.Male : Gender.Unknown;
                        if (g == Gender.Unknown) continue;
                        if (!string.IsNullOrEmpty(c.voiceId)) genderByVoice[Stem(c.voiceId)] = g;
                        if (!string.IsNullOrEmpty(c.revealedVoiceId)) genderByVoice[Stem(c.revealedVoiceId)] = g;
                    }
                }
            }
            catch (Exception e) { ThaiModEntry.Warn("voice gender map: " + e.Message); }
        }

        public static Gender GenderFor(string voicePath)
        {
            if (string.IsNullOrEmpty(voicePath)) return Gender.Unknown;
            if (voicePath.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0) return Gender.Boss;
            lock (MapGate)
                return genderByVoice.TryGetValue(Stem(voicePath), out var g) ? g : Gender.Unknown;
        }

        public static int Hash(string s)
        {
            int h = 17;
            foreach (char c in s ?? "") h = h * 31 + c;
            return h & 0x7FFFFFFF;
        }

        // ---------- cloud synthesis (Gemini TTS / OpenAI gpt-4o-mini-tts) ----------

        private sealed class Job
        {
            public string Url, Body, AuthHeader, AuthValue, Kind;
            public TaskCompletionSource<string> Tcs = new TaskCompletionSource<string>(); // gemini: JSON text
            public TaskCompletionSource<byte[]> Raw = new TaskCompletionSource<byte[]>(); // openai: raw pcm
        }
        private static readonly Queue<Job> pending = new Queue<Job>();

        private static readonly string[] GeminiFemale = { "Kore", "Aoede", "Leda", "Despina", "Callirrhoe", "Autonoe", "Erinome", "Sulafat" };
        private static readonly string[] GeminiMale = { "Puck", "Charon", "Orus", "Iapetus", "Umbriel", "Algieba", "Schedar", "Achird" };
        private static readonly string[] OpenAiFemale = { "coral", "nova", "shimmer", "sage", "marin" };
        private static readonly string[] OpenAiMale = { "ash", "echo", "onyx", "cedar", "verse" };

        private static string Tone(string text, Gender g)
        {
            int bangs = 0, qs = 0;
            foreach (char c in text) { if (c == '!') bangs++; else if (c == '?') qs++; }
            string who = g == Gender.Boss ? "a loud, furious, bossy call-center boss yelling at his lazy staff"
                       : g == Gender.Female ? "a woman talking on the phone" : g == Gender.Male ? "a man talking on the phone" : "a person talking on the phone";
            string mood = bangs >= 2 ? "very agitated and loud" : bangs == 1 ? "emotional and energetic" : qs > 0 ? "suspicious and questioning" : "casual and natural";
            return who + ", " + mood + ", fluent native Thai pronunciation, quick conversational pace";
        }

        // Worker thread: blocks until the main thread fetched the audio. Returns 24 kHz mono floats or null.
        public static float[] SynthesizeCloud(string text, string voicePath, Gender gender)
        {
            LoadCfg();
            int h = Hash(voicePath);
            var job = new Job();
            if (geminiKey != null)
            {
                string voice = gender == Gender.Boss ? "Algenib" : gender == Gender.Female ? GeminiFemale[h % GeminiFemale.Length]
                             : gender == Gender.Male ? GeminiMale[h % GeminiMale.Length] : (h % 2 == 0 ? GeminiFemale : GeminiMale)[h % 8];
                string prompt = "Read this Thai line aloud as " + Tone(text, gender) + ". Say only the line itself:\n" + text;
                string model = workingGeminiModel ?? (geminiModel.Length > 0 ? geminiModel : GeminiModels[0]);
                job.Kind = "gemini";
                job.Url = "https://generativelanguage.googleapis.com/v1beta/models/" + model + ":generateContent";
                job.AuthHeader = "x-goog-api-key"; job.AuthValue = geminiKey;
                job.Body = "{\"contents\":[{\"parts\":[{\"text\":" + Json(prompt) + "}]}],\"generationConfig\":{\"responseModalities\":[\"AUDIO\"]," +
                           "\"speechConfig\":{\"voiceConfig\":{\"prebuiltVoiceConfig\":{\"voiceName\":\"" + voice + "\"}}}}}";
                lock (pending) pending.Enqueue(job);
                if (!job.Tcs.Task.Wait(20000)) { Fail("timeout"); return null; }
                string json = job.Tcs.Task.Result;
                if (json == null) return null;
                if (workingGeminiModel == null) workingGeminiModel = model;
                int rate = 24000;
                int r = json.IndexOf("rate=", StringComparison.Ordinal);
                if (r > 0) { int e = r + 5; while (e < json.Length && char.IsDigit(json[e])) e++; int.TryParse(json.Substring(r + 5, e - r - 5), out rate); }
                byte[] pcm = Convert.FromBase64String(JsonStringValue(json, "data"));
                return Resample(Pcm16(pcm), rate, 24000);
            }
            else
            {
                string voice = gender == Gender.Boss ? "onyx" : gender == Gender.Female ? OpenAiFemale[h % OpenAiFemale.Length]
                             : gender == Gender.Male ? OpenAiMale[h % OpenAiMale.Length] : "alloy";
                job.Kind = "openai";
                job.Url = "https://api.openai.com/v1/audio/speech";
                job.AuthHeader = "Authorization"; job.AuthValue = "Bearer " + openaiKey;
                job.Body = "{\"model\":\"gpt-4o-mini-tts\",\"voice\":\"" + voice + "\",\"response_format\":\"pcm\",\"input\":" + Json(text) +
                           ",\"instructions\":" + Json("Speak Thai like " + Tone(text, gender) + ".") + "}";
                lock (pending) pending.Enqueue(job);
                if (!job.Raw.Task.Wait(20000)) { Fail("timeout"); return null; }
                byte[] pcm = job.Raw.Task.Result;
                return pcm == null ? null : Pcm16(pcm);
            }
        }

        // ---------- OpenAI streaming (audio starts playing while the rest is still being generated) ----------

        public static bool CanStream { get { LoadCfg(); return geminiKey == null && openaiKey != null && DateTime.UtcNow >= cooldownUntil; } }

        private sealed class StreamJob
        {
            public string Url, Body, Auth;
            public BlockingCollection<byte[]> Chunks = new BlockingCollection<byte[]>();
            public volatile bool Failed, Abort;
        }
        private static readonly Queue<StreamJob> streamPending = new Queue<StreamJob>();

        private sealed class StreamSink : DownloadHandlerScript
        {
            private readonly StreamJob job;
            public StreamSink(StreamJob j) : base(new byte[16384]) { job = j; }
            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (dataLength <= 0) return true;
                var copy = new byte[dataLength];
                Buffer.BlockCopy(data, 0, copy, 0, dataLength);
                try { job.Chunks.Add(copy); } catch { }
                return true;
            }
        }

        // Worker thread. Calls onAudio with ~200 ms float blocks as they arrive. Returns false if nothing was received.
        public static bool StreamOpenAI(string text, string voicePath, Gender gender, Action<float[]> onAudio, CancellationToken ct)
        {
            LoadCfg();
            int h = Hash(voicePath);
            string voice = gender == Gender.Boss ? "onyx" : gender == Gender.Female ? OpenAiFemale[h % OpenAiFemale.Length]
                         : gender == Gender.Male ? OpenAiMale[h % OpenAiMale.Length] : "alloy";
            var job = new StreamJob
            {
                Url = "https://api.openai.com/v1/audio/speech",
                Auth = "Bearer " + openaiKey,
                Body = "{\"model\":\"gpt-4o-mini-tts\",\"voice\":\"" + voice + "\",\"response_format\":\"pcm\",\"input\":" + Json(text) +
                       ",\"instructions\":" + Json("Speak Thai like " + Tone(text, gender) + ".") + "}"
            };
            var started = DateTime.UtcNow;
            bool any = false; int carry = -1; var block = new List<float>(4800);
            for (int attempt = 1; attempt <= 2 && !any; attempt++)
            {
                if (attempt > 1)
                {
                    job = new StreamJob { Url = job.Url, Auth = job.Auth, Body = job.Body };
                    ThaiModEntry.Warn("openai voice slow to start; retrying");
                }
                lock (streamPending) streamPending.Enqueue(job);
                while (true)
                {
                    byte[] chunk;
                    bool got;
                    try { got = job.Chunks.TryTake(out chunk, any ? 8000 : 5000, ct); }
                    catch (OperationCanceledException) { job.Abort = true; throw; }
                    catch (InvalidOperationException) { break; } // request finished
                    if (!got)
                    {
                        if (job.Chunks.IsCompleted) break;
                        job.Abort = true; // stalled: drop this request
                        break;
                    }
                    if (!any) ThaiModEntry.Log("openai first audio " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.00") + " s" + (attempt > 1 ? " (retry)" : ""));
                    any = true;
                    int i = 0;
                    if (carry >= 0 && chunk.Length > 0) { block.Add((short)(carry | chunk[0] << 8) / 32768f); i = 1; carry = -1; }
                    for (; i + 1 < chunk.Length; i += 2) block.Add((short)(chunk[i] | chunk[i + 1] << 8) / 32768f);
                    if (i < chunk.Length) carry = chunk[i];
                    if (block.Count >= 4800) { onAudio(block.ToArray()); block.Clear(); }
                }
            }
            if (block.Count > 0) onAudio(block.ToArray());
            if (any) consecutiveFails = 0; else Fail("no audio after retry");
            return any;
        }

        private static System.Collections.IEnumerator SendStream(StreamJob job)
        {
            var req = new UnityWebRequest(job.Url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(job.Body));
            req.uploadHandler.contentType = "application/json";
            var sink = new StreamSink(job);
            req.downloadHandler = sink;
            req.disposeDownloadHandlerOnDispose = false;
            req.SetRequestHeader("Authorization", job.Auth);
            req.timeout = 30;
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (job.Abort) { req.Abort(); break; }
                yield return null;
            }
            if (!job.Abort && req.result != UnityWebRequest.Result.Success)
            {
                job.Failed = true;
                ThaiModEntry.Warn("openai request failed: " + req.responseCode + " " + req.error);
            }
            job.Chunks.CompleteAdding();
            req.Dispose();
            sink.Dispose();
        }

        private static float[] Pcm16(byte[] pcm)
        {
            var f = new float[pcm.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768f;
            return f;
        }

        private static float[] Resample(float[] src, int from, int to)
        {
            if (from == to || from <= 0) return src;
            int n = (int)((long)src.Length * to / from);
            var res = new float[n];
            double step = (double)from / to;
            for (int i = 0; i < n; i++)
            {
                double p = i * step; int i0 = (int)p; int i1 = Math.Min(i0 + 1, src.Length - 1); double fr = p - i0;
                res[i] = (float)(src[i0] * (1 - fr) + src[i1] * fr);
            }
            return res;
        }

        private static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append(' '); else sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string JsonStringValue(string json, string key)
        {
            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) throw new InvalidDataException("no " + key + " in response");
            int q = json.IndexOf('"', json.IndexOf(':', k) + 1);
            int e = json.IndexOf('"', q + 1);
            return json.Substring(q + 1, e - q - 1);
        }

        private static string Signed(int v) => (v >= 0 ? "+" : "") + v;

        private static int consecutiveFails;

        // One bad line uses the Windows voice for that line only; 3 failures in a row pause the cloud voice for 60 s.
        private static void Fail(string why)
        {
            consecutiveFails++;
            if (consecutiveFails >= 3)
            {
                consecutiveFails = 0;
                cooldownUntil = DateTime.UtcNow.AddSeconds(60);
                ThaiModEntry.Warn("cloud voice failed 3x (" + why + "); using Windows voice for 60 s");
            }
            else ThaiModEntry.Warn("cloud voice failed (" + why + "); Windows voice for this line");
        }

        // Main thread pump (ThaiModBehaviour.Update).
        public static void Pump(MonoBehaviour host)
        {
            RefreshMap();
            while (true)
            {
                StreamJob sj;
                lock (streamPending) { if (streamPending.Count == 0) break; sj = streamPending.Dequeue(); }
                host.StartCoroutine(SendStream(sj));
            }
            while (true)
            {
                Job job;
                lock (pending) { if (pending.Count == 0) return; job = pending.Dequeue(); }
                host.StartCoroutine(Send(job));
            }
        }

        private static System.Collections.IEnumerator Send(Job job)
        {
            var started = DateTime.UtcNow;
            var req = new UnityWebRequest(job.Url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(job.Body));
            req.uploadHandler.contentType = "application/json";
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader(job.AuthHeader, job.AuthValue);
            req.timeout = 18;
            yield return req.SendWebRequest();
            bool ok = req.result == UnityWebRequest.Result.Success && req.downloadHandler.data != null && req.downloadHandler.data.Length > 0;
            if (ok)
            {
                ThaiModEntry.Log(job.Kind + " voice " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.00") + " s");
                if (job.Kind == "gemini") job.Tcs.TrySetResult(req.downloadHandler.text); else job.Raw.TrySetResult(req.downloadHandler.data);
            }
            else
            {
                string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (body != null && body.Length > 300) body = body.Substring(0, 300);
                // unknown Gemini model id: try the next one on the next line
                if (job.Kind == "gemini" && req.responseCode == 404 && workingGeminiModel == null)
                {
                    int i = Array.IndexOf(GeminiModels, job.Url.Substring(job.Url.LastIndexOf('/') + 1).Replace(":generateContent", ""));
                    if (i >= 0 && i + 1 < GeminiModels.Length)
                    {
                        geminiModel = GeminiModels[i + 1];
                        ThaiModEntry.Warn("gemini model not found, trying " + geminiModel + " next line");
                        job.Tcs.TrySetResult(null); job.Raw.TrySetResult(null); req.Dispose();
                        yield break;
                    }
                }
                Fail(job.Kind + " " + req.responseCode + " " + req.error + " " + body);
                job.Tcs.TrySetResult(null); job.Raw.TrySetResult(null);
            }
            req.Dispose();
        }
    }
}
