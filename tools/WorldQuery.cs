using System;
using SummerCoop.Core;
class WorldQuery
{
    static void Main(string[] args)
    {
        World world = new CheckpointStore(args[0]).Recover();
        Console.WriteLine("WORLD " + world.Id + " revision " + world.Revision + " entities " + world.Entities.Count);
        int count = 0;
        foreach (Entity e in world.Entities.Values) if (e.Id == args[1] || e.Parent == args[1] || e.Key.IndexOf(args[1], StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (++count > 12) break;
            Console.WriteLine(e.Id + "\t" + e.Parent + "\t" + e.Key + "\t" + e.Prefab + "\tactive=" + e.Active);
            foreach (var p in e.Properties) if (p.Key == "v/Stage" || p.Key == "v/Installed" || p.Key == "v/Tightness" || p.Key == "@state" || p.Key == "v/ObjectCreateName" || p.Key == "v/CreatePrefab" || p.Key == "v/CurrentID") Console.WriteLine(p.Key + "=" + (p.Value.Kind == AtomKind.Text || p.Value.Kind == AtomKind.Reference ? p.Value.Text : p.Value.Number.ToString()));
        }
    }
}
