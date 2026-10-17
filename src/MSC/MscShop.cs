using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MWCoop.Net;
using UnityEngine;
namespace MWCoop
{
    // MSC bags contain numeric Items/Spraycans tables, not MWC Keys/Values.
    // The buyer pays locally; the host orders all product creation and picks.
    public static class MscShop
    {
        public const string RegisterPath = "STORE/StoreCashRegister/Register", CreatorPath = "STORE/LOD/ShopFunctions/BagCreator", ProductsPath = "Spawner/ProductNameList";
        const string Module = "msc-shop";
        static PlayMakerFSM register, products, bagFactory;
        static FsmInt[] quantities;
        static readonly List<PlayMakerFSM> factories = new List<PlayMakerFSM>();
        static readonly Dictionary<string, PlayMakerFSM> bags = new Dictionary<string, PlayMakerFSM>(StringComparer.Ordinal);
        static readonly HashSet<PlayMakerFSM> hookedBags = new HashSet<PlayMakerFSM>();
        static readonly Queue<MscShopMessage> requests = new Queue<MscShopMessage>(), commands = new Queue<MscShopMessage>();
        static readonly HashSet<string> seen = new HashSet<string>();
        static MscShopMessage delivering, draining;
        sealed class Delivery { public int Quantity, Index = -1; public bool Spray; public PlayMakerFSM Factory; public FsmState Fixed; }
        static readonly List<Delivery> deliveries = new List<Delivery>();
        static float nextScan, settleUntil;
        static int ticket, sequence, appliedSequence;
        static bool bound, haveSaveBaseline;
        public static int SavedSequence { get; private set; }
        public static bool Unsaved { get { return sequence > SavedSequence; } }
        public static bool Busy { get { return delivering != null || Time.realtimeSinceStartup < settleUntil; } }
        public static void MarkSaved() { if (Session.IsHost) SavedSequence = sequence; }
        public static void ReceiveSaveSequence(int value)
        {
            if (value < 0) throw new System.IO.IOException("Invalid MSC shop save sequence");
            SavedSequence = value; haveSaveBaseline = true;
            var keep = new List<MscShopMessage>(); foreach (var m in commands) if (m.Sequence > value) keep.Add(m);
            commands.Clear(); foreach (var m in keep) commands.Enqueue(m);
        }
        sealed class Frozen { public Rigidbody Body; public Vector3 Position; public RigidbodyConstraints Constraints; }
        static readonly List<Frozen> frozen = new List<Frozen>();
        public static bool Owns(PlayMakerFSM f)
        {
            if (f == null) return false;
            string path = Recon.Path(f.transform);
            return path == "STORE/LOD/GFX_Pub/PubCashRegister/Register" || path == RegisterPath || path == CreatorPath
                || path == ProductsPath || path == "Spawner/CreateShoppingbag"
                || path.StartsWith("STORE/LOD/ActivateBar/OrderList/", StringComparison.Ordinal) || IsBag(f);
        }
        public static bool IsBag(PlayMakerFSM f)
        {
            return f != null && f.FsmName == "Use" && f.FsmVariables.FindFsmString("UniqueTagArrayItems") != null
                && f.FsmVariables.FindFsmGameObject("ProductSpawner") != null;
        }
        static PlayMakerFSM Fsm(string path, string name) { return Game.FsmOn(Game.FindAny(path), name); }
        public static void OnLevelLoaded()
        {
            bool joining = !Session.IsHost && PlayerSync.InGame && haveSaveBaseline;
            var keep = joining ? new List<MscShopMessage>(commands) : new List<MscShopMessage>();
            int baseline = joining ? SavedSequence : 0;
            register = products = bagFactory = null; quantities = null;
            factories.Clear(); deliveries.Clear(); bags.Clear(); hookedBags.Clear(); requests.Clear(); commands.Clear(); seen.Clear(); frozen.Clear();
            delivering = draining = null; bound = false; sequence = 0; appliedSequence = SavedSequence = baseline; settleUntil = 0;
            haveSaveBaseline = false; foreach (var m in keep) commands.Enqueue(m);
            ticket = UnityEngine.Random.Range(1, 1000000) * 1000; nextScan = 0;
        }
        static void Add(PlayMakerFSM f, string state, FsmStateAction hook, bool first)
        {
            FsmState s = f.Fsm.GetState(state); var actions = new List<FsmStateAction>(s.Actions);
            foreach (FsmStateAction a in actions) if (a.GetType() == hook.GetType()) return;
            if (first) actions.Insert(0, hook); else actions.Add(hook);
            hook.Init(s); s.Actions = actions.ToArray();
        }
        sealed class Payment : ModHook
        {
            public override string Module { get { return MscShop.Module; } }
            public override void OnEnter()
            {
                try
                {
                    if (Session.Active && delivering == null && Replay.Depth == 0)
                    {
                        var m = new MscShopMessage { Op = MscShopMessage.Buy, Actor = Session.LocalId, Ticket = ++ticket, Quantities = Quantities() };
                        // Native Purchase still deducts Total and pays fuel. Its
                        // product loop stays empty until the ordered delivery.
                        foreach (FsmInt q in quantities) q.Value = 0;
                        Submit(m);
                    }
                }
                catch (Exception e) { Replay.HookError(e); }
                Finish();
            }
        }
        sealed class LocalCreator : FsmStateAction
        {
            public FsmStateAction Native;
            public override void OnEnter() { if (!Session.Active) { Native.Init(State); Native.OnEnter(); } Finish(); }
        }
        static object Field(FsmStateAction action, string name) { var field = action.GetType().GetField(name); return field == null ? null : field.GetValue(action); }
        static void BindDeliveries()
        {
            deliveries.Clear();
            foreach (FsmState state in register.Fsm.States)
            {
                if (state.Name != "Spawn" && !state.Name.StartsWith("Spawn ", StringComparison.Ordinal)) continue;
                string quantity = null; FsmStateAction array = null; FsmEventTarget target = null;
                foreach (FsmStateAction action in state.Actions)
                {
                    FsmInt q = action is IntAdd ? ((IntAdd)action).intVariable : action is SetIntValue ? ((SetIntValue)action).intVariable : null;
                    if (q != null && q.Name.StartsWith("Q", StringComparison.Ordinal)) quantity = q.Name;
                    if (action.GetType().Name == "ArrayListSet") array = action;
                    var send = action as SendEvent; var named = action as SendEventByName;
                    if (send != null) target = send.eventTarget; else if (named != null) target = named.eventTarget;
                }
                if (quantity == null) continue;
                int index = System.Array.FindIndex(quantities, q => q.Name == quantity); if (index < 0) continue;
                var d = new Delivery { Quantity = index };
                if (array != null)
                { d.Spray = ((FsmString)Field(array, "reference")).Value == "Spraycans"; d.Index = ((FsmInt)Field(array, "atIndex")).Value; }
                else if (target != null)
                {
                    GameObject go = target.gameObject.OwnerOption == OwnerDefaultOption.UseOwner ? register.gameObject : target.gameObject.GameObject.Value;
                    d.Factory = Game.FsmOn(go, target.fsmName.Value);
                    if (d.Factory == null) throw new InvalidOperationException("MSC checkout factory missing: " + quantity);
                }
                else d.Fixed = state; // The three unique Suomi covers use fixed native objects.
                deliveries.Add(d);
            }
            foreach (FsmInt q in quantities) if (!deliveries.Exists(d => quantities[d.Quantity] == q)) throw new InvalidOperationException("Unmapped MSC checkout product: " + q.Name);
        }
        static bool Bind()
        {
            register = Fsm(RegisterPath, "Data"); products = Fsm(ProductsPath, "Logic"); bagFactory = Fsm("Spawner/CreateShoppingbag", "ShoppingBag");
            if (register == null || products == null || bagFactory == null || !register.gameObject.activeInHierarchy) return false;
            if (!Replay.Claim(register, Module) || !Replay.Claim(products, Module)) return false;
            var list = new List<FsmInt>(); foreach (FsmInt q in register.FsmVariables.IntVariables) if (q.Name.StartsWith("Q", StringComparison.Ordinal)) list.Add(q);
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name)); quantities = list.ToArray();
            factories.Clear();
            foreach (string path in new[] { "Spawner/CreateItems", "Spawner/CreateSpraycans", "Spawner/CreateShoppingbag" })
            {
                GameObject go = Game.FindAny(path); if (go == null) { quantities = null; return false; }
                foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>()) if (f.FsmVariables.FindFsmInt("ObjectNumberInt") != null)
                { f.Fsm.InitData(); factories.Add(f); }
            }
            factories.Sort((a, b) => string.CompareOrdinal(Recon.Path(a.transform) + "::" + a.FsmName, Recon.Path(b.transform) + "::" + b.FsmName));
            BindDeliveries();
            Add(register, "Purchase", new Payment(), true);
            foreach (FsmState state in register.Fsm.States)
            {
                var actions = new List<FsmStateAction>(state.Actions);
                for (int i = 0; i < actions.Count; i++)
                {
                    var send = actions[i] as SendEventByName;
                    if (send != null && state.Name == "State 1" && send.sendEvent.Value == "COPY")
                    { var wrapper = new LocalCreator { Native = actions[i] }; wrapper.Init(state); actions[i] = wrapper; }
                }
                state.Actions = actions.ToArray();
            }
            Log.Info("MSC shop: bound quantities=" + quantities.Length + ";factories=" + factories.Count); return true;
        }
        static int[] Quantities() { int[] v = new int[quantities.Length]; for (int i = 0; i < v.Length; i++) v[i] = quantities[i].Value; return v; }
        static int[] Counters() { int[] v = new int[factories.Count]; for (int i = 0; i < v.Length; i++) v[i] = factories[i].FsmVariables.GetFsmInt("ObjectNumberInt").Value; return v; }
        public static ArrayList List(GameObject go, string name)
        {
            if (go != null) foreach (PlayMakerArrayListProxy p in go.GetComponents<PlayMakerArrayListProxy>()) if (p.referenceName == name) return p.arrayList;
            return null;
        }
        static GameObject ShoppingList { get { return register.FsmVariables.GetFsmGameObject("List").Value; } }
        public static int[] Values(GameObject go, string name)
        {
            ArrayList list = List(go, name); if (list == null) return new int[0];
            int[] v = new int[list.Count]; for (int i = 0; i < v.Length; i++) v[i] = Convert.ToInt32(list[i]); return v;
        }
        static void Values(GameObject go, string name, int[] values)
        {
            ArrayList list = List(go, name); if (list == null) throw new InvalidOperationException("MSC shop list missing: " + name);
            list.Clear(); foreach (int value in values) list.Add(value);
        }
        static bool Any(int[] values) { foreach (int n in values) if (n > 0) return true; return false; }
        static void Submit(MscShopMessage m) { if (Session.IsHost) Enqueue(m); else Send(m, false); }
        static void Enqueue(MscShopMessage m) { if (requests.Count < 256 && seen.Add(m.Actor + ":" + m.Ticket)) requests.Enqueue(m); }
        static void Send(MscShopMessage m, bool broadcast)
        {
            var w = new NetWriter(Msg.Purchase).U8(Session.LocalId).U8(MscShopMessage.Kind); m.Write(w);
            if (w.Length > Transport.MaxPayload) throw new InvalidOperationException("MSC shop packet too large");
            if (broadcast) Session.Broadcast(w, true); else Session.SendToHost(w, true);
        }
        public static void OnMessage(Peer from, NetReader r)
        {
            MscShopMessage m = MscShopMessage.Read(r);
            if (Session.IsHost) { if (m.Op != MscShopMessage.Buy && m.Op != MscShopMessage.Take) return; m.Actor = from.Id; Enqueue(m); }
            else if (from.Id == 0 && (m.Op == MscShopMessage.Deliver || m.Op == MscShopMessage.Extract) && m.Sequence > SavedSequence) commands.Enqueue(m);
        }
        static bool Idle { get { return register.ActiveStateName == "Wait player" || register.ActiveStateName == "Wait button" || register.ActiveStateName == "Teimo"; } }
        public static void Update()
        {
            if (!PlayerSync.InGame || !Session.Active || Time.timeSinceLevelLoad < 2) return;
            if (!bound) { bound = Bind(); if (!bound) return; }
            float now = Time.realtimeSinceStartup;
            if (now >= nextScan)
            {
                nextScan = now + 3;
                foreach (UnityEngine.Object obj in Game.AllFsms()) { var f = obj as PlayMakerFSM; if (IsBag(f)) Track(f); }
            }
            for (int i = frozen.Count - 1; i >= 0; i--)
            {
                Frozen f = frozen[i]; GameObject store = Game.FindAny("STORE/LOD");
                if (f.Body == null || store != null && store.activeInHierarchy || (f.Body.position - f.Position).sqrMagnitude > .04f)
                { if (f.Body != null) f.Body.constraints = f.Constraints; frozen.RemoveAt(i); }
            }
            if (!Session.IsHost)
            {
                if (commands.Count == 0) return; MscShopMessage m = commands.Peek();
                if (!MscShopMessage.AcceptSequence(m.Sequence, appliedSequence)) { commands.Dequeue(); return; }
                if (m.Op == MscShopMessage.Extract && FindBag(m.Bag) == null) return;
                Apply(m); appliedSequence = m.Sequence; commands.Dequeue(); return;
            }
            if (!Idle) return;
            MscShopMessage request = draining;
            if (request == null) { if (requests.Count == 0) return; request = requests.Dequeue(); }
            if (request.Op == MscShopMessage.Buy)
            {
                if (request.Quantities.Length != quantities.Length || !Any(request.Quantities)) return;
                request.Op = MscShopMessage.Deliver; request.Sequence = ++sequence; request.Counters = Counters(); Send(request, true); Apply(request);
            }
            else
            {
                PlayMakerFSM bag = FindBag(request.Bag); if (bag == null) { draining = null; return; }
                int[] items = Values(bag.gameObject, "Items"), cans = Values(bag.gameObject, "Spraycans");
                var choices = new List<int>(); for (int i = 0; i < items.Length; i++) if (items[i] > 0) choices.Add(i);
                for (int i = 0; i < cans.Length; i++) if (cans[i] > 0) choices.Add(18 + i);
                if (choices.Count == 0) { draining = null; return; }
                int choice = request.All ? choices[0] : choices[UnityEngine.Random.Range(0, choices.Count)];
                var m = new MscShopMessage { Op = MscShopMessage.Extract, Actor = request.Actor, Ticket = request.Ticket, Sequence = ++sequence,
                    Bag = request.Bag, Spray = choice >= 18, Index = choice >= 18 ? choice - 18 : choice, Items = items, Cans = cans,
                    Counters = Counters(), Condition = bag.FsmVariables.GetFsmFloat("Condition").Value };
                Apply(m); Send(m, true); draining = request.All ? request : null;
            }
        }
        static void Apply(MscShopMessage m)
        {
            if (m.Counters.Length != factories.Count) throw new InvalidOperationException("MSC shop factory schema mismatch");
            for (int i = 0; i < factories.Count; i++) factories[i].FsmVariables.GetFsmInt("ObjectNumberInt").Value = m.Counters[i];
            if (m.Op == MscShopMessage.Deliver)
            {
                if (m.Quantities.Length != quantities.Length) throw new InvalidOperationException("MSC shop cart schema mismatch");
                Deliver(m);
            }
            else Extract(m);
        }
        static GameObject New(PlayMakerFSM f) { FsmGameObject n = f.FsmVariables.FindFsmGameObject("New"); return n == null ? null : n.Value; }
        static void Deliver(MscShopMessage m)
        {
            int[] items = new int[18], cans = new int[13];
            FsmGameObject spawn = FsmVariables.GlobalVariables.GetFsmGameObject("ShoppingBagSpawn"); GameObject previous = spawn.Value;
            delivering = m;
            try
            {
                spawn.Value = register.FsmVariables.GetFsmGameObject("SpawnPoint").Value;
                foreach (Delivery d in deliveries)
                {
                    int count = m.Quantities[d.Quantity]; if (count == 0) continue;
                    if (d.Index >= 0) { (d.Spray ? cans : items)[d.Index] = count; continue; }
                    if (d.Factory != null)
                    {
                        for (int i = 0; i < count; i++) { d.Factory.SendEvent("SPAWNITEM"); Activate(New(d.Factory)); }
                    }
                    else foreach (FsmStateAction action in d.Fixed.Actions)
                    {
                        if (action is SetParent || action is SetFsmBool || action is ActivateGameObject) action.OnEnter();
                        var activate = action as ActivateGameObject;
                        if (activate != null && activate.activate.Value) Activate(activate.gameObject.GameObject.Value);
                    }
                }
                CreateBag(items, cans);
                Log.Info("MSC shop: delivered order " + m.Sequence + ";local cart untouched");
            }
            finally { spawn.Value = previous; delivering = null; }
        }
        static void CreateBag(int[] items, int[] cans)
        {
            if (!Any(items) && !Any(cans)) return;
            Game.SetState(bagFactory, "Create product"); GameObject go = New(bagFactory);
            if (go == null) throw new InvalidOperationException("MSC bag factory returned no bag");
            Values(go, "Items", items); Values(go, "Spraycans", cans); Activate(go);
            Log.Info("MSC shop: bag " + go.name + ";items=" + string.Join(",", System.Array.ConvertAll(items, x => x.ToString())) + ";cans=" + string.Join(",", System.Array.ConvertAll(cans, x => x.ToString())));
        }
        static void Activate(GameObject go)
        {
            if (go == null) return; go.transform.parent = null; go.SetActive(true); settleUntil = Time.realtimeSinceStartup + .4f;
            GameObject store = Game.FindAny("STORE/LOD"); Rigidbody body = go.GetComponent<Rigidbody>();
            if (body != null && store != null && !store.activeInHierarchy)
            { frozen.Add(new Frozen { Body = body, Position = body.position, Constraints = body.constraints }); body.constraints = RigidbodyConstraints.FreezeAll; }
            Consume.Soon(go); Props.SoonScan(); MscParts.Changed(go);
        }
        public static PlayMakerFSM FindBag(string id) { PlayMakerFSM f; return !string.IsNullOrEmpty(id) && bags.TryGetValue(id, out f) && f != null ? f : null; }
        public static void Track(PlayMakerFSM f)
        {
            if (!IsBag(f) || !f.gameObject.activeInHierarchy) return;
            FsmString id = f.FsmVariables.FindFsmString("ID"); if (id == null || id.Value.Length == 0) return;
            bags[id.Value] = f; if (!hookedBags.Add(f)) return;
            foreach (string state in new[] { "Spawn one", "Spawn all" })
            {
                FsmState s = f.Fsm.GetState(state); var actions = new List<FsmStateAction>(s.Actions);
                for (int i = 0; i < actions.Count; i++) if (actions[i] is SendEventByName)
                { var take = new BagTake { Bag = f, All = state == "Spawn all", Native = actions[i] }; take.Init(s); actions[i] = take; }
                s.Actions = actions.ToArray();
            }
        }
        sealed class BagTake : ModHook
        {
            public PlayMakerFSM Bag; public bool All; public FsmStateAction Native;
            public override string Module { get { return MscShop.Module; } }
            public override void OnEnter()
            {
                try
                {
                    if (!Session.Active) { Native.Init(State); Native.OnEnter(); }
                    else if (Replay.Depth == 0) Submit(new MscShopMessage { Op = MscShopMessage.Take, Actor = Session.LocalId, Ticket = ++ticket, Bag = Bag.FsmVariables.GetFsmString("ID").Value, All = All });
                }
                catch (Exception e) { Replay.HookError(e); }
                Finish();
            }
        }
        static void Extract(MscShopMessage m)
        {
            PlayMakerFSM bag = FindBag(m.Bag); Values(bag.gameObject, "Items", m.Items); Values(bag.gameObject, "Spraycans", m.Cans);
            bag.FsmVariables.GetFsmFloat("Condition").Value = m.Condition;
            products.FsmVariables.GetFsmGameObject("CurrentBag").Value = bag.gameObject;
            products.FsmVariables.GetFsmInt("RandomIndex").Value = m.Index;
            products.FsmVariables.GetFsmInt("Product").Value = (m.Spray ? m.Cans : m.Items)[m.Index];
            ArrayList names = List(products.gameObject, m.Spray ? "Spraycans" : "Items"); string product = Convert.ToString(names[m.Index]);
            PlayMakerFSM factory = Fsm(m.Spray ? "Spawner/CreateSpraycans" : "Spawner/CreateItems", product);
            Replay.Depth++; try { Game.SetState(products, m.Spray ? "State 8" : "State 7"); } finally { Replay.Depth--; }
            GameObject made = factory == null ? null : New(factory); if (made == null) throw new InvalidOperationException("MSC bag factory missing: " + product);
            Activate(made);
            if (Session.IsHost) { m.Position = made.transform.position; m.Rotation = made.transform.rotation; }
            else { made.transform.position = m.Position; made.transform.rotation = m.Rotation; }
            Log.Info("MSC shop: extract " + m.Sequence + ";bag=" + m.Bag + ";product=" + product + ";object=" + made.name);
        }
    }
}
