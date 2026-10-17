using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Whisper;

namespace SWYF.ThaiMod
{
    // When Thai is the call language: download whisper large-v3-turbo (q5_0) once, then point the game's
    // multilingual WhisperManager at it. Falls back to the stock base model if the big one fails to load.
    public static class ThaiWhisper
    {
        public const string BigRel = "Whisper/ggml-large-v3-turbo-q5_0.bin";
        public const string BaseRel = "Whisper/ggml-base.bin";
        private const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin";
        private const long MinBytes = 400L * 1024 * 1024;

        private static bool downloading, downloadFailed, bigBad;
        private static float swapStarted = -1f;
        private static WhisperManager swapped;
        private static object swappedPipe;
        private static bool threadsSet, tooSlow, wasInferring;
        private static float inferStart;
        private static float lastInferSec;
        private static int slowCount, inferCount, peakSamples;

        // Called every frame: time each recognition pass of the big model.
        public static void Watch()
        {
            if (swapped == null || swappedPipe == null || !swapped.IsLoaded) return;
            bool busy = GetField<bool>(swappedPipe, "inferenceInProgress");
            int n = 0;
            if (busy && !wasInferring) inferStart = Time.realtimeSinceStartup;
            if (!busy && wasInferring)
            {
                lastInferSec = Time.realtimeSinceStartup - inferStart;
                inferCount++;
                if (inferCount == 1) { try { if (File.Exists(GpuMarker)) File.Delete(GpuMarker); } catch { } }
                if (lastInferSec > 12f) slowCount++; else slowCount = 0;
                ThaiModEntry.Log("Thai recognition pass took " + lastInferSec.ToString("0.00") + " s");
                if (slowCount >= 3) tooSlow = true;
            }
            wasInferring = busy;
        }

        [DllImport("ggml", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ggml_backend_dev_count();
        [DllImport("ggml", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ggml_backend_register(IntPtr reg);
        [DllImport("ggml-vulkan", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ggml_backend_vk_reg();
        private static bool vulkanTried, gpuActive;
        private static string GpuMarker => Path.Combine(ThaiModEntry.ModDir, "gpu_trying.txt");
        private static string NoGpu => Path.Combine(ThaiModEntry.ModDir, "no_gpu.txt");

        // libwhisper (CPU "compatibility" build) never registers the bundled Vulkan backend, so whisper silently
        // runs on the CPU. The bundled ggml-vulkan.dll is a static-style build (exports ggml_backend_vk_reg only),
        // so register it by hand. A marker file guards against native crashes: if the game dies while the marker
        // exists, the next launch writes no_gpu.txt and stays on the CPU.
        private static void LoadVulkanBackend()
        {
            if (vulkanTried) return;
            vulkanTried = true;
            if (File.Exists(NoGpu)) { ThaiModEntry.Log("no_gpu.txt present; skipping Vulkan"); return; }
            if (Environment.GetEnvironmentVariable("KOLKATA_WHISPER_CPU_ONLY") == "1") { ThaiModEntry.Log("game forced CPU mode; skipping Vulkan"); return; }
            if (File.Exists(GpuMarker))
            {
                ThaiModEntry.Warn("previous Vulkan attempt did not finish (crash?); disabling GPU via no_gpu.txt");
                try { File.WriteAllText(NoGpu, "auto-disabled after a failed GPU attempt; delete this file to retry\n"); File.Delete(GpuMarker); } catch { }
                return;
            }
            try
            {
                File.WriteAllText(GpuMarker, DateTime.Now.ToString("s"));
                long before = (long)ggml_backend_dev_count();
                IntPtr reg = ggml_backend_vk_reg();
                if (reg != IntPtr.Zero) ggml_backend_register(reg);
                long after = (long)ggml_backend_dev_count();
                gpuActive = after > before;
                ThaiModEntry.Log("Vulkan backend " + (gpuActive ? "registered" : "gave no devices") + "; devices " + before + " -> " + after);
                if (!gpuActive) File.Delete(GpuMarker);
            }
            catch (Exception e)
            {
                ThaiModEntry.Warn("Vulkan load error: " + e.Message);
                try { File.Delete(GpuMarker); } catch { }
            }
        }

        private static void EnableGpu(WhisperManager mgr, object pipe)
        {
            LoadVulkanBackend();
            var g = typeof(WhisperManager).GetField("useGpu", BindingFlags.Instance | BindingFlags.NonPublic);
            g?.SetValue(mgr, gpuActive || (bool)(g?.GetValue(mgr) ?? false));
            if (!gpuActive)
            {
                // CPU: shrink whisper's 30 s audio window to the longest utterance the game sends (~3x faster).
                float maxUtter = GetField<float>(pipe, "maximumUtteranceSeconds");
                if (maxUtter <= 0f || maxUtter > 29f) maxUtter = 12f;
                int ctx = Math.Min(1500, ((int)Math.Ceiling((maxUtter + 1.0) * 50.0) + 63) / 64 * 64);
                mgr.audioCtx = ctx;
                ThaiModEntry.Log("CPU mode: maximumUtteranceSeconds=" + maxUtter + " -> audio_ctx " + ctx);
            }
            else mgr.audioCtx = 0;
        }

        private static void SetThreads(WhisperManager mgr)
        {
            try
            {
                var pf = typeof(WhisperManager).GetField("_params", BindingFlags.Instance | BindingFlags.NonPublic);
                object p = pf.GetValue(mgr);
                if (p == null) return;
                var nf = p.GetType().GetField("_param", BindingFlags.Instance | BindingFlags.NonPublic);
                object np = nf.GetValue(p);
                var tf = np.GetType().GetField("n_threads");
                int t = Math.Max(4, Math.Min(12, Environment.ProcessorCount / 2));
                ThaiModEntry.Log("whisper threads " + tf.GetValue(np) + " -> " + t);
                tf.SetValue(np, t);
                nf.SetValue(p, np);
            }
            catch (Exception e) { ThaiModEntry.Warn("set threads: " + e.Message); }
        }

        private static string BigPath => Path.Combine(ThaiModEntry.StreamingAssets, BigRel);

        private static float origSilence = -1f, origPartial = -1f;
        private static string lastVadMode = "";

        // Thai speakers pause mid-sentence; the game finalizes an utterance after 1.25 s of silence.
        // Thai + push-to-talk: only releasing the key ends the sentence. Thai + voice activity: wait 2 s.
        private static void TuneVad()
        {
            bool thai = ThaiModEntry.IsThaiSelected();
            bool ptt = false;
            try { ptt = SettingsUi.Instance != null && SettingsUi.Instance.VoiceChatIsPushToTalkEnabled; } catch { }
            string mode = !thai ? "off" : (ptt ? "ptt" : "vad") + (gpuActive ? "-gpu" : "-cpu") + "-nopartial";
            foreach (var pipe in UnityEngine.Object.FindObjectsByType<WhisperVcPipeline>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var fs = pipe.GetType().GetField("silenceToFinalizeSeconds", BindingFlags.Instance | BindingFlags.NonPublic);
                var fp = pipe.GetType().GetField("partialIntervalSeconds", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fs == null || fp == null) continue;
                if (origSilence < 0f) { origSilence = (float)fs.GetValue(pipe); origPartial = (float)fp.GetValue(pipe); }
                if (!thai) { fs.SetValue(pipe, origSilence); fp.SetValue(pipe, origPartial); }
                else
                {
                    fs.SetValue(pipe, ptt ? 15f : Math.Max(origSilence, 2.0f));
                    fp.SetValue(pipe, 1000f); // no live partial passes: one recognition per sentence
                }
            }
            if (mode != lastVadMode)
            {
                lastVadMode = mode;
                ThaiModEntry.Log("speech detection mode: " + mode + " (default silence " + origSilence + " s)");
            }
        }

        public static void Tick(MonoBehaviour host)
        {
            try { TuneVad(); } catch (Exception e) { ThaiModEntry.Warn("vad: " + e.Message); }
            if (bigBad || !ThaiModEntry.IsThaiSelected()) return;
            if (ThaiCloudStt.HasKey && swapped == null) return;
            if (!File.Exists(BigPath))
            {
                if (!downloading && !downloadFailed) host.StartCoroutine(Download());
                return;
            }
            if (swapped != null)
            {
                if (swapped.IsLoading) return;
                if (swapped.IsLoaded)
                {
                    if (!threadsSet) { threadsSet = true; SetThreads(swapped); }
                    if (tooSlow)
                    {
                        ThaiModEntry.Warn("large Whisper model too slow on this PC (" + lastInferSec.ToString("0.0") + " s); reverting to base model");
                        bigBad = true;
                        Repoint(swapped, BaseRel);
                        if (swappedPipe != null) Invoke(swappedPipe, "ApplyCallLanguage", "th");
                        swapped = null;
                    }
                    return;
                }
                if (Time.unscaledTime - swapStarted > 150f)
                {
                    ThaiModEntry.Warn("large Whisper model did not load; reverting to base model");
                    bigBad = true;
                    Repoint(swapped, BaseRel);
                    swapped = null;
                }
                return;
            }
            foreach (var pipe in UnityEngine.Object.FindObjectsByType<WhisperVcPipeline>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var mgr = GetField<WhisperManager>(pipe, "whisperManager");
                if (mgr == null || mgr.ModelPath == BigRel || mgr.IsLoading) continue;
                if (GetField<bool>(pipe, "inferenceInProgress")) continue;
                ThaiModEntry.Log("switching Thai speech recognition to " + BigRel);
                EnableGpu(mgr, pipe);
                Repoint(mgr, BigRel);
                Invoke(pipe, "ApplyCallLanguage", "th");
                swapped = mgr;
                swappedPipe = pipe;
                swapStarted = Time.unscaledTime;
            }
        }

        private static void Repoint(WhisperManager mgr, string rel)
        {
            if (mgr.IsLoaded)
            {
                // No unload API: drop the loaded context so InitModel() can load the new file.
                typeof(WhisperManager).GetField("_whisper", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mgr, null);
            }
            typeof(WhisperManager).GetField("modelPath", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(mgr, rel);
        }

        private static T GetField<T>(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f == null ? default : (T)f.GetValue(o);
        }

        private static void Invoke(object o, string name, params object[] args)
        {
            o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(o, args);
        }

        private sealed class FileSink : DownloadHandlerScript
        {
            private readonly FileStream fs;
            public long Received, Expected = -1;
            private int lastPct = -10;
            public FileSink(string path) : base(new byte[256 * 1024]) { fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None); }
            protected override void ReceiveContentLengthHeader(ulong contentLength) { Expected = (long)contentLength; }
            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                fs.Write(data, 0, dataLength);
                Received += dataLength;
                if (Expected > 0)
                {
                    int pct = (int)(Received * 100 / Expected);
                    if (pct >= lastPct + 10) { lastPct = pct; ThaiModEntry.Log("Whisper model download " + pct + "%"); }
                }
                return true;
            }
            public void Close() { fs.Flush(); fs.Dispose(); }
        }

        private static IEnumerator Download()
        {
            downloading = true;
            string part = BigPath + ".part";
            ThaiModEntry.Log("downloading " + Url + " (~550 MB, one time)");
            FileSink sink = null;
            UnityWebRequest req = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BigPath));
                sink = new FileSink(part);
                req = new UnityWebRequest(Url, "GET") { downloadHandler = sink, disposeDownloadHandlerOnDispose = false };
            }
            catch (Exception e)
            {
                ThaiModEntry.Warn("cannot start Whisper download: " + e.Message);
                downloadFailed = true; downloading = false; sink?.Close(); yield break;
            }
            yield return req.SendWebRequest();
            sink.Close();
            bool ok = req.result == UnityWebRequest.Result.Success && sink.Received >= MinBytes && (sink.Expected <= 0 || sink.Received == sink.Expected);
            if (ok)
            {
                try { if (File.Exists(BigPath)) File.Delete(BigPath); File.Move(part, BigPath); ThaiModEntry.Log("Whisper model ready: " + sink.Received + " bytes"); }
                catch (Exception e) { ok = false; ThaiModEntry.Warn("move failed: " + e.Message); }
            }
            if (!ok)
            {
                ThaiModEntry.Warn("Whisper download failed: " + req.result + " " + req.error + " received=" + sink.Received);
                try { File.Delete(part); } catch { }
                downloadFailed = true;
            }
            req.Dispose();
            sink.Dispose();
            downloading = false;
        }
    }
}
