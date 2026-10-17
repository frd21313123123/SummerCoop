using System.Collections.Generic;

namespace MWCoop
{
    // Several native turns (including different bolts on one part) can await
    // publication together. Only an acknowledgement of the latest sent turn
    // may release their predicted stage/total to host repair snapshots.
    public sealed class MscPendingBoltEdit
    {
        int native, sent;
        public void Begin() { native++; }
        public void Sent(int revision) { if (native > 0) native--; if (revision > sent) sent = revision; }
        public bool PermitSnapshot(bool mine, int actorRevision)
        {
            if (native > 0) return false;
            if (sent == 0) return true;
            if (!mine || actorRevision < sent) return false;
            sent = 0; return true;
        }
    }
    // An old repair snapshot must not cancel a native action before its request
    // is sent, or acknowledge a newer action using an earlier actor sequence.
    public sealed class MscPendingPartEdit
    {
        bool native;
        int sent;
        public void Begin() { native = true; }
        public void Cancel() { native = false; }
        public void Sent(int revision) { native = false; sent = revision; }
        public bool PermitSnapshot(bool mine, int actorRevision)
        {
            if (native) return false;
            if (sent == 0) return true;
            if (!mine || actorRevision < sent) return false;
            sent = 0; return true;
        }
    }
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
