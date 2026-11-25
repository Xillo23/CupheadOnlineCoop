using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadLanCoop
{
    // Every 60 lockstep frames both PCs hash RNG state, scene, fixed-step count and both players.
    internal static class DesyncCheck
    {
        public static int Checked, Mismatches;
        static long fixedSteps;
        static readonly Dictionary<long, string> mine = new Dictionary<long, string>(), theirs = new Dictionary<long, string>();
        static readonly HardDesyncTracker hardTracker = new HardDesyncTracker();
        static FieldInfo[] rngFields;
        static long ignoreUpTo = -1;

        // Hashes up to a soft correction describe the pre-correction state: drop them.
        public static void IgnoreUpTo(int epoch, int frame)
        {
            ignoreUpTo = Key(epoch, frame);
            List<long> old = new List<long>();
            foreach (long k in mine.Keys) if (k <= ignoreUpTo) old.Add(k);
            foreach (long k in theirs.Keys) if (k <= ignoreUpTo) old.Add(k);
            foreach (long k in old) { mine.Remove(k); theirs.Remove(k); }
        }

        public static void Reset()
        {
            fixedSteps = 0;
            ignoreUpTo = -1;
            hardTracker.Reset();
            SoftResync.Reset();
            mine.Clear(); theirs.Clear();
        }

        static long Key(int epoch, int frame) { return ((long)epoch << 32) | (uint)frame; }

        public static void OnFrame(int steps)
        {
            fixedSteps += steps;
            long k = Key(Lockstep.Epoch, Lockstep.Frame);
            RandomTrace.EndFrame(k);
            if (Lockstep.Frame % (RandomTrace.Enabled ? 1 : 60) != 0) return;
            string detail = Describe();
            if (Lockstep.Frame % 600 == 0) CoopPlugin.Log("Stato " + detail + " controlli=" + Checked + " desync=" + Mismatches);
            mine[k] = detail;
            Session.SendHash(Lockstep.Epoch, Lockstep.Frame, detail);
            Compare(k);
        }

        public static void OnRemote(int epoch, int frame, string detail)
        {
            long k = Key(epoch, frame);
            if (k <= ignoreUpTo) return;
            theirs[k] = detail;
            Compare(k);
        }

        static void Compare(long k)
        {
            string a, b;
            if (!mine.TryGetValue(k, out a) || !theirs.TryGetValue(k, out b)) return;
            mine.Remove(k); theirs.Remove(k);
            Checked++;
            if (a == b) { hardTracker.OnMatch(); return; }
            Mismatches++;
            bool fatalHp;
            DesyncKind kind = DesyncClassifier.Classify(a, b, out fatalHp);
            CoopPlugin.Log("DESYNC (" + kind + ") epoch=" + (k >> 32) + " frame=" + (int)k + "\n  locale: " + a + "\n  remoto: " + b);
            if (RandomTrace.Enabled)
                CoopPlugin.Log("TRACCIA frame " + (int)k + ": " + RandomTrace.Dump(k) + "\n  frame precedente: " + RandomTrace.Dump(k - 1));
            if (hardTracker.OnMismatch(kind, fatalHp)) Session.OnHardDesync();
            else Session.OnDesync();
        }

        static string Describe()
        {
            StringBuilder s = new StringBuilder();
            s.Append("f=").Append(Lockstep.Frame).Append(" fx=").Append(fixedSteps);
            s.Append(" sc=").Append(SceneManager.GetActiveScene().name);
            s.Append(" rng=").Append(Rng());
            for (int i = 0; i < 2; i++)
            {
                AbstractPlayerController p = PlayerManager.GetPlayer(i == 0 ? PlayerId.PlayerOne : PlayerId.PlayerTwo);
                s.Append(" p").Append(i + 1).Append('=');
                if (p == null) { s.Append('-'); continue; }
                Vector3 v = p.transform.position;
                s.Append(Bits(v.x)).Append(',').Append(Bits(v.y));
                if (p.stats != null) s.Append(",hp").Append(p.stats.Health).Append(",sm").Append(Bits(p.stats.SuperMeter));
            }
            return s.ToString();
        }

        static string Bits(float f) { return BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8"); }

        static string Rng()
        {
            object state = UnityEngine.Random.state;
            if (rngFields == null) rngFields = state.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            uint h = Fnv.Seed;
            foreach (FieldInfo f in rngFields)
            {
                object v = f.GetValue(state);
                if (v is int) h = Fnv.Add(h, (int)v);
            }
            return h.ToString("x8");
        }
    }
}
