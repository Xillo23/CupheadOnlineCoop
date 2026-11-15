using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Rewired;

namespace CupheadLanCoop
{
    // Harmony prefixes on the single input layer of Cuphead (Rewired.Player). Active only in session:
    // every read of Rewired players 0/1 returns the lockstep pair; any other player is neutral.
    internal static class InputHooks
    {
        static bool On { get { return Lockstep.Active && !Lockstep.Bypass; } }

        public static void Install(Harmony h, List<string> missing)
        {
            Type player = typeof(Player);
            string[] kinds = { "GetButton", "GetButtonDown", "GetButtonUp", "GetNegativeButton", "GetNegativeButtonDown", "GetNegativeButtonUp" };
            for (int k = 0; k < kinds.Length; k++)
            {
                Patch(h, missing, player, kinds[k], new Type[] { typeof(int) }, "B" + k);
                Patch(h, missing, player, kinds[k], new Type[] { typeof(string) }, "S" + k);
            }
            Patch(h, missing, player, "GetAxis", new Type[] { typeof(int) }, "AxisI");
            Patch(h, missing, player, "GetAxis", new Type[] { typeof(string) }, "AxisS");
            Patch(h, missing, player, "GetAxisRaw", new Type[] { typeof(int) }, "AxisI");
            Patch(h, missing, player, "GetAxisRaw", new Type[] { typeof(string) }, "AxisS");
            Patch(h, missing, player, "GetButtonTimePressed", new Type[] { typeof(int) }, "TimeI");
            Patch(h, missing, player, "GetButtonTimePressed", new Type[] { typeof(string) }, "TimeS");
            Patch(h, missing, player, "GetAnyButtonDown", Type.EmptyTypes, "PlayerAnyDown");
            Patch(h, missing, player, "GetAnyButton", Type.EmptyTypes, "PlayerAnyHeld");

            // Physical controllers: never visible to the game in session (join/reconnect/any-key screens).
            foreach (Type t in typeof(Controller).Assembly.GetTypes())
            {
                if (!typeof(Controller).IsAssignableFrom(t)) continue;
                MethodInfo down = t.GetMethod("GetAnyButtonDown", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (down != null && down.GetMethodBody() != null) Patch(h, missing, down, "False");
                MethodInfo any = t.GetMethod("GetAnyButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (any != null && any.GetMethodBody() != null) Patch(h, missing, any, "False");
            }

            Type anyInput = typeof(CupheadInput.AnyPlayerInput);
            Patch(h, missing, anyInput, "GetAnyButtonDown", Type.EmptyTypes, "AnyInputDown");
            Patch(h, missing, anyInput, "GetAnyButtonHeld", Type.EmptyTypes, "AnyInputHeld");
            Patch(h, missing, typeof(CupheadInput), "CheckForUnconnectedControllerPress", Type.EmptyTypes, "NullJoystick");
            Patch(h, missing, typeof(CupheadInput), "CheckForControllerPress", new Type[] { typeof(long) }, "NullJoystick");
        }

        public static void Patch(Harmony h, List<string> missing, Type type, string name, Type[] args, string prefix)
        {
            MethodInfo m = AccessTools.Method(type, name, args);
            if (m == null) { missing.Add(type.Name + "." + name); return; }
            Patch(h, missing, m, prefix);
        }

        public static void Patch(Harmony h, List<string> missing, MethodInfo m, string prefix)
        {
            try
            {
                h.Patch(m, new HarmonyMethod(typeof(InputHooks).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)), null, null, null, null);
            }
            catch (Exception ex) { missing.Add(m.DeclaringType.Name + "." + m.Name + " (" + ex.Message + ")"); }
        }

        static bool Btn(Player p, int id, int kind, ref bool r)
        {
            if (!On) return true;
            r = Lockstep.Button(p.id, id, kind);
            return false;
        }

        static bool BtnS(Player p, string name, int kind, ref bool r)
        {
            if (!On) return true;
            r = Lockstep.Button(p.id, Lockstep.IdByName(name), kind);
            return false;
        }

        static bool B0(Player __instance, int __0, ref bool __result) { return Btn(__instance, __0, 0, ref __result); }
        static bool B1(Player __instance, int __0, ref bool __result) { return Btn(__instance, __0, 1, ref __result); }
        static bool B2(Player __instance, int __0, ref bool __result) { return Btn(__instance, __0, 2, ref __result); }
        static bool B3(Player __instance, int __0, ref bool __result) { return Btn(__instance, __0, 3, ref __result); }
        static bool B4(Player __instance, int __0, ref bool __result) { return Btn(__instance, __0, 4, ref __result); }
        static bool B5(Player __instance, int __0, ref bool __result) { return Btn(__instance, __0, 5, ref __result); }
        static bool S0(Player __instance, string __0, ref bool __result) { return BtnS(__instance, __0, 0, ref __result); }
        static bool S1(Player __instance, string __0, ref bool __result) { return BtnS(__instance, __0, 1, ref __result); }
        static bool S2(Player __instance, string __0, ref bool __result) { return BtnS(__instance, __0, 2, ref __result); }
        static bool S3(Player __instance, string __0, ref bool __result) { return BtnS(__instance, __0, 3, ref __result); }
        static bool S4(Player __instance, string __0, ref bool __result) { return BtnS(__instance, __0, 4, ref __result); }
        static bool S5(Player __instance, string __0, ref bool __result) { return BtnS(__instance, __0, 5, ref __result); }

        static bool AxisI(Player __instance, int __0, ref float __result)
        {
            if (!On) return true;
            __result = Lockstep.Axis(__instance.id, __0);
            return false;
        }

        static bool AxisS(Player __instance, string __0, ref float __result)
        {
            if (!On) return true;
            __result = Lockstep.Axis(__instance.id, Lockstep.IdByName(__0));
            return false;
        }

        static bool TimeI(Player __instance, int __0, ref float __result)
        {
            if (!On) return true;
            __result = Lockstep.TimePressed(__instance.id, __0);
            return false;
        }

        static bool TimeS(Player __instance, string __0, ref float __result)
        {
            if (!On) return true;
            __result = Lockstep.TimePressed(__instance.id, Lockstep.IdByName(__0));
            return false;
        }

        static int Slot(Player p) { return p.id == 0 ? 0 : (p.id == 1 ? 1 : -1); }

        static bool PlayerAnyDown(Player __instance, ref bool __result)
        {
            if (!On) return true;
            __result = Lockstep.AnyDown(Slot(__instance));
            return false;
        }

        static bool PlayerAnyHeld(Player __instance, ref bool __result)
        {
            if (!On) return true;
            __result = Lockstep.AnyHeld(Slot(__instance));
            return false;
        }

        static bool False(ref bool __result)
        {
            if (!On) return true;
            __result = false;
            return false;
        }

        static bool NullJoystick(ref Joystick __result)
        {
            if (!On) return true;
            __result = null;
            return false;
        }

        static bool Alive(int slot, bool checkIfDead)
        {
            if (!checkIfDead) return true;
            AbstractPlayerController c = PlayerManager.GetPlayer(slot == 0 ? PlayerId.PlayerOne : PlayerId.PlayerTwo);
            return c != null && !c.IsDead;
        }

        static bool AnyInputDown(CupheadInput.AnyPlayerInput __instance, ref bool __result)
        {
            if (!On) return true;
            __result = !InterruptingPrompt.IsInterrupting() &&
                ((Lockstep.AnyDown(0) && Alive(0, __instance.checkIfDead)) || (Lockstep.AnyDown(1) && Alive(1, __instance.checkIfDead)));
            return false;
        }

        static bool AnyInputHeld(CupheadInput.AnyPlayerInput __instance, ref bool __result)
        {
            if (!On) return true;
            __result = !InterruptingPrompt.IsInterrupting() &&
                ((Lockstep.AnyHeld(0) && Alive(0, __instance.checkIfDead)) || (Lockstep.AnyHeld(1) && Alive(1, __instance.checkIfDead)));
            return false;
        }
    }
}
