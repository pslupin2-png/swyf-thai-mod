using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Kolkata.Speech;
using UnityEngine;

namespace SWYF.ThaiMod
{
    // Called by Unity via an entry in RuntimeInitializeOnLoads.json (loadTypes 1 = BeforeSceneLoad).
    public static class ThaiModEntry
    {
        public const string Version = "0.8.2";
        private static bool done;
        internal static string ModDir;      // StreamingAssets/SWYF.ThaiMod
        internal static string StreamingAssets;

        public static void Init()
        {
            if (done) return;
            done = true;
            Log("starting v" + Version);
            StreamingAssets = Application.streamingAssetsPath;
            ModDir = Path.Combine(StreamingAssets, "SWYF.ThaiMod");

            // Sentinel table first: if it fails, do NOT add Thai, otherwise the AI breaks for every language.
            if (Safe("sentinel profile", AddThaiSentinelProfile))
                Safe("call language", AddThaiCallLanguage);

            Safe("thai tts setup", ThaiTts.Setup);
            Safe("groq stt setup", ThaiCloudStt.Setup);
            Safe("voices setup", ThaiVoices.Setup);
            Safe("behaviour", () =>
            {
                var go = new GameObject("SWYF.ThaiMod");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<ThaiModBehaviour>();
            });
        }

        internal static void Log(string msg) => Debug.Log("[ThaiMod] " + msg);
        internal static void Warn(string msg) => Debug.LogWarning("[ThaiMod] " + msg);

        internal static bool Safe(string what, Action a)
        {
            try { a(); Log("OK: " + what); return true; }
            catch (Exception e) { Debug.LogError("[ThaiMod] FAILED: " + what + " -> " + e); return false; }
        }

        internal static bool IsThaiSelected()
        {
            try { return CallLanguage.Current != null && CallLanguage.Current.Code == "th"; }
            catch { return false; }
        }

        private static void AddThaiSentinelProfile()
        {
            var t = typeof(SentinelSpeechLanguage);
            var profiles = (Dictionary<string, SentinelSpeechLanguage>)t.GetField("Profiles", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            if (profiles.ContainsKey("th")) return;
            var ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) }, null);
            profiles["th"] = (SentinelSpeechLanguage)ctor.Invoke(new object[] {
                "th",
                "ศูนย์|หนึ่ง|สอง|สาม|สี่|ห้า|หก|เจ็ด|แปด|เก้า",
                "เอ|บี|ซี|ดี|อี|เอฟ|จี|เอช|ไอ|เจ|เค|แอล|เอ็ม|เอ็น|โอ|พี|คิว|อาร์|เอส|ที|ยู|วี|ดับเบิลยู|เอ็กซ์|วาย|แซด",
                "แอท|จุด|ขีด|ขีดล่าง|บวก|ทับ",
                "นามสกุลเดิมของแม่|ชื่อสัตว์เลี้ยงตัวแรก|สีที่ชอบ",
                "แดง|น้ำเงิน|เขียว|ม่วง|เหลือง|ส้ม|ชมพู|ดำ|ขาว|สีรุ้ง|เขียวอมฟ้า|ทอง|เงิน|น้ำตาล|คราม" });
        }

        private static void AddThaiCallLanguage()
        {
            var existing = CallLanguage.Options; // loads the JSON catalog
            if (existing.Any(o => o.Code == "th")) return;
            var thai = new CallLanguageOption("th", "ไทย", "Thai", false, "");
            var inner = existing.GetType().GetField("list", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(existing) as IList<CallLanguageOption>;
            if (inner != null && !inner.IsReadOnly) inner.Add(thai);
            else
            {
                var list = existing.ToList(); list.Add(thai);
                typeof(CallLanguage).GetField("Options", BindingFlags.Static | BindingFlags.Public)
                    .SetValue(null, new ReadOnlyCollection<CallLanguageOption>(list));
            }
            if (!CallLanguage.Options.Any(o => o.Code == "th")) throw new Exception("Thai was not added.");
        }
    }

    public class ThaiModBehaviour : MonoBehaviour
    {
        private float nextTick;

        private void Start()
        {
            StartCoroutine(ThaiMenu.Run(this));
            CallLanguage.Changed += OnCallLanguageChanged;
            if (ThaiModEntry.IsThaiSelected()) ThaiTts.Prewarm();
        }

        private void OnDestroy()
        {
            CallLanguage.Changed -= OnCallLanguageChanged;
        }

        private void OnCallLanguageChanged(string code)
        {
            if (code == "th") ThaiTts.Prewarm();
        }

        private void Update()
        {
            try { ThaiText.SetUi(LanguageManager.EffectiveCode == "th"); } catch { }
            try { ThaiWhisper.Watch(); } catch { }
            try { ThaiVoices.Pump(this); } catch (Exception e) { ThaiModEntry.Warn("voice pump: " + e.Message); }
            try { ThaiCloudStt.Pump(this); } catch (Exception e) { ThaiModEntry.Warn("groq pump: " + e.Message); }
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            try { ThaiWhisper.Tick(this); }
            catch (Exception e) { ThaiModEntry.Warn("whisper tick: " + e.Message); }
        }
    }
}
