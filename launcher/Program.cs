using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SWYF.ThaiMod.Launcher
{
    internal static class Program
    {
        // Steam launch option:  "<game>\SWYF_ThaiMod\ThaiModLauncher.exe" %command%
        // Steam then passes the game's own command line as our arguments. We repair the mod if the game
        // was updated, then start the game and wait for it, so Steam still sees the game running.
        [STAThread]
        private static int Main(string[] args)
        {
            string here = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string gameExe = args.Length > 0 ? args[0] : null;
            string gameDir = gameExe != null && File.Exists(gameExe) ? Path.GetDirectoryName(Path.GetFullPath(gameExe)) : Directory.GetParent(here).FullName;

            // look for a newer mod release while we patch (Steam start only; the window does its own check)
            UpdateInfo update = null;
            System.Threading.Thread updateThread = null;
            var updateLog = new StringBuilder();
            if (gameExe != null)
            {
                updateThread = new System.Threading.Thread(() => update = Updater.CheckForStart(here, 2500, updateLog)) { IsBackground = true };
                updateThread.Start();
            }

            var log = new StringBuilder();
            log.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  SWYF Thai mod launcher " + typeof(Program).Assembly.GetName().Version + "  patch v" + ThaiPatcher.PatchVersion);
            log.AppendLine("game: " + gameDir + (gameExe != null ? "  (started by Steam)" : "  (opened by hand)"));
            PatchResult result = null;
            Exception error = null;
            try
            {
                result = ThaiPatcher.Run(gameDir, Path.Combine(here, "payload"));
                foreach (var l in result.Lines) log.AppendLine(l);
            }
            catch (Exception e) { error = e; log.AppendLine("ERROR " + e); }
            bool updateKnown = updateThread != null && updateThread.Join(3000);
            if (updateKnown) log.Append(updateLog);
            try { File.WriteAllText(Path.Combine(here, "launcher_log.txt"), log.ToString(), new UTF8Encoding(false)); } catch { }

            if (gameExe != null)
            {
                if (updateKnown && update != null) TellAboutUpdate(update);
                return StartGame(args, gameDir, log);
            }

            string option = "\"" + Path.Combine(here, "ThaiModLauncher.exe") + "\" %command%";
            bool ok = error == null && result != null && !result.Failed;
            string message;
            if (ok) message = result.Changed ? "ติดตั้งม็อดภาษาไทยเรียบร้อยแล้ว" : "ม็อดภาษาไทยติดตั้งอยู่แล้ว พร้อมเล่น";
            else
            {
                string why = error != null ? error.Message : string.Join("\n", result.Lines.Where(l => l.Contains("FAILED") || l.Contains("not found") || l.Contains("missing") || l.Contains("unknown version")).Take(4));
                message = "ติดตั้งม็อดไม่สำเร็จ\n" + why;
            }
            string data = ThaiPatcher.FindDataDir(gameDir);
            string modDir = data != null ? Path.Combine(data, "StreamingAssets", "SWYF.ThaiMod") : Path.Combine(here, "keys");
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new KeysForm(modDir, option, FindAppId(gameDir), ok, message));
            return ok ? 0 : 1;
        }

        // Shown before the game starts, at most once a day per new version. The game starts either way.
        private static void TellAboutUpdate(UpdateInfo u)
        {
            try
            {
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                using (var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Size = new System.Drawing.Size(1, 1), Opacity = 0 })
                {
                    owner.Show();
                    var answer = MessageBox.Show(owner,
                        "มีม็อดภาษาไทยเวอร์ชันใหม่: " + u.Tag + "  (เครื่องนี้ใช้ v" + Updater.Current + ")\n\n" +
                        "เปิดหน้าดาวน์โหลดตอนนี้ไหม?\nเลือกแบบไหนเกมก็เปิดต่อตามปกติ",
                        "SWYF ม็อดภาษาไทย", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (answer == DialogResult.Yes)
                        Process.Start(new ProcessStartInfo(u.Url) { UseShellExecute = true });
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        // steamapps\appmanifest_<id>.acf whose installdir is this game's folder
        private static string FindAppId(string gameDir)
        {
            try
            {
                string common = Path.GetDirectoryName(gameDir.TrimEnd('\\', '/'));
                string steamapps = Path.GetDirectoryName(common);
                string folder = Path.GetFileName(gameDir.TrimEnd('\\', '/'));
                foreach (var f in Directory.GetFiles(steamapps, "appmanifest_*.acf"))
                {
                    string t = File.ReadAllText(f);
                    if (t.IndexOf("\"installdir\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        t.IndexOf("\"" + folder + "\"", StringComparison.OrdinalIgnoreCase) >= 0)
                        return Path.GetFileNameWithoutExtension(f).Substring("appmanifest_".Length);
                }
            }
            catch { }
            return null;
        }

        private static int StartGame(string[] args, string gameDir, StringBuilder log)
        {
            try
            {
                var psi = new ProcessStartInfo(args[0], JoinArgs(args.Skip(1)))
                {
                    UseShellExecute = false,
                    WorkingDirectory = gameDir,
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                MessageBox.Show("เปิดเกมไม่ได้: " + e.Message + "\n\nลบข้อความใน Launch Options ของเกมใน Steam แล้วเปิดเกมใหม่",
                    "SWYF Thai Mod", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string JoinArgs(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) { sb.Append(a); continue; }
                sb.Append('"');
                int slashes = 0;
                foreach (char c in a)
                {
                    if (c == '\\') { slashes++; continue; }
                    if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
                    else { sb.Append('\\', slashes); sb.Append(c); }
                    slashes = 0;
                }
                sb.Append('\\', slashes * 2);
                sb.Append('"');
            }
            return sb.ToString();
        }
    }
}
