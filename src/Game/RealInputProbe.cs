using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    // Opt-in integration check at the Input read, before the native raycast and
    // PickUp tag/check states. No part variable or native state is assigned here.
    public sealed class RealInputProbe : MonoBehaviour
    {
        private const string HarmonyId = "summercoop.real-input-probe";
        private HarmonyInstance harmony;
        private readonly List<MethodInfo> patched = new List<MethodInfo>();
        private readonly List<string> lines = new List<string>();
        private PlayMakerFSM hand;
        private string partId, doorId;
        private GameObject aimed;
        private Vector3 target, eyeOffset;
        private Quaternion doorBefore;
        private int step;
        private int doorButton;
        private float began, sent;
        private float nextRayDiagnostic;
        private float nextNativeHandRayDiagnostic, nextNativeDoorRayDiagnostic;
        private long doorRayCalls;
        private bool nativeRaySample;
        private static readonly FieldInfo NativeRayHit = typeof(ActionHelpers).GetField("mousePickInfo", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo StateFinished = typeof(FsmState).GetField("finished", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private string lastHand = "", lastPick = "";
        private bool done, automatic, sentClick;
        private static RealInputProbe current;
        private static bool sampling;
        private static int clickFrame = -1;

        private void Update()
        {
            CoopRuntime r = CoopRuntime.Instance;
            if (done || r == null || !r.Active || r.Registry == null || r.Joining || r.Session.IsHost) return;
            try
            {
                if (began == 0) began = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - began < 3) return;
                if (step == 0)
                {
                    automatic = CoopRuntime.Argument("--summercoop-test-real-input") == "simulated";
                    foreach (PlayMakerFSM f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
                        if (f.FsmName == "PickUp" && WorldRegistry.PersonalObject(f.gameObject)) { hand = f; break; }
                    foreach (ObjectBinding b in r.Registry.Objects.Values)
                        if (b.Object != null && b.Object.name == "starter(Clone)" && b.Object.tag == "PART") { partId = b.Id; break; }
                    foreach (FsmBinding b in r.Registry.Fsms.Values)
                        if (b.Component != null && b.Component.FsmName == "Use" && b.Key.IndexOf("GarageDoors", StringComparison.Ordinal) >= 0 && b.Key.IndexOf("DoorLeft", StringComparison.Ordinal) >= 0) { doorId = b.Id; break; }
                    if (hand == null || partId == null || Camera.main == null) throw new Exception("Native hand, loose starter or camera unavailable");
                    GameObject player = GameObject.Find("PLAYER");
                    if (player == null) throw new Exception("Local player unavailable");
                    eyeOffset = Camera.main.transform.position - player.transform.position;
                    aimed = r.Registry.ObjectOf(partId); target = Centre(aimed);
                    InstallObservers(); if (automatic) InstallReaders();
                    Record(r, "MODE=" + (automatic ? "simulated Input reads; native raycast/tag/state path" : "physical mouse; native raycast/tag/state path"));
                    Record(r, "READY=take starter with left mouse button");
                    Record(r, "HAND_ENABLED=" + hand.enabled + " ACTIVE=" + hand.gameObject.activeInHierarchy + " MODE=" + NativeHooks.UpdateMode);
                    foreach (string name in new string[] { "PlayerInMenu", "PlayerStop", "PlayerSeated", "PlayerSleeps", "ToolMode" })
                    {
                        FsmBool value = FsmVariables.GlobalVariables.FindFsmBool(name);
                        if (value != null) Record(r, name + "=" + value.Value);
                    }
                    sent = Time.realtimeSinceStartup; step = 1;
                    StartCoroutine(CaptureReady(r));
                }
                for (int button = 0; button < 2; button++)
                    if (Input.GetMouseButtonDown(button) || Input.GetMouseButtonUp(button)) Record(r, "OS_MOUSE=button " + button + " down=" + Input.GetMouseButtonDown(button) + " up=" + Input.GetMouseButtonUp(button) + " position=" + Input.mousePosition);
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F6)) Record(r, "OS_KEY=Escape " + Input.GetKeyDown(KeyCode.Escape) + " F6 " + Input.GetKeyDown(KeyCode.F6));
                Observe(r);
                Player actor = r.Session.World.Players[r.Session.LocalId];
                if (step == 1)
                {
                    if (actor.Held == partId && hand.ActiveStateName == "Part picked")
                    { Record(r, "TAKE=PASS"); sent = Time.realtimeSinceStartup; step = 2; }
                    else PulseOrTimeout(r, "take", 2);
                }
                else if (step == 2 && Time.realtimeSinceStartup - sent >= 3)
                {
                    GameObject part = r.Registry.ObjectOf(partId);
                    if (!part.transform.IsChildOf(GameObject.Find("PLAYER").transform)) throw new Exception("Replicated snapshot detached the native hand item");
                    Record(r, "HOLD=PASS: ItemPivot retained for three seconds");
                    Record(r, "READY=drop starter with left mouse button"); sent = Time.realtimeSinceStartup; sentClick = false; clickFrame = -1; step = 3;
                }
                else if (step == 3)
                {
                    if (actor.Held == "" && hand.ActiveStateName == "Look for object")
                    {
                        Record(r, "DROP=PASS");
                        FsmBinding door = r.Registry.FsmOf(doorId);
                        if (door == null) throw new Exception("Garage door unavailable");
                        aimed = door.Component.gameObject; target = Centre(aimed);
                        GameObject actual = door.Component.FsmVariables.FindFsmGameObject("Door").Value;
                        doorBefore = actual.transform.rotation;
                        FsmFloat rotation = door.Component.FsmVariables.FindFsmFloat("Rotation");
                        doorButton = rotation != null && Math.Abs(rotation.Value) > 50 ? 1 : 0;
                        Record(r, "READY=" + (doorButton == 1 ? "close left garage door with right mouse button" : "open left garage door with left mouse button")); sent = Time.realtimeSinceStartup; sentClick = false; clickFrame = -1; step = 4;
                        StartCoroutine(CaptureReady(r));
                    }
                    else PulseOrTimeout(r, "drop", 2);
                }
                else if (step == 4)
                {
                    FsmBinding door = r.Registry.FsmOf(doorId);
                    GameObject actual = door.Component.FsmVariables.FindFsmGameObject("Door").Value;
                    if (Quaternion.Angle(doorBefore, actual.transform.rotation) > 0.1f)
                    { Record(r, "DOOR=PASS: native input produced a replicated door rotation"); Finish(r, "PASS: actual native raycast/button take, settled hand, drop and door interaction"); }
                    else PulseOrTimeout(r, "door", 3);
                }
            }
            catch (Exception e) { Finish(r, "FAIL: " + e); }
        }
        private void LateUpdate()
        {
            if (done || step == 0 || aimed == null || Camera.main == null) return;
            FsmBool inMenu = FsmVariables.GlobalVariables.FindFsmBool("PlayerInMenu");
            if (inMenu != null && inMenu.Value) return;
            GameObject player = GameObject.Find("PLAYER"); if (player == null) return;
            // Keep the camera near the actual collider, including the original
            // one-metre native hand ray limit. Never move the replicated item.
            Vector3 eye = target + Vector3.up * 0.55f - Vector3.forward * 0.25f;
            player.transform.position = eye - eyeOffset;
            Camera.main.transform.position = eye;
            Camera.main.transform.rotation = Quaternion.LookRotation(target - eye, Vector3.up);
            if (Time.realtimeSinceStartup >= nextRayDiagnostic)
            {
                nextRayDiagnostic = Time.realtimeSinceStartup + 2;
                DiagnoseRay(CoopRuntime.Instance);
            }
        }
        private void PulseOrTimeout(CoopRuntime r, string operation, float delay)
        {
            float elapsed = Time.realtimeSinceStartup - sent;
            if (automatic && elapsed >= delay && !sentClick)
            { sentClick = true; clickFrame = Time.frameCount + 1; Record(r, "INPUT=button " + (step == 4 ? doorButton : 0) + " down frame " + clickFrame); }
            if (elapsed > (automatic ? 12 : 300))
            {
                FsmBinding door = r.Registry.FsmOf(doorId);
                throw new Exception(operation + " stalled; hand=" + hand.ActiveStateName + "; door=" + (door == null ? "" : door.Component.ActiveStateName) + "; status=" + r.StatusText);
            }
        }
        private void Observe(CoopRuntime r)
        {
            string state = hand.ActiveStateName;
            FsmGameObject pick = hand.FsmVariables.FindFsmGameObject("PickedObject");
            string name = pick == null || pick.Value == null ? "NONE" : pick.Value.name;
            if (state != lastHand || name != lastPick)
            { lastHand = state; lastPick = name; Record(r, "HAND=" + state + " PICK=" + name + " OWNER=" + (pick == null || pick.Value == null ? "" : r.Registry.IdOf(pick.Value))); }
        }
        private void DiagnoseRay(CoopRuntime r)
        {
            if (r == null || r.Registry == null || hand == null) return;
            FsmState state = hand.Fsm.ActiveState;
            HutongGames.PlayMaker.Actions.MousePick native = null;
            if (state != null) foreach (FsmStateAction action in state.Actions)
                if (action is HutongGames.PlayMaker.Actions.MousePick) { native = (HutongGames.PlayMaker.Actions.MousePick)action; break; }
            Vector3 mouse = automatic ? MousePosition() : Input.mousePosition;
            FsmBinding door = r.Registry.FsmOf(doorId);
            float distance = native == null ? 1f : native.rayDistance.Value;
            int mask = native == null ? Physics.DefaultRaycastLayers : ActionHelpers.LayerArrayToLayerMask(native.layerMask, native.invertMask.Value);
            if (step == 4 && door != null && door.Component.Fsm.ActiveState != null)
                foreach (FsmStateAction action in door.Component.Fsm.ActiveState.Actions)
                {
                    HutongGames.PlayMaker.Actions.MousePickEvent doorPick = action as HutongGames.PlayMaker.Actions.MousePickEvent;
                    if (doorPick != null) { distance = doorPick.rayDistance.Value; mask = ActionHelpers.LayerArrayToLayerMask(doorPick.layerMask, doorPick.invertMask.Value); break; }
                }
            RaycastHit hit;
            bool found = Physics.Raycast(Camera.main.ScreenPointToRay(mouse), out hit, distance, mask);
            string ray = found && hit.collider != null ? hit.collider.gameObject.name + " tag=" + hit.collider.gameObject.tag + " id=" + r.Registry.IdOf(hit.collider.gameObject) + " distance=" + hit.distance : "NONE";
            string actionResult = native == null ? "no MousePick in active state" : "nativeHit=" + native.storeDidPickObject.Value + " nativeDistance=" + native.storeDistance.Value + " nativePicked=" + (native.storeGameObject.Value == null ? "NONE" : native.storeGameObject.Value.name);
            Record(r, "RAY=" + ray + "; range=" + distance + " mask=" + mask + "; " + actionResult + "; mouse=" + mouse + "; cursorLocked=" + Screen.lockCursor + "; doorState=" + (door == null ? "NONE" : door.Component.ActiveStateName));
            if (door != null)
            {
                FsmState doorState = door.Component.Fsm.ActiveState;
                Record(r, "DOOR_FSM=enabled=" + door.Component.enabled + " objectActive=" + door.Component.gameObject.activeInHierarchy + " initialized=" + door.Component.Fsm.Initialized + " fsmFinished=" + door.Component.Fsm.Finished + " started=" + door.Component.Fsm.Started + " mode=" + NativeHooks.UpdateMode + " personal=" + door.Personal + " requests=" + door.Requests.Count + " stateActive=" + (doorState != null && doorState.Active) + " stateFinished=" + (doorState == null || StateFinished == null ? "UNKNOWN" : StateFinished.GetValue(doorState).ToString()) + " safe=" + NativeHooks.SafeState(doorState) + " local=" + NativeHooks.CanRunLocally(doorState) + " nativePickCalls=" + doorRayCalls);
                if (doorState != null) foreach (FsmStateAction action in doorState.Actions)
                {
                    HutongGames.PlayMaker.Actions.MousePickEvent pick = action as HutongGames.PlayMaker.Actions.MousePickEvent;
                    Record(r, "DOOR_ACTION=" + action.GetType().Name + " enabled=" + action.Enabled + " finished=" + action.Finished + " entered=" + action.Entered + " inUpdate=" + doorState.ActiveActions.Contains(action) + (pick == null ? "" : " everyFrame=" + pick.everyFrame + " owner=" + (pick.Owner == null ? "NONE" : pick.Owner.name)));
                }
            }
        }
        private static Vector3 Centre(GameObject go)
        {
            Collider collider = go.GetComponent<Collider>();
            if (collider == null) collider = go.GetComponentInChildren<Collider>();
            return collider == null ? go.transform.position : collider.bounds.center;
        }
        private void Record(CoopRuntime r, string line)
        {
            lines.Add(Time.realtimeSinceStartup.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " " + line);
            File.AppendAllText(Path.Combine(r.SaveRoot, "real-input-proof.txt"), lines[lines.Count - 1] + Environment.NewLine);
            if (line.StartsWith("READY=", StringComparison.Ordinal) || line.IndexOf("=PASS", StringComparison.Ordinal) >= 0 || line.StartsWith("REAL_INPUT=", StringComparison.Ordinal))
                Debug.Log("SummerCoop real input " + line);
        }
        private void Finish(CoopRuntime r, string outcome)
        { done = true; Record(r, "REAL_INPUT=" + outcome); RemoveReaders(); }
        private IEnumerator CaptureReady(CoopRuntime r)
        {
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            Application.CaptureScreenshot(Path.Combine(r.SaveRoot, step == 4 ? "real-input-door-ready.png" : "real-input-take-ready.png"));
        }
        private void InstallObservers()
        {
            harmony = HarmonyInstance.Create(HarmonyId); current = this;
            MethodInfo method = typeof(Fsm).GetMethod("SwitchState", BindingFlags.Instance | BindingFlags.NonPublic);
            harmony.Patch(method, null, new HarmonyMethod(typeof(RealInputProbe).GetMethod("ObserveSwitch")), null);
            harmony.Patch(typeof(CoopRuntime).GetMethod("RequestState"), new HarmonyMethod(typeof(RealInputProbe).GetMethod("ObserveRequest")), null, null);
            MethodInfo ray = typeof(ActionHelpers).GetMethod("DoMousePick", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (ray == null || NativeRayHit == null) throw new Exception("Native ray observer binding unavailable");
            harmony.Patch(ray, new HarmonyMethod(typeof(RealInputProbe).GetMethod("ObserveNativeRayBefore")), new HarmonyMethod(typeof(RealInputProbe).GetMethod("ObserveNativeRayAfter")), null);
            harmony.Patch(typeof(HutongGames.PlayMaker.Actions.MousePickEvent).GetMethod("DoMousePickEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), new HarmonyMethod(typeof(RealInputProbe).GetMethod("ObserveDoorPick")), null, null);
            harmony.Patch(typeof(Fsm).GetMethod("UpdateState", BindingFlags.Instance | BindingFlags.NonPublic), new HarmonyMethod(typeof(RealInputProbe).GetMethod("ObserveNativeUpdate")), null, null);
        }
        private float nextDoorUpdateDiagnostic;
        public static void ObserveNativeUpdate(Fsm __instance, FsmState state)
        {
            RealInputProbe probe = current; CoopRuntime runtime = CoopRuntime.Instance;
            if (probe == null || probe.done || runtime == null || runtime.Registry == null || Time.realtimeSinceStartup < probe.nextDoorUpdateDiagnostic) return;
            FsmBinding binding = runtime.Registry.Binding(__instance);
            if (binding == null || binding.Id != probe.doorId) return;
            probe.nextDoorUpdateDiagnostic = Time.realtimeSinceStartup + 2;
            probe.Record(runtime, "DOOR_UPDATE=mode=" + NativeHooks.UpdateMode + " requests=" + binding.Requests.Count + " state=" + (state == null ? "NONE" : state.Name) + " local=" + NativeHooks.CanRunLocally(state));
        }
        public static void ObserveNativeRayBefore(float distance, int layerMask)
        {
            RealInputProbe probe = current; CoopRuntime runtime = CoopRuntime.Instance;
            if (probe == null || probe.done || runtime == null || Camera.main == null) return;
            probe.nativeRaySample = false;
            if (Math.Abs(distance - 1f) <= 0.01f)
            { if (Time.realtimeSinceStartup < probe.nextNativeHandRayDiagnostic) return; probe.nextNativeHandRayDiagnostic = Time.realtimeSinceStartup + 2; }
            else if (Math.Abs(distance - 1.5f) <= 0.01f)
            { if (Time.realtimeSinceStartup < probe.nextNativeDoorRayDiagnostic) return; probe.nextNativeDoorRayDiagnostic = Time.realtimeSinceStartup + 2; }
            else return;
            probe.nativeRaySample = true;
            Camera camera = Camera.main;
            probe.Record(runtime, "NATIVE_RAY_BEFORE=frame=" + Time.frameCount + " range=" + distance + " mask=" + layerMask + " camera=" + camera.name + " position=" + camera.transform.position + " forward=" + camera.transform.forward + " mouse=" + Input.mousePosition + " target=" + probe.target);
        }
        public static void ObserveNativeRayAfter(float distance, int layerMask)
        {
            RealInputProbe probe = current; CoopRuntime runtime = CoopRuntime.Instance;
            if (probe == null || !probe.nativeRaySample || runtime == null || runtime.Registry == null) return;
            probe.nativeRaySample = false;
            RaycastHit hit = (RaycastHit)NativeRayHit.GetValue(null);
            string picked = hit.collider == null ? "NONE" : hit.collider.gameObject.name + " tag=" + hit.collider.gameObject.tag + " id=" + runtime.Registry.IdOf(hit.collider.gameObject) + " distance=" + hit.distance;
            probe.Record(runtime, "NATIVE_RAY_AFTER=frame=" + Time.frameCount + " range=" + distance + " mask=" + layerMask + " hit=" + picked);
        }
        public static void ObserveDoorPick(HutongGames.PlayMaker.Actions.MousePickEvent __instance)
        {
            RealInputProbe probe = current; CoopRuntime runtime = CoopRuntime.Instance;
            if (probe == null || probe.done || runtime == null || runtime.Registry == null) return;
            FsmBinding binding = runtime.Registry.Binding(__instance.Fsm);
            if (binding != null && binding.Id == probe.doorId) probe.doorRayCalls++;
        }
        public static void ObserveSwitch(Fsm __instance, FsmState toState)
        {
            RealInputProbe probe = current; CoopRuntime runtime = CoopRuntime.Instance;
            if (probe == null || probe.done || probe.hand == null || runtime == null || runtime.Registry == null || toState == null) return;
            if (__instance == probe.hand.Fsm) probe.Record(runtime, "HAND_SWITCH=" + toState.Name + " ACTIVE=" + __instance.ActiveStateName);
            else
            {
                FsmBinding binding = runtime.Registry.Binding(__instance);
                if (binding != null && binding.Id == probe.doorId) probe.Record(runtime, "DOOR_SWITCH=" + toState.Name + " ACTIVE=" + __instance.ActiveStateName + " LOCAL=" + NativeHooks.CanRunLocally(toState));
            }
        }
        public static void ObserveRequest(FsmBinding b, string state)
        {
            RealInputProbe probe = current; CoopRuntime runtime = CoopRuntime.Instance;
            if (probe == null || probe.done || runtime == null || runtime.Session == null || runtime.Session.World == null || b == null || b.Id != probe.doorId) return;
            GameObject player = GameObject.Find("PLAYER");
            probe.Record(runtime, "REQUEST_STATE=" + state + " recognized=" + b.Requests.Contains(state) + " entity=" + runtime.Session.World.Entities.ContainsKey(b.Id) + " distance=" + (player == null ? -1 : Vector3.Distance(player.transform.position, b.Component.transform.position)) + " OS_INPUT=" + Input.anyKey + "/" + Input.GetMouseButton(0) + "/" + Input.GetMouseButtonUp(0));
        }
        private void InstallReaders()
        {
            sampling = true; clickFrame = -1;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            AddReader(typeof(HutongGames.PlayMaker.Actions.GetMouseButtonDown).GetMethod("OnUpdate", flags));
            AddReader(typeof(HutongGames.PlayMaker.Actions.GetMouseButton).GetMethod("OnUpdate", flags));
            AddReader(typeof(HutongGames.PlayMaker.Actions.GetMouseButtonUp).GetMethod("OnUpdate", flags));
            AddReader(typeof(HutongGames.PlayMaker.Actions.MousePickEvent).GetMethod("DoMousePickEvent", flags));
            AddReader(typeof(ActionHelpers).GetMethod("DoMousePick", flags));
            AddReader(typeof(CoopRuntime).GetMethod("RequestState", flags));
        }
        private void AddReader(MethodInfo method)
        {
            if (method == null) throw new Exception("Native input method unavailable");
            harmony.Patch(method, null, null, new HarmonyMethod(typeof(RealInputProbe).GetMethod("InputReader"))); patched.Add(method);
        }
        public static IEnumerable<CodeInstruction> InputReader(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                MethodInfo method = instruction.operand as MethodInfo;
                if (method != null && method.DeclaringType == typeof(Input))
                {
                    string replacement = method.Name == "GetMouseButtonDown" ? "MouseDown" : method.Name == "GetMouseButtonUp" ? "MouseUp" : method.Name == "GetMouseButton" ? "MouseHeld" : method.Name == "get_mousePosition" ? "MousePosition" : "";
                    if (replacement != "") instruction.operand = typeof(RealInputProbe).GetMethod(replacement);
                }
                yield return instruction;
            }
        }
        private static int SampleButton { get { return current != null && current.step == 4 ? current.doorButton : 0; } }
        public static bool MouseDown(int button) { return sampling ? button == SampleButton && Time.frameCount == clickFrame : Input.GetMouseButtonDown(button); }
        public static bool MouseUp(int button) { return sampling ? button == SampleButton && Time.frameCount == clickFrame + 1 : Input.GetMouseButtonUp(button); }
        public static bool MouseHeld(int button) { return sampling ? button == SampleButton && Time.frameCount == clickFrame : Input.GetMouseButton(button); }
        public static Vector3 MousePosition() { return sampling ? new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0) : Input.mousePosition; }
        private void RemoveReaders()
        {
            sampling = false; clickFrame = -1;
            if (harmony != null)
            {
                harmony.Unpatch(typeof(Fsm).GetMethod("SwitchState", BindingFlags.Instance | BindingFlags.NonPublic), typeof(RealInputProbe).GetMethod("ObserveSwitch"));
                harmony.Unpatch(typeof(CoopRuntime).GetMethod("RequestState"), typeof(RealInputProbe).GetMethod("ObserveRequest"));
                MethodInfo ray = typeof(ActionHelpers).GetMethod("DoMousePick", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                harmony.Unpatch(ray, typeof(RealInputProbe).GetMethod("ObserveNativeRayBefore"));
                harmony.Unpatch(ray, typeof(RealInputProbe).GetMethod("ObserveNativeRayAfter"));
                harmony.Unpatch(typeof(HutongGames.PlayMaker.Actions.MousePickEvent).GetMethod("DoMousePickEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), typeof(RealInputProbe).GetMethod("ObserveDoorPick"));
                harmony.Unpatch(typeof(Fsm).GetMethod("UpdateState", BindingFlags.Instance | BindingFlags.NonPublic), typeof(RealInputProbe).GetMethod("ObserveNativeUpdate"));
                MethodInfo reader = typeof(RealInputProbe).GetMethod("InputReader");
                foreach (MethodInfo method in patched) harmony.Unpatch(method, reader);
                harmony = null; patched.Clear();
            }
            if (current == this) current = null;
        }
        private void OnDestroy() { RemoveReaders(); }
    }
}
