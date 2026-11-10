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
    /// <summary>
    /// A faction is dominant with more than 9 NPC settlements and at least 3 times the average NPC faction's count,
    /// capped at Dynamic Diplomacy's 40% of all NPC settlements. Comparing with the average keeps the rule fair for
    /// worlds with many factions, and two factions splitting the world both count.
    /// </summary>
    internal static class Dominance
    {
        public const float MaxShare = 0.4f;
        public const float TimesAverage = 3f;
        public const int MinSettlements = 9;

        public static bool IsDominant(Faction faction)
        {
            if (faction == null || faction.IsPlayer)
                return false;
            var counts = new Dictionary<Faction, int>();
            int total = 0;
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                Faction owner = s.Faction;
                if (owner == null || owner.IsPlayer || owner.def.settlementGenerationWeight <= 0f || s.def.defName.StartsWith("City_"))
                    continue;
                counts.TryGetValue(owner, out int n);
                counts[owner] = n + 1;
                total++;
            }
            if (!counts.TryGetValue(faction, out int own) || own <= MinSettlements)
                return false;
            float average = total / (float)counts.Count;
            return own >= System.Math.Min(total * MaxShare, average * TimesAverage);
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

                Faction attackerAlly = null;
                if (Dominance.IsDominant(baseDefender))
                    attackerAlly = Join(map, attackers, baseAttacker, baseDefender, fromEdge: false);
                // With both sides dominant, the defender's ally must also fight the attacker's ally, or the two allies
                // can outlive the main forces without fighting each other and the battle never ends.
                if (Dominance.IsDominant(baseAttacker))
                    Join(map, defenders, baseDefender, baseAttacker, fromEdge: true, avoid: attackers, alsoHostileTo: attackerAlly);
            }
            catch (Exception e)
            {
                Exception cause = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                Log.Warning("[DD Fixes & Tweaks] Could not add allied support to the battle: " + cause);
            }
        }

        /// <summary>Spawns one ally group for the side and returns its faction, or null when none joined.</summary>
        private static Faction Join(Map map, List<Pawn> side, Faction sideFaction, Faction dominant, bool fromEdge,
            List<Pawn> avoid = null, Faction alsoHostileTo = null)
        {
            Faction ally = Find.FactionManager.AllFactionsListForReading
                .Where(f => f != sideFaction && f != dominant && !f.IsPlayer && !f.defeated && !f.def.hidden
                    && f.def.settlementGenerationWeight > 0f
                    && f.RelationKindWith(sideFaction) == FactionRelationKind.Ally && f.HostileTo(dominant)
                    && (alsoHostileTo == null || (f != alsoHostileTo && f.HostileTo(alsoHostileTo))))
                .RandomElementWithFallback();
            if (ally == null)
                return null;

            IntVec3 spot = fromEdge ? EdgeSpot(map, avoid) : SideSpot(map, side);
            if (!spot.IsValid)
                return null;
            var kinds = (List<PawnKindDef>)Arena.GenerateGroup.Invoke(null, new object[] { ally, Arena.Points * PointsShare });
            if (kinds == null || kinds.Count == 0)
                return null;
            var pawns = (List<Pawn>)SpawnPawnSet.Invoke(null, new object[] { map, kinds, spot, ally });
            if (pawns == null || pawns.Count == 0)
                return null;
            // Added before the lord so the outcome check and cleanup still see the pawns if the lord fails.
            side.AddRange(pawns);
            LordMaker.MakeNewLord(ally, new LordJobRaidFactionFirstFlee(ally, dominant), map, pawns);
            return ally;
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
