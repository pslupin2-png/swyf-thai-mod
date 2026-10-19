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
    // Picks a Thai voice per character (gender + personality from the caller identity JSON) and, when a Gemini or
    // OpenAI key is present in voice_keys.txt, synthesizes with Gemini TTS (gemini-3.8-flash-tts, streamed) or OpenAI
    // gpt-4o-mini-tts (streamed), 24 kHz PCM.
    // Otherwise ThaiTts falls back to the Windows voice with a gender-appropriate pitch.
    //
    // Speed: short filler lines ("อืม ได้...") are made ahead of time per caller and replayed from memory, and the
    // HTTPS connection to the voice provider is warmed while the player talks and kept alive during play.
    public static class ThaiVoices
    {
        public enum Gender { Unknown, Male, Female, Boss }

        private sealed class CharInfo { public Gender Gender; public string Name; public string Persona; }
        private static readonly Dictionary<string, CharInfo> infoByVoice = new Dictionary<string, CharInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly object MapGate = new object();
        private static float nextMapRefresh;

        private static string cfgPath;
        private static DateTime cfgStamp = DateTime.MinValue;
        private static string geminiKey, openaiKey;
        private static bool openaiBackup;         // voice_backup=openai: OpenAI takes over when Gemini's free quota is used up
        private static string geminiModel = "";   // gemini_model= : tried first
        private static DateTime cooldownUntil = DateTime.MinValue;
        private static readonly object CfgGate = new object();

        // Gemini TTS models, best first. Each model has its own free daily quota (about 30 lines a day on a free key,
        // reset at midnight Pacific time), so when one runs out the next takes over before the Windows voice does.
        //   3.8 Flash: Thai, streams audio as it is made (~1 s to first sound); 3.1 Flash preview streams too
        //   2.5 Flash / Pro preview: whole line at once (4-7 s)
        private static readonly string[] GeminiLadder =
            { "gemini-3.8-flash-tts", "gemini-3.1-flash-tts-preview", "gemini-2.5-flash-preview-tts", "gemini-2.5-pro-preview-tts" };

        private sealed class GemModel
        {
            public string Id;
            public bool Stream;
            public volatile bool Broken;                       // refused our request (400/403/404): skip until keys change
            public DateTime BlockedUntil = DateTime.MinValue;  // 429: briefly, or until the daily quota resets
            public int Strikes;                                // 429s in a row without a success
        }
        private static GemModel[] gemModels = BuildLadder("");

        private static GemModel[] BuildLadder(string preferred)
        {
            var ids = new List<string>();
            if (preferred.Length > 0) ids.Add(preferred);
            foreach (var m in GeminiLadder) if (!ids.Contains(m)) ids.Add(m);
            var res = new GemModel[ids.Count];
            for (int i = 0; i < ids.Count; i++)
                res[i] = new GemModel { Id = ids[i], Stream = !IsLegacyGemini(ids[i]) || ids[i].IndexOf("3.1-flash-tts", StringComparison.Ordinal) >= 0 };
            return res;
        }

        // Older TTS models want the style written into the prompt; 3.8+ takes it in speech_metadata.
        private static bool IsLegacyGemini(string model) =>
            model.IndexOf("2.5-", StringComparison.Ordinal) >= 0 || model.IndexOf("3.1-flash-tts", StringComparison.Ordinal) >= 0;

        private static GemModel CurrentGemini
        {
            get
            {
                if (geminiKey == null) return null;
                var now = DateTime.UtcNow;
                foreach (var m in gemModels) if (!m.Broken && now >= m.BlockedUntil) return m;
                return null;
            }
        }

        private static GemModel FindGemini(string id)
        {
            foreach (var m in gemModels) if (m.Id == id) return m;
            return null;
        }

        // who speaks the next line
        private static bool UseGemini => CurrentGemini != null;
        private static bool UseOpenAi => openaiKey != null && (geminiKey == null || (openaiBackup && CurrentGemini == null));

        // Gemini's daily quotas reset at midnight Pacific time (14:00 or 15:00 in Thailand).
        private static DateTime NextPacificMidnightUtc()
        {
            var now = DateTime.UtcNow;
            int offset = PacificDst(now) ? -7 : -8;
            return now.AddHours(offset).Date.AddDays(1).AddMinutes(2).AddHours(-offset);
        }

        private static bool PacificDst(DateTime utc)
        {
            var mar = new DateTime(utc.Year, 3, 8); while (mar.DayOfWeek != DayOfWeek.Sunday) mar = mar.AddDays(1);   // 2nd Sunday of March
            var nov = new DateTime(utc.Year, 11, 1); while (nov.DayOfWeek != DayOfWeek.Sunday) nov = nov.AddDays(1);  // 1st Sunday of November
            return utc >= mar.AddHours(10) && utc < nov.AddHours(9);
        }

        private static string ThaiClock(DateTime utc) => utc.AddHours(7).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        // A Gemini model said no (400/403/404/429). Moves on to the next model, OpenAI backup or the Windows voice.
        private static void GeminiRefused(string id, long code, string body)
        {
            var m = FindGemini(id);
            if (m == null) return;
            body = body ?? "";
            string what;
            if (code == 429)
            {
                bool perDay = body.IndexOf("PerDay", StringComparison.Ordinal) >= 0 || body.IndexOf("per_day", StringComparison.Ordinal) >= 0;
                int retry = RetrySeconds(body);
                lock (CfgGate)
                {
                    m.Strikes++;
                    if (perDay || m.Strikes >= 3) m.BlockedUntil = NextPacificMidnightUtc();
                    else m.BlockedUntil = DateTime.UtcNow.AddSeconds(Math.Max(20, Math.Min(retry > 0 ? retry : 60, 120)));
                }
                what = DateTime.UtcNow.AddMinutes(10) < m.BlockedUntil
                    ? "used up its free quota for today (resets " + ThaiClock(m.BlockedUntil) + " Thai time" + (perDay ? "" : ", guessed") + ")"
                    : "is rate-limited for " + (m.BlockedUntil - DateTime.UtcNow).TotalSeconds.ToString("0") + " s";
                SaveQuota();
            }
            else { m.Broken = true; what = "refused the request (" + code + ")"; }
            var cur = CurrentGemini;
            ThaiModEntry.Warn("gemini " + id + " " + what + QuotaId(body) + "; next lines: " +
                              (cur != null ? cur.Id + (cur.Stream ? " (streaming)" : " (whole line)") : UseOpenAi ? "OpenAI backup" : "Windows voice"));
        }

        private static int RetrySeconds(string body)
        {
            int k = body.IndexOf("\"retryDelay\"", StringComparison.Ordinal);
            if (k < 0) return 0;
            int q = body.IndexOf('"', body.IndexOf(':', k) + 1);
            int e = q + 1;
            while (e < body.Length && char.IsDigit(body[e])) e++;
            return q > 0 && int.TryParse(body.Substring(q + 1, e - q - 1), out int s) ? s : 0;
        }

        private static string QuotaId(string body)
        {
            int k = body.IndexOf("\"quotaId\"", StringComparison.Ordinal);
            if (k < 0) return "";
            int q = body.IndexOf('"', body.IndexOf(':', k) + 1), e = q < 0 ? -1 : body.IndexOf('"', q + 1);
            return e > q ? " [" + body.Substring(q + 1, e - q - 1) + "]" : "";
        }

        // Daily blocks survive a game restart (otherwise every new session first spends a request finding out).
        private static string QuotaPath => cfgPath == null ? null : Path.Combine(Path.GetDirectoryName(cfgPath), "voice_quota.txt");

        private static void SaveQuota()
        {
            try
            {
                var sb = new StringBuilder("# SWYF Thai mod: Gemini models whose free quota ran out (safe to delete)\nkey=" + Hash(geminiKey ?? "") + "\n");
                foreach (var m in gemModels)
                    if (m.BlockedUntil > DateTime.UtcNow.AddMinutes(10))
                        sb.Append(m.Id).Append('=').Append(m.BlockedUntil.ToString("o", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                if (QuotaPath != null) File.WriteAllText(QuotaPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static void LoadQuota()
        {
            try
            {
                if (QuotaPath == null || !File.Exists(QuotaPath) || geminiKey == null) return;
                bool sameKey = false;
                foreach (var raw in File.ReadAllText(QuotaPath).Split('\n'))
                {
                    var line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.StartsWith("#") || eq < 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    if (k == "key") { sameKey = v == Hash(geminiKey).ToString(); continue; }
                    var m = FindGemini(k);
                    if (sameKey && m != null && DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var until) && until.ToUniversalTime() > DateTime.UtcNow)
                        m.BlockedUntil = until.ToUniversalTime();
                }
            }
            catch { }
        }

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
                    "# gemini_model=gemini-3.8-flash-tts\n", new UTF8Encoding(false));
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
                    cfgStamp = stamp; geminiKey = openaiKey = null; geminiModel = ""; openaiBackup = false;
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
                        if (k == "voice_backup") openaiBackup = v.Equals("openai", StringComparison.OrdinalIgnoreCase);
                    }
                    gemModels = BuildLadder(geminiModel); // new keys: every model gets another try
                    LoadQuota();
                    cooldownUntil = DateTime.MinValue;
                    warmBroken = false;
                    ClearClips(); // keys changed: ready-made clips may be from another provider
                }
                catch { }
            }
        }

        public static string Provider
        {
            get
            {
                LoadCfg();
                string backup = geminiKey != null && openaiBackup && openaiKey != null ? ", OpenAI as backup" : "";
                var gm = CurrentGemini;
                if (gm != null) return "gemini (" + gm.Id + (gm.Stream ? ", streaming" : ", whole lines") + backup + ")";
                if (geminiKey != null) return "gemini (every model used up its free quota today" + backup + ")";
                return openaiKey != null ? "openai (streaming)" : "off (no key in voice_keys.txt)";
            }
        }

        private static string ClipProvider { get { LoadCfg(); return UseGemini ? "gemini" : UseOpenAi ? "openai" : "windows"; } }

        public static bool CloudUsable
        {
            get { LoadCfg(); return DateTime.UtcNow >= cooldownUntil && (UseGemini || UseOpenAi); }
        }

        // ---------- characters: gender + personality ----------

        // Built-in callers keep their voice as "Callers/Defaults/<hash>/voice.wav", so the file name alone ("voice")
        // is not unique; use the folder for such generic names.
        public static string VoiceKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.Replace('\\', '/').Trim().ToLowerInvariant();
            int dot = s.LastIndexOf('.');
            if (dot > s.LastIndexOf('/')) s = s.Substring(0, dot);
            int slash = s.LastIndexOf('/');
            string stem = slash >= 0 ? s.Substring(slash + 1) : s;
            if (slash > 0 && (stem == "voice" || stem == "sample" || stem == "audio" || stem == "reference"))
            {
                int prev = s.LastIndexOf('/', slash - 1);
                stem = s.Substring(prev + 1, slash - prev - 1) + "/" + stem;
            }
            return stem.Replace(' ', '_').Replace('-', '_');
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
                        var info = new CharInfo { Gender = g, Name = c.displayName, Persona = Persona(c.personalityPrompt) };
                        if (!string.IsNullOrEmpty(c.voiceId)) infoByVoice[VoiceKey(c.voiceId)] = info;
                        if (!string.IsNullOrEmpty(c.revealedVoiceId)) infoByVoice[VoiceKey(c.revealedVoiceId)] = info;
                    }
                }
            }
            catch (Exception e) { ThaiModEntry.Warn("voice character map: " + e.Message); }
        }

        // "You are Franklin D. Hyper, a nasty, arrogant casino owner ... He ..." -> first two sentences, <= 300 chars
        public static string Persona(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return null;
            string p = prompt.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (p.StartsWith("You are ", StringComparison.OrdinalIgnoreCase)) p = p.Substring(8);
            int end = -1, firstEnd = -1, sentences = 0;
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i] != '.' && p[i] != '!' && p[i] != '?') continue;
                if (p[i] == '.' && i > 0 && char.IsUpper(p[i - 1]) && (i < 2 || p[i - 2] == ' ')) continue; // "D." initial
                if (i + 1 < p.Length && p[i + 1] != ' ') continue;
                end = i; if (firstEnd < 0) firstEnd = i;
                if (++sentences == 2) break;
            }
            if (end > 0) p = p.Substring(0, (end + 1 > 300 && firstEnd >= 40 ? firstEnd : end) + 1);
            if (p.Length > 300)
            {
                int cut = p.LastIndexOf(' ', 300);
                p = p.Substring(0, cut > 200 ? cut : 300) + "…";
            }
            return p;
        }

        public static Gender GenderFor(string voicePath)
        {
            if (string.IsNullOrEmpty(voicePath)) return Gender.Unknown;
            if (voicePath.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0) return Gender.Boss;
            lock (MapGate)
                return infoByVoice.TryGetValue(VoiceKey(voicePath), out var i) ? i.Gender : Gender.Unknown;
        }

        private static string PersonaFor(string voicePath)
        {
            if (string.IsNullOrEmpty(voicePath)) return null;
            lock (MapGate)
                return infoByVoice.TryGetValue(VoiceKey(voicePath), out var i) ? i.Persona : null;
        }

        public static int Hash(string s)
        {
            int h = 17;
            foreach (char c in s ?? "") h = h * 31 + c;
            return h & 0x7FFFFFFF;
        }

        // ---------- how to say it ----------

        private static string Mood(string text, Gender g)
        {
            int bangs = 0, qs = 0;
            foreach (char c in text) { if (c == '!') bangs++; else if (c == '?') qs++; }
            if (Has(text, "ฮ่า", "555", "ฮา ฮา", "หัวเราะ")) return "amused, laughing a little";
            if (Has(text, "ช่วยด้วย", "ด่วน", "เร็วๆ", "เร็ว ๆ", "ฉุกเฉิน")) return "urgent and anxious";
            if (Has(text, "ขอโทษ", "เสียใจ")) return "apologetic";
            if (Has(text, "ไอ้", "เฮ้ย", "บ้า", "โกง", "หลอก", "แม่ง", "กู ")) return bangs > 0 ? "angry and loud" : "irritated";
            if (g == Gender.Boss) return bangs >= 1 ? "furious, yelling" : "stern and impatient";
            return bangs >= 2 ? "very agitated and loud" : bangs == 1 ? "emotional and energetic" : qs > 0 ? "curious, questioning" : "natural";
        }

        private static bool Has(string text, params string[] keys)
        {
            foreach (var k in keys) if (text.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string Who(Gender g, string persona)
        {
            string body = g == Gender.Female ? "a woman" : g == Gender.Male ? "a man" : "a person";
            if (g == Gender.Boss) return "a loud, bossy call-center boss scolding his lazy staff.";
            return persona != null ? body + " playing this character: " + persona : body + " on the phone.";
        }

        // OpenAI gpt-4o-mini-tts "instructions"
        public static string Instructions(string text, string voicePath, Gender g)
        {
            return "Character: " + Who(g, PersonaFor(voicePath)) + "\n" +
                   "Emotion for this line: " + Mood(text, g) + ".\n" +
                   "Language: Thai with a native Central Thai (Bangkok) accent. Pronounce every tone, vowel length and final consonant clearly; read English names and numbers the way Thai people say them.\n" +
                   "Delivery: a phone call. Natural and conversational at a brisk pace, short pauses only. Act the character's attitude with real feeling, but keep every word easy to understand.\n" +
                   "Say only the given text. Never read punctuation, symbols or stage directions aloud.";
        }

        private static string GeminiPrompt(string text, string voicePath, Gender g)
        {
            return "Read the Thai line below aloud as " + Who(g, PersonaFor(voicePath)) + ". Emotion: " + Mood(text, g) +
                   ". Native Central Thai (Bangkok) accent, every tone clear, brisk conversational phone-call pace. Say only the line itself:\n" + text;
        }

        // Gemini 3.8: the text part holds only the words; how to say them goes in speech_metadata.style
        // (style text placed before the words can get read aloud).
        private static string GeminiStyle(string text, string voicePath, Gender g)
        {
            return "Speaking as " + Who(g, PersonaFor(voicePath)) + " Emotion: " + Mood(text, g) +
                   ". Native Central Thai (Bangkok) accent, every tone clear, brisk conversational phone-call pace.";
        }

        private static string GeminiVoice(Gender gender, int h) =>
            gender == Gender.Boss ? "Algenib" : gender == Gender.Female ? GeminiFemale[h % GeminiFemale.Length]
            : gender == Gender.Male ? GeminiMale[h % GeminiMale.Length] : (h % 2 == 0 ? GeminiFemale : GeminiMale)[h % 8];

        private static string GeminiBody(string model, string text, string voicePath, Gender gender)
        {
            string voice = GeminiVoice(gender, Hash(voicePath));
            if (IsLegacyGemini(model))
                return "{\"contents\":[{\"parts\":[{\"text\":" + Json(GeminiPrompt(text, voicePath, gender)) + "}]}],\"generationConfig\":{\"responseModalities\":[\"AUDIO\"]," +
                       "\"speechConfig\":{\"voiceConfig\":{\"prebuiltVoiceConfig\":{\"voiceName\":\"" + voice + "\"}}}}}";
            return "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":" + Json(text) + ",\"speech_metadata\":{\"style\":" + Json(GeminiStyle(text, voicePath, gender)) + "}}]}]," +
                   "\"generationConfig\":{\"responseModalities\":[\"AUDIO\"],\"speechConfig\":{\"voiceConfig\":{\"voice\":\"" + voice + "\"}}}}";
        }

        // ---------- cloud synthesis (Gemini TTS / OpenAI gpt-4o-mini-tts) ----------

        private sealed class Job
        {
            public string Url, Body, AuthHeader, AuthValue, Kind, Model;
            public bool Critical = true;
            public volatile bool Switch; // gemini model refused / out of quota: try the next one
            public TaskCompletionSource<string> Tcs = new TaskCompletionSource<string>(); // gemini: JSON text
            public TaskCompletionSource<byte[]> Raw = new TaskCompletionSource<byte[]>(); // openai: raw pcm
        }
        private static readonly Queue<Job> pending = new Queue<Job>();

        private static readonly string[] GeminiFemale = { "Kore", "Aoede", "Leda", "Despina", "Callirrhoe", "Autonoe", "Erinome", "Sulafat" };
        private static readonly string[] GeminiMale = { "Puck", "Charon", "Orus", "Iapetus", "Umbriel", "Algieba", "Schedar", "Achird" };
        private static readonly string[] OpenAiFemale = { "marin", "coral", "nova", "shimmer", "sage" };
        private static readonly string[] OpenAiMale = { "cedar", "ash", "echo", "verse", "ballad" };

        private static string OpenAiVoice(Gender g, int h)
        {
            return g == Gender.Boss ? "onyx" : g == Gender.Female ? OpenAiFemale[h % OpenAiFemale.Length]
                 : g == Gender.Male ? OpenAiMale[h % OpenAiMale.Length] : (h % 2 == 0 ? "marin" : "cedar");
        }

        private static string OpenAiBody(string text, string voicePath, Gender g)
        {
            return "{\"model\":\"gpt-4o-mini-tts\",\"voice\":\"" + OpenAiVoice(g, Hash(voicePath)) + "\",\"response_format\":\"pcm\",\"input\":" + Json(text) +
                   ",\"instructions\":" + Json(Instructions(text, voicePath, g)) + "}";
        }

        // Worker thread: blocks until the main thread fetched the audio. Returns 24 kHz mono floats or null.
        public static float[] SynthesizeCloud(string text, string voicePath, Gender gender) => SynthesizeCloud(text, voicePath, gender, true);

        // critical = a line the player is waiting for. Background work (filler clips) passes false: its failures
        // must not count toward the 60 s "use the Windows voice" pause.
        public static float[] SynthesizeCloud(string text, string voicePath, Gender gender, bool critical)
        {
            LoadCfg();
            NoteActivity();
            for (int round = 0; round < 6; round++)
            {
                var job = new Job { Critical = critical };
                var gm = CurrentGemini;
                if (gm != null)
                {
                    job.Kind = "gemini"; job.Model = gm.Id;
                    job.Url = "https://generativelanguage.googleapis.com/v1beta/models/" + gm.Id + ":generateContent";
                    job.AuthHeader = "x-goog-api-key"; job.AuthValue = geminiKey;
                    job.Body = GeminiBody(gm.Id, text, voicePath, gender);
                    lock (pending) pending.Enqueue(job);
                    if (!job.Tcs.Task.Wait(20000)) { if (critical) Fail("timeout"); return null; }
                    string json = job.Tcs.Task.Result;
                    if (json == null) { if (job.Switch) continue; return null; }
                    gm.Strikes = 0;
                    int rate = 24000;
                    int r = json.IndexOf("rate=", StringComparison.Ordinal);
                    if (r > 0) { int e = r + 5; while (e < json.Length && char.IsDigit(json[e])) e++; int.TryParse(json.Substring(r + 5, e - r - 5), out rate); }
                    byte[] pcm = GeminiSse.StripWav(Convert.FromBase64String(GeminiSse.Unescape64(JsonStringValue(json, "data"))), ref rate); // 3.8 returns WAV
                    return Resample(Pcm16(pcm), rate, 24000);
                }
                if (!UseOpenAi) return null;
                job.Kind = "openai";
                job.Url = "https://api.openai.com/v1/audio/speech";
                job.AuthHeader = "Authorization"; job.AuthValue = "Bearer " + openaiKey;
                job.Body = OpenAiBody(text, voicePath, gender);
                lock (pending) pending.Enqueue(job);
                if (!job.Raw.Task.Wait(20000)) { if (critical) Fail("timeout"); return null; }
                byte[] raw = job.Raw.Task.Result;
                return raw == null ? null : Pcm16(raw);
            }
            return null;
        }

        // ---------- ready-made clips (fillers) ----------

        private static readonly Dictionary<string, float[]> clips = new Dictionary<string, float[]>();
        private static readonly HashSet<string> prepared = new HashSet<string>();
        private static readonly object ClipGate = new object();

        private static string ClipKey(string text, string voicePath) => ClipProvider + "|" + VoiceKey(voicePath) + "|" + text.Trim();

        private static void ClearClips() { lock (ClipGate) { clips.Clear(); prepared.Clear(); } }

        public static float[] Clip(string text, string voicePath)
        {
            string k = ClipKey(text, voicePath);
            lock (ClipGate) return clips.TryGetValue(k, out var pcm) ? pcm : null;
        }

        public static void RememberClip(string text, string voicePath, float[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            string k = ClipKey(text, voicePath);
            lock (ClipGate)
            {
                if (clips.Count > 300) clips.Clear();
                clips[k] = pcm;
            }
        }

        // After a caller's first line: make that caller's filler lines in the background, all three at once.
        // OpenAI only: Gemini's free tier allows very few requests, and the Windows voice is local and quick;
        // those keep each clip the first time it is used.
        public static void PrepareFillers(string voicePath)
        {
            if (string.IsNullOrEmpty(voicePath) || !CanStream || UseGemini) return; // Gemini: few free requests a day
            string who = ClipProvider + "|" + VoiceKey(voicePath);
            lock (ClipGate) { if (!prepared.Add(who)) return; }
            var gender = GenderFor(voicePath);
            new Thread(() =>
            {
                try
                {
                    Thread.Sleep(1500); // let the line that is playing now have the connection first
                    var started = DateTime.UtcNow;
                    int made = 0;
                    foreach (var f in ThaiText.FillerLines) // one at a time: lighter on the connection than three at once
                    {
                        if (Clip(f, voicePath) != null) continue;
                        if (!CloudUsable) break;
                        var pcm = SynthesizeCloud(f, voicePath, gender, false);
                        if (pcm != null && pcm.Length > 0) { RememberClip(f, voicePath, pcm); made++; }
                    }
                    ThaiModEntry.Log("filler clips ready for " + VoiceKey(voicePath) + ": " + made + " in " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0") + " s");
                }
                catch (Exception e) { ThaiModEntry.Warn("filler clips: " + e.Message); }
            }) { IsBackground = true, Name = "ThaiMod fillers" }.Start();
        }

        // ---------- keeping the connection warm ----------

        private static long lastActivityTicks, lastWarmTicks;
        private static volatile bool warmRequested, warmBroken;
        private static string warmWhy = "";

        public static void NoteActivity() { Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks); }

        // Any thread. Opens (or refreshes) the HTTPS connection to the voice provider so the next line starts sooner.
        public static void Warm(string why)
        {
            NoteActivity();
            if (warmBroken || !CloudUsable) return;
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastWarmTicks) < TimeSpan.FromSeconds(12).Ticks) return;
            warmWhy = why; warmRequested = true;
        }

        private static System.Collections.IEnumerator SendWarm(string why)
        {
            LoadCfg();
            string url, header = null, value = null;
            if (UseGemini) { url = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1"; header = "x-goog-api-key"; value = geminiKey; }
            else if (UseOpenAi) { url = "https://api.openai.com/v1/models"; header = "Authorization"; value = "Bearer " + openaiKey; }
            else yield break;
            var started = DateTime.UtcNow;
            var req = UnityWebRequest.Get(url);
            req.SetRequestHeader(header, value);
            req.timeout = 10;
            yield return req.SendWebRequest();
            long code = req.responseCode;
            if (req.result != UnityWebRequest.Result.Success && (code == 401 || code == 403 || code == 404)) warmBroken = true; // key can't list models: skip warm-ups
            if (why != "keep-alive" || req.result != UnityWebRequest.Result.Success)
                ThaiModEntry.Log("voice warm-up (" + why + "): " + (req.result == UnityWebRequest.Result.Success ? "ok" : req.responseCode + " " + req.error) +
                                 " " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.00") + " s");
            req.Dispose();
        }

        // ---------- streaming (audio starts playing while the rest is still being generated) ----------
        // OpenAI sends raw PCM. Gemini 3.8 sends server-sent events with base64 PCM inside (see GeminiSse).

        public static bool CanStream
        {
            get
            {
                LoadCfg();
                if (DateTime.UtcNow < cooldownUntil) return false;
                var gm = CurrentGemini;
                return gm != null ? gm.Stream : UseOpenAi;
            }
        }

        private sealed class StreamJob
        {
            public string Url, Body, AuthHeader, AuthValue, Kind, Model;
            public BlockingCollection<byte[]> Chunks = new BlockingCollection<byte[]>();
            public volatile bool Failed, Abort;
            public volatile int Rate = 24000;
        }
        private static readonly Queue<StreamJob> streamPending = new Queue<StreamJob>();

        // Error responses (anything but 200) are kept as text for the log and never played.
        private sealed class StreamSink : DownloadHandlerScript
        {
            private readonly StreamJob job;
            private readonly GeminiSse sse;
            public UnityWebRequest Req;
            public readonly StringBuilder ErrorText = new StringBuilder();
            public string SseError => sse != null ? sse.LastError : null;

            public StreamSink(StreamJob j) : base(new byte[16384])
            {
                job = j;
                if (j.Kind == "gemini") sse = new GeminiSse(pcm => { job.Rate = sse.Rate; Add(pcm); });
            }

            private void Add(byte[] b) { try { job.Chunks.Add(b); } catch { } }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (dataLength <= 0) return true;
                long code = Req != null ? Req.responseCode : 200;
                if (code != 200 && code != 0)
                {
                    if (ErrorText.Length < 2000) ErrorText.Append(Encoding.UTF8.GetString(data, 0, dataLength));
                    return true;
                }
                if (sse != null)
                {
                    try { sse.Feed(data, dataLength); }
                    catch (Exception e) { ThaiModEntry.Warn("gemini stream: " + e.Message); }
                    return true;
                }
                var copy = new byte[dataLength];
                Buffer.BlockCopy(data, 0, copy, 0, dataLength);
                Add(copy);
                return true;
            }

            protected override void CompleteContent()
            {
                if (sse == null) return;
                try { sse.End(); } catch (Exception e) { ThaiModEntry.Warn("gemini stream: " + e.Message); }
            }
        }

        // Worker thread. Calls onAudio with 100-200 ms float blocks (24 kHz) as they arrive. Returns false if nothing came.
        // A provider sometimes sits on a request for 5+ seconds (once 18 s) before the first byte. Instead of waiting it
        // out, a second identical request goes out after ~2 s; whichever starts sending audio first is used and the
        // other is cancelled. Same voice either way, so the caller never changes voice mid-call.
        private const double HedgeAfterSeconds = 2.2, GeminiHedgeAfterSeconds = 3.0, GiveUpSeconds = 10.0;

        public static bool StreamCloud(string text, string voicePath, Gender gender, Action<float[]> onAudio, CancellationToken ct)
        {
            LoadCfg();
            long last = Interlocked.Read(ref lastActivityTicks);
            string idle = last == 0 ? "first line" : "idle " + TimeSpan.FromTicks(DateTime.UtcNow.Ticks - last).TotalSeconds.ToString("0") + " s before";
            NoteActivity();
            // A Gemini model that is out of quota answers 429 at once; the line moves to the next model (or OpenAI).
            for (int round = 0; round < 6; round++)
            {
                if (!CanStream) return false; // the next option speaks whole lines: the caller takes it from here
                var r = StreamOnce(text, voicePath, gender, onAudio, ct, idle);
                if (r != StreamResult.Switched) return r == StreamResult.Audio;
            }
            return false;
        }

        private enum StreamResult { Audio, Failed, Switched }

        private static StreamResult StreamOnce(string text, string voicePath, Gender gender, Action<float[]> onAudio, CancellationToken ct, string idle)
        {
            var gm = CurrentGemini;
            bool gem = gm != null;
            if (!gem && !UseOpenAi) return StreamResult.Failed;
            string model = gem ? gm.Id : null;
            string kind = gem ? "gemini" : "openai";
            string url = gem ? "https://generativelanguage.googleapis.com/v1beta/models/" + model + ":streamGenerateContent?alt=sse"
                             : "https://api.openai.com/v1/audio/speech";
            string header = gem ? "x-goog-api-key" : "Authorization", value = gem ? geminiKey : "Bearer " + openaiKey;
            string body = gem ? GeminiBody(model, text, voicePath, gender) : OpenAiBody(text, voicePath, gender);
            double hedgeAfter = gem ? GeminiHedgeAfterSeconds : HedgeAfterSeconds;
            StreamJob Start()
            {
                var j = new StreamJob { Url = url, Body = body, AuthHeader = header, AuthValue = value, Kind = kind, Model = model };
                lock (streamPending) streamPending.Enqueue(j);
                return j;
            }

            var started = DateTime.UtcNow;
            StreamJob a = Start(), b = null, win = null;
            byte[] chunk = null;
            try
            {
                while (win == null)
                {
                    ct.ThrowIfCancellationRequested();
                    double el = (DateTime.UtcNow - started).TotalSeconds;
                    if (el > GiveUpSeconds) break;
                    if (gem && (gm.Broken || DateTime.UtcNow < gm.BlockedUntil)) break; // refused / out of quota: next model
                    bool aDead = a.Chunks.IsCompleted;
                    if (b == null && (el > hedgeAfter || aDead))
                    {
                        b = Start();
                        ThaiModEntry.Log(kind + " voice " + (aDead ? "request failed" : "slow to start") + "; sent a second request");
                    }
                    if (TakeQuick(a, out chunk, ct)) { win = a; break; }
                    if (b != null && TakeQuick(b, out chunk, ct)) { win = b; break; }
                    if (b != null && aDead && b.Chunks.IsCompleted) break; // both ended without audio
                }
            }
            catch (OperationCanceledException) { a.Abort = true; if (b != null) b.Abort = true; throw; }

            if (win == null)
            {
                a.Abort = true; if (b != null) b.Abort = true;
                NoteActivity();
                if (gem && (gm.Broken || DateTime.UtcNow < gm.BlockedUntil)) return StreamResult.Switched; // not a voice failure
                Fail("no audio from " + kind + " in " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0") + " s");
                return StreamResult.Failed;
            }
            var loser = win == a ? b : a;
            if (loser != null) loser.Abort = true;
            if (gem) gm.Strikes = 0;
            ThaiModEntry.Log(kind + (gem && gm != gemModels[0] ? " " + model : "") + " first audio " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.00") +
                             " s (" + text.Length + " chars, " + idle + ")" + (win == b ? " (second request)" : ""));

            int rate = win.Rate;
            int carry = -1; var block = new List<float>(2400);
            int blockSize = rate / 10; // first block 100 ms so sound starts sooner, then 200 ms
            while (chunk != null)
            {
                int i = 0;
                if (carry >= 0 && chunk.Length > 0) { block.Add((short)(carry | chunk[0] << 8) / 32768f); i = 1; carry = -1; }
                for (; i + 1 < chunk.Length; i += 2) block.Add((short)(chunk[i] | chunk[i + 1] << 8) / 32768f);
                if (i < chunk.Length) carry = chunk[i];
                if (block.Count >= blockSize) { onAudio(Resample(block.ToArray(), rate, 24000)); block.Clear(); blockSize = rate / 5; }

                bool got;
                try { got = win.Chunks.TryTake(out chunk, 8000, ct); }
                catch (OperationCanceledException) { win.Abort = true; throw; }
                catch (InvalidOperationException) { got = false; } // request finished
                if (!got)
                {
                    if (!win.Chunks.IsCompleted) { win.Abort = true; ThaiModEntry.Warn(kind + " voice stalled mid-line"); }
                    chunk = null;
                }
            }
            if (block.Count > 0) onAudio(Resample(block.ToArray(), rate, 24000));
            NoteActivity();
            consecutiveFails = 0;
            return StreamResult.Audio;
        }

        private static bool TakeQuick(StreamJob j, out byte[] chunk, CancellationToken ct)
        {
            try { return j.Chunks.TryTake(out chunk, 25, ct); }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException) { chunk = null; return false; }
        }

        private static System.Collections.IEnumerator SendStream(StreamJob job)
        {
            var req = new UnityWebRequest(job.Url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(job.Body));
            req.uploadHandler.contentType = "application/json";
            var sink = new StreamSink(job) { Req = req };
            req.downloadHandler = sink;
            req.disposeDownloadHandlerOnDispose = false;
            req.SetRequestHeader(job.AuthHeader, job.AuthValue);
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
                string err = sink.ErrorText.ToString().Replace('\n', ' ');
                if (err.Length > 300) err = err.Substring(0, 300);
                long code = req.responseCode;
                if (job.Kind == "gemini" && (code == 400 || code == 403 || code == 404 || code == 429))
                    GeminiRefused(job.Model, code, sink.ErrorText.ToString()); // logs; the line moves on to the next model
                else ThaiModEntry.Warn(job.Kind + " request failed: " + code + " " + req.error + " " + err);
            }
            else if (!job.Abort && sink.SseError != null) ThaiModEntry.Warn("gemini stream error: " + sink.SseError);
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

            // warm-up requested by another thread, or keep-alive while a session is active (lines in the last 3 min)
            long now = DateTime.UtcNow.Ticks;
            bool active = now - Interlocked.Read(ref lastActivityTicks) < TimeSpan.FromMinutes(3).Ticks;
            bool due = now - Interlocked.Read(ref lastWarmTicks) > TimeSpan.FromSeconds(40).Ticks;
            if (warmRequested || (active && due && !warmBroken && CloudUsable))
            {
                string why = warmRequested ? warmWhy : "keep-alive";
                warmRequested = false;
                Interlocked.Exchange(ref lastWarmTicks, now);
                host.StartCoroutine(SendWarm(why));
            }

            while (true)
            {
                StreamJob sj;
                lock (streamPending) { if (streamPending.Count == 0) break; sj = streamPending.Dequeue(); }
                Interlocked.Exchange(ref lastWarmTicks, now); // a real request keeps the connection warm too
                host.StartCoroutine(SendStream(sj));
            }
            while (true)
            {
                Job job;
                lock (pending) { if (pending.Count == 0) return; job = pending.Dequeue(); }
                Interlocked.Exchange(ref lastWarmTicks, now);
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
                ThaiModEntry.Log(job.Kind + (job.Model != null ? " " + job.Model : "") + " voice " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.00") + " s");
                if (job.Kind == "gemini") job.Tcs.TrySetResult(req.downloadHandler.text); else job.Raw.TrySetResult(req.downloadHandler.data);
            }
            else
            {
                string full = req.downloadHandler != null ? req.downloadHandler.text ?? "" : "";
                string body = (full.Length > 300 ? full.Substring(0, 300) : full).Replace('\n', ' ');
                long code = req.responseCode;
                if (job.Kind == "gemini" && (code == 400 || code == 403 || code == 404 || code == 429))
                {
                    GeminiRefused(job.Model, code, full); // not a voice failure: the next model gets the line
                    job.Switch = true;
                }
                else if (job.Critical) Fail(job.Kind + " " + code + " " + req.error + " " + body);
                else ThaiModEntry.Warn("background " + job.Kind + " voice failed: " + code + " " + req.error);
                job.Tcs.TrySetResult(null); job.Raw.TrySetResult(null);
            }
            req.Dispose();
        }
    }
}
