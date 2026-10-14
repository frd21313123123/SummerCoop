using System;

namespace SummerCoop.Core
{
    public static class PlayerControls
    {
        public static readonly string[] Needs = { "PlayerHunger", "PlayerThirst", "PlayerFatigue", "PlayerStress", "PlayerUrine", "PlayerDirtiness" };
        public static readonly string[] NeedLabels = { "Голод", "Жажда", "Усталость", "Стресс", "Мочевой пузырь", "Грязь" };
        public static readonly string[] Effects = { "PlayerDrunk", "PlayerDrunkAdjusted", "PlayerBurns", "PlayerAllergy" };
        public static float ClampPercent(float value)
        {
            if (!V3.Finite(value)) throw new ArgumentException("Invalid percentage");
            return Math.Max(0, Math.Min(100, value));
        }
        public static string ValidateSpawn(World world, Player player, Command command)
        {
            if (command.Number < 0 || command.Number > 100) return "Condition must be between 0 and 100";
            if (command.Target == "" || command.Target.Length > 1024) return "Unknown spawn template";
            if (world.Entities.Count > Protocol.MaxEntities - 512) return "World object limit reached";
            if (player.Held != "") return "Освободите руки перед созданием предмета";
            return null;
        }
    }
    public static class MountedPose
    {
        // Only non-physical mounted parts use this constraint. Doors and suspension
        // keep their native joints and physics rather than being welded by the mod.
        public static bool MustSnap(bool parentChanged, bool hasBody, bool mounted)
        {
            return parentChanged || !hasBody && mounted;
        }
        public static bool AcceptMotion(bool hasBody, bool mounted) { return hasBody || !mounted; }
    }
}
