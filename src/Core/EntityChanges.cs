using System;
using System.Collections.Generic;

namespace SummerCoop.Core
{
    // Coalesced change history for adapters that explicitly report every replacement/deletion.
    // Nodes are reused; a peer gets the latest value of every entity changed since its cursor.
    public sealed class EntityChanges
    {
        private struct Change { public string Id; public long Sequence; }
        private readonly Dictionary<string, LinkedListNode<Change>> entries = new Dictionary<string, LinkedListNode<Change>>(StringComparer.Ordinal);
        private readonly LinkedList<Change> order = new LinkedList<Change>();
        private readonly int limit;
        public long Sequence { get; private set; }
        public long PrunedThrough { get; private set; }
        public EntityChanges(int limit) { if (limit < 1) throw new ArgumentOutOfRangeException("limit"); this.limit = limit; }
        public long Touch(string id)
        {
            LinkedListNode<Change> node;
            Change change = new Change { Id = id, Sequence = ++Sequence };
            if (entries.TryGetValue(id, out node)) { order.Remove(node); node.Value = change; order.AddLast(node); }
            else { node = order.AddLast(change); entries.Add(id, node); }
            if (entries.Count > limit) { LinkedListNode<Change> first = order.First; PrunedThrough = first.Value.Sequence; entries.Remove(first.Value.Id); order.RemoveFirst(); }
            return Sequence;
        }
        public IEnumerable<string> Since(long sequence)
        {
            for (LinkedListNode<Change> node = order.Last; node != null && node.Value.Sequence > sequence; node = node.Previous) yield return node.Value.Id;
        }
    }
}
