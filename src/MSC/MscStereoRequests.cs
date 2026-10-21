using System.Collections.Generic;

namespace MWCoop
{
    // Reliable delivery can reorder turns. Accept each turn once, including
    // an older turn arriving after a newer one, within a bounded window.
    public sealed class MscStereoRequests
    {
        sealed class Actor { public int Highest; public readonly HashSet<int> Seen = new HashSet<int>(); }
        readonly Dictionary<int, Actor> actors = new Dictionary<int, Actor>();
        public void ForgetActor(int actor) { actors.Remove(actor); }
        public bool Accept(int actor, int sequence)
        {
            if (actor < 0 || actor > 255 || sequence <= 0) return false;
            Actor state; if (!actors.TryGetValue(actor, out state)) actors[actor] = state = new Actor();
            if (sequence <= state.Highest - 256 || !state.Seen.Add(sequence)) return false;
            if (sequence > state.Highest) state.Highest = sequence;
            state.Seen.RemoveWhere(value => value <= state.Highest - 256);
            return true;
        }
    }
}
