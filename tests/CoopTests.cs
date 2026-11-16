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

        // Addresses shown to the player.
        NetKind k;
        Check(NetAddresses.Classify("Intel Ethernet | Ethernet", new byte[] { 192, 168, 1, 20 }, true, out k) && k == NetKind.Lan, "lan");
        Check(NetAddresses.Classify("Famatech Radmin VPN Ethernet Adapter", new byte[] { 26, 1, 2, 3 }, false, out k) && k == NetKind.Radmin, "radmin");
        Check(NetAddresses.Classify("Tailscale Tunnel | Tailscale", new byte[] { 100, 101, 2, 3 }, false, out k) && k == NetKind.Tailscale, "tailscale");
        Check(NetAddresses.Classify("ZeroTier Virtual Port", new byte[] { 10, 147, 0, 5 }, false, out k) && k == NetKind.ZeroTier, "zerotier");
        Check(!NetAddresses.Classify("Hyper-V Virtual Ethernet Adapter | vEthernet (WSL)", new byte[] { 172, 20, 0, 1 }, false, out k), "hyper-v hidden");
        Check(!NetAddresses.Classify("VirtualBox Host-Only Ethernet Adapter", new byte[] { 192, 168, 56, 1 }, false, out k), "virtualbox hidden");
        Check(!NetAddresses.Classify("Intel Wi-Fi | Wi-Fi", new byte[] { 169, 254, 3, 4 }, false, out k), "link-local hidden");
        Check(!NetAddresses.Classify("Intel Wi-Fi | Wi-Fi", new byte[] { 8, 8, 8, 8 }, true, out k), "public nic not lan");
        System.Collections.Generic.List<NetAddress> picked = NetAddresses.Pick(new System.Collections.Generic.List<NetAddress>(new NetAddress[] {
            new NetAddress(NetKind.Radmin, IPAddress.Parse("26.1.2.3"), false), new NetAddress(NetKind.Lan, IPAddress.Parse("192.168.56.1"), false),
            new NetAddress(NetKind.Lan, IPAddress.Parse("192.168.1.20"), true) }));
        Check(picked.Count == 2 && picked[0].Address.ToString() == "192.168.1.20" && picked[1].Kind == NetKind.Radmin, "pick one per kind, gateway first");
        Check(NetAddresses.IsPublic("203.0.113.7") && !NetAddresses.IsPublic("100.70.1.1") && !NetAddresses.IsPublic("192.168.0.2") && !NetAddresses.IsPublic("") && !NetAddresses.IsPublic("0.0.0.0"), "public ip");

        // UPnP parsing.
        string desc = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><serviceList>" +
            "<service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType><controlURL>/l3f</controlURL></service>" +
            "<service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><controlURL>/ppp</controlURL></service>" +
            "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>ctl/IPConn</controlURL></service>" +
            "</serviceList></device></root>";
        var services = PortOpener.Services(desc, "http://192.168.1.1:5000/rootDesc.xml");
        Check(services.Count == 2 && services[0].Value == "http://192.168.1.1:5000/ctl/IPConn" && services[1].Value == "http://192.168.1.1:5000/ppp", "upnp services " + services.Count);
        Check(PortOpener.Header("HTTP/1.1 200 OK\r\nCache-Control: max-age=120\r\nLocation: http://192.168.1.1:1900/igd.xml\r\n\r\n", "LOCATION") == "http://192.168.1.1:1900/igd.xml", "ssdp location");
        string fault = "<s:Envelope><s:Body><s:Fault><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>718</errorCode></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        Check(PortOpener.ErrorCode(fault, 500) == 718 && PortOpener.ErrorCode("", 404) == 404, "soap error code");
        Check(PortOpener.Tag("<u:GetExternalIPAddressResponse><NewExternalIPAddress>203.0.113.7</NewExternalIPAddress></u:GetExternalIPAddressResponse>", "NewExternalIPAddress") == "203.0.113.7", "external ip tag");
        int status;
        string chunked = PortOpener.ParseResponse(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n"), out status);
        Check(status == 200 && chunked == "hello world", "http chunked");
        Check(PortOpener.ParseResponse(System.Text.Encoding.ASCII.GetBytes("HTTP/1.0 500 Internal Server Error\r\nContent-Length: 3\r\n\r\nabc"), out status) == "abc" && status == 500, "http plain");

        Console.WriteLine((failures == 0 ? "PASS " : "FAIL ") + (checks - failures) + "/" + checks + " controlli coop");
        return failures == 0 ? 0 : 1;
    }
}
