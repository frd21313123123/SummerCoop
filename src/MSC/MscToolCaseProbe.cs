using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Native lid and tool-pickup QA, only in explicitly requested test games.
    public static class MscToolCaseProbe
    {
        public static readonly List<string> Results = new List<string>();
        static bool done;
        static float menuBegan;
        static int menuStep, spannerId, ratchetId;
        static bool menuFailed, menuCaptured;
        static float menuCaptureAt;
        public static void Update()
        {
            if (MscRuntime.Argument("--summercoop-test-toolcases-menu") == "yes") { TestMenu(); return; }
            if (done || MscRuntime.Argument("--summercoop-test-toolcases") != "yes" || MscToolCases.Count < Session.Players.Count) return;
            done = true;
            try
            {
                int index = Math.Min(Session.LocalId, MscToolCases.Count - 1);
                Rigidbody body = Props.BodyOf(MscToolCases.Identity(index));
                if (body == null) throw new Exception("Case is not registered with Props");
                Transform tools = body.transform.Find("Tools");
                Rigidbody original = Props.BodyOf(MscToolCases.Identity(0));
                Transform originalTools = original.transform.Find("Tools");
                if (tools == null || tools.childCount == 0 || tools.childCount != originalTools.childCount) throw new Exception("Incomplete native tool inventory");
                Transform top = body.transform.Find("Pivot/top");
                PlayMakerFSM lid = Game.FsmOn(top.gameObject, "Use");
                bool originalActive = originalTools.gameObject.activeSelf;
                tools.gameObject.SetActive(false);
                Game.SetState(lid, "Open");
                if (!tools.gameObject.activeSelf || index > 0 && originalTools.gameObject.activeSelf != originalActive) throw new Exception("Lid references another case's tools");
                Transform wrench = tools.Find("spanner(11)(Clone)");
                PlayMakerFSM pick = Game.FindFsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/2Spanner/Pick", "PickUp");
                if (wrench == null || pick == null) throw new Exception("Native wrench/picker missing");
                pick.FsmVariables.GetFsmGameObject("PickedTool").Value = wrench.gameObject;
                Game.SetState(pick, "Spanner");
                GameObject oldTool = pick.FsmVariables.GetFsmGameObject("OldTool").Value;
                GameObject hand = pick.FsmVariables.GetFsmGameObject("HandSpanner").Value;
                if (oldTool != wrench.gameObject || hand == null || !hand.activeSelf) throw new Exception("Native wrench pickup failed");
                Results.Add("ToolCaseNative=PASS;Case=" + index + ";Tools=" + tools.childCount + ";LidOwnTools=True;WrenchPicked=True");
            }
            catch (Exception e) { Results.Add("ToolCaseNative=FAIL;" + e); }
        }

        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Success(string message) { Results.Add("ToolCaseMenu=PASS;" + message); Log.Info("MSC tool case menu QA: " + message); }
        static void TestMenu()
        {
            if (menuFailed) return;
            if (menuBegan == 0) menuBegan = Time.realtimeSinceStartup;
            float elapsed = Time.realtimeSinceStartup - menuBegan;
            try
            {
                if (menuStep == 0)
                {
                    if (Session.IsHost)
                    {
                        Check(MscToolCases.Remove(0) != null, "Original case can be deleted");
                        Check(MscToolCases.Spawn(0) == null, "Spanner spawn failed");
                        spannerId = MscToolCases.IdAt(MscToolCases.Count - 1);
                        Check(MscToolCases.Spawn(1) == null, "Ratchet spawn failed");
                        ratchetId = MscToolCases.IdAt(MscToolCases.Count - 1);
                        Success("HostSpawnedBothTypes");
                    }
                    else Check(MscToolCases.Spawn(1) != null && MscToolCases.RemoveAll() != null, "Guest management was accepted");
                    menuStep = 1;
                }
                if (!menuCaptured && elapsed > 5)
                {
                    menuCaptured = true; Menu.ShowToolCases();
                    menuCaptureAt = Time.realtimeSinceStartup + .5f;
                }
                if (menuCaptureAt > 0 && Time.realtimeSinceStartup >= menuCaptureAt)
                {
                    menuCaptureAt = -1;
                    Application.CaptureScreenshot(MscRuntime.Argument("--summercoop-test-report").Replace("-report.txt", "-toolcases-menu.png"));
                }
                if (menuStep == 1 && elapsed > 8)
                {
                    Check(MscToolCases.Count == 4, "Expected original, automatic and two manual cases, got " + MscToolCases.Count);
                    int ordinary = 0, special = 0;
                    for (int i = 0; i < MscToolCases.Count; i++)
                    {
                        int id = MscToolCases.IdAt(i), kind = MscToolCases.KindAt(i);
                        if (kind == 0) ordinary++; else { special++; ratchetId = id; }
                        if (kind == 0 && id > 1) spannerId = id;
                        Check(Props.BodyOf(MscToolCases.Identity(id)) != null, "Untracked case " + id);
                    }
                    Check(ordinary == 3 && special == 1, "Wrong native case types");
                    NativeCase(spannerId, false); NativeCase(ratchetId, true);
                    Success("BothTypesReplicated;NativeLidsAndTools"); menuStep = 2;
                }
                if (menuStep == 2 && elapsed > 16)
                {
                    if (Session.IsHost) Check(MscToolCases.Remove(spannerId) == null, "Single deletion failed");
                    menuStep = 3;
                }
                if (menuStep == 3 && elapsed > 21)
                {
                    Check(MscToolCases.Count == 3 && Props.BodyOf(MscToolCases.Identity(spannerId)) == null, "Single deletion did not replicate");
                    Check(Props.BodyOf(MscToolCases.Identity(ratchetId)) != null, "Single deletion removed ratchet case");
                    Success("SingleDeletionReplicated"); menuStep = 4;
                }
                if (menuStep == 4 && elapsed > 25)
                {
                    if (Session.IsHost) Check(MscToolCases.RemoveAll() == null, "Bulk deletion failed");
                    menuStep = 5;
                }
                if (menuStep == 5 && elapsed > 30)
                {
                    Check(MscToolCases.Count == 1 && Props.BodyOf(MscToolCases.Identity(0)) != null, "Deleted cases respawned or original was lost");
                    Check(Props.BodyOf(MscToolCases.Identity(ratchetId)) == null, "Ratchet deletion failed");
                    PlayMakerFSM pick = Game.FindFsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/2Spanner/Pick", "PickUp");
                    Check(pick.FsmVariables.GetFsmGameObject("OldTool").Value == null, "Deleted tool selection retained");
                    Success("BulkDeletionReplicated;OriginalPreserved;SelectionCleared;NoAutomaticRespawn");
                    menuStep = 6;
                }
                if (menuStep == 6 && elapsed > 35)
                {
                    if (Session.IsHost) Check(MscToolCases.Spawn(1) == null, "Ratchet respawn after deletion failed");
                    menuStep = 7;
                }
                if (menuStep == 7 && elapsed > 41)
                {
                    Check(MscToolCases.Count == 2 && MscToolCases.KindAt(1) == 1 && MscToolCases.IdAt(1) > ratchetId, "Deleted identity reused or ratchet missing");
                    NativeCase(MscToolCases.IdAt(1), true);
                    Menu.Open = false; Success("RespawnUsesNewIdentity;NativeRatchetWorks"); menuStep = 8;
                }
            }
            catch (Exception e) { menuFailed = true; Results.Add("ToolCaseMenu=FAIL;" + e); Log.Error("MSC tool case menu QA: " + e); }
        }
        static void NativeCase(int id, bool ratchet)
        {
            GameObject box = Props.BodyOf(MscToolCases.Identity(id)).gameObject;
            Transform tools = box.transform.Find("Tools"); Check(tools != null && tools.childCount > 0, "Tools missing in case " + id);
            Transform top = box.transform.Find(ratchet ? "Hinge/Pivot/top" : "Pivot/top");
            Check(top != null, "Lid missing in case " + id);
            tools.gameObject.SetActive(false); Game.SetState(Game.FsmOn(top.gameObject, "Use"), "Open");
            Check(tools.gameObject.activeSelf, "Lid opens wrong inventory " + id);
            Transform tool = null;
            foreach (Transform child in tools) if (child.name.StartsWith(ratchet ? "socket(11)" : "spanner(11)", StringComparison.Ordinal)) { tool = child; break; }
            if (tool == null) { string names = ""; foreach (Transform child in tools) names += child.name + ","; throw new Exception("Native tool missing: " + names); }
            PlayMakerFSM pick = Game.FindFsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/2Spanner/Pick", "PickUp");
            if (!pick.Fsm.Initialized) pick.Fsm.Init(pick);
            pick.FsmVariables.GetFsmGameObject("PickedTool").Value = tool.gameObject;
            Game.SetState(pick, ratchet ? "Socket" : "Spanner");
            Check(pick.FsmVariables.GetFsmGameObject("OldTool").Value == tool.gameObject, "Native pickup failed for case " + id);
            GameObject hand = pick.FsmVariables.GetFsmGameObject(ratchet ? "HandSocket" : "HandSpanner").Value;
            Check(hand != null && hand.activeSelf, "Hand tool hidden for case " + id);
        }
    }
}
