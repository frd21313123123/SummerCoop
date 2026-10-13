using System;

namespace SummerCoop.Core
{
    public static class ToolInventory
    {
        public static bool IsCase(string name)
        {
            return name == "spanner set" || name == "ratchet set"
                || name != null && (name.StartsWith("spanner set(", StringComparison.Ordinal)
                    || name.StartsWith("ratchet set(", StringComparison.Ordinal));
        }
        public static bool IsContainer(string name, string parentName) { return name == "Tools" && IsCase(parentName); }
        public static bool IsRoot(Entity entity, World world)
        {
            Entity parent; Atom name, parentName;
            return entity.Kind != EntityKind.Fsm && world.Entities.TryGetValue(entity.Parent, out parent)
                && entity.Properties.TryGetValue("@name", out name) && name.Kind == AtomKind.Text
                && parent.Properties.TryGetValue("@name", out parentName) && parentName.Kind == AtomKind.Text
                && IsContainer(name.Text, parentName.Text);
        }
    }
}
