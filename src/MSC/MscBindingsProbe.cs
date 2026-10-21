using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
namespace MWCoop
{
    // Explicit isolated QA only; never runs in a normal game.
    public static class MscBindingsProbe
    {
        static bool dumped;
        static readonly HashSet<int> steps = new HashSet<int>();
        static long start;
        static float cash, serviceCost;
        static int shelfBefore;
        static FsmStateAction deliveryWait;
        static string[] packages;
        static bool instantClick, instantClicked, instantRecorded, parcelClick, parcelClicked;
        static float parcelCash;
        static bool Instant { get { return MscRuntime.HasArgument("--summercoop-test-bindings-instant"); } }
        static bool Parcels { get { return MscRuntime.HasArgument("--summercoop-test-bindings-parcels"); } }
        static string Role { get { return MWCoop.Net.Session.IsHost ? "host" : "guest"; } }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-bindings-channel");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 15) return;
            if (!dumped) {
                Dump(channel); dumped = true;
                if (Instant || Parcels) new GameObject("SummerCoop order cheat QA").AddComponent<InstantDriver>();
            }
            Exercise(channel);
        }
        static void Dump(string channel)
        {
            var text = new StringBuilder();
            foreach (UnityEngine.Object obj in Game.AllFsms())
            {
                var f = obj as PlayMakerFSM; if (f == null || f.hideFlags != HideFlags.None) continue;
                string path = Recon.Path(f.transform);
                if (path != "REPAIRSHOP" && !path.StartsWith("REPAIRSHOP/") && !path.StartsWith("Sheets/") && !path.StartsWith("STORE/")
                    && !path.StartsWith("ITEMS/helmet") && !path.StartsWith("ITEMS/coffee cup")
                    && !path.StartsWith("ITEMS/sofa") && !path.StartsWith("Systems/Leaks")
                    && !path.StartsWith("Spawner/") && !path.Contains("SteamFountain")) continue;
                try {
                    if (!f.Fsm.Initialized) {
                        if (path == "REPAIRSHOP" || path == "Sheets/ServiceBrochure" || path == "REPAIRSHOP/LOD/Office/Fleetari/Brochure") f.Fsm.Init(f);
                        else continue;
                    }
                    text.AppendLine("===== " + path + "::" + f.FsmName);
                    Recon.AppendFsm(text, "", f, true);
                } catch (Exception e) { text.AppendLine("DUMP ERROR " + path + ": " + e); }
            }
            foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(typeof(PlayMakerArrayListProxy)))
            {
                var a = obj as PlayMakerArrayListProxy;
                string path = Recon.Path(a.transform);
                if (!path.StartsWith("STORE/") && !path.StartsWith("Sheets/") && !path.StartsWith("REPAIRSHOP/") && !path.StartsWith("Spawner/")) continue;
                text.Append("ARRAY ").Append(path).Append("::").Append(a.referenceName).Append('=');
                if (a.arrayList != null) foreach (object value in a.arrayList) text.Append(value is GameObject ? Recon.Path(((GameObject)value).transform) : Convert.ToString(value)).Append(';');
                text.AppendLine();
            }
            foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(typeof(PlayMakerHashTableProxy))) {
                var h = obj as PlayMakerHashTableProxy; string path = Recon.Path(h.transform);
                if (path == "STORE/Inventory") text.AppendLine("HASH " + h.referenceName + ";Count=" + (h._hashTable == null ? -1 : h._hashTable.Count) + ";Flags=" + h.hideFlags + ";Active=" + h.gameObject.activeInHierarchy);
            }
            Directory.CreateDirectory(channel);
            MscProbeFiles.WriteAllText(Path.Combine(channel, MWCoop.Net.Session.IsHost ? "host-native.txt" : "guest-native.txt"), text.ToString());
        }
        static bool At(int n, float t, float when) { return t >= when && steps.Add(n); }
        static PlayMakerFSM Fsm(string path, string name) { return Game.FsmOn(Game.FindAny(path), name); }
        static void Record(string channel, string name, bool pass, string detail) { MscRegressionProbe.Record(channel, "bindings-" + name, pass, detail); }
        static void Enable(GameObject go)
        {
            for (Transform t = go.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>()) f.Fsm.InitData();
        }
        static void ClickService(string name)
        {
            foreach (UnityEngine.Object obj in Game.AllFsms()) {
                var f = obj as PlayMakerFSM; if (f == null || !Recon.Path(f.transform).StartsWith("Sheets/ServiceBrochure/")) continue;
                f.Fsm.InitData();
                foreach (FsmState state in f.Fsm.States) foreach (FsmStateAction a in state.Actions) {
                    var set = a as HutongGames.PlayMaker.Actions.SetFsmBool;
                    if (set == null || set.variableName.Value != name) continue;
                    Enable(f.gameObject);
                    FsmBool on = f.FsmVariables.FindFsmBool("On"); if (on != null) on.Value = false;
                    Game.SetState(f, state.Name); return;
                }
            }
            throw new Exception("Native Fleetari selection missing: " + name);
        }
        static void Exercise(string channel)
        {
            bool host = MWCoop.Net.Session.IsHost;
            if (!MscOrders.Bound || !Wallet.Ready) return;
            string ready = Path.Combine(channel, Role + "-ready.txt");
            if (!File.Exists(ready)) MscProbeFiles.WriteAllText(ready, "ready");
            string clock = Path.Combine(channel, "start.txt");
            if (host && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(clock)) MscProbeFiles.WriteAllText(clock, DateTime.UtcNow.AddSeconds(3).Ticks.ToString());
            if (start == 0 && (!File.Exists(clock) || !long.TryParse(MscProbeFiles.ReadAllText(clock), out start))) return;
            float t = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start).TotalSeconds;
            var order = Fsm(MscOrders.RepairPath, "Data");
            if (At(0, t, 0)) {
                if ((Instant || Parcels) && host) Cheats.SetAllowed(true);
                Wallet.AddLocal(20000);
                cash = FsmVariables.GlobalVariables.GetFsmFloat("PlayerMoney").Value;
                Transform visitor = Game.PlayerT;
                var cc = visitor.GetComponent<CharacterController>(); if (cc != null) cc.enabled = false;
                visitor.position = host ? new Vector3(-10, 1, 10) : Game.FindAny("REPAIRSHOP/LOD/Office/PlayerDistance").transform.position;
                if (cc != null) cc.enabled = true;
                if (host) {
                    FsmVariables.GlobalVariables.GetFsmInt("GlobalDay").Value = 1;
                    Fsm("MAP/SUN/Pivot/SUN", "Color").FsmVariables.GetFsmInt("Time").Value = 12;
                    Game.SetState(Fsm("REPAIRSHOP", "OpeningHours"), "State 1");
                    Rigidbody car = VehicleSync.Body("SATSUMA");
                    foreach (Rigidbody body in car.GetComponentsInChildren<Rigidbody>(true)) {
                        body.velocity = body.angularVelocity = Vector3.zero;
                        body.useGravity = false; body.isKinematic = true;
                    }
                    car.transform.position = Game.FindAny("REPAIRSHOP/DistanceTarget").transform.position + Vector3.up;
                }
                Fsm("Database/DatabaseMotor/Distributor", "Data").FsmVariables.GetFsmFloat("SparkAngle").Value = 4;
                Fsm("Database/DatabaseMotor/Carburator", "Data").FsmVariables.GetFsmFloat("IdleAdjust").Value = 12;
                Record(channel, "references", Wear.ItemObject(0) != null && Wear.ItemObject(1) != null && typeof(MscRuntime).Assembly.GetType("MWCoop.CarSale") == null,
                    "Helmet=" + (Wear.ItemObject(0) != null) + ";Cup=" + (Wear.ItemObject(1) != null) + ";Wallet=" + Wallet.Ready);
                // Simulate a guest's stale closed scene at join. The shared
                // opening snapshot must repair it without activating LODs.
                if (!host) {
                    Fsm("REPAIRSHOP", "OpeningHours").FsmVariables.GetFsmBool("OpenRepairShop").Value = false;
                    Transform building = Game.FindAny("REPAIRSHOP/Building").transform;
                    Transform door = building.Find("Door1Open/garage_door 1");
                    if (door != null) { door.parent = building.Find("Door1Closed"); door.localPosition = Vector3.zero; door.localRotation = Quaternion.identity; }
                }
            }
            if (At(25, t, 2.5f)) Opening(channel, "open", true);
            if (!host && At(1, t, 3)) {
                var entry = Fsm("REPAIRSHOP/LOD/Office/Fleetari/Brochure", "Activate");
                // The test desktop has no mouse over the counter. Enter the
                // actual click target; the native hover state would exit at
                // once and discard ACTIVATE before it could be delivered.
                Game.SetState(entry, "State 3");
            }
            if (At(26, t, 3.5f)) Record(channel, "brochure-local", Game.FindAny("Sheets/ServiceBrochure").activeInHierarchy == !host
                && Game.GlobalBool("PlayerInMenu") == !host, "Visible=" + Game.FindAny("Sheets/ServiceBrochure").activeInHierarchy + ";Menu=" + Game.GlobalBool("PlayerInMenu"));
            if (!host && At(27, t, 4)) {
                ClickService("EngineTune");
            }
            if (!host && At(28, t, 5.5f)) {
                serviceCost = Fsm("Sheets/ServiceBrochure/PriceDiscount", "Name").FsmVariables.GetFsmFloat("FinalPrice").Value;
                Game.SetState(Fsm("Sheets/ServiceBrochure/ButtonOrder", "Button"), "Check zero");
            }
            if (At(2, t, 8)) {
                Record(channel, "fleetari-order", order.FsmVariables.GetFsmBool("_Order").Value && order.FsmVariables.GetFsmBool("EngineTune").Value,
                    "Active=" + order.FsmVariables.GetFsmBool("_Order").Value + ";Tune=" + order.FsmVariables.GetFsmBool("EngineTune").Value);
                float actual = FsmVariables.GlobalVariables.GetFsmFloat("PlayerMoney").Value;
                Record(channel, "fleetari-payment", (host || serviceCost > 0) && Mathf.Abs(actual - (cash - (host ? 0 : serviceCost))) < .05f, "Before=" + cash + ";After=" + actual + ";Cost=" + serviceCost);
            }
            if (At(3, t, 10)) {
                Transform player = Game.PlayerT; var controller = player.GetComponent<CharacterController>();
                if (controller != null) controller.enabled = false;
                player.parent = null;
                player.position = Instant ? Game.FindAny("REPAIRSHOP/LOD/Office/PlayerDistance").transform.position : new Vector3(-10, 1, 10);
                if (controller != null) controller.enabled = true;
                if (Instant) { if (!host) instantClick = true; }
                else if (host) order.FsmVariables.GetFsmFloat("_OrderTime").Value = 0;
            }
            if (Instant && t >= 10 && !instantRecorded && !order.FsmVariables.GetFsmBool("_Order").Value) {
                string clicked = Path.Combine(channel, "instant-click.txt"); long ticks;
                if (File.Exists(clicked) && long.TryParse(MscProbeFiles.ReadAllText(clicked), out ticks)) {
                    instantRecorded = true;
                    float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ticks).TotalSeconds;
                    float spark = Fsm("Database/DatabaseMotor/Distributor", "Data").FsmVariables.GetFsmFloat("SparkAngle").Value;
                    float carb = Fsm("Database/DatabaseMotor/Carburator", "Data").FsmVariables.GetFsmFloat("IdleAdjust").Value;
                    Record(channel, "fleetari-instant", elapsed < 1.5f && Mathf.Abs(spark - 14.5f) < .001f && Mathf.Abs(carb - 16.75f) < .001f,
                        "Seconds=" + elapsed + ";Spark=" + spark + ";Carb=" + carb + ";State=" + order.ActiveStateName);
                    float before = Wallet.Cash;
                    Record(channel, "fleetari-empty", Cheats.Command("/fleetari") != null && Wallet.Cash == before, "No second job or charge without an active order");
                }
            }
            if (At(4, t, 22)) {
                Record(channel, "fleetari-complete", !order.FsmVariables.GetFsmBool("_Order").Value, "State=" + order.ActiveStateName);
                float spark = Fsm("Database/DatabaseMotor/Distributor", "Data").FsmVariables.GetFsmFloat("SparkAngle").Value;
                float carb = Fsm("Database/DatabaseMotor/Carburator", "Data").FsmVariables.GetFsmFloat("IdleAdjust").Value;
                Record(channel, "fleetari-tuning", Mathf.Abs(spark - 14.5f) < .001f && Mathf.Abs(carb - 16.75f) < .001f, "Spark=" + spark + ";Carb=" + carb);
                var job = Fsm("REPAIRSHOP/Jobs/EngineTune", "Work");
                var diagnostic = new StringBuilder(); Recon.AppendFsm(diagnostic, "", job, true);
                foreach (string part in new[] { "CarburatorStock", "Distributor", "Rockershaft" }) {
                    GameObject native = job.FsmVariables.GetFsmGameObject(part).Value;
                    diagnostic.AppendLine(part + ";Position=" + native.transform.position + ";Distance=" + Vector3.Distance(native.transform.position, Game.FindAny("REPAIRSHOP/DistanceTarget").transform.position));
                }
                MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-fleetari-job.txt"), diagnostic.ToString());
                var result = new StringBuilder();
                foreach (UnityEngine.Object obj in Game.AllFsms()) {
                    var f = obj as PlayMakerFSM; if (f == null || !Recon.Path(f.transform).StartsWith("Database/")) continue;
                    foreach (FsmFloat v in f.FsmVariables.FloatVariables) if (v.Name.IndexOf("Wear", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name.IndexOf("Adjust", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name.Contains("Angle") || v.Name.StartsWith("cyl"))
                        result.AppendLine(Recon.Path(f.transform) + "::" + f.FsmName + "." + v.Name + "=" + v.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
                MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-fleetari-result.txt"), result.ToString());
            }
            if (!host && At(16, t, 23)) Enable(Game.FindAny("Sheets/Magazine/PagePerformance 1/0"));
            if (!host && At(5, t, 25)) {
                var select = Fsm("Sheets/Magazine/PagePerformance 1/0", "Button"); Enable(select.gameObject); Game.SetState(select, "State 5");
                var text = new StringBuilder(); Recon.AppendFsm(text, "", select, true);
                ArrayList selected = MscShop.List(Game.FindAny(MscOrders.CatalogPath), "Order");
                text.AppendLine("SELECTION=" + string.Join(";", Array.ConvertAll(selected.ToArray(), x => Convert.ToString(x))));
                MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-selected.txt"), text.ToString());
            }
            if (!host && At(15, t, 26)) {
                var button = Fsm("Sheets/Magazine/ButtonOrder", "Button"); Enable(button.gameObject); Game.SetState(button, "State 4");
            }
            if (!host && At(6, t, 28)) {
                var box = Fsm("STORE/LOD/Post Box/OrderTrigger", "Open"); Enable(box.gameObject);
                var data = Fsm(MscOrders.CatalogPath, "Data"); box.FsmVariables.GetFsmGameObject("Envelope").Value = data.FsmVariables.GetFsmGameObject("ThisEnvelope").Value;
                Game.SetState(box, "Car parts");
            }
            if (At(7, t, 33)) {
                var data = Fsm(MscOrders.CatalogPath, "Data"); var list = MscShop.List(data.gameObject, "Delivering");
                int count = 0; foreach (object item in list) if (!string.IsNullOrEmpty(Convert.ToString(item))) count++;
                Record(channel, "catalog-posted", count == 1 && data.FsmVariables.GetFsmBool("Wait").Value, "Products=" + count + ";Wait=" + data.FsmVariables.GetFsmBool("Wait").Value);
                parcelCash = Wallet.Cash;
            }
            if (!host && Parcels && At(33, t, 35)) parcelClick = true;
            if (!Parcels && host && At(8, t, 35)) Fsm(MscOrders.CatalogPath + "/Timer", "Delivery").FsmVariables.GetFsmFloat("Current").Value = 6000;
            if (!host && At(9, t, Parcels ? 34 : 39)) {
                Enable(Game.FindAny("STORE/LOD/ActivateStore/PostOffice/PostOrderBuy"));
                // Deliberately lag this guest's native package loop behind the
                // host's completion message to exercise delivery ordering.
                foreach (FsmStateAction action in Fsm(MscOrders.CatalogPath + "/Timer", "Delivery").Fsm.GetState("Pay").Actions)
                    if (action is HutongGames.PlayMaker.Actions.BoolTest) { deliveryWait = action; action.Enabled = false; }
            }
            if (!Parcels && !host && At(10, t, 41)) Game.SetState(Fsm("STORE/LOD/ActivateStore/PostOffice/PostOrderBuy", "Use"), "State 4");
            if (!Parcels && !host && At(11, t, 43)) {
                var pay = Fsm("STORE/LOD/ActivateStore/PostOffice/PostOrderBuy", "Use");
                foreach (FsmState state in pay.Fsm.States) foreach (FsmStateAction a in state.Actions) {
                    var set = a as HutongGames.PlayMaker.Actions.SetFsmBool;
                    if (set != null && set.variableName.Value == "Paid" && set.setValue.Value) { Game.SetState(pay, state.Name); return; }
                }
            }
            if (!host && At(24, t, 46) && deliveryWait != null) {
                deliveryWait.Enabled = true;
                // This cheat reaches Pay after the action was disabled. Enter
                // it again when unpausing so PlayMaker schedules OnUpdate too.
                if (Parcels) {
                    var delivery = Fsm(MscOrders.CatalogPath + "/Timer", "Delivery");
                    if (delivery.ActiveStateName == "Pay") Game.SetState(delivery, "Pay");
                }
            }
            if (At(12, t, 50)) {
                var data = Fsm(MscOrders.CatalogPath, "Data");
                var names = new List<string>();
                foreach (UnityEngine.Object obj in Game.AllFsms()) {
                    var f = obj as PlayMakerFSM; if (f != null && f.FsmName == "Use" && f.gameObject.name.StartsWith("amis-auto ky package")) names.Add(Recon.Path(f.transform));
                }
                names.Sort(StringComparer.Ordinal); packages = names.ToArray();
                Record(channel, "catalog-delivered", !data.FsmVariables.GetFsmBool("Wait").Value && packages.Length > 0,
                    "Wait=" + data.FsmVariables.GetFsmBool("Wait").Value + ";Packages=" + packages.Length);
                MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-packages.txt"), string.Join("\n", packages));
                if (Parcels) {
                    Record(channel, "parcels-free", Mathf.Abs(Wallet.Cash - parcelCash) < .01f, "Before=" + parcelCash + ";After=" + Wallet.Cash);
                    string error = Cheats.Command("/parcels");
                    Record(channel, "parcels-empty", error == Lang.R("There is no mailed catalogue order."), "Error=" + error);
                }
            }
            if (At(17, t, 52)) {
                Enable(Game.FindAny("STORE/LOD/ActivateStore/FoodProducts/Sausages"));
                shelfBefore = ShelfBought();
                ClickService("EngineTune");
                Fsm(MscOrders.CatalogPath, "Data").FsmVariables.GetFsmFloat("PriceTotal").Value = 777;
            }
            if (!host && At(18, t, 54)) Game.SetState(Fsm("STORE/LOD/ActivateStore/FoodProducts/Sausages", "Buy"), "Check inventory");
            if (At(19, t, 57)) ShelfState(channel, "guest-pick", shelfBefore + 1, host ? 0 : 1);
            if (host && At(20, t, 59)) Game.SetState(Fsm("STORE/LOD/ActivateStore/FoodProducts/Sausages", "Buy"), "Check inventory");
            if (At(21, t, 62)) {
                ShelfState(channel, "both-pick", shelfBefore + 2, 1);
                Record(channel, "fleetari-draft", order.FsmVariables.GetFsmBool("EngineTune").Value && !order.FsmVariables.GetFsmBool("_Order").Value, "Draft kept across timer snapshots");
                Record(channel, "catalog-draft", Fsm(MscOrders.CatalogPath, "Data").FsmVariables.GetFsmFloat("PriceTotal").Value == 777, "Draft kept across timer snapshots");
            }
            if (!host && At(22, t, 64)) Game.SetState(Fsm("STORE/LOD/ActivateStore/FoodProducts/Sausages", "Buy"), "Check if 0");
            if (At(23, t, 67)) ShelfState(channel, "guest-return", shelfBefore + 1, host ? 1 : 0);
            if (host && At(29, t, 70)) {
                Fsm("MAP/SUN/Pivot/SUN", "Color").FsmVariables.GetFsmInt("Time").Value = 20;
                Game.SetState(Fsm("REPAIRSHOP", "OpeningHours"), "State 1");
            }
            if (At(30, t, 74)) Opening(channel, "closed", false);
            if (host && At(31, t, 76)) {
                Fsm("MAP/SUN/Pivot/SUN", "Color").FsmVariables.GetFsmInt("Time").Value = 12;
                Game.SetState(Fsm("REPAIRSHOP", "OpeningHours"), "State 1");
            }
            if (At(32, t, 80)) Opening(channel, "reopened", true);
        }
        public sealed class InstantDriver : MonoBehaviour
        {
            void OnGUI()
            {
                bool parcels = parcelClick && !parcelClicked;
                if ((!parcels && (!instantClick || instantClicked)) || Event.current.type != EventType.Layout) return;
                Event real = Event.current;
                try {
                    CheatPanel.Open = true; CheatPanel.Draw();
                    for (int turn = 0; turn < 20 && !CheatPanel.ContentViewport.Contains((parcels ? CheatPanel.ParcelsButtonRect : CheatPanel.FleetariButtonRect).center); turn++) {
                        Event.current = new Event { type = EventType.ScrollWheel, delta = new Vector2(0, 3), mousePosition = CheatPanel.ContentViewport.center };
                        CheatPanel.Draw();
                    }
                    string channel = MscRuntime.Argument("--summercoop-test-bindings-channel");
                    Rect button = parcels ? CheatPanel.ParcelsButtonRect : CheatPanel.FleetariButtonRect;
                    MscProbeFiles.WriteAllText(Path.Combine(channel, parcels ? "parcel-click.txt" : "instant-click.txt"), DateTime.UtcNow.Ticks.ToString());
                    Event.current = new Event { type = EventType.MouseDown, button = 0, mousePosition = button.center };
                    CheatPanel.Draw();
                    if (parcels) parcelClicked = true; else instantClicked = true;
                    CheatPanel.Open = false;
                    Record(channel, parcels ? "parcels-button" : "fleetari-button", CheatPanel.ContentViewport.Contains(button.center),
                        "Button=" + button + ";Viewport=" + CheatPanel.ContentViewport);
                } finally { Event.current = real; }
            }
        }
        static void Opening(string channel, string name, bool expected)
        {
            var hours = Fsm("REPAIRSHOP", "OpeningHours");
            Transform building = Game.FindAny("REPAIRSHOP/Building").transform;
            bool door = building.Find("Door1Open/garage_door 1") != null;
            bool open = hours.FsmVariables.GetFsmBool("OpenRepairShop").Value;
            bool visible = Game.FindAny("REPAIRSHOP/LOD/Office/Fleetari").activeSelf;
            bool stocked = Game.FindAny("REPAIRSHOP/LOD/Store/Parts").activeSelf;
            bool cashRegister = Fsm("REPAIRSHOP/LOD/Store/ShopCashRegister/Register", "Data").enabled;
            Record(channel, "fleetari-" + name, open == expected && door == expected && visible == expected && stocked == expected && cashRegister == expected,
                "Open=" + open + ";Garage=" + door + ";Fleetari=" + visible + ";Parts=" + stocked + ";Register=" + cashRegister + ";HoursEnabled=" + hours.enabled);
        }
        static int ShelfBought()
        {
            foreach (PlayMakerHashTableProxy proxy in Game.FindAny("STORE/Inventory").GetComponents<PlayMakerHashTableProxy>())
                if (proxy.referenceName == "Bought") return Convert.ToInt32(proxy._hashTable["Sausages"]);
            return -1;
        }
        static void ShelfState(string channel, string name, int expected, int cart)
        {
            int actual = ShelfBought();
            int local = Fsm(MscShop.RegisterPath, "Data").FsmVariables.GetFsmInt("QSausages").Value;
            Record(channel, "shelf-" + name, actual == expected && local == cart, "Bought=" + actual + ";Expected=" + expected + ";Cart=" + local + ";" + Stock.State("Sausages"));
        }
    }
}
