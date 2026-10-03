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
    /// Records the attacker/defender of the arena currently being initialized so the basic lord patch can tell them apart.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_InitArenaMap_Context
    {
        internal static Faction Attacker;
        internal static Faction Defender;

        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicDiplomacy.IncidentWorker_NPCConquest"), "InitArenaMap");

        public static void Prefix(Faction baseAttacker, Faction baseDefender)
        {
            Attacker = baseAttacker;
            Defender = baseDefender;
        }

        public static void Finalizer()
        {
            Attacker = null;
            Defender = null;
        }
    }

    /// <summary>
    /// Replaces the basic arena AI (both sides defend map center) with attacker assault / defender holding near its own entry.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MakeBasicLordForPawns
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicDiplomacy.UtilsAI"), "MakeBasicLordForPawns");

        public static bool Prefix(Faction faction, IEnumerable<Pawn> pawns, Map map, ref bool result, ref Lord __result)
        {
            Faction attacker = Patch_InitArenaMap_Context.Attacker;
            Faction defender = Patch_InitArenaMap_Context.Defender;
            if (attacker == null || defender == null || map == null || pawns == null)
                return true;

            try
            {
                LordJob job;
                if (faction == attacker)
                {
                    job = new LordJobRaidFactionFirstFlee(attacker, defender);
                }
                else if (faction == defender)
                {
                    Pawn anchor = pawns.FirstOrDefault(p => p != null && p.Spawned);
                    if (anchor == null)
                        return true;
                    IntVec3 holdPoint = RCellFinder.FindSiegePositionFrom(anchor.Position, map, allowRoofed: true, errorOnFail: false, requireBuildableTerrain: false);
                    if (!holdPoint.IsValid)
                        return true;
                    job = new LordJob_DefendPoint(holdPoint, 10f, null, false, true);
                }
                else
                {
                    return true;
                }

                __result = LordMaker.MakeNewLord(faction, job, map, pawns);
                result = __result != null;
                return !result;
            }
            catch (Exception e)
            {
                Log.Warning("[DD Overflow Fix] Custom basic arena AI failed, falling back to original: " + e.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Same as LordJobRaidFactionFirst but allows the vanilla auto-flee toil, matching the other arena AI branches.
    /// </summary>
    public class LordJobRaidFactionFirstFlee : DynamicDiplomacy.LordJobRaidFactionFirst
    {
        public override bool AddFleeToil => true;

        public LordJobRaidFactionFirstFlee()
        {
        }

        public LordJobRaidFactionFirstFlee(Faction faction, Faction targetFaction)
            : base(faction, targetFaction)
        {
        }
    }
}
