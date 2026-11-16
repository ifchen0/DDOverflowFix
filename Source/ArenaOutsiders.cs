using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace DDOverflowFix
{
    /// <summary>
    /// With Vanilla Tune-Up's "Former colonists and outsiders come back into play" enabled, battles follow its raid rule:
    /// each side (and each ally group) spawned through Dynamic Diplomacy's SpawnPawnSet may bring one healthy member
    /// vanilla never redresses (other race or xenotype, creepjoiner) or a former colonist, picked and rolled by
    /// Vanilla Tune-Up. They are added to the side's fighter list before the lords are made, so Dynamic Diplomacy's
    /// outcome, survivors and cleanup include them. Without Vanilla Tune-Up nothing is patched.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_SpawnPawnSet_Outsiders
    {
        private static readonly Type Visits = AccessTools.TypeByName("VanillaTuneUp.FormerColonistVisits");
        private static readonly MethodInfo TryPickFighter = Visits == null ? null : AccessTools.Method(Visits, "TryPickFighter");
        private static readonly MethodInfo Spawn = Visits == null ? null : AccessTools.Method(Visits, "Spawn");
        private static readonly MethodInfo Announce = Visits == null ? null : AccessTools.Method(Visits, "Announce");

        public static bool Prepare() => TargetMethod() != null && TryPickFighter != null && Spawn != null;

        public static MethodBase TargetMethod() =>
            Arena.ConquestType == null ? null : AccessTools.Method(Arena.ConquestType, "SpawnPawnSet");

        public static void Postfix(Map map, IntVec3 spot, Faction faction, List<Pawn> __result)
        {
            try
            {
                if (map == null || faction == null || __result == null)
                    return;
                var pawn = (Pawn)TryPickFighter.Invoke(null, new object[] { faction });
                if (pawn == null)
                    return;
                // A base siege defender is the garrison already in place: stand beside one of them and share its lord.
                bool garrison = __result == BaseSiege.ActiveGarrison;
                Pawn anchor = garrison ? __result.Where(p => p.Spawned && p.Map == map).RandomElementWithFallback() : null;
                IntVec3 center = anchor?.Position ?? spot;
                if (!center.IsValid || !center.InBounds(map))
                    return;
                Spawn.Invoke(null, new object[] { pawn, center, map });
                if (garrison)
                    anchor?.GetLord()?.AddPawn(pawn);
                __result.Add(pawn);
                Announce?.Invoke(null, new object[] { pawn, faction, MessageTypeDefOf.NeutralEvent });
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DD Fixes & Tweaks] Adding an outsider to a battle group failed: " + (e is TargetInvocationException t ? t.InnerException : e),
                    0x0D5A11);
            }
        }
    }
}
