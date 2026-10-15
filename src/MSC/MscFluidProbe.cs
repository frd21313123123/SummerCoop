using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using UnityEngine;
using MWCoop.Net;
namespace MWCoop
{
    // The fixture invokes the stock pour actions with real native canisters;
    // ambient leaks are paused to measure network accounting independently.
    public static class MscFluidProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static readonly List<FsmStateAction> pouring = new List<FsmStateAction>();
        static bool At(int n, float t, float at) { return t >= at && steps.Add(n); }
        static PlayMakerFSM Data(string path) { return Game.FsmOn(Game.FindAny(path), "Data"); }
        static PlayMakerFSM oil, coolant, brake, clutch, fuel;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        public static void Update(float t, string channel)
        {
            if (At(0, t, 2))
            {
                oil = Data("Database/DatabaseMotor/Oilpan"); coolant = Data("Database/DatabaseMechanics/Radiator");
                brake = Data("Database/DatabaseMechanics/BrakeMasterCylinder"); clutch = Data("Database/DatabaseMechanics/ClutchMasterCylinder");
                fuel = Data("Database/DatabaseMechanics/FuelTank");
                // Missing engine hoses and brake lines in a fresh save leak
                // immediately. Pause those two simulations for the pour test.
                foreach (string path in new[] { "SATSUMA(557kg, 248)/CarSimulation/Car/Cooling", "SATSUMA(557kg, 248)/CarSimulation/Car/Brakes" })
                { GameObject go = Game.FindAny(path); if (go != null) foreach (PlayMakerFSM simulation in go.GetComponents<PlayMakerFSM>()) simulation.enabled = false; }
                foreach (var f in new[] { oil, coolant, brake, clutch, fuel })
                {
                    foreach (string name in new[] { "Installed", "Bolted" }) { var b = f.FsmVariables.FindFsmBool(name); if (b != null) b.Value = true; }
                }
            }
            if (At(1, t, 44) && Session.IsHost) Start();
            if (At(2, t, 46) && Session.IsHost) Stop();
            if (At(3, t, 52)) Capture(channel, "fluids-host-fill");
            if (At(4, t, 55) && !Session.IsHost) Start();
            if (At(5, t, 57) && !Session.IsHost) Stop();
            if (At(6, t, 64)) Capture(channel, "fluids-guest-fill");
            if (At(7, t, 155) && !Session.IsHost) VehicleSync.TestEnter("JONNEZ", false);
            if (At(8, t, 157) && !Session.IsHost) VehicleSync.TestEnter("JONNEZ", true);
            if (At(9, t, 161) && !Session.IsHost)
            {
                var f = Game.FsmOn(VehicleSync.Body("JONNEZ").transform.Find("FuelTank").gameObject, "Data");
                var subtract = new HutongGames.PlayMaker.Actions.FloatSubtract { floatVariable = f.FsmVariables.GetFsmFloat("FuelLevel"), subtract = .25f };
                subtract.Init(f.Fsm.ActiveState); subtract.OnEnter();
            }
            if (At(10, t, 165) && Session.IsHost)
            {
                var f = Game.FsmOn(VehicleSync.Body("JONNEZ").transform.Find("FuelTank").gameObject, "Data");
                var add = new HutongGames.PlayMaker.Actions.FloatAdd { floatVariable = f.FsmVariables.GetFsmFloat("FuelLevel"), add = .1f };
                add.Init(f.Fsm.ActiveState); add.OnEnter();
            }
            if (At(11, t, 171))
            {
                var f = Game.FsmOn(VehicleSync.Body("JONNEZ").transform.Find("FuelTank").gameObject, "Data");
                MscRegressionProbe.Record(channel, "fuel-driver-owner", MscFluids.Authority(f) == 1, "Owner=" + MscFluids.Authority(f) + ";Fuel=" + FireTools.GetF(f, "FuelLevel", -1).ToString("R", C));
                if (!Session.IsHost) VehicleSync.ExitLocal();
            }
            foreach (var action in pouring) action.OnUpdate();
        }
        static void Start()
        {
            pouring.Clear();
            var targets = new Dictionary<string, string> { { "CapTrigger_MotorOil", "MotorOil" }, { "CapTrigger_Coolant1", "Coolant" }, { "CapTrigger_BrakeF", "BrakeFluid" }, { "CapTrigger_BrakeR", "BrakeFluid" }, { "CapTrigger_Clutch", "BrakeFluid" } };
            int brakeIndex = 0;
            var used = new HashSet<string>();
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                var f = item as PlayMakerFSM; string product;
                if (f == null || f.hideFlags != HideFlags.None || !f.transform.root.gameObject.activeInHierarchy || f.FsmName != "Trigger" || !targets.TryGetValue(f.gameObject.name, out product) || !used.Add(f.gameObject.name)) continue;
                GameObject source = MscRegressionProbe.Products[product][product == "BrakeFluid" ? brakeIndex++ : 0];
                Home.MarkHeld(source); Props.Track(source);
                PlayMakerFSM fluid = null;
                foreach (PlayMakerFSM sf in source.GetComponentsInChildren<PlayMakerFSM>(true)) if (sf.FsmName == "Data") fluid = sf;
                f.FsmVariables.FindFsmGameObject("FluidTrigger").Value = fluid.gameObject;
                foreach (FsmStateAction action in FireTools.Actions(f, f.Fsm.GetState("Pouring")))
                {
                    string name = action.GetType().Name;
                    if (name != "AddFsmFloat" && name != "SubtractFsmFloat") continue;
                    action.OnEnter(); pouring.Add(action);
                }
            }
            var native = new HutongGames.PlayMaker.Actions.FloatAdd { floatVariable = fuel.FsmVariables.GetFsmFloat("FuelLevel"), add = 1.25f };
            native.Init(fuel.Fsm.GetState("Activate")); native.OnEnter();
            if (pouring.Count < 10) throw new InvalidOperationException("Native fluid pour fixture missing: " + pouring.Count);
        }
        static void Stop() { foreach (var action in pouring) action.OnExit(); pouring.Clear(); }
        static void Capture(string channel, string name)
        {
            float[] values = { FireTools.GetF(oil, "Oil", -1), FireTools.GetF(coolant, "Water", -1), FireTools.GetF(brake, "BrakeFluidF", -1), FireTools.GetF(brake, "BrakeFluidR", -1), FireTools.GetF(clutch, "ClutchFluid", -1), FireTools.GetF(fuel, "FuelLevel", -1) };
            string detail = "Values="; bool good = true;
            foreach (float v in values) { detail += v.ToString("R", C) + ","; if (v < .05f || float.IsNaN(v) || float.IsInfinity(v)) good = false; }
            MscRegressionProbe.Record(channel, name, good, detail);
        }
    }
}
