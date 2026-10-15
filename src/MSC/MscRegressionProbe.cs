using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Explicitly enabled only by the isolated two-process regression runner.
    public static class MscRegressionProbe
    {
        public static readonly List<string> Results = new List<string>();
        static bool inspected;
        static bool prepared;
        static long clock;
        public static float Elapsed = -1;
        public static readonly Dictionary<string, List<GameObject>> Products = new Dictionary<string, List<GameObject>>();
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-regression-channel");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 28) return;
            if (!inspected) { inspected = true; Inspect(channel); }
            if (!prepared) { prepared = true; Prepare(channel); }
            string start = Path.Combine(channel, "start.txt");
            if (clock == 0)
            {
                File.WriteAllText(Path.Combine(channel, Role + "-ready.txt"), "ready");
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(start)) File.WriteAllText(start, DateTime.UtcNow.AddSeconds(4).Ticks.ToString());
                if (!File.Exists(start) || !long.TryParse(File.ReadAllText(start), out clock)) return;
            }
            Elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds;
            if (Elapsed < 0) return;
            MscPlayerProbe.Update(Elapsed, channel);
            MscFluidProbe.Update(Elapsed, channel);
            MscIslandProbe.Update(Elapsed, channel);
            MscShopProbe.Update(Elapsed, channel);
            if (Elapsed > 8 && !File.Exists(Path.Combine(channel, Role + "-products.txt"))) DumpProducts(channel);
        }
        static void Prepare(string channel)
        {
            foreach (string product in new[] { "MotorOil", "Coolant", "BrakeFluid", "TwoStroke" })
            {
                Products[product] = new List<GameObject>();
                for (int n = 0; n < (product == "BrakeFluid" ? 3 : 1); n++)
                {
                PlayMakerFSM f = Game.FsmOn(Game.FindAny("Spawner/CreateItems"), product);
                Game.SetState(f, "Create product");
                var v = f.FsmVariables.FindFsmGameObject("New");
                if (v != null && v.Value != null)
                {
                    v.Value.SetActive(true);
                    foreach (Rigidbody rb in v.Value.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.useGravity = false; }
                    foreach (Collider col in v.Value.GetComponentsInChildren<Collider>(true)) col.enabled = false;
                    v.Value.transform.position = new Vector3(-15, 3, 7);
                    Products[product].Add(v.Value);
                }
                }
            }
            if (Session.IsHost)
            {
                PlayMakerFSM bag = Game.FsmOn(Game.FindAny("MISC/fireworks bag(itemx)"), "Open");
                Game.SetState(bag, "Play anim");
            }
        }
        static void DumpProducts(string channel)
        {
            var text = new StringBuilder();
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None) continue;
                string path = Recon.Path(f.transform).ToLowerInvariant();
                if (path.Contains("motoroil") || path.Contains("coolant") || path.Contains("brakefluid") || path.Contains("rocket") || path.Contains("saunathrow"))
                { text.AppendLine("===== " + Recon.Path(f.transform) + "::" + f.FsmName); Recon.AppendFsm(text, "", f, true); }
            }
            foreach (Rigidbody rb in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                text.AppendLine("BODY " + Recon.Path(rb.transform) + " key=" + Props.WorldKeyOf(rb));
            File.WriteAllText(Path.Combine(channel, Role + "-products.txt"), text.ToString());
        }
        public static void Record(string channel, string name, bool pass, string detail)
        {
            string line = "Regression=" + name + ";Pass=" + pass + ";" + detail;
            Results.Add(line); Log.Info(line);
            File.WriteAllText(Path.Combine(channel, Role + "-" + name + ".txt"), line);
        }
        static void Inspect(string channel)
        {
            var text = new StringBuilder();
            var all = new List<PlayMakerFSM>();
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None || !f.transform.root.gameObject.activeInHierarchy) continue;
                string path = Recon.Path(f.transform).ToLowerInvariant();
                bool selected = path.Contains("cottage") || path.Contains("bucket") || path.Contains("ladle")
                    || path.Contains("firework") || path.Contains("lantern") || path.Contains("logcarrier")
                    || path.Contains("woodcarrier") || path.Contains("logwall") || path.Contains("chopping")
                    || path.Contains("firewood") || path.Contains("woodaxe") || path.Contains("sleeptrigger")
                    || path.Contains("oilpan") || path.Contains("radiator") || path.Contains("brakemaster")
                    || path.Contains("clutchmaster") || path.Contains("fueltank") || path.Contains("fluid")
                    || path.Contains("fuelpump") || path.Contains("twinbrake") || path.Contains("gastank")
                    || path.StartsWith("map/sun") || path.StartsWith("perajarvi/store") || path.Contains("pub")
                    || path == "systems/death" || path == "systems/setup game" || path.StartsWith("player") && path.Split('/').Length < 3
                    || path.StartsWith("store/") || path.Contains("playertrigger") || path.Contains("scoop")
                    || path.Contains("carrier") || path.Contains("carry") || path.Contains("firework")
                    || f.gameObject.name == "CreateItems" || f.FsmName.ToLowerInvariant().Contains("crouch");
                if (selected) all.Add(f);
            }
            all.Sort((a, b) => string.CompareOrdinal(Recon.Path(a.transform) + "::" + a.FsmName, Recon.Path(b.transform) + "::" + b.FsmName));
            foreach (PlayMakerFSM f in all)
            {
                text.Append("===== ").Append(Recon.Path(f.transform)).Append("::").Append(f.FsmName)
                    .Append(" active=").Append(f.gameObject.activeInHierarchy).Append(" owner=").Append(Replay.Owner(f) ?? "none").Append('\n');
                Recon.AppendFsm(text, "", f, true);
            }
            text.AppendLine("===== GLOBALS");
            foreach (FsmBool b in FsmVariables.GlobalVariables.BoolVariables) text.AppendLine(b.Name + "=" + b.Value);
            File.WriteAllText(Path.Combine(channel, Role + "-native.txt"), text.ToString());
            var index = new StringBuilder();
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || f.hideFlags != HideFlags.None || !f.transform.root.gameObject.activeInHierarchy) continue;
                index.Append(Recon.Path(f.transform)).Append("::").Append(f.FsmName).Append(" active=").Append(f.gameObject.activeInHierarchy);
                foreach (FsmFloat v in f.FsmVariables.FloatVariables) index.Append(" float:").Append(v.Name);
                foreach (FsmBool v in f.FsmVariables.BoolVariables) index.Append(" bool:").Append(v.Name);
                index.AppendLine();
            }
            File.WriteAllText(Path.Combine(channel, Role + "-index.txt"), index.ToString());
            Results.Add("RegressionInspection=Fsms=" + all.Count);
        }
    }
}
