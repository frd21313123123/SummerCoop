using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;
using Session = MWCoop.Net.Session;

namespace MWCoop
{
    internal static class MscBattery
    {
        sealed class Battery
        {
            public PlayMakerFSM Use;
            public FsmBool Installed;
            public Rigidbody Body, Car;
            public Transform Mount;
            public bool Following, WasKinematic;
            public bool SnapshotSeeded;
            public RigidbodyInterpolation Interpolation;
        }
        static readonly Dictionary<GameObject, Battery> batteries = new Dictionary<GameObject, Battery>();
        internal static void Reset() { batteries.Clear(); }
        internal static bool IsBattery(GameObject go)
        {
            return go != null && go.name == "battery(Clone)";
        }
        internal static bool Owns(PlayMakerFSM f)
        {
            // The native loop writes JointBattery's break force. Generic state
            // replay must not execute that loop against another player's joint.
            if (f == null || f.FsmName != "Data") return false;
            string path = Recon.Path(f.transform);
            return path == "Database/PartsStatus/Battery" || path == "Database/DatabaseWiring/WiringBatteryPlus"
                || path == "Database/DatabaseWiring/WiringBatteryMinus";
        }
        internal static void Register(PlayMakerFSM f)
        {
            if (f == null || f.FsmName != "Use" || !IsBattery(f.gameObject)) return;
            Battery b;
            if (!batteries.TryGetValue(f.gameObject, out b)) batteries[f.gameObject] = b = new Battery { Use = f };
            b.Installed = f.FsmVariables.FindFsmBool("Installed");
        }
        sealed class Removed : ModHook
        {
            public PlayMakerFSM Removal;
            public override string Module { get { return "msc-battery"; } }
            public override void OnEnter()
            {
                try { Remove(Removal); }
                catch (Exception e) { Replay.HookError(e); }
                Finish();
            }
        }
        internal static void Prepare(PlayMakerFSM f)
        {
            if (f.FsmName != "Removal" || !IsBattery(f.gameObject)) return;
            FsmState state = f.Fsm.GetState("Remove part");
            if (state == null) return;
            var actions = new List<FsmStateAction>(state.Actions);
            actions.Add(new Removed { Removal = f }); state.Actions = actions.ToArray();
        }
        static void Release(Battery b)
        {
            if (!b.Following) return;
            b.Following = false;
            if (b.Body == null) return;
            b.Body.isKinematic = b.WasKinematic; b.Body.interpolation = b.Interpolation;
            if (!b.Body.isKinematic && b.Car != null)
            {
                b.Body.velocity = VehicleSync.CarVelocity(VehicleSync.CarIndex(b.Car));
                b.Body.angularVelocity = b.Car.angularVelocity;
            }
        }
        static void Remove(PlayMakerFSM removal)
        {
            CompleteRemoval(removal.gameObject, removal);
        }
        internal static void CompleteRemoval(GameObject go, PlayMakerFSM removal)
        {
            Battery b;
            if (batteries.TryGetValue(go, out b)) Release(b);
            // Native Removal destroys the joint but leaves the transform under
            // pivot_battery. That ancestry made Props reject all loose poses.
            Transform car = VehicleSync.CarRoot(go.transform);
            if (car == null) return; // A repeated old removal must not reset a replacement battery.
            go.transform.parent = null;
            PlayMakerFSM use = Game.FsmOn(go, "Use");
            FsmGameObject trigger = use == null ? null : use.FsmVariables.FindFsmGameObject("Trigger");
            PlayMakerFSM assembly = trigger == null || trigger.Value == null ? null : Game.FsmOn(trigger.Value, "Assembly");
            FsmGameObject current = assembly == null ? null : assembly.FsmVariables.FindFsmGameObject("Part");
            bool replacement = current != null && current.Value != null && current.Value != go && SummerCoop.Game.NativeAssembly.Installed(current.Value);
            if (!replacement)
            {
                PlayMakerFSM data = Game.FindFsm("Database/PartsStatus/Battery", "Data");
                FsmBool installed = data == null ? null : data.FsmVariables.FindFsmBool("Installed");
                if (installed != null) installed.Value = false;
                MscParts.ReleaseBatteryTerminals(removal);
            }
            MscParts.Changed(go); Props.Track(go);
        }
        static void Follow(Battery b)
        {
            if (b.Body == null || !b.Following || b.Mount == null) return;
            // Both bodies are kinematic on a moving remote copy. On the local
            // simulation keep the native joint and its real break threshold.
            b.Body.interpolation = RigidbodyInterpolation.None;
            Transform t = b.Body.transform;
            t.localPosition = Vector3.zero; t.localRotation = Quaternion.identity;
            b.Body.position = t.position; b.Body.rotation = t.rotation;
        }
        internal static void FixedUpdate()
        {
            if (!PlayerSync.InGame || !Session.Active) return;
            foreach (Battery b in batteries.Values)
            {
                if (b.Use == null || b.Installed == null) continue;
                b.Installed = b.Use.FsmVariables.FindFsmBool("Installed");
                if (Session.IsHost && b.Installed.Value && !b.SnapshotSeeded)
                {
                    FsmGameObject trigger = b.Use.FsmVariables.FindFsmGameObject("Trigger");
                    if (trigger != null && trigger.Value != null) b.SnapshotSeeded = MscParts.SeedBatterySnapshot(Game.FsmOn(trigger.Value, "Assembly"), b.Use.gameObject);
                }
                if (b.Body == null) b.Body = b.Use.GetComponent<Rigidbody>();
                Transform car = b.Installed.Value ? VehicleSync.CarRoot(b.Use.transform) : null;
                Rigidbody body = car == null ? null : car.GetComponent<Rigidbody>();
                bool copy = body != null && body.isKinematic && VehicleSync.RemotelyDriven(car);
                if (!copy) { Release(b); continue; }
                if (!b.Following)
                {
                    Props.Release(b.Use.gameObject);
                    if (b.Body == null) continue;
                    b.Car = body; b.Mount = b.Use.transform.parent;
                    b.WasKinematic = b.Body.isKinematic; b.Interpolation = b.Body.interpolation;
                    b.Body.isKinematic = true; b.Following = true;
                }
                Follow(b);
            }
        }
        internal static void LateUpdate()
        {
            foreach (Battery b in batteries.Values) Follow(b);
        }
    }
}
