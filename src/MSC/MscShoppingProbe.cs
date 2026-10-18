using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;
namespace MWCoop
{
    // Opt-in native two-process check; no mouse input or personal saves.
    public static class MscShoppingProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static long clock;
        static float cash;
        static string bagId;
        static GameObject filter;
        static HashSet<string> initialItems;
        static int initialFilterCounter;
        static readonly List<string> concurrentBags = new List<string>();
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        static bool At(int step, float t, float at) { return t >= at && steps.Add(step); }
        static PlayMakerFSM Register { get { return Game.FsmOn(Game.FindAny(MscShop.RegisterPath), "Data"); } }
        static float Cash { get { return FsmVariables.GlobalVariables.GetFsmFloat("PlayerMoney").Value; } }
        static HashSet<string> ItemIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            { string id = Props.ItemId(body.gameObject); if (id.Length > 0 && !MscShop.IsBag(Game.FsmOn(body.gameObject, "Use"))) ids.Add(id); }
            return ids;
        }
        public static string WorldItems()
        {
            var ids = new List<string>();
            foreach (UnityEngine.Object obj in Game.AllFsms())
            {
                var f = obj as PlayMakerFSM; if (f == null || f.FsmName != "Use" || !f.gameObject.activeInHierarchy || f.GetComponent<Rigidbody>() == null || MscShop.IsBag(f)) continue;
                var id = f.FsmVariables.FindFsmString("ID"); if (id != null && id.Value.Length > 0) ids.Add(id.Value);
            }
            ids.Sort(StringComparer.Ordinal); return string.Join(",", ids.ToArray());
        }
        static void References(string channel)
        {
            var failed = new List<string>();
            foreach (string key in new[] { MscShop.RegisterPath + "::Data", MscShop.ProductsPath + "::Logic", "Spawner/CreateShoppingbag::ShoppingBag",
                "STORE::Fuelprices", "STORE/LOD/Post Box/OrderTrigger::Open", "YARD/Building/LIVINGROOM/Telephone/Logic/PhoneLogic::Ring", "Systems/ImportCarTexture::Get" })
            { string[] parts = key.Split(new[] { "::" }, StringSplitOptions.None); if (Game.FsmOn(Game.FindAny(parts[0]), parts[1]) == null) failed.Add(key); }
            Record(channel, "native-references", failed.Count == 0, "Missing=" + string.Join(",", failed.ToArray()));
        }
        static void Record(string channel, string test, bool pass, string detail) { MscRegressionProbe.Record(channel, "shopping-" + test, pass, detail); }
        static List<PlayMakerFSM> Bags()
        {
            var result = new List<PlayMakerFSM>();
            foreach (UnityEngine.Object obj in Game.AllFsms())
            {
                var f = obj as PlayMakerFSM; if (!MscShop.IsBag(f) || !f.gameObject.activeInHierarchy) continue;
                var id = f.FsmVariables.FindFsmString("ID"); if (id == null || id.Value.Length == 0) continue;
                MscShop.Track(f); result.Add(f);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.FsmVariables.GetFsmString("ID").Value, b.FsmVariables.GetFsmString("ID").Value));
            return result;
        }
        static int Count(PlayMakerFSM bag)
        {
            if (bag == null) return -1; int count = 0;
            foreach (string name in new[] { "Items", "Spraycans" }) foreach (int value in MscShop.Values(bag.gameObject, name)) count += value;
            return count;
        }
        static void Take(string id, bool all)
        {
            PlayMakerFSM bag = MscShop.FindBag(id); if (bag == null) throw new Exception("Probe bag missing: " + id);
            Game.SetState(bag, "Confirm"); Game.SetState(bag, all ? "Spawn all" : "Spawn one");
        }
        static void Buy(params string[] names)
        {
            var f = Register;
            foreach (FsmInt q in f.FsmVariables.IntVariables) if (q.Name.StartsWith("Q")) q.Value = 0;
            foreach (string name in names) f.FsmVariables.GetFsmInt(name).Value++;
            f.FsmVariables.GetFsmFloat("Total").Value = 100; Game.SetState(f, "Purchase");
        }
        static void Snapshot(string channel, string test, int count)
        {
            var bag = MscShop.FindBag(bagId); var text = new StringBuilder();
            foreach (PlayMakerFSM b in Bags())
            {
                text.Append(b.FsmVariables.GetFsmString("ID").Value).Append(":");
                foreach (string name in new[] { "Items", "Spraycans" }) text.Append(name).Append('=').Append(string.Join(",", Array.ConvertAll(MscShop.Values(b.gameObject, name), x => x.ToString()))).Append('/');
                text.Append(';');
            }
            Record(channel, test, Count(bag) == count, "Count=" + Count(bag) + ";Bags=" + text);
        }
        static void Dump(string channel, string test)
        {
            var text = new StringBuilder();
            foreach (UnityEngine.Object obj in Game.AllFsms())
            {
                var f = obj as PlayMakerFSM; if (f == null || f.hideFlags != HideFlags.None || !f.Fsm.Initialized) continue;
                string path = Recon.Path(f.transform);
                if (path == MscShop.ProductsPath || path == "Spawner/CreateShoppingbag" || path == MscShop.RegisterPath
                    || MscShop.IsBag(f) || path.IndexOf("oil filter", StringComparison.OrdinalIgnoreCase) >= 0)
                { text.AppendLine("===== " + path + "::" + f.FsmName); Recon.AppendFsm(text, "", f, true); }
            }
            MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-" + test + "-native.txt"), text.ToString());
        }
        static PlayMakerFSM FilterData { get { return Game.FsmOn(Game.FindAny("Database/PartsStatus/Oilfilter"), "Data"); } }
        static PlayMakerFSM FilterUse { get { return filter == null ? null : Game.FsmOn(filter, "Use"); } }
        static void FilterState(string channel, string name, bool installed, int stage)
        {
            PlayMakerFSM use = FilterUse, screw = filter == null ? null : Game.FsmOn(filter, "Screw");
            bool actual = use != null && use.FsmVariables.GetFsmBool("Installed").Value;
            float tight = use == null ? -1 : use.FsmVariables.GetFsmFloat("Tightness").Value;
            int actualStage = screw == null ? -1 : screw.FsmVariables.GetFsmInt("Stage").Value;
            bool current = FilterData.FsmVariables.GetFsmGameObject("CurrentFilter").Value == filter;
            Record(channel, name, use != null && actual == installed && (stage < 0 || actualStage == stage && Mathf.Abs(tight - stage) < .01f) && (!installed || current),
                "Id=" + (use == null ? "missing" : use.FsmVariables.GetFsmString("ID").Value) + ";Installed=" + actual + ";Stage=" + actualStage + ";Tightness=" + tight + ";Current=" + current);
            Dump(channel, name);
        }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-shopping-channel"); if (channel.Length == 0 || !Wallet.Ready) return;
            string start = Path.Combine(channel, "start.txt");
            if (clock == 0)
            {
                MscProbeFiles.WriteAllText(Path.Combine(channel, Role + "-ready.txt"), "ready");
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(start)) MscProbeFiles.WriteAllText(start, DateTime.UtcNow.AddSeconds(5).Ticks.ToString());
                if (!File.Exists(start) || !long.TryParse(MscProbeFiles.ReadAllText(start), out clock)) return;
            }
            float t = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds; if (t < 0) return;
            if (At(0, t, 4))
            {
                cash = Cash; initialItems = ItemIds(); References(channel); Dump(channel, "before");
                initialFilterCounter = Game.FsmOn(Game.FindAny("Spawner/CreateItems"), "Oilfilter").FsmVariables.GetFsmInt("ObjectNumberInt").Value;
                if (Session.IsHost) { Register.FsmVariables.GetFsmInt("QPizza").Value = 3; Register.FsmVariables.GetFsmFloat("Total").Value = 72; Register.FsmVariables.GetFsmFloat("PriceTotal").Value = 72; }
            }
            if (At(1, t, 8) && !Session.IsHost) Buy("QSausages", "QSausages", "QPizza", "QOilfilter", "QSpray01", "QSpray01", "QBeer", "QFanbelt");
            if (At(2, t, 16))
            {
                var bags = Bags(); if (bags.Count > 0) bagId = bags[bags.Count - 1].FsmVariables.GetFsmString("ID").Value;
                Record(channel, "payment", Mathf.Abs((cash - Cash) - (Session.IsHost ? 0 : 100)) < .02f, "Before=" + cash + ";After=" + Cash);
                if (Session.IsHost) Record(channel, "cart", Register.FsmVariables.GetFsmInt("QPizza").Value == 3 && Register.FsmVariables.GetFsmFloat("PriceTotal").Value == 72, "Pizza=" + Register.FsmVariables.GetFsmInt("QPizza").Value + ";Price=" + Register.FsmVariables.GetFsmFloat("PriceTotal").Value);
                Snapshot(channel, "contents", 7);
            }
            if (At(3, t, 18) && !Session.IsHost) Take(bagId, false);
            if (At(4, t, 23)) Snapshot(channel, "guest-pick", 6);
            if (At(5, t, 25) && Session.IsHost) Take(bagId, false);
            if (At(6, t, 30)) Snapshot(channel, "host-pick", 5);
            if (At(7, t, 32)) Take(bagId, false);
            if (At(8, t, 38)) Snapshot(channel, "concurrent-pick", 3);
            if (At(9, t, 40) && !Session.IsHost) Take(bagId, true);
            if (At(10, t, 48)) Snapshot(channel, "empty", 0);
            if (At(11, t, 50)) Buy(Session.IsHost ? "QMilk" : "QOilfilter", Session.IsHost ? "QChips" : "QSpray02");
            if (At(12, t, 60))
            {
                int full = 0; foreach (PlayMakerFSM b in Bags()) if (Count(b) == 2) { full++; concurrentBags.Add(b.FsmVariables.GetFsmString("ID").Value); }
                Record(channel, "concurrent-purchase", full == 2, "FullBags=" + full); Dump(channel, "concurrent-purchase");
            }
            if (At(13, t, 64))
            {
                if (concurrentBags.Count == 2) Take(concurrentBags[Session.IsHost ? 0 : 1], true);
            }
            if (At(14, t, 74))
            {
                int left = 0; foreach (PlayMakerFSM b in Bags()) left += Math.Max(0, Count(b));
                Record(channel, "all-bags-empty", left == 0, "Remaining=" + left); Dump(channel, "final");
                var created = ItemIds(); created.ExceptWith(initialItems); var ids = new List<string>(created); ids.Sort(StringComparer.Ordinal);
                Record(channel, "spawned-objects", ids.Count == 12, "Ids=" + string.Join(",", ids.ToArray()));
            }
            if (At(15, t, 78))
            {
                var candidates = new List<GameObject>();
                foreach (UnityEngine.Object obj in Game.AllFsms())
                {
                    var f = obj as PlayMakerFSM; if (f == null || f.FsmName != "Use" || !f.gameObject.activeInHierarchy) continue;
                    var db = f.FsmVariables.FindFsmGameObject("Database"); var id = f.FsmVariables.FindFsmString("ID");
                    int number;
                    if (db != null && db.Value == FilterData.gameObject && id != null && id.Value.StartsWith("oil filter", StringComparison.Ordinal)
                        && int.TryParse(id.Value.Substring("oil filter".Length), out number) && number > initialFilterCounter) candidates.Add(f.gameObject);
                }
                candidates.Sort((a, b) => string.CompareOrdinal(Props.ItemId(a), Props.ItemId(b))); if (candidates.Count > 0) filter = candidates[0];
                Record(channel, "filters-created", candidates.Count == 2 && filter != null && Props.ItemId(filter).StartsWith("mscitem:", StringComparison.Ordinal), "Count=" + candidates.Count + ";Id=" + (filter == null ? "missing" : Props.ItemId(filter)));
                if (Session.IsHost)
                {
                    GameObject old = FilterData.FsmVariables.GetFsmGameObject("CurrentFilter").Value;
                    if (old != null) Game.SetState(Game.FsmOn(old, "Removal"), "Remove part");
                }
            }
            if (At(16, t, 84) && !Session.IsHost && filter != null)
            {
                var use = FilterUse; var trigger = use.FsmVariables.GetFsmGameObject("Trigger").Value;
                var assembly = Game.FsmOn(trigger, "Assembly"); trigger.SetActive(true);
                assembly.FsmVariables.GetFsmGameObject("Part").Value = filter; Game.SetState(assembly, "Assemble");
            }
            if (At(17, t, 90)) FilterState(channel, "filter-installed", true, 0);
            if (At(18, t, 92) && Session.IsHost && filter != null) Game.SetState(Game.FsmOn(filter, "Screw"), "Screw");
            if (At(19, t, 95) && Session.IsHost && filter != null) Game.SetState(Game.FsmOn(filter, "Screw"), "Screw");
            if (At(20, t, 100)) FilterState(channel, "filter-tightened", true, 2);
            if (At(21, t, 103) && !Session.IsHost && filter != null) Game.SetState(Game.FsmOn(filter, "Screw"), "Unscrew");
            if (At(22, t, 108)) FilterState(channel, "filter-unscrewed", true, 1);
            if (At(23, t, 110) && !Session.IsHost && filter != null) Game.SetState(Game.FsmOn(filter, "Screw"), "Unscrew");
            if (At(24, t, 114) && !Session.IsHost && filter != null) Game.SetState(Game.FsmOn(filter, "Removal"), "Remove part");
            if (At(25, t, 120)) FilterState(channel, "filter-removed", false, -1);
            if (At(26, t, 124) && Session.IsHost)
            {
                var f = Game.FsmOn(Game.FindAny("STORE"), "Fuelprices");
                f.FsmVariables.GetFsmFloat("Price98").Value = 5.55f; f.FsmVariables.GetFsmFloat("PriceDiesel").Value = 4.44f; f.FsmVariables.GetFsmFloat("PriceFuelOil").Value = 3.33f;
                Game.SetState(f, "Set table");
            }
            if (At(27, t, 130))
            {
                var f = Game.FsmOn(Game.FindAny("STORE"), "Fuelprices"); float price = f.FsmVariables.GetFsmFloat("Price98").Value;
                var pump = Game.FsmOn(Game.FindAny("STORE/LOD/Pumps/GasolinePump98/Functions/Trigger98"), "Use");
                float pumpPrice = pump.FsmVariables.GetFsmFloat("FuelPrice").Value;
                Record(channel, "fuel-price-reference", Mathf.Abs(price - 5.55f) < .001f && Mathf.Abs(pumpPrice - price) < .001f, "Price=" + price + ";Pump=" + pumpPrice);
            }
        }
    }
}
