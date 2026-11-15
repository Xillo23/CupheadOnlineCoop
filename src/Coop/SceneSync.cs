using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadLanCoop
{
    // Scene loads in session. Copy of SceneLoader.loop_cr where:
    //  - in_cr runs in lockstep (same frames on both PCs);
    //  - PlayerData hash barrier, then free loading frames (neutral input, no network wait);
    //  - blocking barrier inside sceneLoaded (after Awake, before any Start/Update), lockstep restarts at frame 0;
    //  - every remaining wait is decided by the "stage" carried inside the input packets.
    internal static class SceneSync
    {
        static FieldInfo currentlyLoading, doneLoading;
        static MethodInfo inCr, loadCr, iconInCr, iconOutCr, outCr;

        public static void Install(Harmony h, List<string> missing)
        {
            Type t = typeof(SceneLoader);
            BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
            currentlyLoading = t.GetField("currentlyLoading", BindingFlags.Static | BindingFlags.NonPublic);
            doneLoading = t.GetField("doneLoadingSceneAsync", inst);
            inCr = t.GetMethod("in_cr", inst); loadCr = t.GetMethod("load_cr", inst);
            iconInCr = t.GetMethod("iconFadeIn_cr", inst); iconOutCr = t.GetMethod("iconFadeOut_cr", inst);
            outCr = t.GetMethod("out_cr", inst);
            MethodInfo load = t.GetMethod("load", inst);
            if (currentlyLoading == null || doneLoading == null || inCr == null || loadCr == null || iconInCr == null ||
                iconOutCr == null || outCr == null || load == null)
            {
                missing.Add("SceneLoader (contratto loop_cr cambiato: sessione impossibile)");
                return;
            }
            h.Patch(load, new HarmonyMethod(typeof(SceneSync).GetMethod("LoadPrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null, null);
            MethodInfo awake = typeof(AbstractMonoBehaviour).GetMethod("Awake", inst);
            if (awake == null) missing.Add("AbstractMonoBehaviour.Awake (reseed pre-Awake)");
            else h.Patch(awake, new HarmonyMethod(typeof(SceneSync).GetMethod("AwakePrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null, null);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static bool LoadPrefix(SceneLoader __instance)
        {
            if (!Lockstep.Active) return true;
            string scene = SceneLoader.SceneName;
            if (scene != "scene_slot_select" && scene != "scene_cutscene_dlc_saltbaker_prebattle")
                AudioManager.HandleSnapshot(AudioManager.Snapshots.Loadscreen.ToString(), 5f);
            __instance.StartCoroutine(SessionLoop(__instance));
            return false;
        }

        static IEnumerator Call(SceneLoader l, MethodInfo m) { return (IEnumerator)m.Invoke(l, null); }

        static IEnumerator SessionLoop(SceneLoader l)
        {
            currentlyLoading.SetValue(null, true);
            yield return l.StartCoroutine(Call(l, inCr));
            string scene = SceneLoader.SceneName;
            if (scene == "scene_title" || scene == "scene_slot_select")
                Session.End(""); // back to the title on both PCs at the same lockstep frame: natural end
            else if (Lockstep.Active)
            {
                Session.LoadingEntryBarrier();
                Lockstep.EnterLoading();
                for (int i = 0; i < 3; i++) { Determinism.AlignPhase(); yield return null; }
                Determinism.EndFreeFrames();
                yield return null; // timeScale is 1 again before the scene can activate
            }
            bool iconDone = false;
            l.StartCoroutine(Call(l, loadCr));
            l.StartCoroutine(Then(Call(l, iconInCr), delegate { iconDone = true; }));
            yield return l.StartCoroutine(Gate(1, delegate { return iconDone && (bool)doneLoading.GetValue(l); }));
            if (SceneLoader.SceneName != "scene_slot_select") AudioManager.SnapshotReset(SceneLoader.SceneName, 0.15f);
            AsyncOperation op = Resources.UnloadUnusedAssets();
            yield return l.StartCoroutine(Gate(2, delegate { return op.isDone; }));
            yield return l.StartCoroutine(Call(l, iconOutCr));
            yield return l.StartCoroutine(Call(l, outCr));
            SceneLoader.properties.Reset();
            currentlyLoading.SetValue(null, false);
            CoopPlugin.Log("Caricamento completato: " + SceneLoader.SceneName + " epoch=" + Lockstep.Epoch + " frame=" + Lockstep.Frame);
        }

        static IEnumerator Then(IEnumerator inner, Action done)
        {
            while (inner.MoveNext()) yield return inner.Current;
            done();
        }

        // Proceeds on the first lockstep frame where BOTH PCs reported the local condition.
        static IEnumerator Gate(byte stage, Func<bool> ready)
        {
            while (true)
            {
                bool ok = ready();
                if (!Lockstep.Active)
                {
                    if (ok) yield break;
                }
                else
                {
                    if (ok && Lockstep.LocalStage < stage) Lockstep.LocalStage = stage;
                    if (Lockstep.AgreedStage() >= stage) yield break;
                }
                yield return null;
            }
        }

        public static int ReseededFor = -1;

        // Scene activation runs Awake BEFORE our sceneLoaded barrier: the first Awake of an object of
        // the target scene reseeds Random, so every Awake of the activation sees the same sequence.
        static void AwakePrefix(AbstractMonoBehaviour __instance)
        {
            if (!Lockstep.Active || Lockstep.Running || ReseededFor == Session.LoadSeq) return;
            if (__instance.gameObject.scene.name != SceneLoader.SceneName) return;
            ReseededFor = Session.LoadSeq;
            UnityEngine.Random.InitState(Session.SeedFor(Session.LoadSeq, 1));
            CoopPlugin.Log("Reseed pre-Awake da " + __instance.GetType().Name + " seq=" + Session.LoadSeq);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                CoopPlugin.Log("sceneLoaded " + scene.name + " active=" + Lockstep.Active + " running=" + Lockstep.Running);
                if (!Lockstep.Active || Lockstep.Running || scene.name != SceneLoader.SceneName) return;
                Session.ActivationBarrier(scene.name);
            }
            catch (Exception ex)
            {
                CoopPlugin.Log("Barriera fallita: " + ex);
                Session.End(L.Join(L.M("internal"), "loading barrier"));
            }
        }
    }
}
