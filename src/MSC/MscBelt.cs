using System;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MWCoop
{
    // A belt is a numbered consumable, not a DatabaseMotor.ThisPart component.
    internal static class MscBelt
    {
        internal static PlayMakerFSM Use(GameObject part) { return part != null ? Game.FsmOn(part, "Use") : null; }
        internal static bool IsBelt(GameObject part)
        {
            PlayMakerFSM use = Use(part);
            if (use == null || use.FsmVariables.FindFsmBool("Installed") == null) return false;
            FsmGameObject db = use.FsmVariables.FindFsmGameObject("Database");
            return db != null && db.Value != null && db.Value.name == "AlternatorBelt"
                || part.name.StartsWith("alternator belt(", StringComparison.Ordinal) || part.name.StartsWith("alternatorbelt", StringComparison.Ordinal);
        }
        internal static string Identity(GameObject part)
        {
            if (!IsBelt(part)) return "";
            PlayMakerFSM use = Use(part);
            foreach (string name in new[] { "UniqueTag", "ID" })
            {
                FsmString tag = use.FsmVariables.FindFsmString(name);
                if (tag != null && tag.Value.Length > 0 && tag.Value != "alternatorbelt0") return "mscitem:" + tag.Value;
            }
            return part.name.StartsWith("alternator belt(item", StringComparison.Ordinal) ? "mscitem:" + part.name : "";
        }
        internal static bool Installed(GameObject part)
        {
            PlayMakerFSM use = IsBelt(part) ? Use(part) : null;
            return use != null && use.FsmVariables.FindFsmBool("Installed").Value;
        }
    }
}
