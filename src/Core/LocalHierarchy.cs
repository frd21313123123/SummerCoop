using System;
using System.Collections.Generic;

namespace SummerCoop.Core
{
    // Local cameras, hands and menus never belong to a shared native checkpoint.
    public static class LocalHierarchy
    {
        public static bool IsRootName(string name)
        {
            if (name == null) return false;
            if (name.EndsWith("(Clone)", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 7);
            return name == "PLAYER" || name == "GUI" || name == "HUD" || name == "Steam"
                || name == "FPSCamera"
                || name.StartsWith("MSCLoader", StringComparison.Ordinal) || name.StartsWith("SummerCoop", StringComparison.Ordinal);
        }
        public static bool IsNestedName(string name)
        {
            return name != null && (name == "PLAYER" || name == "PLAYER(Clone)"
                || name == "FPSCamera" || name == "FPSCamera(Clone)"
                || name.StartsWith("MSCLoader", StringComparison.Ordinal) || name.StartsWith("SummerCoop", StringComparison.Ordinal));
        }
        public static bool IsPersonalSystemName(string parent, string name)
        {
            return parent == "Systems" && (name == "Options" || name == "OptionsMenu" || name == "InitializeControls" || name == "Death");
        }
        private static bool IsPersonalSystemKey(string key)
        {
            // Collect only these roots. Their descendants are removed by Contains
            // below, without adding every menu label or death overlay as a root.
            return key == "scene/Systems[0]/Options[0]" || key == "scene/Systems[0]/OptionsMenu[0]"
                || key == "scene/Systems[0]/InitializeControls[0]" || key == "scene/Systems[0]/Death[0]";
        }

        private static bool Contains(string root, string key)
        {
            return key == root || key.StartsWith(root + "/", StringComparison.Ordinal) || key.StartsWith(root + "|fsm/", StringComparison.Ordinal);
        }
        private static bool SharedObject(Entity entity)
        {
            Atom loose;
            return entity.Prefab != "" || entity.Kind == EntityKind.Vehicle
                || entity.Properties.TryGetValue("@loose", out loose) && loose.Kind == AtomKind.Boolean && loose.Number != 0;
        }
        private static bool PersonalRoot(Entity entity)
        {
            Atom name, tag;
            if ((entity.Kind != EntityKind.Object && entity.Kind != EntityKind.Rigidbody) || SharedObject(entity)
                || !entity.Properties.TryGetValue("@name", out name) || name.Kind != AtomKind.Text || !IsRootName(name.Text)) return false;
            // Steam also names sauna vapour, and HUD is used by the in-world
            // computer game. Only top-level occurrences denote local roots.
            if (!IsNestedName(name.Text) && entity.Parent != "") return false;
            // An ordinary item named PLAYER is not evidence of a local player tree.
            return name.Text != "PLAYER" && name.Text != "PLAYER(Clone)"
                || entity.Properties.TryGetValue("@tag", out tag) && tag.Kind == AtomKind.Text && tag.Text == "Player";
        }

        public static int RemoveFromCheckpoint(World world)
        {
            var roots = new List<string>();
            foreach (Entity entity in world.Entities.Values)
                if (PersonalRoot(entity) || IsPersonalSystemKey(entity.Key) && entity.Kind != EntityKind.Fsm) roots.Add(entity.Key);
            if (roots.Count == 0) return 0;
            // Held parts retain their own identity, even if an old build registered
            // one beneath a personal key. Preserve their complete subtrees.
            var shared = new List<string>();
            foreach (Entity entity in world.Entities.Values)
                if (SharedObject(entity)) foreach (string root in roots)
                    if (Contains(root, entity.Key)) { shared.Add(entity.Key); break; }
            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entity entity in world.Entities.Values)
            {
                bool personal = false;
                foreach (string root in roots) if (Contains(root, entity.Key)) { personal = true; break; }
                if (!personal) continue;
                foreach (string root in shared) if (Contains(root, entity.Key)) { personal = false; break; }
                if (personal) removed.Add(entity.Id);
            }
            var replacements = new List<Entity>();
            foreach (Entity entity in world.Entities.Values)
            {
                if (removed.Contains(entity.Id)) continue;
                Entity next = null;
                if (removed.Contains(entity.Parent)) { next = entity.Clone(); next.Parent = ""; }
                foreach (KeyValuePair<string, Atom> property in entity.Properties)
                {
                    Atom value = property.Value;
                    if (value.Kind != AtomKind.Reference) continue;
                    int component = value.Text.IndexOf('#');
                    if (!removed.Contains(component < 0 ? value.Text : value.Text.Substring(0, component))) continue;
                    if (next == null) next = entity.Clone();
                    // Omit personal references so Apply preserves the scene's local
                    // value. Writing null would break NPC targets and hand tools.
                    next.Properties.Remove(property.Key);
                }
                if (next != null) replacements.Add(next);
            }
            foreach (string id in removed) { world.Entities.Remove(id); world.EntityChanged(id); }
            foreach (Entity entity in replacements) { world.Entities[entity.Id] = entity; world.EntityChanged(entity.Id); }
            return removed.Count;
        }
    }
}
