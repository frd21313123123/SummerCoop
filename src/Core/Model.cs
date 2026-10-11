using System;
using System.Collections.Generic;
using System.Globalization;

namespace SummerCoop.Core
{
    public static class Protocol
    {
        public const int Version = 2;
        public const string ModVersion = "0.1.2-alpha";
        public const int MaxPlayers = 4;
        public const int MaxFrame = 262144;
        public const int MaxSnapshot = 67108864;
        public const int MaxEntities = 60000;
        public const int MaxProperties = 4096;
        public const int MaxString = 16384;
        public const int SnapshotChunk = 49152;
        public const int MaxDatagram = 1100;
        public const int DefaultPort = 27842;
    }

    public struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public bool IsFinite { get { return Finite(X) && Finite(Y) && Finite(Z); } }
        public static bool Finite(float n) { return !float.IsNaN(n) && !float.IsInfinity(n); }
        public float Distance(V3 other)
        {
            float x = X - other.X, y = Y - other.Y, z = Z - other.Z;
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }
    }

    public struct Q4
    {
        public float X, Y, Z, W;
        public Q4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static Q4 Identity { get { return new Q4(0, 0, 0, 1); } }
        public bool IsFinite { get { return V3.Finite(X) && V3.Finite(Y) && V3.Finite(Z) && V3.Finite(W); } }
    }

    public enum AtomKind : byte { Number, Boolean, Text, Reference, Vector, Rotation }
    public sealed class Atom
    {
        public AtomKind Kind;
        public double Number;
        public string Text = "";
        public V3 Vector;
        public Q4 Rotation;
        public static Atom N(double n) { return new Atom { Kind = AtomKind.Number, Number = n }; }
        public static Atom B(bool n) { return new Atom { Kind = AtomKind.Boolean, Number = n ? 1 : 0 }; }
        public static Atom S(string n) { return new Atom { Kind = AtomKind.Text, Text = n ?? "" }; }
        public static Atom R(string n) { return new Atom { Kind = AtomKind.Reference, Text = n ?? "" }; }
        public static Atom V(V3 n) { return new Atom { Kind = AtomKind.Vector, Vector = n }; }
        public static Atom Q(Q4 n) { return new Atom { Kind = AtomKind.Rotation, Rotation = n }; }
        public Atom Clone() { return (Atom)MemberwiseClone(); }
        public bool Same(Atom a)
        {
            if (a == null || Kind != a.Kind) return false;
            switch (Kind)
            {
                case AtomKind.Number: case AtomKind.Boolean: return Number == a.Number;
                case AtomKind.Text: case AtomKind.Reference: return Text == a.Text;
                case AtomKind.Vector: return Vector.X == a.Vector.X && Vector.Y == a.Vector.Y && Vector.Z == a.Vector.Z;
                case AtomKind.Rotation: return Rotation.X == a.Rotation.X && Rotation.Y == a.Rotation.Y && Rotation.Z == a.Rotation.Z && Rotation.W == a.Rotation.W;
                default: return false;
            }
        }
    }

    public enum EntityKind : byte { Object, Rigidbody, Vehicle, Fsm, Globals }
    public sealed class Entity
    {
        public string Id = "", Key = "", Prefab = "", Parent = "";
        public EntityKind Kind;
        public bool Active = true;
        public V3 Position, Velocity, AngularVelocity;
        public Q4 Rotation = Q4.Identity;
        public Dictionary<string, Atom> Properties;
        public Entity() : this(new Dictionary<string, Atom>(StringComparer.Ordinal)) { }
        internal Entity(Dictionary<string, Atom> properties) { if (properties == null) throw new ArgumentNullException("properties"); Properties = properties; }
        public Entity Clone()
        {
            Entity e = (Entity)MemberwiseClone();
            e.Properties = new Dictionary<string, Atom>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Atom> p in Properties) e.Properties.Add(p.Key, p.Value.Clone());
            return e;
        }
    }

    public sealed class Player
    {
        public string Id = "", Name = "", Held = "", Seat = "";
        public V3 Position;
        public Q4 Rotation = Q4.Identity;
        public float Pitch;
        public bool SleepReady;
        public long LastPose;
        public Player Clone() { return (Player)MemberwiseClone(); }
    }

    public sealed class World
    {
        public string Id = Guid.NewGuid().ToString("N"), GameHash = "", SchemaHash = "";
        public long Revision, SleepEpoch;
        public double Money;
        public Dictionary<string, Entity> Entities = new Dictionary<string, Entity>(StringComparer.Ordinal);
        public Dictionary<string, Player> Players = new Dictionary<string, Player>(StringComparer.Ordinal);
        public EntityChanges Changes;
        public long ChangeSequence;
        public void EnableChangeTracking() { Changes = new EntityChanges(Protocol.MaxEntities); ChangeSequence = 0; }
        public void EntityChanged(string id) { if (Changes != null) ChangeSequence = Changes.Touch(id); }
        // Only for adapters that replace Entity snapshots instead of mutating them.
        public World ReferenceSnapshot()
        {
            World w = (World)MemberwiseClone();
            w.Entities = new Dictionary<string, Entity>(Entities, StringComparer.Ordinal);
            w.Players = new Dictionary<string, Player>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Player> p in Players) w.Players.Add(p.Key, p.Value.Clone());
            return w;
        }
        public World Clone()
        {
            World w = (World)MemberwiseClone();
            w.Changes = null; w.ChangeSequence = 0;
            w.Entities = new Dictionary<string, Entity>(StringComparer.Ordinal);
            w.Players = new Dictionary<string, Player>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Entity> e in Entities) w.Entities.Add(e.Key, e.Value.Clone());
            foreach (KeyValuePair<string, Player> p in Players) w.Players.Add(p.Key, p.Value.Clone());
            return w;
        }
        public bool AllSleeping()
        {
            if (Players.Count == 0) return false;
            foreach (Player p in Players.Values) if (!p.SleepReady) return false;
            return true;
        }
    }

    public enum CommandKind : byte { Take, Drop, EnterSeat, LeaveSeat, Interact, Bolt, Attach, Detach, Wire, Purchase, Sleep, Drive }
    public sealed class Command
    {
        public long Sequence;
        public CommandKind Kind;
        public string Target = "", Argument = "";
        public double Number;
        public V3 Vector;
    }
    public sealed class CommandResult
    {
        public long Sequence, Revision;
        public bool Accepted;
        public string Reason = "";
        public CommandResult Clone() { return (CommandResult)MemberwiseClone(); }
    }

    public sealed class Pose
    {
        public string Id = "";
        public long Sequence;
        public V3 Position;
        public Q4 Rotation = Q4.Identity;
        public float Pitch;
    }
    public sealed class BodyPose
    {
        public string Id = "";
        public V3 Position, Velocity;
        public Q4 Rotation = Q4.Identity;
    }
    public sealed class MotionFrame
    {
        public long Sequence;
        public List<BodyPose> Bodies;
        public MotionFrame() : this(0) { }
        public MotionFrame(int capacity) { Bodies = new List<BodyPose>(capacity); }
    }

    public static class Identity
    {
        // SHA-256 of a permanent, pre-reparenting key. Never String.GetHashCode().
        public static string Stable(string key)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
                return BitConverter.ToString(hash, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
