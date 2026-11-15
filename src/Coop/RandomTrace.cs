using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace CupheadLanCoop
{
    // Diagnostics only (config Diagnostica.TracciaRandom): every UnityEngine.Random call in
    // Assembly-CSharp goes through a wrapper that records its call site per lockstep frame.
    internal static class RandomTrace
    {
        public static bool Enabled;
        static List<string> current = new List<string>();
        static readonly Dictionary<long, List<string>> history = new Dictionary<long, List<string>>();
        static readonly Queue<long> order = new Queue<long>();
        static Dictionary<MethodInfo, MethodInfo> wrappers;

        public static void Install(Harmony h, List<string> missing)
        {
            Type r = typeof(UnityEngine.Random), self = typeof(RandomTrace);
            BindingFlags st = BindingFlags.Static | BindingFlags.Public;
            wrappers = new Dictionary<MethodInfo, MethodInfo>();
            wrappers[r.GetMethod("Range", st, null, new Type[] { typeof(int), typeof(int) }, null)] = self.GetMethod("RangeI");
            wrappers[r.GetMethod("Range", st, null, new Type[] { typeof(float), typeof(float) }, null)] = self.GetMethod("RangeF");
            wrappers[r.GetProperty("value").GetGetMethod()] = self.GetMethod("Value");
            wrappers[r.GetProperty("insideUnitCircle").GetGetMethod()] = self.GetMethod("Circle");
            wrappers[r.GetProperty("insideUnitSphere").GetGetMethod()] = self.GetMethod("Sphere");
            HarmonyMethod transpiler = new HarmonyMethod(self.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic));
            int patched = 0;
            DateTime t0 = DateTime.UtcNow;
            foreach (Type t in typeof(PlayerData).Assembly.GetTypes())
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || t.ContainsGenericParameters || !CallsRandom(m)) continue;
                    try { h.Patch(m, null, null, transpiler, null, null); patched++; }
                    catch (Exception ex) { missing.Add("trace " + t.Name + "." + m.Name + ": " + ex.Message); }
                }
            }
            CoopPlugin.Log("TracciaRandom: " + patched + " metodi strumentati in " + (int)(DateTime.UtcNow - t0).TotalMilliseconds + " ms");
        }

        static bool CallsRandom(MethodInfo m)
        {
            MethodBody body;
            try { body = m.GetMethodBody(); } catch { return false; }
            if (body == null) return false;
            byte[] il = body.GetILAsByteArray();
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                try
                {
                    MethodBase target = m.Module.ResolveMethod(token);
                    if (target != null && target.DeclaringType == typeof(UnityEngine.Random)) return true;
                }
                catch { }
            }
            return false;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int n = 0;
            foreach (CodeInstruction i in instructions)
            {
                MethodInfo called = i.operand as MethodInfo;
                MethodInfo wrapper;
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && called != null && wrappers.TryGetValue(called, out wrapper))
                {
                    yield return new CodeInstruction(OpCodes.Ldstr, original.DeclaringType.Name + "." + original.Name + "#" + (n++));
                    i.opcode = OpCodes.Call;
                    i.operand = wrapper;
                }
                yield return i;
            }
        }

        static void Note(string site)
        {
            if (Lockstep.Running) current.Add(site);
        }

        public static int RangeI(int a, int b, string site) { Note(site); return UnityEngine.Random.Range(a, b); }
        public static float RangeF(float a, float b, string site) { Note(site); return UnityEngine.Random.Range(a, b); }
        public static float Value(string site) { Note(site); return UnityEngine.Random.value; }
        public static Vector2 Circle(string site) { Note(site); return UnityEngine.Random.insideUnitCircle; }
        public static Vector3 Sphere(string site) { Note(site); return UnityEngine.Random.insideUnitSphere; }

        public static void EndFrame(long key)
        {
            if (!Enabled) return;
            history[key] = current;
            order.Enqueue(key);
            current = new List<string>();
            while (order.Count > 180) history.Remove(order.Dequeue());
        }

        public static string Dump(long key)
        {
            List<string> calls;
            if (!history.TryGetValue(key, out calls)) return "(nessuna traccia)";
            StringBuilder s = new StringBuilder();
            s.Append(calls.Count).Append(" chiamate:");
            foreach (string c in calls) s.Append(' ').Append(c);
            return s.ToString();
        }
    }
}
