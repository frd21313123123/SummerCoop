using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace MWCoop
{
    // Geometry only: never enter a Screw state or replay its storage/tightness actions.
    public static class MscBoltPositions
    {
        sealed class Anchor
        {
            public GameObject Visual;
            public Vector3 Position, MeshPosition;
            public Quaternion Rotation, MeshRotation;
        }
        static readonly Dictionary<PlayMakerFSM, Anchor> anchors = new Dictionary<PlayMakerFSM, Anchor>();
        internal static void Reset() { anchors.Clear(); }
        internal static GameObject Visual(PlayMakerFSM f)
        {
            FsmGameObject variable = f.FsmVariables.FindFsmGameObject("ThisBolt");
            if (variable == null) return null;
            if (variable.Value != null && variable.Value.transform.parent == f.transform) return variable.Value;
            // Inactive native clones can still reference the cylinder-head template.
            // Resolve their own mesh before applying any pose.
            for (int i = 0; i < f.transform.childCount; i++)
            {
                Transform child = f.transform.GetChild(i);
                if (child.name.StartsWith("bolt", StringComparison.OrdinalIgnoreCase)) return child.gameObject;
            }
            return null;
        }

        static Transform Target(PlayMakerFSM f, FsmOwnerDefault owner, GameObject visual)
        {
            if (owner == null) return null;
            GameObject go = owner.OwnerOption == OwnerDefaultOption.UseOwner ? f.gameObject
                : owner.GameObject == null ? null : owner.GameObject.Name == "ThisBolt" ? visual : owner.GameObject.Value;
            // A stale reference must never move another part's bolt.
            return go != null && (go == f.gameObject || go == visual) ? go.transform : null;
        }

        internal static bool IsHoseClamp(PlayMakerFSM f)
        {
            for (Transform t = f.transform.parent; t != null; t = t.parent)
                if (t.name.StartsWith("radiator hose", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void PrepareClamp(PlayMakerFSM f)
        {
            // MSC's shared bolt FSM translates the screw mesh by 2.5 mm per
            // turn. A captive hose-clamp screw turns in place. Keep the native
            // zero-stage position for every stage, including local input.
            FsmState zero = f.Fsm.GetState("0 2") ?? f.Fsm.GetState("0");
            if (zero == null) return;
            SetPosition neutral = null;
            foreach (FsmStateAction action in zero.Actions) if (action is SetPosition) { neutral = (SetPosition)action; break; }
            if (neutral == null) return;
            for (int stage = 1; stage <= 8; stage++)
            {
                FsmState pose = f.Fsm.GetState(stage + " 2") ?? f.Fsm.GetState(stage.ToString());
                if (pose == null) continue;
                foreach (FsmStateAction action in pose.Actions)
                {
                    SetPosition position = action as SetPosition;
                    if (position == null) continue;
                    position.vector = neutral.vector;
                    position.x = neutral.x; position.y = neutral.y; position.z = neutral.z;
                    position.space = neutral.space;
                }
            }
        }

        internal static void Prepare(PlayMakerFSM f)
        {
            if (IsHoseClamp(f)) PrepareClamp(f);
            GameObject visual = Visual(f);
            if (visual == null) return;
            Anchor anchor;
            if (!anchors.TryGetValue(f, out anchor) || anchor.Visual != visual)
                anchors[f] = new Anchor { Visual = visual, Position = f.transform.localPosition, Rotation = f.transform.localRotation,
                    MeshPosition = visual.transform.localPosition, MeshRotation = visual.transform.localRotation };
            f.FsmVariables.FindFsmGameObject("ThisBolt").Value = visual;
            FsmString bolt = f.FsmVariables.FindFsmString("Bolt"), suffix = f.FsmVariables.FindFsmString("ArrayIndex");
            FsmInt index = f.FsmVariables.FindFsmInt("Index");
            if (bolt != null) bolt.Value = visual.name;
            string last = visual.name.Substring(visual.name.Length - 1);
            int number;
            if (int.TryParse(last, out number))
            {
                if (suffix != null) suffix.Value = last;
                if (index != null) index.Value = number;
            }
            // Some native clones also retain a constant target inside the pose
            // action, separate from the FSM's ThisBolt variable.
            for (int stage = 0; stage <= 8; stage++)
            {
                FsmState pose = f.Fsm.GetState(stage + " 2") ?? f.Fsm.GetState(stage.ToString());
                if (pose == null) continue;
                foreach (FsmStateAction action in pose.Actions)
                {
                    SetPosition position = action as SetPosition;
                    SetRotation rotation = action as SetRotation;
                    FsmOwnerDefault owner = position != null ? position.gameObject : rotation != null ? rotation.gameObject : null;
                    if (owner == null || owner.OwnerOption != OwnerDefaultOption.SpecifyGameObject || owner.GameObject == null) continue;
                    GameObject target = owner.GameObject.Value;
                    if (owner.GameObject.Name == "ThisBolt" || target != null && target.name.StartsWith("bolt", StringComparison.OrdinalIgnoreCase))
                        owner.GameObject.Value = visual;
                }
            }
        }

        internal static bool Restore(PlayMakerFSM f)
        {
            if (!MscParts.IsBolt(f)) return false;
            Prepare(f);
            GameObject visual = f.FsmVariables.FindFsmGameObject("ThisBolt") == null ? f.gameObject : Visual(f);
            if (visual == null) return false;
            int stage = f.FsmVariables.FindFsmInt("Stage").Value;
            if (stage < 0 || stage > 8) return false;
            FsmState pose = f.Fsm.GetState(stage + " 2") ?? f.Fsm.GetState(stage.ToString());
            if (pose == null) return false;
            Anchor anchor;
            if (anchors.TryGetValue(f, out anchor) && anchor.Visual == visual)
            {
                // Native poses often write only Z; X/Y and the mount orientation
                // are authored in the scene. Preserve them before the first replay.
                f.transform.localPosition = anchor.Position;
                f.transform.localRotation = anchor.Rotation;
                visual.transform.localPosition = anchor.MeshPosition;
                visual.transform.localRotation = anchor.MeshRotation;
            }
            bool restored = false;
            foreach (FsmStateAction action in pose.Actions)
            {
                SetPosition position = action as SetPosition;
                if (position != null)
                {
                    Transform target = Target(f, position.gameObject, visual);
                    if (target == null) continue;
                    Vector3 value = position.space == Space.Self ? target.localPosition : target.position;
                    if (position.vector != null && !position.vector.IsNone) value = position.vector.Value;
                    if (position.x != null && !position.x.IsNone) value.x = position.x.Value;
                    if (position.y != null && !position.y.IsNone) value.y = position.y.Value;
                    if (position.z != null && !position.z.IsNone) value.z = position.z.Value;
                    if (position.space == Space.Self) target.localPosition = value; else target.position = value;
                    restored = true;
                }
                SetRotation rotation = action as SetRotation;
                if (rotation != null)
                {
                    Transform target = Target(f, rotation.gameObject, visual);
                    if (target == null) continue;
                    Quaternion value = rotation.space == Space.Self ? target.localRotation : target.rotation;
                    if (rotation.quaternion != null && !rotation.quaternion.IsNone) value = rotation.quaternion.Value;
                    Vector3 angles = rotation.vector != null && !rotation.vector.IsNone ? rotation.vector.Value : value.eulerAngles;
                    if (rotation.xAngle != null && !rotation.xAngle.IsNone) angles.x = rotation.xAngle.Value;
                    if (rotation.yAngle != null && !rotation.yAngle.IsNone) angles.y = rotation.yAngle.Value;
                    if (rotation.zAngle != null && !rotation.zAngle.IsNone) angles.z = rotation.zAngle.Value;
                    value = Quaternion.Euler(angles);
                    if (rotation.space == Space.Self) target.localRotation = value; else target.rotation = value;
                }
            }
            return restored;
        }

        public static string RestoreAll()
        {
            if (!PlayerSync.InGame) return Lang.R("Load the game first.");
            int restored = 0, failed = 0;
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None || Game.UnderPlayer(f.transform) || !MscParts.IsBolt(f)
                    || f.FsmVariables.FindFsmGameObject("ThisBolt") == null) continue;
                // Include hidden mounted suspension, but not shop/prefab templates.
                string root = f.transform.root.name;
                if (root == "STORE" || !f.Fsm.Initialized && !root.StartsWith("SATSUMA", StringComparison.Ordinal) && root != "CARPARTS") continue;
                try
                {
                    if (!f.Fsm.Initialized) f.Fsm.InitData();
                    if (Restore(f)) restored++;
                }
                catch (Exception e) { failed++; Log.Warn("MSC bolt position repair: " + Recon.Path(f.transform) + ": " + e.Message); }
            }
            Log.Info("MSC bolt positions restored=" + restored + ";failed=" + failed);
            return Lang.R("Bolt positions restored: ") + restored + (failed > 0 ? Lang.R("; failed: ") + failed : "");
        }
    }
}
