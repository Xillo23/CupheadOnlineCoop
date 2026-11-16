using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CupheadLanCoop
{
    public enum NetKind { Lan, Radmin, Tailscale, ZeroTier, Hamachi }

    public sealed class NetAddress
    {
        public NetKind Kind;
        public IPAddress Address;
        public bool Gateway;
        public NetAddress(NetKind kind, IPAddress address, bool gateway) { Kind = kind; Address = address; Gateway = gateway; }
    }

    // Addresses worth showing to the player: at most one per kind (LAN, then each VPN actually present and up).
    // Virtual adapters (Hyper-V/WSL, VirtualBox, VMware, Docker...) and link-local addresses are never shown.
    public static class NetAddresses
    {
        public static List<NetAddress> Fake; // screenshots: never publish real addresses

        static readonly string[] Virtual = { "Hyper-V", "vEthernet", "VirtualBox", "VMware", "WSL", "Virtual Ethernet", "Docker", "Npcap",
            "Loopback", "Bluetooth", "TAP-Windows", "Wintun", "Wi-Fi Direct", "Teredo", "isatap" };

        public static List<NetAddress> List()
        {
            if (Fake != null) return new List<NetAddress>(Fake);
            List<NetAddress> all = new List<NetAddress>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    try
                    {
                        IPInterfaceProperties props = nic.GetIPProperties();
                        bool gateway = false;
                        foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                            if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) gateway = true;
                        string desc = nic.Description + " | " + nic.Name;
                        foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
                        {
                            if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            NetKind kind;
                            if (Classify(desc, u.Address.GetAddressBytes(), gateway, out kind)) all.Add(new NetAddress(kind, u.Address, gateway));
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex) { NetLink.Log("Elenco interfacce fallito: " + ex.Message); }
            return Pick(all);
        }

        // One per kind (the one with a gateway wins), in enum order.
        public static List<NetAddress> Pick(List<NetAddress> all)
        {
            List<NetAddress> result = new List<NetAddress>();
            foreach (NetKind k in (NetKind[])Enum.GetValues(typeof(NetKind)))
            {
                NetAddress best = null;
                foreach (NetAddress a in all) if (a.Kind == k && (best == null || (a.Gateway && !best.Gateway))) best = a;
                if (best != null) result.Add(best);
            }
            return result;
        }

        // The home network address the router forwards to (needed for UPnP).
        public static IPAddress HomeLan()
        {
            foreach (NetAddress a in List()) if (a.Kind == NetKind.Lan && a.Gateway) return a.Address;
            return null;
        }

        public static bool Classify(string desc, byte[] ip, bool gateway, out NetKind kind)
        {
            kind = NetKind.Lan;
            if (ip.Length != 4 || ip[0] == 127 || ip[0] == 0 || (ip[0] == 169 && ip[1] == 254)) return false;
            if (Has(desc, "Radmin") || (ip[0] == 26 && !gateway)) { kind = NetKind.Radmin; return true; }
            if (Has(desc, "Tailscale")) { kind = NetKind.Tailscale; return true; }
            if (Has(desc, "ZeroTier")) { kind = NetKind.ZeroTier; return true; }
            if (Has(desc, "Hamachi") || (ip[0] == 25 && !gateway)) { kind = NetKind.Hamachi; return true; }
            if (ip[0] == 100 && ip[1] >= 64 && ip[1] < 128 && !gateway) { kind = NetKind.Tailscale; return true; }
            foreach (string v in Virtual) if (Has(desc, v)) return false;
            return IsPrivate(ip);
        }

        static bool Has(string s, string what) { return s != null && s.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0; }

        public static bool IsPrivate(byte[] ip)
        {
            return ip[0] == 10 || (ip[0] == 172 && ip[1] >= 16 && ip[1] < 32) || (ip[0] == 192 && ip[1] == 168);
        }

        // True for an address reachable from the whole internet (not private, CGNAT, link-local...).
        public static bool IsPublic(string text)
        {
            IPAddress a;
            if (string.IsNullOrEmpty(text) || !IPAddress.TryParse(text.Trim(), out a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] ip = a.GetAddressBytes();
            if (IsPrivate(ip) || ip[0] == 0 || ip[0] == 127 || ip[0] >= 224 || (ip[0] == 169 && ip[1] == 254)) return false;
            if (ip[0] == 100 && ip[1] >= 64 && ip[1] < 128) return false; // CGNAT
            return true;
        }
    }

    public enum PortState { Working, Open, Cgnat, DoubleNat, Failed }

    // Opens the game's TCP port on the home router with UPnP while hosting, and closes it afterwards.
    // Everything runs on one background thread; the menu only reads State/PublicIp/PublicPort.
    public sealed class PortOpener
    {
        public const string Description = "Cuphead Online Coop";
        public volatile PortState State = PortState.Working;
        public volatile string PublicIp = "";
        public volatile int PublicPort;

        // One router that speaks UPnP (its WAN service).
        sealed class Router { public string Control, Type, External; }

        // One opened port: on router R, external port Ext -> Client:ClientPort.
        sealed class Mapping { public Router R; public int Ext, ClientPort, Lease; public string Client; }

        // Known description URLs, for routers (often the provider's modem) that do not answer discovery.
        static readonly string[] KnownPaths = { ":52869/gatedesc.xml", ":5000/rootDesc.xml", ":49000/igddesc.xml", ":1900/igd.xml",
            ":49152/IGDdevicedesc_brlan0.xml", ":37215/upnpdev.xml", ":2189/gatedesc.xml", ":49152/gateway.xml", ":8000/gatedesc.xml" };

        static PortOpener current;
        readonly int port;
        readonly PortOpener previous;
        readonly ManualResetEvent wake = new ManualResetEvent(false);
        readonly List<Mapping> mappings = new List<Mapping>();
        Thread thread;
        volatile bool stopped;

        PortOpener(int port, PortOpener previous) { this.port = port; this.previous = previous; }

        public static PortOpener Start(int port)
        {
            PortOpener p = new PortOpener(port, current);
            current = p;
            p.thread = new Thread(p.Run);
            p.thread.IsBackground = true;
            p.thread.Name = "CupheadLanCoop UPnP";
            p.thread.Start();
            return p;
        }

        // Screenshots/tests: a finished opener showing a fixed address.
        public static PortOpener Faked(string ip, int port)
        {
            PortOpener p = new PortOpener(port, null);
            p.stopped = true; p.PublicIp = ip; p.PublicPort = port; p.State = PortState.Open;
            return p;
        }

        // Tests: a finished opener whose router refused (the two routers case).
        public static PortOpener FakedFailure()
        {
            PortOpener p = new PortOpener(0, null);
            p.stopped = true; p.State = PortState.DoubleNat;
            return p;
        }

        // Asks the worker to remove the mappings (it does so on its own thread).
        public void Stop() { stopped = true; wake.Set(); }

        // Game closing: give the worker a moment to remove the mappings before the process exits.
        public static void Shutdown(int waitMs)
        {
            PortOpener p = current;
            if (p == null) return;
            p.Stop();
            if (p.thread != null) p.thread.Join(waitMs);
        }

        void Run()
        {
            try
            {
                if (previous != null && previous.thread != null) previous.thread.Join(8000); // its delete must not undo our add
                IPAddress lan = NetAddresses.HomeLan();
                if (lan == null) { Fail("nessuna rete di casa con gateway"); return; }
                List<string> locations = Discover(lan, 2500);
                if (stopped) return;
                Router home = null;
                foreach (string location in locations)
                {
                    if (stopped) return;
                    if ((home = FindRouter(location, 2500)) != null) break;
                }
                if (home == null) { Fail("nessun router UPnP (" + locations.Count + " dispositivi risposti)"); return; }
                NetLink.Log("UPnP: router " + home.Control + " IP esterno " + (home.External ?? "?"));

                // Chain of routers: each one forwards to the next one inward, the first one to this PC.
                Router last = home;
                string client = lan.ToString();
                int clientPort = port;
                for (int hop = 0; hop < 3 && !stopped; hop++)
                {
                    if (last.External != null && IsCgnat(last.External)) { NetLink.Log("UPnP: IP esterno " + last.External + " = CGNAT dell'operatore"); State = PortState.Cgnat; return; }
                    Mapping m = Map(last, client, clientPort);
                    if (m == null)
                    {
                        if (hop == 0) Fail("AddPortMapping rifiutato");
                        else { NetLink.Log("UPnP: il router esterno rifiuta la porta"); State = PortState.DoubleNat; }
                        return;
                    }
                    if (last.External == null || NetAddresses.IsPublic(last.External))
                    {
                        PublicIp = last.External ?? "";
                        PublicPort = m.Ext;
                        if (!stopped) State = PortState.Open;
                        break;
                    }
                    // Its WAN address is private: another router (usually the provider's modem) sits in front of it.
                    NetLink.Log("UPnP: due router in cascata, cerco quello davanti a " + last.External);
                    Router outer = FindOuter(last.External);
                    if (outer == null) { NetLink.Log("UPnP: router esterno non trovato o senza UPnP"); State = PortState.DoubleNat; return; }
                    NetLink.Log("UPnP: router esterno " + outer.Control + " IP esterno " + (outer.External ?? "?"));
                    client = last.External;
                    clientPort = m.Ext;
                    last = outer;
                }
                if (State != PortState.Open && !stopped) { State = PortState.DoubleNat; return; }
                while (!stopped)
                {
                    int shortest = int.MaxValue;
                    foreach (Mapping m in mappings) if (m.Lease > 0 && m.Lease < shortest) shortest = m.Lease;
                    wake.WaitOne(shortest == int.MaxValue ? 30 * 60 * 1000 : (shortest / 2) * 1000, false);
                    if (stopped) break;
                    foreach (Mapping m in mappings) if (m.Lease > 0 && Add(m) != 0) NetLink.Log("UPnP: rinnovo fallito su " + m.R.Control);
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
            finally
            {
                for (int i = mappings.Count - 1; i >= 0; i--)
                {
                    try { NetLink.Log("UPnP: chiusura porta " + mappings[i].Ext + " su " + mappings[i].R.Control + " -> " + (Delete(mappings[i]) == 0 ? "ok" : "fallita")); }
                    catch (Exception ex) { NetLink.Log("UPnP: chiusura porta fallita " + ex.Message); }
                }
                mappings.Clear();
            }
        }

        static bool IsCgnat(string ip)
        {
            IPAddress a;
            if (!IPAddress.TryParse(ip.Trim(), out a)) return false;
            byte[] b = a.GetAddressBytes();
            return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] < 128;
        }

        // Opens the port on one router (tries the next ports if another device already uses it).
        Mapping Map(Router r, string client, int clientPort)
        {
            Mapping m = new Mapping();
            m.R = r; m.Client = client; m.ClientPort = clientPort; m.Lease = 3600;
            int error = -1;
            for (int ext = port; ext < port + 5 && !stopped; ext++)
            {
                m.Ext = ext;
                error = Add(m);
                if (error == 725) { m.Lease = 0; error = Add(m); } // router accepts only permanent mappings
                if (error == 0)
                {
                    mappings.Add(m);
                    NetLink.Log("UPnP: " + r.Control + " porta " + ext + " -> " + client + ":" + clientPort + " (lease " + m.Lease + "s)");
                    return m;
                }
                if (error != 718) break; // 718 = port taken by another device: try the next one
            }
            NetLink.Log("UPnP: AddPortMapping su " + r.Control + " errore " + error);
            return null;
        }

        // The router in front of ours: the gateway of our router's WAN network, usually .1 or .254.
        Router FindOuter(string wanIp)
        {
            IPAddress wan;
            if (!IPAddress.TryParse(wanIp.Trim(), out wan)) return null;
            byte[] b = wan.GetAddressBytes();
            foreach (int last in new int[] { 1, 254 })
            {
                if (stopped || last == b[3]) continue;
                string ip = b[0] + "." + b[1] + "." + b[2] + "." + last;
                List<string> locations = new List<string>();
                string asked = AskDirect(ip);
                if (asked != null) locations.Add(asked);
                foreach (string path in KnownPaths) locations.Add("http://" + ip + path);
                foreach (string location in locations)
                {
                    if (stopped) return null;
                    Router r = FindRouter(location, 800);
                    if (r != null) return r;
                }
            }
            return null;
        }

        // Discovery sent straight to one address (some routers answer this, not the multicast).
        static string AskDirect(string ip)
        {
            try
            {
                using (UdpClient u = new UdpClient(0))
                {
                    u.Client.ReceiveTimeout = 700;
                    byte[] q = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\n\r\n");
                    u.Send(q, q.Length, new IPEndPoint(IPAddress.Parse(ip), 1900));
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    return Header(Encoding.ASCII.GetString(u.Receive(ref from)), "LOCATION");
                }
            }
            catch (Exception) { return null; }
        }

        void Fail(string why)
        {
            NetLink.Log("UPnP: porta non aperta: " + why);
            if (State == PortState.Working) State = PortState.Failed;
        }

        // ---------- SSDP ----------

        static List<string> Discover(IPAddress local, int ms)
        {
            List<string> result = new List<string>();
            string[] targets = { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
                "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1" };
            IPEndPoint group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            using (UdpClient u = new UdpClient(new IPEndPoint(local, 0)))
            {
                u.Client.ReceiveTimeout = 250;
                try { u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2); } catch (Exception) { }
                DateTime start = DateTime.UtcNow, deadline = start.AddMilliseconds(ms);
                bool resent = false;
                SendSearch(u, group, targets);
                while (DateTime.UtcNow < deadline)
                {
                    if (!resent && DateTime.UtcNow > start.AddMilliseconds(800)) { resent = true; SendSearch(u, group, targets); }
                    try
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        string reply = Encoding.ASCII.GetString(u.Receive(ref from));
                        string location = Header(reply, "LOCATION");
                        if (location != null && !result.Contains(location))
                        {
                            result.Add(location);
                            DateTime soon = DateTime.UtcNow.AddMilliseconds(700); // other devices may still answer
                            if (soon < deadline) deadline = soon;
                        }
                    }
                    catch (SocketException) { }
                }
            }
            return result;
        }

        static void SendSearch(UdpClient u, IPEndPoint group, string[] targets)
        {
            foreach (string st in targets)
            {
                byte[] q = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: " + st + "\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n\r\n");
                try { u.Send(q, q.Length, group); } catch (Exception) { }
            }
        }

        public static string Header(string response, string name)
        {
            foreach (string line in response.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && string.Compare(line.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase) == 0) return line.Substring(colon + 1).Trim();
            }
            return null;
        }

        // ---------- device description ----------

        // The first WAN service of this device that answers GetExternalIPAddress (WANIPConnection preferred).
        static Router FindRouter(string location, int connectMs)
        {
            string xml;
            try { int status; xml = Http(location, "GET", null, null, connectMs, out status); if (status != 200) return null; }
            catch (Exception) { return null; }
            foreach (KeyValuePair<string, string> s in Services(xml, location))
            {
                Router r = new Router();
                r.Control = s.Value; r.Type = s.Key;
                try
                {
                    int status;
                    string reply = Soap(r, "GetExternalIPAddress", "", out status);
                    if (status != 200) continue;
                    r.External = Tag(reply, "NewExternalIPAddress");
                    if (r.External != null && r.External.Trim().Length == 0) r.External = null;
                    return r;
                }
                catch (Exception ex) { NetLink.Log("UPnP: " + r.Control + " non risponde: " + ex.Message); }
            }
            return null;
        }

        // (serviceType, absolute controlURL) of every WAN connection service, WANIPConnection first.
        public static List<KeyValuePair<string, string>> Services(string xml, string location)
        {
            List<KeyValuePair<string, string>> ip = new List<KeyValuePair<string, string>>(), ppp = new List<KeyValuePair<string, string>>();
            string urlBase = Tag(xml, "URLBase");
            Uri baseUri;
            try { baseUri = new Uri(string.IsNullOrEmpty(urlBase) ? location : urlBase.Trim()); }
            catch (UriFormatException) { baseUri = new Uri(location); }
            foreach (Match m in Regex.Matches(xml, "<(?:\\w+:)?service>(.*?)</(?:\\w+:)?service>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                string type = Tag(m.Groups[1].Value, "serviceType"), url = Tag(m.Groups[1].Value, "controlURL");
                if (type == null || url == null) continue;
                type = type.Trim();
                bool isIp = type.IndexOf(":WANIPConnection:", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isIp && type.IndexOf(":WANPPPConnection:", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string absolute;
                try { absolute = new Uri(baseUri, url.Trim()).ToString(); } catch (UriFormatException) { continue; }
                (isIp ? ip : ppp).Add(new KeyValuePair<string, string>(type, absolute));
            }
            ip.AddRange(ppp);
            return ip;
        }

        public static string Tag(string xml, string name)
        {
            Match m = Regex.Match(xml ?? "", "<(?:\\w+:)?" + name + "(?:\\s[^>]*)?>([^<]*)</(?:\\w+:)?" + name + ">", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---------- SOAP ----------

        static int Add(Mapping m)
        {
            int status;
            string reply = Soap(m.R, "AddPortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + m.Ext + "</NewExternalPort><NewProtocol>TCP</NewProtocol>" +
                "<NewInternalPort>" + m.ClientPort + "</NewInternalPort><NewInternalClient>" + m.Client + "</NewInternalClient><NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>" + Description + "</NewPortMappingDescription><NewLeaseDuration>" + m.Lease + "</NewLeaseDuration>", out status);
            return status == 200 ? 0 : ErrorCode(reply, status);
        }

        static int Delete(Mapping m)
        {
            int status;
            string reply = Soap(m.R, "DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + m.Ext + "</NewExternalPort><NewProtocol>TCP</NewProtocol>", out status);
            return status == 200 ? 0 : ErrorCode(reply, status);
        }

        public static int ErrorCode(string reply, int status)
        {
            string code = Tag(reply, "errorCode");
            int n;
            return code != null && int.TryParse(code.Trim(), out n) ? n : (status == 0 ? -1 : status);
        }

        static string Soap(Router r, string action, string args, out int status)
        {
            string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:" + action + " xmlns:u=\"" + r.Type + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
            return Http(r.Control, "POST", r.Type + "#" + action, body, 2500, out status);
        }

        // Minimal HTTP/1.1 client (Connection: close, Content-Length or chunked bodies).
        static string Http(string url, string method, string soapAction, string body, int connectMs, out int status)
        {
            status = 0;
            Uri u = new Uri(url);
            byte[] payload = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            StringBuilder h = new StringBuilder();
            h.Append(method).Append(' ').Append(u.PathAndQuery).Append(" HTTP/1.1\r\nHost: ").Append(u.Host).Append(':').Append(u.Port).Append("\r\nConnection: close\r\n");
            if (soapAction != null) h.Append("Content-Type: text/xml; charset=\"utf-8\"\r\nSOAPAction: \"").Append(soapAction).Append("\"\r\n");
            if (method == "POST") h.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            h.Append("\r\n");
            using (TcpClient c = new TcpClient())
            {
                IAsyncResult ar = c.BeginConnect(u.Host, u.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(connectMs, false)) throw new IOException("timeout connessione " + u.Host + ":" + u.Port);
                c.EndConnect(ar);
                c.ReceiveTimeout = 4000; c.SendTimeout = 4000;
                NetworkStream ns = c.GetStream();
                byte[] head = Encoding.ASCII.GetBytes(h.ToString());
                ns.Write(head, 0, head.Length);
                if (payload.Length > 0) ns.Write(payload, 0, payload.Length);
                MemoryStream all = new MemoryStream();
                byte[] buf = new byte[8192];
                while (true)
                {
                    int n;
                    try { n = ns.Read(buf, 0, buf.Length); }
                    catch (IOException) { if (all.Length > 0) break; throw; }
                    if (n <= 0) break;
                    all.Write(buf, 0, n);
                    if (Complete(all.ToArray())) break;
                    if (all.Length > 1024 * 1024) throw new IOException("risposta troppo grande");
                }
                return ParseResponse(all.ToArray(), out status);
            }
        }

        static bool Complete(byte[] data)
        {
            int split = Find(data, 0, "\r\n\r\n");
            if (split < 0) return false;
            string headers = Encoding.ASCII.GetString(data, 0, split);
            string length = Header(headers, "Content-Length");
            int n;
            if (length != null && int.TryParse(length, out n)) return data.Length - split - 4 >= n;
            string te = Header(headers, "Transfer-Encoding");
            return te != null && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0 && data.Length >= split + 9 &&
                Find(data, data.Length - 5, "0\r\n\r\n") == data.Length - 5;
        }

        public static string ParseResponse(byte[] data, out int status)
        {
            status = 0;
            int split = Find(data, 0, "\r\n\r\n");
            string headers = Encoding.ASCII.GetString(data, 0, split < 0 ? data.Length : split);
            int start = split < 0 ? data.Length : split + 4;
            string[] first = headers.Split('\n')[0].Split(' ');
            if (first.Length > 1) int.TryParse(first[1], out status);
            string te = Header(headers, "Transfer-Encoding");
            MemoryStream body = new MemoryStream();
            if (te != null && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int pos = start;
                while (pos < data.Length)
                {
                    int eol = Find(data, pos, "\r\n");
                    if (eol < 0) break;
                    string sizeText = Encoding.ASCII.GetString(data, pos, eol - pos).Split(';')[0].Trim();
                    int size;
                    if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out size) || size <= 0) break;
                    pos = eol + 2;
                    size = Math.Min(size, data.Length - pos);
                    body.Write(data, pos, size);
                    pos += size + 2;
                }
            }
            else body.Write(data, start, data.Length - start);
            return Encoding.UTF8.GetString(body.ToArray());
        }

        static int Find(byte[] data, int from, string what)
        {
            for (int i = from; i + what.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < what.Length && data[i + j] == (byte)what[j]) j++;
                if (j == what.Length) return i;
            }
            return -1;
        }
    }
}
