using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CupheadLanCoop
{
    // Cosmetic code (film grain, ...) that consumes UnityEngine.Random at times that differ between
    // PCs runs on a separate Random stream, so the gameplay sequence stays identical.
    internal static class RngIsolation
    {
        // Found with Diagnostica.TracciaRandom; each entry: type name (methods of nested iterators included).
        static readonly string[] CosmeticTypes = { "ChromaticAberrationFilmGrain", "AudioManagerComponent", "AudioManager" };
        static UnityEngine.Random.State cosmetic;
        static bool initialized;

        public static void Install(Harmony h, List<string> missing)
        {
            HarmonyMethod pre = new HarmonyMethod(typeof(RngIsolation).GetMethod("Pre", BindingFlags.Static | BindingFlags.NonPublic));
            HarmonyMethod post = new HarmonyMethod(typeof(RngIsolation).GetMethod("Post", BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string name in CosmeticTypes)
            {
                Type t = typeof(PlayerData).Assembly.GetType(name, false);
                if (t == null) { missing.Add("isolamento Random " + name); continue; }
                List<Type> types = new List<Type>();
                types.Add(t);
                types.AddRange(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
                foreach (Type type in types)
                    foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null || !UsesRandom(m)) continue;
                        try { h.Patch(m, pre, post, null, null, null); }
                        catch (Exception ex) { missing.Add("isolamento " + type.Name + "." + m.Name + ": " + ex.Message); }
                    }
            }
        }

        static bool UsesRandom(MethodInfo m)
        {
            byte[] il = m.GetMethodBody().GetILAsByteArray();
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                try
                {
                    MethodBase target = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                    if (target != null && (target.DeclaringType == typeof(UnityEngine.Random) || target.DeclaringType == typeof(MathUtils))) return true;
                }
                catch { }
            }
            return false;
        }

        static void Pre(out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            if (!initialized) { initialized = true; UnityEngine.Random.InitState(0x5EED); cosmetic = UnityEngine.Random.state; }
            UnityEngine.Random.state = cosmetic;
        }

        static void Post(UnityEngine.Random.State __state)
        {
            cosmetic = UnityEngine.Random.state;
            UnityEngine.Random.state = __state;
        }
    }
}
