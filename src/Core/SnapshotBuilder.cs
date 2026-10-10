using System;
using System.Collections.Generic;

namespace SummerCoop.Core
{
    // A reusable probe. Published entities and their atoms are never mutated.
    public sealed class SnapshotBuilder
    {
        private readonly Entity scratch = new Entity();
        private readonly List<Atom> atoms = new List<Atom>();
        private int used;
        public Entity Begin(string id, string key, EntityKind kind)
        {
            used = 0; scratch.Properties.Clear();
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
        public bool Publish(World world)
        {
            Entity previous;
            world.Entities.TryGetValue(scratch.Id, out previous);
            if (previous != null && CheckpointStore.Equal(previous, scratch)) return false;
            Entity next = new Entity { Id = scratch.Id, Key = scratch.Key, Kind = scratch.Kind, Prefab = scratch.Prefab, Parent = scratch.Parent, Active = scratch.Active, Position = scratch.Position, Rotation = scratch.Rotation, Velocity = scratch.Velocity, AngularVelocity = scratch.AngularVelocity };
            foreach (KeyValuePair<string, Atom> property in scratch.Properties)
            {
                Atom old;
                next.Properties[property.Key] = previous != null && previous.Properties.TryGetValue(property.Key, out old) && property.Value.Same(old) ? old : property.Value.Clone();
            }
            world.Entities[scratch.Id] = next; world.EntityChanged(scratch.Id); return true;
        }
    }
}
