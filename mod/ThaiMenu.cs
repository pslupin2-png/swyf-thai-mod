using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Metadata;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace SWYF.ThaiMod
{
    // Adds a real "th" Locale to Unity Localization and registers in-memory Thai StringTables built from
    // StreamingAssets/SWYF.ThaiMod/strings_th.tsv (table \t id \t text, with \\ \t \n escapes).
    public static class ThaiMenu
    {
        private static readonly Dictionary<string, StringTable> thaiTables = new Dictionary<string, StringTable>();
        private static Locale thaiLocale;
        private static bool refreshing;

        public static IEnumerator Run(MonoBehaviour host)
        {
            var init = LocalizationSettings.InitializationOperation;
            while (!init.IsDone) yield return null;
            if (!ThaiModEntry.Safe("thai menu tables", Build)) yield break;

            LocalizationSettings.SelectedLocaleChanged += OnSelectedLocaleChanged;
            EnsureRegistered();

            // If the player had Thai selected, the game picked English before our locale existed: re-apply now.
            if (LanguageManager.EffectiveCode == "th" && LanguageManager.Instance != null)
            {
                var m = typeof(LanguageManager).GetMethod("ApplySelection", BindingFlags.Instance | BindingFlags.NonPublic);
                if (m != null) LanguageManager.Instance.StartCoroutine((IEnumerator)m.Invoke(LanguageManager.Instance, null));
                ThaiModEntry.Log("re-applied Thai UI language");
            }
        }

        private static void Build()
        {
            string path = Path.Combine(ThaiModEntry.ModDir, "strings_th.tsv");
            if (!File.Exists(path)) throw new FileNotFoundException("missing " + path);

            var en = LocalizationSettings.AvailableLocales.GetLocale("en");
            if (en == null) throw new Exception("English locale not found");

            thaiLocale = LocalizationSettings.AvailableLocales.GetLocale("th");
            if (thaiLocale == null)
            {
                thaiLocale = Locale.CreateLocale("th");
                thaiLocale.LocaleName = "Thai (th)";
                thaiLocale.Metadata.AddMetadata(new FallbackLocale(en));
                var provider = LocalizationSettings.AvailableLocales as LocalesProvider;
                if (provider != null) provider.AddLocale(thaiLocale);
                else LocalizationSettings.AvailableLocales.Locales.Add(thaiLocale);
            }

            int count = 0;
            foreach (var rawLine in File.ReadAllText(path, Encoding.UTF8).Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                int a = line.IndexOf('\t'); if (a < 0) continue;
                int b = line.IndexOf('\t', a + 1); if (b < 0) continue;
                string table = line.Substring(0, a);
                long id;
                if (!long.TryParse(line.Substring(a + 1, b - a - 1), out id)) continue;
                string text = Unescape(line.Substring(b + 1));

                StringTable th;
                if (!thaiTables.TryGetValue(table, out th))
                {
                    var enTable = LocalizationSettings.StringDatabase.GetTable(table, en);
                    if (enTable == null) { ThaiModEntry.Warn("no English table " + table); thaiTables[table] = null; continue; }
                    th = ScriptableObject.CreateInstance<StringTable>();
                    th.name = table + "_th";
                    th.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    th.LocaleIdentifier = "th";
                    th.SharedData = enTable.SharedData;
                    var smart = enTable.GetMetadata<SmartFormatTag>();
                    if (smart != null) th.AddMetadata(smart);
                    thaiTables[table] = th;
                }
                if (th == null) continue;
                th.AddEntry(id, text);
                count++;
            }
            ThaiModEntry.Log("loaded " + count + " Thai strings into " + thaiTables.Count + " tables");
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') sb.Append('\r');
                    else sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // The string database drops every table on a locale change; put ours back before UI refreshes.
        private static void OnSelectedLocaleChanged(Locale locale)
        {
            if (refreshing) return;
            bool changed = EnsureRegistered();
            if (changed && locale != null && locale.Identifier.Code == "th")
            {
                refreshing = true;
                try
                {
                    var clear = typeof(LanguageManager).GetMethod("ClearTextCaches", BindingFlags.Static | BindingFlags.NonPublic);
                    clear?.Invoke(null, null);
                    LocalizationSettings.Instance.ForceRefresh();
                }
                catch (Exception e) { ThaiModEntry.Warn("refresh: " + e.Message); }
                finally { refreshing = false; }
            }
        }

        private static bool EnsureRegistered()
        {
            bool changed = false;
            var db = LocalizationSettings.StringDatabase;
            var opsProp = db.GetType().GetProperty("TableOperations", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var register = db.GetType().GetMethod("RegisterCompletedTableOperation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var ops = opsProp.GetValue(db) as IDictionary<(LocaleIdentifier, string), AsyncOperationHandle<StringTable>>;
            foreach (var kv in thaiTables)
            {
                if (kv.Value == null) continue;
                var key = ((LocaleIdentifier)"th", kv.Key);
                AsyncOperationHandle<StringTable> existing;
                if (ops != null && ops.TryGetValue(key, out existing) && existing.IsValid() && existing.IsDone && existing.Result == kv.Value) continue;
                if (ops != null)
                {
                    ops.Remove(key);
                    var sfg = typeof(TableReference).GetMethod("StringFromGuid", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (sfg != null)
                    {
                        string g = (string)sfg.Invoke(null, new object[] { kv.Value.SharedData.TableCollectionNameGuid });
                        ops.Remove(((LocaleIdentifier)"th", g));
                    }
                }
                var handle = Addressables.ResourceManager.CreateCompletedOperation(kv.Value, null);
                register.Invoke(db, new object[] { handle });
                changed = true;
            }
            return changed;
        }
    }
}
