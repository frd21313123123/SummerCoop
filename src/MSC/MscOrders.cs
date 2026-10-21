using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;
namespace MWCoop
{
    // Shared native orders; GUI selections and payments remain local. Only
    // accepted orders cross the wire. Native jobs and packages perform the work.
    public static class MscOrders
    {
        public const string RepairPath = "REPAIRSHOP/Order", CatalogPath = "Sheets/OrderList";
        const string Module = "msc-orders";
        static PlayMakerFSM repair, brochure, catalog, timer, postbox, payment;
        static FsmBool[] services;
        static readonly Queue<MscOrderMessage> incoming = new Queue<MscOrderMessage>();
        static readonly HashSet<string> requests = new HashSet<string>();
        static readonly HashSet<string> packageNames = new HashSet<string>(StringComparer.Ordinal);
        static readonly Dictionary<int, MscOrderMessage> pending = new Dictionary<int, MscOrderMessage>();
        static MscOrderMessage repairOrder, catalogOrder;
        static int ticket, revision, applied, randomSeed;
        static float nextBind, nextSync, workUntil;
        static bool bound, applying, posting, baseline, instantWork;
        public static int SavedSequence { get; private set; }
        public static bool Unsaved { get { return revision > SavedSequence; } }
        public static bool Busy { get { return applying || Time.realtimeSinceStartup < workUntil; } }
        public static bool Bound { get { return bound; } }
        public static string CatalogEnvelopeKey(GameObject go)
        {
            FsmGameObject value = catalog != null ? catalog.FsmVariables.FindFsmGameObject("ThisEnvelope") : null;
            return value != null && value.Value == go ? "msc:catalog-envelope" : null;
        }
        public static void MarkSaved() { if (Session.IsHost) SavedSequence = revision; }
        public static void ReceiveSaveSequence(int value) { if (value < 0) throw new System.IO.IOException("Invalid order save sequence"); SavedSequence = value; baseline = true; }
        public static bool Owns(PlayMakerFSM f)
        {
            if (f == null) return false;
            string p = Recon.Path(f.transform);
            return p == RepairPath || p == "REPAIRSHOP/LOD/Office/Fleetari/Brochure" || p.StartsWith("REPAIRSHOP/Jobs/", StringComparison.Ordinal)
                || p.StartsWith("Sheets/ServiceBrochure", StringComparison.Ordinal)
                || p == CatalogPath || p.StartsWith(CatalogPath + "/", StringComparison.Ordinal)
                || p == "STORE/LOD/Post Box/OrderTrigger" || p == "STORE/LOD/ActivateStore/PostOffice/PostOrderBuy";
        }
        public static void OnLevelLoaded()
        {
            int saved = !Session.IsHost && PlayerSync.InGame && baseline ? SavedSequence : 0;
            repair = brochure = catalog = timer = postbox = payment = null; services = null;
            repairOrder = catalogOrder = null; requests.Clear(); pending.Clear(); packageNames.Clear();
            bound = applying = posting = baseline = instantWork = false; revision = applied = SavedSequence = saved;
            nextBind = nextSync = workUntil = 0; ticket = UnityEngine.Random.Range(1, 1000000) * 1000;
            if (!PlayerSync.InGame) incoming.Clear();
        }
        static PlayMakerFSM Find(string p, string name) { return Game.FsmOn(Game.FindAny(p), name); }
        static bool Bind()
        {
            repair = Find(RepairPath, "Data"); brochure = Find("Sheets/ServiceBrochure/ButtonOrder", "Button");
            catalog = Find(CatalogPath, "Data"); timer = Find(CatalogPath + "/Timer", "Delivery");
            postbox = Find("STORE/LOD/Post Box/OrderTrigger", "Open"); payment = Find("STORE/LOD/ActivateStore/PostOffice/PostOrderBuy", "Use");
            if (repair == null || brochure == null || catalog == null || timer == null || postbox == null || payment == null) return false;
            foreach (PlayMakerFSM f in new[] { repair, brochure, catalog, timer, postbox, payment }) f.Fsm.InitData();
            if (MscShop.List(catalog.gameObject, "Delivering") == null) return false;
            var vars = new List<FsmBool>();
            foreach (FsmBool b in repair.FsmVariables.BoolVariables) if (!b.Name.StartsWith("_") && b.Name != "FleetariCarCall") vars.Add(b);
            vars.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name)); services = vars.ToArray();
            if (services.Length > 64) throw new InvalidOperationException("Unexpected Fleetari service table");
            GameObject boxes = Game.FindAny("STORE/Boxes");
            if (boxes != null) foreach (Transform child in boxes.transform) packageNames.Add(child.name);
            repairOrder = Capture(MscOrderMessage.Fleetari); catalogOrder = Capture(MscOrderMessage.Catalog);
            foreach (PlayMakerFSM f in new[] { repair, brochure, catalog, timer, postbox, payment }) Replay.Claim(f, Module);
            Hook(brochure, "Date", 0, false);
            Hook(repair, "Work", 1, true); Hook(repair, "Reset order", 2, false);
            Hook(postbox, "Car parts", 3, true); Hook(postbox, "Wait", 4, false);
            Hook(timer, "Pay", 5, true); Hook(timer, "Reset purchase", 6, false);
            foreach (FsmState state in payment.Fsm.States) foreach (FsmStateAction action in state.Actions) {
                var set = action as SetFsmBool;
                if (set != null && set.variableName.Value == "Paid" && set.setValue.Value) { Hook(payment, state.Name, 7, false); break; }
            }
            foreach (UnityEngine.Object obj in Game.AllFsms()) {
                var f = obj as PlayMakerFSM; if (f == null) continue;
                string path = Recon.Path(f.transform);
                if (path.StartsWith("REPAIRSHOP/Jobs/", StringComparison.Ordinal)) { f.Fsm.InitData(); Replay.Claim(f, Module); PatchRandom(f, path); PatchWaits(f, false); }
            }
            PatchWaits(repair, true);
            Gate(repair, "Distance player"); Gate(repair, "Wait day"); Gate(timer, "State 1");
            Log.Info("MSC orders bound: Fleetari services=" + services.Length + ";catalog slots=" + catalogOrder.Items.Length);
            return true;
        }
        sealed class OrderHook : ModHook
        {
            public int Kind; public override string Module { get { return MscOrders.Module; } }
            public override void OnEnter() { try { if (!applying && (Session.Active || instantWork)) Local(Kind); } catch (Exception e) { Replay.HookError(e); } Finish(); }
        }
        public static string FinishFleetari()
        {
            if (!PlayerSync.InGame) return Lang.R("Load the game first.");
            if (!bound) {
                if (Session.Active || !Bind()) return Lang.R("Fleetari orders are not ready.");
                bound = true;
            }
            if (!Session.Active) repairOrder = Capture(MscOrderMessage.Fleetari);
            if (repairOrder == null || !repairOrder.Active) return Lang.R("There is no active Fleetari order.");
            // The guest's command is announced to the host through the existing
            // cheat channel. Only the host starts and publishes the native job.
            if (Session.Active && !Session.IsHost) return null;
            if (instantWork || repair.ActiveStateName == "Work" || repair.ActiveStateName == "Reset job" || repair.ActiveStateName == "Reset order")
                return Lang.R("Fleetari is already finishing this order.");
            instantWork = true;
            Set(repairOrder);
            repair.FsmVariables.GetFsmFloat("_OrderTime").Value = 0;
            Game.SetState(repair, "Work");
            return null;
        }
        sealed class FleetariWait : FsmStateAction
        {
            public Wait Native; public bool Order;
            bool fast; int frame;
            public override void OnEnter()
            {
                fast = instantWork; frame = Time.frameCount;
                if (!fast) Native.OnEnter();
                else if (!Order) Complete();
            }
            void Complete() { Finish(); Fsm.Event(Native.finishEvent); }
            public override void OnUpdate()
            {
                if (!fast) Native.OnUpdate();
                // Newly activated native job FSMs must get their Start/Update
                // before the order clears their selected services.
                else if (Order && Time.frameCount > frame + 2) Complete();
            }
            public override void OnExit() { if (!fast) Native.OnExit(); }
        }
        static void PatchWaits(PlayMakerFSM f, bool order)
        {
            foreach (FsmState state in f.Fsm.States) {
                if (order && state.Name != "Work") continue;
                var actions = new List<FsmStateAction>(state.Actions);
                for (int i = 0; i < actions.Count; i++) {
                    var wait = actions[i] as Wait; if (wait == null || wait.finishEvent == null) continue;
                    wait.Init(state);
                    var wrapper = new FleetariWait { Native = wait, Order = order }; wrapper.Init(state); actions[i] = wrapper;
                }
                state.Actions = actions.ToArray();
            }
        }
        static void Hook(PlayMakerFSM f, string name, int kind, bool first)
        {
            FsmState state = f.Fsm.GetState(name); if (state == null) throw new InvalidOperationException("MSC order state missing: " + name);
            var actions = new List<FsmStateAction>(state.Actions);
            foreach (FsmStateAction a in actions) if (a is OrderHook && ((OrderHook)a).Kind == kind) return;
            var hook = new OrderHook { Kind = kind }; hook.Init(state);
            if (first) actions.Insert(0, hook); else actions.Add(hook); state.Actions = actions.ToArray();
        }
        sealed class HostAction : FsmStateAction
        {
            public FsmStateAction Native;
            public override void OnEnter() { if (!Session.Active || Session.IsHost) Native.OnEnter(); }
            public override void OnUpdate() { if (!Session.Active || Session.IsHost) Native.OnUpdate(); }
            public override void OnExit() { Native.OnExit(); }
        }
        static void Gate(PlayMakerFSM f, string name)
        {
            FsmState state = f.Fsm.GetState(name); var list = new List<FsmStateAction>(state.Actions);
            for (int i = 0; i < list.Count; i++) if (list[i] is FloatCompare || list[i] is FloatAdd || list[i] is FloatSubtract) {
                list[i].Init(state); var wrapped = new HostAction { Native = list[i] }; wrapped.Init(state); list[i] = wrapped;
            }
            state.Actions = list.ToArray();
        }
        static MscOrderMessage Capture(int type)
        {
            var m = new MscOrderMessage { Type = type, Revision = revision };
            if (type == MscOrderMessage.Fleetari) {
                for (int i = 0; i < services.Length; i++) if (services[i].Value) m.Flags |= 1UL << i;
                m.Active = repair.FsmVariables.GetFsmBool("_Order").Value; m.Time = Mathf.Max(0, repair.FsmVariables.GetFsmFloat("_OrderTime").Value);
                m.Gear = repair.FsmVariables.GetFsmFloat("FinalGear").Value;
                m.CarColor = repair.FsmVariables.GetFsmColor("CarPaintColor").Value; m.RimColor = repair.FsmVariables.GetFsmColor("RimPaintColor").Value;
            } else {
                ArrayList list = MscShop.List(catalog.gameObject, "Delivering"); m.Items = new string[list.Count];
                for (int i = 0; i < list.Count; i++) m.Items[i] = Convert.ToString(list[i]);
                m.Active = catalog.FsmVariables.GetFsmBool("Wait").Value; m.Paid = catalog.FsmVariables.GetFsmBool("Paid").Value;
                m.Cost = catalog.FsmVariables.GetFsmFloat("PriceTotal").Value; m.Time = timer.FsmVariables.GetFsmFloat("Current").Value;
                m.Gear = timer.ActiveStateName == "Pay" ? 1 : 0;
            }
            return m;
        }
        static void Set(MscOrderMessage m)
        {
            if (m.Type == MscOrderMessage.Fleetari) {
                for (int i = 0; i < services.Length; i++) services[i].Value = (m.Flags & (1UL << i)) != 0;
                repair.FsmVariables.GetFsmBool("_Order").Value = m.Active;
                repair.FsmVariables.GetFsmFloat("_OrderTime").Value = m.Time; repair.FsmVariables.GetFsmFloat("FinalGear").Value = m.Gear;
                repair.FsmVariables.GetFsmColor("CarPaintColor").Value = m.CarColor; repair.FsmVariables.GetFsmColor("RimPaintColor").Value = m.RimColor;
            } else {
                ArrayList list = MscShop.List(catalog.gameObject, "Delivering"); list.Clear(); foreach (string item in m.Items) list.Add(item);
                catalog.FsmVariables.GetFsmBool("Wait").Value = m.Active; catalog.FsmVariables.GetFsmBool("Paid").Value = m.Paid;
                catalog.FsmVariables.GetFsmFloat("PriceTotal").Value = m.Cost;
                timer.FsmVariables.GetFsmFloat("Current").Value = m.Time;
            }
        }
        static void Send(MscOrderMessage m, bool broadcast)
        {
            var w = new NetWriter(Msg.Purchase).U8(Session.LocalId).U8(MscOrderMessage.Kind); byte[] bytes = m.Pack(); w.Bytes(bytes, 0, bytes.Length);
            if (broadcast) Session.Broadcast(w, true); else Session.SendToHost(w, true);
        }
        static void Submit(MscOrderMessage m)
        {
            m.Actor = Session.LocalId; m.Ticket = ++ticket; pending[m.Ticket] = m;
            if (Session.IsHost) Accept(m); else Send(m, false);
        }
        static void Local(int kind)
        {
            if (!bound) return;
            if (kind == 2 && instantWork) { instantWork = false; workUntil = 0; }
            if (kind == 0) {
                var m = Capture(MscOrderMessage.Fleetari); m.Cost = brochure.FsmVariables.GetFsmFloat("Price").Value; m.Op = MscOrderMessage.Submit;
                Set(repairOrder); Submit(m);
            } else if (kind == 1 && (!Session.Active || Session.IsHost)) {
                Set(repairOrder); randomSeed = repairOrder.Seed; workUntil = Time.realtimeSinceStartup + 8;
                var m = Capture(MscOrderMessage.Fleetari); m.Seed = randomSeed;
                // Paid is only used by catalogue orders. Its existing wire slot
                // marks instant Fleetari Work without changing packet layout.
                m.Paid = instantWork;
                if (Session.Active) Publish(m, MscOrderMessage.Work);
            } else if (kind == 2 && (!Session.Active || Session.IsHost)) { repairOrder = Capture(MscOrderMessage.Fleetari); if (Session.Active) Publish(repairOrder, MscOrderMessage.Done); }
            else if (kind == 3) posting = true;
            else if (kind == 4 && posting) {
                posting = false; var m = Capture(MscOrderMessage.Catalog); m.Active = true; m.Op = MscOrderMessage.Submit;
                Set(catalogOrder); Submit(m);
            } else if (kind == 5 && Session.IsHost) { catalogOrder = Capture(MscOrderMessage.Catalog); Publish(catalogOrder, MscOrderMessage.Arrive); }
            else if (kind == 6 && Session.IsHost) { catalogOrder = Capture(MscOrderMessage.Catalog); Publish(catalogOrder, MscOrderMessage.Done); }
            else if (kind == 7) {
                var m = Capture(MscOrderMessage.Catalog); m.Op = MscOrderMessage.Pay; m.Cost = payment.FsmVariables.GetFsmFloat("FinalPrice").Value;
                catalog.FsmVariables.GetFsmBool("Paid").Value = false; Submit(m);
            }
        }
        static void Publish(MscOrderMessage m, int op)
        {
            m.Op = op; m.Revision = ++revision; applied = revision; Send(m, true);
        }
        static void Accept(MscOrderMessage m)
        {
            if (!requests.Add(m.Actor + ":" + m.Ticket + ":" + m.Op)) return;
            MscOrderMessage old = m.Type == MscOrderMessage.Fleetari ? repairOrder : catalogOrder;
            bool reject = m.Op == MscOrderMessage.Submit && (old.Active || Busy)
                || m.Op == MscOrderMessage.Pay && (!catalogOrder.Active || catalogOrder.Paid);
            if (reject) {
                if (m.Actor == Session.LocalId) Refund(m);
                else { m.Op = MscOrderMessage.Reject; Send(m, true); }
                return;
            }
            if (m.Type == MscOrderMessage.Catalog) {
                if (m.Items.Length != catalogOrder.Items.Length) return;
                foreach (string item in m.Items) if (item.Length > 0 && !packageNames.Contains(item)) throw new System.IO.IOException("Unknown MSC catalogue product: " + item);
            }
            m.Seed = UnityEngine.Random.Range(1, int.MaxValue); m.Active = true;
            if (m.Op == MscOrderMessage.Pay) { m.Items = catalogOrder.Items; m.Paid = true; }
            Apply(m, m.Op == MscOrderMessage.Pay ? MscOrderMessage.Pay : MscOrderMessage.Commit);
            Publish(m, m.Op == MscOrderMessage.Pay ? MscOrderMessage.Pay : MscOrderMessage.Commit);
        }
        static void Refund(MscOrderMessage m)
        {
            MscOrderMessage local;
            if (m.Actor != Session.LocalId || !pending.TryGetValue(m.Ticket, out local)) return;
            pending.Remove(m.Ticket);
            if (local.Type == MscOrderMessage.Fleetari || local.Op == MscOrderMessage.Pay) {
                FsmVariables.GlobalVariables.GetFsmFloat("PlayerMoney").Value += local.Cost;
                Hud.Toast(Lang.T("Une commande est deja en cours. Paiement rembourse.", "An order is already in progress. Payment refunded."));
            }
        }
        static void Apply(MscOrderMessage m, int op)
        {
            applying = true;
            try {
                if (m.Type == MscOrderMessage.Fleetari) {
                    repairOrder = m; randomSeed = m.Seed;
                    if (op == MscOrderMessage.Sync) {
                        // The brochure edits these same native service variables.
                        // A timer snapshot must not erase an unsubmitted selection.
                        repair.FsmVariables.GetFsmBool("_Order").Value = m.Active;
                        repair.FsmVariables.GetFsmFloat("_OrderTime").Value = m.Time;
                    } else Set(m);
                    if (op == MscOrderMessage.Work) { instantWork = m.Paid; workUntil = Time.realtimeSinceStartup + 8; Game.SetState(repair, "Work"); }
                    else if (op == MscOrderMessage.Commit) Game.SetState(repair, "Distance player");
                } else {
                    // The host can finish its native package loop a frame
                    // before a guest. Keep the guest's Delivering array until
                    // its own loop has unparented every purchased package.
                    if (!Session.IsHost && !m.Active && catalog.FsmVariables.GetFsmBool("Wait").Value
                        && (catalogOrder.Active || catalog.FsmVariables.GetFsmBool("Paid").Value)) {
                        catalogOrder = m;
                        catalog.FsmVariables.GetFsmBool("Paid").Value = true;
                        if (timer.ActiveStateName == "State 1") { timer.gameObject.SetActive(true); Game.SetState(timer, "Pay"); }
                        return;
                    }
                    catalogOrder = m;
                    float draftPrice = catalog.FsmVariables.GetFsmFloat("PriceTotal").Value;
                    Set(m);
                    if (op == MscOrderMessage.Pay) workUntil = Time.realtimeSinceStartup + 3;
                    if (op == MscOrderMessage.Sync && !m.Active) catalog.FsmVariables.GetFsmFloat("PriceTotal").Value = draftPrice;
                    if (op == MscOrderMessage.Commit) {
                        catalog.FsmVariables.GetFsmBool("Activated").Value = false;
                        FsmGameObject envelope = catalog.FsmVariables.FindFsmGameObject("ThisEnvelope"); if (envelope != null && envelope.Value != null) envelope.Value.SetActive(false);
                        timer.gameObject.SetActive(true);
                    } else if (op == MscOrderMessage.Arrive || op == MscOrderMessage.Sync && m.Active && m.Gear == 1 && timer.ActiveStateName != "Pay") { timer.gameObject.SetActive(true); Game.SetState(timer, "Pay"); }
                }
                if (m.Actor == Session.LocalId) pending.Remove(m.Ticket);
            } finally { applying = false; }
        }
        public static void OnMessage(Peer from, NetReader r)
        {
            var m = MscOrderMessage.Read(r.Bytes()); if (r.More) throw new System.IO.IOException("Trailing order bytes");
            if (Session.IsHost) { if (m.Op != MscOrderMessage.Submit && m.Op != MscOrderMessage.Pay) return; m.Actor = from.Id; }
            else if (from.Id != 0) return;
            if (incoming.Count < 256) incoming.Enqueue(m);
        }
        public static void Update()
        {
            if (!PlayerSync.InGame || !Session.Active || Time.timeSinceLevelLoad < 3) return;
            if (!bound) { if (Time.realtimeSinceStartup < nextBind) return; nextBind = Time.realtimeSinceStartup + 2; bound = Bind(); if (!bound) return; }
            while (incoming.Count > 0) {
                // A fast host can send Done before a slower guest has started
                // its job FSMs. Preserve the service mask until local cleanup.
                if (instantWork) break;
                var m = incoming.Dequeue();
                if (Session.IsHost) Accept(m);
                else if (m.Op == MscOrderMessage.Reject) Refund(m);
                else if (m.Op == MscOrderMessage.Sync ? m.Revision >= Mathf.Max(applied, SavedSequence) : m.Revision > Mathf.Max(applied, SavedSequence)) {
                    Apply(m, m.Op); applied = Mathf.Max(applied, m.Revision);
                }
            }
            if (Session.IsHost && Session.RemoteCount > 0 && Time.realtimeSinceStartup >= nextSync && !Busy) {
                nextSync = Time.realtimeSinceStartup + 5;
                foreach (var order in new[] { repairOrder, catalogOrder }) {
                    var m = Capture(order.Type); m.Seed = order.Seed; m.Op = MscOrderMessage.Sync; m.Revision = revision;
                    if (m.Type == MscOrderMessage.Fleetari) { m.Flags = order.Flags; m.Gear = order.Gear; m.CarColor = order.CarColor; m.RimColor = order.RimColor; }
                    Send(m, true);
                }
            }
        }
        sealed class NativeRandom : FsmStateAction
        {
            public FsmStateAction Native; public string Key;
            public override void OnEnter()
            {
                if (!Session.Active) { Native.OnEnter(); return; }
                uint hash = unchecked((uint)randomSeed); foreach (char c in Key) hash = unchecked((hash ^ c) * 16777619);
                float fraction = (hash & 0xffffff) / 16777216f;
                var random = Native as RandomFloat;
                if (random != null) random.storeResult.Value = Mathf.Lerp(random.min.Value, random.max.Value, fraction);
                else {
                    var colors = (FsmColor[])Field(Native, "colors"); var result = (FsmColor)Field(Native, "storeColor");
                    result.Value = colors[Mathf.Min(colors.Length - 1, (int)(fraction * colors.Length))].Value;
                }
                Finish();
            }
        }
        static object Field(object obj, string name) { return obj.GetType().GetField(name).GetValue(obj); }
        static void PatchRandom(PlayMakerFSM f, string path)
        {
            foreach (FsmState state in f.Fsm.States) {
                var list = new List<FsmStateAction>(state.Actions);
                for (int i = 0; i < list.Count; i++) if (list[i] is RandomFloat || list[i].GetType().Name == "SelectRandomColor") {
                    list[i].Init(state); var wrapper = new NativeRandom { Native = list[i], Key = path + "::" + f.FsmName + "/" + state.Name + "/" + i };
                    wrapper.Init(state); list[i] = wrapper;
                }
                state.Actions = list.ToArray();
            }
        }
    }
}
