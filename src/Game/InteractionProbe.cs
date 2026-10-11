using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SummerCoop.Game
{
    // Opt-in diagnostics in isolated game copies. Manual targets position the
    // test player/camera; input and native FSM states are never assigned here.
    public sealed class InteractionProbe : MonoBehaviour
    {
        private float next;
        private bool dumped;
        private bool playerDumped;
        private Transform aimed;
        private Vector3 aim;
        private string lastTarget = "";
        public static bool Enabled { get { return CoopRuntime.Argument("--summercoop-test-interactions") == "yes" && CoopRuntime.Argument("--summercoop-test-report") != ""; } }
        private void Update()
        {
            CoopRuntime r = CoopRuntime.Instance;
            if (!Enabled || r == null || !r.Active || r.Registry == null) return;
            if (!dumped) { dumped = true; try { Dump(r); } catch (Exception e) { Debug.LogWarning("SummerCoop interaction dump: " + e); } }
            if (!playerDumped && Time.realtimeSinceStartup > 100) { playerDumped = true; DumpPlayers(r); }
            string command = Path.Combine(r.SaveRoot, "manual-target.txt");
            if (!r.Joining && File.Exists(command))
            {
                string target = File.ReadAllText(command).Trim();
                if (target != lastTarget) { lastTarget = target; Aim(r, target); }
            }
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 2; Sample(r, "TICK");
        }
        private void LateUpdate()
        {
            if (aimed != null && Camera.main != null)
                Camera.main.transform.rotation = Quaternion.LookRotation(aim - Camera.main.transform.position, Vector3.up);
        }
        private void Aim(CoopRuntime r, string target)
        {
            aimed = null;
            if (target == "walk") { Sample(r, "READY_WALK"); return; }
            if (target == "report") { DumpPlayers(r); Dump(r); return; }
            FsmBinding chosen = null;
            foreach (FsmBinding b in r.Registry.Fsms.Values)
            {
                if (b.Component == null || !b.Component.gameObject.activeInHierarchy) continue;
                if (target == "beer" && b.Component.FsmName == "Use" && b.Component.name.StartsWith("beer case", StringComparison.Ordinal)) { chosen = b; break; }
                if (target == "moped" && b.Component.FsmName == "PlayerTrigger" && b.Key.IndexOf("JONNEZ", StringComparison.Ordinal) >= 0 && b.Key.IndexOf("DriveTrigger", StringComparison.Ordinal) >= 0) { chosen = b; break; }
                if (target == "bolt" && b.Component.FsmName == "Screw" && b.Key.IndexOf("starter", StringComparison.OrdinalIgnoreCase) >= 0 && b.Component.FsmVariables.FindFsmInt("Stage") != null) { chosen = b; break; }
            }
            GameObject player = GameObject.Find("PLAYER");
            if (chosen == null || player == null || Camera.main == null) { Sample(r, "TARGET_UNAVAILABLE=" + target); return; }
            aimed = chosen.Component.transform;
            Collider col = aimed.GetComponent<Collider>(); aim = col == null ? aimed.position : col.bounds.center;
            Vector3 offset = Camera.main.transform.position - player.transform.position;
            Vector3 eye = aim + Vector3.up * 0.35f - Vector3.forward * 0.4f;
            player.transform.position = target == "moped" ? aim + Vector3.back * 0.15f : eye - offset;
            if (target != "moped") Camera.main.transform.position = eye;
            Sample(r, "READY_TARGET=" + target + " " + chosen.Key + " state=" + chosen.Component.ActiveStateName);
        }
        private static void DumpPlayers(CoopRuntime r)
        {
            using (StreamWriter w = new StreamWriter(Path.Combine(r.SaveRoot, "interaction-players.txt")))
            {
                GameObject cached = typeof(CoopRuntime).GetField("localPlayer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(r) as GameObject;
                w.WriteLine("CACHED_PLAYER=" + (cached == null ? "NONE" : GameProbe.PathOf(cached.transform) + " active=" + cached.activeSelf));
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (go.name == "PLAYER" || go.name == "FPSCamera")
                    {
                        w.WriteLine("OBJECT " + GameProbe.PathOf(go.transform) + " active=" + go.activeSelf + "/" + go.activeInHierarchy + " flags=" + go.hideFlags);
                        foreach (Component c in go.GetComponents<Component>()) w.WriteLine("COMPONENT " + (c == null ? "NULL" : c.GetType().Name));
                    }
            }
        }
        public static void Sample(CoopRuntime r, string reason)
        {
            if (!Enabled || r == null) return;
            Directory.CreateDirectory(r.SaveRoot);
            using (StreamWriter w = File.AppendText(Path.Combine(r.SaveRoot, "interaction-state.txt")))
            {
                w.WriteLine("t=" + Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture) + " " + reason + " scale=" + Time.timeScale + " joining=" + r.Joining + " mode=" + NativeHooks.UpdateMode);
                foreach (FsmBool v in FsmVariables.GlobalVariables.BoolVariables) if (v.Name.StartsWith("Player", StringComparison.Ordinal) || v.Name.StartsWith("Tool", StringComparison.Ordinal)) w.WriteLine("BOOL " + v.Name + "=" + v.Value);
                GameObject player = GameObject.Find("PLAYER");
                if (player == null) { w.WriteLine("PLAYER=NONE"); return; }
                w.WriteLine("PLAYER=" + player.transform.position + " parent=" + (player.transform.parent == null ? "NONE" : GameProbe.PathOf(player.transform.parent)));
                foreach (Behaviour c in player.GetComponentsInChildren<Behaviour>(true))
                    if (c.GetType().Name.IndexOf("Controller", StringComparison.Ordinal) >= 0 || c.GetType().Name.IndexOf("Motor", StringComparison.Ordinal) >= 0) w.WriteLine("CONTROL " + c.GetType().Name + " " + c.enabled + " active=" + c.gameObject.activeInHierarchy);
            }
        }
        private static void Dump(CoopRuntime r)
        {
            using (StreamWriter w = new StreamWriter(Path.Combine(r.SaveRoot, "interaction-fsms.txt")))
                foreach (PlayMakerFSM f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                {
                    if (f == null || !f.Fsm.Initialized || f.FsmStates == null) continue;
                    string path = GameProbe.PathOf(f.transform);
                    bool pick = f.FsmName == "PickUp" || f.FsmName == "Death" || path.IndexOf("JONNEZ", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("beer", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("wrench", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("PLAYER", StringComparison.Ordinal) >= 0 && (f.FsmName.IndexOf("Control", StringComparison.OrdinalIgnoreCase) >= 0 || f.FsmName.IndexOf("Move", StringComparison.OrdinalIgnoreCase) >= 0)
                        || path.IndexOf("starter", StringComparison.OrdinalIgnoreCase) >= 0 && f.FsmName == "Screw";
                    if (!pick) continue;
                    FsmBinding b = r.Registry.Binding(f.Fsm);
                    w.WriteLine("FSM " + path + " | " + f.FsmName + " name=" + f.Fsm.Name + " enabled=" + f.enabled + " active=" + f.gameObject.activeInHierarchy + " state=" + f.ActiveStateName + " binding=" + (b == null ? "NONE" : b.Id + " personal=" + b.Personal + " requests=" + string.Join(",", new System.Collections.Generic.List<string>(b.Requests).ToArray())));
                    foreach (FsmState state in f.FsmStates)
                    {
                        w.WriteLine("STATE " + state.Name + " local=" + NativeHooks.CanRunLocally(state));
                        foreach (FsmTransition t in state.Transitions) w.WriteLine("TRANS " + t.EventName + " -> " + t.ToState);
                        foreach (FsmStateAction a in state.Actions)
                        {
                            w.WriteLine("ACTION " + a.GetType().Name);
                            foreach (FieldInfo field in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                            {
                                object value = field.GetValue(a); NamedVariable v = value as NamedVariable;
                                FsmOwnerDefault owner = value as FsmOwnerDefault;
                                if (owner != null) { w.WriteLine("FIELD " + field.Name + " " + owner.OwnerOption + " " + owner.GameObject); continue; }
                                w.WriteLine("FIELD " + field.Name + " " + (v == null ? Convert.ToString(value, CultureInfo.InvariantCulture) : v.Name + "=" + v));
                            }
                        }
                    }
                }
        }
    }
}
