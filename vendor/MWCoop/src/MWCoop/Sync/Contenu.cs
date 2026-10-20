using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Contenu personnalisable recu de l'hote (CD1-3, Radio, Images, Extra : modsync.inc du lanceur), lu par le jeu de
    // l'invite. Demande de JD (09/10) : beaucoup d'invites recevaient le contenu (telecharge) mais ne voyaient rien en jeu.
    // Le lanceur reliait les dossiers du jeu au contenu recu dans la copie de lancement de l'invite -- seulement si tout
    // etait recu au moment du lancement, et seulement dans cette copie.
    // Ici le jeu de l'invite lit directement le contenu recu : chaque automate qui charge un fichier du jeu y prend le
    // dossier du jeu par GetAppPath (AppFolderResult), puis BuildString "file:///" + dossier + "Images/" + "poster1.png"
    // (ou {FolderName} "CD1/", "Radio/", "Extra/"). Pour ce que l'hote a envoye (lancement.ini : ContenuDossier,
    // ContenuRecu, ContenuImages), GetAppPath est coupe et la variable pointe sur le dossier recu ; puis l'automate est
    // relance : les images (Get, ImportPNG, hockey) se rechargent, les musiques deja importees sont reimportees.
    //
    // Musiques (retour d'un joueur, 10/10 : « les invites telechargent ma musique, mais elle ne s'importe pas ») : le jeu
    // n'importe les musiques qu'au menu principal (bouton IMPORT MUSIC FILES, coupe d'origine : Interface/Songs/Button
    // -> Radio/Folk::LoadSongs, puis CD::Playlist allume CD1-3 et leurs LoadSongs), et seulement des fichiers
    // CDn/track1.ogg, track2.ogg... (OGG Vorbis). Avant : le mod ne pointait les lecteurs qu'en jeu -- la radio etait
    // reimportee, mais pas les CD (deja importes, non reconnus) ; et l'invite, entre en partie tout seul 2 s apres
    // l'hote, n'avait jamais le temps de cliquer le bouton. Maintenant, au menu : lecteurs pointes sur le contenu recu,
    // et quand le mod entre lui-meme en partie (Flow.AutoSkip) avec des musiques a lire (recues de l'hote, ou dans ses
    // propres dossiers), l'import est lance comme par le bouton ; Flow attend sa fin (30 s au plus).
    public static class Contenu
    {
        static float applyAt = -1, reapplyAt = -1;
        static readonly HashSet<PlayMakerFSM> done = new HashSet<PlayMakerFSM>();
        static bool dumped;
        static string musicDirectory;

        public static void OnLevelLoaded()
        {
            done.Clear();
            cds = null; roots = null;
            bool on = PlayerSync.InGame && Folder().Length > 0;
            applyAt = on ? Time.realtimeSinceStartup + 1f : -1;
            reapplyAt = on ? Time.realtimeSinceStartup + 12f : -1;
        }

        static string Folder()
        {
            string d = Config.Get("Coop", "ContenuDossier", "");
            if (d.Length == 0 || !Directory.Exists(d)) return "";
            return d.TrimEnd('\\', '/') + "/";
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (applyAt > 0 && now >= applyAt) { applyAt = -1; Apply("chargement"); }
            if (reapplyAt > 0 && now >= reapplyAt) { reapplyAt = -1; Apply("relecture"); Report(); Log.Info("musiques en jeu : " + Songs()); }   // (automates crees ou allumes plus tard)
            WatchCds();
        }

        // Lecteur possible : variable de dossier (Folder, rempli par GetAppPath) ou chargement des musiques. (Lire les actions
        // de TOUS les automates force PlayMaker a les charger une a une : des dizaines de secondes, jeu fige.)
        static bool Reader(PlayMakerFSM f)
        {
            if (f == null || f.Fsm == null || f.Fsm.States == null) return false;
            return f.FsmName == "LoadSongs" || f.FsmVariables.FindFsmString("Folder") != null || f.FsmVariables.FindFsmString("Path") != null;
        }

        static HashSet<string> roots, images;
        static GameObject[] cds;   // CD/CD1-3 : leurs LoadSongs ne s'initialisent qu'a l'import (objets eteints jusque-la)

        static void Apply(string why)
        {
            string dir = Folder();
            if (dir.Length == 0 && string.IsNullOrEmpty(musicDirectory) || (Session.IsHost && Config.GetInt("Test", "ContenuHote", 0) == 0)) return;
            roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in Config.Get("Coop", "ContenuRecu", "").Split(',')) if (r.Trim().Length > 0) roots.Add(r.Trim().TrimEnd('/', '\\') + "/");
            images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in Config.Get("Coop", "ContenuImages", "").Split(',')) if (r.Trim().Length > 0) images.Add(r.Trim());
            var cdList = new List<GameObject>();
            int n = 0;
            foreach (UnityEngine.Object o in Game.AllFsms())
            {
                PlayMakerFSM f = o as PlayMakerFSM;
                if (f == null) continue;
                // (CD/CD1-3 : objet persistant, introuvable par son chemin ; reconnus a leur LoadSongs)
                if (f.FsmName == "LoadSongs" && f.gameObject.name.StartsWith("CD") && f.gameObject.name.Length == 3) cdList.Add(f.gameObject);
                if (!done.Contains(f) && Reader(f) && Point(f, dir)) n++;
            }
            cds = cdList.ToArray();
            if (n > 0) Log.Info("contenu de l'hote (" + why + ") : " + n + " lecteur(s) du jeu pointes sur " + dir);
        }

        // CD importes en cours de partie : leur lecteur pointe des qu'il s'allume (avant sa premiere piste : Init attend 0,2 s).
        static void WatchCds()
        {
            if (cds == null || roots == null) return;
            foreach (GameObject go in cds)
            {
                if (go == null || !go.activeInHierarchy) continue;
                PlayMakerFSM f = Game.FsmOn(go, "LoadSongs");
                if (f == null || done.Contains(f)) continue;
                string dir = Folder();
                if (!string.IsNullOrEmpty(musicDirectory)) dir = musicDirectory;
                if (dir.Length > 0 && Point(f, dir)) Log.Info("contenu de l'hote : musiques de " + go.name + " lues dans " + dir);
                else done.Add(f);   // (pas envoye par l'hote : ses propres musiques)
            }
        }

        // Un lecteur du jeu : s'il lit un fichier que l'hote a envoye, GetAppPath coupe, dossier recu, relance.
        static bool Point(PlayMakerFSM f, string dir)
        {
            if (f.Fsm == null || f.Fsm.States == null) return false;
            FsmStateAction getPath = null;
            string root = null, file = null;
            foreach (FsmState s in f.Fsm.States)
            {
                FsmStateAction[] acts = null;
                try { acts = s.Actions; } catch { }
                if (acts == null) continue;
                foreach (FsmStateAction a in acts)
                {
                    if (a == null) continue;
                    string tn = a.GetType().Name;
                    if (tn == "GetAppPath" && getPath == null) getPath = a;
                    if (tn != "BuildString" || root != null) continue;
                    FieldInfo pf = a.GetType().GetField("stringParts");
                    FsmString[] parts = pf != null ? pf.GetValue(a) as FsmString[] : null;
                    if (parts == null) continue;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string v = parts[i] != null ? parts[i].Value : null;
                        if (string.IsNullOrEmpty(v)) continue;
                        v = v.Replace('\\', '/');
                        string vNorm = v.Trim('/', '\\') + "/";
                        foreach (string r in new[] { "CD1/", "CD2/", "CD3/", "Radio/", "Extra/", "Images/" })
                            if (string.Equals(v, r, StringComparison.OrdinalIgnoreCase) || string.Equals(vNorm, r, StringComparison.OrdinalIgnoreCase))
                            {
                                root = r;
                                if (i + 1 < parts.Length && parts[i + 1] != null) file = parts[i + 1].Value;
                            }
                    }
                }
            }
            bool music = f.FsmName == "LoadSongs" && !string.IsNullOrEmpty(musicDirectory);
            if (getPath == null || root == null || !(music || roots.Contains(root))) return false;
            if (music) dir = musicDirectory;
            if (dir.Length == 0) return false;
            if (root == "Images/" && (file == null || !images.Contains(file))) return false;   // (image que l'hote n'envoie pas : la sienne)
            FieldInfo rf = getPath.GetType().GetField("AppFolderResult");
            FsmString var = rf != null ? rf.GetValue(getPath) as FsmString : null;
            if (var == null) return false;
            done.Add(f);
            bool already = !getPath.Enabled && var.Value == dir;   // (pointe au menu : deja lu dans le dossier recu)
            getPath.Enabled = false;
            var.Value = dir;
            if (already) return true;
            // relance : rechargement avec le dossier recu
            bool songs = f.FsmName == "LoadSongs";
            if (songs && !Imported(f)) return true;   // (pas encore importees : le seront depuis le dossier recu)
            if (songs && !f.gameObject.activeSelf) f.gameObject.SetActive(true);   // (CD1-3 : eteints hors import)
            if (!f.enabled) f.enabled = true;   // (PlayMaker repart du debut ; objet eteint : a son allumage)
            else if (f.gameObject.activeInHierarchy && f.Fsm.StartState != null) Game.SetState(f, songs ? "Init" : f.Fsm.StartState);
            return true;
        }

        // Musiques deja importees ? Radio (Folk) : son Path ; CD1-3 : celui de CD::Playlist (leur Path est le dernier fichier).
        static bool Imported(PlayMakerFSM f)
        {
            if (f == null) return false;
            FsmString path = f.FsmVariables.FindFsmString("Path");
            if (f.FsmName == "LoadSongs" && f.gameObject.name.StartsWith("CD")) return Imported(Fsm("CD", "Playlist"));
            if (f.FsmName == "Playlist") return path != null && path.Value == "CD'S IMPORTED";
            return path != null && path.Value != null && path.Value.Contains("IMPORTED") && path.Value != "NOT IMPORTED";
        }

        static PlayMakerFSM Fsm(string obj, string name)
        {
            foreach (UnityEngine.Object o in Game.AllFsms())
            {
                PlayMakerFSM f = o as PlayMakerFSM;
                if (f != null && f.FsmName == name && f.gameObject.name == obj) return f;
            }
            return null;
        }

        // ---- menu principal : lecteurs pointes, import des musiques quand le mod entre lui-meme en partie
        static float menuAt = -1, importUntil;
        static bool menuDone, importFailed, managedImport;
        public static void MusicReset()
        {
            // At the first loader callback native menu FSMs have not completed
            // Awake yet. Only reset an import that this module actually ran.
            if (!managedImport && string.IsNullOrEmpty(musicDirectory)) return;
            managedImport = false;
            musicDirectory = null; done.Clear(); cds = null; roots = null; songFiles = null; songDir = null;
            foreach (string group in SummerCoop.Core.MusicManifest.Groups)
            { var songs = MscMusic.Songs(group); if (songs != null) songs.Clear(); }
            foreach (UnityEngine.Object value in Game.AllFsms())
            {
                PlayMakerFSM f = value as PlayMakerFSM;
                if (f == null || f.FsmName != "LoadSongs" || !(f.name == "Folk" || f.name == "CD1" || f.name == "CD2" || f.name == "CD3")) continue;
                foreach (FsmState state in f.FsmStates)
                    foreach (FsmStateAction action in FireTools.Actions(f, state) ?? new FsmStateAction[0])
                        if (action != null && action.GetType().Name == "GetAppPath") action.Enabled = true;
                FsmString marker = f.FsmVariables.FindFsmString("Path"); if (marker != null) marker.Value = "NOT IMPORTED";
            }
            PlayMakerFSM playlist = Fsm("CD", "Playlist");
            if (playlist != null) playlist.FsmVariables.GetFsmString("Path").Value = "NOT IMPORTED";
            menuDone = importFailed = false; menuAt = -1; importUntil = 0;
        }
        public static void MusicReceived(string directory)
        {
            managedImport = true;
            musicDirectory = directory.TrimEnd('\\', '/') + "/";
            done.Clear(); cds = null; roots = null; songFiles = null; songDir = null;
            foreach (UnityEngine.Object value in Game.AllFsms())
            {
                PlayMakerFSM f = value as PlayMakerFSM;
                if (f == null || !(f.FsmName == "LoadSongs" && (f.name == "Folk" || f.name == "CD1" || f.name == "CD2" || f.name == "CD3") || f.FsmName == "Playlist" && f.name == "CD")) continue;
                FsmString p = f.FsmVariables.FindFsmString("Path"); if (p != null) p.Value = "NOT IMPORTED";
                foreach (Component component in f.GetComponents<Component>())
                {
                    if (component == null || component.GetType().Name != "PlayMakerArrayListProxy") continue;
                    var field = component.GetType().GetField("_arrayList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var list = field == null ? null : field.GetValue(component) as System.Collections.ArrayList;
                    if (list != null) list.Clear();
                }
            }
            menuDone = importFailed = false; menuAt = Time.realtimeSinceStartup; importUntil = 0;
        }
        public static bool MenuBusy { get { return importFailed || (!menuDone && menuAt > 0) || importUntil > 0; } }

        public static void MenuUpdate()
        {
            if (Application.loadedLevelName != "MainMenu") { menuAt = -1; menuDone = false; importUntil = 0; return; }
            float now = Time.realtimeSinceStartup;
            if (menuAt < 0) { menuAt = now + 1f; done.Clear(); cds = null; roots = null; }
            if (!MscMusic.Ready) return;
            if (!menuDone && now >= menuAt)
            {
                menuDone = true;
                Apply("menu");
                // Des musiques a lire : l'import, comme le bouton. (0.70.1 : seulement si le mod entrait lui-meme en partie,
                // Flow.AutoSkip -- faux chez l'hote des la premiere demi-seconde, le choix du salon deja pris : ses musiques
                // jamais importees, et la radio des invites, qui suit la sienne, muette. Retour d'un joueur, 10/10.)
                MenuImport();
            }
            WatchCds();
            PlayMakerFSM playlist = Fsm("CD", "Playlist");
            bool doneSongs = (playlist != null && Imported(playlist)) || (playlist == null && Imported(Fsm("Folk", "LoadSongs")));
            if (importUntil > 0 && (doneSongs || now >= importUntil))
            {
                string error = doneSongs ? MscMusic.ValidateImport() : "Music import did not finish within 30 seconds";
                if (error != null) { importFailed = true; MscMusic.ImportFailed(error); }
                Log.Info("musiques : " + (error ?? "import fini") + " -- " + Songs());
                importUntil = 0;
            }
        }

        // Fichier de la chanson importee de rang i de la radio (liste "Songs" de Radio/Folk : LoadSongs lit track1..200.ogg
        // dans l'ordre et saute les absents) : dossier recu de l'hote s'il a envoye sa radio, sinon celui du jeu. Pour les
        // copies de postes (Radio.cs), qui chargent leur propre exemplaire.
        static string songDir; static List<string> songFiles;
        public static string SongFile(int i)
        {
            string dir = !Session.IsHost && !string.IsNullOrEmpty(musicDirectory) ? musicDirectory + "Radio/" : roots != null && roots.Contains("Radio/") && Folder().Length > 0 ? Folder() + "Radio/" : Path.Combine(Path.GetDirectoryName(Application.dataPath), "Radio").Replace('\\', '/') + "/";
            if (songFiles == null || songDir != dir)
            {
                songDir = dir; songFiles = new List<string>();
                for (int k = 1; k <= 200; k++) { string p = dir + "track" + k + ".ogg"; try { if (File.Exists(p)) songFiles.Add(p); } catch { } }
            }
            return i >= 0 && i < songFiles.Count ? songFiles[i] : null;
        }

        static bool HasMusic(string root)
        {
            if (!Session.IsHost && !string.IsNullOrEmpty(musicDirectory))
            {
                for (int i = 1; i <= 200; i++) if (File.Exists(Path.Combine(musicDirectory, root.Trim('/', '\\') + "/track" + i + ".ogg"))) return true;
                return false;
            }
            if (roots != null)
            {
                string normRoot = root.TrimEnd('/', '\\') + "/";
                foreach (string r in roots)
                {
                    if (string.Equals(r.TrimEnd('/', '\\') + "/", normRoot, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            string recFolder = Folder();
            if (recFolder.Length > 0)
            {
                try
                {
                    string recDir = Path.Combine(recFolder, root.Trim('/', '\\'));
                    if (Directory.Exists(recDir))
                    {
                        if (File.Exists(Path.Combine(recDir, "track1.ogg"))) return true;
                        string[] oggs = Directory.GetFiles(recDir, "*.ogg");
                        if (oggs != null && oggs.Length > 0) return true;
                    }
                }
                catch {}
            }
            try
            {
                string gameDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), root.Trim('/', '\\'));
                if (Directory.Exists(gameDir))
                {
                    if (File.Exists(Path.Combine(gameDir, "track1.ogg"))) return true;
                    string[] oggs = Directory.GetFiles(gameDir, "*.ogg");
                    if (oggs != null && oggs.Length > 0) return true;
                }
            }
            catch {}
            return false;
        }

        static void MenuImport()
        {
            if (!HasMusic("CD1/") && !HasMusic("CD2/") && !HasMusic("CD3/") && !HasMusic("Radio/"))
            {
                Log.Info("musiques : rien a importer (pas de track1.ogg ni de fichiers .ogg dans CD1, CD2, CD3 ni Radio de " + Path.GetDirectoryName(Application.dataPath) + (roots != null && roots.Count > 0 ? ", ni de musiques recues" : "") + ")");
                return;
            }
            PlayMakerFSM folk = Fsm("Folk", "LoadSongs"), button = Fsm("Button", "Button");
            if (folk == null) { Log.Warn("musiques : Radio/Folk::LoadSongs introuvable, pas d'import"); return; }
            FsmString p = folk.FsmVariables.FindFsmString("Path");
            if (p != null && p.Value == "RADIO IMPORTED")
            {
                PlayMakerFSM pl = Fsm("CD", "Playlist");
                if (pl == null || Imported(pl))
                {
                    Log.Info("musiques : import deja termine");
                    return;
                }
            }
            importUntil = Time.realtimeSinceStartup + 30f;
            managedImport = true;
            if (!folk.gameObject.activeSelf) folk.gameObject.SetActive(true);
            FsmBool si = button != null ? button.FsmVariables.FindFsmBool("SongImported") : null;
            if (si != null) si.Value = true;
            if (button != null)
            {
                try { button.SendEvent("CLICK"); } catch {}
                try { button.SendEvent("MOUSE DOWN"); } catch {}
            }
            string startState = !string.IsNullOrEmpty(folk.Fsm.StartState) ? folk.Fsm.StartState : "Init";
            if (!folk.enabled) folk.enabled = true;
            else Game.SetState(folk, startState);
            Log.Info("musiques : import lance au menu (comme IMPORT MUSIC FILES)" + (roots != null && roots.Count > 0 ? ", contenu de l'hote : " + string.Join(",", new List<string>(roots).ToArray()) : ""));
        }

        // Nombre de musiques chargees (listes ArrayList du jeu) : radio, CD1-3.
        static string Songs()
        {
            var sb = new System.Text.StringBuilder();
            PlayMakerFSM folk = Fsm("Folk", "LoadSongs");
            sb.Append("radio ").Append(folk != null ? Count(folk.transform, "Songs") : -1);
            foreach (string cd in new[] { "CD1", "CD2", "CD3" })
            {
                PlayMakerFSM f = Fsm(cd, "LoadSongs");
                sb.Append(", ").Append(cd).Append(' ').Append(f != null ? Count(f.transform, cd) : -1);
            }
            return sb.ToString() + " musique(s)";
        }

        static int Count(Transform t, string reference)
        {
            for (Transform x = t; x != null; x = x.parent)
                foreach (Component c in x.GetComponents<Component>())
                {
                    if (c == null || c.GetType().Name != "PlayMakerArrayListProxy") continue;
                    FieldInfo rn = c.GetType().GetField("referenceName");
                    if (rn != null && (rn.GetValue(c) as string) != reference) continue;
                    FieldInfo al = c.GetType().GetField("_arrayList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);   // (ArrayMaker)
                    System.Collections.ArrayList list = al != null ? al.GetValue(c) as System.Collections.ArrayList : null;
                    return list != null ? list.Count : -1;
                }
            return -1;
        }

        // Ce que lisent les lecteurs pointes (journal : adresse, et l'image chargee).
        static void Report()
        {
            foreach (PlayMakerFSM f in done)
            {
                if (f == null) continue;
                FsmString addr = f.FsmVariables.FindFsmString("Address");
                FsmTexture tex = f.FsmVariables.FindFsmTexture("Poster") ?? f.FsmVariables.FindFsmTexture("CarCustomPaint");
                Texture2D t2 = tex != null ? tex.Value as Texture2D : null;
                string px = "";
                if (t2 != null) { try { Color c = t2.GetPixel(t2.width / 2, t2.height / 2); px = ", image " + t2.width + "x" + t2.height + " (centre " + c.r.ToString("F2") + "/" + c.g.ToString("F2") + "/" + c.b.ToString("F2") + ")"; } catch { px = ", image " + t2.width + "x" + t2.height; } }
                Log.Info("contenu de l'hote : " + Recon.Path(f.transform) + "::" + f.FsmName + " [" + f.ActiveStateName + "] " + (addr != null ? addr.Value : "") + px);
            }
        }

        // ---- essais : [Test] Autotest=contenu -- les automates qui lisent un fichier du jeu, et ou ils lisent
        static string Val(object v)
        {
            var nv = v as NamedVariable;
            if (nv != null) return nv.UseVariable && !string.IsNullOrEmpty(nv.Name) ? "{" + nv.Name + "}" : nv.ToString();
            if (v is FsmEvent) return ((FsmEvent)v).Name;
            if (v is bool || v is int || v is float || v is Enum) return v.ToString();
            var arr = v as Array;
            if (arr != null)
            {
                var parts = new List<string>();
                foreach (object o in arr) parts.Add(o == null ? "null" : Val(o) ?? o.GetType().Name);
                return "[" + string.Join(", ", parts.ToArray()) + "]";
            }
            return null;
        }

        public static string Dump(FsmState s)
        {
            var sb = new System.Text.StringBuilder(s.Name + " :");
            FsmStateAction[] acts = null;
            try { acts = s.Actions; } catch { }
            if (acts != null) foreach (FsmStateAction a in acts)
            {
                if (a == null) continue;
                sb.Append(" [").Append(a.GetType().Name);
                foreach (FieldInfo fi in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    string txt = Val(fi.GetValue(a));
                    if (txt != null) sb.Append(' ').Append(fi.Name).Append('=').Append(txt);
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        // [Test] Autotest=contenumenu : au menu principal, les automates de l'import des musiques (bouton, CD, radio).
        static bool menuDumped;
        public static void MenuTest(string mode)
        {
            if (mode != "contenumenu" || menuDumped || Application.loadedLevelName != "MainMenu" || Time.realtimeSinceStartup < 25f) return;
            menuDumped = true;
            int n = 0;
            foreach (UnityEngine.Object o in Game.AllFsms())
            {
                PlayMakerFSM f = o as PlayMakerFSM;
                if (f == null || f.Fsm == null || f.Fsm.States == null) continue;
                n++;
                var sb = new System.Text.StringBuilder();
                foreach (FsmState s in f.Fsm.States) sb.Append(" | ").Append(Dump(s));
                string all = sb.ToString();
                bool hit = f.FsmName == "LoadSongs" || f.FsmName == "Playlist" || all.IndexOf("Playlist", StringComparison.OrdinalIgnoreCase) >= 0
                           || all.IndexOf("IMPORT", StringComparison.OrdinalIgnoreCase) >= 0 || all.IndexOf("Radio", StringComparison.OrdinalIgnoreCase) >= 0
                           || f.FsmVariables.FindFsmBool("Import") != null;
                if (!hit) continue;
                Log.Info("autotest : contenumenu, " + Recon.Path(f.transform) + "::" + f.FsmName + " [" + f.ActiveStateName + "] actif " + f.gameObject.activeInHierarchy + "/" + f.enabled);
                foreach (FsmState s in f.Fsm.States) Log.Info("autotest : contenumenu,    " + Dump(s));
                foreach (FsmTransition tr in f.Fsm.GlobalTransitions) Log.Info("autotest : contenumenu,    global " + tr.EventName + " -> " + tr.ToState);
            }
            Log.Info("autotest : contenumenu, " + n + " automates au menu");
        }

        static int cdStep;
        public static void Test(string mode, float t)
        {
            if (mode == "contenucd" && !Session.IsHost)
            {   // import des CD (comme le jeu : LOAD a CD::Playlist), puis d'ou le lecteur de CD1 a lu ses pistes
                if (t > 30f && cdStep == 0) { cdStep = 1; PlayMakerFSM pl = null; foreach (UnityEngine.Object o in Game.AllFsms()) { PlayMakerFSM f = o as PlayMakerFSM; if (f != null && f.FsmName == "Playlist" && f.gameObject.name == "CD") pl = f; } if (pl != null) { foreach (FsmState st in pl.Fsm.States) Log.Info("autotest : contenucd, Playlist " + Dump(st)); foreach (FsmTransition tr in pl.Fsm.GlobalTransitions) Log.Info("autotest : contenucd, Playlist global " + tr.EventName + " -> " + tr.ToState); Log.Info("autotest : contenucd, Playlist [" + pl.ActiveStateName + "]"); FsmBool imp = pl.FsmVariables.FindFsmBool("Import"); if (imp != null) imp.Value = true; if (!pl.enabled) pl.enabled = true; Game.SetState(pl, "State 3"); Log.Info("autotest : contenucd, import des CD"); } else Log.Info("autotest : contenucd, CD::Playlist introuvable"); }
                if (t > 45f && cdStep == 1)
                {
                    cdStep = 2;
                    foreach (GameObject go in cds ?? new GameObject[0])
                    {
                        string cn = go != null ? go.name : "?";
                        PlayMakerFSM f = go != null ? Game.FsmOn(go, "LoadSongs") : null;
                        FsmString p = f != null ? f.FsmVariables.FindFsmString("Path") : null;
                        Log.Info("autotest : contenucd, " + cn + " " + (f != null && done.Contains(f) ? "POINTE" : "jeu") + " : " + (p != null ? p.Value : "?"));
                        if (f != null && cn == "CD1") foreach (FsmState st in f.Fsm.States) Log.Info("autotest : contenucd,    " + Dump(st));
                    }
                }
                return;
            }
            if (mode != "contenu" || dumped || t < 40f) return;
            dumped = true;
            int n = 0;
            foreach (UnityEngine.Object o in Game.AllFsms())
            {
                PlayMakerFSM f = o as PlayMakerFSM;
                if (f == null || !Reader(f)) continue;
                bool reads = false;
                foreach (FsmState s in f.Fsm.States)
                {
                    FsmStateAction[] acts = null;
                    try { acts = s.Actions; } catch { }
                    if (acts != null) foreach (FsmStateAction a in acts) if (a != null && a.GetType().Name == "GetAppPath") reads = true;
                }
                if (!reads) continue;
                n++;
                FsmString addr = f.FsmVariables.FindFsmString("Address"), path = f.FsmVariables.FindFsmString("Path");
                FsmTexture tex = f.FsmVariables.FindFsmTexture("Poster");
                string px = "";
                Texture2D t2 = tex != null ? tex.Value as Texture2D : null;
                if (t2 != null) { try { Color c = t2.GetPixel(t2.width / 2, t2.height / 2); px = ", image " + t2.width + "x" + t2.height + " centre " + c.r.ToString("F2") + "/" + c.g.ToString("F2") + "/" + c.b.ToString("F2"); } catch { px = ", image " + t2.width + "x" + t2.height; } }
                Log.Info("autotest : contenu, " + Recon.Path(f.transform) + "::" + f.FsmName + " [" + f.ActiveStateName + "] " + (done.Contains(f) ? "POINTE" : "jeu")
                         + " : " + (addr != null ? addr.Value : path != null ? path.Value : "?") + px);
                if (f.FsmName == "LoadSongs") foreach (FsmState s in f.Fsm.States) Log.Info("autotest : contenu,    " + Dump(s));
            }
            Log.Info("autotest : contenu, " + n + " automates lisent un fichier du jeu");
        }
    }
}
