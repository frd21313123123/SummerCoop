using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MWCoop
{
    // Explicit functional test only, never attached to a performance measurement.
    public static class MscOptimizationProbe
    {
        static bool done;
        static readonly List<string> results = new List<string>();
        public static IEnumerable<string> Results { get { return results; } }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        public static void Update()
        {
            if (done || MscRuntime.Argument("--summercoop-test-optimization") != "yes") return;
            Rigidbody car = VehicleSync.Body("SATSUMA");
            if (car == null) return;
            done = true;
            const string id = "summercoop:test:hierarchy-cache";
            GameObject go = null, holder = null;
            IDictionary props = (IDictionary)typeof(Props).GetField("props", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            IDictionary bodies = (IDictionary)typeof(Props).GetField("byBody", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            IDictionary world = (IDictionary)typeof(Props).GetField("worldKeys", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Rigidbody body = null;
            object trackedProp = null;
            IList following = (IList)typeof(Props).GetField("following", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            try
            {
                go = new GameObject("SummerCoop hierarchy cache test");
                holder = new GameObject("SummerCoop hierarchy cache parent");
                body = go.AddComponent<Rigidbody>(); body.isKinematic = true;
                Props.Adopt(go, id);
                object prop = props[id];
                trackedProp = prop;
                MethodInfo mounted = typeof(Props).GetMethod("Mounted", BindingFlags.Static | BindingFlags.NonPublic);
                Func<bool> isMounted = delegate { return (bool)mounted.Invoke(null, new[] { prop }); };
                Check(!isMounted(), "Loose body was classified as mounted");
                go.transform.parent = car.transform; Check(isMounted() && isMounted(), "Mount cache missed assembly");
                go.transform.parent = holder.transform; Check(!isMounted(), "Mount cache retained removed body");
                holder.transform.parent = car.transform; Check(isMounted(), "Ancestor reparent did not invalidate cache");
                bodies.Remove(body); UnityEngine.Object.DestroyImmediate(body);
                body = go.AddComponent<Rigidbody>(); body.isKinematic = true; Props.Adopt(go, id);
                Check(isMounted(), "Recreated Rigidbody lost mounting membership");
                go.transform.parent = null; Check(!isMounted(), "Recreated body retained old parent");
                MethodInfo setKinematic = typeof(Props).GetMethod("SetKinematic", BindingFlags.Static | BindingFlags.NonPublic);
                body.isKinematic = false; setKinematic.Invoke(null, new object[] { prop, true });
                bodies.Remove(body); UnityEngine.Object.DestroyImmediate(body);
                body = go.AddComponent<Rigidbody>(); Props.Adopt(go, id);
                Check(body.isKinematic, "Recreated remote body did not regain remote control");
                setKinematic.Invoke(null, new object[] { prop, false });
                Check(!body.isKinematic, "Recreated body restored the previous body's physics state");
                setKinematic.Invoke(null, new object[] { prop, true });
                Type propType = prop.GetType();
                propType.GetField("RemoteBy").SetValue(prop, 1);
                bodies.Remove(body); propType.GetField("Body").SetValue(prop, null);
                Props.Adopt(go, id);
                Check(following.Contains(prop), "Reappearing remote body was omitted from interpolation/timeouts");
                setKinematic.Invoke(null, new object[] { prop, false });
                Check(!body.isKinematic, "Reindexing the same body lost its original physics state");
                bodies.Remove(body); UnityEngine.Object.DestroyImmediate(body); body = null;
                Check(!isMounted(), "Destroyed body survived cache validation");
                CheckRepairCache(go);
                CheckPartOwnership();
                results.Add("OptimizationCache=PASS loose,mounted,cached,removed,ancestor,recreated,remote-rebind,same-body-reindex,inactive-following,destroyed");
            }
            catch (Exception e) { results.Add("OptimizationCache=FAIL " + e); throw; }
            finally
            {
                props.Remove(id); if (body != null) bodies.Remove(body); if (go != null) world.Remove(go);
                if (trackedProp != null) following.Remove(trackedProp);
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
                if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
            }
        }
        static void CheckPartOwnership()
        {
            GameObject go = new GameObject("SummerCoop owner reservation test");
            var created = new List<PlayMakerFSM>();
            IDictionary owners = (IDictionary)typeof(Replay).GetField("owners", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            try
            {
                foreach (string name in new[] { "Assembly", "Removal", "Screw" })
                {
                    PlayMakerFSM f = go.AddComponent<PlayMakerFSM>(); created.Add(f); f.enabled = false; f.FsmName = name;
                    if (name == "Screw") f.FsmVariables.IntVariables = new[] { new HutongGames.PlayMaker.FsmInt { Name = "Stage" } };
                    Check(Replay.ClaimedByOther(f, "quetes"), "Unindexed native part FSM was available to generic discovery");
                    Check(!Replay.Claim(f, "quetes") && !Replay.Claim(f, "monde"), "Generic discovery stole a native part FSM");
                    Check(Replay.Owner(f) == null, "Reservation installed a hook before native part discovery");
                    Check(Replay.Claim(f, "msc-parts") && Replay.Claim(f, "msc-parts"), "Reserved part owner cannot claim its FSM");
                }
                PlayMakerFSM other = go.AddComponent<PlayMakerFSM>(); created.Add(other); other.enabled = false; other.FsmName = "Logic";
                Check(Replay.Claim(other, "quetes") && !Replay.Claim(other, "monde"), "Ordinary single-owner rules changed");
                go.name = "bucket lid(itemx)";
                PlayMakerFSM lid = go.AddComponent<PlayMakerFSM>(); created.Add(lid); lid.enabled = false; lid.FsmName = "Removal";
                Check(Replay.Claim(lid, "msc-brewing"), "Part reservation stole the native brewing lid");
                results.Add("PartOwnership=PASS unindexed-assembly,removal,bolt,generic-owner,brewing-lid");
            }
            finally
            {
                foreach (PlayMakerFSM f in created) owners.Remove(f);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
        static void CheckRepairCache(GameObject go)
        {
            var activation = new HutongGames.PlayMaker.Actions.ActivateGameObject {
                activate = true, recursive = true, resetOnExit = false
            };
            var enable = new HutongGames.PlayMaker.Actions.EnableBehaviour {
                behaviour = "Light", enable = true, resetOnExit = false
            };
            GameObject child = null, other = null;
            Light light = null; Camera camera = null;
            try
            {
                go.SetActive(true);
                Check(MscRepairCache.AlreadyApplied(activation, go), "Matching activation was not recognized");
                child = new GameObject("SummerCoop repair child"); child.transform.parent = go.transform; child.SetActive(false);
                Check(!MscRepairCache.AlreadyApplied(activation, go), "Recursive repair missed a new inactive child");
                activation.recursive.Value = false;
                Check(MscRepairCache.AlreadyApplied(activation, go), "Nonrecursive activation changed child semantics");
                activation.recursive.Value = true; child.SetActive(true);
                Check(MscRepairCache.AlreadyApplied(activation, go), "Recursive activation retained stale child state");
                activation.resetOnExit = true;
                Check(!MscRepairCache.AlreadyApplied(activation, go), "Activation bypassed OnExit bookkeeping");
                light = go.AddComponent<Light>(); light.enabled = true;
                Check(MscRepairCache.AlreadyApplied(enable, go), "Enabled behaviour was not cached");
                light.enabled = false;
                Check(!MscRepairCache.AlreadyApplied(enable, go), "Disabled behaviour was not noticed");
                UnityEngine.Object.DestroyImmediate(light); light = go.AddComponent<Light>(); light.enabled = true;
                Check(MscRepairCache.AlreadyApplied(enable, go), "Recreated behaviour retained a destroyed component");
                other = new GameObject("SummerCoop repair target");
                Check(!MscRepairCache.AlreadyApplied(enable, other), "Target replacement retained the previous component");
                var added = other.AddComponent<Light>(); added.enabled = true;
                Check(MscRepairCache.AlreadyApplied(enable, other), "Missing behaviour was cached permanently");
                enable.behaviour.Value = "Camera";
                Check(!MscRepairCache.AlreadyApplied(enable, go), "Behaviour type change reused an old component");
                camera = go.AddComponent<Camera>(); camera.enabled = true;
                Check(MscRepairCache.AlreadyApplied(enable, go), "New behaviour type was not discovered");
                enable.component = light;
                Check(MscRepairCache.AlreadyApplied(enable, other), "Explicit component selection was ignored");
                enable.resetOnExit.Value = true;
                Check(!MscRepairCache.AlreadyApplied(enable, other), "Behaviour bypassed OnExit bookkeeping");
                results.Add("RepairCache=PASS recursive,child-spawn,target-change,component-recreate,missing-component,resetOnExit");
            }
            finally
            {
                MscRepairCache.Forget(enable);
                if (camera != null) UnityEngine.Object.DestroyImmediate(camera);
                if (light != null) UnityEngine.Object.DestroyImmediate(light);
                if (child != null) UnityEngine.Object.DestroyImmediate(child);
                if (other != null) UnityEngine.Object.DestroyImmediate(other);
            }
        }
    }
}
