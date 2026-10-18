using System.Collections.Generic;
using System.IO;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Native MSC personal items only: helmet and coffee cup. Winter clothing
    // and its PLAYER/Clothing controller are not part of this runtime.
    public static class Wear
    {
        const int NBase = 2, PerSet = 2;
        static readonly string[] Paths = { "ITEMS/helmet(itemx)", "ITEMS/coffee cup(itemx)" };
        static readonly string[] Kinds = { "helmet", "cup" };
        static readonly string[] CopyNames = { "helmet", "coffee cup" };
        static readonly int[] Bits = { PlayerSync.F_Helmet, 0 };
        const string HelmetPath = "Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Helmet";

        class Item
        {
            public GameObject Go; public int Kind; public string Name, Key;
            public bool LocalWorn, Hidden, HidActive;
            public Transform HidParent; public Vector3 HidPos; public Quaternion HidRot; public float HidAt;
            public int WornBy = -1; public float WornAt;                          // porte par un autre (message fiable, redit toutes les 5 s)
            public int OffWho = -1; public Vector3 OffPos; public Quaternion OffRot; public float OffAt = -1;  // pose ou un autre l'a enleve
            public int LastWearer = -1;                                           // dernier porteur vu
            public float YieldAt = -100, HeldLog = -100, SentAt;
        }

        // Helmet and cup, followed by one helmet/cup pair per extra player.
        static readonly List<Item> items = new List<Item>();
        static int pairs = 1;
        static float loadedAt;   // (copies faites 12 s apres le chargement : casque et tasse trouves d'ici la)
        static PlayMakerFSM helmetLogic;
        static Transform player;   // PLAYER (par reference : pas de nom lu 12 fois/s), garde une fois trouve
        static float nextPoll, findAt = -1, nextList, nextSave;
        static bool found, fileRead;
        static readonly List<Vector3[]> filePoses = new List<Vector3[]>();   // (hote) poses des copies lues du fichier

        static Wear() { Reset(); }

        static void Reset()
        {
            items.Clear();
            for (int i = 0; i < NBase; i++) items.Add(new Item { Kind = i, Name = Kinds[i], Key = "msc:personal:" + Kinds[i] });
            pairs = 1;
        }

        public static void OnLevelLoaded()
        {
            Reset();
            helmetLogic = null;
            helmetHooked.Clear(); cupSeen.Clear(); cupResend = 0f;
            player = null;
            found = fileRead = false;
            filePoses.Clear();
            nextList = nextSave = 0f;
            // Tout de suite (avant que le jeu ne range sous PLAYER un vetement porte a la sauvegarde), puis 5 s plus
            // tard pour ce qui manquerait.
            loadedAt = Time.realtimeSinceStartup;
            if (PlayerSync.InGame) { Find(); findAt = Time.realtimeSinceStartup + 5f; }
            else findAt = -1;
        }

        static void Find()
        {
            if (player == null) player = Game.PlayerT;
            if (helmetLogic == null && player != null) {
                Transform h = player.Find(HelmetPath);
                if (h != null) helmetLogic = Game.FsmOn(h.gameObject, "Logic");
            }
            for (int i = 0; i < NBase; i++) if (items[i].Go == null) items[i].Go = Game.FindAny(Paths[i]);
            if (items[0].Go == null && helmetLogic != null) items[0].Go = GoVar(helmetLogic, "Helmet");
            found = player != null && items[0].Go != null && items[1].Go != null;
        }

        // Casque mis (son 'Use', etat "State 3", juste avant EQUIP) : la logique du joueur prend CE casque-la (copie ou
        // celui du jeu), elle range, montre et rend l'objet de sa variable {Helmet}.
        class HelmetHook : ModHook
        {
            public override string Module { get { return "vetements (casque)"; } }
            public GameObject Go;
            public override void OnEnter()
            {
                try
                {
                    if (helmetLogic == null && player != null) { Transform h = player.Find(HelmetPath); if (h != null) helmetLogic = Game.FsmOn(h.gameObject, "Logic"); }
                    FsmGameObject v = helmetLogic != null ? helmetLogic.FsmVariables.FindFsmGameObject("Helmet") : null;
                    if (v != null && Go != null) v.Value = Go;
                }
                catch (System.Exception e) { Replay.HookError(e); }
                Finish();
            }
        }
        static readonly HashSet<GameObject> helmetHooked = new HashSet<GameObject>();
        static void HookHelmets()
        {
            foreach (Item it in items)
            {
                if (it.Kind != 0 || it.Go == null || helmetHooked.Contains(it.Go)) continue;
                PlayMakerFSM use = Game.FsmOn(it.Go, "Use");
                if (use == null || use.Fsm == null) continue;
                FsmState st = use.Fsm.GetState("State 3");
                if (st == null || !st.IsInitialized || st.Actions == null) continue;
                var l = new List<FsmStateAction>(st.Actions); l.Insert(0, new HelmetHook { Go = it.Go }); st.Actions = l.ToArray();
                helmetHooked.Add(it.Go);
            }
        }

        static GameObject GoVar(PlayMakerFSM f, string name)
        {
            FsmGameObject v = f.FsmVariables.FindFsmGameObject(name);
            return v != null ? v.Value : null;
        }

        // Stable personal item keys survive changes of parent between ITEMS, PLAYER and the scene root.
        public static string KeyOf(GameObject go)
        {
            foreach (Item it in items) if (it.Go != null && it.Go == go) return it.Key;
            return null;
        }

        // Native item template for the avatar's helmet copy.
        public static GameObject ItemObject(int i) { return i >= 0 && i < items.Count ? items[i].Go : null; }

        // Bits des genres de vetements portes ici (etat du joueur, pour l'avatar).
        public static int LocalFlags
        {
            get
            {
                int f = 0;
                foreach (Item it in items) if (it.LocalWorn) f |= Bits[it.Kind];
                return f;
            }
        }

        // ---------------------------------------------------------------- copies (une paire par joueur)
        // Point de la paire k (>= 2) dans la maison du debut de partie : couloir, pres de l'armoire (a cote des sacs de
        // couchage) ; posees un peu en l'air, elles tombent au sol.
        static bool SpawnPoint(int kind, int k, out Vector3 at)
        {
            GameObject home = Game.FindAny("YARD/Building/BEDROOM1/LOD_bedroom1/Sleep");
            at = Vector3.zero;
            if (home == null) return false;
            at = home.transform.position + new Vector3(.6f + kind * .35f, .4f, .5f + (k - 2) * .45f);
            RaycastHit hit;
            if (Physics.Raycast(at + Vector3.up, Vector3.down, out hit, 3f, ~(1 << 2))) at = hit.point + Vector3.up * .2f;
            return true;
        }

        static Item MakeCopy(int kind, int k, Vector3 pos, Quaternion rot)
        {
            GameObject src = items[kind].Go;
            if (src == null) return null;
            var go = (GameObject)Object.Instantiate(src, pos, rot);
            go.name = CopyNames[kind] + " " + k + "(itemx)";
            go.transform.parent = null; go.transform.position = pos; go.transform.rotation = rot;
            go.transform.localScale = Vector3.one; go.layer = 19; go.tag = "PART";
            PlayMakerFSM use = Game.FsmOn(go, "Use");
            if (kind == 1 && use != null) {
                foreach (string name in new[] { "UniqueTagCoffee", "UniqueTagCaffeine", "UniqueTagTransform" }) {
                    FsmString tag = use.FsmVariables.FindFsmString(name);
                    if (tag != null) tag.Value += "MSC" + k;
                }
                FsmFloat coffee = use.FsmVariables.FindFsmFloat("Coffee"); if (coffee != null) coffee.Value = .0001f;
            }
            if (kind == 0) { PlayMakerFSM paint = Game.FsmOn(go, "Paint"); if (paint != null) paint.enabled = false; }
            go.SetActive(true);
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = false; rb.velocity = rb.angularVelocity = Vector3.zero; }
            var item = new Item { Go = go, Kind = kind, Name = Kinds[kind] + " " + k, Key = "msc:personal:" + Kinds[kind] + k };
            items.Add(item); Props.SoonScan(); return item;
        }

        // Additional pairs store helmet/cup positions and rotations.
        static bool EnsurePairs(int n, List<Vector3[]> poses, string why)
        {
            if (items[0].Go == null || items[1].Go == null) return false;
            int was = pairs;
            while (pairs < n)
            {
                int k = pairs + 1;
                Vector3[] ps = poses != null && k - 2 < poses.Count ? poses[k - 2] : null;
                for (int kind = 0; kind < PerSet; kind++)
                {
                    Vector3 at; Quaternion q = Quaternion.identity;
                    if (ps != null && kind * 2 + 1 < ps.Length) { at = ps[kind * 2]; q = Quaternion.Euler(ps[kind * 2 + 1]); }
                    else if (!SpawnPoint(kind, k, out at)) { Transform pl = Game.PlayerT; if (pl == null) return false; at = pl.position + pl.forward * 1.2f + pl.right * (kind - 0.5f) * 0.6f + Vector3.up * 0.3f; }
                    if (MakeCopy(kind, k, at, q) == null) return false;
                }
                pairs = k;
            }
            if (pairs > was) Log.Info("vetements : " + pairs + " helmet/cup sets (" + why + ")");
            return true;
        }

        static string FilePath { get { return Path.Combine(MscProfile.SaveDirectory, "MSC-personal-items.txt"); } }

        static void LoadFile()
        {
            WorldFiles.Check(Path.GetDirectoryName(FilePath));
            fileRead = true;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string l in File.ReadAllLines(FilePath))
                {
                    string[] c = l.Split(';');
                    if (c.Length < 12) continue;
                    var v = new Vector3[System.Math.Min(c.Length / 3, PerSet * 2)];
                    for (int j = 0; j < v.Length; j++) v[j] = new Vector3(float.Parse(c[j * 3], ci), float.Parse(c[j * 3 + 1], ci), float.Parse(c[j * 3 + 2], ci));
                    filePoses.Add(v);
                }
            }
            catch (System.Exception e) { Log.Warn("vetements : lecture " + e.Message); }
        }

        // Pose gardee d'une copie : la ou elle est, sauf portee ou cachee (porte par quelqu'un) : son point de la maison.
        static void PoseOf(Item it, int k, out Vector3 p, out Vector3 e)
        {
            Transform t = it.Go != null ? it.Go.transform : null;
            bool away = t == null || it.LocalWorn || it.Hidden || !it.Go.activeInHierarchy || (player != null && t.IsChildOf(player));
            if (!away) { p = t.position; e = t.rotation.eulerAngles; return; }
            e = Vector3.zero;
            if (!SpawnPoint(it.Kind, k, out p)) p = t != null ? t.position : Vector3.zero;
        }

        static void SaveFile()
        {
            if (!Session.IsHost || pairs < 2) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            for (int k = 2; k <= pairs; k++)
            {
                for (int kind = 0; kind < PerSet; kind++)
                {
                    Vector3 p, e;
                    PoseOf(items[NBase + (k - 2) * PerSet + kind], k, out p, out e);
                    foreach (float f in new[] { p.x, p.y, p.z, e.x, e.y, e.z }) sb.Append(f.ToString("R", ci)).Append(';');
                }
                sb.Length--; sb.Append('\n');
            }
            try { File.WriteAllText(FilePath, sb.ToString()); } catch (System.Exception ex) { Log.Warn("vetements : ecriture " + ex.Message); }
        }

        public static void OnGameSave() { SaveFile(); }

        static void SendList()
        {
            if (Session.RemoteCount == 0 || pairs < 2) return;
            var w = new NetWriter(Msg.Job).U8(Session.LocalId).Str("@vet").U8(pairs);
            for (int k = 2; k <= pairs; k++)
                for (int kind = 0; kind < PerSet; kind++)
                {
                    Item it = items[NBase + (k - 2) * PerSet + kind];
                    Vector3 p, e;
                    PoseOf(it, k, out p, out e);
                    w.Vec(p).Vec(e);
                }
            Session.SendAll(w, true);
        }

        // Invite : liste de l'hote (@vet) -- les copies qui manquent ici, a la pose de l'hote.
        public static void OnList(NetReader r)
        {
            int n = r.U8();
            if (n < 1 || n > 8) throw new System.IO.IOException("Invalid personal item count");
            var poses = new List<Vector3[]>();
            for (int k = 2; k <= n; k++) { var ps = new Vector3[PerSet * 2]; for (int j = 0; j < ps.Length; j++) ps[j] = r.Vec(); poses.Add(ps); }
            if (Session.IsHost || n <= pairs) return;
            if (!EnsurePairs(n, poses, "liste de l'hote")) Log.Info("vetements : liste de l'hote (" + n + " sets) waiting for native helmet/cup");
        }

        // ---------------------------------------------------------------- boucle
        // Cafe des tasses (suggestion de Crowbangus, 12/10 : « je verse du cafe dans la tasse et la tends a l'autre : il ne le
        // voit pas »). Chaque tasse (celle du jeu et les copies par joueur, meme rang partout) garde son cafe dans 'Use' (Coffee,
        // Caffeine ; Pour le remplit depuis la casserole, Play anim le vide quand on boit). Celui chez qui il change de plus de
        // 0,004 l'envoie (@tasse, 4 fois/s au plus pendant qu'on verse) ; les autres le posent et repassent par l'etat Data du
        // jeu, qui recalcule la surface du cafe (hauteur, couleur). L'hote renvoie toutes les tasses non vides toutes les 15 s
        // (arrivants).
        static readonly Dictionary<int, float> cupSeen = new Dictionary<int, float>();
        static float cupResend;
        static int cupLogs;
        static PlayMakerFSM CupFsm(int i, out FsmFloat coffee, out FsmFloat caf)
        {
            coffee = caf = null;
            if (i < 0 || i >= items.Count || items[i].Kind != 1 || items[i].Go == null) return null;
            PlayMakerFSM f = Game.FsmOn(items[i].Go, "Use");
            if (f == null) return null;
            coffee = f.FsmVariables.FindFsmFloat("Coffee");
            caf = f.FsmVariables.FindFsmFloat("Caffeine");
            return coffee != null ? f : null;
        }
        static void CupTick(float now)
        {
            if (!Session.Active) return;
            bool resend = Session.IsHost && now >= cupResend;
            if (resend) cupResend = now + 15f;
            for (int i = 0; i < items.Count; i++)
            {
                FsmFloat c, k;
                if (CupFsm(i, out c, out k) == null) continue;
                float last;
                if (!cupSeen.TryGetValue(i, out last)) { cupSeen[i] = c.Value; continue; }
                bool changed = Mathf.Abs(c.Value - last) >= 0.004f;
                if (!changed && !(resend && c.Value > 0.01f)) continue;
                cupSeen[i] = c.Value;
                Session.SendAll(new NetWriter(Msg.Job).U8(Session.LocalId).Str("@tasse").U8(i).F32(c.Value).F32(k != null ? k.Value : 0f), true);
                if (changed && cupLogs++ < 10) Log.Info("tasse " + items[i].Name + " : cafe " + c.Value.ToString("F3") + " envoye");
            }
        }
        public static void OnCup(int who, NetReader r)
        {
            int i = r.U8();
            float coffee = r.F32(), caf = r.F32();
            FsmFloat c, k;
            PlayMakerFSM f = CupFsm(i, out c, out k);
            if (f == null) return;
            if (Mathf.Abs(c.Value - coffee) < 0.0005f) { cupSeen[i] = c.Value; return; }
            string st = f.ActiveStateName;
            if (st == "Pour" || st == "Play anim") return;   // (on verse ou on boit ici : notre valeur partira)
            c.Value = coffee;
            if (k != null) k.Value = caf;
            cupSeen[i] = coffee;
            if (f.enabled && f.gameObject.activeInHierarchy) Game.SetState(f, "Data");   // (surface du cafe : hauteur et couleur)
            if (cupLogs++ < 10) Log.Info("tasse " + items[i].Name + " : cafe " + coffee.ToString("F3") + " recu de #" + who);
        }

        public static void Update()
        {
            if (findAt < 0 || !PlayerSync.InGame) return;
            float now = Time.realtimeSinceStartup;
            if (!found && now >= findAt) { findAt = now + 10f; Find(); }
            if (now < nextPoll) return;
            nextPoll = now + 0.25f;
            if (found && player == null) { found = false; findAt = now; }   // PLAYER detruit : recherche a la prochaine image
            HookHelmets();
            CupTick(now);
            if (Session.Active && Session.IsHost && items[0].Go != null && items[1].Go != null && now - loadedAt > 12f)
            {
                if (!fileRead) LoadFile();
                int want = Mathf.Max(Session.RemoteCount + 1, filePoses.Count + 1);
                if (want > pairs && EnsurePairs(want, filePoses, filePoses.Count + 1 >= want ? "reprises de la sauvegarde" : "une par joueur")) { SaveFile(); nextList = 0f; }
                if (now >= nextList) { nextList = now + 10f; SendList(); }
                if (now >= nextSave) { nextSave = now + 60f; SaveFile(); }
            }
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                GameObject go = it.Go;
                if (go == null) continue;
                Transform t = go.transform;
                // Sous PLAYER (pas sa racine : au volant ou passager, la racine est la voiture).
                bool worn = it.Kind == 0 && !go.activeSelf && player != null && t.IsChildOf(player);
                if (worn != it.LocalWorn)
                {
                    it.LocalWorn = worn;
                    if (worn) it.Hidden = false;   // (cache ici, puis mis par ce joueur-ci : impossible, mais propre)
                    if (Session.Active) { SendWear(i, worn, t); it.SentAt = now; }
                    Log.Info("vetements : " + it.Name + (worn ? " mis(e)" : " enleve(e) en " + t.position.ToString("F1")));
                }
                else if (worn && Session.Active && now - it.SentAt > 5f) { SendWear(i, true, t); it.SentAt = now; }   // (redit : arrivee, message perdu)
                if (!Session.Active) continue;
                int wearer = RemoteWearer(it, now);
                if (worn)
                {
                    if (wearer >= 0 && wearer < Session.LocalId) Yield(it, wearer, now);
                    continue;
                }
                if (wearer >= 0)
                {
                    it.LastWearer = wearer;
                    if (!it.Hidden) Hide(it, wearer, now);
                }
                else if (it.Hidden)
                {
                    // Plus porte : a l'endroit ou celui qui le portait l'a enleve (son message fiable), sinon a sa place d'avant.
                    bool off = it.OffWho == it.LastWearer && it.OffAt >= it.HidAt;
                    Unhide(it, off);
                }
            }
        }

        static void SendWear(int i, bool worn, Transform t)
        {
            Session.SendAll(new NetWriter(Msg.Wear).U8(Session.LocalId).U8(i).Bool(worn).Vec(t.position).Quat(t.rotation), true);
        }

        // Joueur distant qui porte le vetement, -1 : personne (message de moins de 15 s, joueur toujours la).
        static int RemoteWearer(Item it, float now)
        {
            if (it.WornBy < 0 || it.WornBy == Session.LocalId) return -1;
            if (!Session.Players.ContainsKey(it.WornBy) || now - it.WornAt > 15f) { it.WornBy = -1; return -1; }
            return it.WornBy;
        }

        static void Hide(Item it, int wearer, float now)
        {
            Transform t = it.Go.transform;
            if (player != null && t.IsChildOf(player))
            {
                // Tenu en main (ou porte) ici, meme assis dans une voiture : on attend qu'il soit lache.
                if (now - it.HeldLog > 10f) { it.HeldLog = now; Log.Info("vetements : " + it.Name + " porte(e) par #" + wearer + " mais tenu(e) ici"); }
                return;
            }
            Props.Release(it.Go);
            it.HidParent = t.parent; it.HidPos = t.position; it.HidRot = t.rotation; it.HidAt = now; it.HidActive = it.Go.activeSelf;
            it.Go.SetActive(false);
            it.Hidden = true;
            Log.Info("vetements : " + it.Name + " porte(e) par #" + wearer + ", cache(e) ici");
        }

        static void Unhide(Item it, bool off)
        {
            Transform t = it.Go.transform;
            if (off) { t.parent = null; t.position = it.OffPos; t.rotation = it.OffRot; }
            else { t.parent = it.HidParent; t.position = it.HidPos; t.rotation = it.HidRot; }
            it.Go.SetActive(off || it.HidActive);
            Rigidbody rb = it.Go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.WakeUp(); }
            it.Hidden = false;
            Props.SoonScan();
            Log.Info("vetements : " + it.Name + (off ? " enleve(e) par l'autre, reapparait en " : " plus porte(e), reapparait a sa place en ") + t.position.ToString("F1"));
        }

        // Porte ici ET par un joueur de plus petit numero : on l'enleve (etat stable de l'automate du jeu seulement,
        // pas en pleine animation), comme le joueur en regardant en bas ; il sera ensuite cache ici.
        static void Yield(Item it, int wearer, float now)
        {
            if (it.Kind != 0 || now - it.YieldAt < 5f || helmetLogic == null) return;
            string state = helmetLogic.ActiveStateName;
            if (state != "State 1" && state != "Helmet ON" || GoVar(helmetLogic, "Helmet") != it.Go) return;
            it.YieldAt = now; Game.SetState(helmetLogic, "Anim off1");
        }



        public static void OnMessage(Peer from, NetReader r)
        {
            int who = r.U8();
            if (Session.IsHost) who = from.Id;
            int i = r.U8();
            bool worn = r.Bool();
            Vector3 pos = r.Vec();
            Quaternion rot = r.Quat();
            if (Session.IsHost) Session.Broadcast(new NetWriter(Msg.Wear).U8(who).U8(i).Bool(worn).Vec(pos).Quat(rot), true, who);
            if (i < 0 || i >= items.Count) return;   // (copie pas encore la : redit dans 5 s)
            Item it = items[i];
            float now = Time.realtimeSinceStartup;
            bool was = it.WornBy == who;
            if (worn) { it.WornBy = who; it.WornAt = now; }
            else
            {
                if (it.WornBy == who) it.WornBy = -1;
                it.OffWho = who; it.OffPos = pos; it.OffRot = rot; it.OffAt = now;
            }
            if (worn != was) Log.Info("vetements de #" + who + " : " + it.Name + (worn ? " mis(e)" : " enleve(e) en " + pos.ToString("F1")));
        }

    }
}
