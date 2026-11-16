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
        const string IpChars = "0123456789.:";
        const int IpMax = 21; // 255.255.255.255:65535

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
            confirmChildField = t.GetField("confirmDeleteChild", Inst);
            popSelColorField = t.GetField("confirmDeleteSelectedColor", Inst); popUnselColorField = t.GetField("confirmDeleteUnselectedColor", Inst);
            HarmonyMethod block = new HarmonyMethod(typeof(CoopMenu).GetMethod("BeforeOtherPage", BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string name in new string[] { "UpdateSlotSelect", "UpdatePlayerSelect", "UpdateConfirmDelete" })
            {
                MethodInfo m = t.GetMethod(name, Inst);
                if (m != null) h.Patch(m, block, null, null, null, null);
            }
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
                if (CoopPlugin.DumpUi)
                {
                    FieldInfo cd = typeof(SlotSelectScreen).GetField("confirmDeleteChild", Inst);
                    if (cd != null) DumpTree(((RectTransform)cd.GetValue(__instance)).transform, 0);
                }
            }
            catch (Exception ex) { CoopPlugin.Log("Menu LAN non creato: " + ex); lanItem = null; }
        }

        // Test only: logs a UI subtree (names, components, texts, sizes) to design native-looking pages.
        static void DumpTree(Transform t, int depth)
        {
            string info = new string(' ', depth * 2) + t.name + " active=" + t.gameObject.activeSelf;
            RectTransform r = t as RectTransform;
            if (r != null) info += " pos=" + r.anchoredPosition + " size=" + r.rect.size + " anchors=" + r.anchorMin + r.anchorMax;
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                info += " [" + c.GetType().Name;
                Text tx = c as Text;
                if (tx != null) info += " '" + tx.text + "' size=" + tx.fontSize + " font=" + (tx.font != null ? tx.font.name : "-");
                Image im = c as Image;
                if (im != null) info += " sprite=" + (im.sprite != null ? im.sprite.name : "-");
                PropertyInfo text = c.GetType().GetProperty("text");
                if (tx == null && text != null && text.PropertyType == typeof(string)) { try { info += " '" + text.GetValue(c, null) + "'"; } catch { } }
                info += "]";
            }
            CoopPlugin.Log("UI " + info);
            for (int i = 0; i < t.childCount; i++) DumpTree(t.GetChild(i), depth + 1);
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
                if (PopupOpen) { UpdatePopup(); return false; }
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
            if (step == 7) { CheckGlyphs(); StartHost(); return; } // the real path: first-time question
            if (step == 8) { Action yes = popActs != null && popActs.Length > 0 ? popActs[0] : null; HidePopup(); if (yes != null) yes(); return; } // as if YES was pressed
            if (step == 9) { Session.Upnp = PortOpener.FakedFailure(); return; } // router refuses: popup over the save screen
        }

        // Test only: logs characters of mod texts that the current game font cannot draw.
        static void CheckGlyphs()
        {
            int current;
            try { current = (int)Localization.language; } catch { current = 0; }
            bool cjk = current == 6 || current == 10 || current == 11;
            for (int lang = 0; lang < 12; lang++)
            {
                bool langCjk = lang == 6 || lang == 10 || lang == 11;
                if (langCjk != cjk || (cjk && lang != current)) continue; // the font changes with CJK languages
                string missing = "";
                foreach (string text in L.Column(lang))
                    foreach (char c in text)
                        if (c != ' ' && !template.font.HasCharacter(c) && missing.IndexOf(c) < 0) missing += c;
                CoopPlugin.Log("GLIFI " + L.Code(lang) + " font=" + template.font.name + " mancanti: [" + missing + "]");
            }
        }

        // Host chose the slot before player 2 arrived: native-styled waiting text over the slot screen.
        static Text waiting;

        public static void Tick()
        {
            if (screen == null || template == null) return;
            CheckUpnpFailure();
            bool onSlots = stateField.GetValue(screen).ToString() != "MainMenu";
            bool show = Session.HostWaiting || (Session.State == CoopState.Hosting && onSlots);
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
            string text = L.Tr("waiting") + (Session.HostWaiting ? "   " + L.Tr("back_cancels") : "") + "\n" + string.Join("     ", AddressLines(true).ToArray());
            if (waiting.text != text) waiting.text = text;
            if (Session.HostWaiting && !PopupOpen && Press(CupheadButton.Cancel)) { Sound(true); Session.CancelHostWait(); }
        }

        // Session started: nothing of ours may stay on screen.
        public static void CloseAll()
        {
            try { HidePopup(); CloseSubmenu(); if (lanItem != null) lanItem.gameObject.SetActive(false); if (waiting != null) waiting.gameObject.SetActive(false); }
            catch (Exception) { }
        }

        // ---------- popup (a clone of the game's own "delete save?" card) ----------

        static FieldInfo confirmChildField, popSelColorField, popUnselColorField;
        static GameObject popup;
        static Text popTitle, popBody;
        static readonly Text[] popOpts = new Text[2];
        static Action[] popActs;
        static Action popBack;
        static int popSel, popCount, popFrame;
        static PortOpener failShownFor;

        public static bool PopupOpen { get { return popup != null && popup.activeSelf; } }

        static bool EnsurePopup()
        {
            if (popup != null) return true;
            if (confirmChildField == null || screen == null || template == null) return false;
            try
            {
                RectTransform src = (RectTransform)confirmChildField.GetValue(screen);
                Transform card0 = src.Find("Card");
                if (card0 == null || card0.Find("ConfirmText") == null || card0.Find("Yes") == null || card0.Find("No") == null) return false;
                GameObject go = UnityEngine.Object.Instantiate<GameObject>(src.gameObject);
                go.name = "CupheadLanCoop Popup";
                go.transform.SetParent(src.parent, false);
                foreach (Component c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    string n = c.GetType().Name;
                    if (n == "LocalizationHelper" || n == "UITextAnimator" || n == "CustomLanguageFont") UnityEngine.Object.DestroyImmediate(c);
                }
                Transform card = go.transform.Find("Card");
                Transform slot = card.Find("SlotText");
                if (slot != null) slot.gameObject.SetActive(false);
                popTitle = card.Find("ConfirmText").GetComponent<Text>();
                popBody = UnityEngine.Object.Instantiate<Text>(popTitle);
                popBody.name = "Body";
                popBody.transform.SetParent(card, false);
                popOpts[0] = card.Find("Yes").GetComponent<Text>();
                popOpts[1] = card.Find("No").GetComponent<Text>();
                Fit(popTitle, -42f, 44f, 30, 16);
                Fit(popBody, -104f, 78f, 21, 11);
                go.SetActive(false);
                popup = go;
                return true;
            }
            catch (Exception ex) { CoopPlugin.Log("Popup non creato: " + ex); popup = null; return false; }
        }

        static void Fit(Text t, float y, float height, int max, int min)
        {
            RectTransform r = t.rectTransform;
            r.anchoredPosition = new Vector2(226.5f, y);
            r.sizeDelta = new Vector2(405f, height);
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.resizeTextForBestFit = true;
            t.resizeTextMaxSize = max;
            t.resizeTextMinSize = min;
        }

        // options/actions: one or two buttons. back = Cancel button (null = same as closing).
        static bool ShowPopup(string title, string body, string[] options, Action[] actions, Action back)
        {
            if (!EnsurePopup()) return false;
            popTitle.text = title;
            popBody.text = body;
            popCount = Math.Min(2, options.Length);
            for (int i = 0; i < 2; i++)
            {
                popOpts[i].gameObject.SetActive(i < popCount);
                if (i >= popCount) continue;
                popOpts[i].text = options[i];
                Fit(popOpts[i], popCount == 1 ? -198f : -177f - 38f * i, 34f, 30, 14);
            }
            popActs = actions; popBack = back; popSel = 0; popFrame = Time.frameCount;
            popup.transform.SetAsLastSibling();
            popup.SetActive(true);
            RenderPopup();
            return true;
        }

        static void HidePopup() { if (popup != null) popup.SetActive(false); }

        static void UpdatePopup()
        {
            if (Time.frameCount != popFrame)
            {
                if (popCount > 1 && (Press(CupheadButton.MenuUp) || Press(CupheadButton.MenuDown))) { Sound(false); popSel = 1 - popSel; }
                else if (Press(CupheadButton.Accept))
                {
                    Sound(true);
                    Action a = popActs != null && popSel < popActs.Length ? popActs[popSel] : null;
                    HidePopup();
                    if (a != null) a();
                    return;
                }
                else if (Press(CupheadButton.Cancel))
                {
                    Sound(true);
                    Action b = popBack;
                    HidePopup();
                    if (b != null) b();
                    return;
                }
            }
            RenderPopup();
        }

        static void RenderPopup()
        {
            Color sel = popSelColorField != null ? (Color)popSelColorField.GetValue(screen) : (Color)selColorField.GetValue(screen);
            Color unsel = popUnselColorField != null ? (Color)popUnselColorField.GetValue(screen) : (Color)unselColorField.GetValue(screen);
            Font font = template.font; // follows the game language (CJK fonts included)
            foreach (Text t in new Text[] { popTitle, popBody, popOpts[0], popOpts[1] }) if (t.font != font) t.font = font;
            for (int i = 0; i < popCount; i++) popOpts[i].color = i == popSel ? sel : unsel;
        }

        // Slot select / player select / confirm delete: frozen while our popup is open.
        static bool BeforeOtherPage(SlotSelectScreen __instance)
        {
            if (__instance != screen || !PopupOpen) return true;
            try { UpdatePopup(); } catch (Exception ex) { CoopPlugin.Log("Errore popup: " + ex); HidePopup(); }
            return false;
        }

        // CREA PARTITA: the first time, ask about the router port.
        static void StartHost()
        {
            if (CoopPlugin.UpnpChoice == "ask" &&
                ShowPopup(L.Tr("ask_title"), L.Tr("ask_body"), new string[] { L.Tr("ask_yes"), L.Tr("ask_no") },
                    new Action[] { delegate { CoopPlugin.SetUpnp(true); DoHost(); }, delegate { CoopPlugin.SetUpnp(false); DoHost(); } }, null))
                return;
            DoHost();
        }

        static void DoHost()
        {
            Session.Host();
            if (Session.State == CoopState.Hosting) ChooseSave();
        }

        // The router could not be opened: say it once, briefly, with the fix.
        static void CheckUpnpFailure()
        {
            PortOpener u = Session.Upnp;
            if (u == null || u == failShownFor || PopupOpen || Session.State != CoopState.Hosting) return;
            string key = u.State == PortState.Cgnat ? "fail_cgnat" : u.State == PortState.DoubleNat ? "fail_double" : u.State == PortState.Failed ? "fail_router" : null;
            if (key == null) return;
            failShownFor = u;
            ShowPopup(L.Tr("fail_title"), L.Tr(key), new string[] { L.Tr("ok") }, new Action[] { null }, null);
        }

        static List<Entry> Build(out Action back)
        {
            List<Entry> e = new List<Entry>();
            back = delegate { Session.Cancel(); Open(Page.Root); };
            switch (page)
            {
                case Page.Root:
                    e.Add(new Entry(L.Tr("host"), StartHost, false));
                    e.Add(new Entry(L.Tr("join"), delegate { Session.Browse(); Open(Page.Join); }, false));
                    if (CoopPlugin.UpnpChoice != "ask" && !CoopPlugin.DumpUi)
                    {
                        bool on = CoopPlugin.UpnpChoice == "yes";
                        e.Add(new Entry(L.Tr("internet") + ": " + L.Tr(on ? "on" : "off"), delegate { CoopPlugin.SetUpnp(!on); }, false));
                    }
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
                        List<string> addresses = AddressLines(false);
                        if (addresses.Count > 0) e.Add(new Entry(L.Tr("share_ip"), null, true));
                        foreach (string a in addresses) AddWrapped(e, a);
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
                    if (Session.State == CoopState.Idle && !string.IsNullOrEmpty(Session.Error)) AddWrapped(e, L.Tr("join_hint"));
                    e.Add(new Entry(Session.State == CoopState.Idle ? L.Tr("back") : L.Tr("leave"), delegate { Session.Cancel(); Session.Browse(); Open(Page.Join); }, false));
                    back = delegate { Session.Cancel(); Session.Browse(); Open(Page.Join); };
                    break;
            }
            return e;
        }

        static void AddError(List<Entry> e)
        {
            string error = L.Show(Session.Error);
            if (!string.IsNullOrEmpty(error)) AddWrapped(e, error.ToUpper());
        }

        // Splits on explicit line breaks, then at about 48 columns (CJK characters count double).
        static void AddWrapped(List<Entry> e, string text)
        {
            foreach (string part in text.Split('\n'))
            {
                string rest = part.Trim();
                while (rest.Length > 0)
                {
                    int n = 0, width = 0;
                    while (n < rest.Length && width + Columns(rest[n]) <= 48) { width += Columns(rest[n]); n++; }
                    if (n < rest.Length) { int sp = rest.LastIndexOf(' ', n); if (sp > n / 2) n = sp; }
                    if (n <= 0) n = 1;
                    e.Add(new Entry(rest.Substring(0, n).Trim(), null, true));
                    rest = rest.Substring(n).Trim();
                }
            }
        }

        static int Columns(char c) { return c >= 0x1100 && !(c >= 0xFF61 && c <= 0xFFDC) ? 2 : 1; }

        // What the host tells player 2: internet address (UPnP), home LAN, then only the VPNs really installed and up.
        // compact = one-line overlay over the save screen (short labels, no explanations).
        static List<string> AddressLines(bool compact)
        {
            List<string> result = new List<string>();
            string problem = null;
            PortOpener upnp = Session.Upnp;
            if (upnp != null)
            {
                string internet = L.Tr("internet") + (compact ? " " : ": ");
                if (upnp.State == PortState.Open)
                {
                    string where = upnp.PublicIp.Length == 0 ? "" : upnp.PublicIp + (upnp.PublicPort != CoopPlugin.Port ? ":" + upnp.PublicPort : "");
                    result.Add(internet + (where.Length == 0 ? L.Tr("port_open") : where + (compact ? "" : " " + L.Tr("port_open"))));
                }
                else if (upnp.State == PortState.Working) result.Add(internet + L.Tr("upnp_working"));
                else if (!compact) problem = internet + L.Tr("net_off");
            }
            foreach (NetAddress a in NetAddresses.List())
            {
                string label;
                switch (a.Kind)
                {
                    case NetKind.Lan: label = compact ? "LAN" : L.Tr("lan"); break;
                    case NetKind.Radmin: label = "RADMIN"; break;
                    case NetKind.Tailscale: label = "TAILSCALE"; break;
                    case NetKind.ZeroTier: label = "ZEROTIER"; break;
                    default: label = "HAMACHI"; break;
                }
                result.Add(label + (compact ? " " : ": ") + a.Address);
            }
            if (problem != null) result.Add(problem); // usable addresses first, the explanation last
            return result;
        }

        static void Connect()
        {
            if (ip.Length == 0) return;
            try { File.WriteAllText(LastIpPath(), ip); } catch { }
            string host = ip;
            int port = CoopPlugin.Port, colon = ip.IndexOf(':');
            if (colon >= 0)
            {
                host = ip.Substring(0, colon);
                if (!int.TryParse(ip.Substring(colon + 1), out port) || port <= 0 || port > 65535) port = CoopPlugin.Port;
            }
            Session.Join(host, port);
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
                else if (Press(CupheadButton.MenuRight)) { if (ip.Length < IpMax) ip += "0"; }
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
                else if (IpChars.IndexOf(c) >= 0 && ip.Length < IpMax) ip += c;
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
