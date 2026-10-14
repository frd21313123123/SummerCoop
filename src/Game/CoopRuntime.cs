using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MSCLoader;
using Steamworks;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class CoopRuntime : MonoBehaviour
    {
        public static CoopRuntime Instance { get; private set; }
        private static bool cliHandled;
        private static readonly string[] commandLine = Environment.GetCommandLineArgs();
        public Session Session { get; private set; }
        public WorldRegistry Registry { get; private set; }
        public NativeAdapter Adapter { get; private set; }
        public bool Active { get { return Session != null && Session.Phase == SessionPhase.Playing; } }
        public bool Joining { get; private set; }
        public bool NativeBaselineReady { get; private set; }
        private static bool NativeBaseline { get { return Argument("--summercoop-test-native-only") == "yes" && Argument("--summercoop-test-report") != ""; } }
        private SaveIsolation saves;
        private CheckpointStore store;
        private NativeKeys personalKeys;
        private NativeToolRecovery toolRecovery;
        private World preparedWorld;
        private string preparedWorldId;
        private float keyCaptureTime;
        private Authority authority;
        private ITransport pendingTransport;
        private bool hostSteamPending;
        private int hostPortPending;
        private WorldRegistry pendingRegistry;
        private Callback<GameLobbyJoinRequested_t> invitation;
        private float nextSteamCheck;
        private string steamError = "Проверка подключения…";
        private AvatarView avatars;
        private GameObject localPlayer;
        private Camera localCamera;
        private EncodedTransfer nativeBaseline;
        private static readonly MethodInfo VehicleInput = typeof(CarController).GetMethod("GetInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private SceneReadiness sceneReadiness = new SceneReadiness();
        private string gameHash, username = "Player", address = "127.0.0.1", password = "", portText = "27842", lobbyText = "";
        private string status = "F6 — SummerCoop", root, requestedSeat = "", requestedState = "";
        private bool visible, importSave, awaitingScene, nativeLoaded, hostPending, leaving, fatal;
        private float sceneBegan, poseTime, captureTime, checkpointTime, refreshTime, avatarTime, previousScale = 1;
        private long requestedSequence;
        private long sleepEpoch;
        private bool requestAccepted;
        private Fsm requestedFsm;
        private string nativeHeld = "";
        private PlayMakerFSM nativeHand;
        private PlayMakerFSM recoverInput;
        private string recoverState;
        private int recoverAfterFrame;
        private CommandKind requestedKind;
        private static ulong pendingLobby;
        private float respawnTime;
        private Rect panel = new Rect(20, 20, 500, 0);
        private CoopMenuSkin menuSkin;
        private int menuTab;
        private Vector2 spawnScroll;
        private string spawnSearch = "", selectedSpawn = "";
        private float spawnCondition = 100;
        private readonly float[] needValues = new float[PlayerControls.Needs.Length];
        private readonly Dictionary<string, float> requests = new Dictionary<string, float>();
        private readonly PendingNativeActions sharedRequests = new PendingNativeActions();
        private readonly DeferredHandRelease<Fsm> deferredRelease = new DeferredHandRelease<Fsm>();
        public string SaveRoot { get { return root; } }
        public string StatusText { get { return status; } }
        public double LastUpdateMilliseconds { get; private set; }
        public readonly double[] LastStages = new double[10];
        public static readonly bool TraceAllocations = Argument("--summercoop-test-allocations") == "yes" && Argument("--summercoop-test-report") != "";
        public readonly long[] HeapIncrease = new long[6], HeapSamples = new long[6];
        private struct HeapStamp { public long Bytes; public int Collections; }
        private HeapStamp previousUpdateEnd;
        private bool measuredUpdateEnd;
        private static HeapStamp HeapBegin() { return TraceAllocations ? new HeapStamp { Bytes = GC.GetTotalMemory(false), Collections = GC.CollectionCount(0) } : new HeapStamp(); }
        private void HeapEnd(int index, HeapStamp began)
        {
            if (!TraceAllocations || GC.CollectionCount(0) != began.Collections) return;
            long difference = GC.GetTotalMemory(false) - began.Bytes;
            if (difference >= 0) { HeapIncrease[index] += difference; HeapSamples[index]++; }
        }
        public int LastMotionBodies { get; private set; }
        private static double Elapsed(long began) { return (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        public static void Ensure()
        {
            if (Instance != null) return;
            GameObject go = new GameObject("SummerCoop.Runtime"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<CoopRuntime>();
        }
        private void Awake()
        {
            Instance = this; Application.runInBackground = true;
            try
            {
                string profile = Argument("--summercoop-profile");
                root = Path.GetFullPath(profile == "" ? Path.Combine(Application.persistentDataPath, "SummerCoop") : profile);
                saves = new SaveIsolation(root); gameHash = SaveIsolation.GameHash();
                NativeHooks.Install();
                if (Argument("--summercoop-test-report") != "" && Argument("--summercoop-test-import-save") == "yes") importSave = true;
                string cliName = Argument("--summercoop-name");
                if (cliName != "") { username = cliName.Length > 48 ? cliName.Substring(0, 48) : cliName; }
                string cliPassword = Argument("--summercoop-password");
                if (cliPassword != "") password = cliPassword;
                if (Argument("--summercoop-import-save") == "yes") importSave = true;
            }
            catch (Exception e) { Fail(e); }
        }
        private void Start()
        {
            if (saves == null || fatal) return;
            try
            {
                // Let the game's initial Awake callbacks finish before querying its SteamManager.
                RefreshSteam();
                if (cliHandled) return; cliHandled = true;
                string port = Argument("--summercoop-host-lan");
                if (Argument("--summercoop-port") != "" && port == "") portText = Argument("--summercoop-port");
                string joinSteam = Argument("--summercoop-join-steam");
                if (Argument("--summercoop-test-report") != "" && Argument("--summercoop-test-steam-host") == "yes") StartHost(true);
                else if (Argument("--summercoop-host-steam") == "yes") StartHost(true);
                else if (port != "") { portText = port; StartHost(false); }
                else if (Argument("--summercoop-join-lan") != "") { address = Argument("--summercoop-join-lan"); if (Argument("--summercoop-port") != "") portText = Argument("--summercoop-port"); StartClient(false, 0); }
                else if (joinSteam != "") { ulong sid; if (!ulong.TryParse(joinSteam, out sid)) throw new ArgumentException("Нужен номер лобби Steam"); lobbyText = joinSteam; StartClient(true, sid); }
                else { string lobby = Argument("+connect_lobby"); ulong id; if (ulong.TryParse(lobby, out id)) { lobbyText = lobby; StartClient(true, id); } }
            }
            catch (Exception e) { Fail(e); }
        }
        private void RefreshSteam()
        {
            if (Time.realtimeSinceStartup < nextSteamCheck) return;
            nextSteamCheck = Time.realtimeSinceStartup + 1;
            string error = SteamTransport.ConnectionError();
            if (steamError != error)
            {
                steamError = error;
                Debug.Log("SummerCoop Steam: " + (error ?? "подключён"));
            }
            if (invitation != null || !SteamTransport.Available()) return;
            try
            {
                invitation = Callback<GameLobbyJoinRequested_t>.Create(OnInvite);
                if (username == "Player" && Argument("--summercoop-name") == "")
                {
                    string name = SteamFriends.GetPersonaName();
                    if (!string.IsNullOrEmpty(name)) username = name.Length > 48 ? name.Substring(0, 48) : name;
                }
            }
            catch (Exception e) { Debug.LogWarning("SummerCoop Steam invitations: " + e.Message); }
        }
        public static string Argument(string key)
        {
            int i = Array.IndexOf(commandLine, key); return i >= 0 && i + 1 < commandLine.Length ? commandLine[i + 1] : "";
        }
        private int Port()
        {
            int port; if (!int.TryParse(portText, out port) || port < 1024 || port > 65535) throw new ArgumentException("Порт должен быть от 1024 до 65535"); return port;
        }
        private void VerifyVanilla()
        {
            string mods = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Mods");
            if (!Directory.Exists(mods)) return;
            foreach (string file in Directory.GetFiles(mods, "*.dll"))
            {
                string name = Path.GetFileName(file);
                if (name != "SummerCoop.dll" && name != "MSCLoader_Console.dll" && name != "MSCLoader_Settings.dll") throw new InvalidOperationException("Для этой версии отключите другой мод: " + name);
            }
        }
        public void StartHost(bool steam)
        {
            if (Session != null || awaitingScene || Application.loadedLevelName == "GAME") throw new InvalidOperationException("Создайте сессию из главного меню");
            if (steam) SteamTransport.RequireConnection();
            VerifyVanilla(); saves.Activate("world", importSave);
            using (CheckpointStore source = new CheckpointStore(saves.CurrentDirectory)) preparedWorld = source.Recover();
            if (preparedWorld == null && File.Exists(Path.Combine(saves.CurrentDirectory, "world.scw"))) throw new InvalidOperationException("Повреждённая контрольная точка мира");
            if (preparedWorld != null && (preparedWorld.GameHash != gameHash || preparedWorld.SchemaHash != Codec.Hash(System.Text.Encoding.UTF8.GetBytes(gameHash + "/native-schema-2")))) throw new InvalidOperationException("Сохранение SummerCoop сделано другой версией игры или мода");
            preparedWorldId = preparedWorld == null ? Guid.NewGuid().ToString("N") : preparedWorld.Id;
            PrepareKeys(preparedWorldId); ConfigureLoad();
            nativeBaseline = TransferEncoding.Encode(TransferKind.NativeSave, SaveIsolation.Pack(saves.CurrentDirectory));
            sceneReadiness = new SceneReadiness();
            hostSteamPending = steam; hostPortPending = steam ? 0 : Port();
            awaitingScene = true; nativeLoaded = false; hostPending = true; sceneBegan = Time.realtimeSinceStartup; SetStatus("Загрузка мира хоста…"); Application.LoadLevel("GAME");
        }
        public void StartClient(bool steam, ulong lobbyId)
        {
            if (Session != null || awaitingScene || Application.loadedLevelName == "GAME") throw new InvalidOperationException("Подключайтесь из главного меню");
            if (steam) SteamTransport.RequireConnection();
            VerifyVanilla(); saves.Activate("client", false);
            ITransport transport = steam ? (ITransport)new SteamTransport(false, lobbyId, gameHash) : LanTransport.Join(address, Port());
            Session = new Session(transport, null, gameHash, username, password, Time.realtimeSinceStartup); ConfigureSession();
            SetStatus("Подключение к хосту…");
        }
        private static void ConfigureLoad()
        {
            FsmVariables g = FsmVariables.GlobalVariables;
            FsmString slot = g.FindFsmString("SaveSlot"); if (slot != null) slot.Value = "defaultES2File.txt";
            FsmBool perma = g.FindFsmBool("OptionsPermaDeath"); if (perma != null) perma.Value = false;
            perma = g.FindFsmBool("PlayerPermaDeath"); if (perma != null) perma.Value = false;
            foreach (string key in new string[] { "PlayerInMenu", "PlayerStop", "PlayerSleeps", "PlayerSeated", "PlayerCarControl", "PlayerVanControl", "PlayerMuscleControl" }) { FsmBool v = g.FindFsmBool(key); if (v != null) v.Value = false; }
            foreach (string key in new string[] { "PlayerFirstName", "PlayerLastName", "PlayerName" }) { FsmString v = g.FindFsmString(key); if (v != null && v.Value == "") v.Value = "Coop"; }
        }
        public void PreLoad()
        {
            if (!awaitingScene || Application.loadedLevelName != "GAME") return;
            ConfigureLoad(); NativeHooks.ResetScene(); if (NativeBaseline) return;
            pendingRegistry = new WorldRegistry(hostPending, gameHash); pendingRegistry.IndexScene();
        }
        public void Loaded() { if (awaitingScene && Application.loadedLevelName == "GAME") { nativeLoaded = true; sceneBegan = Time.realtimeSinceStartup; } }
        public void Menu()
        {
            if (Application.loadedLevelName != "GAME" && Session == null && !awaitingScene) saves.Restore();
            if (leaving) { leaving = false; saves.Restore(); Registry = null; Adapter = null; authority = null; }
            visible = true;
            if (pendingLobby != 0) { ulong lobby = pendingLobby; pendingLobby = 0; StartClient(true, lobby); }
        }
        private void ConfigureSession()
        {
            // Apply consumes the delta synchronously and retains immutable property
            // maps, never the temporary world dictionary itself.
            Session.ReuseDeltaContainers = true;
            Session.BackgroundSnapshots = true;
            Session.Status = SetStatus; Session.Ended = OnEnded;
            if (Session.IsHost) Session.SetNativeSaveSeed(nativeBaseline);
            Session.NativeSaveReceived = delegate(byte[] bytes)
            {
                SaveIsolation.Unpack(saves.CurrentDirectory, bytes); PrepareKeys(Session.World.Id); ConfigureLoad();
                sceneReadiness = new SceneReadiness();
                awaitingScene = true; nativeLoaded = false; hostPending = false; sceneBegan = Time.realtimeSinceStartup; SetStatus("Загрузка общего мира…"); Application.LoadLevel("GAME");
            };
            Session.WorldReceived = delegate(World world, bool full)
            {
                Registry.Apply(world, full); if (avatars != null) avatars.Reconcile(Session.World, Session.LocalId); ResumeHand(); ReconcileHand(); ReplaySharedActions();
                if (!full && Session.World.SleepEpoch > sleepEpoch) { FsmFloat fatigue = FsmVariables.GlobalVariables.FindFsmFloat("PlayerFatigue"); if (fatigue != null) fatigue.Value = 0; }
                sleepEpoch = Session.World.SleepEpoch;
                if (full) SetStatus("Подключено. F6 — меню сессии");
            };
            Session.MotionReceived = delegate(MotionFrame frame) { Registry.ReceiveMotion(frame); };
            Session.PoseReceived = delegate(Pose pose) { if (avatars != null) avatars.Pose(pose); };
            Session.CommandCompleted = OnCommand;
        }
        private void FinishLoad()
        {
            if (NativeBaseline)
            {
                NativeHooks.Uninstall(); awaitingScene = false; visible = false; NativeBaselineReady = true;
                SetStatus("Диагностика нативной игры без синхронизации"); gameObject.AddComponent<IntegrationProbe>(); return;
            }
            if (personalKeys != null) personalKeys.RestoreGlobals();
            Registry = pendingRegistry ?? new WorldRegistry(hostPending, gameHash); pendingRegistry = null; Registry.Discover(true);
            Registry.PrepareSpawner();
            if (toolRecovery != null) toolRecovery.Dispose();
            toolRecovery = new NativeToolRecovery(Registry);
            avatars = new AvatarView();
            localPlayer = GameObject.Find("PLAYER"); localCamera = Camera.main;
            if (hostPending)
            {
                World world = new World { Id = preparedWorldId, GameHash = gameHash, SchemaHash = Registry.SchemaHash };
                store = new CheckpointStore(Path.Combine(root, "world")) { ImmutableEntities = true };
                World recovered = preparedWorld; preparedWorld = null;
                if (recovered == null && File.Exists(Path.Combine(Path.Combine(root, "world"), "world.scw"))) throw new InvalidOperationException("Контрольная точка и её резервная копия несовместимы или повреждены. Сохраните этот профиль и выберите отдельный новый профиль.");
                if (recovered != null)
                {
                    if (recovered.GameHash != gameHash || recovered.SchemaHash != Registry.SchemaHash) throw new InvalidOperationException("Сохранение SummerCoop сделано другой версией игры или мода");
                    recovered.Players.Clear();
                    int personal = LocalHierarchy.RemoveFromCheckpoint(recovered);
                    if (personal > 0) Debug.Log("[SummerCoop] Recovered checkpoint: excluded " + personal + " local player/menu entities; shared objects retained.");
                    Registry.Apply(recovered, true); world = recovered;
                }
                Registry.RepairInstalledMounts();
                Registry.Capture(world, true); world.EnableChangeTracking(); Adapter = new NativeAdapter(Registry); authority = new Authority(world, Adapter, store);
                // Admit peers only after the native scene is ready; early listeners let clients time out during load.
                pendingTransport = hostSteamPending ? (ITransport)new SteamTransport(true, 0, gameHash) : LanTransport.Host(hostPortPending);
                Session = new Session(pendingTransport, authority, gameHash, username, password, Time.realtimeSinceStartup); pendingTransport = null; ConfigureSession();
                Session.SceneLoaded(Registry.SchemaHash); store.Checkpoint(Session.World);
                SetStatus("Мир открыт: " + Registry.Objects.Count + " объектов, " + Registry.Fsms.Count + " FSM");
            }
            else Session.SceneLoaded(Registry.SchemaHash);
            awaitingScene = false; visible = false; captureTime = checkpointTime = refreshTime = Time.realtimeSinceStartup;
            if (Argument("--summercoop-test-report") != "")
            {
                using (StreamWriter w = new StreamWriter(Path.Combine(root, "registry.tsv")))
                    foreach (ObjectBinding b in Registry.Objects.Values) w.WriteLine(b.Id + "\t" + b.Key + "\t" + GameProbe.PathOf(b.Object.transform));
                Registry.DumpPrefabs(Path.Combine(root, "prefabs.txt"));
            }
            if (Argument("--summercoop-test-report") != "") gameObject.AddComponent<IntegrationProbe>();
            if (Argument("--summercoop-test-report") != "" && Argument("--summercoop-test-native-input") == "yes") gameObject.AddComponent<NativeInputProbe>();
            if (Argument("--summercoop-test-report") != "" && Argument("--summercoop-test-save-restore") == "yes") gameObject.AddComponent<SaveRestoreProbe>();
            if (Argument("--summercoop-test-report") != "" && Argument("--summercoop-test-real-input") != "") gameObject.AddComponent<RealInputProbe>();
            if (Argument("--summercoop-test-report") != "" && Argument("--summercoop-test-native-menu") == "yes") gameObject.AddComponent<NativeMenuProbe>();
            if (InteractionProbe.Enabled) gameObject.AddComponent<InteractionProbe>();
        }
        private void RefreshHooks() { NativeHooks.UpdateMode = Registry != null && Active ? (Joining ? 1 : Session.IsHost ? 0 : 2) : 0; }
        private void Update()
        {
            if (TraceAllocations && measuredUpdateEnd && Active && !Session.IsHost) HeapEnd(5, previousUpdateEnd);
            long updateBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            Array.Clear(LastStages, 0, LastStages.Length);
            try
            {
                if (Input.GetKeyDown(KeyCode.F6)) visible = !visible;
                RefreshSteam();
                if (Active && personalKeys != null && Time.realtimeSinceStartup >= keyCaptureTime)
                { keyCaptureTime = Time.realtimeSinceStartup + 1; personalKeys.Capture(); }
                if (awaitingScene && sceneReadiness.Observe(Time.frameCount, nativeLoaded && Application.loadedLevelName == "GAME", GameObject.Find("PLAYER") != null, Camera.main != null)) FinishLoad();
                long stage = System.Diagnostics.Stopwatch.GetTimestamp();
                HeapStamp heapStage = HeapBegin();
                if (Session != null) Session.Update(Time.realtimeSinceStartup);
                if (recoverInput != null && Time.frameCount > recoverAfterFrame)
                {
                    PlayMakerFSM component = recoverInput; string state = recoverState; recoverInput = null;
                    if (Active && component.Fsm.GetState(state) != null) NativeAdapter.ExecuteState(new FsmBinding { Component = component }, state);
                }
                RefreshHooks();
                HeapEnd(0, heapStage);
                LastStages[0] = Elapsed(stage);
                if (!Active || Registry == null) return;
                heapStage = HeapBegin();
                if (avatars != null) { if (Time.realtimeSinceStartup - avatarTime > 0.5f) { avatars.Reconcile(Session.World, Session.LocalId); avatarTime = Time.realtimeSinceStartup; } avatars.Update(Time.unscaledDeltaTime); }
                HeapEnd(1, heapStage);
                if (Session.IsHost)
                {
                    store.CheckBackground();
                    stage = System.Diagnostics.Stopwatch.GetTimestamp(); Adapter.Tick(Session.World); LastStages[1] = Elapsed(stage);
                    stage = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool discover = Time.realtimeSinceStartup - refreshTime > 3;
                    Registry.DiscoverIncremental(discover); if (discover) refreshTime = Time.realtimeSinceStartup;
                    LastStages[2] = Elapsed(stage);
                    stage = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (!Joining) Registry.CaptureIncremental(Session.World);
                    LastStages[3] = Elapsed(stage);
                    if (!Joining && Time.realtimeSinceStartup - captureTime > 0.2f)
                    {
                        if (Registry.ConsumeChanges())
                        {
                            stage = System.Diagnostics.Stopwatch.GetTimestamp();
                            Session.World.Revision++; store.CommitNative(Session.World);
                            LastStages[4] = Elapsed(stage);
                            stage = System.Diagnostics.Stopwatch.GetTimestamp(); Session.Publish(); LastStages[5] = Elapsed(stage);
                        }
                        captureTime = Time.realtimeSinceStartup;
                    }
                    if (!Joining && Time.realtimeSinceStartup - checkpointTime > 60) { stage = System.Diagnostics.Stopwatch.GetTimestamp(); if (store.BeginCheckpoint(Session.World)) checkpointTime = Time.realtimeSinceStartup; LastStages[7] = Elapsed(stage); }
                }
                else
                {
                    stage = System.Diagnostics.Stopwatch.GetTimestamp(); heapStage = HeapBegin(); Registry.Interpolate(Time.unscaledDeltaTime); SendDriving(); HeapEnd(2, heapStage);
                    heapStage = HeapBegin();
                    Registry.EnforceVariables(Session.World);
                    HeapEnd(3, heapStage);
                    LastStages[3] = Elapsed(stage);
                }
                if (!Joining && Time.realtimeSinceStartup - poseTime > 0.05f)
                {
                    stage = System.Diagnostics.Stopwatch.GetTimestamp();
                    heapStage = HeapBegin();
                    LastMotionBodies = 0;
                    if (localPlayer == null) localPlayer = GameObject.Find("PLAYER");
                    if (localCamera == null || !localCamera.isActiveAndEnabled) localCamera = Camera.main;
                    if (localPlayer != null) Session.SendPose(LocalPose());
                    if (Session.IsHost && Session.World.Players.Count > 1)
                    {
                        long motionBegan = System.Diagnostics.Stopwatch.GetTimestamp(); List<MotionFrame> frames = Registry.CaptureMotion(); LastStages[8] = Elapsed(motionBegan);
                        motionBegan = System.Diagnostics.Stopwatch.GetTimestamp(); foreach (MotionFrame frame in frames) { LastMotionBodies += frame.Bodies.Count; Session.SendMotion(frame); } LastStages[9] = Elapsed(motionBegan);
                    }
                    poseTime = Time.realtimeSinceStartup;
                    HeapEnd(4, heapStage);
                    LastStages[6] = Elapsed(stage);
                }
                if (Application.loadedLevelName != "GAME") Leave("Мир закрыт");
                else if (Input.GetButtonDown("Use")) NativeUseInput.FromView(Registry, localCamera);
            }
            catch (Exception e) { Fail(e); }
            finally { LastUpdateMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - updateBegan) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (TraceAllocations) { previousUpdateEnd = HeapBegin(); measuredUpdateEnd = true; } }
        }
        private void SendDriving()
        {
            Player player; if (!Session.World.Players.TryGetValue(Session.LocalId, out player) || player.Seat == "" || Time.realtimeSinceStartup - poseTime < 0.05f) return;
            FsmBinding seat = Registry.FsmOf(player.Seat); if (seat == null || seat.Key.IndexOf("DriveTrigger", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (seat.Component.transform.root.name == "BOAT")
            {
                Session.Submit(new Command { Kind = CommandKind.Drive, Target = player.Seat, Vector = new V3(Input.GetAxisRaw("Horizontal"), Mathf.Clamp01(Input.GetAxisRaw("Vertical")), 0), Argument = "0|1|0" }); return;
            }
            CarController car = seat.Component.transform.root.GetComponent<CarController>() ?? seat.Component.transform.root.GetComponentInChildren<CarController>(); if (car == null) return;
            Drivetrain drive = car.GetComponent<Drivetrain>();
            object[] controls = { 0f, 0f, 0f, 0f, 0f, false, drive == null ? 1 : drive.gear };
            VehicleInput.Invoke(car, controls);
            Session.Submit(new Command { Kind = CommandKind.Drive, Target = player.Seat, Vector = new V3((float)controls[2], (float)controls[0], (float)controls[1]), Number = (float)controls[3], Argument = ((float)controls[4]).ToString("R", CultureInfo.InvariantCulture) + "|" + controls[6] + "|" + ((bool)controls[5] ? "1" : "0") });
        }
        public bool RequestState(FsmBinding b, string state)
        {
            if (!Active || Joining || b.Component == null || !Session.World.Entities.ContainsKey(b.Id)) return false;
            if (sharedRequests.ContainsTarget(b.Id)) return true;
            Player actor = Session.World.Players[Session.LocalId];
            Vector3 position = localPlayer == null ? WorldRegistry.To(actor.Position) : localPlayer.transform.position;
            if (Vector3.Distance(position, b.Component.transform.position) > 5) return false;
            bool input = Input.anyKey || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Math.Abs(Input.GetAxis("Mouse ScrollWheel")) > 0.001f
                || NativeInputProbe.InputActive && Argument("--summercoop-test-native-input") == "yes" && Argument("--summercoop-test-report") != "";
            if (!input && !(b.Component.FsmName == "Assembly" && actor.Held != "")) return false;
            float last; if (requests.TryGetValue(b.Id, out last) && Time.realtimeSinceStartup - last < 0.08f) return false; requests[b.Id] = Time.realtimeSinceStartup;
            CommandKind kind = CommandKind.Interact;
            if (b.Component.FsmName == "Assembly") kind = CommandKind.Attach;
            else if (b.Component.FsmName == "Removal") kind = CommandKind.Detach;
            else if (b.Component.FsmName == "Assemble" && b.Key.IndexOf("Wiring", StringComparison.OrdinalIgnoreCase) >= 0) kind = CommandKind.Wire;
            else if (b.Component.FsmName == "Screw" && b.Component.FsmVariables.FindFsmInt("Stage") != null) kind = CommandKind.Bolt;
            else if (b.Component.FsmName == "Buy" || b.Key.IndexOf("CashRegister", StringComparison.OrdinalIgnoreCase) >= 0) kind = CommandKind.Purchase;
            if (kind == CommandKind.Attach) deferredRelease.Cancel();
            FsmFloat wrench = FsmVariables.GlobalVariables.FindFsmFloat("ToolWrenchSize");
            SendActionPose();
            Command command = new Command { Kind = kind, Target = b.Id, Argument = state, Number = kind == CommandKind.Bolt && wrench != null ? wrench.Value : 0 };
            CommandResult result = Session.Submit(command);
            if (result == null) sharedRequests.Add(command);
            return true;
        }
        private void SendActionPose()
        {
            if (Session == null || !Active || localPlayer == null) return;
            Session.SendPose(LocalPose(), true);
        }
        private Pose LocalPose()
        {
            Pose pose = new Pose { Position = WorldRegistry.From(localPlayer.transform.position), Rotation = WorldRegistry.From(localPlayer.transform.rotation), Pitch = localCamera == null ? 0 : localCamera.transform.eulerAngles.x };
            Player actor;
            if (Registry != null && Session.World.Players.TryGetValue(Session.LocalId, out actor) && actor.Held != "")
            {
                GameObject held = Registry.ObjectOf(actor.Held);
                if (held != null) { pose.Held = actor.Held; pose.HeldPosition = WorldRegistry.From(held.transform.position); pose.HeldRotation = WorldRegistry.From(held.transform.rotation); }
            }
            return pose;
        }
        private void ReplaySharedActions()
        {
            if (Session == null || Session.IsHost || Registry == null) return;
            foreach (PendingNativeAction action in sharedRequests.Ready(Session.World.Revision))
                NativeHooks.ReplayPersonal(Registry.FsmOf(action.Target), action.State);
        }
        public bool PersonalState(Fsm fsm, FsmState state)
        {
            if (!Active || Joining || NativeHooks.Replaying || Adapter != null && Adapter.Executing) return true;
            if (fsm.Name == "Death")
            {
                foreach (FsmStateAction a in state.Actions) if (a.GetType().Name == "LoadLevel" || a.GetType().Name == "DeleteFile") { if (Time.realtimeSinceStartup - respawnTime > 2) { respawnTime = Time.realtimeSinceStartup; Respawn(); } return false; }
            }
            FsmBinding binding = Registry.Binding(fsm);
            if (binding != null && binding.Key.IndexOf("SleepTrigger", StringComparison.Ordinal) >= 0 && (state.Name == "Get positions" || state.Name == "Player in car"))
            {
                Session.Submit(new Command { Kind = CommandKind.Sleep, Number = 1 }); SetStatus("Готовность ко сну отправлена. Нужны все игроки."); return false;
            }
            if (binding != null && fsm.Name == "PlayerTrigger")
            {
                Player player = Session.World.Players[Session.LocalId];
                if (state.Name == "Player in car" || state.Name == "Reset view" || state.Name.StartsWith("Get positions", StringComparison.OrdinalIgnoreCase))
                {
                    if (player.Seat == binding.Id) return true;
                    return RequestPersonal(CommandKind.EnterSeat, binding.Id, fsm, state.Name);
                }
                if (state.Name == "Create player" || state.Name == "Player out of car")
                {
                    if (player.Seat == binding.Id) Session.Submit(new Command { Kind = CommandKind.LeaveSeat, Target = binding.Id });
                    return true;
                }
            }
            if (fsm.Name == "PickUp")
            {
                if (state.Name == "Set pivot" || state.Name == "Set pivot 2" || state.Name == "Tool picked" || state.Name == "Ragdoll picked")
                {
                    FsmGameObject picked = fsm.Variables.FindFsmGameObject("PickedObject");
                    string target = Registry.IdOf(picked == null ? null : picked.Value); if (target == "") return true;
                    Player p = Session.World.Players[Session.LocalId]; if (p.Held == target) return true;
                    return RequestPersonal(CommandKind.Take, target, fsm, state.Name);
                }
                if (state.Name.StartsWith("Drop ", StringComparison.Ordinal))
                {
                    Player p = Session.World.Players[Session.LocalId];
                    if (!Session.IsHost && p.Held != "")
                    {
                        if (!sharedRequests.ContainsKind(CommandKind.Attach)) deferredRelease.Queue(fsm, state.Name, p.Held, Time.frameCount);
                        return false;
                    }
                    SendActionPose(); if (p.Held != "") Session.Submit(new Command { Kind = CommandKind.Drop, Target = p.Held });
                    return true;
                }
            }
            return true;
        }
        private bool RequestPersonal(CommandKind kind, string target, Fsm fsm, string state)
        {
            if (requestedSeat != "") return false;
            SendActionPose();
            Command command = new Command { Kind = kind, Target = target };
            CommandResult result = Session.Submit(command); if (result != null) { if (!result.Accepted) RecoverInput(fsm); return result.Accepted; }
            requestedSeat = target; requestedState = state; requestedSequence = command.Sequence; requestedKind = kind; requestedFsm = fsm; requestAccepted = false;
            SetStatus("Ожидание подтверждения хоста…"); return false;
        }
        private void OnCommand(CommandResult result)
        {
            sharedRequests.Complete(result); ReplaySharedActions();
            if (!result.Accepted) SetStatus(result.Reason);
            if (requestedSeat == "" || Session == null || Session.IsHost || result.Sequence != requestedSequence) return;
            requestAccepted = result.Accepted;
            if (!result.Accepted) { RecoverInput(requestedFsm); requestedSeat = requestedState = ""; requestedSequence = 0; requestedFsm = null; return; }
            ResumeHand();
        }
        private void RecoverInput(Fsm fsm)
        {
            if (fsm == null) return;
            FsmBinding binding = Registry.Binding(fsm);
            string state = fsm.Name == "PickUp" ? "Look for object" : binding == null ? "" : binding.WaitingState;
            if (state == "") return;
            recoverInput = fsm.Owner as PlayMakerFSM; recoverState = state; recoverAfterFrame = Time.frameCount;
        }
        private void ClearNativeRequests()
        {
            requestedSeat = requestedState = nativeHeld = ""; requestedSequence = 0; requestedFsm = null; requestAccepted = false;
            nativeHand = recoverInput = null; requests.Clear();
            sharedRequests.Clear();
            deferredRelease.Cancel();
            if (toolRecovery != null) { toolRecovery.Dispose(); toolRecovery = null; }
        }
        private void LateUpdate()
        {
            if (!Active || Session.IsHost || Joining) return;
            try
            {
                Player actor; if (!Session.World.Players.TryGetValue(Session.LocalId, out actor)) return;
                Fsm hand; string state, target;
                if (!deferredRelease.Take(Time.frameCount, actor.Held, sharedRequests.ContainsKind(CommandKind.Attach), out hand, out state, out target)) return;
                SendActionPose(); Session.Submit(new Command { Kind = CommandKind.Drop, Target = target });
                bool replaying = NativeHooks.Replaying; NativeHooks.Replaying = true;
                try
                {
                    PlayMakerFSM component = hand.Owner as PlayMakerFSM;
                    if (component != null) NativeAdapter.ExecuteState(new FsmBinding { Component = component }, state);
                }
                finally { NativeHooks.Replaying = replaying; }
            }
            catch (Exception error) { Fail(error); }
        }
        private void ResumeHand()
        {
            if (requestedSeat == "" || !requestAccepted || Session == null) return;
            // Resume the local hand animation only after the host grants ownership.
            Player p; if (Session.World.Players.TryGetValue(Session.LocalId, out p) && (requestedKind == CommandKind.Take ? p.Held == requestedSeat : p.Seat == requestedSeat))
            {
                if (requestedKind == CommandKind.Take) Registry.PrepareLocalCarry(requestedSeat);
                NativeHooks.Replaying = true;
                try { PlayMakerFSM component = requestedFsm.Owner as PlayMakerFSM; if (component != null) NativeAdapter.ExecuteState(new FsmBinding { Component = component }, requestedState); }
                finally { NativeHooks.Replaying = false; }
                requestedSeat = requestedState = ""; requestedSequence = 0; requestAccepted = false;
                requestedFsm = null; SetStatus("Подключено. F6 — меню сессии");
            }
        }
        private void ReconcileHand()
        {
            if (Session == null || Session.IsHost) return;
            Player actor; if (!Session.World.Players.TryGetValue(Session.LocalId, out actor)) return;
            if (nativeHeld != "" && actor.Held == "")
            {
                if (nativeHand == null)
                    nativeHand = NativeHandLayers.CurrentHand();
                if (nativeHand != null && (nativeHand.ActiveStateName == "Part picked" || nativeHand.ActiveStateName == "Item picked" || nativeHand.ActiveStateName == "Tool picked"))
                {
                    bool replaying = NativeHooks.Replaying; NativeHooks.Replaying = true;
                    try { NativeAdapter.ExecuteState(new FsmBinding { Component = nativeHand }, "Wait"); }
                    finally { NativeHooks.Replaying = replaying; }
                }
            }
            nativeHeld = actor.Held;
        }
        private void OnInvite(GameLobbyJoinRequested_t data)
        {
            try
            {
                lobbyText = data.m_steamIDLobby.m_SteamID.ToString(CultureInfo.InvariantCulture);
                if (Application.loadedLevelName == "GAME" || awaitingScene) { pendingLobby = data.m_steamIDLobby.m_SteamID; Leave("Переход в приглашённую сессию"); }
                else { if (Session != null) Leave("Переход в приглашённую сессию"); StartClient(true, data.m_steamIDLobby.m_SteamID); }
            }
            catch (Exception e) { Fail(e); }
        }
        public void Leave(string reason)
        {
            if (Session == null && pendingTransport == null && !awaitingScene) return;
            if (personalKeys != null && Application.loadedLevelName == "GAME" && nativeLoaded) personalKeys.Capture();
            try { if (Session != null && Session.IsHost && Registry != null) { Registry.Capture(Session.World, true); NativeSave.Capture(Registry, saves.CurrentDirectory); store.Checkpoint(Session.World); } }
            finally
            {
                if (Session != null) { Session.Ended = null; Session.Close(reason); Session = null; }
                ClearNativeRequests();
                NativeHooks.UpdateMode = 0;
                DisposeStore();
                if (pendingTransport != null) { pendingTransport.Dispose(); pendingTransport = null; }
                if (avatars != null) { avatars.Dispose(); avatars = null; }
                Joining = false; Time.timeScale = previousScale; awaitingScene = nativeLoaded = false;
                if (Application.loadedLevelName == "GAME") { leaving = true; Application.LoadLevel("MainMenu"); }
                else { saves.Restore(); Registry = null; Adapter = null; }
                SetStatus(reason); visible = true;
            }
        }
        private void OnEnded(string reason)
        {
            if (personalKeys != null && Application.loadedLevelName == "GAME" && nativeLoaded) personalKeys.Capture();
            NativeHooks.UpdateMode = 0;
            Session = null; Joining = false; Time.timeScale = previousScale;
            ClearNativeRequests();
            DisposeStore();
            if (avatars != null) { avatars.Dispose(); avatars = null; }
            SetStatus(reason);
            if (Application.loadedLevelName == "GAME") { leaving = true; Application.LoadLevel("MainMenu"); }
            else saves.Restore(); visible = true;
        }
        private void Fail(Exception e)
        {
            SetStatus("SummerCoop: " + e.Message); Debug.LogException(e);
            if (root != null) { try { File.AppendAllText(Path.Combine(root, "errors.log"), DateTime.UtcNow.ToString("o") + " " + e + Environment.NewLine); } catch (IOException) { } }
            if (!fatal) { fatal = true; try { Leave("Ошибка: " + e.Message); } catch (Exception stop) { Debug.LogException(stop); } }
            visible = true;
        }
        private void SetStatus(string value) { if (status == value) return; status = value; ModConsole.Print("SummerCoop: " + value); }
        private void PrepareKeys(string world)
        {
            string identity = SteamTransport.Available() ? "steam/" + SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture) : "lan/" + username;
            personalKeys = new NativeKeys(root, saves.CurrentDirectory, world, identity);
        }
        public void SaveKeysNow() { if (personalKeys != null && nativeLoaded) personalKeys.Capture(); }
        public void RestoreTools()
        {
            if (!Active || Joining || Application.loadedLevelName != "GAME" || Registry == null || toolRecovery == null)
                throw new InvalidOperationException("Восстановление инструментов доступно после загрузки мира.");
            int count = toolRecovery.Restore(Registry);
            SetStatus(count == 0 ? "Чемоданчик с инструментами не найден в предметах игрока."
                : "Инструменты восстановлены: " + count + ". Откройте чемоданчик и включите режим инструментов (F1).");
        }
        private void DisposeStore() { CheckpointStore current = store; store = null; if (current != null) { try { current.Dispose(); } catch (IOException e) { Debug.LogException(e); } } }
        private void OnApplicationQuit()
        {
            try { if (personalKeys != null && nativeLoaded && Application.loadedLevelName == "GAME") personalKeys.Capture(); if (Session != null && Session.IsHost && Registry != null) { Registry.Capture(Session.World, true); NativeSave.Capture(Registry, saves.CurrentDirectory); store.Checkpoint(Session.World); } }
            catch (Exception e) { Debug.LogException(e); }
            if (Session != null) Session.Dispose();
            if (pendingTransport != null) pendingTransport.Dispose();
            DisposeStore();
            // Keep the isolated ES2 path until the engine has completed ALL native quit callbacks.
        }
        private void OnDestroy() { if (menuSkin != null) menuSkin.Dispose(); if (invitation != null) invitation.Dispose(); if (avatars != null) avatars.Dispose(); if (Instance == this) { Instance = null; NativeHooks.Uninstall(); } }
        private void OnGUI()
        {
            if (!visible || saves == null) return;
            if (menuSkin == null) menuSkin = new CoopMenuSkin();
            GUISkin previousSkin = GUI.skin; Matrix4x4 previousMatrix = GUI.matrix;
            Color previousColor = GUI.color, previousBackground = GUI.backgroundColor, previousContent = GUI.contentColor;
            bool previousEnabled = GUI.enabled;
            try
            {
                GUI.skin = menuSkin.Skin; GUI.color = GUI.backgroundColor = GUI.contentColor = Color.white; GUI.enabled = true;
                float scale = Mathf.Min(1, Mathf.Min(Screen.width / 640f, Screen.height / 650f));
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1));
                panel.x = Mathf.Clamp(panel.x, 0, Mathf.Max(0, Screen.width / scale - panel.width));
                panel.y = Mathf.Clamp(panel.y, 0, Mathf.Max(0, Screen.height / scale - panel.height));
                panel = GUILayout.Window(516750, panel, DrawPanel, "", GUILayout.Width(500));
            }
            finally { GUI.skin = previousSkin; GUI.matrix = previousMatrix; GUI.color = previousColor; GUI.backgroundColor = previousBackground; GUI.contentColor = previousContent; GUI.enabled = previousEnabled; }
        }
        private void DrawPanel(int id)
        {
            GUILayout.Label("SummerCoop " + Protocol.ModVersion);
            GUILayout.Label("Экспериментальная версия. Полная сборка Satsuma проходит проверку.");
            GUILayout.Label(status);
            try
            {
                if (Active)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Toggle(menuTab == 0, "Сессия", GUI.skin.button)) menuTab = 0;
                    if (GUILayout.Toggle(menuTab == 1, "Игрок", GUI.skin.button) && menuTab != 1) { menuTab = 1; ReadNeeds(); }
                    if (GUILayout.Toggle(menuTab == 2, "Спаун", GUI.skin.button)) menuTab = 2;
                    GUILayout.EndHorizontal();
                }
                else menuTab = 0;
                if (Active && menuTab == 1) DrawPlayerControls();
                else if (Active && menuTab == 2) DrawSpawnControls();
                else if (Session == null && !awaitingScene)
                {
                    GUILayout.BeginHorizontal(); GUILayout.Label("Имя", GUILayout.Width(90)); username = GUILayout.TextField(username, 48); GUILayout.EndHorizontal();
                    importSave = GUILayout.Toggle(importSave, "Создать мир из копии одиночного сохранения");
                    if (steamError != null) GUILayout.Label("Steam: " + steamError);
                    GUILayout.BeginHorizontal(); GUI.enabled = steamError == null; if (GUILayout.Button("Хост через Steam")) { fatal = false; StartHost(true); } GUI.enabled = true;
                    if (GUILayout.Button("Хост LAN / VPN")) { fatal = false; StartHost(false); } GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal(); GUILayout.Label("IP хоста", GUILayout.Width(90)); address = GUILayout.TextField(address, 255); GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal(); GUILayout.Label("Порт", GUILayout.Width(90)); portText = GUILayout.TextField(portText, 5); GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal(); GUILayout.Label("Пароль LAN", GUILayout.Width(90)); password = GUILayout.PasswordField(password, '*', 64); GUILayout.EndHorizontal();
                    if (GUILayout.Button("Подключиться LAN / VPN")) { fatal = false; StartClient(false, 0); }
                    GUILayout.BeginHorizontal(); GUILayout.Label("Steam lobby", GUILayout.Width(90)); lobbyText = GUILayout.TextField(lobbyText, 20); GUILayout.EndHorizontal();
                    GUI.enabled = steamError == null;
                    if (GUILayout.Button("Подключиться к Steam lobby")) { ulong lobby; if (!ulong.TryParse(lobbyText, out lobby)) throw new ArgumentException("Нужен номер лобби Steam"); fatal = false; StartClient(true, lobby); }
                    GUI.enabled = true;
                }
                else
                {
                    GUILayout.Label(Session == null ? "Загрузка…" : "Состояние: " + Session.Phase);
                    if (Session != null)
                    {
                        if (Session.World != null) GUILayout.Label("Игроков: " + Session.World.Players.Count + "/4. Общие деньги: " + Session.World.Money.ToString("0.00", CultureInfo.InvariantCulture) + " mk");
                        SteamTransport steam = SessionTransport() as SteamTransport;
                        if (steam != null && steam.IsHost && !steam.LobbyReady)
                        {
                            GUILayout.Label(steam.LobbyStatus);
                            if (steam.CanRetryLobby && GUILayout.Button("Повторить создание лобби Steam")) steam.RetryLobby();
                        }
                        if (steam != null && steam.IsHost && steam.LobbyReady) { GUILayout.Label("Лобби: " + steam.Lobby.m_SteamID); if (GUILayout.Button("Пригласить друзей через Steam")) steam.Invite(); }
                        if (Active)
                        {
                            Player p = Session.World.Players[Session.LocalId];
                            if (GUILayout.Button("Восстановить инструменты в чемоданчиках")) RestoreTools();
                            if (GUILayout.Button(p.SleepReady ? "Отменить готовность ко сну" : "Готов ко сну (нужны все игроки)")) Session.Submit(new Command { Kind = CommandKind.Sleep, Number = p.SleepReady ? 0 : 1 });
                            if (GUILayout.Button("Возродиться дома")) Respawn();
                        }
                    }
                    if (GUILayout.Button("Закрыть сессию")) Leave("Сессия закрыта");
                }
                if (GUILayout.Button("Скрыть (F6)")) visible = false;
            }
            catch (Exception e) { SetStatus(e.Message); }
            finally { GUI.enabled = true; }
            GUI.DragWindow(new Rect(0, 0, panel.width, 40));
        }
        private void ReadNeeds()
        {
            for (int i = 0; i < needValues.Length; i++) { float value; if (NativePlayerControls.Read(PlayerControls.Needs[i], out value)) needValues[i] = PlayerControls.ClampPercent(value); }
        }
        private void DrawPlayerControls()
        {
            GUILayout.Label("Потребности и эффекты вашего персонажа");
            if (GUILayout.Button("Убрать опьянение, похмелье, ожоги и аллергию"))
            { int count = NativePlayerControls.ClearEffects(); SetStatus("Негативные эффекты сняты (" + count + ")."); }
            for (int i = 0; i < needValues.Length; i++)
            {
                GUILayout.BeginHorizontal(); GUILayout.Label(PlayerControls.NeedLabels[i], GUILayout.Width(160));
                needValues[i] = GUILayout.HorizontalSlider(needValues[i], 0, 100);
                GUILayout.Label(needValues[i].ToString("0") + "%", GUILayout.Width(45)); GUILayout.EndHorizontal();
            }
            GUILayout.Label("0% — потребность удовлетворена; 100% — максимум.");
            if (GUILayout.Button("Применить потребности"))
            { for (int i = 0; i < needValues.Length; i++) NativePlayerControls.SetNeed(PlayerControls.Needs[i], needValues[i]); SetStatus("Потребности изменены."); }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Обнулить все потребности")) { NativePlayerControls.ClearNeeds(); ReadNeeds(); SetStatus("Все потребности обнулены."); }
            if (GUILayout.Button("Прочитать текущие")) ReadNeeds();
            GUILayout.EndHorizontal();
        }
        private void DrawSpawnControls()
        {
            GUILayout.Label("Выберите предмет. Он появится перед вашим персонажем.");
            GUILayout.BeginHorizontal(); GUILayout.Label("Поиск", GUILayout.Width(70)); spawnSearch = GUILayout.TextField(spawnSearch, 80); GUILayout.EndHorizontal();
            SpawnChoice selected = null;
            spawnScroll = GUILayout.BeginScrollView(spawnScroll, GUILayout.Height(220));
            try
            {
                foreach (SpawnChoice choice in Registry.Spawner.Choices)
                {
                    if (choice.Id == selectedSpawn) selected = choice;
                    if (spawnSearch != "" && choice.Name.IndexOf(spawnSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (GUILayout.Toggle(selectedSpawn == choice.Id, choice.Name, GUI.skin.button)) { selectedSpawn = choice.Id; selected = choice; }
                }
            }
            finally { GUILayout.EndScrollView(); }
            if (selected != null && selected.HasCondition)
            {
                GUILayout.Label("Состояние: " + spawnCondition.ToString("0") + "%"); spawnCondition = GUILayout.HorizontalSlider(spawnCondition, 0, 100);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Сломанная (0%)")) spawnCondition = 0;
                if (GUILayout.Button("Изношенная (25%)")) spawnCondition = 25;
                if (GUILayout.Button("Новая (100%)")) spawnCondition = 100;
                GUILayout.EndHorizontal();
            }
            else GUILayout.Label("У выбранного предмета нет шкалы износа.");
            if (selected != null && selected.Unique) GUILayout.Label("У этой детали один слот в игре. Вернётся её свободный экземпляр; установленную деталь сначала нужно снять.");
            GUI.enabled = selected != null;
            if (GUILayout.Button(selected != null && selected.Unique ? "Создать / вернуть выбранную деталь" : "Создать выбранный предмет"))
            {
                SendActionPose();
                CommandResult result = Session.Submit(new Command { Kind = CommandKind.Spawn, Target = selectedSpawn, Number = spawnCondition });
                if (result != null && !result.Accepted) SetStatus(result.Reason); else SetStatus("Запрос на предмет отправлен.");
            }
            GUI.enabled = true;
        }
        private ITransport SessionTransport() { return Session == null ? null : Session.Transport; }
        public void Respawn()
        {
            if (!Active) return; Player p = Session.World.Players[Session.LocalId];
            if (p.Held != "") Session.Submit(new Command { Kind = CommandKind.Drop, Target = p.Held });
            if (p.Seat != "") Session.Submit(new Command { Kind = CommandKind.LeaveSeat, Target = p.Seat });
            GameObject player = GameObject.Find("PLAYER"); if (player == null) return;
            player.transform.parent = null; player.transform.position = new Vector3(-9.8f, 0.7f, 11.7f);
            foreach (string name in new string[] { "PlayerHunger", "PlayerThirst", "PlayerStress", "PlayerFatigue", "PlayerUrine", "PlayerDirtiness", "PlayerDrunk", "PlayerBurns" }) { FsmFloat v = FsmVariables.GlobalVariables.FindFsmFloat(name); if (v != null) v.Value = 0; }
            foreach (string name in new string[] { "PlayerStop", "PlayerSleeps", "PlayerSeated" }) { FsmBool v = FsmVariables.GlobalVariables.FindFsmBool(name); if (v != null) v.Value = false; }
        }
    }
}
