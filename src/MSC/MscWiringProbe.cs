using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HutongGames.PlayMaker;
using MWCoop.Net;
using SummerCoop.Game;
using UnityEngine;

namespace MWCoop
{
    // Uses both native connector endpoints, then checks database flags, visible
    // wires and the actual ES2 save. No packets or Installed flags are fabricated.
    public static class MscWiringProbe
    {
        public static readonly List<string> Results = new List<string>();
        static readonly List<PlayMakerFSM> connections = new List<PlayMakerFSM>();
        static readonly List<PlayMakerFSM> starterBolts = new List<PlayMakerFSM>(), terminalBolts = new List<PlayMakerFSM>();
        static readonly SortedDictionary<string, PlayMakerFSM> databases = new SortedDictionary<string, PlayMakerFSM>(StringComparer.Ordinal);
        static bool prepared, saved, recorded, hover;
        static int current = -1, connected, terminalStep;
        static float nextConnect, nextElectrical;
        static PlayMakerFSM secondClick;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        public static void Update()
        {
            string channel = MscRuntime.Argument("--summercoop-test-workshop-channel");
            if (channel.Length == 0) return;
            string clock = Path.Combine(channel, "start.txt");
            long ticks;
            if (!File.Exists(clock) || !long.TryParse(File.ReadAllText(clock), out ticks)) return;
            float elapsed = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ticks).TotalSeconds;
            if (elapsed < 89) return;
            if (!prepared)
            {
                prepared = true;
                SortedDictionary<string, PlayMakerFSM> ends = new SortedDictionary<string, PlayMakerFSM>(StringComparer.Ordinal);
                foreach (UnityEngine.Object item in Game.AllFsms())
                {
                    PlayMakerFSM f = item as PlayMakerFSM;
                    if (f == null || f.hideFlags != HideFlags.None) continue;
                    string path = Recon.Path(f.transform);
                    if (f.FsmName == "Status" && path.IndexOf("/Wiring", StringComparison.Ordinal) >= 0) f.enabled = false; // QA fixture exposes connectors of unmounted parts.
                    if (MscParts.IsBolt(f) && path.Contains("starter(Clone)/Bolts/")) starterBolts.Add(f);
                    if (MscParts.IsBolt(f) && path.Contains("/Wiring/Parts/")) terminalBolts.Add(f);
                    if (f.FsmName == "Data" && path.StartsWith("Database/DatabaseWiring/", StringComparison.Ordinal)) databases[path] = f;
                    if (f.FsmName != "Assemble" || path.IndexOf("/Wiring/Triggers/", StringComparison.Ordinal) < 0) continue;
                    for (Transform p = f.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                    f.enabled = true;
                    FsmGameObject db = f.FsmVariables.FindFsmGameObject("db_ThisPart");
                    if (db != null && db.Value != null && !ends.ContainsKey(db.Value.name)) ends[db.Value.name] = f;
                }
                foreach (PlayMakerFSM f in ends.Values) connections.Add(f);
                File.WriteAllText(Path.Combine(channel, Session.IsHost ? "host-wiring-ready.txt" : "guest-wiring-ready.txt"), "connections=" + connections.Count + ";databases=" + databases.Count);
            }
            if (elapsed < 100 && Session.IsHost && Time.realtimeSinceStartup >= nextElectrical)
            {
                nextElectrical = Time.realtimeSinceStartup + .3f;
                foreach (PlayMakerFSM f in starterBolts) if (f.FsmVariables.FindFsmInt("Stage").Value < 7) Game.SetState(f, "Screw 2");
            }
            if (elapsed >= 241 && Session.IsHost && !saved)
            {
                saved = true;
                CheckSave();
            }
            if (elapsed < 101 || elapsed >= 145) return;
            int stage = (int)((elapsed - 101) / 22);
            float phase = elapsed - 101 - stage * 22;
            bool actor = (stage == 0) != Session.IsHost;
            if (stage != current) { current = stage; connected = terminalStep = 0; recorded = false; nextConnect = 0; secondClick = null; }
            int half = (connections.Count + 1) / 2;
            int begin = stage == 0 ? 0 : half, count = stage == 0 ? half : connections.Count - half;
            if (stage == 0 && !hover && phase >= 1)
            {
                hover = true;
                if (actor && connections.Count > 0) Game.SetState(connections[0], "Assemble");
                int before = 0; foreach (KeyValuePair<string, bool> pair in Snapshot()) if (pair.Key.EndsWith("/Installed", StringComparison.Ordinal) && pair.Value) before++;
                Results.Add("WiringHover=Installed=" + before);
            }
            if (actor && phase >= 2 && connected < count && Time.realtimeSinceStartup >= nextConnect)
            {
                if (secondClick != null)
                {
                    Game.SetState(secondClick, "Sound");
                    Log.Info("MSC wire second click " + Recon.Path(secondClick.transform) + " -> " + secondClick.ActiveStateName);
                    secondClick = null; connected++; nextConnect = Time.realtimeSinceStartup + .2f; return;
                }
                nextConnect = Time.realtimeSinceStartup + .12f;
                PlayMakerFSM first = connections[begin + connected];
                FsmGameObject other = first.FsmVariables.FindFsmGameObject("_OtherEnd");
                PlayMakerFSM second = other != null && other.Value != null ? Game.FsmOn(other.Value, "Assemble") : null;
                if (second == null) { Results.Add("WiringProblem=missing second endpoint: " + Recon.Path(first.transform)); return; }
                for (Transform p = first.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                for (Transform p = second.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                // Same two clicks as wiring mess: each endpoint waits in Sound;
                // CLOSELOOP from the other end completes the native connection.
                Game.SetState(first, "Sound"); secondClick = second;
            }
            if (actor && phase >= 9 && terminalStep < 8 && Time.realtimeSinceStartup >= nextElectrical)
            {
                nextElectrical = Time.realtimeSinceStartup + .3f; terminalStep++;
                foreach (PlayMakerFSM f in starterBolts) if (f.FsmVariables.FindFsmInt("Stage").Value < 8) Game.SetState(f, "Screw 2");
                foreach (PlayMakerFSM f in terminalBolts)
                {
                    if (!TerminalInstalled(f)) continue;
                    for (Transform p = f.transform; p != null; p = p.parent) p.gameObject.SetActive(true);
                    if (f.FsmVariables.FindFsmInt("Stage").Value < 8) Game.SetState(f, "Screw 2");
                }
            }
            string state = Path.Combine(channel, "wiring-state-" + stage + ".txt");
            if (!recorded && phase >= (actor ? 15 : 19) && (actor || File.Exists(state)))
            {
                recorded = true;
                SortedDictionary<string, bool> snapshot = Snapshot(); List<string> rows = new List<string>();
                int installed = 0;
                foreach (KeyValuePair<string, bool> pair in snapshot) { rows.Add(pair.Key + "|" + pair.Value); if (pair.Key.EndsWith("/Installed", StringComparison.Ordinal) && pair.Value) installed++; }
                File.WriteAllLines(Path.Combine(channel, (Session.IsHost ? "host" : "guest") + "-wiring-state-" + stage + ".txt"), rows.ToArray());
                int mismatches = 0;
                if (actor) File.WriteAllLines(state, rows.ToArray());
                else foreach (string row in File.ReadAllLines(state))
                {
                    int at = row.LastIndexOf('|'); string key = row.Substring(0, at); bool actual;
                    if (!snapshot.TryGetValue(key, out actual) || actual != bool.Parse(row.Substring(at + 1))) { mismatches++; Results.Add("WiringMismatch=" + stage + ";" + key); }
                }
                int failed = 0;
                for (int i = begin; i < begin + count; i++)
                    foreach (FsmGameObject db in connections[i].FsmVariables.GameObjectVariables)
                    {
                        if (!db.Name.StartsWith("db_ThisPart", StringComparison.Ordinal) || db.Value == null) continue;
                        PlayMakerFSM data = Game.FsmOn(db.Value, "Data");
                        FsmBool flag = data != null ? data.FsmVariables.FindFsmBool("Installed") : null;
                        if (flag == null || !flag.Value) { failed++; Results.Add("WiringFailed=" + stage + ";" + db.Value.name); }
                    }
                int loose = 0;
                foreach (PlayMakerFSM f in terminalBolts) if (TerminalInstalled(f) && f.FsmVariables.FindFsmInt("Stage").Value != 8) loose++;
                string result = "Wiring=" + stage + ";Role=" + (actor ? "actor" : "observer") + ";Connections=" + count + ";Installed=" + installed + ";Databases=" + databases.Count + ";Fields=" + snapshot.Count + ";Mismatches=" + mismatches + ";Failed=" + failed + ";TerminalBolts=" + terminalBolts.Count + ";LooseTerminals=" + loose;
                Results.Add(result); Log.Info(result);
            }
        }
        static SortedDictionary<string, bool> Snapshot()
        {
            SortedDictionary<string, bool> values = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, PlayMakerFSM> pair in databases)
            {
                FsmBool installed = pair.Value.FsmVariables.FindFsmBool("Installed");
                if (installed != null) values[pair.Key + "/Installed"] = installed.Value;
                FsmBool bolted = pair.Value.FsmVariables.FindFsmBool("Bolted");
                if (bolted != null) values[pair.Key + "/Bolted"] = bolted.Value;
                FsmGameObject wire = pair.Value.FsmVariables.FindFsmGameObject("ActivateThis");
                if (wire != null && wire.Value != null) values[pair.Key + "/Visible"] = wire.Value.activeSelf;
            }
            return values;
        }
        static bool TerminalInstalled(PlayMakerFSM f)
        {
            for (Transform p = f.transform; p != null; p = p.parent)
            {
                PlayMakerFSM check = Game.FsmOn(p.gameObject, "BoltCheck");
                FsmGameObject db = check != null ? check.FsmVariables.FindFsmGameObject("db_ThisPart") : null;
                PlayMakerFSM data = db != null && db.Value != null ? Game.FsmOn(db.Value, "Data") : null;
                FsmBool installed = data != null ? data.FsmVariables.FindFsmBool("Installed") : null;
                if (installed != null) return installed.Value;
            }
            return false;
        }
        static void CheckSave()
        {
            MscNativeSave.Capture();
            int floats = 0, wires = 0, beltFlags = 0, mismatches = 0;
            foreach (UnityEngine.Object item in Game.AllFsms())
            {
                PlayMakerFSM f = item as PlayMakerFSM;
                if (f == null || !f.Fsm.Initialized || f.hideFlags != HideFlags.None) continue;
                bool wiring = Recon.Path(f.transform).StartsWith("Database/DatabaseWiring/", StringComparison.Ordinal);
                bool tuning = f.FsmName == "Data" && (f.FsmVariables.FindFsmFloat("IdleAdjust") != null || f.FsmVariables.FindFsmFloat("cyl1exhaust") != null
                    || f.FsmVariables.FindFsmFloat("SparkAngle") != null || f.FsmVariables.FindFsmFloat("Alignment") != null || f.FsmVariables.FindFsmFloat("SettingBump") != null);
                bool rotation = f.FsmName == "BoltCheck" && f.FsmVariables.FindFsmFloat("Rotation") != null;
                bool belt = f.FsmName == "Use" && MscBelt.IsBelt(f.gameObject);
                if (!(wiring && f.FsmName == "Data") && !tuning && !rotation && !belt) continue;
                foreach (FsmState state in f.FsmStates) foreach (FsmStateAction action in state.Actions)
                {
                    if (action == null) continue;
                    string name = action.GetType().Name;
                    if (name != "SaveFloat" && !((wiring || belt) && name == "SaveBool")) continue;
                    FsmString file = NativeActionReader.Field(action, "saveFile") as FsmString;
                    FsmString tag = NativeActionReader.Field(action, "uniqueTag") as FsmString;
                    if (file == null || tag == null) continue;
                    string path = Path.Combine(MscProfile.SaveDirectory, file.Value) + "?tag=" + tag.Value;
                    if (name == "SaveFloat")
                    {
                        FsmFloat expected = NativeActionReader.Field(action, "saveValue") as FsmFloat;
                        if (expected == null) continue;
                        floats++; if (Mathf.Abs(ES2.Load<float>(path) - expected.Value) > .0001f) mismatches++;
                    }
                    else
                    {
                        FsmBool expected = NativeActionReader.Field(action, "saveValue") as FsmBool;
                        if (expected == null) continue;
                        if (belt) beltFlags++; else wires++;
                        if (ES2.Load<bool>(path) != expected.Value) mismatches++;
                    }
                }
            }
            string result = "WorkshopSave=Floats=" + floats + ";WireFlags=" + wires + ";BeltFlags=" + beltFlags + ";Mismatches=" + mismatches;
            Results.Add(result); Log.Info(result);
        }
    }
}
