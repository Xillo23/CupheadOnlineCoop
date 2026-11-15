using System;
using System.IO;

namespace CupheadLanCoop
{
    // Input of ONE player for ONE lockstep frame. Immutable once built.
    // Bit i of Pos/Neg = action at index i of the session action table.
    public sealed class Pad
    {
        public ulong Pos, Neg;
        public sbyte[] Axes;
        public byte Stage, Flags;

        public static Pad Neutral(int axisCount)
        {
            Pad p = new Pad();
            p.Axes = new sbyte[axisCount];
            return p;
        }

        public bool Same(Pad o)
        {
            if (o == null || Pos != o.Pos || Neg != o.Neg || Stage != o.Stage || Flags != o.Flags || Axes.Length != o.Axes.Length) return false;
            for (int i = 0; i < Axes.Length; i++) if (Axes[i] != o.Axes[i]) return false;
            return true;
        }
    }

    public static class PadCodec
    {
        public static byte[] Encode(int epoch, int frame, Pad p)
        {
            using (MemoryStream m = new MemoryStream(32))
            {
                BinaryWriter w = new BinaryWriter(m);
                w.Write(epoch); w.Write(frame); w.Write(p.Pos); w.Write(p.Neg);
                w.Write(p.Stage); w.Write(p.Flags); w.Write((byte)p.Axes.Length);
                for (int i = 0; i < p.Axes.Length; i++) w.Write(p.Axes[i]);
                w.Flush();
                return m.ToArray();
            }
        }

        public static Pad Decode(byte[] body, out int epoch, out int frame)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(body));
            epoch = r.ReadInt32(); frame = r.ReadInt32();
            Pad p = new Pad();
            p.Pos = r.ReadUInt64(); p.Neg = r.ReadUInt64();
            p.Stage = r.ReadByte(); p.Flags = r.ReadByte();
            int n = r.ReadByte();
            p.Axes = new sbyte[n];
            for (int i = 0; i < n; i++) p.Axes[i] = r.ReadSByte();
            return p;
        }

        // The local PC must use the quantized value too, never the raw one.
        public static sbyte Quantize(float v)
        {
            if (float.IsNaN(v)) return 0;
            if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
            return (sbyte)Math.Round(v * 127f);
        }

        public static float Dequantize(sbyte q) { return q / 127f; }

        // Rewired-like edges. kind: 0 held, 1 down, 2 up, 3 neg held, 4 neg down, 5 neg up.
        public static bool Button(Pad cur, Pad prev, int index, int kind)
        {
            if (index < 0 || index > 63) return false;
            ulong bit = 1UL << index;
            bool c = ((kind < 3 ? cur.Pos : cur.Neg) & bit) != 0;
            bool p = ((kind < 3 ? prev.Pos : prev.Neg) & bit) != 0;
            switch (kind % 3)
            {
                case 0: return c;
                case 1: return c && !p;
                default: return !c && p;
            }
        }

        public static bool AnyDown(Pad cur, Pad prev)
        {
            return ((cur.Pos | cur.Neg) & ~(prev.Pos | prev.Neg)) != 0;
        }

        public static bool AnyHeld(Pad cur) { return (cur.Pos | cur.Neg) != 0; }

        // D = clamp(ceil(RTT/2/16.7)+2, 2, 8)
        public static int DelayFromRtt(double rttMs)
        {
            if (double.IsNaN(rttMs) || rttMs < 0) rttMs = 0;
            int d = (int)Math.Ceiling(rttMs / 2.0 / 16.7) + 2;
            return d < 2 ? 2 : (d > 8 ? 8 : d);
        }
    }

    public static class Fnv
    {
        public const uint Seed = 2166136261;
        public static uint Add(uint h, int v)
        {
            unchecked
            {
                for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 16777619; }
                return h;
            }
        }
        public static uint Add(uint h, float v) { return Add(h, BitConverter.ToInt32(BitConverter.GetBytes(v), 0)); }
        public static uint Add(uint h, string s)
        {
            unchecked
            {
                if (s == null) return Add(h, -1);
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
                return Add(h, s.Length);
            }
        }
    }
}
