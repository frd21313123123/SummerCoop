using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    public static class MscIsland
    {
        public const int Request = 12, Snapshot = 13;
        const int AddWood = 0, TakeWood = 1, LightOn = 2, LightOff = 3, Water = 4;
        sealed class Item
        {
            public string Key, Kind; public GameObject Root; public PlayMakerFSM Fsm;
            public readonly MscEditClock Clock = new MscEditClock();
            public readonly Dictionary<int, int> Acks = new Dictionary<int, int>();
            public readonly Dictionary<int, float> PendingWater = new Dictionary<int, float>();
            public readonly HashSet<string> Used = new HashSet<string>();
            public int Count; public bool On; public float LastWater, Next;
        }
        sealed class Hook : ModHook
        {
            public Item Item; public int Op; public PlayMakerFSM Fsm; public FsmState State; public string Back;
            public override string Module { get { return "msc-island"; } }
            public override void OnEnter()
            {
                if (!Session.Active || Replay.Depth > 0 || applying) { Finish(); return; }
                if (Op == Water)
                {
                    if (Home.CausedHere(Item.Root, 5)) { Finish(); return; }
                }
                else
                {
                    GameObject wood = Op == AddWood ? Obj(Fsm, "FirewoodObject") : null;
                    bool valid = wood != null && wood.name.StartsWith("firewood", StringComparison.Ordinal)
                        && wood.transform.parent == null && FireTools.Held() != wood && Item.Count < 9;
                    if (Op != AddWood || valid && Home.CausedHere(wood, 5))
                    {
                        // A freshly split local log may not yet be in Props' next
                        // census. Register it before the host resolves the edit.
                        if (wood != null) Props.Track(wood);
                        Send(Item, Op, wood != null ? Props.ItemId(wood) : "", 0);
                    }
                }
                FireTools.Cancel(Fsm, State, this, Back);
            }
        }
        static readonly Dictionary<string, Item> items = new Dictionary<string, Item>();
        static readonly HashSet<PlayMakerFSM> hooked = new HashSet<PlayMakerFSM>();
        static readonly HashSet<string> births = new HashSet<string>();
        sealed class Edit { public Item Item; public int Op, Seq, Frame; public string Id; public float Amount; }
        static readonly List<Edit> edits = new List<Edit>();
        static float scan, poll;
        static int birth;
        static bool applying;
        static bool bound;
        static GameObject Obj(PlayMakerFSM f, string name) { var v = f != null ? f.FsmVariables.FindFsmGameObject(name) : null; return v != null ? v.Value : null; }
        public static bool Owns(PlayMakerFSM f)
        {
            if (f == null) return false;
            for (Transform t = f.transform; t != null; t = t.parent)
                if (t.name == "wood carrier(itemx)" || t.name == "water bucket(itemx)" || t.name == "lantern(itemx)") return true;
            return false;
        }
        public static void Reset() { items.Clear(); hooked.Clear(); births.Clear(); edits.Clear(); scan = poll = 0; birth = 0; }
        public static void Update()
        {
            if (!Session.Active || !PlayerSync.InGame) return;
            if (!bound) { bound = true; Session.PlayerLeft += delegate(PlayerInfo p) { if (p != null) foreach (Item item in items.Values) { item.Clock.ForgetActor(p.Id); item.Acks.Remove(p.Id); } }; }
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < edits.Count; i++)
            {
                Edit e = edits[i]; if (Time.frameCount <= e.Frame) continue; edits.RemoveAt(i--);
                if (Session.IsHost) Apply(e.Item, 0, e.Seq, e.Op, e.Id, e.Amount);
                else Session.SendAll(new NetWriter(Msg.Home).U8(Session.LocalId).U8(Request).Str(e.Item.Key).I32(e.Seq).U8(e.Op).Str(e.Id).F32(e.Amount), true);
            }
            if (now >= scan)
            {
                scan = now + 3;
                foreach (UnityEngine.Object value in Game.AllFsms())
                {
                    PlayMakerFSM f = value as PlayMakerFSM;
                    if (f == null || f.hideFlags != HideFlags.None || hooked.Contains(f)) continue;
                    if (f.FsmName == "Trigger" && f.gameObject.name == "WoodTrigger" && f.transform.parent.name == "wood carrier(itemx)")
                    {
                        var tag = f.FsmVariables.FindFsmString("UniqueTagWood"); if (tag == null || tag.Value.Length == 0) continue;
                        Item item = Register("carrier:" + tag.Value, "carrier", f.transform.parent.gameObject, f);
                        item.Count = f.FsmVariables.GetFsmInt("WoodsIndex").Value;
                        AddHook(item, f, "Parent", AddWood, "Wait wood");
                        AddHook(item, f, "Fetch array", AddWood, "Wait wood");
                        PlayMakerFSM pick = Game.FsmOn(item.Root.transform.Find("PickupTrigger").gameObject, "Use");
                        AddHook(item, pick, "Bool test", TakeWood, "Wait player");
                    }
                    else if (f.FsmName == "Use" && f.gameObject.name == "lantern(itemx)")
                    {
                        Item item = Register("lamp:" + Props.WorldKeyOf(f.GetComponent<Rigidbody>()), "lamp", f.gameObject, f);
                        item.On = f.transform.Find("light").gameObject.activeSelf;
                        AddHook(item, f, "ON", LightOn, "Wait player"); AddHook(item, f, "OFF", LightOff, "Wait player 2");
                    }
                    else if (f.FsmName == "Level" && f.gameObject.name == "Water" && f.transform.parent.name == "water bucket(itemx)")
                    {
                        GameObject root = f.transform.parent.gameObject;
                        Item item = Register("water:" + Props.WorldKeyOf(root.GetComponent<Rigidbody>()), "water", root, f);
                        item.LastWater = FireTools.GetF(f, "Water", 0);
                        var fill = Game.FsmOn(root.transform.Find("BucketTrigger").gameObject, "Level");
                        AddHook(item, fill, "Tap", Water, "Wait"); AddHook(item, fill, "Lake", Water, "Wait");
                    }
                }
            }
            if (now < poll) return; poll = now + .2f;
            foreach (Item item in items.Values)
            {
                if (item.Fsm == null) continue;
                if (item.Kind == "carrier")
                {
                    // Native Parent rejects velocity < .001. Network replicas
                    // can have zero velocity while the host puts a log down;
                    // ownership and an unparented split log identify the deposit.
                    AddHook(item, item.Fsm, "Parent", AddWood, "Wait wood");
                    AddHook(item, item.Fsm, "Fetch array", AddWood, "Wait wood");
                    AddHook(item, Game.FsmOn(item.Root.transform.Find("PickupTrigger").gameObject, "Use"), "Bool test", TakeWood, "Wait player");
                }
                if (item.Kind == "water") FlushWater(item);
                if (Session.IsHost && now >= item.Next) { item.Next = now + 2; Publish(item, "", "", Vector3.zero, Quaternion.identity); }
            }
        }
        static Item Register(string key, string kind, GameObject root, PlayMakerFSM f)
        {
            Item result;
            if (items.TryGetValue(key, out result)) return result;
            result = new Item { Key = key, Kind = kind, Root = root, Fsm = f }; items[key] = result;
            hooked.Add(f); Replay.Claim(f, "msc-island"); return result;
        }
        static void AddHook(Item item, PlayMakerFSM f, string state, int op, string back)
        {
            if (f == null) return;
            FsmState st = f.Fsm.GetState(state); if (st == null) return;
            if (!f.gameObject.activeInHierarchy || !st.IsInitialized) return;
            foreach (FsmStateAction action in FireTools.Actions(f, st)) if (action is Hook) return;
            FireTools.Insert(f, st, new Hook { Item = item, Fsm = f, State = st, Op = op, Back = back }, false);
            Replay.Claim(f, "msc-island");
        }
        static void Send(Item item, int op, string id, float amount)
        {
            int seq = item.Clock.Publish(false);
            if (op == Water && !Session.IsHost) item.PendingWater[seq] = amount;
            edits.Add(new Edit { Item = item, Op = op, Id = id, Seq = seq, Amount = amount, Frame = Time.frameCount });
        }
        static void FlushWater(Item item)
        {
            float water = FireTools.GetF(item.Fsm, "Water", 0), delta = water - item.LastWater;
            item.LastWater = water;
            if (float.IsNaN(delta) || float.IsInfinity(delta) || Mathf.Abs(delta) < .00001f || Mathf.Abs(delta) > 5) return;
            if (Session.IsHost) return;
            Send(item, Water, "", delta);
        }
        static void Apply(Item item, int actor, int seq, int op, string id, float amount)
        {
            if (!item.Clock.AcceptRequest(actor, seq)) return;
            item.Acks[actor] = seq;
            Log.Info("MSC island: " + item.Key + ";actor=" + actor + ";seq=" + seq + ";op=" + op + ";amount=" + amount);
            string consumed = "", made = ""; Vector3 pos = Vector3.zero; Quaternion rot = Quaternion.identity;
            if (op == AddWood && item.Kind == "carrier" && item.Count < 9 && id.Length > 0 && !item.Used.Contains(id))
            {
                GameObject wood = Props.ObjectOf(id);
                if (wood != null && wood.name.StartsWith("firewood", StringComparison.Ordinal))
                { item.Used.Add(id); item.Count++; consumed = id; UnityEngine.Object.Destroy(wood); }
            }
            if (op == TakeWood && item.Kind == "carrier" && item.Count > 0)
            {
                item.Count--; made = "msc-carrier-" + item.Key + "-" + (++birth);
                Transform point = item.Root.transform.Find("PickupTrigger"); pos = point.position; rot = point.rotation;
                SpawnWood(item, made, pos, rot);
            }
            if (item.Kind == "lamp" && (op == LightOn || op == LightOff))
            {
                item.On = op == LightOn; applying = true; Replay.Depth++;
                try { FireTools.Resume(item.Fsm); Game.SetState(item.Fsm, item.On ? "ON" : "OFF"); }
                finally { applying = false; Replay.Depth--; }
            }
            if (item.Kind == "water" && op == Water)
            {
                FlushWater(item);
                float value = Mathf.Clamp(FireTools.GetF(item.Fsm, "Water", 0) + amount, 0, 5);
                FireTools.SetF(item.Fsm, "Water", value); item.LastWater = value;
            }
            Visual(item); Publish(item, consumed, made, pos, rot);
        }
        static void Visual(Item item)
        {
            if (item.Kind != "carrier") return;
            item.Fsm.FsmVariables.GetFsmInt("WoodsIndex").Value = item.Count;
            Transform woods = item.Root.transform.Find("Woods"); int i = 0;
            foreach (Transform wood in woods) wood.gameObject.SetActive(i++ < item.Count);
            item.Root.transform.Find("PickupTrigger").gameObject.SetActive(item.Count > 0);
        }
        static void SpawnWood(Item item, string id, Vector3 pos, Quaternion rot)
        {
            if (id.Length == 0 || !births.Add(id)) return;
            GameObject wood = (GameObject)UnityEngine.Object.Instantiate(Obj(item.Fsm, "Prefab"), pos, rot);
            GameObject equipment = Game.FindAny("ITEMS"); if (equipment != null) wood.transform.parent = equipment.transform;
            PlayMakerFSM identity = wood.AddComponent<PlayMakerFSM>(); identity.FsmName = "MWCoopId";
            identity.FsmVariables.StringVariables = new[] { new FsmString("ID") { Value = id } };
            Props.Adopt(wood, id); Consume.Track(wood);
        }
        static void Publish(Item item, string consumed, string made, Vector3 pos, Quaternion rot)
        {
            var w = new NetWriter(Msg.Home).U8(0).U8(Snapshot).Str(item.Key).I32(item.Clock.Publish(true))
                .U8(item.Count).Bool(item.Kind == "lamp" && item.Root.transform.Find("light").gameObject.activeSelf)
                .F32(FireTools.GetF(item.Fsm, "Burning", 0)).F32(FireTools.GetF(item.Fsm, "Burntime", 0))
                .F32(FireTools.GetF(item.Fsm, "Water", 0)).Str(consumed).Str(made).Vec(pos).Quat(rot).U8(item.Acks.Count);
            foreach (var ack in item.Acks) w.U8(ack.Key).I32(ack.Value);
            Session.SendAll(w, true);
        }
        public static void OnMessage(Peer from, int kind, NetReader r)
        {
            string key = r.Str(); int seq = r.I32(); Item item;
            if (kind == Request)
            {
                int op = r.U8(); string id = r.Str(); float amount = r.F32();
                if (!Session.IsHost || from == null || op > Water || float.IsNaN(amount) || float.IsInfinity(amount) || Mathf.Abs(amount) > 5 || !items.TryGetValue(key, out item)) return;
                Apply(item, from.Id, seq, op, id, amount); return;
            }
            int count = r.U8(); bool on = r.Bool(); float burning = r.F32(), burntime = r.F32(), water = r.F32();
            string consumed = r.Str(), made = r.Str(); Vector3 pos = r.Vec(); Quaternion rot = r.Quat();
            int ackCount = r.U8(), acked = 0;
            for (int i = 0; i < ackCount; i++) { int actor = r.U8(), ack = r.I32(); if (actor == Session.LocalId) acked = ack; }
            if (Session.IsHost || from == null || from.Id != 0 || count > 9 || !items.TryGetValue(key, out item)) return;
            if (consumed.Length > 0) { GameObject wood = Props.ObjectOf(consumed); if (wood != null) UnityEngine.Object.Destroy(wood); }
            SpawnWood(item, made, pos, rot);
            if (!item.Clock.AcceptSnapshot(seq)) return;
            if (item.Kind == "water")
            {
                FlushWater(item);
                var remove = new List<int>(); float pending = 0;
                foreach (var pair in item.PendingWater) if (pair.Key <= acked) remove.Add(pair.Key); else pending += pair.Value;
                foreach (int n in remove) item.PendingWater.Remove(n);
                FireTools.SetF(item.Fsm, "Water", Mathf.Clamp(water + pending, 0, 5)); item.LastWater = FireTools.GetF(item.Fsm, "Water", 0);
            }
            item.Count = count; Visual(item);
            if (item.Kind == "lamp")
            {
                applying = true; Replay.Depth++;
                try
                {
                    if (item.On != on) { FireTools.Resume(item.Fsm); Game.SetState(item.Fsm, on ? "ON" : "OFF"); }
                    item.On = on; FireTools.SetF(item.Fsm, "Burning", burning); FireTools.SetF(item.Fsm, "Burntime", burntime);
                }
                finally { applying = false; Replay.Depth--; }
            }
        }
    }
}
