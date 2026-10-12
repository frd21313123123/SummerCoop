using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HutongGames.PlayMaker;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class NativeAdapter : ICopyOnWriteCommandAdapter, ITransientCommandAdapter, ILiveCommandPosition
    {
        private readonly WorldRegistry registry;
        private readonly Dictionary<string, float> leases = new Dictionary<string, float>();
        private readonly List<string> leaseScan = new List<string>();
        private readonly List<string> driverScan = new List<string>();
        private readonly Dictionary<string, string> originals = new Dictionary<string, string>();
        private readonly Dictionary<string, int> heldLayers = new Dictionary<string, int>();
        private NativeHandLayers handLayers;
        public readonly Dictionary<string, DriveInput> Drivers = new Dictionary<string, DriveInput>();
        private readonly Dictionary<string, Dictionary<Behaviour, bool>> controllerStates = new Dictionary<string, Dictionary<Behaviour, bool>>();
        private readonly Dictionary<string, bool> boatControlStates = new Dictionary<string, bool>();
        private static readonly MethodInfo SwitchState = typeof(Fsm).GetMethod("SwitchState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo FlushStates = typeof(Fsm).GetMethod("UpdateStateChanges", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        public static void CheckBindings() { if (SwitchState == null || FlushStates == null) throw new InvalidOperationException("PlayMaker native transition binding unavailable"); }
        public bool Executing { get; private set; }
        public NativeAdapter(WorldRegistry registry) { this.registry = registry; }
        public string Validate(string player, Command c, World world)
        {
            if (c.Kind == CommandKind.Sleep) return null;
            GameObject target = registry.ObjectOf(c.Target); FsmBinding fsm = registry.FsmOf(c.Target);
            if (target == null && fsm == null) return "Native target is unavailable";
            if (c.Kind == CommandKind.Take) return target != null && target.GetComponent<Rigidbody>() != null ? null : "This object cannot be carried";
            if (c.Kind == CommandKind.Drop) return null;
            if (c.Kind == CommandKind.Drive) return DriveInput.Parse(c) == null ? "Invalid controls" : null;
            if (c.Kind == CommandKind.EnterSeat || c.Kind == CommandKind.LeaveSeat)
                return fsm != null && fsm.Component.FsmName == "PlayerTrigger" ? null : "Not a seat";
            if (fsm == null || !fsm.Requests.Contains(c.Argument) || fsm.Component.Fsm.GetState(c.Argument) == null) return "Unsupported native interaction";
            if (c.Kind == CommandKind.Interact && c.Argument == "Remove bottle")
            {
                FsmInt consumed = fsm.Component.FsmVariables.FindFsmInt("DestroyedBottles");
                if (consumed != null && ConsumableStock.Remaining(24, consumed.Value) == 0) return "This beer case is empty";
            }
            string path = fsm.Key;
            if (path.IndexOf("SAVEGAME", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("SleepTrigger", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Computer", StringComparison.OrdinalIgnoreCase) >= 0) return "Use the multiplayer session controls";
            // Bolt symbols are hidden by the host's personal tool mode. Their
            // native state still belongs to the mounted part and can be operated remotely.
            if (!fsm.Component.gameObject.activeInHierarchy && !(c.Kind == CommandKind.Bolt && MountedBolt(fsm))) return "Object is inactive";
            if (Vector3.Distance(WorldRegistry.To(world.Players[player].Position), fsm.Component.transform.position) > 5) return "Object is too far away";
            if (c.Kind == CommandKind.Attach)
            {
                Player p = world.Players[player]; GameObject held = registry.ObjectOf(p.Held);
                if (held == null || !NativeAssembly.Accepts(fsm.Component.FsmVariables.StringVariables, held.name)) return "Wrong part for this attachment point";
                if (Vector3.Distance(player == "host" ? held.transform.position : HeldPosition(p), fsm.Component.transform.position) > 0.8f) return "Move the part to the attachment point";
                FsmGameObject db = fsm.Component.FsmVariables.FindFsmGameObject("db_ThisPart");
                if (db != null && db.Value != null) { PlayMakerFSM data = PlayMakerFSM.FindFsmOnGameObject(db.Value, "Data"); FsmBool installed = data == null ? null : data.FsmVariables.FindFsmBool("Installed"); if (installed != null && installed.Value) return "Part is already installed"; }
            }
            if (c.Kind == CommandKind.Wire)
            {
                GameObject held = registry.ObjectOf(world.Players[player].Held);
                if (held == null || !held.name.StartsWith("wiring mess", StringComparison.OrdinalIgnoreCase)) return "Wiring tool is required";
                if (Vector3.Distance(player == "host" ? held.transform.position : HeldPosition(world.Players[player]), fsm.Component.transform.position) > 0.8f) return "Move the wiring tool to the connector";
            }
            if (c.Kind == CommandKind.Detach)
            {
                foreach (PlayMakerFSM local in fsm.Component.GetComponents<PlayMakerFSM>())
                {
                    if (local.FsmName != "BoltCheck") continue;
                    FsmFloat tightness = local.FsmVariables.FindFsmFloat("Tightness"); if (tightness != null && tightness.Value > 0.01f) return "Unscrew all bolts first";
                }
            }
            if (c.Kind == CommandKind.Bolt)
            {
                FsmFloat size = fsm.Component.FsmVariables.FindFsmFloat("BoltSize");
                if (size != null && size.Value > 0 && Math.Abs(size.Value - c.Number) > 0.01) return "Wrong wrench size";
                if (c.Argument != "Screw 2" && c.Argument != "Unscrew 2" && c.Argument != "Screw" && c.Argument != "Unscrew" && c.Argument != "Tighten" && c.Argument != "Untighten") return "Invalid bolt action";
            }
            return null;
        }
        private static bool MountedBolt(FsmBinding binding)
        {
            if (binding.Component.FsmName != "Screw") return false;
            for (Transform cursor = binding.Component.transform.parent; cursor != null; cursor = cursor.parent)
            {
                foreach (PlayMakerFSM fsm in cursor.GetComponents<PlayMakerFSM>())
                {
                    FsmGameObject database = fsm.FsmVariables.FindFsmGameObject("db_ThisPart");
                    if (database == null || database.Value == null) continue;
                    PlayMakerFSM data = PlayMakerFSM.FindFsmOnGameObject(database.Value, "Data");
                    FsmBool installed = data == null ? null : data.FsmVariables.FindFsmBool("Installed");
                    if (installed != null) return installed.Value && cursor.gameObject.activeInHierarchy;
                }
            }
            return false;
        }
        public void Apply(string player, Command c, World world)
        {
            Executing = true;
            NativeActorContext context = new NativeActorContext(player);
            try
            {
                GameObject target = registry.ObjectOf(c.Target); Player actor = world.Players[player]; string heldBefore = actor.Held;
                if (c.Kind == CommandKind.Take)
                {
                    originals[c.Target] = world.Entities[c.Target].Parent;
                    if (player != "host")
                    {
                        target.GetComponent<Rigidbody>().isKinematic = true; target.transform.parent = null;
                        if (target.CompareTag("PART") || target.CompareTag("ITEM"))
                        {
                            if (handLayers == null) handLayers = NativeHandLayers.Discover();
                            heldLayers[c.Target] = target.layer; target.layer = handLayers.Held;
                        }
                    }
                }
                else if (c.Kind == CommandKind.Drop)
                {
                    if (target != null && player != "host")
                    {
                        if (actor.HasHeldPose) { target.transform.position = WorldRegistry.To(actor.HeldPosition); target.transform.rotation = WorldRegistry.To(actor.HeldRotation); }
                        target.transform.parent = null; Rigidbody body = target.GetComponent<Rigidbody>();
                        int layer; if (heldLayers.TryGetValue(c.Target, out layer)) target.layer = layer;
                        if (body != null) { body.isKinematic = false; body.velocity = WorldRegistry.To(c.Vector); body.WakeUp(); }
                    }
                    originals.Remove(c.Target); heldLayers.Remove(c.Target);
                }
                else if (c.Kind == CommandKind.Drive) Drivers[c.Target] = DriveInput.Parse(c);
                else if (c.Kind == CommandKind.LeaveSeat)
                {
                    Drivers.Remove(c.Target); Dictionary<Behaviour, bool> controls;
                    if (controllerStates.TryGetValue(c.Target, out controls)) { foreach (var saved in controls) if (saved.Key != null) saved.Key.enabled = saved.Value; controllerStates.Remove(c.Target); }
                    bool active; FsmBinding seat = registry.FsmOf(c.Target);
                    if (seat != null && boatControlStates.TryGetValue(c.Target, out active)) { Transform boatControls = seat.Component.transform.root.Find("Controls"); if (boatControls != null) boatControls.gameObject.SetActive(active); boatControlStates.Remove(c.Target); }
                }
                else if (c.Kind == CommandKind.EnterSeat && player != "host")
                {
                    FsmBinding seat = registry.FsmOf(c.Target);
                    if (seat.Key.IndexOf("DriveTrigger", StringComparison.Ordinal) >= 0)
                    {
                        Dictionary<Behaviour, bool> saved = new Dictionary<Behaviour, bool>();
                        foreach (CarController controller in seat.Component.transform.root.GetComponentsInChildren<CarController>(true)) { saved[controller] = controller.enabled; controller.enabled = true; }
                        controllerStates[c.Target] = saved;
                        if (seat.Component.transform.root.name == "BOAT") { Transform controls = seat.Component.transform.root.Find("Controls"); if (controls != null) { boatControlStates[c.Target] = controls.gameObject.activeSelf; controls.gameObject.SetActive(true); } }
                    }
                }
                else if (c.Kind == CommandKind.Sleep)
                {
                    if (world.AllSleeping()) AdvanceSleep(world);
                }
                else if (c.Kind != CommandKind.EnterSeat)
                {
                    // Commands are handled before the frame tick. Use the latest accepted pose now.
                    if (actor.Held != "" && player != "host") MoveHeld(actor);
                    FsmBinding b = registry.FsmOf(c.Target);
                    if (c.Kind == CommandKind.Attach)
                    {
                        FsmGameObject part = b.Component.FsmVariables.FindFsmGameObject("Part"); if (part == null) throw new InvalidOperationException("Assembly Part variable missing");
                        GameObject carried = registry.ObjectOf(actor.Held); Transform previousParent = carried.transform.parent;
                        part.Value = carried;
                        ExecuteState(b, c.Argument);
                        if (carried == null || carried.transform.parent == null || carried.transform.parent == previousParent
                            || WorldRegistry.PersonalObject(carried.transform.parent.gameObject))
                            throw new InvalidOperationException("Native assembly did not mount the held part; ownership retained");
                        originals.Remove(actor.Held); heldLayers.Remove(actor.Held); actor.Held = "";
                    }
                    else
                    {
                        ExecuteState(b, c.Argument);
                        if (HasContinuousForce(b.Component.Fsm.GetState(c.Argument))) leases[b.Id] = Time.realtimeSinceStartup + 0.25f;
                    }
                }
                registry.CaptureRelated(world, c.Target, heldBefore);
            }
            catch (Exception e) { Debug.LogError("SummerCoop native command " + c.Kind + " " + c.Target + " " + c.Argument); Debug.LogException(e); throw; }
            finally { context.Dispose(); Executing = false; }
        }
        public bool TryGetPosition(string target, out V3 position)
        {
            FsmBinding fsm = registry.FsmOf(target);
            GameObject go = registry.ObjectOf(fsm == null ? target : fsm.ObjectId);
            position = go == null ? new V3() : WorldRegistry.From(go.transform.position); return go != null;
        }
        public void ApplyTransient(string player, Command command, World world)
        {
            DriveInput input = DriveInput.Parse(command);
            if (input == null) throw new InvalidOperationException("Invalid controls");
            Drivers[command.Target] = input;
        }
        private static bool HasContinuousForce(FsmState state)
        {
            foreach (FsmStateAction a in state.Actions) if (a.GetType().Name == "AddTorque" || a.GetType().Name == "AddForce") return true; return false;
        }
        public static void ExecuteState(FsmBinding b, string name)
        {
            CheckBindings();
            FsmState state = b.Component.Fsm.GetState(name); if (state == null) throw new InvalidOperationException("Native state missing: " + name);
            SwitchState.Invoke(b.Component.Fsm, new object[] { state }); FlushStates.Invoke(b.Component.Fsm, null);
        }
        public void Tick(World world)
        {
            foreach (Player p in world.Players.Values)
            {
                if (p.Id == "host" || p.Held == "") continue;
                MoveHeld(p);
            }
            leaseScan.Clear(); leaseScan.AddRange(leases.Keys);
            foreach (string id in leaseScan) if (Time.realtimeSinceStartup > leases[id])
            {
                FsmBinding b = registry.FsmOf(id); if (b != null && b.WaitingState != "") ExecuteState(b, b.WaitingState); leases.Remove(id);
            }
            driverScan.Clear(); driverScan.AddRange(Drivers.Keys);
            foreach (string id in driverScan) if (Time.realtimeSinceStartup - Drivers[id].Updated > 0.35f) Drivers.Remove(id);
            leaseScan.Clear(); driverScan.Clear();
        }
        private static Vector3 HeldPosition(Player player) { return player.HasHeldPose ? WorldRegistry.To(player.HeldPosition) : WorldRegistry.To(player.Position) + Vector3.up * 1.35f + WorldRegistry.To(player.Rotation) * Quaternion.Euler(player.Pitch, 0, 0) * Vector3.forward * 1.2f; }
        private void MoveHeld(Player player) { GameObject go = registry.ObjectOf(player.Held); if (go != null) { go.transform.position = HeldPosition(player); go.transform.rotation = WorldRegistry.To(player.HasHeldPose ? player.HeldRotation : player.Rotation); } }
        private static void AdvanceSleep(World world)
        {
            FsmVariables globals = FsmVariables.GlobalVariables;
            FsmInt hour = globals.FindFsmInt("GlobalHour"), day = globals.FindFsmInt("GlobalDay");
            if (hour == null || day == null) throw new InvalidOperationException("Clock schema missing");
            int next = hour.Value + 6; day.Value = ((day.Value - 1 + next / 24) % 7) + 1; hour.Value = next % 24;
            foreach (Player p in world.Players.Values) p.SleepReady = false;
            world.SleepEpoch++;
            FsmFloat fatigue = globals.FindFsmFloat("PlayerFatigue"); if (fatigue != null) fatigue.Value = 0;
            foreach (PlayMakerFSM f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>()) if (f.gameObject.name == "SUN" && (f.FsmName == "Color" || f.FsmName == "Clock"))
            {
                FsmInt time = f.FsmVariables.FindFsmInt("Time"); if (time != null) time.Value = hour.Value;
                if (f.Fsm.GetState("State 1") != null) ExecuteState(new FsmBinding { Component = f }, "State 1");
            }
        }
        public void Rollback(World before) { registry.Apply(before, true); }
        public DriveInput InputFor(CarController controller, World world)
        {
            foreach (Player player in world.Players.Values)
            {
                if (player.Seat == "" || player.Id == "host") continue;
                FsmBinding seat = registry.FsmOf(player.Seat); if (seat == null || seat.Component.transform.root != controller.transform.root) continue;
                DriveInput input; if (Drivers.TryGetValue(player.Seat, out input)) return input;
                return new DriveInput { Brake = 1 };
            }
            return null;
        }
        public DriveInput BoatInput(World world)
        {
            foreach (Player player in world.Players.Values)
            {
                if (player.Id == "host" || player.Seat == "") continue;
                FsmBinding seat = registry.FsmOf(player.Seat); if (seat == null || seat.Component.transform.root.name != "BOAT" || seat.Key.IndexOf("DriveTrigger", StringComparison.Ordinal) < 0) continue;
                DriveInput input; return Drivers.TryGetValue(player.Seat, out input) ? input : new DriveInput();
            }
            return null;
        }
    }
    public sealed class DriveInput
    {
        public float Steering, Throttle, Brake, Handbrake, Clutch, Updated;
        public int Shift;
        public bool Start;
        public static DriveInput Parse(Command c)
        {
            if (Math.Abs(c.Vector.X) > 1 || c.Vector.Y < 0 || c.Vector.Y > 1 || c.Vector.Z < 0 || c.Vector.Z > 1 || c.Number < 0 || c.Number > 1) return null;
            string[] fields = c.Argument.Split('|'); float clutch; int shift;
            if (fields.Length != 3 || !float.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out clutch) || clutch < 0 || clutch > 1 || !V3.Finite(clutch) || !int.TryParse(fields[1], out shift) || shift < 0 || shift > 10 || (fields[2] != "0" && fields[2] != "1")) return null;
            return new DriveInput { Steering = c.Vector.X, Throttle = c.Vector.Y, Brake = c.Vector.Z, Handbrake = (float)c.Number, Clutch = clutch, Shift = shift, Start = fields[2] == "1", Updated = Time.realtimeSinceStartup };
        }
    }
}
