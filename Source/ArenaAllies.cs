using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace DDOverflowFix
{
    /// <summary>Dynamic Diplomacy's dominance rule: more than 9 and at least 40% of all NPC settlements.</summary>
    internal static class Dominance
    {
        public const float Share = 0.4f;
        public const int MinSettlements = 9;

        public static bool IsDominant(Faction faction)
        {
            if (faction == null || faction.IsPlayer)
                return false;
            int total = 0, own = 0;
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                if (s.Faction == faction)
                    own++;
                if (s.Faction != null && !s.Faction.IsPlayer && s.Faction.def.settlementGenerationWeight > 0f && !s.def.defName.StartsWith("City_"))
                    total++;
            }
            return own > MinSettlements && own >= total * Share;
        }
    }

    /// <summary>
    /// When one side of a battle is the dominant faction, one ally of the other side that is also hostile to the dominant
    /// faction joins with half of Dynamic Diplomacy's threat points: next to the attacker, or from the map edge for the
    /// defender. Allies are added to the side's fighter list, so Dynamic Diplomacy's outcome check and cleanup include them.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_InitArenaMap_Allies
    {
        private const float PointsShare = 0.5f;

        private static readonly AccessTools.FieldRef<object, List<Pawn>> Lhs =
            Arena.ArenaType == null ? null : AccessTools.FieldRefAccess<List<Pawn>>(Arena.ArenaType, "lhs");
        private static readonly AccessTools.FieldRef<object, List<Pawn>> Rhs =
            Arena.ArenaType == null ? null : AccessTools.FieldRefAccess<List<Pawn>>(Arena.ArenaType, "rhs");

        private static readonly MethodInfo SpawnPawnSet =
            Arena.ConquestType == null ? null : AccessTools.Method(Arena.ConquestType, "SpawnPawnSet");

        public static bool Prepare() =>
            Arena.InitArenaMap != null && Arena.GenerateGroup != null && SpawnPawnSet != null && Lhs != null && Rhs != null;

        public static MethodBase TargetMethod() => Arena.InitArenaMap;

        public static void Postfix(MapParent mapParent, Faction baseAttacker, Faction baseDefender)
        {
            try
            {
                Map map = mapParent?.Map;
                if (map == null || baseAttacker == null || baseDefender == null)
                    return;
                List<Pawn> attackers = Lhs(mapParent);
                List<Pawn> defenders = Rhs(mapParent);
                if (attackers == null || defenders == null)
                    return;

                if (Dominance.IsDominant(baseDefender))
                    Join(map, attackers, baseAttacker, baseDefender, fromEdge: false);
                if (Dominance.IsDominant(baseAttacker))
                    Join(map, defenders, baseDefender, baseAttacker, fromEdge: true, avoid: attackers);
            }
            catch (Exception e)
            {
                Log.Warning("[DD Arena Fixes] Could not add allied support to the battle: " + e.Message);
            }
        }

        private static void Join(Map map, List<Pawn> side, Faction sideFaction, Faction dominant, bool fromEdge, List<Pawn> avoid = null)
        {
            Faction ally = Find.FactionManager.AllFactionsListForReading
                .Where(f => f != sideFaction && f != dominant && !f.IsPlayer && !f.defeated && !f.def.hidden
                    && f.def.settlementGenerationWeight > 0f
                    && f.RelationKindWith(sideFaction) == FactionRelationKind.Ally && f.HostileTo(dominant))
                .RandomElementWithFallback();
            if (ally == null)
                return;

            IntVec3 spot = fromEdge ? EdgeSpot(map, avoid) : SideSpot(map, side);
            if (!spot.IsValid)
                return;
            var kinds = (List<PawnKindDef>)Arena.GenerateGroup.Invoke(null, new object[] { ally, Arena.Points * PointsShare });
            if (kinds == null || kinds.Count == 0)
                return;
            var pawns = (List<Pawn>)SpawnPawnSet.Invoke(null, new object[] { map, kinds, spot, ally });
            if (pawns == null || pawns.Count == 0)
                return;
            LordMaker.MakeNewLord(ally, new LordJobRaidFactionFirstFlee(ally, dominant), map, pawns);
            side.AddRange(pawns);
        }

        private static IntVec3 SideSpot(Map map, List<Pawn> side)
        {
            Pawn anchor = side.FirstOrDefault(p => p != null && p.Spawned && p.Map == map);
            return anchor != null ? anchor.Position : EdgeSpot(map, null);
        }

        /// <summary>A reachable entry cell, as far from the given pawns as the map allows.</summary>
        private static IntVec3 EdgeSpot(Map map, List<Pawn> avoid)
        {
            Pawn other = avoid?.FirstOrDefault(p => p != null && p.Spawned && p.Map == map);
            float minDistSq = other == null ? 0f : (map.Size.x * 0.5f) * (map.Size.x * 0.5f);
            if (RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 cell, map, CellFinder.EdgeRoadChance_Neutral, false,
                    c => other == null || (c - other.Position).LengthHorizontalSquared >= minDistSq))
                return cell;
            return RCellFinder.TryFindRandomPawnEntryCell(out cell, map, CellFinder.EdgeRoadChance_Neutral) ? cell : IntVec3.Invalid;
        }
    }
}
