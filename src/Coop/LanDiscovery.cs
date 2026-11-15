using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CupheadLanCoop
{
    public sealed class LocalAddress
    {
        public string Label;
        public IPAddress Address, Broadcast;
        public bool Radmin;

        // Every up IPv4 interface, Radmin (26.x.x.x) first.
        public static List<LocalAddress> Fake; // screenshots: never publish real addresses

        public static List<LocalAddress> List()
        {
            if (Fake != null) return new List<LocalAddress>(Fake);
            List<LocalAddress> result = new List<LocalAddress>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    try
                    {
                        foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                        {
                            if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            LocalAddress a = new LocalAddress();
                            a.Address = u.Address;
                            byte[] ip = u.Address.GetAddressBytes();
                            a.Radmin = ip[0] == 26 || nic.Description.IndexOf("Radmin", StringComparison.OrdinalIgnoreCase) >= 0;
                            byte[] mask = u.IPv4Mask != null ? u.IPv4Mask.GetAddressBytes() : new byte[] { 255, 255, 255, 0 };
                            byte[] b = new byte[4];
                            for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                            a.Broadcast = new IPAddress(b);
                            a.Label = (a.Radmin ? "RADMIN " : (ip[0] == 100 && ip[1] >= 64 && ip[1] < 128 ? "TAILSCALE " : (nic.Description.IndexOf("ZeroTier", StringComparison.OrdinalIgnoreCase) >= 0 ? "ZEROTIER " : ""))) + u.Address;
                            if (a.Radmin) result.Insert(0, a); else result.Add(a);
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex) { NetLink.Log("Elenco interfacce fallito: " + ex.Message); }
            return result;
        }
    }

    public sealed class FoundGame
    {
        public string Address, Name, Version;
        public int Port, PingMs;
        public DateTime LastSeen;
    }

    // UDP 28148. Host answers "CUPCOOP?" with "CUPCOOP!|version|port|name".
    public sealed class LanDiscovery
    {
        const string Query = "CUPCOOP?", Answer = "CUPCOOP!";
        readonly UdpClient udp;
        readonly bool responder;
        readonly string version, name;
        readonly int tcpPort, discoveryPort;
        volatile bool stopped;
        readonly Dictionary<string, FoundGame> found = new Dictionary<string, FoundGame>();
        readonly object gate = new object();
        DateTime lastQuery = DateTime.MinValue;

        LanDiscovery(UdpClient u, bool responder, string version, string name, int tcpPort, int discoveryPort)
        {
            udp = u; this.responder = responder; this.version = version; this.name = name;
            this.tcpPort = tcpPort; this.discoveryPort = discoveryPort;
            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "CupheadLanCoop discovery";
            t.Start();
        }

        public static LanDiscovery StartResponder(int discoveryPort, string version, string name, int tcpPort)
        {
            try
            {
                UdpClient u = new UdpClient();
                u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                u.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
                return new LanDiscovery(u, true, version, name, tcpPort, discoveryPort);
            }
            catch (Exception ex)
            {
                NetLink.Log("Discovery host non disponibile (resta l'IP manuale): " + ex.Message);
                return null;
            }
        }

        public static LanDiscovery StartBrowser(int discoveryPort, string version)
        {
            try
            {
                UdpClient u = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                u.EnableBroadcast = true;
                return new LanDiscovery(u, false, version, "", 0, discoveryPort);
            }
            catch (Exception ex)
            {
                NetLink.Log("Ricerca partite non disponibile: " + ex.Message);
                return null;
            }
        }

        // Browser: call every frame; sends a query each second on all interfaces.
        public void Tick()
        {
            if (responder || stopped || (DateTime.UtcNow - lastQuery).TotalSeconds < 1) return;
            lastQuery = DateTime.UtcNow;
            byte[] q = Encoding.UTF8.GetBytes(Query);
            List<IPAddress> targets = new List<IPAddress>();
            targets.Add(IPAddress.Broadcast);
            targets.Add(IPAddress.Loopback);
            foreach (LocalAddress a in LocalAddress.List()) if (!targets.Contains(a.Broadcast)) targets.Add(a.Broadcast);
            foreach (IPAddress t in targets)
            {
                try { udp.Send(q, q.Length, new IPEndPoint(t, discoveryPort)); } catch (Exception) { }
            }
        }

        public List<FoundGame> Games()
        {
            List<FoundGame> list = new List<FoundGame>();
            lock (gate)
            {
                foreach (FoundGame g in found.Values) if ((DateTime.UtcNow - g.LastSeen).TotalSeconds < 4) list.Add(g);
            }
            list.Sort(delegate(FoundGame a, FoundGame b) { return a.PingMs.CompareTo(b.PingMs); });
            return list;
        }

        public void Stop()
        {
            stopped = true;
            try { udp.Close(); } catch { }
        }

        void Loop()
        {
            while (!stopped)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udp.Receive(ref from);
                    string text = Encoding.UTF8.GetString(data);
                    if (responder && text == Query)
                    {
                        byte[] a = Encoding.UTF8.GetBytes(Answer + "|" + version + "|" + tcpPort + "|" + name);
                        udp.Send(a, a.Length, from);
                    }
                    else if (!responder && text.StartsWith(Answer + "|"))
                    {
                        string[] parts = text.Split(new char[] { '|' }, 4);
                        int port;
                        if (parts.Length != 4 || !int.TryParse(parts[2], out port)) continue;
                        string key = from.Address + ":" + port;
                        lock (gate)
                        {
                            FoundGame g;
                            if (!found.TryGetValue(key, out g))
                            {
                                g = new FoundGame();
                                found[key] = g;
                            }
                            // Prefer a non-loopback address for the same host if both answer.
                            g.Address = from.Address.ToString(); g.Port = port; g.Version = parts[1]; g.Name = parts[3];
                            g.PingMs = (int)Math.Max(0, (DateTime.UtcNow - lastQuery).TotalMilliseconds);
                            g.LastSeen = DateTime.UtcNow;
                        }
                    }
                }
                catch (Exception)
                {
                    if (stopped) return;
                    Thread.Sleep(50);
                }
            }
        }
    }
}
