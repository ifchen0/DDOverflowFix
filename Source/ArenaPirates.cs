using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DDOverflowFix
{
    /// <summary>
    /// The stronger the defenders are than the attackers, the likelier a pirate faction hostile to both sides drops in on
    /// the defenders by drop pod, so battles at well-defended bases are not decided before they start. The gap
    /// 1 - attacker power / defender power (pawns' combat power; turrets are left out, which only makes it a little lower
    /// for bases that have them) is both the chance and the pirates' share of Dynamic Diplomacy's threat points, capped at
    /// half, so a small gap brings few pirates. The pirates are a third party: they are in
    /// neither fighter list, so the outcome check ignores them.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_InitArenaMap_Pirates
    {
        private const float MaxPointsShare = 0.5f;

        public static bool Enabled = true;

        // Test hook: drop the pirates whatever the chance.
        public static bool Forced;

        private static readonly AccessTools.FieldRef<object, List<Pawn>> Lhs =
            Arena.ArenaType == null ? null : AccessTools.FieldRefAccess<List<Pawn>>(Arena.ArenaType, "lhs");
        private static readonly AccessTools.FieldRef<object, List<Pawn>> Rhs =
            Arena.ArenaType == null ? null : AccessTools.FieldRefAccess<List<Pawn>>(Arena.ArenaType, "rhs");

        public static bool Prepare() => Arena.InitArenaMap != null && Arena.GenerateGroup != null && Lhs != null && Rhs != null;

        public static MethodBase TargetMethod() => Arena.InitArenaMap;

        // After the allies, so their strength counts.
        [HarmonyPriority(Priority.Low)]
        public static void Postfix(MapParent mapParent, Faction baseAttacker, Faction baseDefender)
        {
            try
            {
                Map map = mapParent?.Map;
                if (map == null || baseAttacker == null || baseDefender == null || !Enabled)
                    return;
                List<Pawn> attackers = Lhs(mapParent);
                List<Pawn> defenders = Rhs(mapParent);
                if (attackers == null || defenders == null)
                    return;
                float gap = Chance(attackers, defenders);
                if (gap <= 0f || !(Forced || Rand.Chance(gap)))
                    return;
                DropIn(map, defenders, baseAttacker, baseDefender, Arena.Points * Mathf.Min(MaxPointsShare, gap));
            }
            catch (Exception e)
            {
                Exception cause = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                Log.Warning("[DD Fixes & Tweaks] Could not add pirates to the battle: " + cause);
            }
        }

        public static float Chance(List<Pawn> attackers, List<Pawn> defenders)
        {
            float defence = Power(defenders);
            return defence <= 0f ? 0f : Mathf.Max(0f, 1f - Power(attackers) / defence);
        }

        public static float Power(List<Pawn> side) =>
            side.Where(p => p != null && p.Spawned && !p.Dead && !p.Downed).Sum(p => p.kindDef.combatPower);

        private static void DropIn(Map map, List<Pawn> defenders, Faction attacker, Faction defender, float points)
        {
            // Industrial or later, so arriving by drop pod fits.
            Faction pirates = Find.FactionManager.AllFactionsListForReading
                .Where(f => f.def.permanentEnemy && !f.IsPlayer && !f.defeated && !f.def.hidden && f.def.humanlikeFaction
                    && f.def.techLevel >= TechLevel.Industrial && f != attacker && f != defender
                    && f.HostileTo(attacker) && f.HostileTo(defender))
                .RandomElementWithFallback();
            if (pirates == null)
                return;
            var kinds = (List<PawnKindDef>)Arena.GenerateGroup.Invoke(null, new object[] { pirates, points });
            if (kinds == null || kinds.Count == 0)
                return;
            List<Pawn> pawns = kinds.Select(k => PawnGenerator.GeneratePawn(k, pirates)).ToList();

            // Land among the defenders, away from the attackers coming in from the edge.
            Pawn target = defenders.Where(p => p != null && p.Spawned && p.Map == map && !p.Dead).RandomElementWithFallback();
            IntVec3 near = target != null ? target.Position : map.Center;
            if (!DropCellFinder.TryFindDropSpotNear(near, map, out IntVec3 spot, allowFogged: true, canRoofPunch: false))
                spot = DropCellFinder.RandomDropSpot(map);
            DropPodUtility.DropThingsNear(spot, map, pawns.Cast<Thing>(), forbid: false, allowFogged: true, faction: pirates);
            LordMaker.MakeNewLord(pirates, new LordJobRaidFactionFirstFlee(pirates, defender), map, pawns);
        }

        public static void DrawSetting(Listing_Standard listing)
        {
            listing.Gap(4f);
            TaggedString label = "DDAF_Pirates".Translate();
            string tooltip = "DDAF_PiratesDesc".Translate();
            SettingsWarnings.WarnIfNoBattles(ref label, ref tooltip);
            listing.CheckboxLabeled(label, ref Enabled, tooltip);
        }
    }
}
