using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    public static class MscBeltProbe
    {
        public static readonly List<string> Results = new List<string>();
        static GameObject belt;
        static PlayMakerFSM spawn, assembly, rotate, check, database, removal, pivotBolt;
        static bool prepared, dumped, blocked, blockedRecorded, mounted, removed, firstRecorded, secondRecorded;
        static int adjustSteps, boltSteps;
        static float nextStep;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-workshop-channel");
            if (channel.Length == 0 || VehicleSync.Count == 0 || Time.timeSinceLevelLoad < 22) return;
            if (!prepared)
            {
                prepared = true;
                if (Session.IsHost)
                {
                    Results.Add("BeltFixture=" + MscParts.TestInstall("crankshaft pulley(Clone)"));
                    Results.Add("BeltFixture=" + MscParts.TestInstall("water pump pulley(Clone)"));
                }
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    PlayMakerFSM f = item as PlayMakerFSM; if (f == null || f.hideFlags != HideFlags.None) continue;
                    if (f.FsmName == "Alternatorbelt" && f.gameObject.name == "CreateItems") spawn = f;
                    if (f.FsmName == "Assembly" && f.gameObject.name == "trigger_alternator belt") assembly = f;
                    if (f.FsmName == "HandRotate" && Recon.Path(f.transform).Contains("alternator(Clone)/Pivot")) rotate = f;
                    if (f.FsmName == "Data" && Recon.Path(f.transform) == "Database/PartsStatus/AlternatorBelt") database = f;
                }
                if (spawn == null || assembly == null || rotate == null || database == null) throw new InvalidOperationException("Native belt fixture is missing");
                if (Session.IsHost)
                {
                    FsmGameObject existing = database.FsmVariables.FindFsmGameObject("CurrentBelt");
                    PlayMakerFSM previous = existing == null || existing.Value == null ? null : Game.FsmOn(existing.Value, "Removal");
                    if (previous != null) { Game.SetState(previous, "Remove part"); Results.Add("BeltFixture=Existing saved belt removed natively"); }
                }
                Game.SetState(spawn, "Create product");
                belt = spawn.FsmVariables.FindFsmGameObject("New").Value;
                if (belt == null) throw new InvalidOperationException("Native belt spawner returned no item");
                belt.transform.parent = null; belt.transform.position = assembly.transform.position + Vector3.up * .5f; belt.SetActive(true);
                GameObject alternator = assembly.FsmVariables.FindFsmGameObject("Alternator").Value;
                check = Game.FsmOn(alternator, "BoltCheck");
                foreach (PlayMakerFSM f in alternator.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (MscParts.IsBolt(f) && f.transform.parent != null && f.transform.parent.name == "Pivot") pivotBolt = f;
                Results.Add("BeltFixture=NativeItem=" + belt.name);
                return;
            }
            if (!dumped && Time.timeSinceLevelLoad > 28)
            {
                dumped = true; removal = Game.FsmOn(belt, "Removal");
                StringBuilder text = new StringBuilder("Item=" + belt.name + ";ID=" + MscParts.IdOf(belt) + "\n");
                foreach (PlayMakerFSM f in belt.GetComponents<PlayMakerFSM>()) Dump(text, f);
                Dump(text, assembly); Dump(text, check); Dump(text, database);
                File.WriteAllText(Path.Combine(channel, Role + "-belt-inventory.txt"), text.ToString());
            }
            string clock = Path.Combine(channel, "start.txt"); long ticks;
            if (!File.Exists(clock) || !long.TryParse(File.ReadAllText(clock), out ticks)) return;
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ticks).TotalSeconds;
            bool actor = !Session.IsHost;
            if (actor && elapsed >= 153 && elapsed < 158 && Time.realtimeSinceStartup >= nextStep)
            {
                nextStep = Time.realtimeSinceStartup + .3f;
                float rotation = check.FsmVariables.FindFsmFloat("Rotation").Value;
                if (rotation < 6) Game.SetState(rotate, "Clockwise");
                else if (rotation > 6.5f) Game.SetState(rotate, "Counterwise");
            }
            if (elapsed >= 160 && elapsed < 174 && actor && !blocked)
            {
                blocked = true;
                GameObject hand = Hand();
                if (hand == null) throw new InvalidOperationException("Native belt hand reference is missing");
                MscParts.IdOf(belt); // retain the item's saved identity before the native hand name
                belt.name = assembly.FsmVariables.FindFsmString("HandChild").Value;
                belt.transform.parent = hand.transform; belt.transform.localPosition = Vector3.zero;
                Rigidbody rb = belt.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = true;
                assembly.FsmVariables.FindFsmGameObject("Part").Value = belt;
                for (Transform p = assembly.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                Game.SetState(assembly, "Requirements");
            }
            if (!blockedRecorded && elapsed >= (actor ? 164 : 168)) blockedRecorded = Record(channel, "blocked", actor);
            if (actor && elapsed >= 174 && elapsed < 178 && adjustSteps < 10 && Time.realtimeSinceStartup >= nextStep)
            {
                nextStep = Time.realtimeSinceStartup + .3f; adjustSteps++;
                if (check.FsmVariables.FindFsmFloat("Rotation").Value > 3) Game.SetState(rotate, "Counterwise");
            }
            if (actor && elapsed >= 180 && elapsed < 184 && !mounted)
            {
                mounted = true;
                assembly.FsmVariables.FindFsmGameObject("Part").Value = belt;
                Game.SetState(assembly, "Requirements");
                // Same click after the native requirements and rotation checks.
                Game.SetState(assembly, "Wait");
            }
            if (actor && elapsed >= 188 && elapsed < 194 && Time.realtimeSinceStartup >= nextStep)
            {
                nextStep = Time.realtimeSinceStartup + .3f;
                if (check.FsmVariables.FindFsmFloat("Rotation").Value < 6) Game.SetState(rotate, "Clockwise");
            }
            if (actor && elapsed >= 195 && elapsed < 200 && pivotBolt != null && Time.realtimeSinceStartup >= nextStep)
            {
                nextStep = Time.realtimeSinceStartup + .3f;
                if (pivotBolt.FsmVariables.FindFsmInt("Stage").Value < 8) { Game.SetState(pivotBolt, "Screw 2"); boltSteps++; }
            }
            if (!firstRecorded && elapsed >= (actor ? 203 : 207)) firstRecorded = Record(channel, "mounted", actor);
            if (Session.IsHost && elapsed >= 211 && elapsed < 215 && pivotBolt != null && Time.realtimeSinceStartup >= nextStep)
            {
                nextStep = Time.realtimeSinceStartup + .3f;
                if (pivotBolt.FsmVariables.FindFsmInt("Stage").Value > 0) Game.SetState(pivotBolt, "Unscrew 2");
            }
            if (Session.IsHost && elapsed >= 216 && elapsed < 222 && Time.realtimeSinceStartup >= nextStep)
            {
                nextStep = Time.realtimeSinceStartup + .3f;
                if (check.FsmVariables.FindFsmFloat("Rotation").Value > 3) Game.SetState(rotate, "Counterwise");
            }
            if (Session.IsHost && elapsed >= 224 && elapsed < 228 && !removed)
            {
                removed = true;
                if (removal == null) removal = Game.FsmOn(belt, "Removal");
                if (removal == null) throw new InvalidOperationException("Native belt removal FSM is missing");
                Game.SetState(removal, "Remove part");
            }
            if (!secondRecorded && elapsed >= (Session.IsHost ? 233 : 237)) secondRecorded = Record(channel, "removed", Session.IsHost);
        }
        static GameObject Hand()
        {
            FsmState state = assembly.Fsm.GetState("Find correct part");
            foreach (FsmStateAction action in state.Actions)
            {
                var child = action as HutongGames.PlayMaker.Actions.GetChild;
                if (child != null && child.gameObject.OwnerOption == OwnerDefaultOption.SpecifyGameObject) return child.gameObject.GameObject.Value;
            }
            return null;
        }
        static SortedDictionary<string, float> Snapshot()
        {
            SortedDictionary<string, float> values = new SortedDictionary<string, float>(StringComparer.Ordinal);
            values["installed"] = database.FsmVariables.FindFsmBool("Installed").Value ? 1 : 0;
            values["itemInstalled"] = MscBelt.Installed(belt) ? 1 : 0;
            values["rotation"] = check.FsmVariables.FindFsmFloat("Rotation").Value;
            values["angle.x"] = rotate.transform.localEulerAngles.x;
            values["rotationEnabled"] = rotate.enabled ? 1 : 0;
            FsmGameObject current = database.FsmVariables.FindFsmGameObject("CurrentBelt");
            values["currentBelt"] = current != null && current.Value == belt ? 1 : 0;
            values["wear"] = database.FsmVariables.FindFsmFloat("Wear").Value;
            values["rigidbody"] = belt.GetComponent<Rigidbody>() != null ? 1 : 0;
            FsmGameObject mesh = assembly.FsmVariables.FindFsmGameObject("BeltMesh");
            if (mesh != null && mesh.Value != null) values["mesh"] = mesh.Value.activeSelf ? 1 : 0;
            if (pivotBolt != null) values["lockingBolt"] = pivotBolt.FsmVariables.FindFsmInt("Stage").Value;
            foreach (PlayMakerFSM f in assembly.FsmVariables.FindFsmGameObject("BeltMesh").Value.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f.FsmName != "Jumping" || !f.Fsm.Initialized) continue;
                FsmFloat rotation = f.FsmVariables.FindFsmFloat("Rotation");
                if (rotation != null) values["beltAnimationRotation"] = rotation.Value;
            }
            return values;
        }
        static bool Record(string channel, string phase, bool actor)
        {
            string expected = Path.Combine(channel, "belt-" + phase + ".txt");
            if (!actor && !File.Exists(expected)) return false;
            SortedDictionary<string, float> values = Snapshot(); List<string> rows = new List<string>();
            foreach (KeyValuePair<string, float> pair in values) rows.Add(pair.Key + "|" + pair.Value.ToString("R", C));
            File.WriteAllLines(Path.Combine(channel, Role + "-belt-" + phase + ".txt"), rows.ToArray());
            int mismatches = 0;
            if (actor) File.WriteAllLines(expected, rows.ToArray());
            else foreach (string row in File.ReadAllLines(expected))
            {
                string[] fields = row.Split('|'); float actual;
                if (!values.TryGetValue(fields[0], out actual) || Mathf.Abs(actual - float.Parse(fields[1], C)) > .002f) { mismatches++; Results.Add("BeltMismatch=" + phase + ";" + row + ";actual=" + actual.ToString("R", C)); }
            }
            bool installed = phase == "mounted";
            int stateErrors = 0;
            foreach (string field in new[] { "installed", "itemInstalled", "mesh" }) if (values[field] != (installed ? 1 : 0)) stateErrors++;
            if (values["rigidbody"] != (installed ? 0 : 1)) stateErrors++;
            // Native HandRotate steps by 0.5; its stored float can cross the
            // threshold by a few ulps and therefore take one additional step.
            float minimum = phase == "removed" ? 2.49f : 5.99f, maximum = phase == "removed" ? 3.01f : 6.51f;
            if (values["rotation"] < minimum || values["rotation"] > maximum) stateErrors++;
            if (values["lockingBolt"] != (installed ? 8 : 0) || values["rotationEnabled"] != (installed ? 0 : 1)) stateErrors++;
            StringBuilder native = new StringBuilder();
            native.Append("PartID=").Append(MscParts.IdOf(belt)).Append("\nRemovalKey=").Append(removal != null ? MscParts.Key(removal) : "missing").Append('\n');
            foreach (PlayMakerFSM f in belt.GetComponents<PlayMakerFSM>()) Dump(native, f);
            foreach (PlayMakerFSM f in assembly.FsmVariables.FindFsmGameObject("BeltMesh").Value.GetComponentsInChildren<PlayMakerFSM>(true)) Dump(native, f);
            File.WriteAllText(Path.Combine(channel, Role + "-belt-" + phase + "-native.txt"), native.ToString());
            string result = "Belt=" + phase + ";Role=" + (actor ? "actor" : "observer") + ";Installed=" + values["installed"] + ";Rotation=" + values["rotation"].ToString("R", C) + ";Locked=" + (pivotBolt != null && pivotBolt.FsmVariables.FindFsmInt("Stage").Value == 8) + ";Fields=" + values.Count + ";Mismatches=" + mismatches + ";StateErrors=" + stateErrors;
            Results.Add(result); Log.Info(result); return true;
        }
        static void Dump(StringBuilder text, PlayMakerFSM f)
        {
            if (f == null) return;
            text.Append("\nFSM ").Append(Recon.Path(f.transform)).Append(" :: ").Append(f.FsmName).Append('\n');
            foreach (FsmString value in f.FsmVariables.StringVariables) text.Append("STRING ").Append(value.Name).Append('=').Append(value.Value).Append('\n');
            foreach (FsmFloat value in f.FsmVariables.FloatVariables) text.Append("FLOAT ").Append(value.Name).Append('=').Append(value.Value.ToString("R", C)).Append('\n');
            foreach (FsmGameObject value in f.FsmVariables.GameObjectVariables) text.Append("OBJECT ").Append(value.Name).Append('=').Append(value.Value != null ? Recon.Path(value.Value.transform) : "").Append('\n');
            foreach (FsmState state in f.FsmStates) if (state.IsInitialized) text.Append(Jobs.DumpActions(f, state.Name)).Append('\n');
        }
    }
}
