using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace CupheadLanCoop
{
    // Fixed time, no vsync speed-up, no pause on focus loss, lockstep clock instead of real time.
    internal static class Determinism
    {
        static bool applied;
        static int savedCapture, savedVSync, savedTarget;
        static float savedFixed, savedTimeScale;

        public static void InstallTranspilers(Harmony h, List<string> missing)
        {
            Transpile(h, missing, AccessTools.Method(typeof(SnowCultLevelSnowball), "newProjectiles"), "SnowCultLevelSnowball.newProjectiles");
            Transpile(h, missing, AccessTools.Method(typeof(DialogueriTween), "Awake"), "DialogueriTween.Awake");
            Transpile(h, missing, AccessTools.Method(typeof(DialogueriTween), "UpdatePercentage"), "DialogueriTween.UpdatePercentage");
        }

        static void Transpile(Harmony h, List<string> missing, MethodInfo m, string label)
        {
            if (m == null) { missing.Add(label); return; }
            try
            {
                h.Patch(m, null, null, new HarmonyMethod(typeof(Determinism).GetMethod("ClockTranspiler", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
            }
            catch (Exception ex) { missing.Add(label + " (" + ex.Message + ")"); }
        }

        static IEnumerable<CodeInstruction> ClockTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo real = AccessTools.PropertyGetter(typeof(Time), "realtimeSinceStartup");
            MethodInfo clock = AccessTools.PropertyGetter(typeof(Lockstep), "Clock");
            foreach (CodeInstruction i in instructions)
            {
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand as MethodInfo == real)
                {
                    i.opcode = OpCodes.Call;
                    i.operand = clock;
                }
                yield return i;
            }
        }

        static bool originalBackground, backgroundSaved;

        // Lobby and game must keep running when the window is not focused (two PCs, alt-tab, two test instances).
        public static void Background(bool on)
        {
            if (!backgroundSaved) { backgroundSaved = true; originalBackground = Application.runInBackground; }
            Application.runInBackground = on || originalBackground;
        }

        public static void Apply()
        {
            if (applied) return;
            applied = true;
            savedCapture = Time.captureFramerate; savedVSync = QualitySettings.vSyncCount; savedTarget = Application.targetFrameRate;
            savedFixed = Time.fixedDeltaTime; savedTimeScale = Time.timeScale;
            CoopPlugin.Log("Determinismo: fixedDeltaTime originale=" + savedFixed.ToString("R") + " vSync=" + savedVSync + " target=" + savedTarget);
            Time.captureFramerate = 60;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            Time.fixedDeltaTime = 1f / 60f;
            Time.timeScale = 1f;
            Background(true);
        }

        public static void Restore()
        {
            if (!applied) return;
            applied = false;
            Time.captureFramerate = savedCapture;
            QualitySettings.vSyncCount = savedVSync;
            Application.targetFrameRate = savedTarget;
            Time.fixedDeltaTime = savedFixed;
            Time.timeScale = savedTimeScale <= 0f ? 1f : savedTimeScale;
        }

        // Free loading frames BEFORE the scene load starts: move the fixed-step phase (time - fixedTime)
        // away from the boundary so every lockstep frame runs exactly one FixedUpdate on both PCs.
        // The caller restores timeScale 1 at least one frame before any scene can activate.
        public static void AlignPhase()
        {
            if (!applied) return;
            float fd = Time.fixedDeltaTime;
            float phase = Time.time - Time.fixedTime;
            bool nearEdge = phase < fd * 0.25f || phase > fd * 0.75f;
            Time.timeScale = nearEdge ? 0.5f : 1f;
        }

        public static void EndFreeFrames()
        {
            if (applied) Time.timeScale = 1f;
        }
    }
}
