using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CupheadLanCoop
{
    [BepInPlugin("it.cupheadlan.coop", "Cuphead LAN Co-op (lockstep)", Version)]
    public sealed class CoopPlugin : BaseUnityPlugin
    {
        public const string Version = "0.2.1";
        public static int Port = 28147, DiscoveryPort = 28148, InputDelay;
        public static string PlayerName = "Giocatore";
        public static bool TestNoSave, DumpUi;
        static ConfigEntry<string> upnpEntry;
        static ConfigFile config;

        // ask (first time), yes, no.
        public static string UpnpChoice
        {
            get
            {
                string v = upnpEntry == null ? "ask" : (upnpEntry.Value ?? "").Trim().ToLowerInvariant();
                if (v == "yes" || v == "true" || v == "on") return "yes";
                if (v == "no" || v == "false" || v == "off") return "no";
                return "ask";
            }
        }

        public static bool UseUpnp { get { return !autoPair && UpnpChoice != "no"; } }

        public static void SetUpnp(bool on)
        {
            if (upnpEntry == null) return;
            upnpEntry.Value = on ? "yes" : "no";
            try { config.Save(); } catch (Exception ex) { Log("Config non salvata: " + ex.Message); }
            Log("UPnP: scelta " + upnpEntry.Value);
        }
        public static string FakePublicIp; // screenshots only
        public static string TestLevel = "";
        static string bot = "off";
        static bool autoPair;
        static string testGameLanguage = "";
        static int autoSlot;
        static StreamWriter log;
        static readonly object logGate = new object();
        static bool returnToTitle;
        static bool autoTried;
        static float autoRetryAt;
        static GUIStyle noticeStyle;

        void Awake()
        {
            Config.SaveOnConfigSet = false; // two instances may start together: never rewrite while binding
            Port = Config.Bind("Network", "TcpPort", 28147, "TCP port of the game (same on both PCs).").Value;
            DiscoveryPort = Config.Bind("Network", "DiscoveryPort", 28148, "UDP port used to find games on the local network / VPN.").Value;
            config = Config;
            upnpEntry = Config.Bind("Network", "UPnP", "ask", "Host: open the router port automatically so friends can join over the internet. ask = ask the first time | yes | no.");
            InputDelay = Config.Bind("Network", "InputDelay", 0, "Input delay in frames. 0 = automatic from ping (2..8).").Value;
            string name = Config.Bind("General", "PlayerName", "", "Name shown to your partner. Empty = computer name.").Value;
            PlayerName = string.IsNullOrEmpty(name) ? Environment.MachineName : name;
            L.Forced = Config.Bind("General", "Language", "auto", "Mod text language: auto (= game language) | en | fr | it | de | es | es-419 | ko | ru | pl | pt-br | ja | zh").Value.Trim();
            bot = Config.Bind("Test", "BotInput", "off", "off | guest | host | both: deterministic automatic input (testing only).").Value;
            autoPair = Config.Bind("Test", "AutoPair", false, "Two instances on the same PC: the first hosts, the second joins 127.0.0.1 (testing only).").Value;
            showMenu = Config.Bind("Test", "ShowMenuPages", false, "Testing only: skip the title and show every co-op menu page in turn.").Value;
            testGameLanguage = Config.Bind("Test", "GameLanguage", "", "Testing only: switch the game language at the title for this run (not saved), e.g. English.").Value.Trim();
            autoSlot = Config.Bind("Test", "AutoSlot", 0, "Save slot used by AutoPair (0..2).").Value;
            TestLevel = Config.Bind("Test", "AutoLevel", "", "Testing only: the host starts this level directly (e.g. Veggies) instead of the map.").Value.Trim();
            TestNoSave = Config.Bind("Test", "NoSave", false, "Testing only: the host does not write saves/settings either.").Value;
            RandomTrace.Enabled = Config.Bind("Diagnostics", "TraceRandom", false, "Desync check every frame plus a trace of Random calls (slow at startup).").Value;
            DumpUi = showMenu;
            if (showMenu)
            {
                NetAddresses.Fake = new List<NetAddress>();
                NetAddresses.Fake.Add(new NetAddress(NetKind.Lan, System.Net.IPAddress.Parse("192.168.1.20"), true));
                NetAddresses.Fake.Add(new NetAddress(NetKind.Radmin, System.Net.IPAddress.Parse("26.12.34.56"), false));
                FakePublicIp = "203.0.113.7";
                PlayerName = "PLAYER";
            }
            if (!File.Exists(Config.ConfigFilePath)) Config.Save();

            OpenLog();
            NetLink.Log = Log;
            Log("Cuphead LAN Co-op " + Version + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
            Harmony h = new Harmony("it.cupheadlan.coop");
            List<string> missing = new List<string>();
            try
            {
                InputHooks.Install(h, missing);
                Determinism.InstallTranspilers(h, missing);
                SceneSync.Install(h, missing);
                Session.InstallGuards(h, missing);
                CoopMenu.Install(h, missing);
                RngIsolation.Install(h, missing);
                if (RandomTrace.Enabled) RandomTrace.Install(h, missing);
            }
            catch (Exception ex) { missing.Add("installazione: " + ex); }
            Log(missing.Count == 0 ? "Patch installate." : "Patch mancanti/opzionali: " + string.Join(" | ", missing.ToArray()));
            if (autoPair) Determinism.Background(true); // test with two windows: the unfocused one must not pause
            StartCoroutine(EndOfFrameLoop());
        }

        void OpenLog()
        {
            try
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "coop-logs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "coop-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log");
                log = new StreamWriter(file, false);
                log.AutoFlush = true;
            }
            catch (Exception ex) { Logger.LogWarning("Log coop non disponibile: " + ex.Message); }
        }

        public static void Log(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + text;
            lock (logGate)
            {
                if (log != null) try { log.WriteLine(line); } catch { }
            }
            UnityEngine.Debug.Log("[CoopLAN] " + text);
        }

        public static bool BotFor(bool host)
        {
            return bot == "both" || (host ? bot == "host" : bot == "guest");
        }

        public static void ReturnToTitle() { returnToTitle = true; }

        void Update()
        {
            try { Session.Update(); CoopMenu.Tick(); }
            catch (Exception ex) { Log("Errore Update: " + ex); }
            if (returnToTitle && !SceneLoader.CurrentlyLoading && !Lockstep.Active)
            {
                returnToTitle = false;
                PlayerManager.ResetPlayers();
                SceneLoader.LoadScene(Scenes.scene_title, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade);
            }
        }

        void FixedUpdate()
        {
            if (Lockstep.Active) Lockstep.FixedStepsThisFrame++;
        }

        IEnumerator EndOfFrameLoop()
        {
            WaitForEndOfFrame end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                try { Lockstep.EndOfFrame(); }
                catch (Exception ex)
                {
                    Log("Errore lockstep: " + ex);
                    Session.End(L.Join(L.M("internal"), ex.Message));
                }
            }
        }

        void OnGUI()
        {
            string text = null;
            if (Session.Notice.Length > 0 && Time.realtimeSinceStartup < Session.NoticeUntil) text = L.Show(Session.Notice);
            else if (Lockstep.Active && Lockstep.Running && Lockstep.LastStallMs > 250) text = L.Tr("slow_net");
            if (text == null) return;
            if (noticeStyle == null)
            {
                noticeStyle = new GUIStyle(GUI.skin.label);
                noticeStyle.fontSize = 22;
                noticeStyle.normal.textColor = Color.white;
            }
            GUI.Label(new Rect(20, 16, Screen.width - 40, 40), text, noticeStyle);
        }

        void OnApplicationQuit()
        {
            if (Session.InGame) Session.End(L.M("partner_quit"));
            else Session.Cancel();
            PortOpener.Shutdown(2000); // close the router port before the process exits
        }

        static void ShowMenuTick()
        {
            StartScreen start = UnityEngine.Object.FindObjectOfType<StartScreen>();
            if (start != null && start.state == StartScreen.State.Title)
            {
                if (testGameLanguage.Length > 0)
                {
                    try
                    {
                        Localization.Languages want = (Localization.Languages)Enum.Parse(typeof(Localization.Languages), testGameLanguage, true);
                        if (Localization.language != want) { Localization.language = want; Log("Test: lingua del gioco " + want); }
                    }
                    catch (Exception ex) { Log("Test: lingua non valida " + testGameLanguage + " " + ex.Message); testGameLanguage = ""; }
                }
                FieldInfo f = typeof(StartScreen).GetField("shouldLoadSlotSelect", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null && !(bool)f.GetValue(start)) f.SetValue(start, true);
                return;
            }
            SlotSelectScreen screen = UnityEngine.Object.FindObjectOfType<SlotSelectScreen>();
            if (screen == null) return;
            if (menuSeenAt < 0 && typeof(SlotSelectScreen).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screen).ToString() != "MainMenu") return;
            if (menuSeenAt < 0) { menuSeenAt = Time.realtimeSinceStartup; Log("MostraMenuLan: menu principale visibile"); }
            float t = Time.realtimeSinceStartup - menuSeenAt;
            if (menuStep <= 9 && t > 8f + menuStep * 5f) CoopMenu.TestStep(menuStep++);
        }

        // Two instances on one PC (test): first binds the port and hosts, second joins 127.0.0.1.
        static bool showMenu;
        static float menuSeenAt = -1f;
        static int menuStep;

        public static void AutoTestTick()
        {
            if (showMenu) ShowMenuTick();
            if (!autoPair || Session.InGame || Time.frameCount % 15 != 0) return;
            StartScreen start = UnityEngine.Object.FindObjectOfType<StartScreen>();
            if (start != null && start.state == StartScreen.State.Title)
            {
                FieldInfo f = typeof(StartScreen).GetField("shouldLoadSlotSelect", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null && !(bool)f.GetValue(start)) { Log("AutoCoppia: salto la schermata titolo"); f.SetValue(start, true); }
                return;
            }
            SlotSelectScreen screen = UnityEngine.Object.FindObjectOfType<SlotSelectScreen>();
            if (screen == null) return;
            object state = typeof(SlotSelectScreen).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screen);
            if (state.ToString() != "MainMenu") return;
            if (Session.State == CoopState.Idle && (!autoTried || (!Session.IsHost && Time.realtimeSinceStartup > autoRetryAt)))
            {
                autoTried = true;
                autoRetryAt = Time.realtimeSinceStartup + 3f;
                Session.Host();
                if (Session.State != CoopState.Hosting)
                {
                    Log("AutoCoppia: porta occupata, mi unisco a 127.0.0.1");
                    Session.Join("127.0.0.1", Port);
                }
                else Log("AutoCoppia: faccio da host");
            }
            if (Session.IsHost && Session.PeerReady && !SceneLoader.CurrentlyLoading)
            {
                Log("AutoCoppia: avvio slot " + autoSlot);
                Session.HostStart(autoSlot, false);
            }
        }
    }
}
