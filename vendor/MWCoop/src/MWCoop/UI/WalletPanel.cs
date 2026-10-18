using System.Collections.Generic;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Porte-monnaie (demande de JD, 10/10) : touche Keys.Wallet (N par defaut, reglable dans le lanceur). Liquide du joueur,
    // montant choisi (boutons -1000 ... +1000, molette), un bouton DONNER par joueur en partie. Echap ou la touche : ferme.
    // Pendant qu'il est ouvert, le jeu ne recoit plus la souris ni les deplacements (Menu.Block).
    public static class WalletPanel
    {
        public static bool Open;
        static int amount = 100;
        static string note; static float noteUntil; static bool noteBad;
        static float openedAt;
        static bool adjustWhenReady;
        public static int Amount { get { return amount; } }
        static readonly int[] Steps = { -1000, -100, -10, 10, 100, 1000 };

        public static void Update()
        {
            if (!PlayerSync.InGame || !Session.Active) { Open = false; return; }
            if (Open && Input.GetKeyDown(KeyCode.Escape)) { HandleKey(KeyCode.Escape); return; }
            if (Input.GetKeyDown(Keys.Wallet)) HandleKey(Keys.Wallet);
            if (Open && adjustWhenReady && Wallet.Ready) AdjustAmount();
        }

        public static bool HandleKey(KeyCode key)
        {
            if (!PlayerSync.InGame || !Session.Active || Menu.Open || Menu.ChatOpen) return false;
            if (Open && key == KeyCode.Escape) { Open = false; return true; }
            if (key != Keys.Wallet) return false;
            Open = !Open;
            if (Open) {
                openedAt = Time.realtimeSinceStartup; note = null; CheatPanel.Open = false; MapPanel.Open = false;
                adjustWhenReady = true;
                if (Wallet.Ready) AdjustAmount();
            }
            return true;
        }

        static void AdjustAmount()
        {
            // A large amount selected in a previous session must not leave the
            // GIVE button silently disabled after loading a smaller balance.
            amount = Mathf.Clamp(amount, 1, Mathf.Max(1, Mathf.FloorToInt(Mathf.Min(Wallet.Cash, Wallet.MaxGift))));
            adjustWhenReady = false;
        }

        static Rect Frame(int players)
        {
            float k = Mathf.Clamp01((Time.realtimeSinceStartup - openedAt) / 0.14f);
            float w = Mathf.Min(Style.Px(560), Screen.width - Style.Px(32));
            float h = Style.Px(250) + Mathf.Max(1, players) * Style.Px(52);
            return new Rect(Mathf.Round((Screen.width - w) / 2), Mathf.Round((Screen.height - h) / 2 + (1f - k) * Style.Px(12)), w, h);
        }

        static Rect GiveRect(Rect frame, int row)
        {
            return new Rect(frame.xMax - Style.Px(22 + 160), frame.y + Style.Px(170) + row * Style.Px(52), Style.Px(160), Style.Px(52) - Style.Px(16));
        }

        internal static Rect RecipientRect(int id)
        {
            int row = 0, count = 0;
            foreach (PlayerInfo pi in Session.Players.Values) if (!pi.Local) count++;
            foreach (PlayerInfo pi in Session.Players.Values) if (!pi.Local) { if (pi.Id == id) return GiveRect(Frame(count), row); row++; }
            return new Rect();
        }

        static void Note(string s, bool bad) { note = s; noteBad = bad; noteUntil = Time.realtimeSinceStartup + 4f; }

        static bool Button(Rect r, string label, bool accent, Event e, bool enabled = true)
        {
            bool hover = enabled && r.Contains(e.mousePosition);
            Color bg = !enabled ? new Color(1f, 1f, 1f, 0.04f) : accent ? (hover ? Style.Accent : Style.AccentDeep) : new Color(1f, 1f, 1f, hover ? 0.14f : 0.08f);
            Style.Round(r, r.height / 2, bg);
            Style.Text(r, label, 15, TextAnchor.MiddleCenter, !enabled ? Style.Dim : accent ? Style.OnAccent : Style.White);
            if (enabled && e.type == EventType.MouseDown && e.button == 0 && hover) { e.Use(); return true; }
            return false;
        }

        public static void Draw()
        {
            if (!Open) return;
            Event e = Event.current;
            float k = Mathf.Clamp01((Time.realtimeSinceStartup - openedAt) / 0.14f);
            Style.Alpha = 1f - (1f - k) * (1f - k);
            var players = new List<PlayerInfo>();
            foreach (PlayerInfo pi in Session.Players.Values) if (!pi.Local) players.Add(pi);
            float rowH = Style.Px(52), pad = Style.Px(22);
            Rect frame = Frame(players.Count);
            float x = frame.x, y = frame.y, w = frame.width, h = frame.height;
            Style.Glass(new Rect(x, y, w, h), Style.Px(20));
            Style.Title(new Rect(x + pad, y + Style.Px(12), w - 2 * pad, Style.Px(40)), Lang.T("PORTE-MONNAIE", "WALLET"), Style.White, TextAnchor.MiddleLeft, 26);
            Style.Text(new Rect(x + pad, y + Style.Px(12), w - 2 * pad, Style.Px(40)), Keys.Label(Keys.Wallet) + Lang.T(" / Échap : fermer", " / Esc: close"), 14, TextAnchor.MiddleRight, Style.Dim, false);
            // liquide
            float cy = y + Style.Px(62);
            Style.Text(new Rect(x + pad, cy, w - 2 * pad, Style.Px(30)), Wallet.Ready ? Lang.T("Liquide : ", "Cash: ") + Mathf.FloorToInt(Wallet.Cash) + " mk" : Wallet.GiftError(Wallet.GiftStatus.Loading), 20, TextAnchor.MiddleLeft, Wallet.Ready ? Style.Good : Style.Dim);
            // montant
            cy += Style.Px(44);
            Style.Text(new Rect(x + pad, cy, Style.Px(120), Style.Px(36)), Lang.T("Montant", "Amount"), 16, TextAnchor.MiddleLeft, Style.Dim);
            float gap = Style.Px(6), bx = x + pad + Style.Px(100), bw = Mathf.Floor((x + w - pad - bx - Style.Px(96) - 6 * gap) / 6);
            for (int i = 0; i < Steps.Length; i++)
            {
                if (i == 3) { Style.Text(new Rect(bx, cy, Style.Px(96), Style.Px(36)), amount + " mk", 20, TextAnchor.MiddleCenter, Style.White); bx += Style.Px(96) + gap; }
                if (Button(new Rect(bx, cy, bw, Style.Px(36)), (Steps[i] > 0 ? "+" : "") + Steps[i], false, e)) amount = Mathf.Clamp(amount + Steps[i], 1, 1000000);
                bx += bw + gap;
            }
            var amountRect = new Rect(x + pad, cy, w - 2 * pad, Style.Px(36));
            if (e.type == EventType.ScrollWheel && amountRect.Contains(e.mousePosition)) { amount = Mathf.Clamp(amount + (e.delta.y < 0 ? 10 : -10), 1, 1000000); e.Use(); }
            // joueurs
            cy += Style.Px(56);
            Style.Fill(new Rect(x + pad, cy - Style.Px(8), w - 2 * pad, Mathf.Max(1f, Style.Px(1))), Style.Line);
            if (players.Count == 0) Style.Text(new Rect(x + pad, cy, w - 2 * pad, rowH), Lang.T("Personne d'autre dans la partie", "Nobody else in the game"), 16, TextAnchor.MiddleLeft, Style.Dim, false);
            int row = 0;
            foreach (PlayerInfo pi in players)
            {
                bool inGame = pi.Level == 1;
                Wallet.GiftStatus status = Wallet.CheckGift(pi.Id, amount);
                Style.Text(new Rect(x + pad, cy, w - 2 * pad - Style.Px(170), rowH), pi.Name + (inGame ? "" : Lang.T("  (au menu)", "  (in menu)")), 18, TextAnchor.MiddleLeft, inGame ? Style.White : Style.Dim);
                Rect button = GiveRect(frame, row++);
                if (Button(button, Lang.T("DONNER ", "GIVE ") + amount, true, e, status == Wallet.GiftStatus.Available))
                {
                    string err = Wallet.Give(pi.Id, amount);
                    if (err == null) Note(amount + Lang.T(" mk donnés à ", " mk given to ") + pi.Name, false);
                    else Note(err, true);
                }
                else if (status != Wallet.GiftStatus.Available && e.type == EventType.MouseDown && e.button == 0 && button.Contains(e.mousePosition)) { Note(Wallet.GiftError(status), true); e.Use(); }
                cy += rowH;
            }
            if (note != null && Time.realtimeSinceStartup < noteUntil)
                Style.Text(new Rect(x + pad, y + h - Style.Px(46), w - 2 * pad, Style.Px(30)), note, 16, TextAnchor.MiddleLeft, noteBad ? Style.Warn : Style.Good, false);
            Style.Alpha = 1f;
        }

        // [Test] Autotest=don : a 30 s chacun note son liquide ; a 32 s l'hote donne 250 mk au premier invite ; a 36 s chacun
        // note de nouveau ; a 38 s l'hote ouvre le panneau (capture ecran-porte-monnaie a 39 s).
        static int testStep;
        public static void Test(string mode, float t)
        {
            if (mode != "don") return;
            if (t > 30f && testStep == 0) { testStep = 1; Log.Info("autotest : don, liquide avant " + Wallet.Cash); }
            if (t > 32f && testStep == 1) { testStep = 2; if (Session.IsHost) Log.Info("autotest : don, " + TestGive(250)); }
            if (t > 36f && testStep == 2) { testStep = 3; Log.Info("autotest : don, liquide apres " + Wallet.Cash); }
            if (t > 38f && testStep == 3) { testStep = 4; if (Session.IsHost) { Open = true; openedAt = 0; Autotest.CaptureSoon("porte-monnaie", 1f); } }
        }

        // Essais : don direct (sans l'interface).
        public static string TestGive(int amount)
        {
            foreach (PlayerInfo pi in Session.Players.Values) if (!pi.Local) { string err = Wallet.Give(pi.Id, amount); return err ?? "donne " + amount + " a " + pi.Name; }
            return "personne";
        }
    }
}
