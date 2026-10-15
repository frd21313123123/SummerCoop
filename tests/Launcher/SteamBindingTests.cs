using System;
using System.IO;
using System.Reflection;
using SummerCoop.Launcher;

// Local integration check against the installed game's real managed/native
// Steamworks libraries. Never sends an invitation or launches/joins a game.
internal static class SteamBindingTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Packet(Assembly game, string name) { return Activator.CreateInstance(game.GetType("Steamworks." + name, true)); }
    private static void Dispatch(SteamInviteClient client, object packet)
    {
        typeof(SteamInviteClient).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(packet.GetType()).Invoke(client, new[] { packet });
    }

    private static int Main(string[] args)
    {
        try
        {
            string gamePath = Path.GetFullPath(args[0]);
            using (SteamInviteClient client = new SteamInviteClient())
            {
                client.Update(gamePath);
                Console.WriteLine("Steam runtime: " + client.Status);
                Check(client.Ready || client.Status.StartsWith("Steam недоступен.", StringComparison.Ordinal) || client.Status.StartsWith("Steam отключён.", StringComparison.Ordinal), "real Steamworks binding failed: " + client.Status);
                Assembly game = (Assembly)typeof(SteamInviteClient).GetField("steamAssembly", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
                Check(game != null, "real game assembly not loaded");
                foreach (string name in new[] { "LobbyInvite_t", "GameLobbyJoinRequested_t" })
                {
                    Type packetType = game.GetType("Steamworks." + name, true);
                    Type callback = game.GetType("Steamworks.Callback`1", true).MakeGenericType(packetType);
                    Type signature = callback.GetMethod("Create").GetParameters()[0].ParameterType;
                    MethodInfo receiver = typeof(SteamInviteClient).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(packetType);
                    Check(Delegate.CreateDelegate(signature, client, receiver) != null, "callback signature mismatch for " + name);
                }
                int count = 0;
                ulong last = 0;
                client.Invitation += delegate(SteamInvitation invite) { count++; last = invite.LobbyId; };
                object packet = Packet(game, "LobbyInvite_t");
                Type type = packet.GetType();
                type.GetField("m_ulGameID").SetValue(packet, (ulong)SteamInvitation.AppId);
                type.GetField("m_ulSteamIDLobby").SetValue(packet, 109775241234567890UL);
                Dispatch(client, packet);
                Check(count == 1 && last == 109775241234567890UL, "actual LobbyInvite_t layout not decoded");
                type.GetField("m_ulGameID").SetValue(packet, 4164420UL);
                Dispatch(client, packet);
                Check(count == 1, "Winter Car invitation accepted for MSC");
                object join = Packet(game, "GameLobbyJoinRequested_t");
                Type id = game.GetType("Steamworks.CSteamID", true);
                join.GetType().GetField("m_steamIDLobby").SetValue(join, Activator.CreateInstance(id, new object[] { 109775241234567891UL }));
                Dispatch(client, join);
                Check(count == 2 && last == 109775241234567891UL, "actual GameLobbyJoinRequested_t layout not decoded");
                client.Dispose();
                client.Update(gamePath);
                Check(!client.Ready, "disposed Steam client restarted");
            }
            Console.WriteLine("PASS installed MSC Steamworks bindings: native loading, initialization result, actual invitation structs, app filter and disposal");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
