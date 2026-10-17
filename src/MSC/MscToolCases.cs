using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    public static class MscToolCases
    {
        public const int Snapshot = 17;
        sealed class Case { public int Id, Kind; public GameObject Object; }
        static readonly List<Case> cases = new List<Case>();
        static readonly Dictionary<GameObject, string> ids = new Dictionary<GameObject, string>();
        static readonly GameObject[] templates = new GameObject[2];
        static GameObject templateRoot;
        static Vector3 origin;
        static Quaternion rotation;
        static float nextScan, nextSend;
        static int automaticPlayers, nextId = 1, revision, appliedRevision;
        public static int Count { get { return cases.Count; } }
        public static int IdAt(int index) { return cases[index].Id; }
        public static int KindAt(int index) { return cases[index].Kind; }
        public static string Identity(int id) { return "w:msc:toolcase:" + id; }
        public static string IdOf(GameObject go) { string id; return go != null && ids.TryGetValue(go, out id) ? id : ""; }
        public static int RequiredTotal(int existing, int players) { return Math.Max(existing, Math.Max(1, Math.Min(MscToolCaseSnapshot.Limit, players))); }
        public static bool Ready(PlayerInfo player, float now) { return player.Level == 1 && (player.Local || player.StateTime > 0 && now - player.StateTime <= 15); }
        public static bool CanManage(bool active, bool host, bool inGame) { return active && host && inGame; }
        public static bool Generated(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                string id;
                if (ids.TryGetValue(t.gameObject, out id) && id != "w:msc:toolcase:0") return true;
                if (t.name == "MWCoop-ToolCaseTemplate") return true;
            }
            return false;
        }
        public static void Reset()
        {
            if (templateRoot != null) UnityEngine.Object.Destroy(templateRoot);
            templateRoot = null; templates[0] = templates[1] = null;
            cases.Clear(); ids.Clear(); nextScan = nextSend = 0; automaticPlayers = revision = appliedRevision = 0; nextId = 1;
        }
        static bool FindTemplate(int kind)
        {
            if (templates[kind] != null) return true;
            if (Time.timeSinceLevelLoad < 2) return false;
            GameObject source = null;
            foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(typeof(Transform)))
            {
                Transform t = (Transform)obj;
                if (kind == 0 ? t.name != "spanner set(itemx)" || !t.gameObject.activeInHierarchy
                              : t.name != "ratchet set(Clone)" || t.root.name != "STORE") continue;
                if (t.hideFlags != HideFlags.None || Generated(t) || t.Find("Tools") == null || t.GetComponent<Rigidbody>() == null) continue;
                source = t.gameObject; break;
            }
            if (source == null) return false;
            if (templateRoot == null) { templateRoot = new GameObject("MWCoop-ToolCaseTemplate"); templateRoot.SetActive(false); }
            bool active = source.activeSelf; source.SetActive(false);
            try
            {
                templates[kind] = (GameObject)UnityEngine.Object.Instantiate(source);
                templates[kind].transform.SetParent(templateRoot.transform, false);
            }
            finally { source.SetActive(active); }
            if (kind == 0)
            {
                origin = source.transform.position; rotation = source.transform.rotation;
                cases.Add(new Case { Id = 0, Object = source });
                ids[source] = Identity(0); Props.Adopt(source, Identity(0));
                revision = 1;
                Log.Info("MSC: original tool case registered: " + Recon.Path(source.transform));
            }
            return true;
        }
        static void Create(int id, int kind, Vector3 position, Quaternion rot)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(templates[kind]);
            go.name = (kind == 0 ? "spanner set(itemx)" : "ratchet set(Clone)") + " SummerCoop " + id;
            go.transform.parent = null; go.transform.position = position; go.transform.rotation = rot; go.layer = 19;
            Rigidbody body = go.GetComponent<Rigidbody>();
            body.isKinematic = false; body.velocity = body.angularVelocity = Vector3.zero;
            try
            {
                foreach (PlayMakerFSM f in go.GetComponentsInChildren<PlayMakerFSM>(true))
                {
                    if (!f.Fsm.Initialized) f.Fsm.Init(f);
                    foreach (FsmState state in f.Fsm.States)
                        foreach (FsmStateAction action in state.Actions)
                            if (action != null && (action.GetType().Name.StartsWith("Save", StringComparison.Ordinal)
                                || action.GetType().Name.StartsWith("ES2", StringComparison.Ordinal) || action.GetType().Name == "ArrayListEasySave")) action.Enabled = false;
                }
                foreach (ES2Auto save in go.GetComponentsInChildren<ES2Auto>(true)) save.enabled = false;
            }
            catch { UnityEngine.Object.Destroy(go); throw; }
            cases.Add(new Case { Id = id, Kind = kind, Object = go });
            ids[go] = Identity(id); Props.Adopt(go, Identity(id));
            go.SetActive(true); body.WakeUp(); WorldFsms.SoonScan();
            Log.Info("MSC: tool case " + id + " kind=" + kind + " spawned (total " + cases.Count + ")");
        }
        static string ManageError()
        {
            return CanManage(Session.Active, Session.IsHost, PlayerSync.InGame) ? null : "Создавать и удалять ящики может только хост в игре";
        }
        public static string Spawn(int kind)
        {
            string error = ManageError(); if (error != null) return error;
            if (kind < 0 || kind > 1) return "Неизвестный тип ящика";
            if (cases.Count >= MscToolCaseSnapshot.Limit) return "Достигнут предел: 32 ящика";
            if (!FindTemplate(0) || !FindTemplate(kind)) return "Набор инструментов ещё не загружен";
            Transform camera = PlayerSync.LocalCamera;
            if (camera == null) return "Дождитесь загрузки персонажа";
            Vector3 forward = camera.forward; forward.y = 0;
            if (forward.sqrMagnitude < .01f) forward = Vector3.forward; else forward.Normalize();
            Vector3 position = camera.position + forward * 1.8f - Vector3.up * .6f;
            RaycastHit hit;
            if (Physics.Raycast(position + Vector3.up, Vector3.down, out hit, 4f) && hit.normal.y > .4f) position.y = hit.point.y + .25f;
            Create(nextId++, kind, position, Quaternion.Euler(0, camera.eulerAngles.y, 0));
            revision++; Publish(); return null;
        }
        static void ClearSelectedTool(GameObject box)
        {
            PlayMakerFSM pick = Game.FindFsm("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/2Spanner/Pick", "PickUp");
            if (pick == null) return;
            FsmGameObject old = pick.FsmVariables.FindFsmGameObject("OldTool");
            if (old == null || old.Value == null || !old.Value.transform.IsChildOf(box.transform)) return;
            old.Value = null;
            FsmGameObject picked = pick.FsmVariables.FindFsmGameObject("PickedTool"); if (picked != null) picked.Value = null;
            foreach (string name in new[] { "HandSpanner", "HandSocket", "HandSocketplug", "HandSparkplug", "HandScrewdriver", "Ratchet", "Ruler" })
            {
                FsmGameObject model = pick.FsmVariables.FindFsmGameObject(name); if (model != null && model.Value != null) model.Value.SetActive(false);
            }
            FsmBool ratchet = pick.FsmVariables.FindFsmBool("RatchetInUse"); if (ratchet != null) ratchet.Value = false;
            FsmFloat size = FsmVariables.GlobalVariables.FindFsmFloat("ToolWrenchSize"); if (size != null) size.Value = 0;
        }
        static void DestroyCase(int index)
        {
            Case item = cases[index];
            if (item.Object != null)
            {
                ClearSelectedTool(item.Object); Props.Release(item.Object); Props.Vanish(Identity(item.Id));
                ids.Remove(item.Object); UnityEngine.Object.Destroy(item.Object);
            }
            cases.RemoveAt(index);
            Log.Info("MSC: tool case " + item.Id + " deleted (total " + cases.Count + ")");
        }
        public static string Remove(int id)
        {
            string error = ManageError(); if (error != null) return error;
            if (id == 0) return "Исходный ящик нельзя удалить";
            for (int i = 1; i < cases.Count; i++)
            {
                if (cases[i].Id != id) continue;
                if (Props.InUse(Identity(id))) return "Сначала положите ящик на землю";
                DestroyCase(i); revision++; Publish(); return null;
            }
            return "Ящик уже удалён";
        }
        public static string RemoveAll()
        {
            string error = ManageError(); if (error != null) return error;
            for (int i = 1; i < cases.Count; i++) if (Props.InUse(Identity(cases[i].Id))) return "Сначала положите все ящики на землю";
            for (int i = cases.Count - 1; i > 0; i--) DestroyCase(i);
            revision++; Publish(); return null;
        }
        static void Publish()
        {
            if (cases.Count == 0) return;
            nextSend = Time.realtimeSinceStartup + 2;
            var w = new NetWriter(Msg.Home).U8(0).U8(Snapshot).I32(revision).U8(cases.Count);
            foreach (Case item in cases)
                w.I32(item.Id).U8(item.Kind).Vec(item.Object.transform.position).Quat(item.Object.transform.rotation);
            Session.SendAll(w, true);
        }
        public static void Update()
        {
            if (!Session.Active || !PlayerSync.InGame) return;
            if (templates[0] == null)
            {
                if (Time.realtimeSinceStartup < nextScan) return;
                nextScan = Time.realtimeSinceStartup + 1;
                if (!FindTemplate(0)) return;
            }
            // Capture the inactive shop template before purchase can remove it.
            if (templates[1] == null && Time.realtimeSinceStartup >= nextScan) { nextScan = Time.realtimeSinceStartup + 3; FindTemplate(1); }
            if (!Session.IsHost) return;
            int players = 0, ordinary = 0;
            foreach (PlayerInfo p in Session.Players.Values) if (Ready(p, Time.realtimeSinceStartup)) players++;
            foreach (Case item in cases) if (item.Kind == 0) ordinary++;
            if (players > automaticPlayers)
            {
                while (ordinary < players && cases.Count < MscToolCaseSnapshot.Limit)
                {
                    int slot = ordinary - 1;
                    Create(nextId++, 0, origin + rotation * new Vector3(.7f * (slot % 4 + 1), .15f, .7f * (slot / 4)), rotation);
                    ordinary++; revision++;
                }
                automaticPlayers = players; Publish();
            }
            if (Time.realtimeSinceStartup >= nextSend) Publish();
        }
        public static void OnMessage(Peer from, NetReader r)
        {
            if (Session.IsHost || from == null || from.Id != 0 || !Session.Active || !PlayerSync.InGame) return;
            MscToolCaseSnapshot snapshot = MscToolCaseSnapshot.Read(r);
            if (!MscToolCaseSnapshot.AcceptRevision(snapshot.Revision, appliedRevision)) return;
            if (!FindTemplate(0)) return;
            foreach (MscToolCaseSnapshot.Entry entry in snapshot.Entries) if (!FindTemplate(entry.Kind)) return;
            var present = new HashSet<int>();
            foreach (MscToolCaseSnapshot.Entry entry in snapshot.Entries)
            {
                present.Add(entry.Id); bool exists = false;
                foreach (Case item in cases) if (item.Id == entry.Id) { exists = true; if (item.Kind != entry.Kind) throw new InvalidOperationException("Tool case type changed"); break; }
                if (!exists) Create(entry.Id, entry.Kind, entry.Position, entry.Rotation);
            }
            for (int i = cases.Count - 1; i > 0; i--) if (!present.Contains(cases[i].Id)) DestroyCase(i);
            appliedRevision = snapshot.Revision;
        }
    }
}
