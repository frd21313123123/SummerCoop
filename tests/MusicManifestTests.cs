using System;
using System.IO;
using SummerCoop.Core;

internal static class MusicManifestTests
{
    static void Check(bool value) { if (!value) throw new Exception("Music catalogue assertion failed"); }
    static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Invalid catalogue accepted"); }
    public static void Run(Action<string, Action> test)
    {
        test("music catalogue permits only native track paths, bounded sizes and SHA256", delegate {
            foreach (string path in new[] { "Radio/track1.ogg", "CD1/track200.ogg", "CD3/track3.ogg" }) Check(MusicManifest.ValidPath(path));
            foreach (string path in new[] { "../Radio/track1.ogg", "Radio/../track1.ogg", "Radio\\track1.ogg", "Radio/track0.ogg", "Radio/track201.ogg", "CD4/track1.ogg", "Radio/track01.ogg", "Radio/track1.ogg.exe", "Radio/track.ogg" }) Check(!MusicManifest.ValidPath(path));
            var file = new MusicFile { Path = "Radio/track1.ogg", Size = 12, Hash = new string('a', 64) };
            Reject(delegate { new MusicManifest(new[] { file, file }); });
            foreach (int size in new[] { -1, 0, MusicManifest.MaxFileBytes + 1 }) Reject(delegate { new MusicManifest(new[] { new MusicFile { Path = file.Path, Hash = file.Hash, Size = size } }); });
            Reject(delegate { new MusicManifest(new[] { new MusicFile { Path = file.Path, Hash = "invalid", Size = 1 } }); });
        });
        test("music tracks keep numeric order through gaps and same names with different content never alias", delegate {
            string directory = Path.Combine(Path.GetTempPath(), "SummerCoop-music-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "Radio"));
            try {
                File.WriteAllText(Path.Combine(directory, "Radio/track1.ogg"), "first");
                File.WriteAllText(Path.Combine(directory, "Radio/track3.ogg"), "third");
                File.WriteAllText(Path.Combine(directory, "Radio/track10.ogg"), "tenth");
                File.WriteAllText(Path.Combine(directory, "Radio/ignored.mp3"), "ignored");
                MusicManifest first = MusicManifest.Scan(directory);
                Check(first.Files.Length == 3 && first.Files[1].Path == "Radio/track3.ogg" && first.Files[2].Path == "Radio/track10.ogg");
                string key = first.Files[0].Key, id = first.Id;
                Check(MusicManifest.Matches(Path.Combine(directory, first.Files[0].Path), first.Files[0]));
                File.WriteAllText(Path.Combine(directory, "Radio/track1.ogg"), "other");
                MusicManifest second = MusicManifest.Scan(directory);
                Check(second.Id != id && second.Files[0].Key != key && !MusicManifest.Matches(Path.Combine(directory, first.Files[0].Path), first.Files[0]));
                Check(second.Id == new MusicManifest(second.Files).Id);
                second.Files[0].Hash = new string('b', 64); Check(first.Files[0].Key == key);
            } finally { Directory.Delete(directory, true); }
        });
    }
}
