using System;
using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Native MSC bucket, with one authoritative fermentation and reliable,
    // ordered input requests. Peers never overwrite each other's whole recipe.
    public static class MscBrewing
    {
        public const int Request = 8, Snapshot = 9;
        const int Sugar = 0, Yeast = 1, Water = 2, LidOn = 3, LidOff = 4, Bottle = 5, Empty = 6, Refresh = 7, WaterStart = 8;
        const string Module = "msc-brewing";
        static readonly string[] floats = { "Alcohol", "Sugar", "Sweetness", "Time", "Vinegar", "Water", "Yeast" };
        static readonly HashSet<PlayMakerFSM> hooked = new HashSet<PlayMakerFSM>();
        static readonly HashSet<string> ingredients = new HashSet<string>(StringComparer.Ordinal);
        static readonly List<FsmStateAction> disabled = new List<FsmStateAction>();
        static MscEditClock clock = new MscEditClock();
        static PlayMakerFSM brew, ingredient, filling, water, assembly, removal;
        static GameObject bucket, lid;
        static bool applying, leftHooked, replicaMuted;
        static float nextFind, nextSend;
        static int sequence, waterOwner;
        static BrewState previous;
        public static PlayMakerFSM Bucket { get { return brew; } }
        public static bool Owns(PlayMakerFSM f)
        {
            if (f == null) return false;
            string name = f.gameObject.name;
            if (name == "bucket lid(itemx)") return f.FsmName == "Removal";
            if (name == "bucket(itemx)") return f.FsmName == "Use" || f.FsmName == "Empty";
            Transform parent = f.transform.parent;
            return parent != null && parent.name == "bucket(itemx)" &&
                (name == "TriggerIngredients" || name == "TriggerFill" || name == "BucketTrigger" || name == "trigger_lid");
        }
        public static void Reset()
        {
            foreach (FsmStateAction action in disabled) if (action != null) action.Enabled = true;
            disabled.Clear(); hooked.Clear(); ingredients.Clear(); clock = new MscEditClock();
            brew = ingredient = filling = water = assembly = removal = null; bucket = lid = null;
            applying = replicaMuted = false; nextFind = nextSend = 0; sequence = waterOwner = 0; previous = new BrewState();
        }
        sealed class Hook : ModHook
        {
            public override string Module { get { return MscBrewing.Module; } }
            public PlayMakerFSM F; public FsmState State; public int Operation; public bool InputOnly;
            public override void OnEnter()
            {
                bool cancel = false;
                try { cancel = Enter(this); } catch (Exception e) { Replay.HookError(e); }
                if (!cancel) Finish();
            }
        }
        static void HookState(PlayMakerFSM f, string state, int operation, bool inputOnly)
        {
            if (f == null || !f.Fsm.Initialized) return;
            FsmState native = f.Fsm.GetState(state); if (native == null) throw new InvalidOperationException("MSC kilju state missing: " + f.FsmName + "/" + state);
            FireTools.Insert(f, native, new Hook { F = f, State = native, Operation = operation, InputOnly = inputOnly }, false);
        }
        static PlayMakerFSM Child(GameObject go, string child, string fsm)
        {
            Transform t = go != null ? go.transform.Find(child) : null; return t != null ? Game.FsmOn(t.gameObject, fsm) : null;
        }
        static void Find()
        {
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (!Owns(f) || f.hideFlags != HideFlags.None || !f.transform.root.gameObject.activeInHierarchy) continue;
                if (!Replay.Claim(f, Module)) throw new InvalidOperationException("MSC kilju FSM already owned by " + Replay.Owner(f));
                if (f.gameObject.name == "bucket(itemx)" && f.FsmName == "Use") { bucket = f.gameObject; brew = f; }
                if (f.gameObject.name == "bucket lid(itemx)") { lid = f.gameObject; removal = f; }
            }
            if (brew == null) return;
            ingredient = Child(bucket, "TriggerIngredients", "Level"); filling = Child(bucket, "TriggerFill", "Fill");
            water = Child(bucket, "BucketTrigger", "Level"); assembly = Child(bucket, "trigger_lid", "Assembly");
            if (ingredient != null && ingredient.Fsm.Initialized && hooked.Add(ingredient)) { HookState(ingredient, "Sugar", Sugar, false); HookState(ingredient, "Yeast", Yeast, false); }
            if (filling != null && filling.Fsm.Initialized && hooked.Add(filling)) { HookState(filling, "Fill", Bottle, false); HookState(filling, "Empty bucket", Empty, false); }
            if (water != null && water.Fsm.Initialized && hooked.Add(water)) { HookState(water, "Tap", Water, true); HookState(water, "Lake", Water, true); HookState(water, "Dilute", Water, false); }
            if (assembly != null && assembly.Fsm.Initialized && hooked.Add(assembly)) HookState(assembly, "Assemble", LidOn, false);
            if (removal != null && removal.Fsm.Initialized && hooked.Add(removal)) HookState(removal, "Remove part", LidOff, false);
            if (brew.Fsm.Initialized && hooked.Add(brew))
            {
                if (!Session.IsHost) { DisableReplicaWrites(); replicaMuted = true; }
                previous = Capture();
                if (!Session.IsHost) SendRequest(Refresh, "", 0);
                Log.Info("MSC kilju: native bucket resolved, host owns fermentation");
            }
        }
        static void DisableReplicaWrites()
        {
            foreach (FsmState state in brew.Fsm.States)
                foreach (FsmStateAction action in state.Actions)
                {
                    if (action is FloatAdd || action is FloatSubtract || action is FloatClamp || action is SetFloatValue || action is FloatOperator || action is SetBoolValue)
                    {
                        NamedVariable target = SummerCoop.Game.NativeActionReader.Field(action, "floatVariable") as NamedVariable
                            ?? SummerCoop.Game.NativeActionReader.Field(action, "storeResult") as NamedVariable
                            ?? SummerCoop.Game.NativeActionReader.Field(action, "boolVariable") as NamedVariable;
                        if (target != null && (Array.IndexOf(floats, target.Name) >= 0 || target.Name == "KiljuFinished")) { action.Enabled = false; disabled.Add(action); }
                    }
                }
        }
        static bool Enter(Hook hook)
        {
            if (!Session.Active || applying || Replay.Depth > 0) return false;
            GameObject item = hook.Operation == Sugar || hook.Operation == Yeast || hook.Operation == Bottle ? ObjectVariable(hook.F, "Object") : bucket;
            if (hook.Operation == LidOn || hook.Operation == LidOff) item = lid;
            bool local = Home.CausedHere(item, 60f) && !(hook.InputOnly && Session.IsHost && waterOwner > 0 && Session.Players.ContainsKey(waterOwner));
            if (!local)
            {
                Log.Info("MSC kilju: input belongs to the other mover, skipped " + hook.State.Name);
                FireTools.Cancel(hook.F, hook.State, hook, hook.F == ingredient || hook.F == filling || hook.F == water ? "Wait" : hook.F == assembly ? "Wait" : "Mouse off");
                return true;
            }
            if (hook.InputOnly) { if (!Session.IsHost) SendRequest(WaterStart, "", 0); return false; }
            float amount = hook.Operation == Water ? FireTools.GetF(water, "AddedWater", 0) : 0;
            if (hook.Operation == Water && amount <= .0001f) return false;
            string id = item != null && (hook.Operation == Sugar || hook.Operation == Yeast || hook.Operation == Bottle) ? Identity(item) : "";
            if (Session.IsHost)
            {
                if ((hook.Operation == Sugar || hook.Operation == Yeast) && id.Length > 0) ingredients.Add(id);
                nextSend = 0; return false;
            }
            SendRequest(hook.Operation, id, amount);
            FireTools.Cancel(hook.F, hook.State, hook, hook.F == ingredient || hook.F == filling || hook.F == water ? "Wait" : hook.F == assembly ? "Wait" : "Mouse off");
            return true;
        }
        static GameObject ObjectVariable(PlayMakerFSM f, string name)
        { FsmGameObject value = f != null ? f.FsmVariables.FindFsmGameObject(name) : null; return value != null ? value.Value : null; }
        static string Identity(GameObject item)
        {
            string id = Props.ItemId(item); if (id.Length > 0) return id;
            Rigidbody body = item.GetComponent<Rigidbody>(); return body != null ? Props.WorldKeyOf(body) ?? "" : "";
        }
        static void SendRequest(int operation, string id, float amount)
        { Session.SendAll(new NetWriter(Msg.Home).U8(Session.LocalId).U8(Request).I32(++sequence).U8(operation).Str(id).F32(amount), true); }
        public static void Update()
        {
            if (!Session.Active || Session.IsHost)
            {
                foreach (FsmStateAction action in disabled) if (action != null) action.Enabled = true;
                disabled.Clear(); replicaMuted = false;
            }
            if (!Session.Active || !PlayerSync.InGame || Time.timeSinceLevelLoad < 6) return;
            if (!Session.IsHost && brew != null && !replicaMuted && brew.Fsm.Initialized) { DisableReplicaWrites(); replicaMuted = true; }
            if (!leftHooked)
            {
                leftHooked = true;
                Session.PlayerLeft += player => { clock.ForgetActor(player.Id); if (waterOwner == player.Id) waterOwner = 0; };
            }
            float now = Time.realtimeSinceStartup;
            if (now >= nextFind) { nextFind = now + 1; Find(); }
            if (brew == null || !hooked.Contains(brew) || !Session.IsHost || now < nextSend) return;
            BrewState current = Capture();
            if (!current.Equals(previous) || now >= nextSend) Publish("");
        }
        public static BrewState Capture()
        {
            return new BrewState { Alcohol = FireTools.GetF(brew, "Alcohol", 0), Sugar = FireTools.GetF(brew, "Sugar", 0), Sweetness = FireTools.GetF(brew, "Sweetness", 0), Time = FireTools.GetF(brew, "Time", 0), Vinegar = FireTools.GetF(brew, "Vinegar", 0), Water = FireTools.GetF(brew, "Water", 0), Yeast = FireTools.GetF(brew, "Yeast", 0), Finished = Bool(brew, "KiljuFinished"), LidOn = Bool(brew, "LidOn") };
        }
        static bool Bool(PlayMakerFSM f, string name) { FsmBool value = f != null ? f.FsmVariables.FindFsmBool(name) : null; return value != null && value.Value; }
        static void Publish(string can)
        {
            if (brew == null) return;
            BrewState state = Capture();
            var w = new NetWriter(Msg.Home).U8(Session.LocalId).U8(Snapshot).I32(clock.Publish(true)); state.Write(w); w.Str(can);
            if (can.Length > 0)
            {
                GameObject go = Props.ObjectOf(can); PlayMakerFSM use = go != null ? Game.FsmOn(go, "Use") : null;
                foreach (string name in new[] { "KiljuAlc", "KiljuSweetness", "KiljuVinegar", "KiljuYeast" }) w.F32(FireTools.GetF(use, name, 0));
            }
            Session.SendAll(w, true); previous = state; nextSend = Time.realtimeSinceStartup + .5f;
        }
        public static void OnMessage(Peer from, int kind, NetReader r)
        {
            if (kind == Request)
            {
                int revision = r.I32(), operation = r.U8(); string id = r.Str(); float amount = r.F32();
                if (operation > WaterStart || revision <= 0 || id.Length > 256 || amount < 0 || amount > 32 || r.More) throw new IOException("Invalid kilju request");
                if (!Session.IsHost || from == null || !clock.AcceptRequest(from.Id, revision)) return;
                Find(); if (brew == null) return;
                if (operation == WaterStart) { waterOwner = from.Id; return; }
                bool changed = Apply(operation, id, amount); Publish(operation == Bottle && changed ? id : "");
                Log.Info("MSC kilju: request #" + from.Id + "/" + revision + " operation=" + operation + ";amount=" + amount + ";applied=" + changed);
            }
            else
            {
                int revision = r.I32(); BrewState state = BrewState.Read(r); string can = r.Str();
                float[] contents = can.Length > 0 ? new[] { r.F32(), r.F32(), r.F32(), r.F32() } : null;
                if (r.More || can.Length > 256) throw new IOException("Invalid kilju snapshot");
                if (Session.IsHost || from == null || from.Id != 0 || !clock.AcceptSnapshot(revision)) return;
                Find(); if (brew == null) return;
                ApplySnapshot(state, can, contents);
            }
        }
        static GameObject Resolve(string id)
        {
            GameObject found = Props.ObjectOf(id); if (found != null) return found;
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                var f = item as PlayMakerFSM;
                if (f == null || f.FsmName != "Use" || f.hideFlags != HideFlags.None || Props.ItemId(f.gameObject) != id) continue;
                Props.Track(f.gameObject); Consume.Track(f.gameObject); return f.gameObject;
            }
            return null;
        }
        static bool Apply(int operation, string id, float amount)
        {
            GameObject item = id.Length > 0 ? Resolve(id) : null;
            applying = true;
            try
            {
                if (operation == Sugar || operation == Yeast)
                {
                    if (item == null || !item.name.StartsWith(operation == Sugar ? "sugar" : "yeast", StringComparison.Ordinal) || !ingredients.Add(id)) return false;
                    ingredient.FsmVariables.FindFsmGameObject("Object").Value = item;
                    Game.SetState(ingredient, operation == Sugar ? "Sugar" : "Yeast");
                }
                else if (operation == Water)
                {
                    waterOwner = 0;
                    float before = FireTools.GetF(brew, "Water", 0); amount = Mathf.Min(amount, 30 - before);
                    FireTools.SetF(brew, "Water", before + amount); FireTools.SetF(water, "AddedWater", amount); FireTools.SetF(water, "WaterLevel", before);
                    Game.SetState(water, "Dilute");
                }
                else if (operation == LidOn || operation == LidOff) ApplyLid(operation == LidOn);
                else if (operation == Bottle && item != null && !Bool(Game.FsmOn(item, "Use"), "ContainsKilju") && !Bool(Game.FsmOn(item, "Use"), "ContainsJuice") && FireTools.GetF(brew, "Water", 0) >= 1.5f && !Capture().LidOn)
                { filling.FsmVariables.FindFsmGameObject("Object").Value = item; Game.SetState(filling, "Fill"); }
                else if (operation == Empty) { Game.SetState(filling, "Empty bucket"); }
                else if (operation != Refresh) return false;
                return true;
            }
            finally { applying = false; }
        }
        static void ApplyLid(bool on)
        {
            if (Bool(brew, "LidOn") == on) return;
            PlayMakerFSM f = on ? assembly : removal;
            if (f == null) return;
            FireTools.Resume(f);
            f.gameObject.SetActive(true); f.enabled = true;
            if (!f.Fsm.Initialized) f.Fsm.InitData();
            Game.SetState(f, on ? "Assemble" : "Remove part"); Props.SoonScan();
        }
        static void ApplySnapshot(BrewState state, string can, float[] contents)
        {
            applying = true; Replay.Depth++;
            try
            {
                ApplyLid(state.LidOn);
                bool refresh = state.Finished != Bool(brew, "KiljuFinished") || Mathf.Abs(state.Sugar - FireTools.GetF(brew, "Sugar", 0)) > .01f || Mathf.Abs(state.Yeast - FireTools.GetF(brew, "Yeast", 0)) > .01f;
                float[] values = { state.Alcohol, state.Sugar, state.Sweetness, state.Time, state.Vinegar, state.Water, state.Yeast };
                for (int i = 0; i < floats.Length; i++) FireTools.SetF(brew, floats[i], values[i]);
                brew.FsmVariables.FindFsmBool("KiljuFinished").Value = state.Finished;
                brew.FsmVariables.FindFsmBool("LidOn").Value = state.LidOn;
                if (refresh) Game.SetState(brew, "State 3");
                if (contents != null)
                {
                    GameObject item = Props.ObjectOf(can); PlayMakerFSM use = item != null ? Game.FsmOn(item, "Use") : null;
                    if (use != null)
                    {
                        string[] names = { "KiljuAlc", "KiljuSweetness", "KiljuVinegar", "KiljuYeast" };
                        for (int i = 0; i < names.Length; i++) FireTools.SetF(use, names[i], contents[i]);
                        FsmBool full = use.FsmVariables.FindFsmBool("ContainsKilju"); if (full != null) full.Value = true;
                        use.SendEvent("GLOBALEVENT");
                    }
                }
            }
            finally { Replay.Depth--; applying = false; }
        }
    }
}
