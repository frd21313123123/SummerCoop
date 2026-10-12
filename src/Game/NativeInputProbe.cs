using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    // Test the native hand/hook path, rather than submitting Take directly.
    // Attached only with an explicit report path and --summercoop-test-native-input yes.
    public sealed class NativeInputProbe : MonoBehaviour
    {
        private int step;
        private float began, sent;
        private PlayMakerFSM hand;
        private string partId, assemblyId, boltId, doorId;
        private int boltBefore;
        private Quaternion doorBefore;
        private string beerId;
        private int beerBefore;
        private bool doorReversed;
        private readonly List<string> lines = new List<string>();
        private bool done;
        private bool rejectionChecked;
        private Action<CommandResult> previous;
        private CommandResult result;
        internal static bool InputActive;
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
                    foreach (PlayMakerFSM f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>()) if (f.FsmName == "PickUp" && f.transform.root.name == "PLAYER") { hand = f; break; }
                    foreach (ObjectBinding b in r.Registry.Objects.Values) if (b.Object != null && b.Object.name == "starter(Clone)") { partId = b.Id; break; }
                    foreach (FsmBinding b in r.Registry.Fsms.Values)
                    {
                        FsmString expected = b.Component.FsmVariables.FindFsmString("HandChild");
                        if (b.Component.FsmName == "Assembly" && expected != null && expected.Value == "starter(Clone)") assemblyId = b.Id;
                        if (b.Component.FsmName == "Use" && b.Key.IndexOf("GarageDoors", StringComparison.Ordinal) >= 0 && b.Key.IndexOf("DoorLeft", StringComparison.Ordinal) >= 0) doorId = b.Id;
                    }
                    if (hand == null || partId == null) throw new Exception("Native hand/starter unavailable");
                    previous = r.Session.CommandCompleted;
                    r.Session.CommandCompleted = delegate(CommandResult c) { if (previous != null) previous(c); result = c; lines.Add("RESULT=" + c.Accepted + " " + c.Reason); };
                    foreach (FsmBinding b in r.Registry.Fsms.Values)
                        if (!b.Personal && b.Requests.Count > 0 && b.Component != null && b.Component.gameObject.activeInHierarchy && b.Key.IndexOf("YARD", StringComparison.Ordinal) >= 0 && b.Component.Fsm.ActiveState != null && !NativeHooks.SafeState(b.Component.Fsm.ActiveState))
                        {
                            if (lines.Count < 20) { string actions = ""; foreach (FsmStateAction action in b.Component.Fsm.ActiveState.Actions) actions += action.GetType().Name + ","; lines.Add("BLOCKED=" + b.Key + " state=" + b.Component.ActiveStateName + " actions=" + actions); }
                        }
                    lines.Add("HAND_BEFORE=" + hand.ActiveStateName);
                    Vector3 part = r.Registry.ObjectOf(partId).transform.position;
                    GameObject player = GameObject.Find("PLAYER"); player.transform.position = part - Vector3.up * 0.4f; player.transform.rotation = Quaternion.identity;
                    r.Session.SendPose(new Pose { Position = WorldRegistry.From(player.transform.position), Rotation = WorldRegistry.From(player.transform.rotation) });
                    sent = Time.realtimeSinceStartup; step = 1; return;
                }
                if (step == 1)
                {
                    if (Time.realtimeSinceStartup - sent < 1) return;
                    hand.FsmVariables.FindFsmGameObject("PickedObject").Value = r.Registry.ObjectOf(partId);
                    NativeAdapter.ExecuteState(new FsmBinding { Component = hand }, "Set pivot");
                    lines.Add("HAND_REQUEST=" + hand.ActiveStateName); sent = Time.realtimeSinceStartup; step = 2; return;
                }
                if (step == 2)
                {
                    if (result != null && !result.Accepted) throw new Exception("Native take rejected: " + result.Reason);
                    Player actor = r.Session.World.Players[r.Session.LocalId];
                    if (actor.Held == partId && hand.ActiveStateName == "Part picked")
                    {
                        GameObject part = r.Registry.ObjectOf(partId);
                        lines.Add("HAND_ACCEPTED=" + hand.ActiveStateName + " parent=" + (part.transform.parent == null ? "NONE" : part.transform.parent.name));
                        sent = Time.realtimeSinceStartup; step = 3; return;
                    }
                    if (Time.realtimeSinceStartup - sent > 10) throw new Exception("Native take stalled: held=" + actor.Held + " hand=" + hand.ActiveStateName + " status=" + r.StatusText);
                }
                if (step == 3)
                {
                    if (Time.realtimeSinceStartup - sent < 3) return;
                    GameObject part = r.Registry.ObjectOf(partId);
                    lines.Add("HAND_SETTLED=" + hand.ActiveStateName + " parent=" + (part.transform.parent == null ? "NONE" : part.transform.parent.name));
                    if (part.transform.parent == null || !part.transform.IsChildOf(GameObject.Find("PLAYER").transform)) throw new Exception("Server replication detached the locally held part from the hand");
                    result = null; NativeAdapter.ExecuteState(new FsmBinding { Component = hand }, "Drop part");
                    sent = Time.realtimeSinceStartup; step = 4; return;
                }
                if (step == 4)
                {
                    if (r.Session.World.Players[r.Session.LocalId].Held != "" || hand.ActiveStateName != "Look for object") { Timeout(r, "native drop"); return; }
                    lines.Add("DROP=PASS"); result = null;
                    Position(r, r.Registry.ObjectOf(partId).transform.position + Vector3.right * 30); sent = Time.realtimeSinceStartup; step = 5; return;
                }
                if (step == 5)
                {
                    // Keep a teleported diagnostic player above terrain while
                    // waiting for the pose; an old falling velocity survives a teleport.
                    Position(r, r.Registry.ObjectOf(partId).transform.position + (rejectionChecked ? Vector3.up * 2 : Vector3.right * 30 + Vector3.up * 10));
                    if (Time.realtimeSinceStartup - sent < 1) return;
                    lines.Add("TAKE_DISTANCE=" + Vector3.Distance(GameObject.Find("PLAYER").transform.position, r.Registry.ObjectOf(partId).transform.position));
                    hand.FsmVariables.FindFsmGameObject("PickedObject").Value = r.Registry.ObjectOf(partId);
                    NativeAdapter.ExecuteState(new FsmBinding { Component = hand }, "Set pivot"); sent = Time.realtimeSinceStartup; step = rejectionChecked ? 6 : 15; return;
                }
                if (step == 15)
                {
                    if (result == null || result.Accepted || hand.ActiveStateName != "Look for object")
                    {
                        if (Time.realtimeSinceStartup - sent > 15) throw new Exception("Rejected pickup did not restore input; hand=" + hand.ActiveStateName);
                        return;
                    }
                    lines.Add("REJECT_RECOVERY=PASS: " + result.Reason); rejectionChecked = true; result = null;
                    Position(r, r.Registry.ObjectOf(partId).transform.position - Vector3.up * 0.4f); sent = Time.realtimeSinceStartup; step = 5; return;
                }
                if (step == 6)
                {
                    if (r.Session.World.Players[r.Session.LocalId].Held != partId || hand.ActiveStateName != "Part picked") { Timeout(r, "second native pickup"); return; }
                    lines.Add("RETAKE=PASS"); sent = Time.realtimeSinceStartup; step = 7; return;
                }
                if (step == 7)
                {
                    Position(r, r.Registry.FsmOf(assemblyId).Component.transform.position - Vector3.up * 1.35f - Vector3.forward * 1.2f);
                    if (Time.realtimeSinceStartup - sent < 1) return;
                    // Align the actual native hand, rather than the former synthetic host offset.
                    GameObject player = GameObject.Find("PLAYER");
                    Vector3 snap = r.Registry.FsmOf(assemblyId).Component.transform.position;
                    player.transform.position += snap - r.Registry.ObjectOf(partId).transform.position;
                    lines.Add("ACTUAL_HAND_SNAP_DISTANCE=" + Vector3.Distance(snap, r.Registry.ObjectOf(partId).transform.position));
                    result = null; Boundary(r.Registry.FsmOf(assemblyId), "Wait"); sent = Time.realtimeSinceStartup; step = 8; return;
                }
                if (step == 8)
                {
                    if (r.Session.World.Players[r.Session.LocalId].Held != "") { Timeout(r, "native assembly boundary"); return; }
                    GameObject part = r.Registry.ObjectOf(partId);
                    if (part.tag == "PART" || part.transform.parent == null) { Timeout(r, "replicated installed part"); return; }
                    if (hand.ActiveStateName != "Look for object") { Timeout(r, "hand release after assembly"); return; }
                    lines.Add("ASSEMBLY=PASS: " + part.transform.parent.name + "; hand=" + hand.ActiveStateName);
                    foreach (FsmBinding b in r.Registry.Fsms.Values)
                        if (b.Component.transform.IsChildOf(part.transform) && b.Component.FsmName == "Screw" && b.Component.FsmVariables.FindFsmInt("Stage") != null) { boltId = b.Id; break; }
                    if (boltId == null) throw new Exception("Installed native bolt unavailable");
                    FsmBinding bolt = r.Registry.FsmOf(boltId); boltBefore = bolt.Component.FsmVariables.FindFsmInt("Stage").Value;
                    foreach (FsmState s in bolt.Component.FsmStates)
                    { string actions = ""; foreach (FsmStateAction a in s.Actions) actions += a.GetType().Name + ","; lines.Add("BOLT_STATE=" + s.Name + " local=" + NativeHooks.CanRunLocally(s) + " request=" + bolt.Requests.Contains(s.Name) + " " + actions); }
                    sent = Time.realtimeSinceStartup; step = 9; return;
                }
                if (step == 9)
                {
                    FsmBinding bolt = r.Registry.FsmOf(boltId); Position(r, bolt.Component.transform.position);
                    if (Time.realtimeSinceStartup - sent < 1) return;
                    FsmFloat wrench = FsmVariables.GlobalVariables.FindFsmFloat("ToolWrenchSize"), size = bolt.Component.FsmVariables.FindFsmFloat("BoltSize");
                    if (wrench != null && size != null) wrench.Value = size.Value;
                    result = null; Boundary(bolt, "Screw 2"); sent = Time.realtimeSinceStartup; step = 10; return;
                }
                if (step == 10)
                {
                    int stage = r.Registry.FsmOf(boltId).Component.FsmVariables.FindFsmInt("Stage").Value;
                    if (stage <= boltBefore) { Timeout(r, "native bolt boundary"); return; }
                    lines.Add("BOLT=PASS: " + boltBefore + " -> " + stage);
                    result = null; sent = Time.realtimeSinceStartup; step = 17; return;
                }
                if (step == 17)
                {
                    if (Time.realtimeSinceStartup - sent < 0.2f) return;
                    Boundary(r.Registry.FsmOf(boltId), "Unscrew 2"); sent = Time.realtimeSinceStartup; step = 16; return;
                }
                if (step == 16)
                {
                    int stage = r.Registry.FsmOf(boltId).Component.FsmVariables.FindFsmInt("Stage").Value;
                    if (stage != boltBefore) { Timeout(r, "native bolt unscrew boundary"); return; }
                    lines.Add("UNSCREW=PASS: 1 -> " + stage);
                    FsmBinding door = r.Registry.FsmOf(doorId); GameObject actual = door.Component.FsmVariables.FindFsmGameObject("Door").Value;
                    doorBefore = actual.transform.rotation; sent = Time.realtimeSinceStartup; step = 11; return;
                }
                if (step == 11)
                {
                    FsmBinding door = r.Registry.FsmOf(doorId); Position(r, door.Component.transform.position);
                    if (Time.realtimeSinceStartup - sent < 1) return;
                    result = null; Boundary(door, "Open"); sent = Time.realtimeSinceStartup; step = 12; return;
                }
                if (step == 12)
                {
                    FsmBinding door = r.Registry.FsmOf(doorId); GameObject actual = door.Component.FsmVariables.FindFsmGameObject("Door").Value;
                    Position(r, door.Component.transform.position);
                    float angle = Quaternion.Angle(doorBefore, actual.transform.rotation);
                    if (angle < .1f && !doorReversed && Time.realtimeSinceStartup - sent > 3 && door.Requests.Contains("Close"))
                    { doorReversed = true; result = null; Boundary(door, "Close"); sent = Time.realtimeSinceStartup; return; }
                    if (angle < 0.1f) { Timeout(r, "native door boundary/convergence"); return; }
                    lines.Add("DOOR=PASS: replicated rotation changed " + angle);
                    foreach (FsmBinding binding in r.Registry.Fsms.Values)
                        if (binding.Key.IndexOf("switch_hallway", StringComparison.Ordinal) >= 0 && binding.Component.Fsm.GetState("Wait player") != null)
                        {
                            FsmState input = binding.Component.Fsm.GetState("Wait player");
                            if (!NativeHooks.CanRunLocally(input) || !NativeHooks.UpdatePrefix(binding.Component.Fsm, input)) throw new Exception("Mixed native input state is still blocked");
                            lines.Add("MIXED_INPUT=PASS: local polling permitted, shared setup effects filtered"); break;
                        }
                    foreach (FsmBinding beer in r.Registry.Fsms.Values)
                    {
                        FsmInt consumed = beer.Component.FsmVariables.FindFsmInt("DestroyedBottles");
                        if (beer.Component.FsmName != "Use" || consumed == null || consumed.Value >= 23 || !beer.Component.gameObject.activeInHierarchy || beer.Component.transform.root.GetComponent<Rigidbody>() == null) continue;
                        beerId = beer.Id; beerBefore = consumed.Value; break;
                    }
                    if (beerId == null) throw new Exception("No active beer case available for native Use proof");
                    Position(r, r.Registry.FsmOf(beerId).Component.transform.position + Vector3.up * .2f);
                    result = null; sent = Time.realtimeSinceStartup; step = 13; return;
                }
                if (step == 13)
                {
                    if (Time.realtimeSinceStartup - sent < 1) return;
                    InputActive = true;
                    try { if (!NativeUseInput.Activate(r.Registry.FsmOf(beerId))) throw new Exception("Native configured Use button is not routable"); }
                    finally { InputActive = false; }
                    sent = Time.realtimeSinceStartup; step = 14; return;
                }
                if (step == 14)
                {
                    int count = r.Registry.FsmOf(beerId).Component.FsmVariables.FindFsmInt("DestroyedBottles").Value;
                    if (count != beerBefore + 1) { Timeout(r, "native beer Use/count convergence"); return; }
                    if (Time.realtimeSinceStartup - sent < 9) return;
                    lines.Add("BEER_USE=PASS: configured Use -> native checks -> host consumes exactly one bottle");
                    GameObject keys = GameObject.Find("GifuKeys");
                    if (keys != null)
                    {
                        PlayMakerFSM pickup = PlayMakerFSM.FindFsmOnGameObject(keys, "Use");
                        if (pickup != null) { NativeAdapter.ExecuteState(new FsmBinding { Component = pickup }, "State 1"); r.SaveKeysNow(); lines.Add("PERSONAL_KEY_PICKUP=" + FsmVariables.GlobalVariables.FindFsmInt("PlayerKeyGifu").Value); }
                    }
                    Finish(r, "PASS: native hand take/drop, rejected take recovery/retake, assembly hook, hand release, bolt tighten/unscrew, door hook, mixed input and configured beer Use");
                }
            }
            catch (Exception e) { Finish(r, "FAIL: " + e); }
        }
        private static void Boundary(FsmBinding binding, string state)
        {
            InputActive = true;
            try { NativeAdapter.ExecuteState(binding, state); }
            finally { InputActive = false; }
        }
        private void Timeout(CoopRuntime r, string operation)
        {
            if (result != null && !result.Accepted) throw new Exception(operation + " rejected: " + result.Reason);
            if (Time.realtimeSinceStartup - sent > 15) throw new Exception(operation + " stalled; hand=" + hand.ActiveStateName + "; status=" + r.StatusText);
        }
        private static void Position(CoopRuntime r, Vector3 position)
        {
            GameObject player = GameObject.Find("PLAYER"); player.transform.position = position; player.transform.rotation = Quaternion.identity;
            if (Camera.main != null) Camera.main.transform.rotation = Quaternion.identity;
            r.Session.SendPose(new Pose { Position = WorldRegistry.From(position), Rotation = WorldRegistry.From(Quaternion.identity), Pitch = 0 });
        }
        private void Finish(CoopRuntime r, string outcome)
        {
            done = true; lines.Add("NATIVE_INPUT=" + outcome);
            File.WriteAllLines(Path.Combine(r.SaveRoot, "native-input-proof.txt"), lines.ToArray());
            Debug.Log("SummerCoop " + outcome); if (previous != null) r.Session.CommandCompleted = previous;
            StartCoroutine(CaptureMenu(r));
        }
        private IEnumerator CaptureMenu(CoopRuntime r)
        {
            typeof(CoopRuntime).GetField("visible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(r, true);
            yield return new WaitForEndOfFrame(); yield return new WaitForEndOfFrame();
            Application.CaptureScreenshot(Path.Combine(r.SaveRoot, "native-menu.png"));
            yield return new WaitForEndOfFrame();
            int width = Screen.width, height = Screen.height;
            Screen.SetResolution(640, 480, false);
            for (int i = 0; i < 12; i++) yield return new WaitForEndOfFrame();
            Application.CaptureScreenshot(Path.Combine(r.SaveRoot, "native-menu-640.png"));
            yield return new WaitForEndOfFrame(); Screen.SetResolution(width, height, false);
            float deadline = Time.realtimeSinceStartup + 120;
            while (!File.Exists(CoopRuntime.Argument("--summercoop-test-report")) && Time.realtimeSinceStartup < deadline) yield return null;
            if (!File.Exists(CoopRuntime.Argument("--summercoop-test-report"))) yield break;
            r.Leave("Проверка меню завершения сессии");
            while (Application.loadedLevelName != "MainMenu") yield return null;
            for (int i = 0; i < 12; i++) yield return new WaitForEndOfFrame();
            Application.CaptureScreenshot(Path.Combine(r.SaveRoot, "native-main-menu.png"));
            yield return new WaitForEndOfFrame(); Screen.SetResolution(640, 480, false);
            for (int i = 0; i < 12; i++) yield return new WaitForEndOfFrame();
            Application.CaptureScreenshot(Path.Combine(r.SaveRoot, "native-main-menu-640.png"));
            yield return new WaitForEndOfFrame(); Screen.SetResolution(width, height, false);
        }
    }
}
