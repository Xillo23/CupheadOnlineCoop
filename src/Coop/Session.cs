using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace CupheadLanCoop
{
    internal enum CoopState { Idle, Hosting, Joining, Connected, InGame }

    internal sealed class StartInfo
    {
        public int Slot, Seed, Delay, Language;
        public bool Mugman, Empty, Rotate;
        public string Json, TestLevel = "";
    }

    internal static class Session
    {
        public const int Protocol = 1;
        public static CoopState State { get; private set; }
        public static bool IsHost { get; private set; }
        public static string PeerName = "", Error = "", Notice = "";
        public static float NoticeUntil;
        public static int PingMs = -1;
        public static bool SaveGuardArmed;

        static NetHost listener;
        public static PortOpener Upnp; // null = UPnP off (config, test on one PC)
        static NetLink link;
        static LanDiscovery discovery;
        static bool welcomed;
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static long lastPing;
        static double rttAverage = -1;
        static int seed, loadSeq, resyncs;
        static readonly Dictionary<int, uint> peerDataHash = new Dictionary<int, uint>();
        static readonly Dictionary<int, string> peerDataFix = new Dictionary<int, string>();
        static int peerArrived = -1;
        static int savedLanguage = -1;
        static bool savedRotate;
        static List<KeyValuePair<string, string>> fingerprint;
        static StartInfo pendingStart;
        static int hostWaitSlot = -1;
        static bool hostWaitMugman;
        public static bool HostWaiting { get { return hostWaitSlot >= 0 && State != CoopState.InGame; } }

        public static int LoadSeq { get { return loadSeq; } }
        public static int SeedFor(int seq, int salt) { return seed ^ (seq * 7919) ^ (salt * 104729); }

        public static bool PeerReady { get { return link != null && link.Connected && welcomed && State == CoopState.Connected; } }
        public static bool InGame { get { return State == CoopState.InGame; } }
        public static LanDiscovery Discovery { get { return discovery; } }

        // ---------- lobby ----------

        public static void Host()
        {
            Cancel();
            string error;
            listener = NetHost.Listen(CoopPlugin.Port, out error);
            if (listener == null) { Error = error; return; }
            discovery = LanDiscovery.StartResponder(CoopPlugin.DiscoveryPort, CoopPlugin.Version, CoopPlugin.PlayerName, CoopPlugin.Port);
            if (CoopPlugin.FakePublicIp != null) Upnp = PortOpener.Faked(CoopPlugin.FakePublicIp, CoopPlugin.Port);
            else if (CoopPlugin.UseUpnp) Upnp = PortOpener.Start(CoopPlugin.Port);
            IsHost = true;
            State = CoopState.Hosting;
            Determinism.Background(true);
            Error = "";
            CoopPlugin.Log("Host in ascolto su TCP " + CoopPlugin.Port);
        }

        static void StopUpnp()
        {
            if (Upnp != null) Upnp.Stop();
            Upnp = null;
        }

        public static void Browse()
        {
            if (discovery != null) return;
            discovery = LanDiscovery.StartBrowser(CoopPlugin.DiscoveryPort, CoopPlugin.Version);
        }

        public static void Join(string address, int port)
        {
            Cancel();
            IsHost = false;
            State = CoopState.Joining;
            Determinism.Background(true);
            Error = "";
            string error;
            link = NetLink.Connect(address, port, 4000, out error);
            if (link == null) { State = CoopState.Idle; Error = error; return; }
            link.Send(Msg.Hello, Msg.Build(delegate(BinaryWriter w)
            {
                w.Write(Protocol); w.Write(CoopPlugin.Version); w.Write(CoopPlugin.PlayerName);
                List<KeyValuePair<string, string>> fp = Fingerprint();
                w.Write(fp.Count);
                foreach (KeyValuePair<string, string> e in fp) { w.Write(e.Key); w.Write(e.Value); }
            }));
            CoopPlugin.Log("Connesso a " + address + ":" + port + ", Hello inviato");
        }

        // Leave the lobby (not used during a game: see End).
        public static void Cancel()
        {
            if (link != null) { link.Send(Msg.Bye, Msg.Build(delegate(BinaryWriter w) { w.Write(L.M("partner_cancel")); })); link.Close("annullato"); }
            link = null;
            if (listener != null) listener.Stop();
            listener = null;
            StopUpnp();
            if (discovery != null) discovery.Stop();
            discovery = null;
            welcomed = false;
            PeerName = "";
            PingMs = -1;
            rttAverage = -1;
            hostWaitSlot = -1;
            if (State != CoopState.InGame) { State = CoopState.Idle; Determinism.Background(false); }
        }

        public static void Update()
        {
            if (discovery != null) discovery.Tick();
            if (State == CoopState.Hosting && link == null && listener != null)
            {
                NetLink l = listener.TryAccept();
                if (l != null) { link = l; CoopPlugin.Log("Guest connesso da " + l.Remote); }
            }
            Pump();
            if (State == CoopState.InGame) TryEnterPending();
            if (HostWaiting && PeerReady && !SceneLoader.CurrentlyLoading)
            {
                int slot = hostWaitSlot;
                hostWaitSlot = -1;
                HostStart(slot, hostWaitMugman);
            }
            if (HostWaiting && !IsHost) hostWaitSlot = -1;
            if (link != null && (State == CoopState.Connected || State == CoopState.InGame) && clock.ElapsedMilliseconds - lastPing > 1000)
            {
                lastPing = clock.ElapsedMilliseconds;
                long now = lastPing;
                link.Send(Msg.Ping, Msg.Build(delegate(BinaryWriter w) { w.Write(now); }));
            }
            CoopPlugin.AutoTestTick();
        }

        public static void Pump()
        {
            if (link == null) return;
            NetMessage m;
            while (link != null && link.TryReceive(out m))
            {
                try { Handle(m); }
                catch (Exception ex) { CoopPlugin.Log("Messaggio " + m.Type + " non valido: " + ex); }
            }
            if (link != null && !link.Connected)
            {
                string reason = link.CloseReason;
                if (State == CoopState.InGame) End(L.Join(L.M("conn_lost"), reason));
                else
                {
                    link = null; welcomed = false; PeerName = "";
                    if (IsHost && listener != null) State = CoopState.Hosting;
                    else { State = CoopState.Idle; Determinism.Background(false); if (Error.Length == 0) Error = reason; }
                }
            }
        }

        static void Handle(NetMessage m)
        {
            BinaryReader r = Msg.Read(m.Body);
            if (m.Type != Msg.Input && m.Type != Msg.Ping && m.Type != Msg.Pong && m.Type != Msg.Hash) CoopPlugin.Log("Messaggio " + m.Type + " (" + m.Body.Length + "B) stato=" + State);
            switch (m.Type)
            {
                case Msg.Input: Lockstep.OnRemoteInput(m.Body); break;
                case Msg.Hello: OnHello(r); break;
                case Msg.Welcome:
                    PeerName = r.ReadString();
                    welcomed = true;
                    State = CoopState.Connected;
                    CoopPlugin.Log("Accettato dall'host " + PeerName);
                    break;
                case Msg.Reject:
                    Error = r.ReadString();
                    link.Close("rifiutato");
                    break;
                case Msg.Start: if (!IsHost && State == CoopState.Connected) Begin(ReadStart(r)); break;
                case Msg.DataHash: { int seq = r.ReadInt32(); peerDataHash[seq] = r.ReadUInt32(); break; }
                case Msg.DataFix: { int seq = r.ReadInt32(); peerDataFix[seq] = r.ReadString(); break; }
                case Msg.Arrive: peerArrived = Math.Max(peerArrived, r.ReadInt32()); break;
                case Msg.Snap: SoftResync.OnSnap(m.Body); break;
                case Msg.Hash: { int e = r.ReadInt32(); int f = r.ReadInt32(); DesyncCheck.OnRemote(e, f, r.ReadString()); break; }
                case Msg.Ping: { long t = r.ReadInt64(); link.Send(Msg.Pong, Msg.Build(delegate(BinaryWriter w) { w.Write(t); })); break; }
                case Msg.Pong:
                    {
                        double rtt = clock.ElapsedMilliseconds - r.ReadInt64();
                        rttAverage = rttAverage < 0 ? rtt : rttAverage * 0.7 + rtt * 0.3;
                        PingMs = (int)Math.Round(rttAverage);
                        break;
                    }
                case Msg.Bye:
                    {
                        string reason = r.ReadString();
                        if (State == CoopState.InGame) End(reason);
                        else { Error = reason; link.Close(reason); }
                        break;
                    }
            }
        }

        static void OnHello(BinaryReader r)
        {
            if (!IsHost || welcomed) return;
            int protocol = r.ReadInt32();
            string version = r.ReadString(), name = r.ReadString();
            int n = r.ReadInt32();
            Dictionary<string, string> theirs = new Dictionary<string, string>();
            for (int i = 0; i < n; i++) { string k = r.ReadString(); theirs[k] = r.ReadString(); }
            string reason = "";
            if (protocol != Protocol || version != CoopPlugin.Version)
                reason = L.M("version_diff", CoopPlugin.Version, version);
            else reason = CompareFingerprint(theirs);
            if (reason.Length > 0)
            {
                CoopPlugin.Log("Rifiuto " + name + ": " + reason);
                string text = reason;
                link.Send(Msg.Reject, Msg.Build(delegate(BinaryWriter w) { w.Write(text); }));
                link.Close("rifiutato: " + reason);
                Error = L.Join(L.M("rejected", name), reason);
                return;
            }
            PeerName = name;
            welcomed = true;
            State = CoopState.Connected;
            link.Send(Msg.Welcome, Msg.Build(delegate(BinaryWriter w) { w.Write(CoopPlugin.PlayerName); }));
            CoopPlugin.Log("Ospite accettato: " + name);
        }

        static string CompareFingerprint(Dictionary<string, string> theirs)
        {
            List<string> diff = new List<string>();
            Dictionary<string, string> mine = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> e in Fingerprint()) mine[e.Key] = e.Value;
            foreach (KeyValuePair<string, string> e in mine)
            {
                string v;
                if (!theirs.TryGetValue(e.Key, out v)) diff.Add(e.Key + " (host)");
                else if (v != e.Value) diff.Add(e.Key + " (!=)");
            }
            foreach (string k in theirs.Keys) if (!mine.ContainsKey(k)) diff.Add(k + " (guest)");
            if (diff.Count == 0) return "";
            return L.M("install_diff", string.Join(", ", diff.ToArray()));
        }

        // Everything that must be identical for lockstep: game code, Rewired, other mods and their config, DLC, actions.
        static List<KeyValuePair<string, string>> Fingerprint()
        {
            if (fingerprint != null) return fingerprint;
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            string managed = Path.Combine(Path.Combine(BepInEx.Paths.GameRootPath, "Cuphead_Data"), "Managed");
            Add(list, "Assembly-CSharp.dll", Path.Combine(managed, "Assembly-CSharp.dll"));
            Add(list, "Rewired_Core.dll", Path.Combine(managed, "Rewired_Core.dll"));
            List<string> plugins = new List<string>(Directory.GetFiles(BepInEx.Paths.PluginPath, "*.dll", SearchOption.AllDirectories));
            plugins.Sort(StringComparer.OrdinalIgnoreCase);
            string self = Path.GetFullPath(typeof(CoopPlugin).Assembly.Location);
            foreach (string p in plugins)
            {
                // Our own DLL is covered by the version check; its bytes differ between builds of the same version.
                if (string.Equals(Path.GetFullPath(p), self, StringComparison.OrdinalIgnoreCase)) continue;
                Add(list, "plugins/" + Path.GetFileName(p), p);
            }
            List<string> configs = new List<string>(Directory.GetFiles(BepInEx.Paths.ConfigPath, "*.cfg"));
            configs.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string c in configs)
            {
                string name = Path.GetFileName(c);
                if (name.Equals("BepInEx.cfg", StringComparison.OrdinalIgnoreCase) || name.StartsWith("it.cupheadlan", StringComparison.OrdinalIgnoreCase)) continue;
                Add(list, "config/" + name, c);
            }
            list.Add(new KeyValuePair<string, string>("DLC", DLCManager.DLCEnabled() ? "si" : "no"));
            list.Add(new KeyValuePair<string, string>("azioni Rewired", Fnv.Add(Fnv.Seed, Lockstep.ActionSignature).ToString("x8")));
            fingerprint = list;
            return list;
        }

        static void Add(List<KeyValuePair<string, string>> list, string key, string path)
        {
            string hash;
            try
            {
                using (SHA256 sha = SHA256.Create()) using (FileStream f = File.OpenRead(path))
                    hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
            }
            catch (Exception ex) { hash = "errore:" + ex.Message; }
            list.Add(new KeyValuePair<string, string>(key, hash));
        }

        // ---------- start ----------

        public static bool IsSlotEmpty(int slot)
        {
            PlayerData d = PlayerData.GetDataForSlot(slot);
            return !d.GetMapData(Scenes.scene_map_world_1).sessionStarted && !d.IsTutorialCompleted && d.CountLevelsCompleted(Level.world1BossLevels) == 0;
        }

        // Host: the native slot screen chose a slot; start the session instead of the local game.
        public static void HostStart(int slot, bool mugman)
        {
            StartInfo s = new StartInfo();
            s.Slot = slot; s.Mugman = mugman; s.Empty = IsSlotEmpty(slot);
            s.Seed = new System.Random().Next();
            s.Delay = CoopPlugin.InputDelay > 0 ? CoopPlugin.InputDelay : PadCodec.DelayFromRtt(rttAverage < 0 ? 0 : rttAverage);
            s.Language = (int)Localization.language;
            s.Rotate = SettingsData.Data.rotateControlsWithCamera;
            s.Json = JsonUtility.ToJson(PlayerData.GetDataForSlot(slot));
            s.TestLevel = CoopPlugin.TestLevel;
            link.Send(Msg.Start, Msg.Build(delegate(BinaryWriter w)
            {
                w.Write(s.Slot); w.Write(s.Mugman); w.Write(s.Empty); w.Write(s.Seed); w.Write(s.Delay);
                w.Write(s.Language); w.Write(s.Rotate); w.Write(s.Json); w.Write(s.TestLevel);
            }));
            Begin(s);
        }

        static StartInfo ReadStart(BinaryReader r)
        {
            StartInfo s = new StartInfo();
            s.Slot = r.ReadInt32(); s.Mugman = r.ReadBoolean(); s.Empty = r.ReadBoolean(); s.Seed = r.ReadInt32();
            s.Delay = r.ReadInt32(); s.Language = r.ReadInt32(); s.Rotate = r.ReadBoolean(); s.Json = r.ReadString();
            s.TestLevel = r.ReadString();
            return s;
        }

        static void Begin(StartInfo s)
        {
            CoopPlugin.Log("Inizio sessione: host=" + IsHost + " slot=" + s.Slot + " vuoto=" + s.Empty + " D=" + s.Delay + " ping=" + PingMs);
            if (discovery != null) { discovery.Stop(); discovery = null; }
            State = CoopState.InGame;
            seed = s.Seed; loadSeq = 0; resyncs = 0; peerArrived = -1;
            peerDataHash.Clear(); peerDataFix.Clear();
            DesyncCheck.Reset();
            savedLanguage = (int)Localization.language;
            savedRotate = SettingsData.Data.rotateControlsWithCamera;
            if (CoopPlugin.TestNoSave) { SaveGuardArmed = true; CoopPlugin.Log("Test: salvataggi disattivati anche sull'host"); }
            if (!IsHost)
            {
                SaveGuardArmed = true;
                if ((int)Localization.language != s.Language) Localization.language = (Localization.Languages)s.Language;
                SettingsData.Data.rotateControlsWithCamera = s.Rotate;
            }
            CoopMenu.CloseAll();
            Lockstep.Bot = CoopPlugin.BotFor(IsHost);
            Lockstep.Start(IsHost ? 0 : 1, s.Delay);
            Determinism.Apply();
            ForceJoin();
            pendingStart = s; // entered from Update as soon as SceneLoader is idle (LoadScene is ignored while loading)
            TryEnterPending();
        }

        static void TryEnterPending()
        {
            if (pendingStart == null || SceneLoader.CurrentlyLoading) return;
            StartInfo s = pendingStart;
            pendingStart = null;
            CoopPlugin.Log("Entro in partita: slot " + s.Slot);
            if (!IsHost) SetSlotData(s.Slot, s.Json);
            EnterGameFor(s.Slot, s.Mugman, s.Empty, s.TestLevel);
        }

        static void SetSlotData(int slot, string json)
        {
            FieldInfo files = typeof(PlayerData).GetField("_saveFiles", BindingFlags.Static | BindingFlags.NonPublic);
            PlayerData.GetDataForSlot(slot); // makes sure the array exists
            PlayerData[] array = (PlayerData[])files.GetValue(null);
            array[slot] = JsonUtility.FromJson<PlayerData>(json);
        }

        // Same steps as SlotSelectScreen.EnterGame, without the slot UI.
        static void EnterGameFor(int slot, bool mugman, bool empty, string testLevel)
        {
            DLCManager.RefreshDLC();
            PlayerManager.SetPlayerCanSwitch(PlayerId.PlayerOne, false);
            PlayerData.CurrentSaveFileIndex = slot;
            PlayerManager.player1IsMugman = mugman;
            PlayerData.GetDataForSlot(slot).isPlayer1Mugman = mugman;
            if (!DLCManager.DLCEnabled())
            {
                PlayerData data = PlayerData.Data;
                for (int i = 0; i < 2; i++)
                {
                    PlayerData.PlayerLoadouts.PlayerLoadout l = data.Loadouts.GetPlayerLoadout((PlayerId)i);
                    if (Array.IndexOf(PlayerData.WeaponsDLC, l.secondaryWeapon) >= 0) l.secondaryWeapon = Weapon.None;
                    if (Array.IndexOf(PlayerData.WeaponsDLC, l.primaryWeapon) >= 0)
                    {
                        l.primaryWeapon = Weapon.level_weapon_peashot;
                        if (l.secondaryWeapon == Weapon.level_weapon_peashot) l.secondaryWeapon = Weapon.None;
                    }
                    if (Array.IndexOf(PlayerData.CharmsDLC, l.charm) >= 0) l.charm = Charm.None;
                }
            }
            Level.ResetPreviousLevelInfo();
            if (testLevel.Length > 0)
            {
                CoopPlugin.Log("Test: carico direttamente il livello " + testLevel);
                PlayerData.Data.CurrentMap = Scenes.scene_map_world_1;
                SceneLoader.LoadLevel((Levels)Enum.Parse(typeof(Levels), testLevel), SceneLoader.Transition.Fade, SceneLoader.Icon.Hourglass, null);
            }
            else if (!empty)
            {
                if (!DLCManager.DLCEnabled() && PlayerData.Data.CurrentMap == Scenes.scene_map_world_DLC)
                {
                    PlayerData.Data.CurrentMap = Scenes.scene_map_world_1;
                    PlayerData.Data.GetMapData(Scenes.scene_map_world_1).sessionStarted = false;
                }
                SceneLoader.LoadScene(PlayerData.Data.CurrentMap, SceneLoader.Transition.Fade, SceneLoader.Transition.Iris);
            }
            else
            {
                PlayerData.Data.CurrentMap = Scenes.scene_map_world_1;
                PlayerData.Data.GetMapData(Scenes.scene_map_world_1).sessionStarted = false;
                Cutscene.Load(Scenes.scene_level_house_elder_kettle, Scenes.scene_cutscene_intro, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
            }
            PlayerData.inGame = true;
            if (StartScreenAudio.Instance != null) UnityEngine.Object.Destroy(StartScreenAudio.Instance.gameObject);
        }

        // P1 and P2 joined, no physical controller state (so no disconnect prompts), no join/leave.
        static void ForceJoin()
        {
            Array slots = (Array)typeof(PlayerManager).GetField("playerSlots", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Type t = slots.GetType().GetElementType();
            FieldInfo join = t.GetField("joinState"), ctrl = t.GetField("controllerState"), canJoin = t.GetField("canJoin");
            for (int i = 0; i < 2; i++)
            {
                object s = slots.GetValue(i);
                join.SetValue(s, Enum.ToObject(join.FieldType, 3));   // Joined
                ctrl.SetValue(s, Enum.ToObject(ctrl.FieldType, 0));   // NoController
                canJoin.SetValue(s, false);
            }
            PlayerManager.Multiplayer = true;
        }

        static void UnjoinP2()
        {
            Array slots = (Array)typeof(PlayerManager).GetField("playerSlots", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Type t = slots.GetType().GetElementType();
            t.GetField("joinState").SetValue(slots.GetValue(1), Enum.ToObject(t.GetField("joinState").FieldType, 0));
            PlayerManager.Multiplayer = false;
        }

        // ---------- barriers ----------

        // Both PCs at the same lockstep frame (after in_cr): same progression or the host's wins.
        public static void LoadingEntryBarrier()
        {
            loadSeq++;
            int seq = loadSeq;
            string json = JsonUtility.ToJson(PlayerData.Data);
            uint h = Fnv.Add(Fnv.Seed, json);
            Send(Msg.DataHash, delegate(BinaryWriter w) { w.Write(seq); w.Write(h); });
            CoopPlugin.Log("Barriera dati seq=" + seq + " hash=" + h.ToString("x8") + " link=" + (link != null && link.Connected));
            uint theirs = 0;
            if (!Wait(delegate { return peerDataHash.TryGetValue(seq, out theirs); }, 30000, "salvataggio del compagno")) return;
            peerDataHash.Remove(seq);
            if (theirs == h) return;
            CoopPlugin.Log("PlayerData diverso al caricamento " + seq + ": l'host corregge l'ospite");
            if (IsHost) Send(Msg.DataFix, delegate(BinaryWriter w) { w.Write(seq); w.Write(json); });
            else
            {
                string fix = null;
                if (!Wait(delegate { return peerDataFix.TryGetValue(seq, out fix); }, 30000, "correzione dall'host")) return;
                peerDataFix.Remove(seq);
                SetSlotData(PlayerData.CurrentSaveFileIndex, fix);
                PlayerData.CurrentSaveFileIndex = PlayerData.CurrentSaveFileIndex; // reload dialogue variables
            }
        }

        // Inside sceneLoaded of the real scene: Awake done, no Start/Update yet. Blocks until the peer is here too.
        public static void ActivationBarrier(string scene)
        {
            int seq = loadSeq;
            Send(Msg.Arrive, delegate(BinaryWriter w) { w.Write(seq); });
            if (!Wait(delegate { return peerArrived >= seq; }, 120000, "caricamento del compagno")) return;
            if (SceneSync.ReseededFor != seq) CoopPlugin.Log("Attenzione: nessun Awake intercettato prima della barriera (seq " + seq + ")");
            UnityEngine.Random.InitState(SeedFor(seq, 2));
            Lockstep.Restart(seq);
            DesyncCheck.Reset();
            CoopPlugin.Log("Barriera superata: " + scene + " seq=" + seq);
        }

        static bool Wait(Func<bool> done, int timeoutMs, string what)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (!done())
            {
                Pump();
                if (State != CoopState.InGame) return false;
                if (sw.ElapsedMilliseconds > timeoutMs) { CoopPlugin.Log("Timeout: " + what); End(L.M("timeout")); return false; }
                if (sw.ElapsedMilliseconds / 5000 != (sw.ElapsedMilliseconds + 2) / 5000)
                    CoopPlugin.Log("Attendo " + what + " da " + sw.ElapsedMilliseconds + " ms, in coda=" + (link == null ? -1 : link.Pending));
                Thread.Sleep(1);
            }
            return true;
        }

        // ---------- in game ----------

        static void Send(byte type, Action<BinaryWriter> body) { if (link != null) link.Send(type, Msg.Build(body)); }

        public static void SendInput(byte[] body) { if (link != null) link.Send(Msg.Input, body); }

        public static void SendHash(int epoch, int frame, string detail)
        {
            Send(Msg.Hash, delegate(BinaryWriter w) { w.Write(epoch); w.Write(frame); w.Write(detail); });
        }

        // A desync never sends the players back to the level start: the host raises a flag (executed on both
        // PCs in the same lockstep frame) and there the guest copies the host's RNG state and super meters.
        public static void OnDesync()
        {
            if (IsHost && !SoftResync.Pending) { SoftResync.Pending = true; Lockstep.LocalFlags |= Lockstep.FlagResync; }
        }

        // Executed on both PCs in the same lockstep frame.
        public static void OnResyncFlag()
        {
            SoftResync.Pending = false;
            if (SceneLoader.CurrentlyLoading) return;
            resyncs++;
            int e = Lockstep.Epoch, f = Lockstep.Frame;
            CoopPlugin.Log("Correzione sincronizzazione " + resyncs + " su " + SceneLoader.SceneName + " frame=" + f);
            if (IsHost) { if (link != null) link.Send(Msg.Snap, SoftResync.Capture(e, f)); }
            else if (Wait(delegate { return SoftResync.Has(e, f); }, 8000, "stato host")) SoftResync.Apply();
            DesyncCheck.IgnoreUpTo(e, f);
        }

        public static void ShowNotice(string text)
        {
            Notice = text;
            NoticeUntil = Time.realtimeSinceStartup + 4f;
        }

        // reason "" = natural end (both PCs return to the title together).
        public static void End(string reason)
        {
            if (State != CoopState.InGame) return;
            CoopPlugin.Log("Fine sessione: " + (reason.Length == 0 ? "ritorno al titolo" : reason));
            bool natural = reason.Length == 0;
            if (!natural && link != null) { string r = reason; Send(Msg.Bye, delegate(BinaryWriter w) { w.Write(r); }); }
            if (link != null) link.Close(natural ? "fine sessione" : reason);
            link = null;
            if (listener != null) listener.Stop();
            listener = null;
            StopUpnp();
            welcomed = false;
            State = CoopState.Idle;
            pendingStart = null;
            Lockstep.Stop();
            Determinism.Restore();
            Determinism.Background(false);
            UnjoinP2();
            if (!IsHost)
            {
                if (savedLanguage >= 0 && (int)Localization.language != savedLanguage) Localization.language = (Localization.Languages)savedLanguage;
                SettingsData.Data.rotateControlsWithCamera = savedRotate;
            }
            if (!natural)
            {
                ShowNotice(reason);
                Error = reason;
                CoopPlugin.ReturnToTitle();
            }
        }

        // ---------- guards ----------

        public static void InstallGuards(Harmony h, List<string> missing)
        {
            MethodInfo skip = typeof(Session).GetMethod("SkipIfGuarded", BindingFlags.Static | BindingFlags.NonPublic);
            Type steam = typeof(OnlineInterfaceSteam);
            Patch(h, missing, AccessTools.Method(steam, "SaveCloudData"), typeof(Session).GetMethod("CloudSavePrefix", BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string n in new string[] { "UnlockAchievement", "SyncAchievementsAndStats", "SetStat", "IncrementStat" })
                foreach (MethodInfo m in steam.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (m.Name == n && m.ReturnType == typeof(void)) Patch(h, missing, m, skip);
            Type local = typeof(LocalAchievementsManager);
            Patch(h, missing, AccessTools.Method(local, "UnlockAchievement"), skip);
            Patch(h, missing, AccessTools.Method(local, "IncrementStat"), skip);
            Patch(h, missing, AccessTools.Method(local, "saveToCloud"), skip);
            Patch(h, missing, AccessTools.Method(typeof(SettingsData), "Save"), skip);
            Patch(h, missing, AccessTools.Method(typeof(SettingsData), "SaveToCloud"), skip);
            foreach (string store in new string[] { "Rewired.Data.UserDataStore_Cuphead", "Rewired.Data.UserDataStore_PlayerPrefs" })
            {
                Type t = typeof(PlayerData).Assembly.GetType(store, false);
                if (t == null) { missing.Add(store); continue; }
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name.StartsWith("Save") && m.ReturnType == typeof(void) && m.GetMethodBody() != null) Patch(h, missing, m, skip);
            }
            Patch(h, missing, AccessTools.Method(typeof(PlayerData), "Init"), typeof(Session).GetMethod("PlayerDataInitPrefix", BindingFlags.Static | BindingFlags.NonPublic));
            Patch(h, missing, AccessTools.Method(typeof(PlayerManager), "PlayerLeave"), typeof(Session).GetMethod("PlayerLeavePrefix", BindingFlags.Static | BindingFlags.NonPublic));
            Patch(h, missing, AccessTools.Method(typeof(SlotSelectScreen), "EnterGame"), typeof(Session).GetMethod("EnterGamePrefix", BindingFlags.Static | BindingFlags.NonPublic));
        }

        static void Patch(Harmony h, List<string> missing, MethodInfo target, MethodInfo prefix)
        {
            if (target == null) { missing.Add("guard " + prefix.Name); return; }
            try { h.Patch(target, new HarmonyMethod(prefix), null, null, null, null); }
            catch (Exception ex) { missing.Add(target.DeclaringType.Name + "." + target.Name + " (" + ex.Message + ")"); }
        }

        static bool SkipIfGuarded() { return !SaveGuardArmed; }

        // Guest: the game believes it saved (same flow as the host), nothing is written.
        static bool CloudSavePrefix(SaveCloudDataHandler __1)
        {
            if (!SaveGuardArmed) return true;
            if (__1 != null) __1(true);
            return false;
        }

        // Back at the title after a session the guest reloads its own saves from disk: guard off.
        static void PlayerDataInitPrefix()
        {
            if (SaveGuardArmed && State != CoopState.InGame)
            {
                SaveGuardArmed = false;
                CoopPlugin.Log("Ospite: ricarico i propri salvataggi dal disco, protezione scrittura disattivata");
            }
        }

        public static void TestWait() { hostWaitSlot = 0; PlayerData.inGame = true; }

        public static void CancelHostWait()
        {
            CoopPlugin.Log("Attesa annullata dall'host");
            Cancel();
            PlayerData.inGame = false;
            SceneLoader.LoadScene(Scenes.scene_slot_select, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
        }

        static bool PlayerLeavePrefix() { return !Lockstep.Active; }

        static bool EnterGamePrefix(SlotSelectScreen __instance)
        {
            if (State == CoopState.InGame) return false;
            if (IsHost && State == CoopState.Hosting)
            {
                // Slot chosen before player 2 joined: wait on this screen, start as soon as the guest is accepted.
                hostWaitSlot = (int)typeof(SlotSelectScreen).GetField("_slotSelection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(__instance);
                Array waitSlots = (Array)typeof(SlotSelectScreen).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(__instance);
                hostWaitMugman = ((SlotSelectScreenSlot)waitSlots.GetValue(hostWaitSlot)).isPlayer1Mugman;
                CoopPlugin.Log("Slot " + hostWaitSlot + " scelto: attendo il giocatore 2");
                return false;
            }
            if (!IsHost || !PeerReady)
            {
                if (link != null) { CoopPlugin.Log("Partita locale avviata: chiudo la lobby"); Cancel(); }
                return true;
            }
            int slot = (int)typeof(SlotSelectScreen).GetField("_slotSelection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(__instance);
            Array slots = (Array)typeof(SlotSelectScreen).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(__instance);
            bool mugman = ((SlotSelectScreenSlot)slots.GetValue(slot)).isPlayer1Mugman;
            HostStart(slot, mugman);
            return false;
        }
    }
}
