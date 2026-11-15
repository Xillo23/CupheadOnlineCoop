using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CupheadLanCoop
{
    // "MULTIGIOCATORE LAN" in the original title menu. Every line is a clone of a native menu
    // Text (same font, material, colours, sounds); while the submenu is open the native lines are hidden.
    internal static class CoopMenu
    {
        enum Page { None, Root, Host, Join, Ip, Guest }

        sealed class Entry
        {
            public string Text;
            public Action Accept;
            public bool Small;
            public Entry(string text, Action accept, bool small) { Text = text; Accept = accept; Small = small; }
        }

        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static FieldInfo itemsField, selectionField, stateField, childField, selColorField, unselColorField, timeField, slotsField;
        static MethodInfo buttonDown, setState;
        static SlotSelectScreen screen;
        static Text template, lanItem;
        static readonly List<Text> lines = new List<Text>();
        static Vector2 firstPos, step;
        static bool layoutGroup, onLan;
        static Page page = Page.None;
        static int selected;
        static string ip = "";
        static bool editIp;
        const string IpChars = "0123456789.";

        public static void Install(Harmony h, List<string> missing)
        {
            Type t = typeof(SlotSelectScreen);
            itemsField = t.GetField("mainMenuItems", Inst); selectionField = t.GetField("_mainMenuSelection", Inst);
            stateField = t.GetField("state", Inst); childField = t.GetField("mainMenuChild", Inst);
            selColorField = t.GetField("mainMenuSelectedColor", Inst); unselColorField = t.GetField("mainMenuUnselectedColor", Inst);
            timeField = t.GetField("timeSinceStart", Inst); slotsField = t.GetField("slots", Inst);
            buttonDown = t.GetMethod("GetButtonDown", Inst); setState = t.GetMethod("SetState", Inst);
            MethodInfo awake = t.GetMethod("Awake", Inst), update = t.GetMethod("UpdateMainMenu", Inst);
            if (itemsField == null || selectionField == null || stateField == null || childField == null || selColorField == null ||
                unselColorField == null || timeField == null || slotsField == null || buttonDown == null || setState == null || awake == null || update == null)
            {
                missing.Add("menu titolo (SlotSelectScreen cambiato): voce LAN non disponibile");
                return;
            }
            h.Patch(awake, null, new HarmonyMethod(typeof(CoopMenu).GetMethod("AfterAwake", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
            h.Patch(update, new HarmonyMethod(typeof(CoopMenu).GetMethod("BeforeMainMenu", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null, null);
            h.Patch(setState, null, new HarmonyMethod(typeof(CoopMenu).GetMethod("AfterState", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
            try { ip = File.ReadAllText(LastIpPath()).Trim(); } catch { ip = ""; }
        }

        static string LastIpPath() { return Path.Combine(BepInEx.Paths.ConfigPath, "it.cupheadlan.coop.ultimo-ip.txt"); }

        static Text[] Native() { return (Text[])itemsField.GetValue(screen); }

        static void AfterAwake(SlotSelectScreen __instance)
        {
            try
            {
                screen = __instance;
                lines.Clear();
                page = Page.None; onLan = false;
                Text[] native = Native();
                template = native[native.Length - 1];
                layoutGroup = template.transform.parent.GetComponent<LayoutGroup>() != null;
                lanItem = Clone(L.Tr("menu"), false);
                lanItem.gameObject.SetActive(true);
                Place(lanItem, 1);
                lanItem.color = (Color)unselColorField.GetValue(__instance);
            }
            catch (Exception ex) { CoopPlugin.Log("Menu LAN non creato: " + ex); lanItem = null; }
        }

        static Text Clone(string text, bool small)
        {
            Text t = UnityEngine.Object.Instantiate<Text>(template);
            t.name = "CupheadLanCoop " + text;
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c is Transform || c is CanvasRenderer || c is Graphic || c is BaseMeshEffect) continue;
                UnityEngine.Object.DestroyImmediate(c);
            }
            t.transform.SetParent(template.transform.parent, false);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.text = text;
            if (small) t.fontSize = Mathf.Max(10, (int)(template.fontSize * 0.7f));
            return t;
        }

        // Positions are read after the native layout ran (not in Awake).
        static void MeasureNative()
        {
            Text[] native = Native();
            firstPos = native[0].rectTransform.anchoredPosition;
            step = native.Length > 1 ? native[1].rectTransform.anchoredPosition - native[0].rectTransform.anchoredPosition : Vector2.zero;
            if (step.sqrMagnitude < 1f) step = new Vector2(0f, -template.rectTransform.rect.height * 1.15f);
        }

        // Directly under START.
        static void Place(Text t, int index)
        {
            t.transform.SetSiblingIndex(Native()[0].transform.GetSiblingIndex() + 1);
            if (!layoutGroup) { MeasureNative(); t.rectTransform.anchoredPosition = firstPos + step * index; }
        }

        static bool Press(CupheadButton b) { return (bool)buttonDown.Invoke(screen, new object[] { b }); }

        static void Sound(bool select) { AudioManager.Play(select ? "level_menu_select" : "level_menu_move"); }

        static void AfterState(SlotSelectScreen __instance)
        {
            if (__instance != screen || lanItem == null) return;
            bool main = stateField.GetValue(__instance).ToString() == "MainMenu";
            if (!main) { CloseSubmenu(); onLan = false; }
            lanItem.gameObject.SetActive(main && page == Page.None);
        }

        static bool BeforeMainMenu(SlotSelectScreen __instance)
        {
            if (__instance != screen || lanItem == null) return true;
            try
            {
                string label = L.Tr("menu");
                if (lanItem.text != label) lanItem.text = label;
                if (lanItem.font != template.font) lanItem.font = template.font;
                if ((float)timeField.GetValue(__instance) < 0.75f) return true;
                if (page != Page.None) { UpdateSubmenu(); return false; }
                Text[] native = Native();
                Color sel = (Color)selColorField.GetValue(__instance), unsel = (Color)unselColorField.GetValue(__instance);
                int current = (int)selectionField.GetValue(__instance);
                if (!onLan)
                {
                    bool down = current == 0 && Press(CupheadButton.MenuDown);
                    bool up = !down && current == 1 && Press(CupheadButton.MenuUp);
                    if (!down && !up) { lanItem.color = unsel; return true; }
                    Sound(false);
                    onLan = true;
                }
                else if (Press(CupheadButton.MenuDown)) { Sound(false); onLan = false; selectionField.SetValue(__instance, Math.Min(1, native.Length - 1)); return false; }
                else if (Press(CupheadButton.MenuUp)) { Sound(false); onLan = false; selectionField.SetValue(__instance, 0); return false; }
                else if (Press(CupheadButton.Accept)) { Sound(true); Open(Page.Root); return false; }
                foreach (Text t in native) t.color = unsel;
                lanItem.color = sel;
                return false;
            }
            catch (Exception ex)
            {
                CoopPlugin.Log("Errore menu LAN: " + ex);
                CloseSubmenu();
                return true;
            }
        }

        static void Open(Page p)
        {
            if (page == Page.None)
            {
                MeasureNative();
                foreach (Text t in Native()) t.gameObject.SetActive(false);
                lanItem.gameObject.SetActive(false);
            }
            page = p;
            selected = 0;
            editIp = false;
        }

        static void CloseSubmenu()
        {
            if (page == Page.None) return;
            page = Page.None;
            foreach (Text t in lines) if (t != null) t.gameObject.SetActive(false);
            if (screen == null) return;
            foreach (Text t in Native()) t.gameObject.SetActive(true);
            if (lanItem != null) lanItem.gameObject.SetActive(true);
        }

        // Test only (config Test.MostraMenuLan): walks the pages for screenshots.
        public static void TestStep(int step)
        {
            if (screen == null || lanItem == null) return;
            CoopPlugin.Log("MostraMenuLan: passo " + step);
            if (step == 0) { onLan = true; return; }
            if (step == 1) { Open(Page.Root); return; }
            if (step == 2) { Session.Host(); if (Session.State == CoopState.Hosting) ChooseSave(); return; }
            if (step == 3) { Session.TestWait(); return; }
            if (step == 4) { Session.CancelHostWait(); return; }
            if (step == 5) { Session.Browse(); Open(Page.Join); return; }
            if (step == 6) { Open(Page.Ip); return; }
        }

        // Host chose the slot before player 2 arrived: native-styled waiting text over the slot screen.
        static Text waiting;

        public static void Tick()
        {
            if (screen == null || template == null) return;
            bool show = Session.HostWaiting;
            if (!show) { if (waiting != null && waiting.gameObject.activeSelf) waiting.gameObject.SetActive(false); return; }
            if (waiting == null)
            {
                waiting = Clone("", true);
                waiting.transform.SetParent(template.canvas.transform, false);
                RectTransform r = waiting.rectTransform;
                r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 1f);
                r.anchoredPosition = new Vector2(0f, -12f);
                r.sizeDelta = new Vector2(1800f, 200f);
                waiting.alignment = TextAnchor.UpperCenter;
                waiting.fontSize = Mathf.Max(10, (int)(template.fontSize * 0.55f));
                waiting.lineSpacing = 0.9f;
            }
            waiting.gameObject.SetActive(true);
            waiting.transform.SetAsLastSibling();
            string text = L.Tr("waiting") + "   " + L.Tr("back_cancels") + "\n";
            List<LocalAddress> addresses = LocalAddress.List();
            for (int i = 0; i < addresses.Count && i < 3; i++) text += (i > 0 ? "     " : "") + "IP " + addresses[i].Label;
            if (waiting.text != text) waiting.text = text;
            if (Press(CupheadButton.Cancel)) { Sound(true); Session.CancelHostWait(); }
        }

        // Session started: nothing of ours may stay on screen.
        public static void CloseAll()
        {
            try { CloseSubmenu(); if (lanItem != null) lanItem.gameObject.SetActive(false); if (waiting != null) waiting.gameObject.SetActive(false); }
            catch (Exception) { }
        }

        static List<Entry> Build(out Action back)
        {
            List<Entry> e = new List<Entry>();
            back = delegate { Session.Cancel(); Open(Page.Root); };
            switch (page)
            {
                case Page.Root:
                    e.Add(new Entry(L.Tr("host"), delegate { Session.Host(); if (Session.State == CoopState.Hosting) ChooseSave(); }, false));
                    e.Add(new Entry(L.Tr("join"), delegate { Session.Browse(); Open(Page.Join); }, false));
                    e.Add(new Entry(L.Tr("back"), delegate { Session.Cancel(); CloseSubmenu(); }, false));
                    AddError(e);
                    back = delegate { Session.Cancel(); CloseSubmenu(); };
                    break;
                case Page.Host:
                    if (Session.State == CoopState.Connected)
                    {
                        e.Add(new Entry(L.Tr("player2", Session.PeerName.ToUpper()), null, true));
                        e.Add(new Entry("PING " + (Session.PingMs < 0 ? "..." : Session.PingMs + " MS"), null, true));
                        e.Add(new Entry(L.Tr("choose_save"), ChooseSave, false));
                    }
                    else if (Session.State == CoopState.Hosting)
                    {
                        e.Add(new Entry(L.Tr("waiting"), null, true));
                        List<LocalAddress> addresses = LocalAddress.List();
                        for (int i = 0; i < addresses.Count && i < 3; i++) e.Add(new Entry("IP " + addresses[i].Label, null, true));
                    }
                    AddError(e);
                    e.Add(new Entry(L.Tr("cancel"), back, false));
                    break;
                case Page.Join:
                    if (Session.State == CoopState.Joining || Session.State == CoopState.Connected) { page = Page.Guest; return Build(out back); }
                    List<FoundGame> games = Session.Discovery != null ? Session.Discovery.Games() : new List<FoundGame>();
                    if (games.Count == 0) e.Add(new Entry(L.Tr("searching"), null, true));
                    for (int i = 0; i < games.Count && i < 5; i++)
                    {
                        FoundGame g = games[i];
                        string label = g.Name.ToUpper() + " - " + g.Address + " - " + g.PingMs + " MS" + (g.Version != CoopPlugin.Version ? " " + L.Tr("version", g.Version) : "");
                        e.Add(new Entry(label, delegate { Session.Join(g.Address, g.Port); }, false));
                    }
                    AddError(e);
                    e.Add(new Entry(L.Tr("enter_ip"), delegate { Open(Page.Ip); }, false));
                    e.Add(new Entry(L.Tr("back"), back, false));
                    break;
                case Page.Ip:
                    e.Add(new Entry("IP: " + ip + (editIp || (Time.realtimeSinceStartup % 1f) < 0.5f ? "_" : " "), delegate { editIp = !editIp; }, false));
                    e.Add(new Entry(L.Tr("connect"), Connect, false));
                    AddError(e);
                    e.Add(new Entry(L.Tr("back"), delegate { Session.Browse(); Open(Page.Join); }, false));
                    back = delegate { Session.Browse(); Open(Page.Join); };
                    break;
                case Page.Guest:
                    if (Session.State == CoopState.Joining) e.Add(new Entry(L.Tr("connecting"), null, true));
                    else if (Session.State == CoopState.Connected)
                    {
                        e.Add(new Entry(L.Tr("connected_to", Session.PeerName.ToUpper()), null, true));
                        e.Add(new Entry("PING " + (Session.PingMs < 0 ? "..." : Session.PingMs + " MS"), null, true));
                        e.Add(new Entry(L.Tr("host_choosing"), null, true));
                    }
                    AddError(e);
                    e.Add(new Entry(Session.State == CoopState.Idle ? L.Tr("back") : L.Tr("leave"), delegate { Session.Cancel(); Session.Browse(); Open(Page.Join); }, false));
                    back = delegate { Session.Cancel(); Session.Browse(); Open(Page.Join); };
                    break;
            }
            return e;
        }

        static void AddError(List<Entry> e)
        {
            string error = L.Show(Session.Error);
            if (string.IsNullOrEmpty(error)) return;
            error = error.ToUpper();
            while (error.Length > 0)
            {
                int n = Math.Min(48, error.Length);
                if (n < error.Length) { int sp = error.LastIndexOf(' ', n); if (sp > 20) n = sp; }
                e.Add(new Entry(error.Substring(0, n).Trim(), null, true));
                error = error.Substring(n);
            }
        }

        static void Connect()
        {
            if (ip.Length == 0) return;
            try { File.WriteAllText(LastIpPath(), ip); } catch { }
            Session.Join(ip, CoopPlugin.Port);
            Open(Page.Guest);
        }

        static void ChooseSave()
        {
            CloseSubmenu();
            onLan = false;
            selectionField.SetValue(screen, 0);
            setState.Invoke(screen, new object[] { Enum.ToObject(stateField.FieldType, (int)SlotSelectScreen.State.SlotSelect) });
            Array slots = (Array)slotsField.GetValue(screen);
            for (int j = 0; j < slots.Length; j++) ((SlotSelectScreenSlot)slots.GetValue(j)).Init(j);
        }

        static void UpdateSubmenu()
        {
            Action back;
            List<Entry> entries = Build(out back);
            if (page == Page.None) return;
            List<int> selectable = new List<int>();
            for (int i = 0; i < entries.Count; i++) if (entries[i].Accept != null) selectable.Add(i);
            if (selected >= selectable.Count) selected = selectable.Count - 1;
            if (selected < 0) selected = 0;
            int focus = selectable.Count > 0 ? selectable[selected] : -1;

            if (page == Page.Ip) TypeIp(focus == 0);
            if (editIp && page == Page.Ip && focus == 0)
            {
                if (Press(CupheadButton.MenuUp)) Cycle(1);
                else if (Press(CupheadButton.MenuDown)) Cycle(-1);
                else if (Press(CupheadButton.MenuRight)) { if (ip.Length < 15) ip += "0"; }
                else if (Press(CupheadButton.MenuLeft)) { if (ip.Length > 0) ip = ip.Substring(0, ip.Length - 1); }
                else if (Press(CupheadButton.Accept) || Press(CupheadButton.Cancel)) { Sound(true); editIp = false; }
            }
            else if (Press(CupheadButton.MenuDown) && selectable.Count > 0) { Sound(false); selected = (selected + 1) % selectable.Count; }
            else if (Press(CupheadButton.MenuUp) && selectable.Count > 0) { Sound(false); selected = (selected + selectable.Count - 1) % selectable.Count; }
            else if (Press(CupheadButton.Accept) && focus >= 0) { Sound(true); entries[focus].Accept(); }
            else if (Press(CupheadButton.Cancel)) { Sound(true); back(); }
            if (page == Page.None) return;
            Render(entries, focus);
        }

        static void TypeIp(bool focused)
        {
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (ip.Length > 0) ip = ip.Substring(0, ip.Length - 1); }
                else if (c == '\n' || c == '\r') { Connect(); return; }
                else if (IpChars.IndexOf(c) >= 0 && ip.Length < 15) ip += c;
            }
        }

        static void Cycle(int dir)
        {
            if (ip.Length == 0) ip = "0";
            int i = IpChars.IndexOf(ip[ip.Length - 1]);
            i = ((i < 0 ? 0 : i) + dir + IpChars.Length) % IpChars.Length;
            ip = ip.Substring(0, ip.Length - 1) + IpChars[i];
        }

        static void Render(List<Entry> entries, int focus)
        {
            Color sel = (Color)selColorField.GetValue(screen), unsel = (Color)unselColorField.GetValue(screen);
            while (lines.Count < entries.Count) lines.Add(Clone("", false));
            for (int i = 0; i < lines.Count; i++)
            {
                Text t = lines[i];
                if (i >= entries.Count) { t.gameObject.SetActive(false); continue; }
                Entry e = entries[i];
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                if (t.text != e.Text) t.text = e.Text;
                if (t.font != template.font) t.font = template.font;
                t.fontSize = e.Small ? Mathf.Max(10, (int)(template.fontSize * 0.7f)) : template.fontSize;
                t.color = i == focus ? sel : (e.Accept == null ? Color.Lerp(unsel, Color.gray, 0.35f) : unsel);
                if (!layoutGroup) t.rectTransform.anchoredPosition = firstPos + step * i;
                else
                {
                    int want = t.transform.parent.childCount - entries.Count + i;
                    if (t.transform.GetSiblingIndex() != want) t.transform.SetSiblingIndex(want);
                }
            }
        }
    }
}
