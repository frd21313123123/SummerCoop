using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;
using SummerCoop.Core;

namespace SummerCoop.Game
{
    public static class NativeHooks
    {
        private static HarmonyInstance harmony;
        public static bool Saving, Replaying;
        // Refreshed by the session lifecycle: 0 native execution, 1 join barrier, 2 client filtering.
        public static int UpdateMode;
        public static long DiagnosticInputReentries;
        private static readonly FieldInfo Pending = typeof(Fsm).GetField("switchToState", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Dictionary<FsmState, bool> StateSafety = new Dictionary<FsmState, bool>();
        private static readonly Dictionary<FsmState, ClientProjection> Projections = new Dictionary<FsmState, ClientProjection>();
        private static readonly Dictionary<FsmState, ClientProjection> SharedProjections = new Dictionary<FsmState, ClientProjection>();
        private static readonly Dictionary<FsmState, ClientProjection> PersonalProjections = new Dictionary<FsmState, ClientProjection>();
        private static readonly NativeReplayTracker<Fsm> RemoteActors = new NativeReplayTracker<Fsm>();
        private static readonly NativeReplayTracker<Fsm> PersonalReplays = new NativeReplayTracker<Fsm>();
        private static bool localReplay;
        public sealed class ClientProjection
        {
            public bool Permitted;
            public FsmStateAction[] Effects;
            public bool[] Enabled;
            public ClientProjection Previous;
            public ClientProjection Snapshot() { return new ClientProjection { Permitted = Permitted, Effects = Effects, Enabled = new bool[Effects.Length], Previous = Previous == null ? null : Previous.Snapshot() }; }
            public void Suspend() { if (Previous != null) Previous.Suspend(); for (int i = 0; i < Effects.Length; i++) { Enabled[i] = Effects[i].Enabled; Effects[i].Enabled = false; } }
            public void Restore() { for (int i = 0; i < Effects.Length; i++) Effects[i].Enabled = Enabled[i]; if (Previous != null) Previous.Restore(); }
            public void FinishSuppressed() { for (int i = 0; i < Effects.Length; i++) if (Enabled[i]) Effects[i].Finished = true; }
        }
        public static void Install()
        {
            NativeAdapter.CheckBindings();
            if (harmony != null) return; harmony = HarmonyInstance.Create("summercoop.native.v1");
            harmony.Patch(typeof(Fsm).GetMethod("SwitchState", BindingFlags.Instance | BindingFlags.NonPublic), new HarmonyMethod(typeof(NativeHooks).GetMethod("SwitchPrefix")), new HarmonyMethod(typeof(NativeHooks).GetMethod("SwitchPostfix")), null);
            foreach (MethodInfo method in typeof(Fsm).GetMethods(BindingFlags.Instance | BindingFlags.Public)) if (method.Name == "Event") harmony.Patch(method, new HarmonyMethod(typeof(NativeHooks).GetMethod("EventPrefix")), null, null);
            foreach (MethodInfo input in NativeVehicleBindings.InputMethods()) harmony.Patch(input, new HarmonyMethod(typeof(NativeHooks).GetMethod("InputPrefix")), null, null);
            MethodInfo boatAxis = typeof(HutongGames.PlayMaker.Actions.GetAxis).GetMethod("DoGetAxis", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (boatAxis != null) harmony.Patch(boatAxis, new HarmonyMethod(typeof(NativeHooks).GetMethod("BoatAxisPrefix")), null, null);
            foreach (string method in new string[] { "UpdateState", "FixedUpdateState", "LateUpdateState" })
                harmony.Patch(typeof(Fsm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic), new HarmonyMethod(typeof(NativeHooks).GetMethod("UpdatePrefix")), null, null);
        }
        public static bool EventPrefix(Fsm __instance) { if (Saving) Pending.SetValue(__instance, null); return !Saving; }
        public static bool SwitchPrefix(Fsm __instance, FsmState toState, out ClientProjection __state)
        {
            __state = null;
            FsmState state = toState;
            CoopRuntime runtime = CoopRuntime.Instance;
            if (Saving) { Pending.SetValue(__instance, null); return false; }
            if (runtime != null && runtime.Registry != null && runtime.Registry.Applying) { Pending.SetValue(__instance, null); return false; }
            if (runtime == null || !runtime.Active || runtime.Registry == null) return true;
            FsmBinding b = runtime.Registry.Binding(__instance);
            // A new host drink is local input, even if this case still carries
            // the guest's asynchronous Play anim marker from its previous use.
            if (runtime.Session.IsHost && NativeActorContext.Current == null && b != null
                && b.Component.FsmName == "Use" && state.Name == "Check drink"
                && b.Component.FsmVariables.FindFsmInt("DestroyedBottles") != null) RemoteActors.Release(__instance);
            bool remote = NativeActorContext.Current != null && NativeActorContext.Current != "host" || RemoteActors.Contains(__instance);
            if (runtime.Session.IsHost && remote)
            {
                if (b != null && b.Personal || __instance.Owner != null && WorldRegistry.PersonalObject(__instance.Owner.gameObject)) { Pending.SetValue(__instance, null); return false; }
                RemoteActors.Transition(__instance, state.Name, b == null ? "" : b.WaitingState);
                __state = MaskSwitch(__instance.ActiveState, state, NativeActionScope.SharedActor); __state.Suspend(); return true;
            }
            if (!runtime.Session.IsHost && (localReplay || PersonalReplays.Contains(__instance)))
            {
                if (b == null || b.Personal) return true;
                if (!localReplay && b.Requests.Contains(state.Name) && !CanRunLocally(state) && runtime.RequestState(b, state.Name)) { Pending.SetValue(__instance, null); return false; }
                PersonalReplays.Transition(__instance, state.Name, b.WaitingState);
                __state = MaskSwitch(__instance.ActiveState, state, NativeActionScope.PersonalReplay); __state.Suspend(); return true;
            }
            if (Replaying) return true;
            if (b == null || b.Personal) { bool permitted = runtime.PersonalState(__instance, state); if (!permitted) Pending.SetValue(__instance, null); return permitted; }
            if (runtime.Joining) { Pending.SetValue(__instance, null); return false; }
            if (runtime.Session.IsHost) return true;
            if (b.Requests.Contains(state.Name) && !CanRunLocally(state))
            {
                Pending.SetValue(__instance, null); runtime.RequestState(b, state.Name);
                // Keep the client's input state alive; effects only run on the host.
                FsmState current = __instance.ActiveState;
                if (current != null && CanRunLocally(current))
                {
                    if (CoopRuntime.TraceAllocations) DiagnosticInputReentries++;
                    ClientProjection projection = Projection(current); projection.Suspend(); Replaying = true;
                    try { current.OnEnter(); } finally { Replaying = false; projection.Restore(); }
                }
                Pending.SetValue(__instance, null); return false;
            }
            if (CanRunLocally(state)) { __state = MaskSwitch(__instance.ActiveState, state, NativeActionScope.InputPolling); __state.Suspend(); return true; }
            Pending.SetValue(__instance, null); return false;
        }
        public static void SwitchPostfix(Fsm __instance, ClientProjection __state)
        {
            if (__state != null) { __state.FinishSuppressed(); __state.Restore(); }
            CoopRuntime runtime = CoopRuntime.Instance;
            if (runtime != null && runtime.Active && __instance.Owner != null && PersonalKeys.IsPickup(__instance.Owner.gameObject.name)) runtime.SaveKeysNow();
        }
        public static bool UpdatePrefix(Fsm __instance, FsmState state)
        {
            if (RemoteActors.Contains(__instance)) { RemoveEffects(state, NativeActionScope.SharedActor); return true; }
            if (PersonalReplays.Contains(__instance)) { RemoveEffects(state, NativeActionScope.PersonalReplay); return true; }
            if (UpdateMode == 0 || Replaying) return true;
            if (UpdateMode == 1) return false;
            CoopRuntime r = CoopRuntime.Instance;
            FsmBinding b = r.Registry.Binding(__instance);
            if (b == null || b.Personal) return true;
            if (b.Requests.Count == 0) return false;
            if (!CanRunLocally(state)) { ResumeInput(b); return false; }
            // The scene can have entered this state before multiplayer became active.
            // Remove its shared effects from the already initialized update list too.
            if (!SafeState(state)) foreach (FsmStateAction effect in Projection(state).Effects) state.ActiveActions.Remove(effect);
            return true;
        }
        public static bool ResumeInput(FsmBinding binding)
        {
            if (binding == null || binding.Component == null || binding.WaitingState == "") return false;
            FsmState waiting = binding.Component.Fsm.GetState(binding.WaitingState);
            if (!CanRunLocally(waiting)) return false;
            ClientProjection projection = Projection(waiting); bool replaying = Replaying;
            projection.Suspend(); Replaying = true;
            try { NativeAdapter.ExecuteState(binding, waiting.Name); }
            finally { Replaying = replaying; projection.Restore(); }
            foreach (FsmStateAction effect in projection.Effects) waiting.ActiveActions.Remove(effect);
            return true;
        }
        public static bool SafeState(FsmState state)
        {
            if (state == null) return false;
            bool safe;
            if (StateSafety.TryGetValue(state, out safe)) return safe;
            safe = true;
            foreach (FsmStateAction action in state.Actions) if (!NativeActionPolicy.ReadOnly(action.GetType().Name)) { safe = false; break; }
            StateSafety[state] = safe; return safe;
        }
        public static bool CanRunLocally(FsmState state) { return state != null && (SafeState(state) || Projection(state).Permitted); }
        private static ClientProjection Projection(FsmState state)
        {
            ClientProjection projection;
            if (Projections.TryGetValue(state, out projection)) return projection;
            List<NativeActionInfo> infos = new List<NativeActionInfo>(); List<FsmStateAction> effects = new List<FsmStateAction>();
            foreach (FsmStateAction action in state.Actions)
            {
                NativeActionInfo info = NativeActionReader.Describe(action, delegate(object target) { return NativePersonalReferences.IsPersonal(target, state.Fsm); }); infos.Add(info);
                if (!NativeActionPolicy.Allowed(info, NativeActionScope.InputPolling)) effects.Add(action);
            }
            projection = new ClientProjection { Permitted = NativeActionPolicy.Pollable(infos), Effects = effects.ToArray(), Enabled = new bool[effects.Count] };
            Projections.Add(state, projection); return projection;
        }
        private static ClientProjection ScopeProjection(FsmState state, NativeActionScope scope)
        {
            if (scope == NativeActionScope.InputPolling) return Projection(state);
            Dictionary<FsmState, ClientProjection> cache = scope == NativeActionScope.SharedActor ? SharedProjections : PersonalProjections;
            ClientProjection projection; if (cache.TryGetValue(state, out projection)) return projection;
            List<FsmStateAction> effects = new List<FsmStateAction>();
            foreach (FsmStateAction action in state.Actions)
            {
                NativeActionInfo info = NativeActionReader.Describe(action, delegate(object target) { return NativePersonalReferences.IsPersonal(target, state.Fsm); });
                if (!NativeActionPolicy.Allowed(info, scope)) effects.Add(action);
            }
            projection = new ClientProjection { Permitted = true, Effects = effects.ToArray(), Enabled = new bool[effects.Count] }; cache.Add(state, projection); return projection;
        }
        private static ClientProjection MaskSwitch(FsmState current, FsmState next, NativeActionScope scope)
        {
            ClientProjection incoming = ScopeProjection(next, scope);
            ClientProjection mask = incoming.Snapshot();
            mask.Previous = current == null || current == next ? null : ScopeProjection(current, scope).Snapshot(); return mask;
        }
        private static void RemoveEffects(FsmState state, NativeActionScope scope)
        { if (state != null) foreach (FsmStateAction effect in ScopeProjection(state, scope).Effects) state.ActiveActions.Remove(effect); }
        public static void ReplayPersonal(FsmBinding binding, string state)
        {
            if (binding == null || binding.Component == null || binding.Personal) return;
            localReplay = true;
            try { NativeAdapter.ExecuteState(binding, state); }
            finally { localReplay = false; }
        }
        public static bool InputPrefix(CarController __instance, ref float throttleInput, ref float brakeInput, ref float steerInput, ref float handbrakeInput, ref float clutchInput, ref bool startEngineInput, ref int targetGear)
        {
            CoopRuntime r = CoopRuntime.Instance; if (r == null || !r.Active || r.Adapter == null || !r.Session.IsHost) return true;
            DriveInput input = r.Adapter.InputFor(__instance, r.Session.World); if (input == null) return true;
            throttleInput = input.Throttle; brakeInput = input.Brake; steerInput = input.Steering; handbrakeInput = input.Handbrake; clutchInput = input.Clutch; startEngineInput = input.Start; targetGear = input.Shift;
            return false;
        }
        public static bool BoatAxisPrefix(HutongGames.PlayMaker.Actions.GetAxis __instance)
        {
            CoopRuntime r = CoopRuntime.Instance;
            if (r == null || !r.Active || !r.Session.IsHost || r.Adapter == null) return true;
            DriveInput input = r.Adapter.BoatInput(r.Session.World); if (input == null) return true;
            if (__instance.Owner == null || __instance.Owner.transform.root.name != "BOAT") return true;
            string axis = __instance.axisName.Value.ToLowerInvariant(); float value;
            if (axis.IndexOf("steer", StringComparison.Ordinal) >= 0 || axis == "horizontal") value = input.Steering;
            else if (axis.IndexOf("throttle", StringComparison.Ordinal) >= 0 || axis == "vertical") value = input.Throttle;
            else return true;
            __instance.store.Value = value * __instance.multiplier.Value; return false;
        }
        public static void ResetScene() { StateSafety.Clear(); Projections.Clear(); SharedProjections.Clear(); PersonalProjections.Clear(); RemoteActors.Clear(); PersonalReplays.Clear(); NativePersonalReferences.Clear(); localReplay = false; }
        public static void Uninstall()
        {
            if (harmony != null)
            {
                // Harmony 1 removes prefixes first in UnpatchAll. A postfix using
                // __state then has no matching local and cannot be regenerated.
                harmony.Unpatch(typeof(Fsm).GetMethod("SwitchState", BindingFlags.Instance | BindingFlags.NonPublic), typeof(NativeHooks).GetMethod("SwitchPostfix"));
                harmony.UnpatchAll("summercoop.native.v1"); harmony = null;
            }
            UpdateMode = 0; ResetScene();
        }
    }
}
