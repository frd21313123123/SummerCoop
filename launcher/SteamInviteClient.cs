using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SummerCoop.Launcher
{
    internal interface ISteamInviteClient : IDisposable
    {
        event Action<SteamInvitation> Invitation;
        bool Ready { get; }
        string Status { get; }
        void Update(string gamePath);
    }

    // Same callbacks as upstream launcher/steam.inc: LobbyInvite (503) and
    // GameLobbyJoinRequested (333). Use the installed game's Steamworks.NET so the
    // callback ABI and interface versions always match its native Steam libraries.
    // No Steam/game binaries are redistributed in the launcher.
    internal sealed class SteamInviteClient : ISteamInviteClient
    {
        public event Action<SteamInvitation> Invitation;
        public bool Ready { get; private set; }
        public string Status { get; private set; }
        private string managedPath, loadedGamePath;
        private Assembly steamAssembly;
        private readonly List<IDisposable> callbacks = new List<IDisposable>();
        private readonly List<IntPtr> nativeLibraries = new List<IntPtr>();
        private readonly HashSet<string> nativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private MethodInfo runCallbacks, shutdown;
        private DateTime retryUtc, checkConnectionUtc;
        private bool initialized, disposed, resolverAttached;

        internal SteamInviteClient() { Status = "Steam: ожидание папки игры."; }

        public void Update(string gamePath)
        {
            if (disposed) return;
            try
            {
                if (!Ready)
                {
                    if (DateTime.UtcNow < retryUtc) return;
                    retryUtc = DateTime.UtcNow.AddSeconds(10);
                    Initialize(gamePath);
                }
                if (Ready)
                {
                    if (DateTime.UtcNow >= checkConnectionUtc)
                    {
                        checkConnectionUtc = DateTime.UtcNow.AddSeconds(1);
                        if (!(bool)Method("SteamAPI", "IsSteamRunning").Invoke(null, null) ||
                            !(bool)Method("SteamUser", "BLoggedOn").Invoke(null, null))
                        {
                            Status = "Steam отключён. Откройте Steam и войдите в аккаунт с My Summer Car.";
                            ReleaseSession();
                            retryUtc = DateTime.UtcNow.AddSeconds(10);
                            return;
                        }
                    }
                    runCallbacks.Invoke(null, null);
                }
            }
            catch (Exception error)
            {
                Exception detail = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                Status = "Steam: " + detail.Message;
                ReleaseSession();
            }
        }

        private void Initialize(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath) || !File.Exists(Path.Combine(gamePath, "mysummercar.exe")))
                { Status = "Steam: выберите папку My Summer Car в настройках."; return; }
            string fullPath = Path.GetFullPath(gamePath);
            if (loadedGamePath != null && !string.Equals(fullPath, loadedGamePath, StringComparison.OrdinalIgnoreCase))
                { Status = "Steam: папка игры изменена — перезапустите лаунчер."; return; }
            if (steamAssembly == null)
            {
                managedPath = Path.Combine(fullPath, @"mysummercar_Data\Managed");
                string binding = Path.Combine(managedPath, "Assembly-CSharp-firstpass.dll");
                if (!File.Exists(binding)) throw new FileNotFoundException("Не найдена Steamworks.NET из My Summer Car.");
                // Resolve managed dependencies from this game only, never the CWD.
                if (!resolverAttached)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += ResolveGameAssembly;
                    resolverAttached = true;
                }
                loadedGamePath = fullPath;
                string nativeApi = Path.Combine(fullPath, IntPtr.Size == 8 ? "steam_api64.dll" : "steam_api.dll");
                LoadNative(nativeApi);
                // MSC's older managed bindings call CSteamworks; newer builds may
                // call steam_api directly. Load the bridge only when it exists.
                string bridge = Path.Combine(fullPath, @"mysummercar_Data\Plugins\CSteamworks.dll");
                if (File.Exists(bridge)) LoadNative(bridge);
                steamAssembly = Assembly.LoadFrom(binding);
                runCallbacks = Method("SteamAPI", "RunCallbacks");
                shutdown = Method("SteamAPI", "Shutdown");
            }
            // Upstream uses these process-local values instead of writing
            // steam_appid.txt into either the launcher or game directory.
            string oldApp = Environment.GetEnvironmentVariable("SteamAppId");
            string oldGame = Environment.GetEnvironmentVariable("SteamGameId");
            try
            {
                Environment.SetEnvironmentVariable("SteamAppId", SteamInvitation.AppId.ToString(CultureInfo.InvariantCulture));
                Environment.SetEnvironmentVariable("SteamGameId", SteamInvitation.AppId.ToString(CultureInfo.InvariantCulture));
                if (!(bool)Method("SteamAPI", "Init").Invoke(null, null))
                    { Status = "Steam недоступен. Откройте Steam и войдите в аккаунт с My Summer Car."; return; }
                initialized = true;
                object app = Method("SteamUtils", "GetAppID").Invoke(null, null);
                if (Convert.ToUInt32(app.GetType().GetField("m_AppId").GetValue(app), CultureInfo.InvariantCulture) != SteamInvitation.AppId)
                    throw new InvalidOperationException("Steam подключён к другой игре. Перезапустите лаунчер из Steam.");
                Register("LobbyInvite_t");
                Register("GameLobbyJoinRequested_t");
                Ready = true;
                Status = "Steam подключён · приглашения появятся здесь.";
            }
            finally
            {
                Environment.SetEnvironmentVariable("SteamAppId", oldApp);
                Environment.SetEnvironmentVariable("SteamGameId", oldGame);
            }
        }

        private Assembly ResolveGameAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
            string path = Path.Combine(managedPath, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private MethodInfo Method(string type, string method)
        {
            Type binding = steamAssembly.GetType("Steamworks." + type, true);
            return binding.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
        }

        private void Register(string packetName)
        {
            Type packet = steamAssembly.GetType("Steamworks." + packetName, true);
            Type callback = steamAssembly.GetType("Steamworks.Callback`1", true).MakeGenericType(packet);
            MethodInfo create = callback.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            Type dispatch = create.GetParameters()[0].ParameterType;
            MethodInfo receiver = GetType().GetMethod("Receive", BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(packet);
            Delegate handler = Delegate.CreateDelegate(dispatch, this, receiver);
            callbacks.Add((IDisposable)create.Invoke(null, new object[] { handler }));
        }

        private void Receive<T>(T value)
        {
            // Never let a UI/event exception cross a native Steam callback boundary.
            try
            {
                object packet = value;
                Type type = typeof(T);
                ulong lobby, sender;
                if (type.Name == "LobbyInvite_t")
                {
                    ulong game = (ulong)type.GetField("m_ulGameID").GetValue(packet);
                    if (game != SteamInvitation.AppId) return;
                    lobby = (ulong)type.GetField("m_ulSteamIDLobby").GetValue(packet);
                    sender = (ulong)type.GetField("m_ulSteamIDUser").GetValue(packet);
                }
                else
                {
                    lobby = SteamId(type.GetField("m_steamIDLobby").GetValue(packet));
                    sender = SteamId(type.GetField("m_steamIDFriend").GetValue(packet));
                }
                if (lobby == 0) return;
                string name = null;
                if (sender != 0)
                {
                    try
                    {
                        object id = Activator.CreateInstance(steamAssembly.GetType("Steamworks.CSteamID", true), new object[] { sender });
                        name = (string)Method("SteamFriends", "GetFriendPersonaName").Invoke(null, new[] { id });
                    }
                    catch (Exception) { }
                }
                Action<SteamInvitation> received = Invitation;
                if (received != null) received(new SteamInvitation(lobby, sender, name));
            }
            catch (Exception error) { Status = "Не удалось прочитать приглашение Steam: " + error.Message; }
        }

        private static ulong SteamId(object value) { return (ulong)value.GetType().GetField("m_SteamID").GetValue(value); }

        private void LoadNative(string path)
        {
            if (nativePaths.Contains(path)) return;
            IntPtr library = LoadLibraryEx(path, IntPtr.Zero, 0x1100); // DLL_LOAD_DIR | SYSTEM32
            if (library == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Не удалось загрузить " + Path.GetFileName(path) + " из папки игры");
            nativeLibraries.Add(library);
            nativePaths.Add(path);
        }

        private void ReleaseSession()
        {
            Ready = false;
            foreach (IDisposable callback in callbacks) try { callback.Dispose(); } catch (Exception) { }
            callbacks.Clear();
            if (initialized) try { shutdown.Invoke(null, null); } catch (Exception) { }
            initialized = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ReleaseSession();
            if (resolverAttached) AppDomain.CurrentDomain.AssemblyResolve -= ResolveGameAssembly;
            // The CLR caches native P/Invoke entry points. Keep DLLs loaded for the
            // rest of this process rather than invalidating those cached pointers.
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    }
}
