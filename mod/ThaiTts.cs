using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Kolkata.MossTtsNano;
using PocketTTS;

namespace SWYF.ThaiMod
{
    // Thai speech for caller lines. Kolkata.MossTtsNano.dll is patched (Cecil) so that the MOSS worker calls
    // ThaiTts.HasRuntimeOrThai(...) before loading MOSS and ThaiTts.Synthesize(...) instead of runtime.Synthesize(...).
    // Thai text is spoken by the Windows built-in Thai voice through a small persistent PowerShell helper.
    public static class ThaiTts
    {
        private const int OutRate = 24000;
        private static readonly object Gate = new object();
        private static string scriptPath;
        private static string tempDir;
        private static Process proc;
        private static StreamWriter stdin;
        private static StreamReader stdout;
        private static int requestId;
        private static bool helperBroken;
        internal static string VoiceName = "";

        public static bool IsThai(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text) if (c >= '฀' && c <= '๿') return true;
            return false;
        }

        // Patched into MOSS: "if (runtime == null) runtime = new MossOnnxRuntime(...)" becomes
        // "if (!HasRuntimeOrThai(runtime, text)) runtime = new ..." so Thai lines never load MOSS.
        public static bool HasRuntimeOrThai(MossOnnxRuntime runtime, string text)
        {
            return runtime != null || IsThai(text);
        }

        // Patched into MOSS in place of runtime.Synthesize(text, voicePath, emit, ct).
        public static int Synthesize(MossOnnxRuntime runtime, string text, string voicePath, Action<PocketTtsAudioResponse> emit, CancellationToken ct)
        {
            if (!IsThai(text))
                return runtime.Synthesize(text, voicePath, emit, ct);

            MarkMossGenerating();
            foreach (PromptSegment seg in PauseTagParser.Parse(text))
            {
                ct.ThrowIfCancellationRequested();
                if (seg.IsPause)
                {
                    if (seg.PauseSamples > 0) emit(new PocketTtsAudioResponse(new float[seg.PauseSamples], seg.PauseSamples, null));
                    continue;
                }
                var speech = new StringBuilder();
                var words = new Queue<string>();
                foreach (PromptSegment span in TranscriptTagParser.ParseSpeechSpans(seg.Text))
                {
                    speech.Append(span.Text);
                    IEnumerable<string> w = span.Text != span.TranscriptText
                        ? (IEnumerable<string>)TranscriptTagParser.CreateOverrideWords(span.Text, span.TranscriptText)
                        : TranscriptTagParser.CreateTranscriptWords(span.Text);
                    foreach (var x in w) words.Enqueue(x);
                }
                string spoken = speech.ToString().Trim();
                if (spoken.Length == 0 && words.Count == 0) continue;
                string say = ThaiSpeechText.Clean(spoken); // what the voice reads; subtitles keep the original words
                int until = 0;

                // "อืม ได้..." while the AI thinks: a ready-made clip plays at once
                if (ThaiText.IsFiller(say))
                {
                    float[] clip = ThaiVoices.Clip(say, voicePath);
                    if (clip == null)
                    {
                        try { clip = Speak(say, voicePath, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { ThaiModEntry.Warn("Thai TTS failed (filler): " + e.Message); }
                        if (clip != null && clip.Length > 0) ThaiVoices.RememberClip(say, voicePath, clip);
                    }
                    EmitPcm(clip != null && clip.Length > 0 ? clip : Silent(spoken), words, ref until, true, emit, ct);
                    continue;
                }
                ThaiVoices.PrepareFillers(voicePath);
                ThaiCloudStt.NoteCallerLine(spoken);

                // OpenAI / Gemini 3.8: stream audio into the game as it arrives (first sound after ~1 s instead of the whole line).
                bool streamTried = false;
                if (say.Length > 0 && ThaiVoices.CanStream)
                {
                    streamTried = true;
                    var box = new int[1];
                    float[] held = null;
                    bool streamed = false;
                    try
                    {
                        var gender = ThaiVoices.GenderFor(voicePath);
                        streamed = ThaiVoices.StreamCloud(say, voicePath, gender, block =>
                        {
                            if (held != null)
                            {
                                int u = box[0];
                                emit(new PocketTtsAudioResponse(held, held.Length, SpeechTranscriptTiming.TakeTranscriptWords(words, ref u, held.Length, false)));
                                box[0] = u;
                            }
                            held = block;
                        }, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { ThaiModEntry.Warn("voice stream error: " + e.Message); }
                    if (held != null)
                    {
                        int u = box[0];
                        emit(new PocketTtsAudioResponse(held, held.Length, SpeechTranscriptTiming.TakeTranscriptWords(words, ref u, held.Length, true)));
                    }
                    if (streamed || held != null) continue;
                }
                // Streaming failed: Windows voice for this line. But if the provider can no longer stream at all
                // (Gemini refused the new model), the whole-line cloud request below still gets its turn.
                bool streamFailed = streamTried && ThaiVoices.CanStream;

                // Windows voice: on a long line, speak the first phrase while the rest is being made.
                bool local = streamFailed || !ThaiVoices.CloudUsable;
                if (local && ThaiSpeechText.SplitFirst(say, 12, 40, 50, out var first, out var rest))
                {
                    float[] a = null;
                    try { a = SpeakWindows(first, voicePath, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { ThaiModEntry.Warn("Thai TTS failed, using silent timing: " + e.Message); }
                    if (a != null && a.Length > 0)
                    {
                        EmitPcm(a, words, ref until, false, emit, ct);
                        float[] b = null;
                        try { b = SpeakWindows(rest, voicePath, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { ThaiModEntry.Warn("Thai TTS failed, using silent timing: " + e.Message); }
                        EmitPcm(b != null && b.Length > 0 ? b : Silent(rest), words, ref until, true, emit, ct);
                        continue;
                    }
                }

                float[] pcm = null;
                try { if (say.Length > 0) pcm = Speak(say, voicePath, ct, streamFailed); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { ThaiModEntry.Warn("Thai TTS failed, using silent timing: " + e.Message); }
                if (pcm == null || pcm.Length == 0) pcm = Silent(spoken); // silent fallback keeps subtitles moving
                EmitPcm(pcm, words, ref until, true, emit, ct);
            }
            return 0;
        }

        private static float[] Silent(string text) => new float[Math.Max(OutRate / 2, (int)(text.Length * 0.075 * OutRate))];

        // Hands audio to the game in 200 ms pieces, moving the subtitle words along with it.
        private static void EmitPcm(float[] pcm, Queue<string> words, ref int until, bool lastPart, Action<PocketTtsAudioResponse> emit, CancellationToken ct)
        {
            const int chunk = OutRate / 5; // 200 ms
            for (int off = 0; off < pcm.Length; off += chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(chunk, pcm.Length - off);
                var part = new float[n];
                Array.Copy(pcm, off, part, 0, n);
                bool last = lastPart && off + n >= pcm.Length;
                emit(new PocketTtsAudioResponse(part, n, SpeechTranscriptTiming.TakeTranscriptWords(words, ref until, n, last)));
            }
        }

        private static void MarkMossGenerating()
        {
            try
            {
                var inst = MossTtsSingleton.Instance;
                if (inst == null) return;
                var f = typeof(MossTtsSingleton).GetField("<Status>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null && (ModelStatus)f.GetValue(inst) == ModelStatus.Loading) f.SetValue(inst, ModelStatus.Generate);
            }
            catch { }
        }

        // ---------- Windows Thai voice helper ----------

        public static void Setup()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "SWYF.ThaiMod");
            Directory.CreateDirectory(tempDir);
            scriptPath = Path.Combine(tempDir, "thai_tts.ps1");
            File.WriteAllText(scriptPath, HelperScript, new UTF8Encoding(true));
        }

        public static void Prewarm()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { lock (Gate) EnsureHelper(); }
                catch (Exception e) { ThaiModEntry.Warn("TTS prewarm failed: " + e.Message); }
            });
        }

        private static void EnsureHelper()
        {
            if (proc != null && !proc.HasExited) return;
            if (helperBroken) throw new InvalidOperationException("Thai voice helper unavailable.");
            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(ps)) ps = "powershell.exe";
            var psi = new ProcessStartInfo(ps, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            proc = Process.Start(psi);
            stdin = proc.StandardInput;
            stdin.AutoFlush = true;
            stdout = proc.StandardOutput;
            string ready = stdout.ReadLine();
            if (ready == null || !ready.StartsWith("READY"))
            {
                helperBroken = true;
                throw new InvalidOperationException("Helper did not start: " + ready);
            }
            var parts = ready.Split('\t');
            VoiceName = parts.Length > 1 ? parts[1] : "";
            if (VoiceName == "NOVOICE")
            {
                helperBroken = true;
                throw new InvalidOperationException("No Thai voice installed in Windows (Settings > Time & language > Speech).");
            }
            ThaiModEntry.Log("Thai voice ready: " + VoiceName);
        }

        private static float[] Speak(string text, string voicePath, CancellationToken ct, bool skipCloud = false)
        {
            if (!skipCloud && ThaiVoices.CloudUsable)
            {
                try
                {
                    var neural = ThaiVoices.SynthesizeCloud(text, voicePath, ThaiVoices.GenderFor(voicePath));
                    if (neural != null && neural.Length > 0) return neural;
                }
                catch (Exception e) { ThaiModEntry.Warn("cloud voice error: " + e.Message); }
            }
            return SpeakWindows(text, voicePath, ct);
        }

        // Windows voice (one male Thai voice): vary pitch per character, raise it for women. Kept close to the
        // natural pitch and speed: big shifts make Thai tones hard to hear.
        private static float[] SpeakWindows(string text, string voicePath, CancellationToken ct)
        {
            var gender = ThaiVoices.GenderFor(voicePath);
            int h = ThaiVoices.Hash(voicePath);
            double pitch, rate = 1.00 + (h / 41 % 7) / 100.0;                            // 1.00 .. 1.06
            switch (gender)
            {
                case ThaiVoices.Gender.Female: pitch = 1.25 + (h % 11) / 100.0; break;   // 1.25 .. 1.35
                case ThaiVoices.Gender.Male: pitch = 0.88 + (h % 13) / 100.0; break;     // 0.88 .. 1.00
                case ThaiVoices.Gender.Boss: pitch = 0.80; rate = 1.08; break;
                default: pitch = 0.92 + (h % 21) / 100.0; break;                          // 0.92 .. 1.12
            }
            var started = DateTime.UtcNow;
            byte[] wav;
            lock (Gate)
            {
                ct.ThrowIfCancellationRequested();
                EnsureHelper();
                int id = ++requestId;
                string outPath = Path.Combine(tempDir, "tts_" + id + ".wav");
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                stdin.WriteLine(id.ToString(CultureInfo.InvariantCulture) + "\t" + pitch.ToString("0.00", CultureInfo.InvariantCulture) + "\t" + rate.ToString("0.00", CultureInfo.InvariantCulture) + "\t" + b64 + "\t" + outPath);
                string line;
                while (true)
                {
                    line = stdout.ReadLine();
                    if (line == null) { proc = null; throw new IOException("Thai voice helper exited."); }
                    if (line.StartsWith(id.ToString(CultureInfo.InvariantCulture) + "\t")) break;
                }
                if (!line.Contains("\tOK")) throw new IOException("Thai voice error: " + line);
                wav = File.ReadAllBytes(outPath);
                try { File.Delete(outPath); } catch { }
            }
            ThaiModEntry.Log("windows voice " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.00") + " s (" + text.Length + " chars)");
            return DecodeWavTo24k(wav);
        }

        private static float[] DecodeWavTo24k(byte[] b)
        {
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') throw new InvalidDataException("Not a WAV file.");
            int pos = 12, fmt = 1, ch = 1, rate = 16000, bits = 16, dataOff = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    fmt = BitConverter.ToInt16(b, body);
                    ch = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                    if (fmt == -2 && len >= 26) fmt = BitConverter.ToInt16(b, body + 24); // WAVE_FORMAT_EXTENSIBLE
                }
                else if (id == "data")
                {
                    dataOff = body;
                    dataLen = Math.Min(len < 0 ? 0x7FFFFFFF : len, b.Length - body);
                    break;
                }
                pos = body + len + (len & 1);
            }
            if (dataOff < 0) throw new InvalidDataException("WAV has no data chunk.");
            int bytesPer = bits / 8;
            int frames = dataLen / (bytesPer * ch);
            var mono = new float[frames];
            var f1 = new float[1];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = dataOff + (i * ch + c) * bytesPer;
                    if (fmt == 3 && bits == 32) { Buffer.BlockCopy(b, o, f1, 0, 4); sum += f1[0]; }
                    else if (bits == 16) sum += BitConverter.ToInt16(b, o) / 32768f;
                    else if (bits == 8) sum += (b[o] - 128) / 128f;
                    else if (bits == 24) sum += ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f;
                    else if (bits == 32) sum += BitConverter.ToInt32(b, o) / 2147483648f;
                }
                mono[i] = sum / ch;
            }
            if (rate == OutRate) return mono;
            int outLen = (int)((long)frames * OutRate / rate);
            var res = new float[outLen];
            double step = (double)rate / OutRate;
            for (int i = 0; i < outLen; i++)
            {
                double src = i * step;
                int i0 = (int)src;
                int i1 = Math.Min(i0 + 1, frames - 1);
                double f = src - i0;
                res[i] = (float)(mono[i0] * (1 - f) + mono[i1] * f);
            }
            return res;
        }

        private const string HelperScript = @"$ErrorActionPreference = 'Stop'
try {
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $null = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media.SpeechSynthesis, ContentType = WindowsRuntime]
  $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
  function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $null = $task.Wait(-1); return $task.Result }
  $synth = New-Object Windows.Media.SpeechSynthesis.SpeechSynthesizer
  $voice = [Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices | Where-Object { $_.Language -like 'th*' } | Select-Object -First 1
} catch {
  [Console]::Out.WriteLine(""READY`tERROR "" + ($_.Exception.Message -replace ""`r|`n"", ' ')); [Console]::Out.Flush(); exit 1
}
if ($null -eq $voice) { [Console]::Out.WriteLine(""READY`tNOVOICE""); [Console]::Out.Flush(); exit 2 }
$synth.Voice = $voice
[Console]::Out.WriteLine(""READY`t"" + $voice.DisplayName); [Console]::Out.Flush()
$inv = [Globalization.CultureInfo]::InvariantCulture
while ($true) {
  $line = [Console]::In.ReadLine()
  if ($null -eq $line) { break }
  $p = $line.Split(""`t"")
  try {
    $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p[3]))
    try { $synth.Options.AudioPitch = [double]::Parse($p[1], $inv); $synth.Options.SpeakingRate = [double]::Parse($p[2], $inv) } catch { }
    $stream = Await ($synth.SynthesizeTextToStreamAsync($text)) ([Windows.Media.SpeechSynthesis.SpeechSynthesisStream])
    $ms = New-Object IO.MemoryStream
    $rs = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream)
    $rs.CopyTo($ms); $rs.Dispose(); $stream.Dispose()
    [IO.File]::WriteAllBytes($p[4], $ms.ToArray())
    [Console]::Out.WriteLine($p[0] + ""`tOK"")
  } catch {
    [Console]::Out.WriteLine($p[0] + ""`tERR`t"" + ($_.Exception.Message -replace ""`r|`n"", ' '))
  }
  [Console]::Out.Flush()
}
";
    }
}
