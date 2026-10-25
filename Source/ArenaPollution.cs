using HarmonyLib;
using RimWorld;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Vanilla blames the player for any pollution on a map that is not a player home: a wastepack dissolving there,
    /// wastepacks left behind when the map closes, or pollution gained by then, cost goodwill with the nearest
    /// settlement. Settlement map battle sites can hold wastepacks that NPC fights burst, so battle site maps are
    /// left out of all three.
    /// </summary>
    [HarmonyPatch(typeof(PollutionInfo), nameof(PollutionInfo.MapRemoved))]
    public static class Patch_PollutionInfo_MapRemoved
    {
        public static bool Prefix(PollutionInfo __instance) => !BaseSiege.IsArena(__instance.map?.Parent);
    }

    [HarmonyPatch(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.DoDissolutionEffectMap))]
    public static class Patch_DissolutionGoodwill_Map
    {
        public static bool Prefix(CompDissolutionEffect_Goodwill __instance) => !BaseSiege.IsArena(__instance.parent.MapHeld?.Parent);
    }

    /// <summary>Marks a battle site map being closed, when the things left on it count as abandoned at its tile.</summary>
    [HarmonyPatch(typeof(MapDeiniter), nameof(MapDeiniter.Deinit))]
    public static class Patch_MapDeiniter_Deinit
    {
        internal static bool ClosingArena;

        public static void Prefix(Map map) => ClosingArena = BaseSiege.IsArena(map?.Parent);

        public static void Finalizer() => ClosingArena = false;
    }

    [HarmonyPatch(typeof(CompDissolutionEffect_Goodwill), nameof(CompDissolutionEffect_Goodwill.DoDissolutionEffectWorld))]
    public static class Patch_DissolutionGoodwill_World
    {
        public static bool Prefix() => !Patch_MapDeiniter_Deinit.ClosingArena;
    }
}
