using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SWYF.ThaiMod.Launcher
{
    // Window shown when ThaiModLauncher.exe is opened by hand: install status, API keys, Steam launch option.
    internal sealed class KeysForm : Form
    {
        private readonly string modDir, launchOption, appId;
        private readonly TextBox groqBox = new TextBox(), voiceBox = new TextBox(), optionBox = new TextBox();
        private readonly RadioButton rbWindows = new RadioButton(), rbOpenAI = new RadioButton(), rbGemini = new RadioButton();
        private readonly CheckBox showKeys = new CheckBox();
        private readonly Label voiceHint = new Label(), status = new Label();
        private readonly LinkLabel voiceLink = new LinkLabel(), updateLink = new LinkLabel();
        private readonly Button saveBtn = new Button(), playBtn = new Button(), copyBtn = new Button();
        private string openaiKey = "", geminiKey = "", geminiModel = "";

        private static readonly Color Ok = Color.FromArgb(0, 128, 60), Bad = Color.FromArgb(190, 30, 30), Muted = Color.FromArgb(95, 95, 95);

        public KeysForm(string modDir, string launchOption, string appId, bool installOk, string installMessage)
        {
            this.modDir = modDir; this.launchOption = launchOption; this.appId = appId;
            Text = "SWYF ม็อดภาษาไทย v" + Updater.Current;
            Font = new Font(FontFamily.Families.Any(f => f.Name == "Leelawadee UI") ? "Leelawadee UI" : "Tahoma", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            Controls.Add(root);

            // install status
            var head = new Label
            {
                AutoSize = true, MaximumSize = new Size(560, 0),
                Text = (installOk ? "✓ " : "⚠ ") + installMessage,
                ForeColor = installOk ? Ok : Bad,
                Font = new Font(Font.FontFamily, 11.5f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 10)
            };
            root.Controls.Add(head);

            // newer release on GitHub? (filled in by a background check)
            updateLink.AutoSize = true; updateLink.Visible = false; updateLink.Margin = new Padding(0, 0, 0, 10);
            updateLink.Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
            updateLink.LinkClicked += (s, e) => Open((string)updateLink.Tag);
            root.Controls.Add(updateLink);
            new Thread(() =>
            {
                var u = Updater.FetchLatest(6000);
                if (!Updater.IsNewer(u)) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        updateLink.Text = "มีเวอร์ชันใหม่ " + u.Tag + " · กดตรงนี้เพื่อไปดาวน์โหลด";
                        updateLink.Tag = u.Url;
                        updateLink.Visible = true;
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();

            // 1. Groq
            var g1 = Group("1. ฟังเสียงพูดภาษาไทย (Groq — ฟรี)");
            groqBox.Width = 420; groqBox.UseSystemPasswordChar = true;
            g1.Controls.Add(Row(groqBox, Link("ขอคีย์ฟรี", "https://console.groq.com/keys")));
            g1.Controls.Add(Hint("คีย์ขึ้นต้นด้วย gsk_ · ถ้าเว้นว่าง ม็อดจะใช้ Whisper ในเครื่องแทน (ช้ากว่าและต้องโหลด ~550MB)"));
            root.Controls.Add(g1);

            // 2. voices
            var g2 = Group("2. เสียงลูกค้าแบบธรรมชาติ แยกชาย/หญิง (ไม่บังคับ)");
            rbWindows.Text = "เสียง Windows (ฟรี)"; rbOpenAI.Text = "OpenAI"; rbGemini.Text = "Gemini";
            foreach (var rb in new[] { rbWindows, rbOpenAI, rbGemini }) { rb.AutoSize = true; rb.Margin = new Padding(0, 2, 18, 2); rb.CheckedChanged += (s, e) => OnProvider(); }
            g2.Controls.Add(Row(rbWindows, rbOpenAI, rbGemini));
            voiceBox.Width = 420; voiceBox.UseSystemPasswordChar = true;
            voiceLink.AutoSize = true; voiceLink.Margin = new Padding(8, 5, 0, 0);
            voiceLink.LinkClicked += (s, e) => Open((string)voiceLink.Tag);
            g2.Controls.Add(Row(voiceBox, voiceLink));
            voiceHint.AutoSize = true; voiceHint.MaximumSize = new Size(540, 0); voiceHint.ForeColor = Muted;
            g2.Controls.Add(voiceHint);
            root.Controls.Add(g2);

            showKeys.Text = "แสดงคีย์"; showKeys.AutoSize = true; showKeys.Margin = new Padding(2, 0, 0, 8);
            showKeys.CheckedChanged += (s, e) => { groqBox.UseSystemPasswordChar = voiceBox.UseSystemPasswordChar = !showKeys.Checked; };
            root.Controls.Add(showKeys);

            // 3. launch option
            var g3 = Group("3. ให้ม็อดลงใหม่เองทุกครั้งที่เกมอัปเดต (ตั้งครั้งเดียว)");
            g3.Controls.Add(Hint("Steam > คลิกขวาเกม > Properties > General > Launch Options แล้ววางข้อความนี้:"));
            optionBox.ReadOnly = true; optionBox.Width = 440; optionBox.Text = launchOption;
            copyBtn.Text = "คัดลอก"; copyBtn.AutoSize = true; copyBtn.Click += (s, e) => { try { Clipboard.SetText(launchOption); SetStatus("คัดลอกแล้ว ไปวางใน Launch Options ของ Steam ได้เลย", Ok); } catch { } };
            g3.Controls.Add(Row(optionBox, copyBtn));
            root.Controls.Add(g3);

            // bottom
            status.AutoSize = true; status.MaximumSize = new Size(560, 0); status.Margin = new Padding(0, 6, 0, 6);
            root.Controls.Add(status);
            saveBtn.Text = "บันทึกคีย์"; saveBtn.AutoSize = true; saveBtn.Padding = new Padding(10, 2, 10, 2);
            saveBtn.Click += (s, e) => Save(false);
            playBtn.Text = "บันทึกแล้วเล่นเลย"; playBtn.AutoSize = true; playBtn.Padding = new Padding(10, 2, 10, 2);
            playBtn.Click += (s, e) => Save(true);
            playBtn.Visible = appId != null;
            var bottom = Row(saveBtn, playBtn);
            bottom.Anchor = AnchorStyles.Right;
            root.Controls.Add(bottom);
            AcceptButton = saveBtn;

            LoadKeys();
            if (!installOk) SetStatus("ปิดเกมก่อน แล้วเปิดโปรแกรมนี้ใหม่ · รายละเอียดอยู่ใน launcher_log.txt", Bad);
        }

        // ---------- layout helpers ----------
        private FlowLayoutPanel Group(string title)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            p.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) });
            return p;
        }
        private static FlowLayoutPanel Row(params Control[] cs)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            p.Controls.AddRange(cs);
            return p;
        }
        private Label Hint(string text) => new Label { Text = text, AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = Muted, Margin = new Padding(0, 2, 0, 2) };
        private LinkLabel Link(string text, string url)
        {
            var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(8, 5, 0, 0) };
            l.LinkClicked += (s, e) => Open(url);
            return l;
        }
        private static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
        private void SetStatus(string text, Color c) { status.Text = text; status.ForeColor = c; }

        // ---------- keys ----------
        private string GroqPath => Path.Combine(modDir, "groq_key.txt");
        private string VoicePath => Path.Combine(modDir, "voice_keys.txt");

        private void LoadKeys()
        {
            try
            {
                if (File.Exists(GroqPath))
                    groqBox.Text = File.ReadAllText(GroqPath).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 10 && !l.StartsWith("#")) ?? "";
                if (File.Exists(VoicePath))
                    foreach (var raw in File.ReadAllText(VoicePath).Split('\n'))
                    {
                        string line = raw.Trim(); bool off = line.StartsWith("#");
                        if (off) line = line.TrimStart('#').Trim();
                        int eq = line.IndexOf('='); if (eq < 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                        if (v.Length < 20 && k != "gemini_model") continue;
                        if (k == "openai" && (openaiKey == "" || !off)) openaiKey = v;
                        if (k == "gemini" && (geminiKey == "" || !off)) geminiKey = v;
                        if (k == "gemini_model" && !off) geminiModel = v;
                    }
                // active provider = what the mod would use (Gemini wins when both are active)
                string active = ActiveProvider();
                (active == "gemini" ? rbGemini : active == "openai" ? rbOpenAI : rbWindows).Checked = true;
            }
            catch (Exception e) { SetStatus("อ่านไฟล์คีย์ไม่ได้: " + e.Message, Bad); rbWindows.Checked = true; }
            OnProvider();
        }

        private string ActiveProvider()
        {
            if (!File.Exists(VoicePath)) return "";
            foreach (var raw in File.ReadAllText(VoicePath).Split('\n'))
            {
                string line = raw.Trim(); if (line.StartsWith("#")) continue;
                if (line.StartsWith("gemini=") && line.Length >= 27) return "gemini";
            }
            foreach (var raw in File.ReadAllText(VoicePath).Split('\n'))
            {
                string line = raw.Trim(); if (line.StartsWith("#")) continue;
                if (line.StartsWith("openai=") && line.Length >= 27) return "openai";
            }
            return "";
        }

        private string current = null;
        private void OnProvider()
        {
            // remember what was typed for the provider we are leaving
            if (current == "openai") openaiKey = voiceBox.Text.Trim();
            if (current == "gemini") geminiKey = voiceBox.Text.Trim();
            current = rbOpenAI.Checked ? "openai" : rbGemini.Checked ? "gemini" : "windows";
            voiceBox.Enabled = current != "windows";
            voiceLink.Visible = current != "windows";
            if (current == "openai")
            {
                voiceBox.Text = openaiKey; voiceLink.Text = "ขอคีย์ OpenAI"; voiceLink.Tag = "https://platform.openai.com/api-keys";
                voiceHint.Text = "คีย์ขึ้นต้นด้วย sk- · ต้องเติมเครดิตในบัญชี OpenAI ก่อน (คิดเงินตามการใช้)";
            }
            else if (current == "gemini")
            {
                voiceBox.Text = geminiKey; voiceLink.Text = "ขอคีย์ Gemini"; voiceLink.Tag = "https://aistudio.google.com/apikey";
                voiceHint.Text = "คีย์จาก Google AI Studio · ใช้ฟรีได้ระดับหนึ่ง ถ้าใช้เยอะต้องเปิด billing";
            }
            else
            {
                voiceBox.Text = ""; voiceHint.Text = "ใช้เสียงภาษาไทยของ Windows (ต้องติดตั้ง: Settings > Time & language > Speech > Add voices > ไทย)";
            }
        }

        private void Save(bool play)
        {
            if (current == "openai") openaiKey = voiceBox.Text.Trim();
            if (current == "gemini") geminiKey = voiceBox.Text.Trim();
            string groq = groqBox.Text.Trim();
            try
            {
                Directory.CreateDirectory(modDir);
                File.WriteAllText(GroqPath,
                    "# Paste your Groq API key (starts with gsk_) on the line below, save, done.\n" +
                    "# วางคีย์ Groq (ขึ้นต้นด้วย gsk_) ในบรรทัดล่างนี้ แล้วกดบันทึก\n\n" + groq + "\n", new UTF8Encoding(false));
                var sb = new StringBuilder();
                sb.Append("# Natural Thai voices for callers and the boss / เสียงไทยธรรมชาติ\n");
                sb.Append("# ตั้งค่าง่ายกว่า: ดับเบิลคลิก SWYF_ThaiMod\\ThaiModLauncher.exe ในโฟลเดอร์เกม\n");
                sb.Append("# บรรทัดที่ขึ้นต้นด้วย # คือคีย์ที่เก็บไว้แต่ไม่ได้ใช้\n");
                sb.Append(current == "gemini" ? "gemini=" + geminiKey : (geminiKey.Length > 0 ? "#gemini=" + geminiKey : "gemini=")).Append('\n');
                sb.Append(current == "openai" ? "openai=" + openaiKey : (openaiKey.Length > 0 ? "#openai=" + openaiKey : "openai=")).Append('\n');
                sb.Append(geminiModel.Length > 0 ? "gemini_model=" + geminiModel : "# gemini_model=gemini-3.8-flash-tts").Append('\n');
                File.WriteAllText(VoicePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { SetStatus("บันทึกไม่ได้: " + e.Message, Bad); return; }

            SetStatus("บันทึกแล้ว · กำลังตรวจคีย์…", Muted);
            saveBtn.Enabled = playBtn.Enabled = false;
            string provider = current, vkey = provider == "openai" ? openaiKey : provider == "gemini" ? geminiKey : "";
            new Thread(() =>
            {
                var parts = new List<string>(); bool anyBad = false;
                if (groq.Length > 0) parts.Add("Groq " + Check("https://api.groq.com/openai/v1/models", "Bearer " + groq, ref anyBad));
                else parts.Add("Groq: ไม่ได้ใส่ (ใช้ Whisper ในเครื่อง)");
                if (provider == "openai" && vkey.Length > 0) parts.Add("OpenAI " + Check("https://api.openai.com/v1/models", "Bearer " + vkey, ref anyBad));
                else if (provider == "gemini" && vkey.Length > 0) parts.Add("Gemini " + Check("https://generativelanguage.googleapis.com/v1beta/models?key=" + Uri.EscapeDataString(vkey), null, ref anyBad));
                else if (provider != "windows") { parts.Add("ยังไม่ได้ใส่คีย์เสียง (ใช้เสียง Windows)"); }
                else parts.Add("เสียง: Windows");
                string msg = "บันทึกแล้ว · " + string.Join(" · ", parts) + (play && !anyBad ? " · กำลังเปิดเกม…" : "") + "\nเกมเปิดอยู่ก็ไม่ต้องปิด ม็อดอ่านคีย์ใหม่เอง";
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        SetStatus(msg, anyBad ? Bad : Ok);
                        saveBtn.Enabled = playBtn.Enabled = true;
                        if (play && !anyBad) { Open("steam://rungameid/" + appId); Close(); }
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        private static string Check(string url, string auth, ref bool anyBad)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = 10000; req.UserAgent = "SWYF-ThaiMod";
                if (auth != null) req.Headers["Authorization"] = auth;
                using (var resp = (HttpWebResponse)req.GetResponse()) return "✓ ใช้ได้";
            }
            catch (WebException e) when (e.Response is HttpWebResponse r)
            {
                int code = (int)r.StatusCode;
                if (code == 400 || code == 401 || code == 403) { anyBad = true; return "✗ คีย์ไม่ถูกต้อง"; }
                if (code == 429) return "✓ ใช้ได้ (ตอนนี้ใช้ถี่เกิน รอสักพัก)";
                return "? ตรวจไม่ได้ (" + code + ")";
            }
            catch { return "? ตรวจไม่ได้ (เช็คอินเทอร์เน็ต)"; }
        }
    }
}
