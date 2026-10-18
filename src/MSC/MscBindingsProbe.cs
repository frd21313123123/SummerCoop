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
        static float cash;
        static int shelfBefore;
        static FsmStateAction deliveryWait;
        static string[] packages;
        static string Role { get { return MWCoop.Net.Session.IsHost ? "host" : "guest"; } }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-bindings-channel");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 15) return;
            if (!dumped) { Dump(channel); dumped = true; }
            Exercise(channel);
        }
        static void Dump(string channel)
        {
            var text = new StringBuilder();
            foreach (UnityEngine.Object obj in Game.AllFsms())
            {
                var f = obj as PlayMakerFSM; if (f == null || f.hideFlags != HideFlags.None) continue;
                string path = Recon.Path(f.transform);
                if (!path.StartsWith("REPAIRSHOP/") && !path.StartsWith("Sheets/") && !path.StartsWith("STORE/")
                    && !path.StartsWith("ITEMS/helmet") && !path.StartsWith("ITEMS/coffee cup")
                    && !path.StartsWith("ITEMS/sofa") && !path.StartsWith("Systems/Leaks")
                    && !path.StartsWith("Spawner/") && !path.Contains("SteamFountain")) continue;
                try {
                    if (!f.Fsm.Initialized) continue;
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
            if (!MscOrders.Bound) return;
            string ready = Path.Combine(channel, Role + "-ready.txt");
            if (!File.Exists(ready)) MscProbeFiles.WriteAllText(ready, "ready");
            string clock = Path.Combine(channel, "start.txt");
            if (host && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(clock)) MscProbeFiles.WriteAllText(clock, DateTime.UtcNow.AddSeconds(3).Ticks.ToString());
            if (start == 0 && (!File.Exists(clock) || !long.TryParse(MscProbeFiles.ReadAllText(clock), out start))) return;
            float t = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start).TotalSeconds;
            var order = Fsm(MscOrders.RepairPath, "Data");
            if (At(0, t, 0)) {
                cash = FsmVariables.GlobalVariables.GetFsmFloat("PlayerMoney").Value;
                if (host) {
                    Rigidbody car = VehicleSync.Body("SATSUMA");
                    car.position = Game.FindAny("REPAIRSHOP/DistanceTarget").transform.position + Vector3.up;
                    car.velocity = car.angularVelocity = Vector3.zero;
                }
                Fsm("Database/DatabaseMotor/Distributor", "Data").FsmVariables.GetFsmFloat("SparkAngle").Value = 4;
                Fsm("Database/DatabaseMotor/Carburator", "Data").FsmVariables.GetFsmFloat("IdleAdjust").Value = 12;
                Record(channel, "references", Wear.ItemObject(0) != null && Wear.ItemObject(1) != null && typeof(MscRuntime).Assembly.GetType("MWCoop.CarSale") == null,
                    "Helmet=" + (Wear.ItemObject(0) != null) + ";Cup=" + (Wear.ItemObject(1) != null) + ";Wallet=" + Wallet.Ready);
                if (!host) Enable(Game.FindAny("Sheets/ServiceBrochure/ButtonOrder"));
            }
            if (!host && At(1, t, 3)) {
                ClickService("EngineTune");
                var button = Fsm("Sheets/ServiceBrochure/ButtonOrder", "Button"); Enable(button.gameObject);
                button.FsmVariables.GetFsmFloat("Price").Value = 100;
                Game.SetState(button, "Date");
            }
            if (At(2, t, 8)) {
                Record(channel, "fleetari-order", order.FsmVariables.GetFsmBool("_Order").Value && order.FsmVariables.GetFsmBool("EngineTune").Value,
                    "Active=" + order.FsmVariables.GetFsmBool("_Order").Value + ";Tune=" + order.FsmVariables.GetFsmBool("EngineTune").Value);
                float actual = FsmVariables.GlobalVariables.GetFsmFloat("PlayerMoney").Value;
                Record(channel, "fleetari-payment", Mathf.Abs(actual - (cash - (host ? 0 : 100))) < .05f, "Before=" + cash + ";After=" + actual);
            }
            if (At(3, t, 10)) {
                // Native Fleetari requires the player to leave the workshop.
                Transform player = Game.PlayerT; var controller = player.GetComponent<CharacterController>();
                if (controller != null) controller.enabled = false;
                player.parent = null; player.position = new Vector3(-10, 1, 10);
                if (controller != null) controller.enabled = true;
                if (host) order.FsmVariables.GetFsmFloat("_OrderTime").Value = 0;
            }
            if (At(4, t, 22)) {
                Record(channel, "fleetari-complete", !order.FsmVariables.GetFsmBool("_Order").Value, "State=" + order.ActiveStateName);
                float spark = Fsm("Database/DatabaseMotor/Distributor", "Data").FsmVariables.GetFsmFloat("SparkAngle").Value;
                float carb = Fsm("Database/DatabaseMotor/Carburator", "Data").FsmVariables.GetFsmFloat("IdleAdjust").Value;
                Record(channel, "fleetari-tuning", Mathf.Abs(spark - 14.5f) < .001f && Mathf.Abs(carb - 16.75f) < .001f, "Spark=" + spark + ";Carb=" + carb);
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
            }
            if (host && At(8, t, 35)) Fsm(MscOrders.CatalogPath + "/Timer", "Delivery").FsmVariables.GetFsmFloat("Current").Value = 6000;
            if (!host && At(9, t, 39)) {
                Enable(Game.FindAny("STORE/LOD/ActivateStore/PostOffice/PostOrderBuy"));
                // Deliberately lag this guest's native package loop behind the
                // host's completion message to exercise delivery ordering.
                foreach (FsmStateAction action in Fsm(MscOrders.CatalogPath + "/Timer", "Delivery").Fsm.GetState("Pay").Actions)
                    if (action is HutongGames.PlayMaker.Actions.BoolTest) { deliveryWait = action; action.Enabled = false; }
            }
            if (!host && At(10, t, 41)) Game.SetState(Fsm("STORE/LOD/ActivateStore/PostOffice/PostOrderBuy", "Use"), "State 4");
            if (!host && At(11, t, 43)) {
                var pay = Fsm("STORE/LOD/ActivateStore/PostOffice/PostOrderBuy", "Use");
                foreach (FsmState state in pay.Fsm.States) foreach (FsmStateAction a in state.Actions) {
                    var set = a as HutongGames.PlayMaker.Actions.SetFsmBool;
                    if (set != null && set.variableName.Value == "Paid" && set.setValue.Value) { Game.SetState(pay, state.Name); return; }
                }
            }
            if (!host && At(24, t, 46) && deliveryWait != null) deliveryWait.Enabled = true;
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
