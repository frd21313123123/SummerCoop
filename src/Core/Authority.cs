using System;
using System.Collections.Generic;

namespace SummerCoop.Core
{
    public interface ICommandAdapter
    {
        // Must validate before applying. Returning an error must not mutate Unity.
        string Validate(string player, Command command, World world);
        void Apply(string player, Command command, World world);
        void Rollback(World before);
    }

    public interface ICommitLog { void Commit(World world, string player, Command command); }
    // Implementations must treat existing Entity/Atom snapshots as immutable.
    public interface ICopyOnWriteCommandAdapter : ICommandAdapter { }
    // A validated input lease changes no durable model state.
    public interface ITransientCommandAdapter : ICommandAdapter { void ApplyTransient(string player, Command command, World world); }
    public interface ILiveCommandPosition { bool TryGetPosition(string target, out V3 position); }

    public sealed class Authority
    {
        public World World { get; private set; }
        public bool ImmutableEntities { get { return adapter is ICopyOnWriteCommandAdapter; } }
        private readonly ICommandAdapter adapter;
        private readonly ICommitLog log;
        private readonly Dictionary<string, long> last = new Dictionary<string, long>();
        private readonly Dictionary<string, Dictionary<long, CommandResult>> results = new Dictionary<string, Dictionary<long, CommandResult>>();
        public Authority(World world, ICommandAdapter adapter, ICommitLog log)
        {
            World = world; this.adapter = adapter; this.log = log;
        }
        public void ReplaceWorld(World world) { World = world; }
        public void AddPlayer(string id, string name)
        {
            if (World.Players.ContainsKey(id)) throw new InvalidOperationException("Player already connected");
            if (World.Players.Count >= Protocol.MaxPlayers) throw new InvalidOperationException("Lobby full");
            World.Players.Add(id, new Player { Id = id, Name = name });
            // Sequence numbers belong to the admitted connection, not to a claimed wire ID.
            last[id] = 0; results[id] = new Dictionary<long, CommandResult>();
        }
        public void RemovePlayer(string id)
        {
            Player p;
            if (!World.Players.TryGetValue(id, out p)) return;
            if (p.Held != "") Execute(id, new Command { Sequence = last[id] + 1, Kind = CommandKind.Drop, Target = p.Held });
            if (p.Seat != "") Execute(id, new Command { Sequence = last[id] + 1, Kind = CommandKind.LeaveSeat, Target = p.Seat });
            World.Players.Remove(id); last.Remove(id); results.Remove(id);
        }
        public CommandResult Execute(string id, Command c)
        {
            CommandResult cached;
            Dictionary<long, CommandResult> history;
            if (!results.TryGetValue(id, out history)) return Reject(c, "Unknown player");
            if (history.TryGetValue(c.Sequence, out cached)) return cached.Clone();
            if (c.Sequence <= last[id] || c.Sequence <= 0) return Reject(c, "Stale command");
            last[id] = c.Sequence;
            string error = Validate(id, c);
            if (error != null) return Remember(id, c.Sequence, Reject(c, error));
            ITransientCommandAdapter transient = adapter as ITransientCommandAdapter;
            if (c.Kind == CommandKind.Drive && transient != null)
            {
                try { transient.ApplyTransient(id, c, World); }
                catch (Exception e) { return Remember(id, c.Sequence, Reject(c, "Input failed: " + e.Message)); }
                return Remember(id, c.Sequence, new CommandResult { Sequence = c.Sequence, Accepted = true, Revision = World.Revision });
            }
            World before = ImmutableEntities ? World.ReferenceSnapshot() : World.Clone();
            try
            {
                Apply(id, c);
                World.Revision++;
                if (log != null) log.Commit(World, id, c);
            }
            catch (Exception e)
            {
                World = before;
                if (adapter != null) adapter.Rollback(before);
                return Remember(id, c.Sequence, Reject(c, "Transaction failed: " + e.Message));
            }
            return Remember(id, c.Sequence, new CommandResult { Sequence = c.Sequence, Accepted = true, Revision = World.Revision });
        }
        private CommandResult Remember(string id, long sequence, CommandResult r)
        {
            Dictionary<long, CommandResult> h = results[id];
            h[sequence] = r;
            // Ancient retries are rejected using last[], rather than executed again.
            if (h.Count > 256)
            {
                long oldest = long.MaxValue;
                foreach (long n in h.Keys) if (n < oldest) oldest = n;
                h.Remove(oldest);
            }
            return r.Clone();
        }
        private CommandResult Reject(Command c, string why)
        {
            return new CommandResult { Sequence = c.Sequence, Revision = World.Revision, Reason = why };
        }
        private string Validate(string id, Command c)
        {
            if (!Enum.IsDefined(typeof(CommandKind), c.Kind)) return "Unknown command";
            if (double.IsNaN(c.Number) || double.IsInfinity(c.Number) || !c.Vector.IsFinite) return "Invalid number";
            Player p = World.Players[id];
            if (c.Kind == CommandKind.Sleep) return null;
            if (c.Kind == CommandKind.Spawn)
            {
                string spawnError = PlayerControls.ValidateSpawn(World, p, c);
                return spawnError ?? (adapter == null ? "No adapter" : adapter.Validate(id, c, World));
            }
            Entity e;
            if (!World.Entities.TryGetValue(c.Target, out e)) return "Unknown target";
            V3 position = e.Position; ILiveCommandPosition live = adapter as ILiveCommandPosition;
            if (live != null) { V3 current; if (live.TryGetPosition(c.Target, out current)) position = current; }
            if (c.Kind != CommandKind.Drop && c.Kind != CommandKind.LeaveSeat && c.Kind != CommandKind.Drive && p.Position.Distance(position) > 5) return "Target too far away";
            if (c.Kind == CommandKind.Take)
            {
                if (p.Held != "") return "Hands occupied";
                Atom loose;
                bool carryable = e.Properties.TryGetValue("@loose", out loose) ? loose.Kind == AtomKind.Boolean && loose.Number != 0 : e.Kind == EntityKind.Rigidbody && e.Parent == "";
                if (!carryable) return "Item cannot be carried";
                foreach (Player other in World.Players.Values) if (other.Held == c.Target) return "Item already held";
            }
            if (c.Kind == CommandKind.Drop && p.Held != c.Target) return "Item not owned";
            if (c.Kind == CommandKind.EnterSeat)
            {
                if (p.Seat != "") return "Already seated";
                foreach (Player other in World.Players.Values) if (other.Seat == c.Target) return "Seat occupied";
            }
            if ((c.Kind == CommandKind.LeaveSeat || c.Kind == CommandKind.Drive) && p.Seat != c.Target) return "Seat not owned";
            return adapter == null ? ((c.Kind == CommandKind.Take || c.Kind == CommandKind.Drop || c.Kind == CommandKind.Sleep || c.Kind == CommandKind.EnterSeat || c.Kind == CommandKind.LeaveSeat) ? null : "No adapter") : adapter.Validate(id, c, World);
        }
        private void Apply(string id, Command c)
        {
            Player p = World.Players[id];
            switch (c.Kind)
            {
                case CommandKind.Take: p.Held = c.Target; p.HasHeldPose = false; break;
                case CommandKind.Drop: p.Held = ""; break;
                case CommandKind.EnterSeat: p.Seat = c.Target; break;
                case CommandKind.LeaveSeat: p.Seat = ""; break;
                case CommandKind.Sleep: p.SleepReady = c.Number != 0; break;
            }
            if (adapter != null) adapter.Apply(id, c, World);
            if (p.Held == "") p.HasHeldPose = false;
        }
        public bool SetPose(string id, Pose pose)
        {
            Player p;
            if (!World.Players.TryGetValue(id, out p) || pose.Sequence <= p.LastPose || !pose.Position.IsFinite || !pose.Rotation.IsFinite || !V3.Finite(pose.Pitch)) return false;
            if (pose.Held != "" && (pose.Held != p.Held || !pose.HeldPosition.IsFinite || !pose.HeldRotation.IsFinite || pose.Position.Distance(pose.HeldPosition) > 4)) return false;
            if (Math.Abs(pose.Position.X) > 100000 || Math.Abs(pose.Position.Y) > 100000 || Math.Abs(pose.Position.Z) > 100000) return false;
            p.Position = pose.Position; p.Rotation = pose.Rotation; p.Pitch = pose.Pitch; p.LastPose = pose.Sequence;
            p.HasHeldPose = pose.Held != ""; p.HeldPosition = pose.HeldPosition; p.HeldRotation = pose.HeldRotation;
            return true;
        }
    }
}
