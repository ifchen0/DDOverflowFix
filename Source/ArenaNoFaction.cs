using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// DD gives every battle site the player faction, so the site is listed as the player's.
    /// Nothing in DD reads that faction, and vanilla treats a map parent without faction the same way as a player one,
    /// so battle sites get no faction instead.
    /// </summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.SetFaction))]
    public static class Patch_ArenaSetFaction
    {
        public static bool Prepare() => Arena.ArenaType != null;

        public static void Prefix(WorldObject __instance, ref Faction newFaction)
        {
            if (newFaction != null && newFaction.IsPlayer && Arena.ArenaType.IsInstanceOfType(__instance))
                newFaction = null;
        }
    }

    /// <summary>Clears the player faction from battle sites created before this fix.</summary>
    [HarmonyPatch]
    public static class Patch_ArenaExposeData
    {
        private static readonly AccessTools.FieldRef<WorldObject, Faction> FactionField =
            AccessTools.FieldRefAccess<WorldObject, Faction>("factionInt");

        public static bool Prepare() => Arena.ArenaType != null && TargetMethod() != null;

        public static System.Reflection.MethodBase TargetMethod() => AccessTools.DeclaredMethod(Arena.ArenaType, "ExposeData");

        public static void Postfix(WorldObject __instance)
        {
            if (Scribe.mode == LoadSaveMode.PostLoadInit && __instance.Faction != null && __instance.Faction.IsPlayer)
                FactionField(__instance) = null;
        }
    }
}
