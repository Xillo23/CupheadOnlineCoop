using System;
using System.IO;
using System.Net;
using System.Threading;
using CupheadLanCoop;

// Desktop-CLR tests for the Unity-free parts of CupheadLanCoop (codec, edges, framing, delay).
static class CoopTests
{
    static int failures, checks;

    static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) { failures++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        // Codec round trip.
        Pad p = Pad.Neutral(2);
        p.Pos = 0x8000000000000005UL; p.Neg = 2; p.Axes[0] = 127; p.Axes[1] = -127; p.Stage = 2; p.Flags = 1;
        int e, f;
        Pad q = PadCodec.Decode(PadCodec.Encode(7, 123456, p), out e, out f);
        Check(e == 7 && f == 123456 && q.Same(p), "round trip pad");
        Check(PadCodec.Encode(1, 2, Pad.Neutral(2)).Length < 40, "packet small");

        // Quantization is symmetric and bounded.
        Check(PadCodec.Quantize(1f) == 127 && PadCodec.Quantize(-1f) == -127 && PadCodec.Quantize(5f) == 127, "quantize clamp");
        Check(PadCodec.Quantize(float.NaN) == 0 && PadCodec.Quantize(0.001f) == 0, "quantize nan/deadzone");
        Check(Math.Abs(PadCodec.Dequantize(PadCodec.Quantize(0.5f)) - 0.5f) < 0.01f, "dequantize");

        // Edges (Rewired semantics).
        Pad prev = Pad.Neutral(0), cur = Pad.Neutral(0);
        cur.Pos = 1UL << 3;
        Check(PadCodec.Button(cur, prev, 3, 0) && PadCodec.Button(cur, prev, 3, 1) && !PadCodec.Button(cur, prev, 3, 2), "down edge");
        Check(!PadCodec.Button(cur, cur, 3, 1) && PadCodec.Button(cur, cur, 3, 0), "held no edge");
        Check(PadCodec.Button(prev, cur, 3, 2) && !PadCodec.Button(prev, cur, 3, 0), "up edge");
        cur.Neg = 1UL << 63;
        Check(PadCodec.Button(cur, prev, 63, 4) && !PadCodec.Button(cur, prev, 63, 1), "negative edge bit 63");
        Check(!PadCodec.Button(cur, prev, 64, 0) && !PadCodec.Button(cur, prev, -1, 0), "index bounds");
        Check(PadCodec.AnyDown(cur, prev) && !PadCodec.AnyDown(cur, cur) && PadCodec.AnyHeld(cur), "any");

        // Delay from RTT.
        Check(PadCodec.DelayFromRtt(1) == 3 && PadCodec.DelayFromRtt(0) == 2, "delay lan");
        Check(PadCodec.DelayFromRtt(40) == 4 && PadCodec.DelayFromRtt(1000) == 8 && PadCodec.DelayFromRtt(double.NaN) == 2, "delay radmin/clamp");

        // FNV is stable.
        Check(Fnv.Add(Fnv.Seed, "abc") == Fnv.Add(Fnv.Seed, "abc") && Fnv.Add(Fnv.Seed, "abc") != Fnv.Add(Fnv.Seed, "abd"), "fnv");

        // TCP framing over loopback: order, large payload, close detection.
        string error;
        NetHost host = NetHost.Listen(28199, out error);
        Check(host != null, "listen " + error);
        if (host != null)
        {
            NetLink client = NetLink.Connect("127.0.0.1", 28199, 3000, out error);
            Check(client != null, "connect " + error);
            NetLink server = null;
            for (int i = 0; i < 200 && server == null; i++) { server = host.TryAccept(); Thread.Sleep(5); }
            Check(server != null, "accept");
            if (client != null && server != null)
            {
                for (int i = 0; i < 1000; i++) client.Send(Msg.Input, PadCodec.Encode(1, i, p));
                byte[] big = new byte[100000];
                big[99999] = 42;
                client.Send(Msg.DataFix, big);
                int next = 0; bool bigOk = false;
                DateTime until = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < until && !bigOk)
                {
                    NetMessage m;
                    while (server.TryReceive(out m))
                    {
                        if (m.Type == Msg.Input) { PadCodec.Decode(m.Body, out e, out f); if (f == next) next++; }
                        else if (m.Type == Msg.DataFix) bigOk = m.Body.Length == 100000 && m.Body[99999] == 42;
                    }
                    Thread.Sleep(1);
                }
                Check(next == 1000, "ordered inputs " + next);
                Check(bigOk, "large frame");
                client.Close("test");
                until = DateTime.UtcNow.AddSeconds(3);
                NetMessage dummy;
                while (DateTime.UtcNow < until && server.Connected) { server.TryReceive(out dummy); Thread.Sleep(5); }
                Check(!server.Connected, "close detected");
            }
            host.Stop();
        }

        Console.WriteLine((failures == 0 ? "PASS " : "FAIL ") + (checks - failures) + "/" + checks + " controlli coop");
        return failures == 0 ? 0 : 1;
    }
}
