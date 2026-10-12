using System;
using System.Collections.Generic;
using System.Globalization;
using SummerCoop.Core;
using Steamworks;

namespace SummerCoop.Game
{
    public sealed class SteamTransport : ITransport
    {
        private const int ReliableChannel = 121, MotionChannel = 122;
        private readonly EventQueue events = new EventQueue();
        private readonly HashSet<string> peers = new HashSet<string>();
        private readonly HashSet<string> blocked = new HashSet<string>();
        private readonly Callback<P2PSessionRequest_t> sessionRequest;
        private readonly Callback<P2PSessionConnectFail_t> sessionFailed;
        private readonly CallResult<LobbyCreated_t> created;
        private readonly CallResult<LobbyEnter_t> entered;
        private readonly LobbyCreation creation = new LobbyCreation();
        private string lobbyStatus = "";
        private double nextLobbyPoll;
        private CSteamID lobby, owner;
        private bool closed;
        private readonly string gameHash;
        public string HostPeer { get { return owner.m_SteamID.ToString(CultureInfo.InvariantCulture); } }
        public bool IsHost { get; private set; }
        public bool LobbyReady { get; private set; }
        public CSteamID Lobby { get { return lobby; } }
        public string LobbyStatus { get { return lobbyStatus; } }
        public bool CanRetryLobby { get { return !closed && IsHost && creation.CanRetry; } }
        public IList<string> Peers { get { return new List<string>(peers); } }
        public SteamTransport(bool host, ulong lobbyId, string gameHash)
        {
            if (!Available()) throw new InvalidOperationException("Steam for My Summer Car is unavailable. Use LAN/VPN.");
            this.gameHash = gameHash; IsHost = host;
            sessionRequest = Callback<P2PSessionRequest_t>.Create(OnRequest);
            sessionFailed = Callback<P2PSessionConnectFail_t>.Create(OnFailure);
            created = CallResult<LobbyCreated_t>.Create(OnCreated);
            entered = CallResult<LobbyEnter_t>.Create(OnEntered);
            if (host) creation.Start(UnityEngine.Time.realtimeSinceStartup);
            else { lobby = new CSteamID(lobbyId); entered.Set(SteamMatchmaking.JoinLobby(lobby)); }
        }
        public static bool Available()
        {
            try
            {
                Type manager = typeof(CarController).Assembly.GetType("SteamManager", true);
                System.Reflection.PropertyInfo initialized = manager.GetProperty("Initialized", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                return initialized != null && (bool)initialized.GetValue(null, null) && SteamUtils.GetAppID().m_AppId == 516750;
            }
            catch (Exception) { return false; }
        }
        public static bool Connected()
        {
            try { return Available() && SteamUser.BLoggedOn(); }
            catch (Exception) { return false; }
        }
        public static void RequireConnection()
        {
            string error = ConnectionError();
            if (error != null) throw new InvalidOperationException(error);
        }
        public static string ConnectionError()
        {
            if (!Available()) return "API игры недоступен. Перезапустите Steam и игру из библиотеки с одинаковыми правами Windows. Пока можно использовать LAN / VPN.";
            if (!Connected()) return "API игры не подключён к серверам Valve. Переподключите Steam. Магазин и друзья могут работать отдельно; LAN / VPN доступен.";
            return null;
        }
        public void RetryLobby()
        {
            if (CanRetryLobby) creation.Start(UnityEngine.Time.realtimeSinceStartup);
        }
        private void LobbyProgress(string text)
        {
            if (lobbyStatus == text) return;
            lobbyStatus = text;
            events.Add(new TransportEvent { Kind = TransportEventKind.Status, Error = text });
        }
        private void CreateLobby()
        {
            double now = UnityEngine.Time.realtimeSinceStartup;
            bool connected = Connected();
            if (creation.Request(now, connected))
            {
                try
                {
                    SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Protocol.MaxPlayers);
                    if (call.m_SteamAPICall == 0) creation.Complete(now, false, true, "Steam не принял запрос создания лобби");
                    else created.Set(call);
                }
                catch (Exception e) { creation.Complete(now, false, true, "Запрос Steam API: " + e.Message); }
            }
            if (!creation.Running)
                LobbyProgress("Лобби Steam не создано: " + creation.Error + ". Мир хоста открыт. " + (creation.Pending ? "Ожидание завершения запроса Steam." : "После восстановления Steam нажмите «Повторить создание лобби»."));
            else if (!connected) LobbyProgress("Мир хоста открыт. Ожидание подключения Steam API к серверам Valve…");
            else LobbyProgress("Мир хоста открыт. Создание лобби Steam: попытка " + creation.Attempts + "/" + LobbyCreation.MaxAttempts + (creation.Pending ? "…" : ". Повтор после временного отказа: " + creation.Error));
        }
        private void OnCreated(LobbyCreated_t data, bool failure)
        {
            bool success = !failure && data.m_eResult == EResult.k_EResultOK;
            if (closed) { if (success) SteamMatchmaking.LeaveLobby(new CSteamID(data.m_ulSteamIDLobby)); return; }
            bool retryable = failure || data.m_eResult == EResult.k_EResultNoConnection || data.m_eResult == EResult.k_EResultTimeout || data.m_eResult == EResult.k_EResultServiceUnavailable || data.m_eResult == EResult.k_EResultFail;
            string reason = data.m_eResult == EResult.k_EResultNoConnection ? "нет соединения Steam API с серверами Valve (NoConnection)" : "Steam: " + data.m_eResult + (failure ? " (ошибка ответа API)" : "");
            if (!creation.Complete(UnityEngine.Time.realtimeSinceStartup, success, retryable, reason))
            {
                if (success) SteamMatchmaking.LeaveLobby(new CSteamID(data.m_ulSteamIDLobby));
                return;
            }
            lobby = new CSteamID(data.m_ulSteamIDLobby); owner = SteamUser.GetSteamID();
            SteamMatchmaking.SetLobbyData(lobby, "summercoop.version", Protocol.Version.ToString(CultureInfo.InvariantCulture));
            SteamMatchmaking.SetLobbyData(lobby, "summercoop.game", gameHash);
            SteamMatchmaking.SetLobbyData(lobby, "summercoop.mod", Protocol.ModVersion);
            SteamMatchmaking.SetLobbyJoinable(lobby, true); LobbyReady = true;
            LobbyProgress("Лобби Steam готово. Можно приглашать друзей.");
        }
        private void OnEntered(LobbyEnter_t data, bool failure)
        {
            if (closed) return;
            if (failure || data.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) { Error("Steam lobby join failed"); return; }
            lobby = new CSteamID(data.m_ulSteamIDLobby); owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (SteamMatchmaking.GetLobbyData(lobby, "summercoop.version") != Protocol.Version.ToString(CultureInfo.InvariantCulture) || SteamMatchmaking.GetLobbyData(lobby, "summercoop.game") != gameHash)
            {
                Error("Different SummerCoop or game version"); SteamMatchmaking.LeaveLobby(lobby); return;
            }
            if (owner == SteamUser.GetSteamID()) { Error("Host has left the lobby"); return; }
            LobbyReady = true; Add(owner);
        }
        private void Add(CSteamID id)
        {
            string peer = id.m_SteamID.ToString(CultureInfo.InvariantCulture);
            if (blocked.Contains(peer) || !peers.Add(peer)) return;
            events.Add(new TransportEvent { Kind = TransportEventKind.Connected, Peer = peer });
        }
        private bool Member(CSteamID id)
        {
            if (!LobbyReady) return false;
            for (int i = 0; i < SteamMatchmaking.GetNumLobbyMembers(lobby); i++) if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == id) return true;
            return false;
        }
        private void OnRequest(P2PSessionRequest_t data)
        {
            string peer = data.m_steamIDRemote.m_SteamID.ToString(CultureInfo.InvariantCulture);
            if (!closed && Member(data.m_steamIDRemote) && !blocked.Contains(peer) && (IsHost || data.m_steamIDRemote == owner)) SteamNetworking.AcceptP2PSessionWithUser(data.m_steamIDRemote);
        }
        private void OnFailure(P2PSessionConnectFail_t data)
        {
            string peer = data.m_steamIDRemote.m_SteamID.ToString(CultureInfo.InvariantCulture);
            if (peers.Remove(peer)) events.Add(new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = peer, Error = "Steam P2P: " + data.m_eP2PSessionError });
        }
        private void Error(string text) { events.Add(new TransportEvent { Kind = TransportEventKind.Error, Error = text }); }
        public void Update()
        {
            if (closed) return;
            if (!LobbyReady)
            {
                if (IsHost && UnityEngine.Time.realtimeSinceStartup >= nextLobbyPoll)
                {
                    nextLobbyPoll = UnityEngine.Time.realtimeSinceStartup + .25;
                    CreateLobby();
                }
                return;
            }
            if (SteamMatchmaking.GetLobbyOwner(lobby) != owner) { Error("Host left. Session ended."); return; }
            HashSet<string> members = new HashSet<string>();
            for (int i = 0; i < SteamMatchmaking.GetNumLobbyMembers(lobby); i++)
            {
                CSteamID id = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                if (id == SteamUser.GetSteamID()) continue;
                members.Add(id.m_SteamID.ToString(CultureInfo.InvariantCulture)); if (IsHost) Add(id);
            }
            foreach (string peer in new List<string>(peers)) if (!members.Contains(peer)) { peers.Remove(peer); events.Add(new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = peer }); }
            ReadChannel(ReliableChannel, true); ReadChannel(MotionChannel, false);
        }
        private void ReadChannel(int channel, bool reliable)
        {
            for (int i = 0; i < 128; i++)
            {
                uint size;
                if (!SteamNetworking.IsP2PPacketAvailable(out size, channel)) break;
                // Old API has no discard operation: bounded scratch buffer drains oversized packets.
                if (size > 1048576) { Error("Oversized Steam packet"); return; }
                byte[] bytes = new byte[size]; uint read; CSteamID sender;
                if (!SteamNetworking.ReadP2PPacket(bytes, size, out read, out sender, channel)) break;
                string peer = sender.m_SteamID.ToString(CultureInfo.InvariantCulture);
                if (!peers.Contains(peer) || blocked.Contains(peer) || read != size || size > Protocol.MaxFrame) continue;
                if (!reliable && size > Protocol.MaxDatagram) continue;
                try { Codec.Validate(bytes); } catch (Exception) { Disconnect(peer); continue; }
                events.Add(new TransportEvent { Kind = TransportEventKind.Data, Peer = peer, Data = bytes, Reliable = reliable });
            }
        }
        public void Send(string peer, byte[] bytes, bool reliable)
        {
            if (closed || !peers.Contains(peer)) return;
            if (bytes.Length > (reliable ? Protocol.MaxFrame : Protocol.MaxDatagram)) throw new ProtocolException("Steam packet too large");
            CSteamID id = new CSteamID(ulong.Parse(peer, CultureInfo.InvariantCulture));
            if (!SteamNetworking.SendP2PPacket(id, bytes, (uint)bytes.Length, reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay, reliable ? ReliableChannel : MotionChannel)) Error("Steam send failed");
        }
        public void Invite() { if (IsHost && LobbyReady) SteamFriends.ActivateGameOverlayInviteDialog(lobby); }
        public void Disconnect(string peer)
        {
            blocked.Add(peer); peers.Remove(peer);
            SteamNetworking.CloseP2PSessionWithUser(new CSteamID(ulong.Parse(peer, CultureInfo.InvariantCulture)));
            events.Add(new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = peer });
        }
        public bool Poll(out TransportEvent item) { return events.Poll(out item); }
        public void Dispose()
        {
            if (closed) return; closed = true;
            foreach (string peer in peers) SteamNetworking.CloseP2PSessionWithUser(new CSteamID(ulong.Parse(peer, CultureInfo.InvariantCulture)));
            if (LobbyReady) SteamMatchmaking.LeaveLobby(lobby);
            sessionRequest.Dispose(); sessionFailed.Dispose(); created.Cancel(); entered.Cancel(); peers.Clear();
        }
    }
}
