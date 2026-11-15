using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CupheadLanCoop
{
    // Soft correction after a desync, instead of reloading the level: in the lockstep frame of the
    // resync flag the host sends its RNG state and the players' super meters, the guest copies them.
    // Real two-PC logs showed only these two values diverging (positions and HP identical).
    internal static class SoftResync
    {
        public static bool Pending;
        static byte[] snap;
        static long snapKey = -1;
        static FieldInfo[] rngFields;
        static MethodInfo setSuper, onSuperChanged;

        static long Key(int epoch, int frame) { return ((long)epoch << 32) | (uint)frame; }

        public static void Reset() { Pending = false; snap = null; snapKey = -1; }

        static FieldInfo[] RngFields()
        {
            if (rngFields == null)
            {
                rngFields = UnityEngine.Random.state.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Array.Sort(rngFields, delegate(FieldInfo a, FieldInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            }
            return rngFields;
        }

        static PlayerStatsManager Stats(int i)
        {
            AbstractPlayerController p = PlayerManager.GetPlayer(i == 0 ? PlayerId.PlayerOne : PlayerId.PlayerTwo);
            return p == null ? null : p.stats;
        }

        public static byte[] Capture(int epoch, int frame)
        {
            return Msg.Build(delegate(BinaryWriter w)
            {
                w.Write(epoch); w.Write(frame);
                object state = UnityEngine.Random.state;
                FieldInfo[] fs = RngFields();
                w.Write(fs.Length);
                foreach (FieldInfo f in fs) { object v = f.GetValue(state); w.Write(v is int ? (int)v : 0); }
                for (int i = 0; i < 2; i++)
                {
                    PlayerStatsManager s = Stats(i);
                    w.Write(s != null);
                    w.Write(s != null ? s.SuperMeter : 0f);
                }
            });
        }

        public static void OnSnap(byte[] body)
        {
            BinaryReader r = Msg.Read(body);
            int e = r.ReadInt32(), f = r.ReadInt32();
            snap = body; snapKey = Key(e, f);
        }

        public static bool Has(int epoch, int frame) { return snap != null && snapKey == Key(epoch, frame); }

        public static void Apply()
        {
            try
            {
                BinaryReader r = Msg.Read(snap);
                r.ReadInt32(); r.ReadInt32();
                FieldInfo[] fs = RngFields();
                int n = r.ReadInt32();
                object state = UnityEngine.Random.state;
                for (int i = 0; i < n; i++)
                {
                    int v = r.ReadInt32();
                    if (i < fs.Length && fs[i].FieldType == typeof(int)) fs[i].SetValue(state, v);
                }
                UnityEngine.Random.state = (UnityEngine.Random.State)state;
                if (setSuper == null)
                {
                    BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    PropertyInfo pi = typeof(PlayerStatsManager).GetProperty("SuperMeter", bf);
                    setSuper = pi == null ? null : pi.GetSetMethod(true);
                    onSuperChanged = typeof(PlayerStatsManager).GetMethod("OnSuperChanged", bf, null, new Type[] { typeof(bool) }, null);
                }
                for (int i = 0; i < 2; i++)
                {
                    bool has = r.ReadBoolean();
                    float sm = r.ReadSingle();
                    PlayerStatsManager s = Stats(i);
                    if (!has || s == null || setSuper == null || s.SuperMeter == sm) continue;
                    setSuper.Invoke(s, new object[] { sm });
                    if (onSuperChanged != null) onSuperChanged.Invoke(s, new object[] { false });
                }
            }
            catch (Exception ex) { CoopPlugin.Log("Correzione sincronizzazione fallita: " + ex.Message); }
            snap = null;
        }
    }
}
