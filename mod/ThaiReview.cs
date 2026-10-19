using System;
using System.Collections.Generic;
using Kolkata.Speech;

namespace SWYF.ThaiMod
{
    // Hooks patched into Assembly-CSharp.dll for the end-of-day boss review and PA announcements.
    public static class ThaiReview
    {
        public static bool ThaiActive()
        {
            try
            {
                if (CallLanguage.Current != null && CallLanguage.Current.Code == "th") return true;
            }
            catch { }
            try { return LanguageManager.EffectiveCode == "th"; } catch { return false; }
        }

        public static bool HasThai(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int n = 0;
            foreach (char c in s) if (c >= '฀' && c <= '๿' && ++n >= 3) return true;
            return false;
        }

        // Appended to the boss-review request. The perspective rules matter in Thai: with กู/มึง and no explicit
        // subjects, the model otherwise reacts to a quote like "ขู่พ่อ" as if the player had threatened the boss's father.
        private const string LanguageEntry =
            "{\"language\":{\"name\":\"Thai\",\"code\":\"th\",\"instruction\":\"Write EVERY human-readable string value of your JSON answer " +
            "(deck and slide titles, narrations, bullets, notes, quote paraphrases, verdicts, outro) in natural spoken Thai, " +
            "in Mr Curry's loud, rude boss voice, calling the employees มึง/พวกมึง and himself กู. Match the swearing level the rest of this request asks for: " +
            "if it asks for family-friendly language, use no swear words; otherwise crude Thai insults are fine (e.g. ไอ้พวกขี้เกียจ, ห่าเอ๊ย, บ้าเอ๊ย). " +
            "WHO SAID WHAT: every quote and call note is something an EMPLOYEE said TO A CALLER (a customer on the phone) during a scam call. " +
            "None of it was said to Mr Curry or about him. React as the boss judging how the employee handled that customer: refer to the caller " +
            "in the third person (ลูกค้า, ไอ้ลูกค้า, or the caller's name) and never treat the quote as an insult, threat or claim about the boss, " +
            "his family, his body or his powers. Example: quote ขู่พ่อ means the employee threatened the CUSTOMER's father, so say " +
            "มึงไปขู่พ่อลูกค้าเหรอ, never มึงขู่พ่อกู. Likewise a caller's dark powers, problems or jobs belong to the caller, not to the boss. " +
            "Quote paraphrases must be clear on their own: say who did what to whom (e.g. ขู่ว่าจะทำร้ายพ่อของลูกค้า), not two-word fragments. " +
            "Keep JSON keys, moment ids, player names, money amounts and tier letters (S/A/B/C/D/F) unchanged, and say each player's tier as 'เทียร์ S' etc. " +
            "Player quotes are already Thai; paraphrase them in Thai.\"}}";

        // ReviewEvidencePacket..ctor: UserMessage = Decorate(UserMessage)
        public static string DecorateUserMessage(string message)
        {
            try
            {
                if (!ThaiActive() || string.IsNullOrEmpty(message)) return message;
                string m = message.TrimEnd();
                if (!m.EndsWith("]") || m.Contains("\"language\":{\"name\":\"Thai\"")) return message;
                ThaiModEntry.Log("boss review: asking for Thai");
                return m.Substring(0, m.Length - 1) + "," + LanguageEntry + "]";
            }
            catch { return message; }
        }

        // ReviewBossAiClient.IsWeakNoteResponse / RankingNarrationUsesCorrectTier: the stock checks look for
        // English swear words and "X tier"; accept Thai copy instead of replacing it with English templates.
        public static bool AcceptThaiText(string text)
        {
            return text != null && text.Trim().Length >= 6 && HasThai(text);
        }

        // ReviewWhiteboardNarrator.Speak: was hard-coded "en". Thai lines go to the Thai voice; English
        // fallback lines keep the boss's original English voice.
        public static string NarrationLanguage()
        {
            return ThaiActive() ? "th" : "en";
        }

        public static string NarrationLanguageFor(string text)
        {
            return HasThai(text) ? "th" : "en";
        }

        private static readonly Dictionary<string, string> Canned = new Dictionary<string, string>
        {
            ["Okay."] = "โอเค",
            ["And today..."] = "แล้ววันนี้...",
            ["Okay. And today..."] = "โอเค แล้ววันนี้...",
            ["You are fired."] = "แกโดนไล่ออก!",
            ["All right, new hires! Go answer the phones and scam!"] =
                "เอาล่ะ ไอ้พวกเด็กใหม่! ไปรับโทรศัพท์แล้วต้มตุ๋นซะ!",
            ["Attention, employees. Welcome to floor two. You have reached the next level of the business. Your salary has also gone up. By one floor. Please enjoy the upgraded break room. The breaks themselves are still imaginary."] =
                "ประกาศ พนักงานทุกคน ยินดีต้อนรับสู่ชั้นสอง พวกแกได้ขึ้นมาอีกระดับของธุรกิจแล้ว เงินเดือนก็ขึ้นด้วยนะ ขึ้นมาหนึ่งชั้น เชิญเพลิดเพลินกับห้องพักที่อัปเกรดแล้ว ส่วนเวลาพักน่ะ ยังเป็นแค่จินตนาการเหมือนเดิม",
            ["Attention, employees. Welcome to floor three. We are now a world class operation. Same scam, higher ceiling. You are the upper management now. You work upstairs, and I manage to keep all the money. Get to work."] =
                "ประกาศ พนักงานทุกคน ยินดีต้อนรับสู่ชั้นสาม ตอนนี้เราเป็นองค์กรระดับโลกแล้ว สแกมเหมือนเดิม แค่เพดานสูงขึ้น พวกแกคือผู้บริหารระดับสูงแล้ว พวกแกทำงานชั้นบน ส่วนฉันก็บริหารเงินทั้งหมดเข้ากระเป๋าตัวเอง ไปทำงานซะ!",
        };

        // OfficePromotionAnnouncements.DialogueForDay: translate the PA lines.
        public static string CannedLine(string english)
        {
            if (string.IsNullOrEmpty(english) || !ThaiActive()) return english;
            return Canned.TryGetValue(english, out var th) ? th : english;
        }
    }
}
