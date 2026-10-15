using System.Collections.Generic;

namespace MWCoop
{
    // Reliable UDP can deliver retransmissions out of order. Each actor's edit
    // sequence is checked by the host; observers follow the host's sequence.
    public sealed class MscEditClock
    {
        readonly Dictionary<int, int> actors = new Dictionary<int, int>();
        int local;
        public int HostRevision { get; private set; }
        public int Publish(bool host) { return host ? ++HostRevision : ++local; }
        public void ForgetActor(int actor) { actors.Remove(actor); }
        public bool AcceptRequest(int actor, int revision)
        {
            int previous; actors.TryGetValue(actor, out previous);
            if (revision <= previous) return false;
            actors[actor] = revision; return true;
        }
        public bool AcceptSnapshot(int revision)
        {
            if (revision <= HostRevision) return false;
            HostRevision = revision; return true;
        }
    }
}
