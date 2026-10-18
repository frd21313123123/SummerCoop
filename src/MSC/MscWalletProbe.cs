using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;
namespace MWCoop
{
    // Explicit isolated QA: production N-key handler and real IMGUI button,
    // followed by two reliable transfers through the ordinary Jobs route.
    public static class MscWalletProbe
    {
        static readonly HashSet<int> steps = new HashSet<int>();
        static long clock;
        static bool installed, earlyOpened;
        static int pendingRecipient = -1;
        static float initial;
        static string channel;
        static string Role { get { return Session.IsHost ? "host" : "guest"; } }
        static bool At(int step, float t, float at) { return t >= at && steps.Add(step); }
        static void Record(string name, bool pass, string detail) { MscRegressionProbe.Record(channel, "wallet-" + name, pass, detail); }
        public static void Tick()
        {
            channel = MscRuntime.Argument("--summercoop-test-wallet-channel");
            if (channel.Length == 0 || !PlayerSync.InGame || Session.Players.Count != 2) return;
            foreach (PlayerInfo pi in Session.Players.Values) if (pi.Level != 1) return;
            if (!installed) {
                installed = true;
                new GameObject("SummerCoop wallet QA clicks").AddComponent<ClickDriver>();
            }
            if (!earlyOpened) {
                earlyOpened = true;
                Record("n-open", WalletPanel.HandleKey(Keys.Wallet) && WalletPanel.Open, "Key=" + Keys.Wallet + ";Ready=" + Wallet.Ready);
            }
            if (!Wallet.Ready) return;
            string ready = Path.Combine(channel, Role + "-ready.txt");
            if (!File.Exists(ready)) MscProbeFiles.WriteAllText(ready, "ready");
            string start = Path.Combine(channel, "start.txt");
            if (Session.IsHost && File.Exists(Path.Combine(channel, "guest-ready.txt")) && !File.Exists(start)) MscProbeFiles.WriteAllText(start, DateTime.UtcNow.AddSeconds(3).Ticks.ToString());
            if (clock == 0 && (!File.Exists(start) || !long.TryParse(MscProbeFiles.ReadAllText(start), out clock))) return;
            float t = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - clock).TotalSeconds;
            int other = Session.IsHost ? 1 : 0;
            if (At(0, t, 0)) {
                initial = Wallet.Cash;
                Record("available", Wallet.CheckGift(other, WalletPanel.Amount) == Wallet.GiftStatus.Available,
                    "Cash=" + initial + ";Amount=" + WalletPanel.Amount + ";BankPresent=" + (FsmVariables.GlobalVariables.FindFsmFloat("PlayerBankAccount") != null));
            }
            if (Session.IsHost && At(1, t, 3)) pendingRecipient = other;
            if (At(2, t, 7)) Record("host-to-guest", Mathf.Abs(Wallet.Cash - (initial + (Session.IsHost ? -100 : 100))) < .01f, "Before=" + initial + ";After=" + Wallet.Cash);
            if (!Session.IsHost && At(3, t, 9)) pendingRecipient = other;
            if (At(4, t, 13)) Record("guest-to-host", Mathf.Abs(Wallet.Cash - initial) < .01f, "Before=" + initial + ";After=" + Wallet.Cash);
            if (At(5, t, 15)) {
                float cash = Wallet.Cash;
                bool rejected = Wallet.Give(other, 0) != null && Wallet.Give(other, -1) != null && Wallet.Give(other, Wallet.MaxGift + 1) != null
                    && Wallet.Give(other, (int)Math.Floor(cash) + 1) != null && Wallet.Give(255, 1) != null;
                Record("reject", rejected && Wallet.Cash == cash, "Invalid, missing and unaffordable transfers preserve cash");
                WalletPanel.HandleKey(Keys.Wallet);
                Record("n-close", !WalletPanel.Open, "N closes wallet");
                typeof(WalletPanel).GetField("amount", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, Wallet.MaxGift);
                WalletPanel.HandleKey(Keys.Wallet);
                Record("amount-restored", WalletPanel.Open && WalletPanel.Amount <= Wallet.Cash && Wallet.CheckGift(other, WalletPanel.Amount) == Wallet.GiftStatus.Available,
                    "ReopenedAmount=" + WalletPanel.Amount + ";Cash=" + Wallet.Cash);
            }
            if (At(6, t, 17)) Application.CaptureScreenshot(Path.Combine(channel, Role + "-wallet.png"));
            if (At(7, t, 20)) WalletPanel.HandleKey(Keys.Wallet);
        }
        public sealed class ClickDriver : MonoBehaviour
        {
            void OnGUI()
            {
                if (pendingRecipient < 0 || Event.current.type != EventType.Layout) return;
                int recipient = pendingRecipient; pendingRecipient = -1;
                Event real = Event.current;
                try {
                    Event.current = new Event { type = EventType.MouseDown, button = 0, mousePosition = WalletPanel.RecipientRect(recipient).center };
                    float before = Wallet.Cash;
                    WalletPanel.Draw();
                    Record("click", Mathf.Abs(Wallet.Cash - (before - WalletPanel.Amount)) < .01f, "Recipient=" + recipient + ";Before=" + before + ";After=" + Wallet.Cash);
                } finally { Event.current = real; }
            }
        }
    }
}
