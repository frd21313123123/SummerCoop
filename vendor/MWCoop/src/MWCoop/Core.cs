using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Le composant unique du mod : boucle d'image, interface.
    public class Core : MonoBehaviour
    {
        public static Core I;
        float nextBeat, dumpAt = -1;
        bool dumpEnabled;

        void Awake()
        {
            I = this;
            Config.Load();
            MscRuntime.Configure();
            dumpEnabled = Config.GetInt("Test", "Vidage", 0) != 0 || Config.Get("Test", "VidageCibles", "").Length > 0;
            Log.Info("Core pret, profil '" + System.Environment.GetEnvironmentVariable("MWCOOP_PROFIL") + "'");
            Log.Info("sauvegardes : " + Application.persistentDataPath);
            SaveTransfer.CopyPlayerOptions();
            Session.Start();
        }

        void FixedUpdate()
        {
            Perf.FixedStart();
            Perf.Begin();
            try { VehicleSync.RevLimiterFixed(); VehicleSync.FixedUpdate(); }
            catch (System.Exception e) { if (Time.frameCount % 600 == 0) Log.Warn("voitures (physique) : " + e.Message); }
            Perf.End("(physique)");
            Perf.FixedEnd();
        }

        void LateUpdate()
        {
            Perf.Begin();
            LateUpdateBody();
            Perf.End("(poses)");
            Autotest.LateMeasure();
            Perf.Frame();
        }

        void LateUpdateBody()
        {
            // (CarVisuals d'abord : volant et levier de vitesse poses, puis les mains des avatars dessus -- elles avaient une image de retard.)
            try
            {
                Perf.MarkStart();
                CarVisuals.LateUpdate(); Perf.Mark("[poses volants]");
                Autotest.PedalTest();
                HandTools.LateUpdate(); Perf.Mark("[poses outils]");
                MscRuntime.Run(Kauppa.LateKartturi);
                PlayerSync.LateUpdate(); Perf.Mark("[poses avatars]");
                Machines.LateUpdate(); Perf.Mark("[poses machines]");
                Npcs.LateUpdate(); Perf.Mark("[poses PNJ]");
                VehicleSync.LateUpdate(); Perf.Mark("[poses voitures]");
            }
            catch (System.Exception e) { if (Time.frameCount % 600 == 0) Log.Warn("poses : " + e.Message); }
            try { AssembleIcon.LateUpdate(); }
            catch (System.Exception e) { if (Time.frameCount % 600 == 0) Log.Warn("icone de montage : " + e.Message); }
            try { Lights.LateUpdate(); }
            catch (System.Exception e) { if (Time.frameCount % 600 == 0) Log.Warn("lumieres : " + e.Message); }
            try { Studio.LateUpdate(); }
            catch (System.Exception e) { if (Time.frameCount % 600 == 0) Log.Warn("tenues (pose) : " + e.Message); }
        }

        void Update()
        {
            Perf.UpdateStart();
            if (Time.realtimeSinceStartup >= nextBeat)
            {
                nextBeat = Time.realtimeSinceStartup + 10f;
                Log.Info("images " + Time.frameCount + ", temps " + Time.time.ToString("F1")
                         + ", niveau " + Application.loadedLevelName + ", " + Session.Status
                         + (Session.T != null ? ", recu " + Session.T.BytesIn / 1024 + " Ko, envoye " + Session.T.BytesOut / 1024 + " Ko" : "")
                         // Besoins du joueur (une mort de soif ou de faim se relit dans le journal) :
                         + (PlayerSync.InGame ? ", soif " + Game.GlobalFloat("PlayerThirst").ToString("F0") + " faim " + Game.GlobalFloat("PlayerHunger").ToString("F0")
                            + " fatigue " + Game.GlobalFloat("PlayerFatigue").ToString("F0") + " vessie " + Game.GlobalFloat("PlayerUrine").ToString("F0") : ""));
            }
            if (dumpAt > 0 && Time.realtimeSinceStartup >= dumpAt)
            {
                dumpAt = -1;
                try
                {
                    string targets = Config.Get("Test", "VidageCibles", "");
                    if (targets.Length > 0 && Application.loadedLevelName == "GAME") Log.Info("cibles : " + Recon.DumpTargets(targets) + ", pieces : " + Parts.DumpIds());
                    if (Config.GetInt("Test", "Vidage", 0) == 1) Recon.DumpLevel("auto", Application.loadedLevelName != "GAME");
                    // Vidage=2 : niveau complet AVEC les parametres des actions (reference hors jeu, voir ref\LISEZMOI.md).
                    if (Config.GetInt("Test", "Vidage", 0) == 2) Recon.DumpLevel("complet", true);
                    Log.Info("globales : " + Recon.DumpGlobals());
                    Log.Info("personnages : " + Recon.DumpCharacters());
                    if (Application.loadedLevelName == "GAME") Log.Info("apparences : " + Recon.DumpLooks());
                    Log.Info("clips : " + Recon.DumpClips());
                }
                catch (System.Exception e) { Log.Error("vidage : " + e); }
            }
            foreach (Mod m in mods) Step(m.Name, m.Run);
        }

        // Modules de la boucle, dans l'ordre ; delegues crees une fois (un par module et par image sinon : allocations).
        struct Mod { public readonly string Name; public readonly System.Action Run; public Mod(string n, System.Action r) { Name = n; Run = r; } }
        static readonly Mod[] mods =
        {
            new Mod("session", Session.Update),
            new Mod("api", CoopApi.Update),
            new Mod("sauvegarde en jeu", Game.Update),
            new Mod("porte-monnaie", WalletPanel.Update),
            new Mod("triches", Cheats.Update),
            new Mod("coups de poing", Pvp.Update),
            new Mod("panneau des triches", CheatPanel.Update),
            new Mod("carte", MapRender.Update),
            new Mod("panneau carte", MapPanel.Update),
            new Mod("mods (compatibilite)", ModSecondMachtwagen.Update),
            new Mod("mods (300D)", ModMachtwagen300D.Update),
            new Mod("vocal", Voice.Update),
            new Mod("visage perso", CustomFace.Update),
            new Mod("musiques (menu)", Contenu.MenuUpdate),
            new Mod("menu", Menu.Update),
            new Mod("mscloader", MscMods.Update),
            new Mod("lanceur", MWCoop.Net.Admin.Update),
            new Mod("supports MSC", MscPlatforms.Update),
            new Mod("joueurs", PlayerSync.Update),
            new Mod("monde", World.Update),
            new Mod("radio", Radio.Update),
            new Mod("particules", Particles.Update),
            new Mod("interactions", Interactions.Update),
            new Mod("voitures", VehicleSync.Update),
            new Mod("pieces", Parts.Update),
            new Mod("objets", Props.Update),
            new Mod("argent", Wallet.Update),
            new Mod("magasin", Shop.Update),
            new Mod("peinture", Paint.Update),
            new Mod("reglages", Settings.Update),
            new Mod("liquides", Fluids.Update),
            new Mod("sauvegarde", SaveTransfer.Update),
            new Mod("consommables", Consume.Update),
            new Mod("voix", Voices.Update),
            new Mod("portieres", CarDoors.Update),
            new Mod("tableaux de bord", CarVisuals.Update),
            new Mod("passagers", Seats.Update),
            new Mod("reapparition", Respawn.Update),
            new Mod("audit", Audit.Update),
            new Mod("portes automatiques", NearDoors.Update),
            new Mod("machines", Machines.Update),
            new Mod("givre", Frost.Update),
            new Mod("remorquage", Tow.Update),
            new Mod("appels", Calls.Update),
            new Mod("vetements", Wear.Update),
            new Mod("cuisine", Cooking.Update),
            new Mod("feux", Fires.Update),
            new Mod("atelier", Garage.Update),
            new Mod("maison", Home.Update),
            new Mod("gestes", Gestures.Update),
            new Mod("outils en main", HandTools.Update),
            new Mod("tas de bois des remorques", LogPiles.Update),
            new Mod("sacs de couchage", SleepingBags.Update),
            new Mod("ajouts (hote)", Ajouts.Update),
            new Mod("boutique", Kauppa.Update),
            new Mod("mods (Cheatbox++)", ModCheatbox.Update),
            new Mod("fendeuse (attelage)", CutterHitch.Update),
            new Mod("fuites et vapeur", CarLeaks.Update),
            new Mod("porte du garage", GarageDoor.Update),
            new Mod("vue (troisieme personne)", ThirdPerson.Update),
            new Mod("camera drone", DroneCam.Update),
            new Mod("ray tracing", RayTracing.Update),
            new Mod("boissons", Drinks.Update),
            new Mod("rayons", Stock.Update),
            new Mod("PNJ", Npcs.Update),
            new Mod("voitures garees", Parked.Update),
            new Mod("monde (automates)", WorldFsms.Update),
            new Mod("trafic", Traffic.Update),
            new Mod("courses", Races.Update),
            new Mod("portes poussees", PushDoors.Update),
            new Mod("molettes", Knobs.Update),
            new Mod("evenements", Events.Update),
            new Mod("defrichage", Clearing.Update),
            new Mod("sauvegarde du jeu", SaveHook.Update),
            new Mod("luge de Teimo", TeimoSled.Update),
            new Mod("graphismes", Gfx.Update),
            new Mod("rallye", Rally.Update),
            new Mod("couleur", CarColor.Update),
            new Mod("peinture", CustomPaint.Update),
            new Mod("carburant", FuelPrices.Update),
            new Mod("telephone", PhoneKeys.Update),
            new Mod("chocs", CarHits.Update),
            new Mod("vitres", WindowCranks.Update),
            new Mod("ram1500", ModRam1500.Update),
            new Mod("jonne", ModJonne.Update),
            new Mod("chariot", ModTowCart.Update),
            new Mod("chandelles", ModJackStands.Update),
            new Mod("tenues", Studio.Update),
            new Mod("quetes", Jobs.Update),
            new Mod("trace", Trace.Update),
            new Mod("deroule", Flow.Update),
            new Mod("ecran d'attente", WaitScreen.Update),
            new Mod("autotest", Autotest.Update),
        };

        // Chaque module, chronometre : un passage de plus de 30 ms (a-coup visible) est note, 1 fois / 10 s par module.
        static readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        static readonly System.Collections.Generic.Dictionary<string, float> slowLogged = new System.Collections.Generic.Dictionary<string, float>();

        // [Test] SautModules=a,b,... : ces modules ne tournent plus apres 60 s de jeu ("*" : tous sauf session, joueurs, deroule, autotest) -- mesures.
        static System.Collections.Generic.HashSet<string> skip;
        static bool skipAll;
        static void Step(string what, System.Action a)
        {
            if (skip == null)
            {
                skip = new System.Collections.Generic.HashSet<string>();
                foreach (string s in Config.Get("Test", "SautModules", "").Split(',')) if (s.Trim().Length > 0) skip.Add(s.Trim());
                skipAll = skip.Contains("*");
            }
            if (skip.Count > 0 && PlayerSync.InGame && Time.timeSinceLevelLoad > 60f && (skip.Contains(what) || skipAll && what != "session" && what != "joueurs" && what != "deroule" && what != "autotest")) return;
            long mem = Perf.MemNow();
            watch.Reset(); watch.Start();
            try { MscRuntime.Run(a); } catch (System.Exception e) { Log.Error(what + " : " + e); }
            watch.Stop();
            Perf.Add(what, watch.Elapsed.TotalMilliseconds);
            Perf.AddAlloc(what, mem);
            if (watch.ElapsedMilliseconds > 30)
            {
                float last;
                if (!slowLogged.TryGetValue(what, out last) || Time.realtimeSinceStartup - last > 10f)
                {
                    slowLogged[what] = Time.realtimeSinceStartup;
                    Log.Warn("lent : " + what + " " + watch.ElapsedMilliseconds + " ms");
                }
            }
        }

        void OnLevelWasLoaded(int level)
        {
            MscRuntime.SceneChanged();
            Log.Info("niveau charge : " + level + " " + Application.loadedLevelName);
            MscRuntime.Run(Replay.OnLevelLoaded);
            MscRuntime.Run(PlayerSync.OnLevelLoaded);
            MscRuntime.Run(World.OnLevelLoaded);
            MscRuntime.Run(Radio.OnLevelLoaded);
            MscRuntime.Run(Particles.OnLevelLoaded);
            MscRuntime.Run(Interactions.OnLevelLoaded);
            MscRuntime.Run(VehicleSync.OnLevelLoaded);
            MscRuntime.Run(Parts.OnLevelLoaded);
            MscRuntime.Run(Props.OnLevelLoaded);
            MscRuntime.Run(Wallet.OnLevelLoaded);
            MscRuntime.Run(Shop.OnLevelLoaded);
            MscRuntime.Run(Paint.OnLevelLoaded);
            MscRuntime.Run(Settings.OnLevelLoaded);
            MscRuntime.Run(Fluids.OnLevelLoaded);
            MscRuntime.Run(Consume.OnLevelLoaded);
            MscRuntime.Run(Voices.OnLevelLoaded);
            MscRuntime.Run(CarDoors.OnLevelLoaded);
            MscRuntime.Run(CarVisuals.OnLevelLoaded);
            MscRuntime.Run(Seats.OnLevelLoaded);
            MscRuntime.Run(Voice.OnLevelLoaded);
            MscRuntime.Run(CustomFace.OnLevelLoaded);
            MscRuntime.Run(MapRender.OnLevelLoaded);
            MscRuntime.Run(ModSecondMachtwagen.OnLevelLoaded);
            MscRuntime.Run(ModMachtwagen300D.OnLevelLoaded);
            MscRuntime.Run(Respawn.OnLevelLoaded);
            MscRuntime.Run(Audit.OnLevelLoaded);
            MscRuntime.Run(NearDoors.OnLevelLoaded);
            MscRuntime.Run(Machines.OnLevelLoaded);
            MscRuntime.Run(Frost.OnLevelLoaded);
            MscRuntime.Run(Tow.OnLevelLoaded);
            MscRuntime.Run(Calls.OnLevelLoaded);
            MscRuntime.Run(Wear.OnLevelLoaded);
            MscRuntime.Run(Cooking.OnLevelLoaded);
            MscRuntime.Run(Fires.OnLevelLoaded);
            MscRuntime.Run(Garage.OnLevelLoaded);
            MscRuntime.Run(Home.OnLevelLoaded);
            MscRuntime.Run(Gestures.OnLevelLoaded);
            MscRuntime.Run(HandTools.OnLevelLoaded);
            MscRuntime.Run(LogPiles.OnLevelLoaded);
            MscRuntime.Run(SleepingBags.OnLevelLoaded);
            MscRuntime.Run(WorldFiles.OnLevelLoaded);
            MscRuntime.Run(Ajouts.OnLevelLoaded);
            MscRuntime.Run(Kauppa.OnLevelLoaded);
            MscRuntime.Run(ModCheatbox.OnLevelLoaded);
            MscRuntime.Run(ModJonne.OnLevelLoaded);
            MscRuntime.Run(ModTowCart.OnLevelLoaded);
            MscRuntime.Run(ModJackStands.OnLevelLoaded);
            MscRuntime.Run(CutterHitch.OnLevelLoaded);
            MscRuntime.Run(AssembleIcon.OnLevelLoaded);
            MscRuntime.Run(CarLeaks.OnLevelLoaded);
            MscRuntime.Run(GarageDoor.OnLevelLoaded);
            MscRuntime.Run(ThirdPerson.OnLevelLoaded);
            MscRuntime.Run(DroneCam.OnLevelLoaded);
            MscRuntime.Run(RayTracing.OnLevelLoaded);
            MscRuntime.Run(Drinks.OnLevelLoaded);
            MscRuntime.Run(Stock.OnLevelLoaded);
            MscRuntime.Run(Npcs.OnLevelLoaded);
            MscRuntime.Run(Parked.OnLevelLoaded);
            MscRuntime.Run(WorldFsms.OnLevelLoaded);
            MscRuntime.Run(Traffic.OnLevelLoaded);
            MscRuntime.Run(Races.OnLevelLoaded);
            MscRuntime.Run(PushDoors.OnLevelLoaded);
            MscRuntime.Run(Knobs.OnLevelLoaded);
            MscRuntime.Run(Events.OnLevelLoaded);
            MscRuntime.Run(Clearing.OnLevelLoaded);
            MscRuntime.Run(SaveHook.OnLevelLoaded);
            MscRuntime.Run(TeimoSled.OnLevelLoaded);
            MscRuntime.Run(Gfx.OnLevelLoaded);
            MscRuntime.Run(Lights.OnLevelLoaded);
            MscRuntime.Run(Rally.OnLevelLoaded);
            MscRuntime.Run(CarColor.OnLevelLoaded);
            MscRuntime.Run(CustomPaint.OnLevelLoaded);
            MscRuntime.Run(FuelPrices.OnLevelLoaded);
            MscRuntime.Run(PhoneKeys.OnLevelLoaded);
            MscRuntime.Run(CarHits.OnLevelLoaded);
            MscRuntime.Run(WindowCranks.OnLevelLoaded);
            MscRuntime.Run(ModRam1500.OnLevelLoaded);
            MscRuntime.Run(Studio.OnLevelLoaded);
            MscRuntime.Run(Jobs.OnLevelLoaded);
            MscRuntime.Run(WaitScreen.OnLevelLoaded);
            if (dumpEnabled) dumpAt = Time.realtimeSinceStartup + (Application.loadedLevelName == "GAME" ? Config.GetInt("Test", "VidageDelai", 25) : 5f);
        }

        void OnGUI()
        {
            Perf.Begin();
            try
            {
                if (DroneCam.Active) DroneCam.Draw();   // (drone : viseur seul, rien d'autre du mod sur la photo)
                else if (Kauppa.Viewfinder) Kauppa.DrawViewfinder();   // (camescope : son viseur seul)
                else { Hud.Draw(); Kauppa.DrawKameraHint(); }
                WaitScreen.Draw(); Menu.Draw(); WalletPanel.Draw(); CheatPanel.Draw(); MapPanel.Draw(); Respawn.Draw(); Kauppa.DrawMontage();
                if (Menu.CursorWanted) Style.DrawCursor(Menu.CursorPos());
            }
            catch (System.Exception e) { Style.Alpha = 1f; Log.Error("hud : " + e); }
            Perf.End("(interface)");
        }

        void OnApplicationQuit()
        {
            Session.Stop();
            // Le lanceur cherche cette ligne : absente a la fin du journal, le jeu s'est arrete brutalement (crash,
            // processus tue) et l'icone des journaux porte un "!" rouge.
            if (!closed) { closed = true; Log.Info("jeu ferme normalement"); }
        }

        // (aussi a la destruction de ce module, qui ne survient qu'a la fermeture du jeu : au cas ou Unity sauterait
        // OnApplicationQuit selon la facon de quitter -- pas de fausse alerte d'arret brutal dans le lanceur)
        static bool closed;
        void OnDestroy()
        {
            if (!closed) { closed = true; Log.Info("jeu ferme normalement"); }
        }
    }
}
