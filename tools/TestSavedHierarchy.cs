using System;
using System.Collections.Generic;
using System.IO;
using SummerCoop.Core;

internal static class TestSavedHierarchy
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool RefersTo(Atom value, HashSet<string> removed)
    {
        if (value.Kind != AtomKind.Reference) return false;
        int component = value.Text.IndexOf('#');
        return removed.Contains(component < 0 ? value.Text : value.Text.Substring(0, component));
    }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: TestSavedHierarchy copied-source empty-output-directory");
            Check(!Directory.Exists(args[1]), "Output directory must be fresh.");
            World before;
            using (var store = new CheckpointStore(args[0])) before = store.Recover();
            Check(before != null, "No recoverable checkpoint.");
            string originalHash = Codec.Hash(Codec.World(before));
            World after = before.ReferenceSnapshot();
            int count = LocalHierarchy.RemoveFromCheckpoint(after), exact = 0, omitted = 0, detached = 0;
            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in before.Entities.Keys) if (!after.Entities.ContainsKey(id)) removed.Add(id);
            Check(count == removed.Count, "Removal count differs from the actual entity set.");
            foreach (Entity entity in after.Entities.Values)
            {
                Entity old = before.Entities[entity.Id];
                if (CheckpointStore.Equal(entity, old)) { exact++; continue; }
                Entity expected = old.Clone();
                if (removed.Contains(expected.Parent)) { expected.Parent = ""; detached++; }
                var properties = new List<string>(expected.Properties.Keys);
                foreach (string property in properties)
                    if (property.StartsWith("v/PlayerKey", StringComparison.Ordinal) || RefersTo(expected.Properties[property], removed))
                    { expected.Properties.Remove(property); omitted++; }
                Check(CheckpointStore.Equal(expected, entity), "Shared gameplay data changed: " + entity.Key);
            }
            foreach (Entity entity in after.Entities.Values)
            {
                Check(entity.Parent == "" || after.Entities.ContainsKey(entity.Parent), "Unresolved saved parent: " + entity.Key);
                foreach (Atom value in entity.Properties.Values) Check(!RefersTo(value, removed), "Reference to removed local entity: " + entity.Key);
            }
            Check(after.Id == before.Id && after.Money == before.Money && after.Revision == before.Revision
                && after.SleepEpoch == before.SleepEpoch && after.GameHash == before.GameHash && after.SchemaHash == before.SchemaHash
                && after.Players.Count == before.Players.Count, "World metadata changed.");
            Check(originalHash == Codec.Hash(Codec.World(before)), "Recovery mutated its input snapshot.");
            string repairedHash = Codec.Hash(Codec.World(after));
            Check(LocalHierarchy.RemoveFromCheckpoint(after) == 0 && Codec.Hash(Codec.World(after)) == repairedHash, "Repair is not idempotent.");
            using (var store = new CheckpointStore(args[1])) store.Checkpoint(after);
            using (var store = new CheckpointStore(args[1]))
                Check(Codec.Hash(Codec.World(store.Recover())) == repairedHash, "Restart changed the repaired checkpoint.");
            Console.WriteLine("PASS shared entity state, parent/reference integrity, immutable input, idempotence and disk restart");
            Console.WriteLine("REMOVED=" + count + " EXACT_UNCHANGED=" + exact + " OMITTED_LOCAL_PROPERTIES=" + omitted + " DETACHED_HELD=" + detached);
            Console.WriteLine("REVISION=" + after.Revision + " MONEY=" + after.Money + " RETAINED_ENTITIES=" + after.Entities.Count);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
