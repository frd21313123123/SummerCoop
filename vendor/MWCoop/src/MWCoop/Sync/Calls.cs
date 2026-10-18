using System.Collections.Generic;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;
namespace MWCoop
{
    // MSC incoming landline only. Catalogue orders belong to MscOrders.
    public static class Calls
    {
        const int K_Phone = 3;
        public static void OnLevelLoaded() {
            for (int i = 0; i < phones.Length; i++) phones[i] = null;
            ringCapped = false; ringNext = nextPhone = nextPhoneSend = 0; phoneSig = "";
        }
        public static void Update() {
            if (!PlayerSync.InGame || !Session.Active || Time.realtimeSinceStartup < nextPhone) return;
            nextPhone = Time.realtimeSinceStartup + 1; PhoneStep(Time.realtimeSinceStartup);
        }
        public static void OnMessage(Peer from, NetReader r) {
            int who = r.U8(), kind = r.U8();
            if (!Session.IsHost && from.Id == 0 && kind == K_Phone) OnPhones(r);
        }
        public static string TestEvent(string part, string ev) {
            foreach (Phone p in phones) if (p != null && p.Ring != null && Recon.Path(p.Ring.transform).Contains(part)) { p.Ring.SendEvent(ev); return ev; }
            return null;
        }
        static readonly string[] LogicPaths = { "YARD/Building/LIVINGROOM/Telephone/Logic/PhoneLogic" };
        static readonly string[] LogicNames = { "maison" };
        const float TakeDist = 25f, KeepDist = 60f, WarnDist = 20f;
        class Phone
        {
            public string Name; public GameObject Go; public PlayMakerFSM Ring, Jokes;
            public bool HostRuns = true, RunsHere, LastRuns = true, Fought;
            public float HostSeen = -1, WarnAt;
            public string HostState = "";
        }

        // Sonnerie : MasterAudio, groupe HouseFoley, variation phone_ring, jouee a l'emplacement du telephone par 'Ring'
        // (RingingNEW / RingingOLD). Dans le jeu seul, la logique du telephone s'eteint quand on s'eloigne ; en coop elle
        // tourne pour qui est pres (et chez l'hote toujours), et la sonnerie s'entendait de tres loin (retour d'un
        // joueur, 08/10). Ses sources (variations sous MasterAudio) : son en 3D, baisse lineaire, plus rien au-dela de
        // 35 m. Cherchees une fois (et revues tant qu'aucune n'est trouvee).
        const float RingRange = 35f;
        static bool ringCapped;
        static float ringNext;
        static void CapRing(Phone p)
        {
            if (ringCapped || Time.realtimeSinceStartup < ringNext) return;
            ringNext = Time.realtimeSinceStartup + 20f;
            int n = 0;
            foreach (Object o in Resources.FindObjectsOfTypeAll(typeof(AudioSource)))
            {
                var a = (AudioSource)o;
                if (a.hideFlags != HideFlags.None) continue;
                string nm = a.gameObject.name.ToLowerInvariant(), cn = a.clip != null ? a.clip.name.ToLowerInvariant() : "";
                if (nm.IndexOf("phone_ring") < 0 && cn.IndexOf("phone_ring") < 0) continue;
                a.spatialBlend = 1f;
                a.rolloffMode = AudioRolloffMode.Linear;
                a.minDistance = Mathf.Min(a.minDistance, 2f);
                a.maxDistance = RingRange;
                n++;
            }
            if (n == 0) return;
            ringCapped = true;
            Log.Info("appels : sonnerie des telephones bornee a " + RingRange + " m (" + n + " sources)");
        }
        static readonly Phone[] phones = new Phone[LogicPaths.Length];
        static float nextPhone, nextPhoneSend;
        static string phoneSig = "";

        // Vrai chez un invite qui fait tourner lui-meme la logique de ce telephone (celle de l'hote ne tourne pas) :
        // WorldFsms ne doit ni l'arreter ni la recaler sur les etats de l'hote.
        public static bool RunsHere(PlayMakerFSM f)
        {
            if (Session.IsHost || f == null) return false;
            foreach (Phone p in phones) if (p != null && p.RunsHere && (p.Ring == f || p.Jokes == f)) return true;
            return false;
        }

        static Phone PhoneAt(int i)
        {
            // (objet detruit depuis -- telephone remplace : NullReferenceException a chaque seconde dans le journal de Matilda,
            // 14/10, 1146 fois) : cherche de nouveau
            if (phones[i] != null && phones[i].Go == null) phones[i] = null;
            if (phones[i] != null) return phones[i];
            GameObject go = Game.FindAny(LogicPaths[i]);
            if (go == null) return null;
            phones[i] = new Phone { Name = LogicNames[i], Go = go, Ring = Game.FsmOn(go, "Ring"), Jokes = Game.FsmOn(go, "Jokes") };
            return phones[i];
        }

        static bool Runs(Phone p) { return p.Go != null && p.Go.activeInHierarchy && p.Ring != null && p.Ring.enabled; }

        static float NearestGuest(Vector3 pos, out string name)
        {
            float best = float.MaxValue;
            name = "";
            foreach (PlayerInfo pi in Session.Players.Values)
            {
                if (pi.Local || pi.Level != 1) continue;
                float d = Vector3.Distance(pi.State.Feet, pos);
                if (d < best) { best = d; name = pi.Name; }
            }
            return best;
        }

        static float LocalDistance(Vector3 pos)
        {
            GameObject pl = GameObject.Find("PLAYER");
            return pl != null ? Vector3.Distance(pl.transform.position, pos) : float.MaxValue;
        }

        // Call : U8 joueur, U8 K_Phone, U8 n, (U8 ligne, Bool tourne chez l'hote, Str etat de 'Ring') x n (hote -> invites).
        static void PhoneStep(float now)
        {
            if (Session.IsHost)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < phones.Length; i++)
                {
                    Phone p = PhoneAt(i);
                    if (p == null) continue;
                    CapRing(p);
                    if (p.Go.activeInHierarchy && p.Ring != null && !p.Ring.enabled)
                    {
                        // Objet allume, automate arrete (par une autre logique) : la logique de l'hote tourne quelle que soit la distance.
                        p.Ring.enabled = true;
                        if (p.Jokes != null) p.Jokes.enabled = true;
                        Log.Info("appels : logique du telephone " + p.Name + " arretee chez l'hote alors que son objet est allume : rallumee");
                    }
                    bool runs = Runs(p);
                    if (runs != p.LastRuns)
                    {
                        p.LastRuns = runs;
                        Log.Info("appels : logique du telephone " + p.Name + (runs ? " tourne de nouveau" : " ne tourne plus (objet " + (p.Go.activeInHierarchy ? "allume" : "eteint") + ")") + " chez l'hote");
                    }
                    if (!runs && Session.RemoteCount > 0 && now >= p.WarnAt)
                    {
                        string who;
                        float d = NearestGuest(p.Go.transform.position, out who);
                        if (d < WarnDist) { p.WarnAt = now + 30f; Log.Warn("appels : logique du telephone " + p.Name + " eteinte chez l'hote, " + who + " a " + d.ToString("F0") + " m du telephone (il la fait tourner chez lui)"); }
                    }
                    sb.Append(i).Append(runs ? '1' : '0').Append(p.Ring != null ? p.Ring.ActiveStateName : "").Append('|');
                }
                string sig = sb.ToString();
                if (Session.RemoteCount > 0 && (sig != phoneSig || now >= nextPhoneSend))
                {
                    phoneSig = sig; nextPhoneSend = now + 10f;
                    var w = new NetWriter(Msg.Call).U8(Session.LocalId).U8(K_Phone);
                    int n = 0; foreach (Phone p in phones) if (p != null) n++;
                    w.U8(n);
                    for (int i = 0; i < phones.Length; i++) if (phones[i] != null) w.U8(i).Bool(Runs(phones[i])).Str(phones[i].Ring != null ? phones[i].Ring.ActiveStateName ?? "" : "");
                    Session.SendAll(w, true);
                }
                return;
            }
            for (int i = 0; i < phones.Length; i++)
            {
                Phone p = PhoneAt(i);
                if (p == null || p.Ring == null) continue;
                CapRing(p);
                float d = LocalDistance(p.Go.transform.position);
                bool want = !p.HostRuns && p.Go.activeInHierarchy && d < (p.RunsHere ? KeepDist : TakeDist);
                if (want != p.RunsHere)
                {
                    p.RunsHere = want; p.Fought = false;
                    p.Ring.enabled = want;
                    if (p.Jokes != null) p.Jokes.enabled = want;
                    Log.Info("appels : telephone " + p.Name + (want ? " : la logique de l'hote ne tourne pas, elle tourne ici (joueur a " + d.ToString("F0") + " m)"
                                                                     : " : logique rendue a l'hote (" + (p.HostRuns ? "elle tourne de nouveau chez lui" : "joueur a " + d.ToString("F0") + " m") + ")"));
                }
                else if (p.RunsHere && !p.Ring.enabled && !p.Fought)
                {
                    p.Fought = true;   // arretee par WorldFsms (sans Calls.RunsHere) : on ne lutte pas (chaque rallumage la relancerait)
                    Log.Warn("appels : telephone " + p.Name + " : logique arretee par le monde alors qu'elle devrait tourner ici");
                }
            }
        }

        static void OnPhones(NetReader r)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = 0, n = r.U8(); i < n; i++)
            {
                int idx = r.U8();
                bool runs = r.Bool();
                string st = r.Str();
                Phone p = idx < phones.Length ? PhoneAt(idx) : null;
                if (p == null) continue;
                if (p.HostRuns != runs) Log.Info("appels : logique du telephone " + p.Name + (runs ? " tourne" : " ne tourne pas") + " chez l'hote");
                p.HostRuns = runs; p.HostState = st; p.HostSeen = now;
            }
        }

    }
}
