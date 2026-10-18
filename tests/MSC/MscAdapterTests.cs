using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MWCoop.Net;

class MscAdapterTests
{
    static int passed;
    static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
    static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Malformed packet accepted"); }
#if !MWCOOP_NETWORK_ONLY
    static byte[] CaseSnapshot(int revision, int[] ids, int[] kinds)
    {
        var w = new NetWriter(Msg.Home).I32(revision).U8(ids.Length);
        for (int i = 0; i < ids.Length; i++) w.I32(ids[i]).U8(kinds[i]).F32(i).F32(2).F32(3).F32(0).F32(0).F32(0).F32(1);
        return w.ToArray();
    }
    static MWCoop.MscToolCaseSnapshot ReadCases(byte[] bytes) { return MWCoop.MscToolCaseSnapshot.Read(new NetReader(bytes, 1, bytes.Length - 1)); }
#endif
    sealed class Simulated : ILink
    {
        public static readonly Dictionary<string, Simulated> Nodes = new Dictionary<string, Simulated>();
        public static float Time;
        static Random random = new Random(516750);
        sealed class Item { public string From; public byte[] Bytes; public float Due; }
        readonly List<Item> packets = new List<Item>();
        public string Id;
        public Simulated(string id) { Id = id; Nodes[id] = this; }
        public bool Send(object to, byte[] data, int length)
        {
            if (random.Next(100) < 5) return true;
            byte[] owned = new byte[length]; Buffer.BlockCopy(data, 0, owned, 0, length);
            Nodes[(string)to].packets.Add(new Item { From = Id, Bytes = owned, Due = Time + .15f + (float)random.NextDouble() * .08f }); return true;
        }
        public bool Receive(byte[] buffer, out int length, out object from)
        {
            for (int i = packets.Count - 1; i >= 0; i--) if (packets[i].Due <= Time) { Item item = packets[i]; packets.RemoveAt(i); from = item.From; length = item.Bytes.Length; Buffer.BlockCopy(item.Bytes, 0, buffer, 0, length); return true; }
            from = null; length = 0; return false;
        }
        public string KeyOf(object value) { return (string)value; }
        public string Describe(object value) { return (string)value; }
        public void Forget(object value) { }
        public void Close() { packets.Clear(); }
    }
    static Transport Create() { return new Transport { NetVersion = 1044 }; }
    static int Main(string[] args)
    {
        try
        {
#if !MWCOOP_NETWORK_ONLY
            Test("wallet transfer availability uses MSC cash without a bank and rejects unavailable recipients and invalid amounts", delegate {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                var cashField = typeof(MWCoop.Wallet).GetField("cash", flags);
                var bankField = typeof(MWCoop.Wallet).GetField("bank", flags);
                var readyField = typeof(MWCoop.Wallet).GetField("ready", flags);
                object savedCash = cashField.GetValue(null), savedBank = bankField.GetValue(null), savedReady = readyField.GetValue(null);
                bool savedActive = Session.Active;
                var savedPlayers = new Dictionary<int, PlayerInfo>(Session.Players);
                try {
                    Session.Active = true; Session.Players.Clear();
                    Session.Players[0] = new PlayerInfo { Id = 0, Local = true, Level = 1 };
                    Session.Players[1] = new PlayerInfo { Id = 1, Local = false, Level = 1 };
                    var cash = new HutongGames.PlayMaker.FsmFloat(500); cashField.SetValue(null, cash); bankField.SetValue(null, null);
                    readyField.SetValue(null, false);
                    Assert(MWCoop.Wallet.CheckGift(1, 100) == MWCoop.Wallet.GiftStatus.Loading, "Uninitialized wallet enabled the button");
                    readyField.SetValue(null, true);
                    Assert(MWCoop.Wallet.CheckGift(1, 100) == MWCoop.Wallet.GiftStatus.Available, "MSC cash requires a nonexistent bank");
                    Assert(MWCoop.Wallet.CheckGift(1, 501) == MWCoop.Wallet.GiftStatus.InsufficientCash, "Unavailable cash can be transferred");
                    foreach (int amount in new[] { -1, 0, 1000001 }) Assert(MWCoop.Wallet.CheckGift(1, amount) == MWCoop.Wallet.GiftStatus.InvalidAmount, "Invalid amount accepted");
                    Assert(MWCoop.Wallet.CheckGift(0, 100) == MWCoop.Wallet.GiftStatus.PlayerUnavailable && MWCoop.Wallet.CheckGift(7, 100) == MWCoop.Wallet.GiftStatus.PlayerUnavailable, "Self or missing recipient accepted");
                    Session.Players[1].Level = 0;
                    Assert(MWCoop.Wallet.CheckGift(1, 100) == MWCoop.Wallet.GiftStatus.PlayerUnavailable, "Menu recipient accepted");
                    Assert(cash.Value == 500, "Availability checks changed money");
                } finally {
                    cashField.SetValue(null, savedCash); bankField.SetValue(null, savedBank); readyField.SetValue(null, savedReady);
                    Session.Active = savedActive; Session.Players.Clear(); foreach (var player in savedPlayers) Session.Players.Add(player.Key, player.Value);
                }
            });
            Test("native order packets preserve Fleetari options and a full catalogue without native compression", delegate {
                var expected = new MWCoop.MscOrderMessage { Type = MWCoop.MscOrderMessage.Fleetari, Op = MWCoop.MscOrderMessage.Commit,
                    Ticket = 57, Actor = 1, Revision = 9, Seed = 516750, Active = true, Cost = 2500, Time = 1700, Gear = 4.11f,
                    Flags = (1UL << 31) | (1UL << 4), CarColor = new UnityEngine.Color(.1f, .2f, .3f, 1), Items = new string[47] };
                for (int i = 0; i < expected.Items.Length; i++) expected.Items[i] = "wheelset steelwide(Clone)";
                byte[] packet = expected.Pack();
                Assert(packet.Length + 8 <= Transport.MaxPayload, "Full native catalogue exceeds one reliable packet");
                var actual = MWCoop.MscOrderMessage.Read(packet);
                Assert(actual.Flags == expected.Flags && actual.Items.Length == 47 && actual.Items[46] == expected.Items[46], "Order products or service mask changed");
                Assert(actual.Gear == expected.Gear && actual.CarColor.b == expected.CarColor.b && actual.Seed == expected.Seed, "Repair settings/random seed changed");
                byte[] truncated = new byte[packet.Length - 1]; Array.Copy(packet, truncated, truncated.Length);
                Reject(delegate { MWCoop.MscOrderMessage.Read(truncated); });
                byte[] extra = new byte[packet.Length + 1]; Array.Copy(packet, extra, packet.Length);
                Reject(delegate { MWCoop.MscOrderMessage.Read(extra); });
                expected.Time = float.NaN; Reject(delegate { MWCoop.MscOrderMessage.Read(expected.Pack()); });
            });
            Test("MSC checkout and extraction preserve both bag tables and native identities within one UDP packet", delegate {
                var expected = new MWCoop.MscShopMessage { Op = MWCoop.MscShopMessage.Extract, Ticket = 9, Sequence = 4, Actor = 1,
                    Bag = "shoppingbagx123", Index = 13, Spray = true, Condition = 73.5f, Items = new int[18], Cans = new int[14], Counters = new int[44] };
                expected.Items[0] = 2; expected.Items[7] = 1; expected.Cans[13] = 3; expected.Counters[43] = 500;
                var writer = new NetWriter(Msg.Purchase); expected.Write(writer); byte[] bytes = writer.ToArray();
                Assert(bytes.Length + 2 < Transport.MaxPayload, "Native shop operation exceeds reliable UDP payload");
                var actual = MWCoop.MscShopMessage.Read(new NetReader(bytes, 1, bytes.Length - 1));
                Assert(actual.Items[0] == 2 && actual.Items[7] == 1 && actual.Cans[13] == 3 && actual.Counters[43] == 500, "Bag contents/counter changed in transit");
                Assert(actual.Index == 13 && actual.Spray && actual.Bag == expected.Bag && actual.Condition == 73.5f, "Selected product/freshness lost");
                var native = new MWCoop.MscShopMessage { Op = MWCoop.MscShopMessage.Extract, Ticket = 8, Sequence = 5, Bag = "shoppingbagx1", Spray = true, Index = 12, Items = new int[18], Cans = new int[13] };
                native.Cans[12] = 1; var nativeWriter = new NetWriter(Msg.Purchase); native.Write(nativeWriter); byte[] nativeBytes = nativeWriter.ToArray();
                Assert(MWCoop.MscShopMessage.Read(new NetReader(nativeBytes, 1, nativeBytes.Length - 1)).Cans.Length == 13, "MSC's real thirteen-slot spray list rejected");
                Assert(!MWCoop.MscShopMessage.AcceptSequence(4, 4) && !MWCoop.MscShopMessage.AcceptSequence(3, 4)
                    && MWCoop.MscShopMessage.AcceptSequence(5, 4), "Duplicate/reordered command can create a second item");
                byte[] truncated = new byte[bytes.Length - 1]; Array.Copy(bytes, truncated, truncated.Length);
                Reject(delegate { MWCoop.MscShopMessage.Read(new NetReader(truncated, 1, truncated.Length - 1)); });
                byte[] extra = new byte[bytes.Length + 1]; Array.Copy(bytes, extra, bytes.Length);
                Reject(delegate { MWCoop.MscShopMessage.Read(new NetReader(extra, 1, extra.Length - 1)); });
                expected.Index = 14; writer = new NetWriter(Msg.Purchase); expected.Write(writer); bytes = writer.ToArray();
                Reject(delegate { MWCoop.MscShopMessage.Read(new NetReader(bytes, 1, bytes.Length - 1)); });
                expected.Index = 13; expected.Items[0] = -1; writer = new NetWriter(Msg.Purchase); expected.Write(writer); bytes = writer.ToArray();
                Reject(delegate { MWCoop.MscShopMessage.Read(new NetReader(bytes, 1, bytes.Length - 1)); });
            });
            Test("tool case snapshots preserve both native types and deletion without reusing identities", delegate {
                var born = ReadCases(CaseSnapshot(3, new[] { 0, 1, 2 }, new[] { 0, 0, 1 }));
                Assert(born.Revision == 3 && born.Entries.Count == 3 && born.Entries[2].Kind == 1, "Ratchet type lost");
                var removed = ReadCases(CaseSnapshot(4, new[] { 0, 2 }, new[] { 0, 1 }));
                Assert(removed.Entries.Count == 2 && removed.Entries[1].Id == 2, "Deletion renamed surviving case");
                Assert(!MWCoop.MscToolCaseSnapshot.AcceptRevision(born.Revision, removed.Revision), "Delayed snapshot resurrects deleted case");
                Assert(!MWCoop.MscToolCaseSnapshot.AcceptRevision(4, 4) && MWCoop.MscToolCaseSnapshot.AcceptRevision(5, 4), "Duplicate/reordered snapshots accepted");
                Assert(MWCoop.MscToolCases.CanManage(true, true, true) && !MWCoop.MscToolCases.CanManage(true, false, true)
                    && !MWCoop.MscToolCases.CanManage(false, true, true) && !MWCoop.MscToolCases.CanManage(true, true, false), "Guest or unloaded game can manage cases");
            });
            Test("tool case snapshots reject forged counts, duplicate identities, missing original and invalid kinds", delegate {
                int[] maxIds = new int[MWCoop.MscToolCaseSnapshot.Limit], maxKinds = new int[maxIds.Length];
                for (int i = 0; i < maxIds.Length; i++) { maxIds[i] = i; maxKinds[i] = i % 2; }
                byte[] full = CaseSnapshot(1, maxIds, maxKinds);
                Assert(full.Length + 2 <= Transport.MaxPayload && ReadCases(full).Entries.Count == maxIds.Length, "Case limit overflows reliable UDP packet");
                Reject(delegate { ReadCases(CaseSnapshot(1, new int[maxIds.Length + 1], new int[maxIds.Length + 1])); });
                Reject(delegate { ReadCases(CaseSnapshot(1, new[] { 0, 0 }, new[] { 0, 0 })); });
                Reject(delegate { ReadCases(CaseSnapshot(1, new[] { 1 }, new[] { 0 })); });
                Reject(delegate { ReadCases(CaseSnapshot(1, new[] { 0 }, new[] { 1 })); });
                Reject(delegate { ReadCases(CaseSnapshot(1, new[] { 0, -1 }, new[] { 0, 0 })); });
                Reject(delegate { ReadCases(CaseSnapshot(1, new[] { 0, 1 }, new[] { 0, 2 })); });
                Reject(delegate { ReadCases(CaseSnapshot(0, new[] { 0 }, new[] { 0 })); });
                Reject(delegate { ReadCases(CaseSnapshot(1, new int[0], new int[0])); });
                byte[] good = CaseSnapshot(1, new[] { 0, 4 }, new[] { 0, 1 });
                byte[] shortPacket = new byte[good.Length - 1]; Array.Copy(good, shortPacket, shortPacket.Length);
                Reject(delegate { ReadCases(shortPacket); });
                byte[] extra = new byte[good.Length + 1]; Array.Copy(good, extra, good.Length);
                Reject(delegate { ReadCases(extra); });
            });
            Test("tool cases include host, grow for late joiners and reuse cases after reconnect", delegate {
                Assert(MWCoop.MscToolCases.RequiredTotal(1, 1) == 1, "Original host case duplicated");
                int total = MWCoop.MscToolCases.RequiredTotal(1, 2);
                Assert(total == 2 && MWCoop.MscToolCases.RequiredTotal(total, 2) == 2, "Join was not idempotent");
                total = MWCoop.MscToolCases.RequiredTotal(total, 4);
                Assert(total == 4 && MWCoop.MscToolCases.RequiredTotal(total, 2) == 4, "Leave deleted usable cases");
                Assert(MWCoop.MscToolCases.RequiredTotal(total, 4) == 4, "Reconnect duplicated cases");
                Assert(MWCoop.MscToolCases.Identity(0) != MWCoop.MscToolCases.Identity(1), "Case IDs collide");
                Assert(MWCoop.MscToolCases.Ready(new PlayerInfo { Local = true, Level = 1 }, 100), "Host was not counted");
                Assert(!MWCoop.MscToolCases.Ready(new PlayerInfo { Level = 0, StateTime = 100 }, 100), "Loading peer was counted");
                Assert(!MWCoop.MscToolCases.Ready(new PlayerInfo { Level = 1, StateTime = 80 }, 100), "Stale disconnected peer duplicated a reconnect case");
                Assert(MWCoop.MscToolCases.Ready(new PlayerInfo { Level = 1, StateTime = 99 }, 100), "Ready guest was not counted");
            });
            Test("MSC teleport aliases stay aligned after removing the MWC apartment", delegate {
                var find = typeof(MWCoop.Cheats).GetMethod("FindPlace", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Assert(find.Invoke(null, new object[] { "apartment" }) == null && find.Invoke(null, new object[] { "appartement" }) == null, "Apartment is still available");
                string[] aliases = { "home", "pub", "town", "harbor", "garage", "inspection", "landfill", "dance", "cottage", "cabin" };
                string[] keys = { "maison", "pub", "ville", "port", "garage", "inspection", "decharge", "danse", "cabane", "chalet" };
                for (int i = 0; i < aliases.Length; i++) {
                    var place = (MWCoop.Cheats.Place)find.Invoke(null, new object[] { aliases[i] });
                    Assert(place != null && place.Key == keys[i], "Wrong place for " + aliases[i]);
                }
                foreach (int hour in new[] { -1, 1, 9, 15, 23, 25 }) Assert(!MWCoop.Cheats.SetHour(hour), "Unsupported hour reached the native clock");
            });
            Test("in-game cheat permission persists and guests cannot override the host", delegate {
                string folder = Path.Combine(Path.GetTempPath(), "summercoop-cheats-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string previous = MWCoop.Config.IniPath;
                string oldSetting = MWCoop.Config.Get("Coop", "Triches", "0");
                bool active = Session.Active, host = Session.IsHost;
                var hostAllows = typeof(MWCoop.Cheats).GetField("hostAllows", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                bool oldAllowed = (bool)hostAllows.GetValue(null);
                try {
                    string path = Path.Combine(folder, "mwcoop.ini");
                    MWCoop.Config.IniPath = path;
                    File.WriteAllText(path, "[Coop]\nTriches=0\nPort=27842\n[Other]\nValue=keep\n");
                    Session.Active = Session.IsHost = false;
                    MWCoop.Config.Override("Coop", "Triches", "0");
                    Assert(!MWCoop.Cheats.Allowed, "Cheats unexpectedly enabled");
                    MWCoop.Cheats.SetAllowed(true);
                    string saved = File.ReadAllText(path);
                    Assert(MWCoop.Cheats.Allowed && saved.Contains("Triches=1"), "Enabling did not persist");
                    Assert(saved.Contains("Port=27842") && saved.Contains("Value=keep"), "Unrelated settings lost");
                    Session.Active = true;
                    MWCoop.Cheats.OnAllowed(false);
                    Assert(!MWCoop.Cheats.Allowed, "Guest used local setting instead of host permission");
                    MWCoop.Cheats.SetAllowed(true);
                    Assert(!MWCoop.Cheats.Allowed && File.ReadAllText(path) == saved, "Guest changed host permission or local INI");
                    MWCoop.Cheats.OnAllowed(true);
                    Assert(MWCoop.Cheats.Allowed, "Guest ignored host enabling cheats");
                    MWCoop.Cheats.SetAllowed(false);
                    Assert(MWCoop.Cheats.Allowed && File.ReadAllText(path) == saved, "Guest disabled host permission");
                    MWCoop.Cheats.OnAllowed(false);
                    Assert(!MWCoop.Cheats.Allowed, "Guest ignored host disabling cheats");
                    Session.Active = false;
                    MWCoop.Cheats.SetAllowed(false);
                    Assert(!MWCoop.Cheats.Allowed && File.ReadAllText(path).Contains("Triches=0"), "Disabling did not persist");
                    Assert(MWCoop.LangRu.Table["Off: F10 > CHEATS > Allow cheats."].Contains("F10 > ЧИТЫ"), "Russian F7 hint does not point to the in-game setting");
                } finally {
                    Session.Active = active; Session.IsHost = host;
                    hostAllows.SetValue(null, oldAllowed);
                    MWCoop.Config.Override("Coop", "Triches", oldSetting);
                    MWCoop.Config.IniPath = previous;
                    Directory.Delete(folder, true);
                }
            });
            Test("launcher customization reload keeps connection and world settings", delegate {
                string folder = Path.Combine(Path.GetTempPath(), "summercoop-looks-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "mwcoop.ini");
                string previous = MWCoop.Config.IniPath;
                try {
                    MWCoop.Config.IniPath = path;
                    MWCoop.Config.Override("Coop", "Port", "27842");
                    MWCoop.Config.Override("Coop", "Profil", "host");
                    File.WriteAllText(path, "[Coop]\nApparence=char_shirt02\nPantalon=char_pants03\nVisage=char_face01\nCorps=1712-37\nChapeau=cap\nLunettes=glasses\nCheveux=hair\nCouleurVoiture=123ABC\nPort=12345\nProfil=guest\n");
                    Assert(MWCoop.MscLauncher.ReloadCustomization(path), "Launcher changes were ignored");
                    Assert(MWCoop.Config.Get("Coop", "Apparence", "") == "char_shirt02" && MWCoop.Config.Get("Coop", "Pantalon", "") == "char_pants03", "Clothes lost");
                    Assert(MWCoop.Config.Get("Coop", "Chapeau", "") == "cap" && MWCoop.Config.Get("Coop", "CouleurVoiture", "") == "123ABC", "Accessories or color lost");
                    Assert(MWCoop.Config.Get("Coop", "Port", "") == "27842" && MWCoop.Config.Get("Coop", "Profil", "") == "host", "Active session settings changed");
                    Assert(!MWCoop.MscLauncher.ReloadCustomization(path), "Unchanged customization reported a change");
                    File.WriteAllText(path, "[Coop]\nChapeau=\nCouleurVoiture=\n[Other]\nApparence=wrong\n");
                    Assert(MWCoop.MscLauncher.ReloadCustomization(path), "Removing hat or random color ignored");
                    Assert(MWCoop.Config.Get("Coop", "Chapeau", "none") == "none" && MWCoop.Config.Get("Coop", "CouleurVoiture", "random") == "random", "Empty selections lost");
                    Assert(MWCoop.Config.Get("Coop", "Apparence", "") == "char_shirt02", "Other INI section replaced outfit");
                    MWCoop.Config.Save("Coop", "Apparence", "char_shirt03");
                    Assert(File.ReadAllText(path).Contains("Apparence=char_shirt03"), "F10 changes not saved to shared config");
                } finally { MWCoop.Config.IniPath = previous; Directory.Delete(folder, true); }
            });
            Test("launcher and isolated profile share settings and preview paths", delegate {
                string folder = Path.Combine(Path.GetTempPath(), "summercoop-launcher-" + Guid.NewGuid().ToString("N"));
                string profile = Path.Combine(folder, "profile");
                string game = Path.Combine(folder, "game");
                string[] names = { "MWCOOP_CONFIG", "MWCOOP_LAUNCHER_DIR", "MWCOOP_DIR", "MWCOOP_DATA" };
                var saved = new Dictionary<string, string>();
                foreach (string name in names) saved[name] = Environment.GetEnvironmentVariable(name);
                string ini = MWCoop.Config.IniPath;
                try {
                    Directory.CreateDirectory(profile);
                    File.WriteAllText(Path.Combine(profile, "mwcoop.ini"), "[Coop]\nApparence=char_shirt04\nPantalon=char_pants02\n");
                    Environment.SetEnvironmentVariable("MWCOOP_DIR", profile);
                    Environment.SetEnvironmentVariable("MWCOOP_DATA", profile);
                    MWCoop.MscLauncher.Initialize(game, profile, Path.Combine(folder, "missing.ini"));
                    MWCoop.Config.Load();
                    string shared = Path.Combine(game, "MWCoop");
                    Assert(MWCoop.Config.IniPath == Path.Combine(shared, "mwcoop.ini"), "Config stayed in isolated profile");
                    Assert(MWCoop.Config.Get("Coop", "Apparence", "") == "char_shirt04", "Existing appearance was not migrated");
                    Assert(Path.GetDirectoryName(MWCoop.Looks.PreviewPath) == Path.Combine(shared, "cache") && Path.GetDirectoryName(MWCoop.CarColor.MeshPath) == Path.Combine(shared, "cache"), "Previews are invisible to launcher");
                    Assert(Environment.GetEnvironmentVariable("MWCOOP_DATA") == profile && Environment.GetEnvironmentVariable("MWCOOP_DIR") == profile, "World data isolation changed");
                    MWCoop.Config.Save("Coop", "Apparence", "char_shirt05");
                    MWCoop.MscLauncher.Initialize(game, profile, Path.Combine(folder, "missing.ini"));
                    Assert(File.ReadAllText(Path.Combine(shared, "mwcoop.ini")).Contains("char_shirt05"), "Migration overwrote launcher selection");
                    File.WriteAllText(Path.Combine(profile, "mwcoop.ini"), "[Test]\nProfileOnly=kept\n[Coop]\nApparence=old\n");
                    MWCoop.Config.Load();
                    Assert(MWCoop.Config.Get("Test", "ProfileOnly", "") == "kept" && MWCoop.Config.Get("Coop", "Apparence", "") == "char_shirt05", "Profile fallback or shared config priority changed");
                    string request = Path.Combine(folder, "lancement.ini");
                    string original = Path.Combine(folder, "Исходная игра с пробелами");
                    long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                    File.WriteAllText(request, "[Lancement]\nHorodatage=" + now + "\nLauncherDir=" + original + "\n");
                    MWCoop.MscLauncher.Initialize(game, profile, request);
                    Assert(Environment.GetEnvironmentVariable("MWCOOP_LAUNCHER_DIR") == original, "Launch copy lost original Unicode directory");
                    File.WriteAllText(request, "[Lancement]\nHorodatage=1\nLauncherDir=" + original + "\n");
                    MWCoop.MscLauncher.Initialize(game, profile, request);
                    Assert(Environment.GetEnvironmentVariable("MWCOOP_LAUNCHER_DIR") == shared, "Expired launch request changed shared settings directory");
                } finally {
                    foreach (string name in names) Environment.SetEnvironmentVariable(name, saved[name]);
                    MWCoop.Config.IniPath = ini;
                    Directory.Delete(folder, true);
                }
            });
            Test("MSC assembly loads MWCoop runtime without the legacy SummerCoop runtime", delegate {
                var assembly = typeof(MWCoop.MscRuntime).Assembly;
                Assert(assembly.GetType("MWCoop.Core") != null && assembly.GetType("SummerCoop.Game.SummerCoopMod") != null, "New runtime missing");
                Assert(assembly.GetType("SummerCoop.Game.CoopRuntime") == null, "Two multiplayer runtimes would run");
                Assert(Session.NetVersion != 41 && MWCoop.Version.Text == SummerCoop.Core.Protocol.ModVersion, "Build identities disagree");
            });
            Test("Avatar body cache follows profile replacements without sharing mutable looks", delegate {
                var player = new PlayerInfo { Skin = "shirt|pants|face|1712-37|hat" };
                Assert(player.BodyKey == "1712-37" && player.BodyKey == "1712-37", "Body cache not populated");
                var look = MWCoop.Looks.Parse(player.Skin); look.Body = "changed";
                Assert(player.BodyKey == "1712-37", "Mutable look changed a cached profile");
                player.Skin = "shirt|pants|face|other"; Assert(player.BodyKey == "other", "Profile replacement kept old body");
                player.Skin = "shirt"; Assert(player.BodyKey == "", "Legacy shirt-only profile kept body");
            });
            Test("Preview float writer preserves BinaryWriter file bytes", delegate {
                var expected = new MemoryStream(); var actual = new MemoryStream();
                using (var baseline = new BinaryWriter(expected))
                using (var candidate = new MWCoop.MscPreviewWriter(actual)) {
                    var random = new Random(1046);
                    for (int i = 0; i < 1000; i++) {
                        byte[] bits = new byte[8]; random.NextBytes(bits);
                        float single = BitConverter.ToSingle(bits, 0); double number = BitConverter.ToDouble(bits, 0);
                        baseline.Write(single); baseline.Write(number); candidate.Write(single); candidate.Write(number);
                    }
                }
                Assert(Convert.ToBase64String(expected.ToArray()) == Convert.ToBase64String(actual.ToArray()), "Preview format changed");
            });
#endif
            Test("MWCoop codec retains Cyrillic, exact numbers and bounded owned slices", delegate {
                byte[] bytes = new NetWriter(Msg.Chat).Str("Привет, MSC 🚗").I32(123).F32(1.25f).Bool(true).Bytes(new byte[] { 4, 5, 6 }, 1, 2).ToArray();
                NetReader reader = new NetReader(bytes, 1, bytes.Length - 1);
                Assert(reader.Str() == "Привет, MSC 🚗" && reader.I32() == 123 && reader.F32() == 1.25f && reader.Bool(), "Codec changed values");
                Assert(reader.Bytes()[1] == 6 && !reader.More, "Byte slice changed");
            });
            Test("Direct MWCoop packets match BinaryWriter bytes including nonfinite sentinels and exact UTF8", delegate {
                var random = new Random(1046);
                for (int trial = 0; trial < 300; trial++) {
                    byte[] bits = new byte[8]; random.NextBytes(bits);
                    float single = BitConverter.ToSingle(bits, 0); double number = BitConverter.ToDouble(bits, 0);
                    string text = trial % 3 == 0 ? "Деталь/🚗/\u0000/" + trial : new string('x', trial * 7);
                    byte[] encoded = System.Text.Encoding.UTF8.GetBytes(text);
                    var expected = new MemoryStream();
                    using (var writer = new BinaryWriter(expected)) {
                        writer.Write((byte)Msg.Chat); writer.Write((byte)trial); writer.Write((ushort)(trial * 413)); writer.Write(-trial);
                        writer.Write(single); writer.Write(number); writer.Write((ushort)encoded.Length); writer.Write(encoded);
                        byte[] actual = new NetWriter(Msg.Chat).U8(trial).U16(trial * 413).I32(-trial).F32(single).F64(number).Str(text).ToArray();
                        Assert(Convert.ToBase64String(expected.ToArray()) == Convert.ToBase64String(actual), "Packet bytes changed at trial " + trial);
                    }
                }
            });
            Test("Pooled writers retain owned packets through nested use, reuse and exceptions", delegate {
                byte[] first; byte[] second;
                using (var outer = NetWriter.Rent(Msg.Prop)) {
                    outer.Str("original").F32(1.25f); first = outer.ToArray();
                    using (var inner = NetWriter.Rent(Msg.Chat)) inner.Str(new string('y', 8000)).ToArray();
                    second = outer.ToArray(); Assert(!object.ReferenceEquals(first, second), "ToArray did not return an owned copy");
                    first[0] = 255; Assert(second[0] == (byte)Msg.Prop, "Copies alias each other");
                    Reject(delegate { outer.Str(new string('x', 16385)); }); outer.I32(42);
                    Assert(outer.Length == second.Length + 4, "Failed write corrupted cursor");
                }
                for (int i = 0; i < 100; i++) using (var writer = NetWriter.Rent(Msg.Chat)) writer.Str("replacement").I32(i).ToArray();
                Assert(new NetReader(second, 1, second.Length - 1).Str() == "original", "Pool reuse overwrote a pending packet");
            });
            Test("MWCoop codec rejects invalid ranges, truncation and UTF8", delegate {
                Reject(delegate { new NetReader(new byte[8], int.MaxValue, 4); }); Reject(delegate { new NetReader(new byte[8], 0, -1); });
                Reject(delegate { new NetReader(new byte[] { 3, 0, 65 }, 0, 3).Str(); }); Reject(delegate { new NetReader(new byte[] { 1, 0, 255 }, 0, 3).Str(); });
                Reject(delegate { new NetWriter(Msg.Chat).Str(new string('x', 16385)); }); Reject(delegate { new NetWriter(Msg.Chat).Bytes(new byte[4], 0, 65536); });
            });
            Test("MWCoop rejects NaN, infinity and invalid booleans before game state changes", delegate {
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity }) Reject(delegate { new NetReader(BitConverter.GetBytes(value), 0, 4).F32(); });
                Reject(delegate { new NetReader(BitConverter.GetBytes(double.NaN), 0, 8).F64(); }); Reject(delegate { new NetReader(new byte[] { 2 }, 0, 1).Bool(); });
            });
#if !MWCOOP_NETWORK_ONLY
            Test("MSC edits reject duplicate/reordered requests and follow the host's canonical revision", delegate {
                var hostClock = new MWCoop.MscEditClock();
                float hostValue = 0;
                int hostRevision = 0;
                foreach (int sequence in new[] { 3, 1, 2, 3 })
                    if (hostClock.AcceptRequest(1, sequence)) { hostValue = sequence * .3f; hostRevision = hostClock.Publish(true); }
                Assert(Math.Abs(hostValue - .9f) < .0001f && hostRevision == 1, "Older actor packet overwrote the final adjustment");
                Assert(hostClock.AcceptRequest(2, 1), "A different player's first edit was rejected");
                hostValue = .6f; hostRevision = hostClock.Publish(true);
                var observer = new MWCoop.MscEditClock(); float observed = 0;
                if (observer.AcceptSnapshot(hostRevision)) observed = hostValue;
                if (observer.AcceptSnapshot(1)) observed = .9f;
                if (observer.AcceptSnapshot(hostRevision)) observed = 5;
                Assert(observed == .6f, "Duplicate or stale host snapshot overwrote the latest value");
                var lateJoiner = new MWCoop.MscEditClock();
                Assert(lateJoiner.AcceptSnapshot(hostRevision), "Late joiner rejected the current host snapshot");
                Assert(!hostClock.AcceptRequest(2, 0) && !lateJoiner.AcceptSnapshot(0), "Unversioned edits were accepted");
                hostClock.ForgetActor(2);
                Assert(hostClock.AcceptRequest(2, 1) && hostClock.HostRevision == hostRevision, "A rejoining player's reset request sequence was rejected or changed the host revision");
            });
            Test("an old host repair cannot cancel native assembly or acknowledge a newer local removal", delegate {
                var pending = new MWCoop.MscPendingPartEdit();
                pending.Begin();
                Assert(!pending.PermitSnapshot(false, 1), "Old removal cancelled native assembly before publication");
                pending.Sent(2);
                Assert(!pending.PermitSnapshot(true, 1) && !pending.PermitSnapshot(false, 8), "Unacknowledged assembly was replaced");
                pending.Begin(); pending.Sent(3);
                Assert(!pending.PermitSnapshot(true, 2), "Assembly reply acknowledged a later local removal");
                Assert(pending.PermitSnapshot(true, 3) && pending.PermitSnapshot(false, 9), "Latest acknowledgement did not restore host authority");
                pending.Begin(); pending.Cancel();
                Assert(pending.PermitSnapshot(false, 10), "Failed native action blocked host repair");
            });
            Test("bolt repair waits for native storage and the acknowledgement of the latest guest turn", delegate {
                var pending = new MWCoop.MscPendingBoltEdit();
                pending.Begin();
                Assert(!pending.PermitSnapshot(false, 0), "Host repair erased an unpublished bolt turn");
                pending.Sent(3);
                Assert(!pending.PermitSnapshot(false, 0) && !pending.PermitSnapshot(true, 2), "Old snapshot acknowledged a new turn");
                pending.Begin();
                Assert(!pending.PermitSnapshot(true, 3), "Earlier echo interrupted the next native turn");
                pending.Sent(4);
                Assert(!pending.PermitSnapshot(true, 3), "Earlier echo erased the latest published turn");
                Assert(pending.PermitSnapshot(true, 4) && pending.PermitSnapshot(false, 0), "Matching echo did not restore host authority");
            });
            Test("a shared suspension total waits for every locally edited bolt before accepting a repair", delegate {
                var total = new MWCoop.MscPendingBoltEdit();
                total.Begin(); total.Begin(); total.Sent(10);
                Assert(!total.PermitSnapshot(true, 10), "First bolt echo erased another bolt's unpublished tightness");
                total.Sent(11);
                Assert(!total.PermitSnapshot(true, 10) && !total.PermitSnapshot(false, 100), "Unacknowledged suspension tightness was replaced");
                Assert(total.PermitSnapshot(true, 11), "Latest total was not acknowledged");
                total.Begin(); total.Sent(13); total.Sent(12);
                Assert(!total.PermitSnapshot(true, 12) && total.PermitSnapshot(true, 13), "Publication order lowered the required acknowledgement");
            });
            Test("MSC policy excludes winter terrain, MWC shops and native DXR while retaining major coop modules", delegate {
                foreach (Type type in new[] { typeof(MWCoop.Clearing), typeof(MWCoop.Kauppa), typeof(MWCoop.Frost), typeof(MWCoop.RayTracing) }) Assert(!MWCoop.MscRuntime.Supports(type), "Winter-only module enabled: " + type);
                foreach (Type type in new[] { typeof(MWCoop.PlayerSync), typeof(MWCoop.VehicleSync), typeof(MWCoop.Parts), typeof(MWCoop.Chat), typeof(MWCoop.WorldFsms), typeof(MWCoop.Wallet), typeof(SaveTransfer) }) Assert(MWCoop.MscRuntime.Supports(type), "Core MWCoop module removed: " + type);
            });
            Test("MSC save waits for Hello before a transport-accepted peer can queue a manifest", delegate {
                var auth = (HashSet<int>)typeof(Session).GetField("authenticated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                var waiting = (System.Collections.IList)typeof(SaveTransfer).GetField("waiting", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                var outgoing = (System.Collections.IList)typeof(SaveTransfer).GetField("outgoing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                var eligible = typeof(Session).GetMethod("AuthenticatedForSave", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Peer peer = new Peer { Id = 222, Accepted = true }; int queued = waiting.Count, active = outgoing.Count;
                Assert(!auth.Contains(peer.Id), "Test peer identity is already authenticated");
                SaveTransfer.Queue(peer); SaveTransfer.SendTo(peer);
                Assert(waiting.Count == queued && outgoing.Count == active && !(bool)eligible.Invoke(null, new object[] { peer }), "Save began before Hello completed");
                try { auth.Add(peer.Id); Assert((bool)eligible.Invoke(null, new object[] { peer }), "Completed Hello cannot enable a save"); peer.Accepted = false; Assert(!(bool)eligible.Invoke(null, new object[] { peer }), "Closed peer remains eligible for a save"); }
                finally { auth.Remove(peer.Id); }
            });
            Test("MSC save coalesces a late Hello while that peer's transfer is still outgoing", delegate {
                var auth = (HashSet<int>)typeof(Session).GetField("authenticated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                var waiting = (System.Collections.IList)typeof(SaveTransfer).GetField("waiting", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                var outgoing = (System.Collections.IList)typeof(SaveTransfer).GetField("outgoing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                Type type = typeof(SaveTransfer).GetNestedType("Outgoing", System.Reflection.BindingFlags.NonPublic);
                object item = Activator.CreateInstance(type, true); Peer peer = new Peer { Id = 222, Accepted = true };
                type.GetField("P").SetValue(item, peer); int queued = waiting.Count;
                try { auth.Add(peer.Id); outgoing.Add(item); SaveTransfer.Queue(peer); SaveTransfer.SendTo(peer); Assert(waiting.Count == queued && outgoing.Count == 1, "Late Hello enqueued a second save manifest"); }
                finally { outgoing.Remove(item); auth.Remove(peer.Id); }
            });
            Test("Failed MSC host or guest connection cannot fall through into offline GAME", delegate {
                var policy = typeof(MWCoop.MscFlow).GetMethod("MayStartOffline", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                foreach (string mode in new[] { "host", "client", "hote", "invite", "" }) Assert(!(bool)policy.Invoke(null, new object[] { mode, true, true }), "Failed connection started offline: " + mode);
                Assert((bool)policy.Invoke(null, new object[] { "solo", true, false }) && (bool)policy.Invoke(null, new object[] { "solo", false, true }) && !(bool)policy.Invoke(null, new object[] { "solo", false, false }), "Explicit offline start policy changed");
            });
            Test("Native QA clocks and snapshots remain complete during concurrent atomic publication", delegate {
                Type type = typeof(MWCoop.MscRuntime).Assembly.GetType("MWCoop.MscProbeFiles", true);
                var write = type.GetMethod("WriteAllText"); var read = type.GetMethod("ReadAllText");
                string directory = Path.Combine(Path.GetTempPath(), "summercoop-probe-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "clock.txt"), body = new string('x', 8192); Exception failure = null; Thread writer = null;
                try {
                    write.Invoke(null, new object[] { path, "0|" + body + "|0" });
                    writer = new Thread(delegate() { try { for (int i = 1; i <= 60; i++) write.Invoke(null, new object[] { path, i + "|" + body + "|" + i }); } catch (Exception e) { failure = e; } });
                    writer.Start(); int samples = 0;
                    do { string[] fields = ((string)read.Invoke(null, new object[] { path })).Split('|'); Assert(fields.Length == 3 && fields[0] == fields[2] && fields[1] == body, "Reader observed an incomplete clock/snapshot"); samples++; } while (writer.IsAlive);
                    writer.Join(); Assert(failure == null && samples > 0, "Concurrent native QA publication failed: " + failure);
                    type.GetMethod("WriteAllLines").Invoke(null, new object[] { path, new[] { "кириллица", "second" } });
                    var lines = (string[])type.GetMethod("ReadAllLines").Invoke(null, new object[] { path });
                    Assert(lines.Length == 2 && lines[0] == "кириллица" && lines[1] == "second", "QA line encoding changed");
                } finally {
                    if (writer != null) writer.Join();
                    foreach (string suffix in new[] { "", ".pending", ".bak" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
                    Directory.Delete(directory);
                }
            });
            Test("MSC mount adapter preserves variant HandChild slots and reads native Part parenting actions", delegate {
                var names = new[] { new HutongGames.PlayMaker.FsmString { Name = "HandChild2", Value = "starter(Clone)" }, new HutongGames.PlayMaker.FsmString { Name = "Other", Value = "block(Clone)" } };
                Assert(SummerCoop.Game.NativeAssembly.Accepts(names, "starter(Clone)") && !SummerCoop.Game.NativeAssembly.Accepts(names, "block(Clone)"), "MSC mount compatibility changed");
                var action = new HutongGames.PlayMaker.Actions.SetParent { gameObject = new HutongGames.PlayMaker.FsmOwnerDefault { OwnerOption = HutongGames.PlayMaker.OwnerDefaultOption.SpecifyGameObject, GameObject = new HutongGames.PlayMaker.FsmGameObject { Name = "Part" } } };
                var actions = new HutongGames.PlayMaker.FsmStateAction[] { null, action };
                Assert(object.ReferenceEquals(SummerCoop.Game.NativeAssembly.MountAction(actions), action), "Native Part parent was ignored");
                Assert(object.ReferenceEquals(SummerCoop.Game.NativeAssembly.MountAction((IEnumerable<HutongGames.PlayMaker.FsmStateAction>)actions), action), "Array mount selection differs from the original sequence");
                action.gameObject.GameObject.Name = "Other";
                Assert(SummerCoop.Game.NativeAssembly.MountAction(actions) == null, "Mount selection cached a changed native action");
            });
            Test("MSC indexed native offsets preserve sequential float values and live variables", delegate {
                var owner = new HutongGames.PlayMaker.FsmOwnerDefault { OwnerOption = HutongGames.PlayMaker.OwnerDefaultOption.SpecifyGameObject, GameObject = new HutongGames.PlayMaker.FsmGameObject { Name = "Part" } };
                var position = new HutongGames.PlayMaker.Actions.SetPosition {
                    gameObject = owner, space = UnityEngine.Space.Self,
                    vector = new HutongGames.PlayMaker.FsmVector3(), x = new HutongGames.PlayMaker.FsmFloat(),
                    y = new HutongGames.PlayMaker.FsmFloat { UseVariable = true, Name = "" }, z = new HutongGames.PlayMaker.FsmFloat { UseVariable = true, Name = "" }
                };
                var actions = new HutongGames.PlayMaker.FsmStateAction[] { null, position };
                for (int i = 0; i < 300; i++) {
                    position.vector.Value = new UnityEngine.Vector3(i / 13f, -i / 7f, i / 19f); position.x.Value = i / -17f;
                    UnityEngine.Vector3 input = new UnityEngine.Vector3(i / 31f, 4, -2);
                    var indexed = SummerCoop.Game.NativeAssembly.LocalOffset(actions, input);
                    var original = SummerCoop.Game.NativeAssembly.LocalOffset((IEnumerable<HutongGames.PlayMaker.FsmStateAction>)actions, input);
                    Assert(indexed.x == original.x && indexed.y == original.y && indexed.z == original.z, "Native mount offset changed float values");
                }
                position.space = UnityEngine.Space.World;
                var untouched = SummerCoop.Game.NativeAssembly.LocalOffset(actions, new UnityEngine.Vector3(1,2,3));
                Assert(untouched.x == 1 && untouched.y == 2 && untouched.z == 3, "World-space action changed a local mount offset");
            });
            Test("MSC respawn offers only the church and parents' house", delegate {
                var destinations = (Array)typeof(MWCoop.Respawn).GetField("places", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
                Assert(destinations.Length == 2, "Unexpected respawn destinations");
                string first = (string)destinations.GetValue(0).GetType().GetField("NameEn").GetValue(destinations.GetValue(0));
                string second = (string)destinations.GetValue(1).GetType().GetField("NameEn").GetValue(destinations.GetValue(1));
                Assert(first == "the church" && second == "the parents' house", "MWC apartment respawn survived");
            });
#endif
            Test("standing player support poses retain local feet, head and yaw and detach without stale attachment data", delegate {
                var pose = new SupportPose { Vehicle = 5, Feet = new UnityEngine.Vector3(.2f, 1.4f, -2.3f), Head = new UnityEngine.Vector3(.3f, 3f, -2.2f), Yaw = -45f };
                var writer = new NetWriter(Msg.PlayerState); pose.Write(writer);
                byte[] bytes = writer.ToArray();
                var decoded = SupportPose.Read(new NetReader(bytes, 1, bytes.Length - 1));
                Assert(decoded.Attached && decoded.VehicleIndex == 4 && decoded.Feet.x == .2f && decoded.Head.y == 3f && decoded.Yaw == -45f, "Relative player pose changed on the wire");
                writer = new NetWriter(Msg.PlayerState); new SupportPose().Write(writer); bytes = writer.ToArray();
                decoded = SupportPose.Read(new NetReader(bytes, 1, bytes.Length - 1));
                Assert(!decoded.Attached && decoded.VehicleIndex == -1 && bytes.Length == 2, "Leaving a platform retained the old vehicle or coordinates");
            });
            Test("kilju snapshots retain persistent recipe values, fermentation progress and lid state", delegate {
                var recipe = new BrewState { Sugar = 6, Yeast = 1, Water = 30, Alcohol = .14f, Sweetness = .2f, Time = 681, Vinegar = .0004f, Finished = true, LidOn = true };
                var writer = new NetWriter(Msg.Home); recipe.Write(writer); byte[] bytes = writer.ToArray();
                var reader = new NetReader(bytes, 1, bytes.Length - 1); BrewState decoded = BrewState.Read(reader);
                Assert(!reader.More && recipe.Equals(decoded), "Persistent kilju contents changed on the wire");
                writer = new NetWriter(Msg.Home); new BrewState().Write(writer); bytes = writer.ToArray();
                decoded = BrewState.Read(new NetReader(bytes, 1, bytes.Length - 1));
                Assert(decoded.Water == 0 && decoded.Sugar == 0 && !decoded.Finished && !decoded.LidOn, "An emptied bucket retained the previous recipe");
            });
            Test("invalid kilju quantities, nonfinite quality, truncated frames and invalid flags are rejected", delegate {
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, -1, 100 })
                    Reject(delegate { new BrewState { Water = value }.Write(new NetWriter(Msg.Home)); });
                Reject(delegate { new BrewState { Alcohol = .5f }.Write(new NetWriter(Msg.Home)); });
                Reject(delegate { BrewState.Read(new NetReader(new byte[27], 0, 27)); });
                byte[] bad = new byte[30]; bad[28] = 2;
                Reject(delegate { BrewState.Read(new NetReader(bad, 0, bad.Length)); });
                bad = new byte[30]; Buffer.BlockCopy(BitConverter.GetBytes(float.NaN), 0, bad, 0, 4);
                Reject(delegate { BrewState.Read(new NetReader(bad, 0, bad.Length)); });
            });
            Test("invalid, truncated and excessive player support poses are refused before applying an avatar", delegate {
                Reject(delegate { new SupportPose { Vehicle = 256 }.Write(new NetWriter(Msg.PlayerState)); });
                Reject(delegate { new SupportPose { Vehicle = -1 }.Write(new NetWriter(Msg.PlayerState)); });
                Reject(delegate { new SupportPose { Vehicle = 1, Feet = new UnityEngine.Vector3(33f, 0, 0) }.Write(new NetWriter(Msg.PlayerState)); });
                Reject(delegate { SupportPose.Read(new NetReader(new byte[] { 1 }, 0, 1)); });
                Reject(delegate { SupportPose.Read(new NetReader(new byte[] { 0, 1 }, 0, 2)); });
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, 33f }) {
                    byte[] bad = new NetWriter(Msg.PlayerState).U8(1).Vec(new UnityEngine.Vector3(value, 0, 0)).Vec(new UnityEngine.Vector3()).F32(0).ToArray();
                    Reject(delegate { SupportPose.Read(new NetReader(bad, 1, bad.Length - 1)); });
                }
            });
            Test("moving MSC vehicles without cabin heat survive the production motion decoder", delegate {
                foreach (int mode in new[] { 1, 2 })
                {
                    var writer = new NetWriter(Msg.Vehicle).U8(1).U8(0).U8(mode)
                        .Vec(new UnityEngine.Vector3(10f, 2f, 30f)).Quat(new UnityEngine.Quaternion(0f, 0f, 0f, 1f))
                        .Vec(new UnityEngine.Vector3(3f, 0f, 0f)).Vec(new UnityEngine.Vector3())
                        .F32(1400f).F32(.5f).F32(.1f).F32(float.NaN).U16(0);
                    if (mode == 1) writer.Vec(new UnityEngine.Vector3(0f, 1f, 0f));
                    byte[] packet = writer.F32(12f).ToArray();
                    var reader = new NetReader(packet, 4, packet.Length - 4);
                    VehicleSnapshot state = VehicleSnapshot.Read(reader, mode);
                    Assert(!reader.More && float.IsNaN(state.Heat) && state.Rpm == 1400f && state.Position.x == 10f && state.Velocity.x == 3f && state.Timestamp == 12f, "Driving packet lost motion or optional heat");
                }
                // Tow.SendCar deliberately has no timestamp or heater.
                byte[] tow = new NetWriter(Msg.Vehicle).Vec(new UnityEngine.Vector3(2f, 0f, 0f))
                    .Quat(new UnityEngine.Quaternion(0f, 0f, 0f, 1f)).Vec(new UnityEngine.Vector3()).Vec(new UnityEngine.Vector3())
                    .F32(0f).F32(0f).F32(0f).F32(float.NaN).U16(0).ToArray();
                VehicleSnapshot towed = VehicleSnapshot.Read(new NetReader(tow, 1, tow.Length - 1), 2);
                Assert(towed.Timestamp == -1f && float.IsNaN(towed.Heat) && towed.Position.x == 2f, "Upstream towing packet no longer decodes");
                byte[] parked = new NetWriter(Msg.Vehicle).Vec(new UnityEngine.Vector3(5f, 0f, 0f))
                    .Quat(new UnityEngine.Quaternion(0f, 0f, 0f, 1f)).Vec(new UnityEngine.Vector3()).Vec(new UnityEngine.Vector3()).ToArray();
                Assert(VehicleSnapshot.Read(new NetReader(parked, 1, parked.Length - 1), 0).Position.x == 5f, "Parked packet changed");
            });
            Test("optional cabin heat cannot weaken validation of vehicle motion", delegate {
                Assert(float.IsNaN(new NetReader(BitConverter.GetBytes(float.NaN), 0, 4).OptionalF32()), "Missing heat not retained");
                foreach (float v in new[] { float.PositiveInfinity, float.NegativeInfinity }) Reject(delegate { new NetReader(BitConverter.GetBytes(v), 0, 4).OptionalF32(); });
                foreach (int mode in new[] { 1, 2 })
                foreach (int badField in new[] { 0, 13, 16 })
                {
                    // 13 pose/velocity floats, then rpm/throttle/steering/heat.
                    var w = new NetWriter(Msg.Vehicle);
                    for (int i = 0; i < 17; i++) w.F32(i == badField ? (badField == 16 ? float.PositiveInfinity : float.NaN) : 0f);
                    byte[] packet = w.U16(0).ToArray();
                    Reject(delegate { VehicleSnapshot.Read(new NetReader(packet, 1, packet.Length - 1), mode); });
                }
                Reject(delegate { VehicleSnapshot.Read(new NetReader(new byte[0], 0, 0), 3); });
            });
            Test("active vehicle owner outranks delayed parked samples and yields to the next driver", delegate {
                Assert(!VehicleSnapshot.AcceptMotion(0, 0, 1), "Host parking sample overrode guest driver");
                Assert(!VehicleSnapshot.AcceptMotion(2, 1, 2), "Previous owner's coast sample overrode next driver");
                Assert(VehicleSnapshot.AcceptMotion(0, 1, 1), "Current driver cannot release vehicle");
                Assert(VehicleSnapshot.AcceptMotion(1, 2, 1), "Next driver cannot take over");
                Assert(VehicleSnapshot.AcceptMotion(0, 0, -1), "Host cannot sync parked vehicle");
            });
            Test("MWCoop reliable UDP protocol preserves three clients' ordering through loss and reordering", delegate {
                Simulated.Time = 0; Simulated.Nodes.Clear(); Transport host = Create(); host.Host(new Simulated("host")); List<Transport> clients = new List<Transport>();
                Dictionary<int, int> received = new Dictionary<int, int>();
                host.OnMessage += delegate(Peer peer, byte[] data, int offset, int length) { int value = BitConverter.ToInt32(data, offset); int expected; received.TryGetValue(peer.Id, out expected); Assert(value == expected, "Reliable message duplicated or reordered"); received[peer.Id] = expected + 1; };
                try {
                    for (int i = 0; i < 3; i++) { Transport client = Create(); clients.Add(client); client.Connect(new Simulated("client" + i), "host"); }
                    bool sent = false;
                    for (int step = 0; step < 4000; step++) {
                        Simulated.Time = step * .02f; host.Update(Simulated.Time); foreach (Transport client in clients) client.Update(Simulated.Time);
                        if (!sent && host.Peers.Count == 3 && clients.TrueForAll(c => c.LocalId > 0)) { foreach (Transport client in clients) for (int n = 0; n < 600; n++) client.SendReliable(client.Peers[0], BitConverter.GetBytes(n)); sent = true; }
                        if (sent && received.Count == 3 && new List<int>(received.Values).TrueForAll(n => n == 600)) break;
                    }
                    Assert(received.Count == 3 && new List<int>(received.Values).TrueForAll(n => n == 600), "Reliable transfer failed under 150ms latency and 5% loss");
                } finally { foreach (Transport client in clients) client.Close(); host.Close(); }
            });
            if (Array.IndexOf(args, "--no-sockets") < 0) Test("MWCoop transport connects a host and three actual UDP clients on localhost", delegate {
                int port; using (Socket reserve = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) { reserve.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = ((IPEndPoint)reserve.LocalEndPoint).Port; }
                Transport host = Create(); List<Transport> clients = new List<Transport>(); int received = 0; host.OnMessage += delegate { received++; };
                try {
                    host.Host(port); for (int i = 0; i < 3; i++) { Transport client = Create(); clients.Add(client); client.Connect("127.0.0.1", port); }
                    bool sent = false;
                    for (int step = 0; step < 600; step++) { float time = step * .01f; host.Update(time); foreach (Transport client in clients) client.Update(time); if (!sent && clients.TrueForAll(c => c.LocalId > 0)) { foreach (Transport client in clients) client.SendReliable(client.Peers[0], new byte[] { 1, 2, 3 }); sent = true; } if (received == 3) break; Thread.Sleep(5); }
                    Assert(received == 3 && host.Peers.Count == 3, "Actual UDP transport failed");
                } finally { foreach (Transport client in clients) client.Close(); host.Close(); }
            });
            if (Array.IndexOf(args, "--no-sockets") < 0) Test("actual UDP bulk snapshots converge after three receivers pause between polls", delegate {
                int port; using (Socket reserve = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) { reserve.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = ((IPEndPoint)reserve.LocalEndPoint).Port; }
                Transport host = Create(); List<Transport> clients = new List<Transport>(); int[] received = new int[3];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try {
                    host.Host(port);
                    for (int i = 0; i < 3; i++) {
                        int index = i; Transport client = Create(); clients.Add(client);
                        client.OnMessage += delegate(Peer peer, byte[] bytes, int offset, int length) {
                            Assert(length == 1000 && BitConverter.ToInt32(bytes, offset) == received[index], "Bulk snapshot order or size changed");
                            for (int n = 4; n < length; n++) Assert(bytes[offset + n] == (byte)(received[index] + n), "Pending snapshot bytes were overwritten");
                            received[index]++;
                        };
                        client.Connect("127.0.0.1", port);
                    }
                    bool sent = false;
                    while (clock.Elapsed.TotalSeconds < 20) {
                        float now = (float)clock.Elapsed.TotalSeconds; host.Update(now);
                        foreach (Transport client in clients) client.Update(now);
                        if (!sent && clients.TrueForAll(c => c.LocalId > 0)) {
                            foreach (Peer peer in host.Peers) for (int n = 0; n < 600; n++) {
                                byte[] bytes = new byte[1000]; Buffer.BlockCopy(BitConverter.GetBytes(n), 0, bytes, 0, 4);
                                for (int k = 4; k < bytes.Length; k++) bytes[k] = (byte)(n + k);
                                host.SendReliable(peer, bytes);
                            }
                            sent = true;
                        }
                        if (sent && Array.TrueForAll(received, n => n == 600)) break;
                        // Loading/long frames stop polling; do not require a high rendering rate.
                        Thread.Sleep(50);
                    }
                    Assert(Array.TrueForAll(received, n => n == 600), "Bulk snapshots stalled with paused receivers: " + string.Join(",", Array.ConvertAll(received, n => n.ToString())));
                    host.Update((float)clock.Elapsed.TotalSeconds);
                    Assert(host.Peers.TrueForAll(p => p.InFlight == 0), "Bulk reliable bytes remain owned after ACK");
                } finally { foreach (Transport client in clients) client.Close(); host.Close(); }
            });
            Console.WriteLine("ALL " + passed + " MSC ADAPTER TESTS PASSED"); return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL after " + passed + ": " + e); return 1; }
    }
}
