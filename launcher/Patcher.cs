// SWYF Thai mod patcher: installs/repairs the mod in the player's own game files.
// Shared by the Windows launcher (net48) and the Linux test harness.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace SWYF.ThaiMod.Launcher
{
    public sealed class PatchResult
    {
        public readonly List<string> Lines = new List<string>();
        public bool Changed, Failed;
        public void Log(string s) { Lines.Add(s); }
    }

    // Reads every assembly fully into memory so no game file stays locked while we overwrite it.
    internal sealed class MemResolver : DefaultAssemblyResolver
    {
        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            return base.Resolve(name, new ReaderParameters { AssemblyResolver = this, InMemory = true });
        }
    }

    public static class ThaiPatcher
    {
        public const int PatchVersion = 3;
        private const string ModAsm = "SWYF.ThaiMod";
        private static readonly string[] Targets = { "Assembly-CSharp.dll", "ScriptsAssDef.dll", "Kolkata.MossTtsNano.dll", "com.whisper.unity.dll" };
        private static readonly string[] PayloadToStreaming = { "strings_th.tsv", "ui_th.tsv" };

        public static string FindDataDir(string gameDir)
        {
            foreach (var d in Directory.GetDirectories(gameDir, "*_Data"))
                if (File.Exists(Path.Combine(d, "Managed", "Assembly-CSharp.dll"))) return d;
            return null;
        }

        public static PatchResult Run(string gameDir, string payloadDir)
        {
            var r = new PatchResult();
            string data = FindDataDir(gameDir);
            if (data == null) { r.Failed = true; r.Log("game data folder not found in " + gameDir); return r; }
            string managed = Path.Combine(data, "Managed");
            string modSa = Path.Combine(data, "StreamingAssets", "SWYF.ThaiMod");
            Directory.CreateDirectory(modSa);

            // 1) payload -> game (never touches key files)
            bool payloadChanged = false;
            payloadChanged |= Sync(Path.Combine(payloadDir, ModAsm + ".dll"), Path.Combine(managed, ModAsm + ".dll"), r);
            foreach (var f in PayloadToStreaming) payloadChanged |= Sync(Path.Combine(payloadDir, f), Path.Combine(modSa, f), r);
            if (!File.Exists(Path.Combine(managed, ModAsm + ".dll"))) { r.Failed = true; r.Log("mod dll missing"); return r; }

            string payloadHash = Sha(Encoding.UTF8.GetBytes(PatchVersion + "|" + FileSha(Path.Combine(managed, ModAsm + ".dll")) + "|" + FileShaOrEmpty(Path.Combine(modSa, "ui_th.tsv"))));
            string statePath = Path.Combine(modSa, "patch_state.txt");
            var state = LoadState(statePath);

            var dict = LoadDict(Path.Combine(modSa, "ui_th.tsv"));
            var resolver = new MemResolver();
            resolver.AddSearchDirectory(managed);
            var mod = ModuleDefinition.ReadModule(Path.Combine(managed, ModAsm + ".dll"), new ReaderParameters { AssemblyResolver = resolver, InMemory = true });

            // literals the game also compares/uses as keys anywhere -> never translate them
            HashSet<string> unsafeLits = null;

            foreach (var name in Targets)
            {
                string file = Path.Combine(managed, name);
                if (!File.Exists(file)) { r.Log(name + ": not found, skipped"); continue; }
                string orig = file + ".orig";
                string cur = FileSha(file);
                state.TryGetValue(name, out var st);
                if (st != null && cur == st[1] && st[2] == payloadHash) { r.Log(name + ": up to date"); continue; }

                string source;
                if (st != null && cur == st[1] && File.Exists(orig) && FileSha(orig) == st[0]) source = orig;      // mod updated
                else if (ReferencesMod(file, resolver))
                {
                    if (File.Exists(orig) && !ReferencesMod(orig, resolver)) source = orig;
                    else { r.Log(name + ": already patched by an unknown version and no clean backup; use Verify integrity in Steam"); r.Failed = true; continue; }
                }
                else { File.Copy(file, orig, true); source = orig; r.Log(name + ": new game files detected, backed up"); }

                try
                {
                    var m = ModuleDefinition.ReadModule(source, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
                    var sites = new List<string>();
                    if (name == "Kolkata.MossTtsNano.dll") PatchMoss(m, mod, sites);
                    else if (name == "com.whisper.unity.dll") PatchWhisper(m, mod, sites);
                    else
                    {
                        if (unsafeLits == null)
                        {
                            unsafeLits = FindUnsafeLiterals(managed, resolver, dict);
                            r.Log("text: " + dict.Count + " entries, " + unsafeLits.Count + " left in English because the game also uses them as keys: " + string.Join(" | ", unsafeLits.OrderBy(x => x)));
                        }
                        if (name == "Assembly-CSharp.dll") PatchAssemblyCSharp(m, mod, sites, r);
                        else PatchScriptsAssDef(m, mod, sites, r);
                        WrapLiterals(m, mod, dict, unsafeLits, sites);
                    }
                    string tmp = file + ".swyf_tmp";
                    m.Write(tmp);
                    File.Copy(tmp, file, true);
                    File.Delete(tmp);
                    state[name] = new[] { FileSha(orig), FileSha(file), payloadHash };
                    r.Changed = true;
                    r.Log(name + ": patched (" + string.Join(", ", sites.GroupBy(s => s).Select(g => g.Key + (g.Count() > 1 ? " x" + g.Count() : ""))) + ")");
                }
                catch (Exception e)
                {
                    r.Failed = true;
                    r.Log(name + ": FAILED " + e.Message + " -> restoring original");
                    try { if (File.Exists(orig)) File.Copy(orig, file, true); state.Remove(name); } catch { }
                }
            }

            r.Changed |= EnsureJson(data, r);
            SaveState(statePath, state);
            resolver.Dispose();
            if (payloadChanged) r.Changed = true;
            return r;
        }

        // ---------------- individual patches ----------------

        private static MethodReference Hook(ModuleDefinition target, ModuleDefinition mod, string type, string method)
        {
            var t = mod.GetType("SWYF.ThaiMod." + type) ?? throw new Exception("hook type " + type + " missing");
            return target.ImportReference(t.Methods.Single(x => x.Name == method));
        }

        private static void PatchMoss(ModuleDefinition m, ModuleDefinition mod, List<string> sites)
        {
            var mHas = Hook(m, mod, "ThaiTts", "HasRuntimeOrThai");
            var mSyn = Hook(m, mod, "ThaiTts", "Synthesize");
            var single = m.GetTypes().Single(t => t.FullName == "Kolkata.MossTtsNano.MossTtsSingleton");
            foreach (var closure in single.NestedTypes)
                foreach (var meth in closure.Methods.Where(x => x.HasBody && x.Name.StartsWith("<Prompt>")))
                {
                    var body = meth.Body;
                    var synth = body.Instructions.FirstOrDefault(i => (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) && i.Operand is MethodReference mr && mr.Name == "Synthesize" && mr.DeclaringType.Name == "MossOnnxRuntime");
                    var check = body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldfld && ((FieldReference)i.Operand).Name == "runtime" && i.Next != null && (i.Next.OpCode.Code == Code.Brtrue_S || i.Next.OpCode.Code == Code.Brtrue));
                    var textField = closure.Fields.FirstOrDefault(f => f.Name == "text");
                    if (synth == null || check == null || textField == null) continue;
                    body.SimplifyMacros();
                    var il = body.GetILProcessor();
                    il.InsertAfter(check, il.Create(OpCodes.Call, mHas));
                    il.InsertAfter(check, il.Create(OpCodes.Ldfld, textField));
                    il.InsertAfter(check, il.Create(OpCodes.Ldarg_0));
                    synth.OpCode = OpCodes.Call; synth.Operand = mSyn;
                    body.OptimizeMacros();
                    sites.Add("thai voice");
                }
            if (!sites.Contains("thai voice")) throw new Exception("MOSS synth site not found");
        }

        private static void PatchWhisper(ModuleDefinition m, ModuleDefinition mod, List<string> sites)
        {
            var hook = Hook(m, mod, "ThaiCloudStt", "TryTranscribe");
            var mgr = m.GetType("Whisper.WhisperManager");
            var target = mgr.Methods.Single(x => x.Name == "GetTextAsync" && x.Parameters.Count == 3 && x.Parameters[0].ParameterType.FullName == "System.Single[]");
            ReturnIfNotNull(target, hook, OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3);
            sites.Add("groq speech");
        }

        // hook(args...) -> if non-null return it, else continue with the original body
        private static void ReturnIfNotNull(MethodDefinition target, MethodReference hook, params OpCode[] loads)
        {
            var body = target.Body;
            body.SimplifyMacros();
            var il = body.GetILProcessor();
            var first = body.Instructions[0];
            var pop = il.Create(OpCodes.Pop);
            foreach (var op in loads) il.InsertBefore(first, il.Create(op));
            il.InsertBefore(first, il.Create(OpCodes.Call, hook));
            il.InsertBefore(first, il.Create(OpCodes.Dup));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse, pop));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            il.InsertBefore(first, pop);
            body.MaxStackSize = Math.Max(body.MaxStackSize, loads.Length + 2);
            body.OptimizeMacros();
        }

        private static void PatchAssemblyCSharp(ModuleDefinition m, ModuleDefinition mod, List<string> sites, PatchResult r)
        {
            TypeDefinition T(string full) => m.GetTypes().FirstOrDefault(t => t.FullName == full);
            void Try(string what, Action a) { try { a(); } catch (Exception e) { r.Log("  skipped " + what + ": " + e.Message); } }

            Try("boss review language", () =>
            {
                var t = T("Kolkata.UI.ReviewEvidencePacket") ?? throw new Exception("type missing");
                var h = Hook(m, mod, "ThaiReview", "DecorateUserMessage");
                int n = 0;
                foreach (var ctor in t.Methods.Where(x => x.IsConstructor && !x.IsStatic && x.HasBody))
                {
                    var targets = ctor.Body.Instructions.Where(i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "UserMessage").ToList();
                    if (targets.Count == 0) continue;
                    ctor.Body.SimplifyMacros();
                    var il = ctor.Body.GetILProcessor();
                    foreach (var ins in targets) { il.InsertBefore(ins, il.Create(OpCodes.Call, h)); n++; }
                    ctor.Body.OptimizeMacros();
                }
                if (n == 0) throw new Exception("UserMessage not found");
                sites.Add("boss review language");
            });
            void Prologue(string type, string method, bool ret)
            {
                Try(method, () =>
                {
                    var meth = (T(type) ?? throw new Exception("type missing")).Methods.Single(x => x.Name == method);
                    var h = Hook(m, mod, "ThaiReview", "AcceptThaiText");
                    var body = meth.Body; body.SimplifyMacros();
                    var il = body.GetILProcessor(); var first = body.Instructions[0];
                    il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(first, il.Create(OpCodes.Call, h));
                    il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
                    il.InsertBefore(first, il.Create(ret ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                    il.InsertBefore(first, il.Create(OpCodes.Ret));
                    body.OptimizeMacros();
                    sites.Add("boss review checks");
                });
            }
            Prologue("Kolkata.UI.ReviewBossAiClient", "IsWeakNoteResponse", false);
            Prologue("Kolkata.UI.ReviewBossAiClient", "RankingNarrationUsesCorrectTier", true);
            Try("boss narration voice", () =>
            {
                var meth = (T("Kolkata.UI.ReviewWhiteboardNarrator") ?? throw new Exception("type missing")).Methods.Single(x => x.Name == "Speak" && x.Parameters.Count == 1);
                var body = meth.Body; body.SimplifyMacros();
                var trim = body.Instructions.First(i => i.Operand is MethodReference mr && mr.Name == "Trim");
                var store = trim.Next;
                if (store.OpCode != OpCodes.Stloc) store = store.Next;
                if (store.OpCode != OpCodes.Stloc) throw new Exception("text local not found");
                var v = (VariableDefinition)store.Operand;
                var il = body.GetILProcessor();
                il.InsertAfter(trim, il.Create(OpCodes.Call, Hook(m, mod, "ThaiReview", "CannedLine")));
                var lang = Hook(m, mod, "ThaiReview", "NarrationLanguageFor");
                foreach (var ins in body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "en").ToList())
                { ins.OpCode = OpCodes.Ldloc; ins.Operand = v; il.InsertAfter(ins, il.Create(OpCodes.Call, lang)); }
                body.OptimizeMacros();
                sites.Add("boss narration voice");
            });
            Try("PA announcements", () =>
            {
                var meth = (T("OfficePromotionAnnouncements") ?? throw new Exception("type missing")).Methods.Single(x => x.Name == "DialogueForDay");
                var body = meth.Body; body.SimplifyMacros();
                var il = body.GetILProcessor(); var h = Hook(m, mod, "ThaiReview", "CannedLine");
                foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
                {
                    var call = il.Create(OpCodes.Call, h);
                    il.InsertBefore(ret, call);
                    Retarget(body, ret, call);
                }
                body.OptimizeMacros();
                sites.Add("PA announcements");
            });
            Try("caller fillers", () =>
            {
                var meth = (T("CallerResponseFillers") ?? throw new Exception("type missing")).Methods.Single(x => x.Name == "Get" && x.Parameters.Count == 2);
                ReturnIfNotNull(meth, Hook(m, mod, "ThaiText", "Filler"), OpCodes.Ldarg_0, OpCodes.Ldarg_1);
                sites.Add("thai fillers");
            });
        }

        private static void PatchScriptsAssDef(ModuleDefinition m, ModuleDefinition mod, List<string> sites, PatchResult r)
        {
            try
            {
                var lm = m.GetTypes().First(t => t.FullName == "LanguageManager");
                var meth = lm.Methods.Single(x => x.Name == "GetCachedLocalizedText" && x.Parameters.Count == 3);
                var h = Hook(m, mod, "ThaiText", "Missing");
                var body = meth.Body; body.SimplifyMacros();
                var il = body.GetILProcessor();
                foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
                {
                    var ld = il.Create(OpCodes.Ldarg_1);
                    il.InsertBefore(ret, ld);
                    il.InsertBefore(ret, il.Create(OpCodes.Call, h));
                    Retarget(body, ret, ld);
                }
                body.MaxStackSize = Math.Max(body.MaxStackSize, 3);
                body.OptimizeMacros();
                sites.Add("menu fallback");
            }
            catch (Exception e) { r.Log("  skipped menu fallback: " + e.Message); }
        }

        // ---------------- hard-coded text ----------------

        private static readonly HashSet<string> DenyMethods = new HashSet<string>(StringComparer.Ordinal) {
            "op_Equality","op_Inequality","Equals","Compare","CompareOrdinal","CompareTo","StartsWith","EndsWith","Contains","IndexOf","LastIndexOf",
            "Replace","Split","TryGetValue","ContainsKey","Remove","get_Item","set_Item","Find","FindChild","FindChildRecursive","FindChildGameObjectByName",
            "FindTextByObjectName","Q","Query","GetComponent","GetComponentInChildren","Load","LoadAll","GetString","SetString","HasKey","GetFloat","SetFloat",
            "GetInt","SetInt","SetBool","SetTrigger","SetInteger","Play","StringToHash","PropertyToID","AddToClassList","RemoveFromClassList","EnableInClassList",
            "ClassListContains","set_name","CompareTag","ComputeStringHash","Parse","TryParse","IsMatch","Match","Matches","GetTexture","GetExpressionTexture",
            "Combine","GetFileName","GetFileNameWithoutExtension","SendCommandInternal","SendRPCInternal","SendTargetRPCInternal","Log","LogWarning","LogError","LogFormat",
            "ToLowerInvariant","Instantiate","Create" };
        private static readonly string[] DenyTypes = { "Debug", "PlayerPrefs", "Animator", "Shader", "Resources", "Path", "File", "Directory", "Regex", "GameObject", "Exception" };

        private static bool UnsafeUse(Instruction ldstr)
        {
            var i = ldstr.Next;
            for (int n = 0; i != null && n < 10; n++, i = i.Next)
            {
                if (i.OpCode.FlowControl == FlowControl.Branch || i.OpCode.FlowControl == FlowControl.Cond_Branch || i.OpCode.FlowControl == FlowControl.Return) return false;
                if (i.OpCode == OpCodes.Stsfld || i.OpCode == OpCodes.Stfld) return false;
                if (i.Operand is MethodReference mr)
                {
                    if (mr.Name == "Concat" || mr.Name == "Format" || mr.Name == "Trim") continue;
                    if (DenyMethods.Contains(mr.Name)) return true;
                    string dt = mr.DeclaringType.Name;
                    foreach (var d in DenyTypes) if (dt.Contains(d)) return true;
                    return false;
                }
            }
            return false;
        }

        private static HashSet<string> FindUnsafeLiterals(string managed, IAssemblyResolver res, Dictionary<string, string> dict)
        {
            var bad = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in new[] { "Assembly-CSharp.dll", "ScriptsAssDef.dll" })
            {
                string f = Path.Combine(managed, name);
                string src = File.Exists(f + ".orig") && ReferencesMod(f, res) ? f + ".orig" : f;
                var m = ModuleDefinition.ReadModule(src, new ReaderParameters { AssemblyResolver = res, InMemory = true });
                foreach (var t in m.GetTypes()) foreach (var meth in t.Methods.Where(x => x.HasBody))
                    foreach (var ins in meth.Body.Instructions)
                        if (ins.OpCode == OpCodes.Ldstr)
                        {
                            string s = ((string)ins.Operand).Trim();
                            if (dict.ContainsKey(s) && UnsafeUse(ins)) bad.Add(s);
                        }
            }
            return bad;
        }

        private static bool SkipType(TypeDefinition t)
        {
            string n = t.FullName;
            return n.Contains("Test") || n.Contains("DevMenu") || n.Contains("Debug") || n.StartsWith("Mirror.") || n.Contains("Demo");
        }

        private static void WrapLiterals(ModuleDefinition m, ModuleDefinition mod, Dictionary<string, string> dict, HashSet<string> unsafeLits, List<string> sites)
        {
            if (dict.Count == 0) return;
            var h = Hook(m, mod, "ThaiText", "T");
            foreach (var t in m.GetTypes())
            {
                if (SkipType(t)) continue;
                foreach (var meth in t.Methods.Where(x => x.HasBody))
                {
                    var hits = meth.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr)
                        .Where(i => { string s = ((string)i.Operand).Trim(); return s.Length > 0 && dict.ContainsKey(s) && !unsafeLits.Contains(s); }).ToList();
                    if (hits.Count == 0) continue;
                    meth.Body.SimplifyMacros();
                    var il = meth.Body.GetILProcessor();
                    foreach (var ins in hits) { il.InsertAfter(ins, il.Create(OpCodes.Call, h)); sites.Add("text"); }
                    meth.Body.OptimizeMacros();
                }
            }
        }

        // ---------------- helpers ----------------

        // Code that jumped to `from` must now run `to` first (branches, switch tables, try/handler ends).
        private static void Retarget(MethodBody body, Instruction from, Instruction to)
        {
            foreach (var j in body.Instructions)
            {
                if (j == to) continue;
                if (j.Operand == from) j.Operand = to;
                else if (j.Operand is Instruction[] arr) for (int k = 0; k < arr.Length; k++) if (arr[k] == from) arr[k] = to;
            }
            foreach (var eh in body.ExceptionHandlers)
            {
                if (eh.TryEnd == from) eh.TryEnd = to;
                if (eh.HandlerEnd == from) eh.HandlerEnd = to;
            }
        }

        private static bool ReferencesMod(string file, IAssemblyResolver res)
        {
            using (var m = ModuleDefinition.ReadModule(file, new ReaderParameters { AssemblyResolver = res, InMemory = true }))
                return m.AssemblyReferences.Any(a => a.Name == ModAsm);
        }

        private static bool EnsureJson(string data, PatchResult r)
        {
            bool changed = false;
            string rio = Path.Combine(data, "RuntimeInitializeOnLoads.json");
            string s = File.ReadAllText(rio);
            if (!s.Contains("\"assemblyName\":\"" + ModAsm + "\""))
            {
                int end = s.LastIndexOf(']');
                if (end < 0) throw new Exception("RuntimeInitializeOnLoads.json format");
                string entry = "{\"assemblyName\":\"" + ModAsm + "\",\"nameSpace\":\"" + ModAsm + "\",\"className\":\"ThaiModEntry\",\"methodName\":\"Init\",\"loadTypes\":1,\"isUnityClass\":false}";
                bool empty = s.Substring(0, end).TrimEnd().EndsWith("[");
                s = s.Substring(0, end) + (empty ? "" : ",") + entry + s.Substring(end);
                File.WriteAllText(rio, s, new UTF8Encoding(false));
                r.Log("RuntimeInitializeOnLoads.json: mod entry added"); changed = true;
            }
            string sa = Path.Combine(data, "ScriptingAssemblies.json");
            s = File.ReadAllText(sa);
            if (!s.Contains("\"" + ModAsm + ".dll\""))
            {
                s = AppendToArray(s, "\"names\"", "\"" + ModAsm + ".dll\"");
                s = AppendToArray(s, "\"types\"", "16");
                File.WriteAllText(sa, s, new UTF8Encoding(false));
                r.Log("ScriptingAssemblies.json: mod added"); changed = true;
            }
            return changed;
        }

        private static string AppendToArray(string json, string key, string value)
        {
            int k = json.IndexOf(key, StringComparison.Ordinal);
            int open = json.IndexOf('[', k);
            int close = json.IndexOf(']', open);
            if (k < 0 || open < 0 || close < 0) throw new Exception("ScriptingAssemblies.json format");
            bool empty = json.Substring(open + 1, close - open - 1).Trim().Length == 0;
            return json.Substring(0, close) + (empty ? "" : ",") + value + json.Substring(close);
        }

        private static bool Sync(string src, string dst, PatchResult r)
        {
            if (!File.Exists(src)) return false;
            if (File.Exists(dst) && FileSha(src) == FileSha(dst)) return false;
            File.Copy(src, dst, true);
            r.Log("installed " + Path.GetFileName(dst));
            return true;
        }

        private static Dictionary<string, string> LoadDict(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return d;
            foreach (var raw in File.ReadAllText(path, Encoding.UTF8).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                d[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1));
            }
            return d;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char e = s[++i];
                sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e);
            }
            return sb.ToString();
        }

        private static Dictionary<string, string[]> LoadState(string path)
        {
            var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return d;
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Split('\t');
                if (p.Length == 4) d[p[0]] = new[] { p[1], p[2], p[3] };
            }
            return d;
        }

        private static void SaveState(string path, Dictionary<string, string[]> state)
        {
            File.WriteAllLines(path, state.Select(kv => kv.Key + "\t" + string.Join("\t", kv.Value)).ToArray());
        }

        private static string FileShaOrEmpty(string f) => File.Exists(f) ? FileSha(f) : "";
        public static string FileSha(string f) { using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return Hex(h.ComputeHash(s)); }
        private static string Sha(byte[] b) { using (var h = SHA256.Create()) return Hex(h.ComputeHash(b)); }
        private static string Hex(byte[] b) { var sb = new StringBuilder(); foreach (var x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }
    }
}
