using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using MWCoop.Net;
using SummerCoop.Core;
using UnityEngine;
using Session = MWCoop.Net.Session;

namespace MWCoop
{
    // Bounded, ordered pulls over the authenticated game transport. All file
    // reads, writes and hashing run in the IO worker. Guests retain their files.
    public static class MscMusic
    {
        const int Begin = 0, Entry = 1, End = 2, Need = 3, Data = 4, Error = 5;
        const int Block = 32000, Chunk = 1000;
        sealed class Sending { public Peer Peer; public int Entry = -1, File, Offset; public byte[] Bytes; }
        static readonly List<Sending> sending = new List<Sending>();
        static readonly HashSet<Peer> reading = new HashSet<Peer>();
        static readonly Queue<Action> completed = new Queue<Action>();
        static readonly Dictionary<AudioClip, string> clipKeys = new Dictionary<AudioClip, string>();
        static readonly Dictionary<string, AudioClip> keyClips = new Dictionary<string, AudioClip>();
        static MusicManifest manifest;
        static MusicFile[] incoming;
        static bool scanning, ready, preparing;
        static bool[] present;
        static string source, cache, folder, incomingId, failure;
        static int generation, incomingCount, current, offset;
        static long total, received, downloaded;
        static MemoryStream block;
        static float nextNotice;
        public static bool Ready { get { return !Session.Active || ready; } }
        public static string Failure { get { return failure; } }
        public static string Folder { get { return ready ? folder : ""; } }
        public static string CatalogueId { get { return manifest == null ? "" : manifest.Id; } }
        public static float Progress { get { return total == 0 ? ready ? 1 : 0 : Mathf.Clamp01((float)received / total); } }
        public static void Reset()
        {
            Contenu.MusicReset();
            generation++; manifest = null; incoming = null; scanning = ready = preparing = false;
            sending.Clear(); reading.Clear(); clipKeys.Clear(); keyClips.Clear();
            source = cache = folder = incomingId = failure = null; present = null;
            if (block != null) block.Dispose(); block = null;
            total = received = downloaded = incomingCount = current = offset = 0;
            lock (completed) completed.Clear();
        }
        static void Post(int epoch, Action done)
        { lock (completed) completed.Enqueue(delegate { if (epoch == generation) done(); }); }
        static void Work(Action run)
        {
            int epoch = generation;
            if (!MscIo.TryQueue(delegate { try { run(); } catch (Exception e) { Post(epoch, delegate { Fail(e.Message); }); } })) Fail("Music IO queue is busy");
        }
        static void Fail(string message)
        { if (failure != null) return; failure = message; Log.Error("MSC music: " + message); Session.Status = Lang.R("Music synchronization failed: ") + message; }
        static void Paths()
        {
            if (source != null) return;
            source = Path.GetDirectoryName(Application.dataPath);
            source = Path.GetFullPath(source); cache = Path.Combine(MscRuntime.Root, "music");
        }
        public static void Queue(Peer peer)
        {
            Paths();
            foreach (Sending item in sending) if (item.Peer == peer && item.Bytes == null) return;
            sending.Add(new Sending { Peer = peer });
            BeginHost();
        }
        public static void BeginHost()
        {
            Paths();
            if (manifest != null || scanning) return;
            scanning = true; int epoch = generation; string directory = source;
            string root = cache;
            Work(delegate {
                MusicManifest result = MusicManifest.Scan(directory);
                string snapshot = Path.Combine(root, "catalogues/" + result.Id);
                foreach (MusicFile f in result.Files)
                {
                    string target = Path.Combine(snapshot, f.Path); Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (!MusicManifest.Matches(target, f)) File.Copy(Path.Combine(directory, f.Path), target, true);
                    if (!MusicManifest.Matches(target, f)) throw new IOException("Music changed while preparing: " + f.Path);
                }
                Post(epoch, delegate { manifest = result; folder = snapshot; scanning = false; ready = true; Log.Info("MSC music catalogue: " + result.Files.Length + " files, " + result.Id); });
            });
        }
        public static void Update()
        {
            lock (completed) while (completed.Count > 0) completed.Dequeue()();
            if (!Session.Active) return;
            if (Session.IsHost)
            {
                for (int i = sending.Count - 1; i >= 0; i--)
                {
                    Sending s = sending[i];
                    if (Session.T == null || !Session.T.Peers.Contains(s.Peer)) { sending.RemoveAt(i); continue; }
                    if (s.Peer.InFlight >= 96) continue;
                    if (failure != null) { Session.T.SendReliable(s.Peer, new NetWriter(Msg.Music).U8(Error).Str(failure).ToArray()); sending.RemoveAt(i); continue; }
                    if (manifest == null) continue;
                    if (s.Bytes != null)
                    {
                        for (int k = 0; k < 8 && s.Offset < s.Bytes.Length; k++)
                        {
                            int count = Math.Min(Chunk, s.Bytes.Length - s.Offset);
                            Session.T.SendReliable(s.Peer, new NetWriter(Msg.Music).U8(Data).U16(s.File).I32(s.Entry + s.Offset).Bytes(s.Bytes, s.Offset, count).ToArray()); s.Offset += count;
                        }
                        if (s.Offset >= s.Bytes.Length) sending.RemoveAt(i);
                    }
                    else
                    {
                        if (s.Entry < 0) { Session.T.SendReliable(s.Peer, new NetWriter(Msg.Music).U8(Begin).Str(manifest.Id).U16(manifest.Files.Length).ToArray()); s.Entry = 0; }
                        for (int k = 0; k < 4 && s.Entry < manifest.Files.Length; k++, s.Entry++)
                        { MusicFile f = manifest.Files[s.Entry]; Session.T.SendReliable(s.Peer, new NetWriter(Msg.Music).U8(Entry).U16(s.Entry).Str(f.Path).I32(f.Size).Str(f.Hash).ToArray()); }
                        if (s.Entry >= manifest.Files.Length) { Session.T.SendReliable(s.Peer, new NetWriter(Msg.Music).U8(End).ToArray()); sending.RemoveAt(i); }
                    }
                }
            }
            else if (!ready && failure == null && Time.realtimeSinceStartup >= nextNotice)
            { nextNotice = Time.realtimeSinceStartup + 5; Session.Status = Lang.R("Receiving host music: ") + Mathf.RoundToInt(Progress * 100) + "%"; }
        }
        public static void OnMessage(Peer peer, NetReader r)
        {
            int kind = r.U8();
            if (Session.IsHost)
            {
                if (kind != Need || manifest == null || !Session.AuthenticatedForSave(peer)) return;
                int index = r.U16(), start = r.I32();
                if (index >= manifest.Files.Length || start < 0 || start >= manifest.Files[index].Size || r.More) throw new IOException("Invalid music request");
                foreach (Sending item in sending) if (item.Peer == peer) return;
                if (!reading.Add(peer)) return;
                int epoch = generation; MusicFile f = manifest.Files[index]; string file = Path.Combine(folder, f.Path);
                Work(delegate {
                    try
                    {
                        byte[] bytes = new byte[Math.Min(Block, f.Size - start)];
                        using (var stream = File.OpenRead(file))
                        {
                            if (stream.Length != f.Size) throw new IOException("Music changed during transfer: " + f.Path);
                            stream.Position = start; int got = 0;
                            while (got < bytes.Length) { int n = stream.Read(bytes, got, bytes.Length - got); if (n <= 0) throw new EndOfStreamException(); got += n; }
                        }
                        Post(epoch, delegate { reading.Remove(peer); sending.Add(new Sending { Peer = peer, File = index, Entry = start, Bytes = bytes }); });
                    }
                    catch (Exception e) { string message = e.Message; Post(epoch, delegate { reading.Remove(peer); if (Session.T != null && peer.Accepted) Session.T.SendReliable(peer, new NetWriter(Msg.Music).U8(Error).Str(message).ToArray()); }); }
                });
                return;
            }
            if (peer == null || peer.Id != 0 || failure != null) return;
            Paths();
            switch (kind)
            {
                case Begin:
                    if (preparing || block != null) throw new IOException("Overlapping music catalogue");
                    incomingId = r.Str(); int count = r.U16();
                    if (!MusicManifest.ValidHash(incomingId) || count > MusicManifest.MaxFiles) throw new IOException("Invalid music catalogue");
                    incoming = new MusicFile[count]; incomingCount = 0; ready = false; total = received = downloaded = 0; break;
                case Entry:
                    int at = r.U16();
                    if (incoming == null || at != incomingCount || at >= incoming.Length) throw new IOException("Unordered music catalogue");
                    var entry = new MusicFile { Path = r.Str(), Size = r.I32(), Hash = r.Str() };
                    new MusicManifest(new[] { entry }); incoming[at] = entry; incomingCount++;
                    total += entry.Size; break;
                case End:
                    if (incoming == null || incomingCount != incoming.Length) throw new IOException("Incomplete music catalogue");
                    manifest = new MusicManifest(incoming);
                    if (manifest.Id != incomingId) throw new IOException("Music catalogue hash differs");
                    Prepare(); break;
                case Data:
                    int index = r.U16(), start = r.I32(); byte[] bytes = r.Bytes();
                    if (manifest == null || block == null || index != current || start != offset + block.Length || bytes.Length == 0 || bytes.Length > Chunk || block.Length + bytes.Length > Math.Min(Block, manifest.Files[current].Size - offset)) throw new IOException("Invalid music block");
                    block.Write(bytes, 0, bytes.Length); received += bytes.Length; downloaded += bytes.Length;
                    if (block.Length == Math.Min(Block, manifest.Files[current].Size - offset)) Store(); break;
                case Error: Fail(r.Str()); break;
                default: throw new IOException("Unknown music operation");
            }
            if (r.More) throw new IOException("Trailing music packet data");
        }
        static void Prepare()
        {
            preparing = true; int epoch = generation; MusicManifest catalogue = manifest;
            folder = Path.Combine(cache, "catalogues/" + catalogue.Id);
            string destination = folder, blobs = Path.Combine(cache, "blobs"), localSource = source;
            Work(delegate {
                var found = new bool[catalogue.Files.Length]; long cachedBytes = 0;
                foreach (string group in MusicManifest.Groups) Directory.CreateDirectory(Path.Combine(destination, group)); Directory.CreateDirectory(blobs);
                for (int i = 0; i < catalogue.Files.Length; i++)
                {
                    MusicFile f = catalogue.Files[i]; string target = Path.Combine(destination, f.Path), blob = Path.Combine(blobs, f.Hash);
                    if (!MusicManifest.Matches(target, f))
                    {
                        if (!MusicManifest.Matches(blob, f)) { string local = Path.Combine(localSource, f.Path); if (MusicManifest.Matches(local, f)) File.Copy(local, blob, true); }
                        if (MusicManifest.Matches(blob, f)) File.Copy(blob, target, true);
                    }
                    if (found[i] = MusicManifest.Matches(target, f)) cachedBytes += f.Size;
                }
                Post(epoch, delegate { present = found; received = cachedBytes; preparing = false; current = offset = 0; Next(); });
            });
        }
        static void Next()
        {
            while (current < present.Length && present[current]) current++;
            if (current == present.Length)
            { ready = true; clipKeys.Clear(); keyClips.Clear(); Contenu.MusicReceived(folder); Log.Info("MSC music ready: " + manifest.Id + ";files=" + manifest.Files.Length + ";downloaded=" + downloaded); return; }
            block = new MemoryStream(Block); Session.SendToHost(new NetWriter(Msg.Music).U8(Need).U16(current).I32(offset), true);
        }
        static void Store()
        {
            byte[] bytes = block.ToArray(); block.Dispose(); block = null;
            int epoch = generation, index = current, start = offset; MusicFile f = manifest.Files[index];
            string blob = Path.Combine(cache, "blobs/" + f.Hash), part = blob + ".part", target = Path.Combine(folder, f.Path); bool final = start + bytes.Length == f.Size;
            Work(delegate {
                using (var stream = new FileStream(part, start == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write))
                { if (stream.Length != start) throw new IOException("Music file offset differs"); stream.Position = start; stream.Write(bytes, 0, bytes.Length); }
                if (final)
                { if (!MusicManifest.Matches(part, f)) throw new IOException("Music SHA-256 differs: " + f.Path); if (File.Exists(blob)) File.Delete(blob); File.Move(part, blob); File.Copy(blob, target, true); }
                Post(epoch, delegate { offset = start + bytes.Length; if (final) { present[index] = true; current++; offset = 0; } Next(); });
            });
        }
        public static ArrayList Songs(string group)
        {
            Transform root = null;
            foreach (UnityEngine.Object value in Game.AllFsms())
            {
                PlayMakerFSM f = value as PlayMakerFSM;
                if (f != null && f.FsmName == "LoadSongs" && f.name == (group == "Radio" ? "Folk" : group)) { root = f.transform; break; }
            }
            // CD lists live on the persistent parent, including while its
            // LoadSongs child is inactive after the menu import.
            for (Transform t = root; t != null; t = t.parent)
                foreach (Component component in t.GetComponents<Component>())
                {
                    if (component == null || component.GetType().Name != "PlayMakerArrayListProxy") continue;
                    var name = component.GetType().GetField("referenceName");
                    if (name != null && (string)name.GetValue(component) != (group == "Radio" ? "Songs" : group)) continue;
                    var field = component.GetType().GetField("_arrayList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null) return field.GetValue(component) as ArrayList;
                }
            return null;
        }
        static void IndexClips()
        {
            if (manifest == null) return;
            foreach (string group in MusicManifest.Groups)
            {
                ArrayList songs = Songs(group); if (songs == null) continue; int at = 0;
                foreach (MusicFile f in manifest.Files) if (f.Path.StartsWith(group + "/", StringComparison.Ordinal))
                { if (at >= songs.Count) break; AudioClip clip = songs[at++] as AudioClip; if (clip != null) { clipKeys[clip] = f.Key; keyClips[f.Key] = clip; } }
            }
        }
        public static string ClipKey(AudioClip clip)
        { if (clip == null) return ""; string key; if (!clipKeys.TryGetValue(clip, out key)) { IndexClips(); clipKeys.TryGetValue(clip, out key); } return key ?? clip.name; }
        public static AudioClip Clip(string key)
        { AudioClip clip; if (!keyClips.TryGetValue(key, out clip) || clip == null) { IndexClips(); keyClips.TryGetValue(key, out clip); } return clip; }
        public static string FileFor(string key)
        { if (manifest != null && folder != null) foreach (MusicFile f in manifest.Files) if (f.Key == key) return Path.Combine(folder, f.Path); return null; }
        public static string ValidateImport()
        {
            if (manifest == null) return null;
            foreach (string group in MusicManifest.Groups)
            {
                int expected = 0; foreach (MusicFile f in manifest.Files) if (f.Path.StartsWith(group + "/", StringComparison.Ordinal)) expected++;
                ArrayList songs = Songs(group); int actual = songs == null ? 0 : songs.Count;
                if (actual != expected) return group + ": imported " + actual + " of " + expected + " tracks";
                if (songs != null) foreach (object value in songs)
                { AudioClip clip = value as AudioClip; if (clip == null || clip.loadState == AudioDataLoadState.Failed) return group + ": invalid OGG track"; }
            }
            return null;
        }
        public static void ImportFailed(string message) { Fail(message); }
        public static void BindClip(AudioClip clip, string key) { if (clip != null) clipKeys[clip] = key; }
        public static void UnbindClip(AudioClip clip) { if (!ReferenceEquals(clip, null)) clipKeys.Remove(clip); }
    }
}
