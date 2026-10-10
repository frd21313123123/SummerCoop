using System;
using System.Collections.Generic;

namespace SummerCoop.Core
{
    // A reusable probe. Published entities and their atoms are never mutated.
    public sealed class SnapshotBuilder
    {
        private readonly Entity scratch = new Entity();
        private readonly List<Atom> atoms = new List<Atom>();
        private readonly Atom falseValue = Atom.B(false), trueValue = Atom.B(true);
        private readonly Atom zeroVector = Atom.V(new V3()), unitVector = Atom.V(new V3(1, 1, 1)), identityRotation = Atom.Q(Q4.Identity);
        private readonly Dictionary<double, Atom> smallNumbers = new Dictionary<double, Atom>();
        private readonly Dictionary<string, Atom> texts = new Dictionary<string, Atom>(StringComparer.Ordinal);
        private readonly Dictionary<string, Atom> references = new Dictionary<string, Atom>(StringComparer.Ordinal);
        private readonly Dictionary<string, Atom>[] propertyBuffers = new Dictionary<string, Atom>[9];
        private int propertyBufferIndex;
        private int used;
        public Entity Begin(string id, string key, EntityKind kind)
        {
            return Begin(id, key, kind, 32);
        }
        private static int BufferIndex(int count) { int index = 0; while (index < 8 && (32 << index) < count) index++; return index; }
        public Entity Begin(string id, string key, EntityKind kind, int propertyHint)
        {
            propertyBufferIndex = BufferIndex(propertyHint);
            Dictionary<string, Atom> buffer = propertyBuffers[propertyBufferIndex];
            if (buffer == null) { buffer = new Dictionary<string, Atom>(32 << propertyBufferIndex, StringComparer.Ordinal); propertyBuffers[propertyBufferIndex] = buffer; }
            used = 0; buffer.Clear(); scratch.Properties = buffer;
            scratch.Id = id; scratch.Key = key; scratch.Kind = kind;
            scratch.Prefab = scratch.Parent = ""; scratch.Active = true;
            scratch.Position = scratch.Velocity = scratch.AngularVelocity = new V3(); scratch.Rotation = Q4.Identity;
            return scratch;
        }
        private Atom Next(AtomKind kind)
        {
            if (used == atoms.Count) atoms.Add(new Atom());
            Atom value = atoms[used++]; value.Kind = kind; value.Number = 0; value.Text = ""; value.Vector = new V3(); value.Rotation = new Q4(); return value;
        }
        public Atom N(double value) { Atom a = Next(AtomKind.Number); a.Number = value; return a; }
        public Atom B(bool value) { Atom a = Next(AtomKind.Boolean); a.Number = value ? 1 : 0; return a; }
        public Atom S(string value) { Atom a = Next(AtomKind.Text); a.Text = value ?? ""; return a; }
        public Atom R(string value) { Atom a = Next(AtomKind.Reference); a.Text = value ?? ""; return a; }
        public Atom V(V3 value) { Atom a = Next(AtomKind.Vector); a.Vector = value; return a; }
        public Atom Q(Q4 value) { Atom a = Next(AtomKind.Rotation); a.Rotation = value; return a; }
        private Atom Published(Atom value)
        {
            // These shared values never escape as scratch probes and are never mutated.
            // Restrict caches to recurring values so continuously changing telemetry cannot grow them.
            if (value.Kind == AtomKind.Boolean) return value.Number == 0 ? falseValue : trueValue;
            if (value.Kind == AtomKind.Vector)
            {
                if (value.Vector.X == 0 && value.Vector.Y == 0 && value.Vector.Z == 0) return zeroVector;
                if (value.Vector.X == 1 && value.Vector.Y == 1 && value.Vector.Z == 1) return unitVector;
            }
            if (value.Kind == AtomKind.Rotation && value.Rotation.X == 0 && value.Rotation.Y == 0 && value.Rotation.Z == 0 && value.Rotation.W == 1) return identityRotation;
            Atom cached;
            if (value.Kind == AtomKind.Number && value.Number >= -16 && value.Number <= 256 && value.Number == Math.Truncate(value.Number))
            {
                if (smallNumbers.TryGetValue(value.Number, out cached)) return cached;
                cached = value.Clone(); smallNumbers.Add(value.Number, cached); return cached;
            }
            if ((value.Kind == AtomKind.Text || value.Kind == AtomKind.Reference) && value.Text.Length <= 128)
            {
                Dictionary<string, Atom> cache = value.Kind == AtomKind.Text ? texts : references;
                if (cache.TryGetValue(value.Text, out cached)) return cached;
                if (cache.Count < 4096) { cached = value.Clone(); cache.Add(value.Text, cached); return cached; }
            }
            return value.Clone();
        }
        public bool Publish(World world)
        {
            int actualIndex = BufferIndex(scratch.Properties.Count);
            if (actualIndex > propertyBufferIndex)
            {
                // A previously unknown large array must not enlarge every following small probe.
                propertyBuffers[propertyBufferIndex] = null;
                if (propertyBuffers[actualIndex] == null) propertyBuffers[actualIndex] = scratch.Properties;
            }
            Entity previous;
            world.Entities.TryGetValue(scratch.Id, out previous);
            if (previous != null && CheckpointStore.Equal(previous, scratch)) return false;
            Entity next = new Entity { Id = scratch.Id, Key = scratch.Key, Kind = scratch.Kind, Prefab = scratch.Prefab, Parent = scratch.Parent, Active = scratch.Active, Position = scratch.Position, Rotation = scratch.Rotation, Velocity = scratch.Velocity, AngularVelocity = scratch.AngularVelocity, Properties = new Dictionary<string, Atom>(scratch.Properties.Count, StringComparer.Ordinal) };
            foreach (KeyValuePair<string, Atom> property in scratch.Properties)
            {
                Atom old;
                next.Properties[property.Key] = previous != null && previous.Properties.TryGetValue(property.Key, out old) && property.Value.Same(old) ? old : Published(property.Value);
            }
            world.Entities[scratch.Id] = next; world.EntityChanged(scratch.Id); return true;
        }
    }
}
