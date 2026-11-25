using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CupheadLanCoop
{
    public enum DesyncKind { None, Soft, Hard }

    // Pure, Unity-free classification of DesyncCheck detail lines and clipboard/IP helpers.
    // Detail format: "f=1560 fx=1560 sc=scene_level_mouse rng=386b8384 p1=439f1556,c37f7899,hp3,sm41f9666c p2=..."
    public static class DesyncClassifier
    {
        const float HardPosThresholdSq = 25f * 25f;

        sealed class PlayerSnap
        {
            public bool Present;
            public float X, Y;
            public int Hp = -1;
            public string Sm = "";
        }

        sealed class Snap
        {
            public string Fx = "", Scene = "", Rng = "";
            public PlayerSnap P1 = new PlayerSnap(), P2 = new PlayerSnap();
        }

        public static DesyncKind Classify(string localDetail, string remoteDetail, out bool fatalHpMismatch)
        {
            fatalHpMismatch = false;
            if (localDetail == remoteDetail) return DesyncKind.None;
            if (string.IsNullOrEmpty(localDetail) || string.IsNullOrEmpty(remoteDetail)) return DesyncKind.Hard;
            Snap a = Parse(localDetail), b = Parse(remoteDetail);
            if (a.Scene != b.Scene || a.Fx != b.Fx) return DesyncKind.Hard;
            bool p1Fatal, p2Fatal;
            bool p1Hard = PlayerHardDiff(a.P1, b.P1, out p1Fatal);
            bool p2Hard = PlayerHardDiff(a.P2, b.P2, out p2Fatal);
            fatalHpMismatch = p1Fatal || p2Fatal;
            if (p1Hard || p2Hard) return DesyncKind.Hard;
            return DesyncKind.Soft;
        }

        static bool PlayerHardDiff(PlayerSnap a, PlayerSnap b, out bool fatalHp)
        {
            fatalHp = false;
            if (a.Present != b.Present) { fatalHp = true; return true; }
            if (!a.Present) return false;
            if (a.Hp != b.Hp)
            {
                if ((a.Hp == 0 && b.Hp > 0) || (b.Hp == 0 && a.Hp > 0)) fatalHp = true;
                return true;
            }
            float dx = a.X - b.X, dy = a.Y - b.Y;
            if (float.IsNaN(dx) || float.IsNaN(dy) || dx * dx + dy * dy > HardPosThresholdSq) return true;
            return false;
        }

        static Snap Parse(string detail)
        {
            Snap s = new Snap();
            foreach (string tok in detail.Split(' '))
            {
                if (tok.StartsWith("fx=")) s.Fx = tok.Substring(3);
                else if (tok.StartsWith("sc=")) s.Scene = tok.Substring(3);
                else if (tok.StartsWith("rng=")) s.Rng = tok.Substring(4);
                else if (tok.StartsWith("p1=")) s.P1 = ParsePlayer(tok.Substring(3));
                else if (tok.StartsWith("p2=")) s.P2 = ParsePlayer(tok.Substring(3));
            }
            return s;
        }

        static PlayerSnap ParsePlayer(string value)
        {
            PlayerSnap p = new PlayerSnap();
            if (value == "-" || value.Length == 0) return p;
            string[] parts = value.Split(',');
            if (parts.Length < 2) return p;
            p.Present = true;
            p.X = HexToFloat(parts[0]);
            p.Y = HexToFloat(parts[1]);
            for (int i = 2; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("hp")) int.TryParse(parts[i].Substring(2), out p.Hp);
                else if (parts[i].StartsWith("sm")) p.Sm = parts[i].Substring(2);
            }
            return p;
        }

        static float HexToFloat(string hex)
        {
            try { return BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToInt32(hex, 16)), 0); }
            catch { return float.NaN; }
        }
    }

    // Decides when a sustained hard desync should trigger an automatic scene resync instead of
    // letting the two PCs drift into separate worlds and eventually hit the 8-second barrier deadlock.
    public sealed class HardDesyncTracker
    {
        public int ConsecutiveHard { get; private set; }

        public void Reset() { ConsecutiveHard = 0; }

        public void OnMatch() { ConsecutiveHard = 0; }

        public bool OnMismatch(DesyncKind kind, bool fatalHpMismatch)
        {
            if (kind != DesyncKind.Hard) { ConsecutiveHard = 0; return false; }
            ConsecutiveHard++;
            return ConsecutiveHard >= 3 || (fatalHpMismatch && ConsecutiveHard >= 2);
        }
    }

    public static class IpUtil
    {
        const string Allowed = "0123456789.:";
        static readonly Regex Ipv4Pattern = new Regex(@"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?::[0-9]{1,5})?\b");

        // Extracts a clean IPv4 (optional :port) from arbitrary clipboard text.
        public static string SanitizeIp(string raw, int maxLen)
        {
            if (string.IsNullOrEmpty(raw) || maxLen <= 0) return "";
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) return "";
            Match m = Ipv4Pattern.Match(trimmed);
            if (m.Success)
            {
                string hit = m.Value;
                return hit.Length <= maxLen ? hit : hit.Substring(0, maxLen);
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < trimmed.Length && sb.Length < maxLen; i++)
            {
                char c = trimmed[i];
                if (Allowed.IndexOf(c) >= 0) sb.Append(c);
            }
            return sb.ToString();
        }

        // Picks the best address to copy to clipboard for the host:
        // 1) public UPnP address if open, 2) VPN address (Radmin/Tailscale/ZeroTier/Hamachi), 3) LAN address.
        public static string PickCopyIp(PortState upnpState, string publicIp, int publicPort, int defaultPort, List<NetAddress> addrs)
        {
            if (upnpState == PortState.Open && !string.IsNullOrEmpty(publicIp))
            {
                string ip = publicIp.Trim();
                if (ip.Length > 0) return publicPort > 0 && publicPort != defaultPort ? ip + ":" + publicPort : ip;
            }
            if (addrs != null)
            {
                foreach (NetAddress a in addrs)
                    if (a != null && a.Address != null && a.Kind != NetKind.Lan) return a.Address.ToString();
                foreach (NetAddress a in addrs)
                    if (a != null && a.Address != null) return a.Address.ToString();
            }
            return "";
        }
    }
}
