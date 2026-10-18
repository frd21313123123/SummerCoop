using System.Collections.Generic;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Each MSC player keeps personal cash and earnings across reconnects.
    // Replayed jobs suppress money writes for the other players.
    public static class Wallet
    {
        // Boulots rejoues (Jobs) : la paie qu'ils versent ici est celle d'un autre joueur, retiree.
        static float suppressUntil;

        public static void Suppress(float seconds) { suppressUntil = Mathf.Max(suppressUntil, Time.realtimeSinceStartup + seconds); }

        static FsmFloat cash, bank;
        static float lastCash, lastBank, next, readyAt = -1, nextStore;
        static float loadingCredit;
        static string world;

        // Versements du monde, reperes par leur etat de paiement (crochet au debut et a la fin de l'etat :
        // ecart exact de liquide et de banque, hors du releve ordinaire) :
        //  - partages (allocation, aide au logement) : chaque joueur envoie
        //    le montant que sa logique a verse (avec son numero de source) ; chacun garde, UNE fois, le plus
        //    haut montant vu pour ce versement pendant PayWindow : copie locale en trop retiree (celle de
        //    l'autre deja la, ou etat de paie rejoue une 2e fois par Jobs), ecart complete ;
        //  - personnels (interets, paies des boulots depuis 0.75.42) : calcules chez chacun, gardes pour soi, jamais envoyes ;
        //    une paie de boulot versee par un etat rejoue (boulot d'un autre) est retiree.
        // L'ordre de la liste est le numero de source envoye : le meme chez tous (meme version).
        class Pay
        {
            public readonly string Path, Fsm, State, Label;
            public readonly bool Shared, Own;                           // Own : enveloppe reservee a ce module (Claim)
            public PlayMakerFSM F;
            public bool Hooked, Open, Replayed;                         // Replayed : etat entre pendant un rejeu
            public float C0, B0;                                        // releve au debut de l'etat
            public float At = -100, HaveC, HaveB, BestC, BestB, Sent;   // versement en cours : deja en poche, plus haut vu, envoye
            public Pay(string path, string fsm, string state, string label, bool shared, bool own = false) { Path = path; Fsm = fsm; State = state; Label = label; Shared = shared; Own = own; }
        }
        static readonly Pay[] Pays = new Pay[0];
        const float PayWindow = 30f;   // < 36 s : mardi -> vendredi des publicites en sommeil accelere (12 s par jour)
        const int FirstGain = 7;       // rang du 1er gain d'objet dans Pays
        static float nextPayScan;
        static int payHooked, worldLocal, worldIn;

        // Observe un etat de paiement. Pas une ModHook : ce n'est pas un rejeu, et l'audit compterait les
        // paies des boulots comme accrochees par deux modules (Jobs les suit deja).
        class PayHook : FsmStateAction
        {
            public Pay P;
            public bool End;
            public override void OnEnter()
            {
                try { if (End) PayEnd(P); else PayStart(P); } catch (System.Exception e) { Replay.HookError(e); }
                Finish();
            }
        }

        static string StorePath { get { return System.IO.Path.Combine(Log.DataDir, "porte-monnaie.ini"); } }

        // ---------------------------------------------------------------- don entre joueurs (porte-monnaie, WalletPanel)
        // Le donneur retire la somme de son liquide, le receveur l'ajoute au sien ; ni l'un ni l'autre n'est un revenu
        // ou une depense pour le releve (pas renvoye aux autres comme une paie).
        public static bool Ready { get { return ready && cash != null; } }
        public static float Cash { get { return cash != null ? cash.Value : 0f; } }
        public const int MaxGift = 1000000;
        public enum GiftStatus { Available, Loading, PlayerUnavailable, InvalidAmount, InsufficientCash }

        public static GiftStatus CheckGift(int to, int amount)
        {
            if (!Ready || !Session.Active) return GiftStatus.Loading;
            PlayerInfo pi;
            if (!Session.Players.TryGetValue(to, out pi) || pi.Local || pi.Level != 1) return GiftStatus.PlayerUnavailable;
            if (amount <= 0 || amount > MaxGift) return GiftStatus.InvalidAmount;
            if (float.IsNaN(cash.Value) || float.IsInfinity(cash.Value) || cash.Value < amount) return GiftStatus.InsufficientCash;
            return GiftStatus.Available;
        }

        public static string GiftError(GiftStatus status)
        {
            switch (status) {
                case GiftStatus.Loading: return Lang.T("Chargement du porte-monnaie personnel...", "Loading personal wallet...");
                case GiftStatus.PlayerUnavailable: return Lang.T("Ce joueur n'est pas encore en jeu", "This player is not in the game yet");
                case GiftStatus.InvalidAmount: return Lang.T("Montant : de 1 a 1000000.", "Amount: 1 to 1000000.");
                case GiftStatus.InsufficientCash: return Lang.T("Pas assez de liquide", "Not enough cash");
                default: return null;
            }
        }

        public static string Give(int to, int amount)
        {
            GiftStatus status = CheckGift(to, amount);
            if (status != GiftStatus.Available) return GiftError(status);
            PlayerInfo pi = Session.Players[to];
            cash.Value -= amount; lastCash -= amount;
            Session.SendAll(new NetWriter(Msg.Job).U8(Session.LocalId).Str("@don").U8((byte)to).I32(amount), true);
            Log.Info("argent : " + amount + " mk donnes a " + pi.Name + " (#" + to + ")");
            Store();
            return null;
        }

        public static void OnGift(int who, int to, int amount)
        {
            if (to != Session.LocalId || amount <= 0 || amount > 1000000) return;
            PlayerInfo pi;
            string name = Session.Players.TryGetValue(who, out pi) ? pi.Name : "#" + who;
            if (!Ready) { pendingGift += amount; pendingFrom = name; Log.Info("argent : " + amount + " mk recus de " + name + " (porte-monnaie pas pret : en attente)"); return; }
            cash.Value += amount; lastCash += amount;
            Log.Info("argent : " + amount + " mk recus de " + name);
            Hud.Toast(name + Lang.T(" vous a donn\u00E9 ", " gave you ") + amount + " mk");
            Store();
        }
        static int pendingGift; static string pendingFrom;

        // Triches (/argent) : liquide pour soi seul -- ni revenu partage ni depense (lastCash suit, comme un don recu).
        sealed class LoadingCreditHook : FsmStateAction
        {
            public override void OnEnter()
            {
                // MSC may finish loading PlayerMoney after the first UI frame.
                // Reapply only after a native load has replaced the balance,
                // never from an ordinary balance change such as a purchase.
                if (!ready && loadingCredit > 0)
                {
                    FsmFloat loaded = FsmVariables.GlobalVariables.FindFsmFloat("PlayerMoney");
                    if (loaded != null) loaded.Value += loadingCredit;
                }
                Finish();
            }
        }

        static void PreserveLoadingCredit()
        {
            GameObject database = Game.FindAny("Database/PlayerDatabase");
            PlayMakerFSM simulation = database != null ? Game.FsmOn(database, "Simulation") : null;
            FsmState load = simulation != null ? simulation.Fsm.GetState("Load") : null;
            if (load == null) return;
            foreach (FsmStateAction action in load.Actions) if (action is LoadingCreditHook) return;
            var actions = new List<FsmStateAction>(load.Actions);
            actions.Add(new LoadingCreditHook());
            load.Actions = actions.ToArray();
        }

        public static bool AddLocal(int amount)
        {
            if (amount <= 0) return false;
            // Cheat buttons also work in solo and during the personal-wallet
            // loading grace period. Credit MSC's actual cash immediately.
            if (!Ready)
            {
                cash = FsmVariables.GlobalVariables.FindFsmFloat("PlayerMoney");
                PreserveLoadingCredit();
            }
            if (cash == null) return false;
            cash.Value += amount;
            if (Ready) { lastCash += amount; Store(); }
            else loadingCredit += amount;
            Log.Info("argent : +" + amount + " mk (triche)");
            return true;
        }

        // Etat du corps de l'invite, garde comme son porte-monnaie (etat-joueur.ini, une ligne par monde) : la
        // sauvegarde recue a chaque connexion est celle de l'hote, et ces globales y sont les SIENNES -- l'invite
        // reprenait sinon la faim, la soif, la fatigue, l'ivresse... de l'hote a chaque session. Les cles de vehicules,
        // amendes, nom et adresse restent ceux de la partie (communs).
        static readonly string[] SelfFloats = { "PlayerHunger", "PlayerThirst", "PlayerFatigue", "PlayerStress", "PlayerDirtiness", "PlayerUrine",
            "PlayerDrunk", "PlayerDrunkAdjusted", "PlayerAlcoholism", "PlayerSweat", "PlayerTemp", "PlayerWeight", "PlayerBurns", "PlayerAllergy",
            "PlayerBerryPickSkill" };
        static readonly string[] SelfInts = { "PlayerCigarettes" };
        static string SelfPath { get { return System.IO.Path.Combine(Log.DataDir, "etat-joueur.ini"); } }
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        static string SelfLine()
        {
            var sb = new System.Text.StringBuilder();
            foreach (string n in SelfFloats)
            {
                FsmFloat v = FsmVariables.GlobalVariables.FindFsmFloat(n);
                if (v != null) sb.Append(sb.Length > 0 ? ";" : "").Append(n).Append(':').Append(v.Value.ToString("R", Inv));
            }
            foreach (string n in SelfInts)
            {
                FsmInt v = FsmVariables.GlobalVariables.FindFsmInt(n);
                if (v != null) sb.Append(sb.Length > 0 ? ";" : "").Append(n).Append(':').Append(v.Value.ToString(Inv));
            }
            return sb.ToString();
        }

        static void RestoreSelf()
        {
            if (Session.IsHost || world.Length == 0 || !System.IO.File.Exists(SelfPath)) return;
            foreach (string l in System.IO.File.ReadAllLines(SelfPath))
            {
                int eq = l.IndexOf('=');
                if (eq <= 0 || l.Substring(0, eq) != world) continue;
                int n = 0;
                foreach (string kv in l.Substring(eq + 1).Split(';'))
                {
                    int c = kv.IndexOf(':');
                    if (c <= 0) continue;
                    string name = kv.Substring(0, c), val = kv.Substring(c + 1);
                    float f; int i;
                    FsmFloat vf = System.Array.IndexOf(SelfFloats, name) >= 0 ? FsmVariables.GlobalVariables.FindFsmFloat(name) : null;
                    FsmInt vi = System.Array.IndexOf(SelfInts, name) >= 0 ? FsmVariables.GlobalVariables.FindFsmInt(name) : null;
                    if (vf != null && float.TryParse(val, System.Globalization.NumberStyles.Float, Inv, out f)) { vf.Value = f; n++; }
                    else if (vi != null && int.TryParse(val, System.Globalization.NumberStyles.Integer, Inv, out i)) { vi.Value = i; n++; }
                }
                Log.Info("joueur : etat du corps retrouve pour ce monde (" + n + " valeurs : faim, soif, fatigue...)");
            }
        }

        static void StoreSelf()
        {
            if (Session.IsHost || world == null || world.Length == 0) return;
            var lines = new List<string>();
            if (System.IO.File.Exists(SelfPath))
                foreach (string l in System.IO.File.ReadAllLines(SelfPath)) if (!l.StartsWith(world + "=")) lines.Add(l);
            lines.Add(world + "=" + SelfLine());
            try { System.IO.File.WriteAllLines(SelfPath, lines.ToArray()); } catch { }
        }

        static void Restore()
        {
            FsmFloat pid = FsmVariables.GlobalVariables.FindFsmFloat("PlayerID");
            world = pid != null ? pid.Value.ToString("F0") : "";
            RestoreSelf();
            if (Session.IsHost || world.Length == 0) return;
            if (System.IO.File.Exists(StorePath)) foreach (string l in System.IO.File.ReadAllLines(StorePath))
            {
                string[] f = l.Split('=');
                if (f.Length != 2 || f[0] != world) continue;
                string[] v = f[1].Split(';');
                float c, b;
                if (v.Length == 2 && float.TryParse(v[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out c)
                    && float.TryParse(v[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b))
                {
                    cash.Value = c + loadingCredit; bank.Value = 0;
                    Log.Info("argent : porte-monnaie retrouve pour ce monde (" + world + ")");
                    Hud.Toast(Lang.T("Votre porte-monnaie : ", "Your wallet: ") + Mathf.RoundToInt(c) + " mk");
                    return;
                }
            }
            FirstVisit();
        }

        // Premiere venue d'un invite dans ce monde : l'argent d'une nouvelle partie, pas celui de l'hote a cet instant
        // (demande de JD, 10/10). Valeurs relevees au menu principal (globales du jeu avant tout chargement) ; [Coop]
        // ArgentDepart=0 : comme avant (l'argent de la sauvegarde de l'hote).
        static float startCash = -1, startBank = 0;
        static void FirstVisit()
        {
            if (Session.IsHost || Config.GetInt("Coop", "ArgentDepart", 1) == 0) return;
            float c = Config.GetFloat("Coop", "ArgentDepartLiquide", startCash), b = 0;
            if (c < 0 || b < 0) { Log.Info("argent : premiere venue dans ce monde, argent de depart inconnu : celui de la sauvegarde"); return; }
            Log.Info("argent : premiere venue dans ce monde (" + world + ") : argent de depart " + c + " liquide, " + b + " banque (au lieu de " + cash.Value + " / " + bank.Value + ")");
            cash.Value = c + loadingCredit; bank.Value = b;
            Hud.Toast(Lang.T("Premi\u00E8re venue : ", "First visit: ") + Mathf.RoundToInt(c + b) + Lang.T(" mk pour commencer", " mk to start with"));
        }

        static void Store()
        {
            if (Session.IsHost || world == null || world.Length == 0) return;
            var lines = new System.Collections.Generic.List<string>();
            if (System.IO.File.Exists(StorePath))
                foreach (string l in System.IO.File.ReadAllLines(StorePath)) if (!l.StartsWith(world + "=")) lines.Add(l);
            lines.Add(world + "=" + cash.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";" + bank.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try { System.IO.File.WriteAllLines(StorePath, lines.ToArray()); } catch { }
            StoreSelf();
        }
        static bool ready;

        public static void OnLevelLoaded()
        {
            if (Application.loadedLevelName == "MainMenu" && startCash < 0)
            {
                FsmFloat mc = FsmVariables.GlobalVariables.FindFsmFloat("PlayerMoney"), mb = FsmVariables.GlobalVariables.FindFsmFloat("PlayerBankAccount");
                if (mc != null) { startCash = mc.Value; startBank = 0; Log.Info("argent : MSC initial cash " + startCash); }
            }
            cash = bank = null;
            ready = false;
            loadingCredit = 0;
            // MSC can still load the host's globals during its initial frames.
            // Restore personal cash after that native initialization completes.
            readyAt = PlayerSync.InGame ? Time.realtimeSinceStartup + 15f : -1;
            foreach (Pay p in Pays) { p.F = null; p.Hooked = p.Open = false; p.At = -100; }
            nextPayScan = 0; payHooked = worldLocal = worldIn = 0;
            testStep = 0; remoteSince = -1;
            UnmuteTrigs();
            foreach (Trig g in Trigs) { g.F = null; g.Hooked = false; }
            envs.Clear(); envIncomeUntil = 0;
            claimAt = PlayerSync.InGame ? Time.realtimeSinceStartup + 4f : -1;
            trigLocal = trigOther = 0;
        }

        // Accroche les etats de paiement des automates charges (objet actif) ; les autres (taxi pas encore la, enveloppe
        // pas encore sortie) au prochain passage, toutes les 2 s : une enveloppe touchee juste apres son apparition doit
        // deja etre suivie (sinon son argent passait pour un revenu ordinaire, partage).
        static void HookPays()
        {
            if (payHooked == Pays.Length || Time.realtimeSinceStartup < nextPayScan) return;
            nextPayScan = Time.realtimeSinceStartup + 2f;
            foreach (Pay p in Pays)
            {
                if (p.Hooked) continue;
                GameObject go = GameObject.Find(p.Path);   // actifs seulement : automate initialise
                PlayMakerFSM f = go != null && go.activeInHierarchy ? Game.FsmOn(go, p.Fsm) : null;
                if (f == null) continue;
                try
                {
                    FsmState st = f.Fsm.GetState(p.State);
                    if (st == null) { p.Hooked = true; payHooked++; Log.Warn("argent : etat " + p.State + " absent de " + p.Path + "::" + p.Fsm); continue; }
                    var list = new List<FsmStateAction>(st.Actions);
                    bool already = false;
                    foreach (FsmStateAction a in list) { var h = a as PayHook; if (h != null && h.P == p) already = true; }
                    if (!already)
                    {
                        list.Insert(0, new PayHook { P = p });
                        list.Add(new PayHook { P = p, End = true });
                        st.Actions = list.ToArray();
                    }
                }
                catch { continue; }   // automate pas encore pret : au prochain passage
                p.F = f; p.Hooked = true; payHooked++;
                Log.Info("argent : versement du monde suivi : " + p.Label + " (" + p.Path + "::" + p.Fsm + " / " + p.State + ", " + (p.Shared ? "compte une fois, au plus haut des joueurs" : "chacun le sien") + ")");
            }
        }

        // Debut de l'etat de paiement (avant ses actions) : releve du liquide et de la banque.
        static void PayStart(Pay p)
        {
            if (!ready || !Session.Active) return;
            p.C0 = cash.Value; p.B0 = bank.Value; p.Open = true; p.Replayed = Replay.Depth > 0;
        }

        // Fin de l'etat (apres ses actions ; sinon a la mise a jour suivante) : l'ecart est le versement.
        static void PayEnd(Pay p)
        {
            if (!p.Open) return;
            p.Open = false;
            Settle(p, cash.Value - p.C0, bank.Value - p.B0);
        }

        static void Settle(Pay p, float dC, float dB)
        {
            dC = Mathf.Max(dC, 0f); dB = Mathf.Max(dB, 0f);
            if (dC < 0.005f && dB < 0.005f) return;   // rien de verse
            lastCash += dC; lastBank += dB;            // hors du releve ordinaire : jamais renvoye comme un revenu
            if (!p.Shared && p.Replayed && System.Array.IndexOf(Pays, p) < FirstGain && System.Array.IndexOf(Pays, p) >= 2 && Session.RemoteCount > 0)
            {
                // Etat de paie d'un boulot rejoue ici depuis un autre joueur : sa paie, pas la notre.
                Add(-dC, -dB);
                Log.Info("argent : " + p.Label + " +" + (dC + dB) + " verse par un rejeu (boulot d'un autre joueur) : retire");
                return;
            }
            if (!p.Shared)
            {
                bool gain = System.Array.IndexOf(Pays, p) >= FirstGain;
                Log.Info("argent : " + p.Label + " +" + (dC + dB) + (gain ? ", garde pour soi (gain d'un objet mis par ce joueur, pas partage)" : ", gardes pour soi (calcules sur son propre solde)"));
                return;
            }
            worldLocal++;
            float adj = Merge(p, dC, dB, true);
            if (dC + dB > p.Sent + 0.005f)
            {
                p.Sent = dC + dB;
                Session.SendAll(new NetWriter(Msg.Income).U8(Session.LocalId).F32(dC).F32(dB).U8(System.Array.IndexOf(Pays, p) + 1), true);
            }
            Log.Info("argent : " + p.Label + " +" + (dC + dB) + " verse ici par le monde"
                     + (adj < -0.005f ? ", deja compte : " + (-adj) + " retire" : adj > 0.005f ? ", complete de " + adj : "") + " (une fois, au plus haut des joueurs)");
        }

        // Libelle d'un versement dans la langue du joueur (les Label restent en francais sans accents pour le journal).
        static string Shown(string label)
        {
            switch (label)
            {
                case "allocation chomage": return Lang.T("allocation ch\u00F4mage", "unemployment benefit");
                case "aide au logement": return Lang.T("aide au logement", "housing support");
                case "paie de l'usine": return Lang.T("paie de l'usine", "factory pay");
                case "paie de l'usine (heures sup)": return Lang.T("paie de l'usine (heures sup)", "factory pay (overtime)");
                case "paie des publicites": return Lang.T("paie des publicit\u00E9s", "ads pay");
                case "paie du taxi": return Lang.T("paie du taxi", "taxi pay");
                case "interets": return Lang.T("int\u00E9r\u00EAts", "interest");
                case "rachat de ferraille": return Lang.T("rachat de ferraille", "scrap sale");
                case "kilju vendu a Jokke": return Lang.T("kilju vendu \u00E0 Jokke", "kilju sold to Jokke");
                case "gains du loto": return Lang.T("gains du loto", "lottery win");
                case "prix du rallye": return Lang.T("prix du rallye", "rally prize");
                case "prix de la course sur glace": return Lang.T("prix de la course sur glace", "ice race prize");
            }
            return Lang.Fr ? label : Lang.R("income");
        }

        // Versement partage vu chez un autre joueur.
        static void WorldIn(Pay p, float c, float b, string name)
        {
            worldIn++;
            float adj = Merge(p, c, b, false);
            if (adj >= 1f) Hud.Toast("+" + Mathf.RoundToInt(adj) + " mk (" + Shown(p.Label) + ")");
            Log.Info("argent : " + p.Label + " de " + name + " : " + (c + b) + (adj > 0.005f ? ", +" + adj + " ici" : ", deja compte ici"));
        }

        // Un montant de plus pour le versement en cours (ici : deja ajoute par le jeu ; ailleurs : pas encore) :
        // le porte-monnaie garde le plus haut vu, une seule fois. Rend l'ajustement fait.
        static float Merge(Pay p, float c, float b, bool local)
        {
            float now = Time.realtimeSinceStartup;
            if (now - p.At > PayWindow) { p.At = now; p.HaveC = p.HaveB = p.BestC = p.BestB = p.Sent = 0; }   // nouveau versement
            if (local) { p.HaveC += c; p.HaveB += b; }
            p.BestC = Mathf.Max(p.BestC, c); p.BestB = Mathf.Max(p.BestB, b);
            float dC = p.BestC - p.HaveC, dB = p.BestB - p.HaveB;
            if (Mathf.Abs(dC) >= 0.005f || Mathf.Abs(dB) >= 0.005f) Add(dC, dB);
            p.HaveC = p.BestC; p.HaveB = p.BestB;
            return dC + dB;
        }

        // Change le porte-monnaie sans que le releve ordinaire le prenne pour un revenu ou une depense.
        static void Add(float c, float b)
        {
            cash.Value += c; bank.Value += b;
            lastCash += c; lastBank += b;
        }

        // Audit : automate dont Wallet observe un etat de paiement.
        public static bool Observes(PlayMakerFSM f)
        {
            if (f == null) return false;
            foreach (Pay p in Pays) if (p.F == f) return true;
            return false;
        }

        // ================================================================ gains declenches par un objet
        // Declencheur qui paie quand un objet y tombe : 'Object' = l'objet ; ses etats d'argent n'ecrivent rien chez un
        // joueur qui ne l'a pas mis la (sa copie Props y est tombee aussi). Ces automates (sans saisie ni sauvegarde) ne
        // sont rejoues par personne : ils sont a ce module.
        //  - benne a ferraille de Fleetari : 'State 3' (AddFsmFloat : valeur du metal ajoutee a l'enveloppe ScrapMoney) ;
        //  - poubelle du Voittous (billets de loto, megaveto) : 'State 4', 'Bank' (gain vire a la banque, releve).
        // Le billet ou la piece part a la decharge chez tous ('Turn into garbage' n'est pas coupe).
        class Trig
        {
            public readonly string Path, Fsm, Label; public readonly string[] States;
            public PlayMakerFSM F; public bool Hooked;
            public Trig(string path, string fsm, string label, params string[] states) { Path = path; Fsm = fsm; Label = label; States = states; }
        }
        static readonly Trig[] Trigs = new Trig[0];
        static readonly List<FsmStateAction> trigMuted = new List<FsmStateAction>();
        static float claimAt = -1, nextTrigScan;
        static int trigLocal, trigOther;

        class TrigHook : ModHook
        {
            public override string Module { get { return "argent"; } }
            public Trig G; public FsmState St;
            public override void OnEnter()
            {
                try { if (Session.Active && Replay.Depth == 0) OnTrig(this); } catch (System.Exception e) { Replay.HookError(e); }
                Finish();
            }
        }

        // Avant le releve du monde (16 s) : enveloppes et declencheurs a ce module.
        static void ClaimEarly()
        {
            var sb = new System.Text.StringBuilder("argent : reserves a ce module :");
            foreach (Pay p in Pays) if (p.Own) sb.Append(' ').Append(ClaimAt(p.Path, p.Fsm));
            foreach (Trig g in Trigs) sb.Append(' ').Append(ClaimAt(g.Path, g.Fsm));
            sb.Append(", ").Append(ClaimEnvelopes());
            Log.Info(sb.ToString());
        }

        static string ClaimAt(string path, string fsm)
        {
            GameObject go = Game.FindAny(path);
            PlayMakerFSM f = go != null ? Game.FsmOn(go, fsm) : null;
            string n = path.Substring(path.LastIndexOf('/') + 1);
            if (f == null) return n + " (absent)";
            return Replay.Claim(f, "argent") ? n : n + " (deja a " + Replay.Owner(f) + ")";
        }

        static void HookTrigs()
        {
            if (Time.realtimeSinceStartup < nextTrigScan) return;
            nextTrigScan = Time.realtimeSinceStartup + 2f;
            foreach (Trig g in Trigs)
            {
                if (g.Hooked) continue;
                GameObject go = GameObject.Find(g.Path);
                PlayMakerFSM f = go != null && go.activeInHierarchy ? Game.FsmOn(go, g.Fsm) : null;
                if (f == null || Replay.Owner(f) != "argent") continue;
                try
                {
                    foreach (string s in g.States)
                    {
                        FsmState st = f.Fsm.GetState(s);
                        if (st == null) { Log.Warn("argent : etat " + s + " absent de " + g.Path); continue; }
                        if (System.Array.Exists(st.Actions, a => a is TrigHook)) continue;
                        var list = new List<FsmStateAction>(st.Actions);
                        list.Insert(0, new TrigHook { G = g, St = st });
                        st.Actions = list.ToArray();
                    }
                }
                catch { continue; }   // pas encore pret
                g.F = f; g.Hooked = true;
                Log.Info("argent : declencheur suivi : " + g.Label + " (" + g.Path + "::" + g.Fsm + "), paye seulement chez celui qui y met l'objet");
            }
        }

        // Ecritures d'argent : vers un autre automate (AddFsmFloat, SetFsmString : releve de banque, SendEventByName :
        // avis), ou vers une globale (PlayerBankAccount, PlayerMoney).
        static bool MoneyAction(PlayMakerFSM f, FsmStateAction a)
        {
            string n = a.GetType().Name;
            if (n == "AddFsmFloat" || n == "SetFsmFloat" || n == "SubtractFsmFloat" || n == "SetFsmInt" || n == "AddToFsmInt"
                || n == "SetFsmString" || n == "SetFsmBool" || n == "SendEventByName") return true;
            foreach (System.Reflection.FieldInfo fi in a.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (fi.Name != "floatVariable" && fi.Name != "intVariable" && fi.Name != "storeResult") continue;
                var nv = fi.GetValue(a) as NamedVariable;
                if (nv != null && nv.UseVariable && !Game.LocalVar(f, nv.Name)) return true;
            }
            return false;
        }

        static void OnTrig(TrigHook h)
        {
            Trig g = h.G;
            FsmGameObject ov = g.F != null ? g.F.FsmVariables.FindFsmGameObject("Object") : null;
            GameObject obj = ov != null ? ov.Value : null;
            if (Home.CausedHere(obj, 120f))
            {
                trigLocal++;
                Log.Info("argent : " + g.Label + " (" + h.St.Name + ") : " + (obj != null ? obj.name : "?") + " mis par ce joueur-ci, paye ici");
                return;
            }
            int n = 0;
            bool after = false;
            foreach (FsmStateAction a in h.St.Actions)
            {
                if (a == h) { after = true; continue; }
                if (!after || a == null || !a.Enabled || !MoneyAction(g.F, a)) continue;
                a.Enabled = false; trigMuted.Add(a); n++;
            }
            trigOther++;
            Log.Info("argent : " + g.Label + " (" + h.St.Name + ") : " + (obj != null ? obj.name : "?") + " mis par un autre joueur, rien verse ici (" + n + " actions coupees)");
        }

        static void UnmuteTrigs()
        {
            if (trigMuted.Count == 0) return;
            foreach (FsmStateAction a in trigMuted) if (a != null) a.Enabled = true;
            trigMuted.Clear();
        }

        public static void Update()
        {
            UnmuteTrigs();   // ecritures d'argent coupees pour un seul passage (etat deja entre)
            if (Session.Active && claimAt > 0 && Time.realtimeSinceStartup >= claimAt) { claimAt = -1; ClaimEarly(); }
            if (!Session.Active || readyAt < 0 || Time.realtimeSinceStartup < readyAt) return;
            if (!ready)
            {
                cash = FsmVariables.GlobalVariables.FindFsmFloat("PlayerMoney");
                // MSC has cash only. A missing MWC bank must not disable the wallet.
                bank = new FsmFloat(0);
                FsmFloat playerId = FsmVariables.GlobalVariables.FindFsmFloat("PlayerID");
                if (cash == null || playerId == null || playerId.Value == 0) { readyAt = Time.realtimeSinceStartup + 1; return; }
                Restore();
                loadingCredit = 0;
                if (pendingGift > 0) { cash.Value += pendingGift; Hud.Toast(pendingFrom + Lang.T(" vous a donn\u00E9 ", " gave you ") + pendingGift + " mk"); pendingGift = 0; }
                lastCash = cash.Value; lastBank = bank.Value;
                ready = true;
                Log.Info("argent : liquide " + lastCash + ", banque " + lastBank);
            }
            HookPays();
            HookTrigs();
            HookEnvelopes();
            foreach (Pay p in Pays) if (p.Open) PayEnd(p);   // fin d'etat pas vue (etat quitte avant la fin de ses actions)
            if (Time.realtimeSinceStartup >= nextStore) { nextStore = Time.realtimeSinceStartup + 10f; Store(); }
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 0.5f;
            float dC = cash.Value - lastCash, dB = bank.Value - lastBank;
            if (Mathf.Abs(dC) < 0.005f && Mathf.Abs(dB) < 0.005f) return;
            float transfer = 0;
            if (dC > 0 && dB < 0) transfer = Mathf.Min(dC, -dB);
            else if (dC < 0 && dB > 0) transfer = Mathf.Min(-dC, dB);
            float inC = dC > 0 ? dC - (dB < 0 ? transfer : 0) : 0;
            float inB = dB > 0 ? dB - (dC < 0 ? transfer : 0) : 0;
            lastCash = cash.Value; lastBank = bank.Value;
            if (inC < 0.005f && inB < 0.005f) return;
            float nowU = Time.realtimeSinceStartup;
            if (Machines.KeepsMoney) { Log.Info("argent : gain " + (inC + inB) + " d'une machine a jeu, garde pour soi"); return; }
            if (ModCheatbox.MenuOpen()) { Log.Info("argent : +" + (inC + inB) + " tape dans le menu de Cheatbox++, garde pour soi"); return; }
            if (nowU < suppressUntil && nowU >= envIncomeUntil && Session.RemoteCount > 0)
            {
                // Paie versee ici par le boulot d'un autre joueur, rejoue (Jobs) : la sienne, pas la notre.
                cash.Value -= inC; bank.Value -= inB;
                lastCash = cash.Value; lastBank = bank.Value;
                Log.Info("argent : revenu " + (inC + inB) + " venu d'un boulot rejoue (celui d'un autre joueur) : retire");
                return;
            }
            Log.Info("argent : revenu " + inC + " (liquide) + " + inB + " (banque), garde pour soi (chacun sa paie)");
        }

        public static void OnMessage(Peer from, NetReader r)
        {
            int who = r.U8();
            if (Session.IsHost) who = from.Id;
            float inC = r.F32(), inB = r.F32();
            int src = r.More ? r.U8() : 0;   // versement du monde (numero dans Pays), 0 : revenu d'un joueur
            if (Session.IsHost)
            {
                var w = new NetWriter(Msg.Income).U8(who).F32(inC).F32(inB);
                if (src > 0) w.U8(src);
                Session.Broadcast(w, true, who);
            }
            if (!ready || inC < 0 || inB < 0 || inC + inB > 1e6f) return;
            PlayerInfo pi;
            string name = Session.Players.TryGetValue(who, out pi) ? pi.Name : "?";
            if (src > 0) { if (src <= Pays.Length && Pays[src - 1].Shared) WorldIn(Pays[src - 1], inC, inB, name); return; }
            // Revenu d'un joueur (versions avant 0.75.42) : a lui, chacun sa paie.
            Log.Info("argent : revenu " + (inC + inB) + " annonce par " + name + " : a lui (chacun sa paie), rien ici");
        }

        // ---------------------------------------------------------------- enveloppes de paie
        // Le client d'un boulot (fosses septiques HouseShit*, livraisons de bois HouseWood*, fermier) ou l'organisateur
        // (pas les prix des courses, PriceMoney* : podium de chacun, voir Pays) tend une enveloppe : PayMoney :: Use. Clic -> "State 1" (PlayerMoney +=
        // Money, animation), puis "State 3" (Money a 0, enveloppe cachee) ; le bois passe aussi par "Pay for car" -> "State 3".
        // Le boulot est rejoue chez tous : chacun a SA copie de l'enveloppe, pleine. Le monde (WorldFsms) ne l'aurait
        // suivie qu'a son releve suivant (etale : jusqu'a une minute apres son apparition) ; prise par l'un avant, elle
        // restait pleine chez l'autre, qui la prenait aussi, et chaque prise etant un revenu partage, chacun touchait la
        // paie DEUX fois. Reservees ici des le chargement (inactives comprises) : prise par un joueur (entree en "State 3"),
        // elle est retiree chez les autres ("State 3" : ni argent ni son), meme si leur copie n'apparait qu'apres (60 s).
        // La paie est un revenu ordinaire du preneur (Update : jamais pris pour celle d'un boulot rejoue), gardee par lui
        // seul (0.75.42). Pas l'enveloppe de Jokke (gain d'objet, voir Pays).
        class Env { public string Key; public PlayMakerFSM F; public bool Hooked; public float RetireUntil = -1; }
        static readonly List<Env> envs = new List<Env>();
        static float nextEnvHook, envIncomeUntil;
        static bool envApplying;

        class EnvHook : ModHook
        {
            public override string Module { get { return "argent"; } }
            public Env E; public bool Take;
            public override void OnEnter()
            {
                try
                {
                    if (Session.Active && !envApplying && Replay.Depth == 0)
                    {
                        if (Take) envIncomeUntil = Time.realtimeSinceStartup + 3f;   // la paie qui suit est a nous (pas celle d'un boulot rejoue)
                        else EnvTaken(E);
                    }
                }
                catch (System.Exception e) { Replay.HookError(e); }
                Finish();
            }
        }

        static bool IsEnvelope(PlayMakerFSM f)
        {
            if (f.FsmName != "Use" || f.hideFlags != HideFlags.None) return false;
            string n = f.gameObject.name;
            return n == "PayMoney";   // (PriceMoney* : prix des courses, personnels -- voir Pays)
        }

        static string ClaimEnvelopes()
        {
            envs.Clear();
            var keys = new HashSet<string>();
            foreach (Object o in Game.AllFsms())
            {
                var f = (PlayMakerFSM)o; if (f == null) continue;
                if (!IsEnvelope(f) || !f.transform.root.gameObject.activeInHierarchy) continue;   // (modeles : racine inactive)
                string key = Recon.Path(f.transform);
                if (!keys.Add(key)) continue;
                if (!Replay.Claim(f, "argent")) { Log.Warn("argent : enveloppe " + key + " deja a " + Replay.Owner(f)); continue; }
                envs.Add(new Env { Key = key, F = f });
            }
            return envs.Count + " enveloppes de paie";
        }

        // Accrochees a leur premiere apparition (automate demarre : etats charges) ; retrait en attente applique.
        static void HookEnvelopes()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextEnvHook) return;
            nextEnvHook = now + 0.25f;
            foreach (Env e in envs)
            {
                if (e.F == null || !e.F.gameObject.activeInHierarchy) continue;
                if (!e.Hooked)
                {
                    try
                    {
                        FsmState take = e.F.Fsm.GetState("State 1"), end = e.F.Fsm.GetState("State 3");
                        if (take == null || end == null) { e.Hooked = true; Log.Warn("argent : enveloppe " + e.Key + " sans State 1/State 3"); continue; }
                        if (!take.IsInitialized || !end.IsInitialized) continue;
                        var l1 = new List<FsmStateAction>(take.Actions); l1.Insert(0, new EnvHook { E = e, Take = true }); take.Actions = l1.ToArray();
                        var l3 = new List<FsmStateAction>(end.Actions); l3.Insert(0, new EnvHook { E = e }); end.Actions = l3.ToArray();
                        e.Hooked = true;
                        Log.Info("argent : enveloppe " + e.Key + " tendue ici");
                    }
                    catch { continue; }   // automate pas encore pret
                }
                if (e.RetireUntil > 0)
                {
                    if (now > e.RetireUntil) e.RetireUntil = -1;
                    else Retire(e);
                }
            }
        }

        // Prise ici (ou achat de la voiture du client du bois) : retiree chez les autres.
        static void EnvTaken(Env e)
        {
            Log.Info("argent : enveloppe " + e.Key + " prise ici");
            Session.SendAll(new NetWriter(Msg.Payout).U8(Session.LocalId).Str(e.Key), true);
        }

        static void Retire(Env e)
        {
            e.RetireUntil = -1;
            string st = e.F.ActiveStateName;
            if (st == "State 3" || st == "State 5") return;
            envApplying = true; Replay.Depth++;
            try { Game.SetState(e.F, "State 3"); }
            finally { envApplying = false; Replay.Depth--; }
            Log.Info("argent : enveloppe " + e.Key + " retiree ici (" + st + " -> " + e.F.ActiveStateName + ")");
        }

        public static void OnPayout(Peer from, NetReader r)
        {
            int who = r.U8();
            if (Session.IsHost) who = from.Id;
            string key = r.Str();
            if (Session.IsHost) Session.Broadcast(new NetWriter(Msg.Payout).U8(who).Str(key), true, who);
            Env e = envs.Find(x => x.Key == key);
            if (e == null || e.F == null) { Log.Warn("argent : enveloppe " + key + " prise par #" + who + ", introuvable ici"); return; }
            PlayerInfo pi;
            Log.Info("argent : enveloppe " + key + " prise par " + (Session.Players.TryGetValue(who, out pi) ? pi.Name : "#" + who));
            if (e.F.gameObject.activeInHierarchy && e.Hooked) Retire(e);
            else e.RetireUntil = Time.realtimeSinceStartup + 60f;   // pas encore tendue ici : retiree a son apparition
        }

        // Essais (Autotest=paie) : enveloppe sortie de sous son PNJ, suivie comme les autres sous son nouveau chemin.
        public static string TestEnvelope(PlayMakerFSM f)
        {
            if (f == null) return "pas d'automate";
            Env e = envs.Find(x => x.F == f);
            if (e == null) { e = new Env { F = f }; envs.Add(e); }
            e.Key = Recon.Path(f.transform);
            return "enveloppe d'essai " + e.Key + " suivie (" + (Replay.Owner(f) ?? "libre") + ")";
        }

        // Essais (Autotest) 'revenu' : a 25 s, etat ; a 30 s (l'hote attend en plus un invite en partie depuis
        // 20 s, porte-monnaie pret), le monde verse ici l'aide au logement de la semaine, chez l'hote comme
        // chez l'invite (comme chaque vendredi) ; 10 s plus tard, les interets du jour ; 22 s apres, etat.
        // Attendu des deux cotes : l'aide comptee UNE fois, les interets de chacun gardes pour soi, aucun
        // « argent : +... de » (revenu ordinaire) pour ces versements.
        static int testStep;
        static float testAt, remoteSince = -1;

        public static string Test(float amount)
        {
            if (!ready) return "argent pas pret";
            cash.Value += amount;
            return "liquide " + cash.Value;
        }

        public static string State() { return ready ? "liquide " + cash.Value + ", banque " + bank.Value : "?"; }
    }
}
