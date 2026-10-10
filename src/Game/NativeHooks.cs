using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

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
        private static readonly HashSet<string> Safe = new HashSet<string>(StringComparer.Ordinal)
        {
            "MousePickEvent", "MousePick", "Raycast", "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp", "GetButton", "GetButtonDown", "GetButtonUp", "GetKey", "GetKeyDown", "GetAxis", "GetAxisVector",
            "GetDistance", "GetPosition", "GetRotation", "GetScale", "GetVelocity", "GetName", "GetChild", "GetParent", "GetComponent", "GetStringLength", "GetSubstring", "GetStringRight", "BuildString",
            "GetFsmBool", "GetFsmFloat", "GetFsmInt", "GetFsmString", "GetFsmGameObject", "GameObjectIsNull", "GameObjectCompare", "GameObjectCompareTag", "GameObjectIsChildOf", "HasComponent",
            "FloatCompare", "FloatOperator", "FloatClamp", "FloatMultiply", "IntCompare", "IntOperator", "IntClamp", "BoolTest", "BoolAllTrue", "BoolAnyTrue", "BoolNoneTrue", "StringCompare", "TriggerEvent", "Wait",
            "SetBoolValue", "SetStringValue", "SetFloatValue", "SetIntValue", "SetGameObject", "SetVector3Value", "ArrayListGet"
        };
        public static void Install()
        {
            NativeAdapter.CheckBindings();
            if (harmony != null) return; harmony = HarmonyInstance.Create("summercoop.native.v1");
            harmony.Patch(typeof(Fsm).GetMethod("SwitchState", BindingFlags.Instance | BindingFlags.NonPublic), new HarmonyMethod(typeof(NativeHooks).GetMethod("SwitchPrefix")), null, null);
            foreach (MethodInfo method in typeof(Fsm).GetMethods(BindingFlags.Instance | BindingFlags.Public)) if (method.Name == "Event") harmony.Patch(method, new HarmonyMethod(typeof(NativeHooks).GetMethod("EventPrefix")), null, null);
            MethodInfo input = typeof(AxisCarController).GetMethod("GetInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (input != null) harmony.Patch(input, new HarmonyMethod(typeof(NativeHooks).GetMethod("InputPrefix")), null, null);
            MethodInfo boatAxis = typeof(HutongGames.PlayMaker.Actions.GetAxis).GetMethod("DoGetAxis", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (boatAxis != null) harmony.Patch(boatAxis, new HarmonyMethod(typeof(NativeHooks).GetMethod("BoatAxisPrefix")), null, null);
            foreach (string method in new string[] { "UpdateState", "FixedUpdateState", "LateUpdateState" })
                harmony.Patch(typeof(Fsm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic), new HarmonyMethod(typeof(NativeHooks).GetMethod("UpdatePrefix")), null, null);
        }
        public static bool EventPrefix(Fsm __instance) { if (Saving) Pending.SetValue(__instance, null); return !Saving; }
        public static bool SwitchPrefix(Fsm __instance, FsmState toState)
        {
            FsmState state = toState;
            CoopRuntime runtime = CoopRuntime.Instance;
            if (Saving) { Pending.SetValue(__instance, null); return false; }
            if (runtime != null && runtime.Registry != null && runtime.Registry.Applying) { Pending.SetValue(__instance, null); return false; }
            if (Replaying || runtime == null || !runtime.Active || runtime.Registry == null) return true;
            FsmBinding b = runtime.Registry.Binding(__instance);
            if (b == null || b.Personal) { bool permitted = runtime.PersonalState(__instance, state); if (!permitted) Pending.SetValue(__instance, null); return permitted; }
            if (runtime.Joining) { Pending.SetValue(__instance, null); return false; }
            if (runtime.Session.IsHost) return true;
            if (b.Requests.Contains(state.Name) && !SafeState(state))
            {
                Pending.SetValue(__instance, null); runtime.RequestState(b, state.Name);
                // Keep the client's input state alive; effects only run on the host.
                FsmState current = __instance.ActiveState;
                if (current != null && SafeState(current)) { if (CoopRuntime.TraceAllocations) DiagnosticInputReentries++; Replaying = true; try { current.OnEnter(); } finally { Replaying = false; } }
                Pending.SetValue(__instance, null); return false;
            }
            if (SafeState(state)) return true;
            Pending.SetValue(__instance, null); return false;
        }
        public static bool UpdatePrefix(Fsm __instance, FsmState state)
        {
            if (UpdateMode == 0 || Replaying) return true;
            if (UpdateMode == 1) return false;
            CoopRuntime r = CoopRuntime.Instance;
            FsmBinding b = r.Registry.Binding(__instance);
            return b == null || b.Personal || (b.Requests.Count > 0 && SafeState(state));
        }
        public static bool SafeState(FsmState state)
        {
            if (state == null) return false;
            bool safe;
            if (StateSafety.TryGetValue(state, out safe)) return safe;
            safe = true;
            foreach (FsmStateAction action in state.Actions) if (!Safe.Contains(action.GetType().Name)) { safe = false; break; }
            StateSafety[state] = safe; return safe;
        }
        public static bool InputPrefix(AxisCarController __instance, ref float throttleInput, ref float brakeInput, ref float steerInput, ref float handbrakeInput, ref float clutchInput, ref bool startEngineInput, ref int targetGear)
        {
            CoopRuntime r = CoopRuntime.Instance; if (r == null || !r.Active || r.Adapter == null || !r.Session.IsHost) return true;
            DriveInput input = r.Adapter.InputFor(__instance, r.Session.World); if (input == null) return true;
            throttleInput = input.Throttle; brakeInput = input.Brake; steerInput = input.Steering; handbrakeInput = input.Handbrake; clutchInput = input.Clutch; startEngineInput = input.Start; targetGear = input.Shift;
            return false;
        }
        public static bool BoatAxisPrefix(HutongGames.PlayMaker.Actions.GetAxis __instance)
        {
            CoopRuntime r = CoopRuntime.Instance;
            if (r == null || !r.Active || !r.Session.IsHost || r.Adapter == null || __instance.Owner == null || __instance.Owner.transform.root.name != "BOAT") return true;
            DriveInput input = r.Adapter.BoatInput(r.Session.World); if (input == null) return true;
            string axis = __instance.axisName.Value.ToLowerInvariant(); float value;
            if (axis.IndexOf("steer", StringComparison.Ordinal) >= 0 || axis == "horizontal") value = input.Steering;
            else if (axis.IndexOf("throttle", StringComparison.Ordinal) >= 0 || axis == "vertical") value = input.Throttle;
            else return true;
            __instance.store.Value = value * __instance.multiplier.Value; return false;
        }
        public static void Uninstall() { if (harmony != null) { harmony.UnpatchAll("summercoop.native.v1"); harmony = null; } UpdateMode = 0; StateSafety.Clear(); }
    }
}
