using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Rewired;
using UnityEngine;

namespace CupheadLanCoop
{
    // Deterministic input lockstep. Slot 0 = P1 = host, slot 1 = P2 = guest on BOTH PCs.
    // Frame F is executed with Cur = inputs for F; the local input sampled at the end of
    // frame F is scheduled for frame F+Delay and sent to the peer.
    internal static class Lockstep
    {
        public const byte FlagResync = 1;
        public static bool Active;      // session owns the input of Rewired players 0/1
        public static bool Running;     // exchanging inputs (false = free loading frames, neutral input)
        public static bool Bypass;      // hooks return the original Rewired value
        public static int Epoch, Frame, Delay = 2, LocalSlot;
        public static long ClockFrames;
        public static byte LocalStage, LocalFlags;
        public static bool Bot;
        public static int LastStallMs;
        public static Pad[] Cur, Prev, FixCur, FixPrev;
        public static int FixedStepsThisFrame;

        static int[] actionIds = new int[0];
        static int[] indexById = new int[0], axisById = new int[0];
        static int axisCount;
        static string actionSignature = "";
        static float fixedStamp = float.NaN;
        static int[,] held = new int[2, 64];
        static readonly Dictionary<long, Pad> remote = new Dictionary<long, Pad>(), local = new Dictionary<long, Pad>();
        static readonly Dictionary<string, int> idByName = new Dictionary<string, int>();

        public static float Clock { get { return Active ? ClockFrames / 60f : Time.realtimeSinceStartup; } }
        public static string ActionSignature { get { if (actionSignature.Length == 0) BuildActions(); return actionSignature; } }

        public static void BuildActions()
        {
            List<InputAction> actions = new List<InputAction>();
            foreach (InputAction a in ReInput.mapping.Actions) actions.Add(a);
            actions.Sort(delegate(InputAction a, InputAction b) { return a.id.CompareTo(b.id); });
            if (actions.Count > 64) actions.RemoveRange(64, actions.Count - 64);
            int maxId = 0;
            foreach (InputAction a in actions) maxId = Math.Max(maxId, a.id);
            actionIds = new int[actions.Count];
            indexById = new int[maxId + 1];
            axisById = new int[maxId + 1];
            for (int i = 0; i <= maxId; i++) { indexById[i] = -1; axisById[i] = -1; }
            axisCount = 0;
            System.Text.StringBuilder sig = new System.Text.StringBuilder();
            for (int i = 0; i < actions.Count; i++)
            {
                actionIds[i] = actions[i].id;
                indexById[actions[i].id] = i;
                if (actions[i].type == InputActionType.Axis) axisById[actions[i].id] = axisCount++;
                sig.Append(actions[i].id).Append(':').Append(actions[i].name).Append(':').Append((int)actions[i].type).Append(';');
            }
            actionSignature = sig.ToString();
        }

        public static void Start(int localSlot, int delay)
        {
            BuildActions();
            LocalSlot = localSlot;
            Delay = Math.Max(1, delay);
            Epoch = 0;
            ClockFrames = 0;
            Active = true;
            Running = false;
            ResetBuffers();
        }

        public static void Stop()
        {
            Active = false;
            Running = false;
            lock (remote) { remote.Clear(); }
            ResetBuffers();
        }

        // Called on both PCs at the same logical point (activation barrier).
        public static void Restart(int epoch)
        {
            Epoch = epoch;
            ResetBuffers();
            Running = true;
        }

        public static void EnterLoading()
        {
            Running = false;
            LocalStage = 0;
            LocalFlags = 0;
            Cur = Neutral2(); Prev = Neutral2();
        }

        static void ResetBuffers()
        {
            Frame = 0;
            lock (remote)
            {
                // Keep inputs of the current epoch: the peer may leave the barrier first and send them early.
                List<long> stale = new List<long>();
                foreach (long k in remote.Keys) if ((int)(k >> 32) < Epoch) stale.Add(k);
                foreach (long k in stale) remote.Remove(k);
            }
            local.Clear();
            Cur = Neutral2(); Prev = Neutral2(); FixCur = Neutral2(); FixPrev = Neutral2();
            fixedStamp = float.NaN;
            held = new int[2, 64];
        }

        static Pad[] Neutral2() { return new Pad[] { Pad.Neutral(axisCount), Pad.Neutral(axisCount) }; }

        static long Key(int epoch, int frame) { return ((long)epoch << 32) | (uint)frame; }

        public static void OnRemoteInput(byte[] body)
        {
            int epoch, frame;
            Pad p = PadCodec.Decode(body, out epoch, out frame);
            if (epoch < Epoch) return; // stale packet from before a barrier
            lock (remote) { remote[Key(epoch, frame)] = p; }
        }

        // End of frame F (after rendering): schedule local input for F+Delay, then wait for F+1.
        public static void EndOfFrame()
        {
            if (!Active) return;
            int steps = FixedStepsThisFrame;
            FixedStepsThisFrame = 0;
            if (!Running)
            {
                Prev = Cur; Cur = Neutral2();
                return;
            }
            Pad mine = Sample();
            mine.Stage = LocalStage;
            mine.Flags = LocalFlags;
            LocalFlags = 0;
            int target = Frame + Delay;
            local[Key(Epoch, target)] = mine;
            Session.SendInput(PadCodec.Encode(Epoch, target, mine));

            Frame++;
            Pad ours = Frame < Delay ? Pad.Neutral(axisCount) : Take(local, Epoch, Frame);
            Pad theirs = Frame < Delay ? Pad.Neutral(axisCount) : WaitRemote(Frame);
            if (!Active || !Running) return; // session ended or barrier while waiting
            Prev = Cur;
            Cur = new Pad[2];
            Cur[LocalSlot] = ours ?? Pad.Neutral(axisCount);
            Cur[1 - LocalSlot] = theirs;
            for (int s = 0; s < 2; s++)
                for (int i = 0; i < actionIds.Length; i++)
                    held[s, i] = ((Cur[s].Pos >> i) & 1) != 0 ? held[s, i] + 1 : 0;
            ClockFrames++;
            DesyncCheck.OnFrame(steps);
            if (((Cur[0].Flags) & FlagResync) != 0) Session.OnResyncFlag();
        }

        static Pad Take(Dictionary<long, Pad> d, int epoch, int frame)
        {
            Pad p;
            long k = Key(epoch, frame);
            if (d.TryGetValue(k, out p)) d.Remove(k);
            return p;
        }

        static Pad WaitRemote(int frame)
        {
            Stopwatch sw = Stopwatch.StartNew();
            long k = Key(Epoch, frame);
            while (true)
            {
                Pad p;
                lock (remote)
                {
                    if (remote.TryGetValue(k, out p)) { remote.Remove(k); LastStallMs = (int)sw.ElapsedMilliseconds; return p; }
                }
                Session.Pump();
                if (!Active || !Running) return Pad.Neutral(axisCount);
                long ms = sw.ElapsedMilliseconds;
                if (ms > 8000) { Session.End(L.M("no_response")); return Pad.Neutral(axisCount); }
                if (ms > 250 && ms % 1000 < 2) CoopPlugin.Log("Stallo rete: attendo frame " + frame + " da " + ms + " ms");
                Thread.Sleep(ms < 4 ? 0 : 1);
            }
        }

        static Pad Sample()
        {
            Pad p = Pad.Neutral(axisCount);
            if (Bot) return BotPad();
            Player player = ReInput.players.GetPlayer(0);
            if (player == null) return p;
            Bypass = true;
            try
            {
                for (int i = 0; i < actionIds.Length; i++)
                {
                    int id = actionIds[i];
                    if (player.GetButton(id)) p.Pos |= 1UL << i;
                    if (player.GetNegativeButton(id)) p.Neg |= 1UL << i;
                    int a = axisById[id];
                    if (a >= 0) p.Axes[a] = PadCodec.Quantize(player.GetAxis(id));
                }
            }
            finally { Bypass = false; }
            return p;
        }

        // Deterministic test pattern for the guest (config BotInput=true): walk, shoot, jump.
        static Pad BotPad()
        {
            Pad p = Pad.Neutral(axisCount);
            long f = ClockFrames + LocalSlot * 45;
            int moveX = (f / 90) % 2 == 0 ? 1 : -1;
            SetBit(p, 0, moveX > 0, moveX < 0);
            if (axisCount > 0 && Index(0) >= 0 && axisById[0] >= 0) p.Axes[axisById[0]] = (sbyte)(moveX * 127);
            SetBit(p, 3, true, false);                    // Shoot held
            SetBit(p, 2, f % 47 < 6, false);              // Jump taps
            SetBit(p, 7, f % 211 < 3, false);             // Dash
            SetBit(p, 13, f % 37 < 3, false);             // Accept: dialogues, menus, "press a button"
            SetBit(p, 4, f % 503 < 3, false);             // Super/EX
            return p;
        }

        static void SetBit(Pad p, int actionId, bool pos, bool neg)
        {
            int i = Index(actionId);
            if (i < 0) return;
            if (pos) p.Pos |= 1UL << i;
            if (neg) p.Neg |= 1UL << i;
        }

        static int Index(int actionId) { return actionId >= 0 && actionId < indexById.Length ? indexById[actionId] : -1; }

        public static int IdByName(string name)
        {
            if (name == null) return -1;
            int id;
            if (!idByName.TryGetValue(name, out id))
            {
                InputAction a = ReInput.mapping.GetAction(name);
                id = a == null ? -1 : a.id;
                idByName[name] = id;
            }
            return id;
        }

        static int SlotOf(int rewiredPlayerId) { return rewiredPlayerId == 0 ? 0 : (rewiredPlayerId == 1 ? 1 : -1); }

        // FixedUpdate reads see their own prev/cur pair, advanced once per fixed step,
        // so a "Down" edge is seen by exactly one fixed step (Rewired semantics).
        static void Pair(int slot, out Pad cur, out Pad prev)
        {
            if (Time.inFixedTimeStep)
            {
                float t = Time.fixedTime;
                if (t != fixedStamp)
                {
                    fixedStamp = t;
                    FixPrev = FixCur;
                    FixCur = new Pad[] { Cur[0], Cur[1] };
                }
                cur = FixCur[slot]; prev = FixPrev[slot];
            }
            else { cur = Cur[slot]; prev = Prev[slot]; }
        }

        public static bool Button(int rewiredPlayerId, int actionId, int kind)
        {
            int slot = SlotOf(rewiredPlayerId);
            if (slot < 0 || !Running) return false;
            Pad c, p;
            Pair(slot, out c, out p);
            return PadCodec.Button(c, p, Index(actionId), kind);
        }

        public static float Axis(int rewiredPlayerId, int actionId)
        {
            int slot = SlotOf(rewiredPlayerId);
            if (slot < 0 || !Running) return 0f;
            int i = Index(actionId);
            if (i < 0) return 0f;
            Pad c, p;
            Pair(slot, out c, out p);
            int a = axisById[actionId];
            if (a >= 0) return PadCodec.Dequantize(c.Axes[a]);
            return (((c.Pos >> i) & 1) != 0 ? 1f : 0f) - (((c.Neg >> i) & 1) != 0 ? 1f : 0f);
        }

        public static float TimePressed(int rewiredPlayerId, int actionId)
        {
            int slot = SlotOf(rewiredPlayerId), i = Index(actionId);
            if (slot < 0 || i < 0 || !Running) return 0f;
            return held[slot, i] / 60f;
        }

        public static bool AnyDown(int slot)
        {
            if (slot < 0 || slot > 1 || !Running) return false;
            Pad c, p;
            Pair(slot, out c, out p);
            return PadCodec.AnyDown(c, p);
        }

        public static bool AnyHeld(int slot)
        {
            if (slot < 0 || slot > 1 || !Running) return false;
            Pad c, p;
            Pair(slot, out c, out p);
            return PadCodec.AnyHeld(c);
        }

        public static int AgreedStage()
        {
            if (!Running) return 0;
            return Math.Min(Cur[0].Stage, Cur[1].Stage);
        }
    }
}
