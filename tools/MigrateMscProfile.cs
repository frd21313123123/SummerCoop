using System;
using System.IO;

// The two engines have different checkpoints. Do not rewrite an old checkpoint
// with a new hash: MSC native files are copied explicitly on first host launch.
class MigrateMscProfile
{
    static int Main(string[] args)
    {
        if (args.Length != 6 || args[5] != "--same-schema-v2") { Console.Error.WriteLine("Invalid profile update arguments"); return 1; }
        if (!Directory.Exists(args[1])) { Console.Error.WriteLine("Profile directory is missing"); return 1; }
        Console.WriteLine("Legacy SummerCoop checkpoint preserved. MWCoop MSC uses separate mwcoop-profiles/host and guest directories.");
        Console.WriteLine("Enable saved-world import in the launcher to copy native MSC files into the new host profile.");
        return 0;
    }
}
