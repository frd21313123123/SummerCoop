using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using SummerCoop.Game;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Opt-in inspection/tests of native MSC leisure and consumable mechanics.
    public static class MscActivitiesProbe
    {
        public static readonly List<string> Results = new List<string>();
        static bool inspected;
        static bool prepared, ready;
        static long ticks;
        static float preparedAt;
        static float stressBeforeSteam;
        static bool steamBlurBefore;
        static PlayMakerFSM brew, water, ingredient, fill, lidOn, lidOff, stove, fire, heat, steam, logwall, grill, pan, cup;
        static readonly Dictionary<string, List<GameObject>> products = new Dictionary<string, List<GameObject>>();
        static readonly HashSet<int> steps = new HashSet<int>();
        static readonly Dictionary<int, GameObject> logs = new Dictionary<int, GameObject>();
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static string role { get { return Session.IsHost ? "host" : "guest"; } }
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-activities-channel");
            if (channel.Length == 0 || Time.timeSinceLevelLoad < 24) return;
            if (!inspected) { inspected = true; Inspect(channel); }
            if (!prepared) { Prepare(); prepared = true; preparedAt = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - preparedAt < 5) return;
            if (!ready)
            {
                ready = true;
                foreach (var list in products.Values) foreach (GameObject go in list) { Props.Track(go); Consume.Track(go); }
                GameObject juice = products["Juice"][0]; PlayMakerFSM use = Game.FsmOn(juice, "Use");
                use.FsmVariables.FindFsmBool("ContainsJuice").Value = false; use.FsmVariables.FindFsmBool("ContainsKilju").Value = false;
                Replay.Depth++; try { use.SendEvent("GLOBALEVENT"); } finally { Replay.Depth--; }
                MscProbeFiles.WriteAllText(Path.Combine(channel, role + "-ready.txt"), "ready");
            }
            string clockFile = Path.Combine(channel, "start.txt");
            if (ticks == 0)
            {
                if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(clockFile)) MscProbeFiles.WriteAllText(clockFile, DateTime.UtcNow.AddSeconds(3).Ticks.ToString(C));
                if (!File.Exists(clockFile) || !long.TryParse(MscProbeFiles.ReadAllText(clockFile), out ticks)) return;
            }
            float t = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ticks).TotalSeconds; if (t < 0) return;
            Run(channel, t);
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
                bool selected = path.Contains("cottage") || path.Contains("sauna") || path.Contains("kilju")
                    || path.Contains("bucket") || path.Contains("coffee") || path.Contains("fish trap")
                    || f.gameObject.name == "WoodTrigger" || f.gameObject.name == "SausageTrigger" || f.gameObject.name == "CreateItems";
                if (selected) all.Add(f);
            }
            all.Sort((a, b) => string.CompareOrdinal(Recon.Path(a.transform) + "::" + a.FsmName, Recon.Path(b.transform) + "::" + b.FsmName));
            foreach (PlayMakerFSM f in all)
            {
                text.Append("===== ").Append(Recon.Path(f.transform)).Append("::").Append(f.FsmName)
                    .Append(" active=").Append(f.gameObject.activeInHierarchy).Append(" owner=").Append(Replay.Owner(f) ?? "none").Append('\n');
                Recon.AppendFsm(text, "", f, true);
            }
            MscProbeFiles.WriteAllText(Path.Combine(channel, role + "-native.txt"), text.ToString());
            Results.Add("ActivitiesInspection=Fsms=" + all.Count);
        }
        static PlayMakerFSM Fsm(string path, string name)
        {
            GameObject go = Game.FindAny(path); PlayMakerFSM f = go != null ? Game.FsmOn(go, name) : null;
            if (f == null) throw new InvalidOperationException("Native activity fixture absent: " + path + "::" + name); return f;
        }
        static void Prepare()
        {
            brew = MscBrewing.Bucket; if (brew == null) throw new InvalidOperationException("MSC brewing adapter did not resolve its bucket");
            water = Fsm("ITEMS/bucket(itemx)/BucketTrigger", "Level"); ingredient = Fsm("ITEMS/bucket(itemx)/TriggerIngredients", "Level"); fill = Fsm("ITEMS/bucket(itemx)/TriggerFill", "Fill");
            lidOn = Fsm("ITEMS/bucket(itemx)/trigger_lid", "Assembly");
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f != null && f.hideFlags == HideFlags.None && f.name == "bucket lid(itemx)" && f.FsmName == "Removal")
                {
                    if (lidOff != null && lidOff != f) throw new InvalidOperationException("Ambiguous bucket lid fixture");
                    lidOff = f;
                }
            }
            if (lidOff == null) throw new InvalidOperationException("Native bucket lid removal fixture absent");
            stove = Fsm("COTTAGE/Sauna/Stove/WoodTrigger", "Trigger"); fire = Fsm("COTTAGE/Sauna/Stove/SetFire", "Use"); heat = Fsm("COTTAGE/Sauna/Stove/Simulation", "Time"); steam = Fsm("COTTAGE/Sauna/Stove/StoveTrigger", "Steam");
            logwall = Fsm("COTTAGE/Logging/Logwall", "Use"); grill = Fsm("COTTAGE/Grill/Fireplace/SausageTrigger", "Logic");
            pan = Fsm("ITEMS/coffee pan(itemx)", "Data"); cup = Fsm("ITEMS/coffee cup(itemx)", "Use");
            // A copied profile may start with a mounted lid and some water.
            // Normalize before the timed steps; the native empty state on an inactive
            // trigger can otherwise run later, after the test has already filled the bucket.
            Replay.Depth++;
            try
            {
                Rigidbody fixtureBody = brew.GetComponent<Rigidbody>();
                if (fixtureBody != null) { fixtureBody.isKinematic = true; fixtureBody.useGravity = false; }
                brew.transform.rotation = Quaternion.identity;
                brew.transform.position = new Vector3(0, 50, 0);
                Lid(false);
                foreach (string key in new[] { "Alcohol", "Sugar", "Sweetness", "Time", "Vinegar", "Water", "Yeast" }) FireTools.SetF(brew, key, 0);
                brew.FsmVariables.FindFsmBool("KiljuFinished").Value = false;
                brew.FsmVariables.FindFsmBool("LidOn").Value = false;
                brew.SendEvent("GLOBALEVENT");
            }
            finally { Replay.Depth--; }
            Log.Info("Activity fixture bucket reset: " + BucketDetails());
            FireTools.SetF(brew, "BrewTime", 20); FireTools.SetF(fire, "BurnTime", 30);
            foreach (var entry in new[] { "Sugar:2", "Yeast:1", "Juice:1", "Sausages:1" })
            {
                string[] parts = entry.Split(':'); var list = new List<GameObject>(); products[parts[0]] = list;
                PlayMakerFSM factory = Fsm("Spawner/CreateItems", parts[0]);
                for (int i = 0; i < int.Parse(parts[1], C); i++)
                {
                    Game.SetState(factory, "Create product"); GameObject go = factory.FsmVariables.FindFsmGameObject("New").Value;
                    if (go == null) throw new InvalidOperationException("Native item factory failed: " + entry);
                    go.SetActive(true); go.transform.position = new Vector3(0, 40 + products.Count * 3, i * 3);
                    Rigidbody body = go.GetComponent<Rigidbody>(); if (body != null) { body.isKinematic = true; body.useGravity = false; }
                    foreach (Collider collider in go.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                    list.Add(go);
                }
            }
        }
        static string BucketDetails()
        {
            BrewState state = MscBrewing.Capture();
            return "Water=" + state.Water + ";Sugar=" + state.Sugar + ";Yeast=" + state.Yeast + ";Alcohol=" + state.Alcohol
                + ";Sweetness=" + state.Sweetness + ";Time=" + state.Time + ";Vinegar=" + state.Vinegar + ";State=" + brew.ActiveStateName;
        }
        static bool At(int step, float t, float at) { return t >= at && steps.Add(step); }
        static GameObject Obj(PlayMakerFSM f, string name) { FsmGameObject value = f.FsmVariables.FindFsmGameObject(name); return value != null ? value.Value : null; }
        static void Mark(GameObject go) { Home.MarkHeld(go); }
        static void AddIngredient(string kind, int index)
        {
            GameObject go = products[kind][index]; Mark(go); ingredient.gameObject.SetActive(true);
            ingredient.FsmVariables.FindFsmGameObject("Object").Value = go; Game.SetState(ingredient, kind);
        }
        static void Tap(bool on)
        {
            Mark(brew.gameObject); FireTools.SetF(water, "AddedWater", on ? 0 : FireTools.GetF(water, "AddedWater", 0));
            Game.SetState(water, on ? "Tap" : "Dilute");
        }
        static void Lid(bool on)
        {
            Mark(lidOff.gameObject); PlayMakerFSM f = on ? lidOn : lidOff; f.gameObject.SetActive(true); f.enabled = true;
            Game.SetState(f, on ? "Assemble" : "Remove part");
        }
        static void CreateLog(int number)
        {
            Game.SetState(logwall, "Create log"); GameObject log = Obj(logwall, "Log"); if (log == null) throw new InvalidOperationException("Native log creator returned no log");
            logs[number] = log;
            foreach (Rigidbody body in log.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;
        }
        static void SplitLog(int number)
        {
            GameObject log = logs[number];
            foreach (FixedJoint joint in log.GetComponentsInChildren<FixedJoint>(true)) UnityEngine.Object.Destroy(joint);
        }
        static List<GameObject> Firewood()
        {
            var result = new List<GameObject>();
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                if (body.name.StartsWith("firewood", StringComparison.Ordinal) && Props.ItemId(body.gameObject).Length > 0) result.Add(body.gameObject);
            result.Sort((a, b) => string.CompareOrdinal(Props.ItemId(a), Props.ItemId(b))); return result;
        }
        static void PutWood(int index)
        {
            List<GameObject> wood = Firewood(); if (wood.Count <= index) throw new InvalidOperationException("No native chopped wood for stove: " + wood.Count);
            GameObject go = wood[index]; Mark(go); stove.FsmVariables.FindFsmGameObject("Collider").Value = go; Game.SetState(stove, "Parent");
        }
        static int Woods() { return stove.FsmVariables.FindFsmInt("Woods").Value; }
        static int VisibleLogs()
        {
            Transform root = stove.transform.parent.Find("Woods"); int n = 0; foreach (Transform log in root) if (log.name.StartsWith("log", StringComparison.Ordinal) && log.gameObject.activeSelf) n++; return n;
        }
        static List<PlayMakerFSM> Sausages()
        {
            var result = new List<PlayMakerFSM>();
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM; if (f == null || f.FsmName != "Use") continue;
                if (Props.ItemId(f.gameObject).StartsWith("makkara", StringComparison.Ordinal)) result.Add(f);
            }
            result.Sort((a, b) => string.CompareOrdinal(Props.ItemId(a.gameObject), Props.ItemId(b.gameObject))); return result;
        }
        static void Cook(string state)
        {
            foreach (PlayMakerFSM use in Sausages())
            {
                Rigidbody body = use.GetComponent<Rigidbody>(); if (body != null) body.isKinematic = true;
                PlayMakerFSM f = Game.FsmOn(use.gameObject, "Fire");
                if (f == null || f.Fsm.GetState(state) == null) throw new InvalidOperationException("Native sausage cooking state absent: " + state);
                Game.SetState(f, state);
            }
        }
        static void Near(Vector3 point)
        {
            Transform p = Game.PlayerT; CharacterController controller = p.GetComponent<CharacterController>();
            controller.enabled = false; p.position = point + Vector3.up * 2; controller.enabled = true;
        }
        static void Hold(GameObject go)
        {
            GameObject hand = Game.PlayerPart("Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand");
            PlayMakerFSM pickup = hand != null ? Game.FsmOn(hand, "PickUp") : null;
            if (pickup == null) throw new InvalidOperationException("Native pickup fixture absent");
            pickup.FsmVariables.FindFsmGameObject("PickedObject").Value = go;
            if (go != null) Home.MarkHeld(go);
        }
        static void Record(string channel, string name, bool pass, string detail)
        {
            string line = "Activity=" + name + ";Pass=" + pass + ";" + detail;
            Results.Add(line); Log.Info(line); MscProbeFiles.WriteAllText(Path.Combine(channel, role + "-" + name + ".txt"), line);
        }
        static void Run(string channel, float t)
        {
            if (At(1, t, 1) && Session.IsHost) CreateLog(1);
            if (At(2, t, 2)) AddIngredient("Sugar", Session.IsHost ? 0 : 1);
            if (At(3, t, 3) && !Session.IsHost) AddIngredient("Yeast", 0);
            if (At(4, t, 6) && Session.IsHost) SplitLog(1);
            if (At(5, t, 9))
            {
                BrewState state = MscBrewing.Capture(); Record(channel, "ingredients", state.Sugar == 2 && state.Yeast == 1, "Sugar=" + state.Sugar + ";Yeast=" + state.Yeast);
                Record(channel, "wood-split", Firewood().Count == 2, "Pieces=" + Firewood().Count);
            }
            if (At(6, t, 11) && !Session.IsHost) { PutWood(0); Tap(true); }
            if (At(7, t, 15) && !Session.IsHost) Tap(false);
            if (At(8, t, 18) && Session.IsHost) { PutWood(0); Tap(true); }
            if (At(9, t, 22) && Session.IsHost) Tap(false);
            if (At(10, t, 26))
            {
                float amount = MscBrewing.Capture().Water; Record(channel, "water", amount > 3.4f && amount < 4.6f, "Water=" + amount.ToString("F3", C));
                Record(channel, "stove-wood", Woods() == 2 && VisibleLogs() == 2, "Woods=" + Woods() + ";Visible=" + VisibleLogs());
            }
            if (At(11, t, 28) && !Session.IsHost) Lid(true);
            if (At(12, t, 32)) Record(channel, "lid-mounted", MscBrewing.Capture().LidOn && lidOff.transform.parent == brew.transform.Find("PivotLid"), "LidOn=" + MscBrewing.Capture().LidOn + ";Parent=" + (lidOff.transform.parent != null ? lidOff.transform.parent.name : "none"));
            if (At(13, t, 34) && Session.IsHost) Lid(false);
            if (At(14, t, 38)) Record(channel, "lid-removed", !MscBrewing.Capture().LidOn && lidOff.GetComponent<Rigidbody>() != null, "LidOn=" + MscBrewing.Capture().LidOn);
            if (At(15, t, 40) && !Session.IsHost) { Lid(true); CreateLog(2); }
            if (At(16, t, 43) && !Session.IsHost) SplitLog(2);
            if (At(17, t, 48)) PutWood(Session.IsHost ? 0 : Firewood().Count - 1);
            if (At(18, t, 52)) Record(channel, "stove-concurrent", Woods() == 4 && VisibleLogs() == 4, "Woods=" + Woods() + ";Visible=" + VisibleLogs());
            if (At(19, t, 54) && !Session.IsHost) { fire.gameObject.SetActive(true); Game.SetState(fire, "Start fire 2"); }
            if (At(20, t, 59)) Record(channel, "stove-fire", fire.gameObject.activeSelf && fire.ActiveStateName == "Burn", "State=" + fire.ActiveStateName);
            if (At(48, t, 56) && Session.IsHost) CreateLog(3);
            if (At(49, t, 60) && Session.IsHost) SplitLog(3);
            if (At(21, t, 62) && Session.IsHost) { FireTools.SetF(heat, "StoveHeat", 400); FireTools.SetF(heat, "SaunaHeat", 70); }
            if (At(22, t, 68) && !Session.IsHost) Game.SetState(steam, "Check stove heat");
            if (At(50, t, 65) && !Session.IsHost) PutWood(0);
            if (At(51, t, 67))
            {
                stressBeforeSteam = Game.GlobalFloat("PlayerStress"); Behaviour blur = SteamBlur(); steamBlurBefore = blur != null && blur.enabled;
                Record(channel, "stove-full", Woods() == 4 && Firewood().Count == 2, "Woods=" + Woods() + ";UnusedLogs=" + Firewood().Count);
            }
            if (At(23, t, 70)) Record(channel, "sauna-steam", SteamVisible() && FireTools.GetF(heat, "StoveHeat", 0) > 390, "StoveHeat=" + FireTools.GetF(heat, "StoveHeat", 0).ToString("F2", C) + ";SteamVisual=" + SteamVisible() + ";SteamState=" + steam.ActiveStateName);
            if (At(24, t, 74)) Record(channel, "sauna-heat", FireTools.GetF(heat, "StoveHeat", 0) > 390 && FireTools.GetF(heat, "SaunaHeat", 0) > 50, "StoveHeat=" + FireTools.GetF(heat, "StoveHeat", 0).ToString("F2", C) + ";SaunaHeat=" + FireTools.GetF(heat, "SaunaHeat", 0).ToString("F2", C));
            if (At(52, t, 75) && Session.IsHost)
            {
                Behaviour blur = SteamBlur(); bool after = blur != null && blur.enabled;
                Record(channel, "steam-personal", blur != null && steam.ActiveStateName == "Idle" && after == steamBlurBefore && FireTools.GetF(steam, "CleanEfficiency", 0) == 0,
                    "LocalState=" + steam.ActiveStateName + ";BlurBefore=" + steamBlurBefore + ";BlurAfter=" + after + ";StressBefore=" + stressBeforeSteam.ToString("F3", C) + ";StressAfter=" + Game.GlobalFloat("PlayerStress").ToString("F3", C));
            }
            if (At(25, t, 88))
            { BrewState state = MscBrewing.Capture(); Record(channel, "kilju-finished", state.Finished && state.Alcohol > .01f && state.Sugar == 2 && state.Yeast == 1, "Finished=" + state.Finished + ";Alcohol=" + state.Alcohol.ToString("F4", C) + ";Time=" + state.Time); }
            if (At(26, t, 89)) Record(channel, "wood-burned", Woods() == 3 && VisibleLogs() == 3, "Woods=" + Woods() + ";Visible=" + VisibleLogs());
            if (At(27, t, 91) && !Session.IsHost) Lid(false);
            if (At(53, t, 92)) PutWood(Session.IsHost ? 0 : Firewood().Count - 1);
            if (At(28, t, 94) && !Session.IsHost)
            { GameObject can = products["Juice"][0]; Mark(can); fill.gameObject.SetActive(true); fill.FsmVariables.FindFsmGameObject("Object").Value = can; Game.SetState(fill, "Fill"); }
            if (At(29, t, 99))
            {
                PlayMakerFSM can = Game.FsmOn(products["Juice"][0], "Use"); BrewState state = MscBrewing.Capture();
                Record(channel, "kilju-bottled", can.FsmVariables.FindFsmBool("ContainsKilju").Value && Mathf.Abs(FireTools.GetF(can, "KiljuAlc", 0) - state.Alcohol) < .002f && state.Water > 1.8f && state.Water < 3.1f,
                    "ContainsKilju=" + can.FsmVariables.FindFsmBool("ContainsKilju").Value + ";Alcohol=" + FireTools.GetF(can, "KiljuAlc", 0).ToString("F4", C) + ";Water=" + state.Water.ToString("F3", C));
            }
            if (At(54, t, 100)) Record(channel, "stove-capacity-race", Woods() == 4 && Firewood().Count == 1, "Woods=" + Woods() + ";UnusedLogs=" + Firewood().Count);
            if (At(55, t, 184)) SaveCheck(channel, "native-save-kilju", new[] { brew, heat, Game.FsmOn(products["Juice"][0], "Use") });
            if (At(30, t, 205) && Session.IsHost) { Mark(brew.gameObject); Game.SetState(fill, "Empty bucket"); }
            if (At(31, t, 215))
            { BrewState state = MscBrewing.Capture(); Record(channel, "bucket-empty", state.Water == 0 && state.Sugar == 0 && state.Yeast == 0 && !state.Finished, "Water=" + state.Water + ";Sugar=" + state.Sugar + ";Finished=" + state.Finished); }
            if (At(32, t, 114) && !Session.IsHost)
            {
                Near(grill.transform.position); GameObject package = products["Sausages"][0]; Mark(package); package.transform.position = grill.transform.position + Vector3.up * .2f;
                grill.FsmVariables.FindFsmGameObject("Package").Value = package; Game.SetState(grill, "State 4");
            }
            if (At(33, t, 120)) Record(channel, "grill-opened", Sausages().Count == 4, "Sausages=" + Sausages().Count);
            if (At(34, t, 122) && !Session.IsHost) Cook("Grilled");
            if (At(35, t, 128)) RecordFood(channel, "grill-cooked", "Grilled");
            if (At(36, t, 130) && !Session.IsHost) Cook("Burnt");
            if (At(37, t, 136)) RecordFood(channel, "grill-burnt", "Burnt");
            if (At(38, t, 142)) Record(channel, "wood-cycle", Woods() == 3 && VisibleLogs() == 3, "Woods=" + Woods() + ";Visible=" + VisibleLogs());
            if (At(39, t, 146) && Session.IsHost)
            {
                Near(pan.transform.position); FireTools.SetF(pan, "Water", 1.5f); FireTools.SetF(pan, "Ground", 5); FireTools.SetF(pan, "Coffee", .1f); Game.SetState(pan, "Cooking");
            }
            if (At(40, t, 152))
            {
                float coffee = FireTools.GetF(pan, "Coffee", 0), ground = FireTools.GetF(pan, "Ground", 0);
                Record(channel, "coffee-brewed", coffee > .2f && ground < 4, "Coffee=" + coffee.ToString("F3", C) + ";Ground=" + ground.ToString("F3", C) + ";Caffeine=" + FireTools.GetF(pan, "Caffeine", 0).ToString("F3", C));
            }
            if (At(41, t, 156) && Session.IsHost) Game.SetState(pan, "Idle");
            if (At(42, t, 158) && !Session.IsHost)
            {
                Near(cup.transform.position); Hold(cup.gameObject);
                Transform cupTarget = cup.transform.Find("PourTarget"), panTarget = pan.transform.Find("PanTarget");
                Rigidbody body = cup.GetComponent<Rigidbody>(); if (body != null) body.isKinematic = true;
                cup.transform.position += panTarget.position - cupTarget.position; Game.SetState(cup, "State 5");
            }
            if (At(43, t, 164) && !Session.IsHost) Game.SetState(cup, "Reset pan");
            if (At(44, t, 166)) Record(channel, "coffee-poured", FireTools.GetF(cup, "Coffee", 0) > .1f && FireTools.GetF(cup, "Caffeine", 0) > .1f, "Coffee=" + FireTools.GetF(cup, "Coffee", 0).ToString("F3", C) + ";Caffeine=" + FireTools.GetF(cup, "Caffeine", 0).ToString("F3", C));
            if (At(45, t, 168) && !Session.IsHost) Game.SetState(cup, "Play anim");
            if (At(46, t, 177)) Record(channel, "coffee-drunk", FireTools.GetF(cup, "Coffee", 1) < .001f, "Coffee=" + FireTools.GetF(cup, "Coffee", 0).ToString("F4", C));
            if (At(47, t, 180) && !Session.IsHost) Hold(null);
            if (At(56, t, 220)) SaveCheck(channel, "native-save-coffee", new[] { pan, cup });
        }
        static Behaviour SteamBlur()
        {
            foreach (FsmStateAction action in steam.Fsm.GetState("Idle").Actions)
            {
                object property = NativeActionReader.Field(action, "targetProperty");
                var target = NativeActionReader.Field(property, "TargetObject") as FsmObject;
                var behaviour = target != null ? target.Value as Behaviour : null;
                if (behaviour != null && behaviour.name == "FPSCamera") return behaviour;
            }
            return null;
        }
        static bool SteamVisible()
        {
            foreach (FsmStateAction action in steam.Fsm.GetState("Steam").Actions)
            {
                object property = NativeActionReader.Field(action, "targetProperty");
                var target = NativeActionReader.Field(property, "TargetObject") as FsmObject;
                var emitter = target != null ? target.Value as ParticleEmitter : null;
                if (emitter != null && emitter.name == "Steam" && emitter.emit) return true;
            }
            return false;
        }
        static void SaveCheck(string channel, string name, PlayMakerFSM[] fsms)
        {
            MscNativeSave.Capture(); int values = 0, mismatches = 0;
            var tags = new HashSet<string>();
            foreach (PlayMakerFSM f in fsms)
                foreach (FsmState state in f.Fsm.States)
                    foreach (FsmStateAction action in state.Actions)
                    {
                        if (action == null || (action.GetType().Name != "SaveFloat" && action.GetType().Name != "SaveBool")) continue;
                        var file = NativeActionReader.Field(action, "saveFile") as FsmString; var tag = NativeActionReader.Field(action, "uniqueTag") as FsmString;
                        if (file == null || tag == null) continue;
                        string path = Path.Combine(MscProfile.SaveDirectory, file.Value) + "?tag=" + tag.Value;
                        if (!tags.Add(path)) continue;
                        values++;
                        var number = NativeActionReader.Field(action, "saveValue") as FsmFloat;
                        var flag = NativeActionReader.Field(action, "saveValue") as FsmBool;
                        if (number != null && Mathf.Abs(ES2.Load<float>(path) - number.Value) > .0001f || flag != null && ES2.Load<bool>(path) != flag.Value) mismatches++;
                    }
            Record(channel, name, values >= (name.EndsWith("kilju", StringComparison.Ordinal) ? 15 : 4) && mismatches == 0, "Values=" + values + ";Mismatches=" + mismatches);
        }
        static void RecordFood(string channel, string name, string state)
        {
            List<PlayMakerFSM> food = Sausages(); int correct = 0;
            foreach (PlayMakerFSM use in food) { PlayMakerFSM fireFsm = Game.FsmOn(use.gameObject, "Fire"); if (fireFsm != null && fireFsm.ActiveStateName == state) correct++; }
            Record(channel, name, food.Count == 4 && correct == 4, "Sausages=" + food.Count + ";Correct=" + correct);
        }
    }
}
