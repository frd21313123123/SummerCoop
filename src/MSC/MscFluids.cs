using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    public static class MscFluids
    {
        static readonly HashSet<PlayMakerFSM> hooked = new HashSet<PlayMakerFSM>();
        static float scan;
        static HarmonyInstance harmony;
        static bool CanPour(FsmStateAction __instance)
        {
            if (!Session.Active || __instance == null || __instance.Fsm == null || !__instance.Fsm.GameObjectName.StartsWith("CapTrigger_", StringComparison.Ordinal)) return true;
            var source = __instance.Fsm.Variables.FindFsmGameObject("FluidTrigger");
            GameObject go = source != null && source.Value != null && source.Value.transform.parent != null ? source.Value.transform.parent.gameObject : null;
            return go == null || Home.CausedHere(go, 5);
        }
        sealed class Pour : ModHook
        {
            public PlayMakerFSM Fsm; public FsmState State;
            public override string Module { get { return "msc-fluids"; } }
            public override void OnEnter()
            {
                if (!Session.Active || Replay.Depth > 0) { Finish(); return; }
                var source = Fsm.FsmVariables.FindFsmGameObject("FluidTrigger");
                GameObject go = source != null && source.Value != null ? source.Value.transform.parent.gameObject : null;
                if (go == null || Home.CausedHere(go, 5)) { Finish(); return; }
                FireTools.Cancel(Fsm, State, this, "State 1");
            }
        }
        public static void Reset() { hooked.Clear(); scan = 0; }
        public static void Update()
        {
            if (!Session.Active || !PlayerSync.InGame || Time.realtimeSinceStartup < scan) return;
            if (harmony == null)
            {
                harmony = HarmonyInstance.Create("SummerCoop.MscFluids");
                foreach (Type type in new[] { typeof(HutongGames.PlayMaker.Actions.AddFsmFloat), typeof(HutongGames.PlayMaker.Actions.SubtractFsmFloat) })
                    foreach (string name in new[] { "OnEnter", "OnUpdate" })
                    {
                        MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
                        if (method != null && method.DeclaringType == type) harmony.Patch(method, new HarmonyMethod(typeof(MscFluids).GetMethod("CanPour", BindingFlags.Static | BindingFlags.NonPublic)), null);
                    }
            }
            scan = Time.realtimeSinceStartup + 2;
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None || f.FsmName != "Trigger" || !f.name.StartsWith("CapTrigger_", StringComparison.Ordinal) || hooked.Contains(f)) continue;
                FsmState state = f.Fsm.GetState("Pouring"); if (state == null || f.FsmVariables.FindFsmGameObject("FluidTrigger") == null) continue;
                if (FireTools.Insert(f, state, new Pour { Fsm = f, State = state }, false)) hooked.Add(f);
            }
        }
        public static bool Owns(PlayMakerFSM f)
        {
            if (f == null || f.FsmName != "Data") return false;
            string path = Recon.Path(f.transform);
            if (path == "Database/DatabaseMotor/Oilpan" || path == "Database/DatabaseMechanics/Radiator"
                || path == "Database/DatabaseOrders/Racing Radiator" || path == "Database/DatabaseMechanics/BrakeMasterCylinder"
                || path == "Database/DatabaseMechanics/ClutchMasterCylinder" || path == "Database/DatabaseMechanics/FuelTank") return true;
            return f.gameObject.name == "FuelTank" && f.FsmVariables.FindFsmFloat("FuelLevel") != null;
        }
        public static int Authority(PlayMakerFSM f)
        {
            if (!Owns(f)) return -1;
            Rigidbody car;
            if (f.transform.root.name == "Database") car = VehicleSync.Body("SATSUMA");
            else { Transform root = VehicleSync.CarRoot(f.transform); car = root != null ? root.GetComponent<Rigidbody>() : null; }
            return car != null ? VehicleSync.Authority(car) : 0;
        }
        public static string ReservoirKey(PlayMakerFSM f)
        {
            if (!Owns(f) || f.transform.root.name == "Database") return null;
            // Jonnez is a native '(Clone)' with no part ID. The generic prop
            // filter intentionally skips such objects; its saved fuel tag is
            // the stable identity of this vehicle's reservoir on every peer.
            var tag = f.FsmVariables.FindFsmString("UniqueTagFuelLevel");
            return tag != null && !string.IsNullOrEmpty(tag.Value) ? "msc-fuel:" + tag.Value : "p:" + Recon.Path(f.transform);
        }
        public static bool Metadata(string name) { return name == "OilGrade" || name == "OilContamination"; }
        public static float Clamp(PlayMakerFSM f, string name, float value)
        {
            if (!Owns(f)) return value;
            float max = 100;
            if (name == "FuelLevel") max = FireTools.GetF(f, "MaxCapacity", 1000);
            else if (name == "Oil") max = 3;
            else if (name == "Water") max = f.gameObject.name.Contains("Racing") ? 7 : 5.4f;
            else if (name == "BrakeFluidF" || name == "BrakeFluidR") max = 1;
            else if (name == "ClutchFluid") max = .5f;
            return Mathf.Clamp(value, name == "OilContamination" ? 1 : 0, max);
        }
    }
}
