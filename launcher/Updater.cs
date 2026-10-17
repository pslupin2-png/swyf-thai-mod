using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SWYF.ThaiMod.Launcher
{
    internal sealed class UpdateInfo
    {
        public string Tag;      // e.g. "v0.7.3"
        public string Url;      // release page
        public Version Version;
    }

    // Looks for a newer release on GitHub. Never throws; any problem just means "no update known".
    internal static class Updater
    {
        public const string Repo = "pslupin2-png/swyf-thai-mod";
        public static readonly string ReleasesUrl = "https://github.com/" + Repo + "/releases/latest";
        private const string StateFile = "update_check.txt";

        public static Version Current
        {
            get { var v = typeof(Updater).Assembly.GetName().Version; return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        }

        // Fresh lookup. Returns the latest release, or null if it can't be reached.
        public static UpdateInfo FetchLatest(int timeoutMs)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            // 1) GitHub API
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
                req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "SWYF-ThaiMod-Launcher";
                req.Accept = "application/vnd.github+json";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    var url = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/releases/tag/[^\"]+)\"");
                    if (tag.Success) return Make(tag.Groups[1].Value, url.Success ? url.Groups[1].Value : null);
                }
            }
            catch { }
            // 2) the /releases/latest redirect (works when the API is rate limited)
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(ReleasesUrl);
                req.Method = "HEAD"; req.AllowAutoRedirect = false;
                req.Timeout = timeoutMs; req.UserAgent = "SWYF-ThaiMod-Launcher";
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    string loc = resp.Headers["Location"] ?? "";
                    int i = loc.IndexOf("/releases/tag/", StringComparison.Ordinal);
                    if (i > 0) return Make(Uri.UnescapeDataString(loc.Substring(i + "/releases/tag/".Length)), loc);
                }
            }
            catch { }
            return null;
        }

        private static UpdateInfo Make(string tag, string url)
        {
            var v = ParseVersion(tag);
            if (v == null) return null;
            return new UpdateInfo { Tag = tag, Url = url ?? ReleasesUrl, Version = v };
        }

        // "v0.7.3", "0.7.3", "v0.8" -> Version; anything else -> null
        public static Version ParseVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            var m = Regex.Match(tag.Trim(), "^[vV]?(\\d+)\\.(\\d+)(?:\\.(\\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        public static bool IsNewer(UpdateInfo u) => u != null && u.Version > Current;

        // Steam start: look at most twice a day, and nag about one version at most once a day.
        // Returns an update worth telling the player about now, or null.
        public static UpdateInfo CheckForStart(string dir, int timeoutMs, StringBuilder log)
        {
            try
            {
                string path = Path.Combine(dir, StateFile);
                var st = State.Load(path);
                DateTime now = DateTime.UtcNow;
                UpdateInfo latest = null;
                if (st.CheckedAt.HasValue && (now - st.CheckedAt.Value).TotalHours < 12 && st.Tag != null)
                {
                    latest = Make(st.Tag, st.Url);
                    log?.AppendLine("update: cached latest " + st.Tag);
                }
                else
                {
                    latest = FetchLatest(timeoutMs);
                    log?.AppendLine("update: latest " + (latest != null ? latest.Tag : "unknown (offline?)"));
                    if (latest != null) { st.Tag = latest.Tag; st.Url = latest.Url; }
                    st.CheckedAt = now; // also after a failure, so an offline PC doesn't wait every start
                }
                if (!IsNewer(latest)) { st.Save(path); return null; }
                if (st.ToldTag == latest.Tag && st.ToldAt.HasValue && (now - st.ToldAt.Value).TotalHours < 24) { st.Save(path); return null; }
                st.ToldTag = latest.Tag; st.ToldAt = now;
                st.Save(path);
                return latest;
            }
            catch (Exception e) { log?.AppendLine("update: " + e.Message); return null; }
        }

        private sealed class State
        {
            public DateTime? CheckedAt, ToldAt;
            public string Tag, Url, ToldTag;

            public static State Load(string path)
            {
                var s = new State();
                try
                {
                    if (!File.Exists(path)) return s;
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        int eq = raw.IndexOf('='); if (eq <= 0) continue;
                        string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                        if (v.Length == 0) continue;
                        switch (k)
                        {
                            case "checked": s.CheckedAt = Time(v); break;
                            case "told": s.ToldAt = Time(v); break;
                            case "latest": s.Tag = v; break;
                            case "url": s.Url = v; break;
                            case "told_tag": s.ToldTag = v; break;
                        }
                    }
                }
                catch { }
                return s;
            }

            public void Save(string path)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.Append("# SWYF Thai mod: last update check (safe to delete)\n");
                    if (CheckedAt.HasValue) sb.Append("checked=").Append(CheckedAt.Value.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
                    if (Tag != null) sb.Append("latest=").Append(Tag).Append('\n');
                    if (Url != null) sb.Append("url=").Append(Url).Append('\n');
                    if (ToldTag != null) sb.Append("told_tag=").Append(ToldTag).Append('\n');
                    if (ToldAt.HasValue) sb.Append("told=").Append(ToldAt.Value.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }

            private static DateTime? Time(string v)
            {
                DateTime t;
                return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t.ToUniversalTime() : (DateTime?)null;
            }
        }
    }
}
