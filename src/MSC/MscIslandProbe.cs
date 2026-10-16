using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;
namespace MWCoop
{
    public static class MscIslandProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static PlayMakerFSM carrier, water, fill, lamp;
        static Vector3 bucketPosition;
        static Vector3 rocketStart;
        static bool At(int n, float t, float at) { return t >= at && steps.Add(n); }
        static PlayMakerFSM Fsm(string path, string name) { return Game.FsmOn(Game.FindAny(path), name); }
        static List<GameObject> Wood(bool intact)
        {
            var result = new List<GameObject>();
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                if (Props.ItemId(body.gameObject).StartsWith("b", StringComparison.Ordinal) && body.name.StartsWith(intact ? "log(Clone)" : "firewood", StringComparison.Ordinal)) result.Add(body.gameObject);
            return result;
        }
        static void AddWood()
        {
            var wood = Wood(false); if (wood.Count == 0) throw new InvalidOperationException("No split wood for carrier");
            Home.MarkHeld(wood[0]); carrier.FsmVariables.GetFsmGameObject("FirewoodObject").Value = wood[0]; Game.SetState(carrier, "Parent");
        }
        static void TakeWood() { var f = Game.FsmOn(carrier.transform.parent.Find("PickupTrigger").gameObject, "Use"); Game.SetState(f, "Bool test"); }
        static void Count(string channel, string name, int expected)
        {
            int visible = 0; foreach (Transform wood in carrier.transform.parent.Find("Woods")) if (wood.gameObject.activeSelf) visible++;
            int count = carrier.FsmVariables.GetFsmInt("WoodsIndex").Value;
            MscRegressionProbe.Record(channel, name, count == expected && visible == expected, "Count=" + count + ";Visible=" + visible);
        }
        public static void Update(float t, string channel)
        {
            if (At(0, t, 0))
            {
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    var f = item as PlayMakerFSM; if (f == null || f.hideFlags != HideFlags.None) continue;
                    if (f.FsmName == "Trigger" && f.FsmVariables.FindFsmString("UniqueTagWood") != null && f.FsmVariables.GetFsmString("UniqueTagWood").Value == "woodcarrier1wood") carrier = f;
                    if (f.FsmName == "Level" && f.name == "Water" && f.transform.parent.name == "water bucket(itemx)" && f.transform.position.x < -400) water = f;
                }
                fill = Game.FsmOn(water.transform.parent.Find("BucketTrigger").gameObject, "Level"); bucketPosition = water.transform.parent.position;
                lamp = Fsm("ITEMS/lantern(itemx)", "Use");
            }
            if (At(1, t, 2) && Session.IsHost) Game.SetState(Fsm("COTTAGE/Logging/Logwall", "Use"), "Create log");
            if (At(2, t, 10)) MscRegressionProbe.Record(channel, "log-no-auto-split", Wood(true).Count == 2 && Wood(false).Count == 0, "JoinedBodies=" + Wood(true).Count + ";Split=" + Wood(false).Count);
            if (At(3, t, 12) && Session.IsHost) foreach (var wood in Wood(true)) { var joint = wood.GetComponent<FixedJoint>(); if (joint != null) UnityEngine.Object.Destroy(joint); }
            if (At(4, t, 16)) MscRegressionProbe.Record(channel, "log-axe-split", Wood(false).Count == 2, "Split=" + Wood(false).Count);
            if (At(5, t, 18) && Session.IsHost) AddWood();
            if (At(6, t, 24)) Count(channel, "carrier-host-add", 1);
            if (At(7, t, 39) && !Session.IsHost) TakeWood();
            if (At(8, t, 44)) Count(channel, "carrier-guest-take", 0);
            if (At(9, t, 59) && !Session.IsHost) AddWood();
            if (At(10, t, 65)) Count(channel, "carrier-guest-add", 1);
            if (At(11, t, 67) && Session.IsHost) TakeWood();
            if (At(12, t, 71)) Count(channel, "carrier-host-take", 0);
            if (At(13, t, 72) && Session.IsHost) Game.SetState(lamp, "ON");
            if (At(14, t, 76)) MscRegressionProbe.Record(channel, "lamp-on", lamp.transform.Find("light").gameObject.activeSelf, "State=" + lamp.ActiveStateName);
            if (At(15, t, 77) && !Session.IsHost) Game.SetState(lamp, "OFF");
            if (At(16, t, 80)) MscRegressionProbe.Record(channel, "lamp-off", !lamp.transform.Find("light").gameObject.activeSelf, "State=" + lamp.ActiveStateName);
            if (At(17, t, 81) && Session.IsHost)
            {
                Home.MarkHeld(water.transform.parent.gameObject); var body = water.transform.parent.GetComponent<Rigidbody>(); body.isKinematic = true;
                body.position = new Vector3(bucketPosition.x, -5, bucketPosition.z); Game.SetState(fill, "Lake");
            }
            if (At(18, t, 82) && Session.IsHost) { water.transform.parent.GetComponent<Rigidbody>().position = bucketPosition; Game.SetState(fill, "Wait"); }
            if (At(19, t, 86)) MscRegressionProbe.Record(channel, "island-bucket-fill", FireTools.GetF(water, "Water", 0) > 4.9f, "Water=" + FireTools.GetF(water, "Water", 0));
            if (At(26, t, 85) && !Session.IsHost) Fsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/SaunaThrow", "Use").gameObject.SetActive(true);
            if (At(20, t, 87) && !Session.IsHost)
            {
                var dipper = Fsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/SaunaThrow", "Use");
                foreach (string name in new[] { "Water is", "Water is 2" })
                    foreach (FsmStateAction action in FireTools.Actions(dipper, dipper.Fsm.GetState(name)))
                    {
                        var subtract = action as HutongGames.PlayMaker.Actions.SubtractFsmFloat;
                        if (subtract != null && subtract.gameObject.GameObject.Value == water.gameObject) { Game.SetState(dipper, name); return; }
                    }
            }
            if (At(21, t, 92)) MscRegressionProbe.Record(channel, "island-dipper-water", Mathf.Abs(FireTools.GetF(water, "Water", 0) - 4.8f) < .02f, "Water=" + FireTools.GetF(water, "Water", 0));
            if (At(22, t, 94) && !Session.IsHost)
            {
                GameObject rocket = Props.ObjectOf("msc-rocket-1"); Game.SetState(Game.FsmOn(rocket.transform.Find("Trigger").gameObject, "Use"), "ON 3");
            }
            if (At(27, t, 93))
            {
                GameObject rocket = Props.ObjectOf("msc-rocket-1");
                if (Session.IsHost)
                {
                    var body = rocket.GetComponent<Rigidbody>(); body.rotation = Quaternion.identity;
                    body.constraints = RigidbodyConstraints.FreezeRotation;
                    // The bag's native spawn point is under the cottage roof.
                    // Launch above it to test unobstructed native thrust.
                    body.position += Vector3.up * 12;
                }
                rocketStart = rocket.transform.position;
            }
            if (At(23, t, 96)) Rocket(channel, "firework-fuse", "ON 3");
            if (At(24, t, 100.5f)) Rocket(channel, "firework-flight", "ON");
            if (At(25, t, 105)) Rocket(channel, "firework-explosion", "ON 2");
        }
        static void Rocket(string channel, string name, string state)
        {
            GameObject rocket = Props.ObjectOf("msc-rocket-1"); var use = rocket != null ? Game.FsmOn(rocket.transform.Find("Trigger").gameObject, "Use") : null;
            var body = rocket != null ? rocket.GetComponent<Rigidbody>() : null;
            float distance = rocket != null ? Vector3.Distance(rocketStart, rocket.transform.position) : 0;
            bool flying = name != "firework-flight" || rocket != null && rocket.transform.position.y > rocketStart.y + 3;
            if (name == "firework-explosion") flying = rocket != null && !MscFireworks.Flying(rocket);
            string force = "";
            if (use != null) foreach (FsmStateAction action in FireTools.Actions(use, use.Fsm.GetState("ON"))) if (action is HutongGames.PlayMaker.Actions.AddForce) { var f = (HutongGames.PlayMaker.Actions.AddForce)action; force = action.Enabled + ":yNone=" + f.y.IsNone + ":" + Recon.Describe(action); }
            MscRegressionProbe.Record(channel, name, use != null && use.ActiveStateName == state && flying, "State=" + (use != null ? use.ActiveStateName : "absent") + ";Position=" + (rocket != null ? rocket.transform.position.ToString("F2") : "absent") + ";Kinematic=" + (body != null && body.isKinematic) + ";Mass=" + (body != null ? body.mass : 0) + ";Distance=" + distance + ";Constraints=" + (body != null ? body.constraints.ToString() : "?") + ";Joints=" + (body != null ? body.GetComponents<Joint>().Length : 0) + ";Parent=" + (rocket != null && rocket.transform.parent != null ? rocket.transform.parent.name : "none") + ";Fixed=" + (use != null && use.Fsm.HandleFixedUpdate) + ";Drag=" + (body != null ? body.drag : 0) + ";TimeScale=" + Time.timeScale + ";Force=" + force);
        }
    }
}
