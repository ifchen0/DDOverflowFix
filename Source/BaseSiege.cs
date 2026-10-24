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
    /// Fourth arena style: the battle map is generated with the contested settlement's own map generator, so the
    /// defender fights from a real base with the garrison that generator spawns (Custom Base Framework layouts and
    /// Better Settlement Defense roles apply). The attacker uses the basic arena AI.
    /// </summary>
    public static class BaseSiege
    {
        public const float DefaultChance = 0.25f;
        public static float Chance = DefaultChance;

        private static readonly AccessTools.FieldRef<WorldObject, Faction> FactionField =
            AccessTools.FieldRefAccess<WorldObject, Faction>("factionInt");

        // Arena whose map is being generated as a base, and the generator borrowed from its settlement.
        internal static MapParent Generating;
        internal static MapGeneratorDef GeneratingDef;
        private static Faction savedFaction;

        // Garrison standing in for the defender group while InitArenaMap sets up a base siege.
        internal static List<Pawn> ActiveGarrison;

        public static bool IsArena(MapParent parent) => parent != null && Arena.ArenaType != null && Arena.ArenaType.IsInstanceOfType(parent);

        /// <summary>Rolled once per arena (seeded by its ID), so every call agrees without saving anything.</summary>
        public static bool Wanted(MapParent arena)
        {
            ForcedArenaAI forced = Patch_InitArenaMap_ForcedRoll.Forced;
            if (forced != ForcedArenaAI.None && forced != ForcedArenaAI.BaseSiege)
                return false;
            Settlement combatLoc = Arena.CombatLoc(arena);
            Faction defender = Arena.DefenderFaction(arena);
            if (combatLoc == null || combatLoc.Destroyed || defender == null || defender.IsPlayer)
                return false;
            return forced == ForcedArenaAI.BaseSiege || Rand.ChanceSeeded(Chance, Gen.HashCombineInt(arena.ID, 0x0BA5E));
        }

        /// <summary>A base siege map is an arena map holding buildings of the defender.</summary>
        public static bool IsBaseSiegeMap(Map map)
        {
            if (map == null || !IsArena(map.Parent))
                return false;
            Faction defender = Arena.DefenderFaction(map.Parent);
            return defender != null && map.listerBuildings.allBuildingsNonColonist.Any(b => b.Faction == defender);
        }

        /// <summary>Defender pawns able to fight; Custom Base Framework civilians are left out.</summary>
        public static List<Pawn> Garrison(Map map, Faction defender) =>
            map.mapPawns.SpawnedPawnsInFaction(defender)
                .Where(p => !p.Dead && !p.Downed && (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid)
                    && !p.WorkTagIsDisabled(WorkTags.Violent)
                    && !(p.GetLord()?.LordJob?.GetType().Name.Contains("Zivil") ?? false))
                .ToList();

        internal static void BeginGeneration(MapParent arena, MapGeneratorDef def)
        {
            Generating = arena;
            GeneratingDef = def;
            // Settlement generators take the base owner from the map parent's faction; the arena belongs to the player.
            savedFaction = arena.Faction;
            FactionField(arena) = Arena.DefenderFaction(arena);
        }

        internal static void EndGeneration()
        {
            if (Generating == null)
                return;
            FactionField(Generating) = savedFaction;
            Generating = null;
            GeneratingDef = null;
            savedFaction = null;
        }

        public static void DrawSetting(Listing_Standard listing)
        {
            listing.Gap(4f);
            listing.Label("DDAF_BaseSiegeChance".Translate(Chance.ToStringPercent()), -1f, "DDAF_BaseSiegeChanceDesc".Translate());
            Chance = Mathf.Round(listing.Slider(Chance, 0f, 1f) * 20f) / 20f;
        }
    }

    /// <summary>Generates a chosen arena's map with its settlement's generator, owned by the defender meanwhile.</summary>
    [HarmonyPatch(typeof(GetOrGenerateMapUtility), nameof(GetOrGenerateMapUtility.GetOrGenerateMap),
        new[] { typeof(PlanetTile), typeof(IntVec3), typeof(WorldObjectDef), typeof(IEnumerable<GenStepWithParams>), typeof(bool) })]
    public static class Patch_GetOrGenerateMap_BaseSiege
    {
        public static bool Prepare() => Arena.ArenaType != null && Arena.CombatLoc != null && Arena.DefenderFaction != null;

        // Before Custom Base Framework's prefixes, which already look at the map parent.
        [HarmonyPriority(Priority.High)]
        public static void Prefix(PlanetTile tile)
        {
            if (BaseSiege.Generating != null || Current.Game.FindMap(tile) != null)
                return;
            MapParent parent = Find.WorldObjects.MapParentAt(tile);
            if (!BaseSiege.IsArena(parent))
                return;
            try
            {
                if (!BaseSiege.Wanted(parent))
                    return;
                MapGeneratorDef def = Arena.CombatLoc(parent).MapGeneratorDef;
                if (def == null || def == parent.MapGeneratorDef)
                    return;
                BaseSiege.BeginGeneration(parent, def);
            }
            catch (Exception e)
            {
                BaseSiege.EndGeneration();
                Log.Warning("[DD Arena Fixes] Could not set up a settlement map for the battle, using the open field: " + e.Message);
            }
        }

        public static void Finalizer() => BaseSiege.EndGeneration();
    }

    [HarmonyPatch(typeof(MapParent), nameof(MapParent.MapGeneratorDef), MethodType.Getter)]
    public static class Patch_MapParent_MapGeneratorDef
    {
        public static void Postfix(MapParent __instance, ref MapGeneratorDef __result)
        {
            if (__instance == BaseSiege.Generating && BaseSiege.GeneratingDef != null)
                __result = BaseSiege.GeneratingDef;
        }
    }

    [HarmonyPatch]
    public static class Patch_InitArenaMap_BaseSiege
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => Arena.InitArenaMap;

        public static void Prefix() => BaseSiege.ActiveGarrison = null;

        public static void Finalizer() => BaseSiege.ActiveGarrison = null;
    }

    /// <summary>On a base siege map the defender group is the garrison already there instead of newly spawned pawns.</summary>
    [HarmonyPatch]
    public static class Patch_SpawnPawnSet_BaseSiege
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            Arena.ConquestType == null ? null : AccessTools.Method(Arena.ConquestType, "SpawnPawnSet");

        public static bool Prefix(Map map, Faction faction, ref List<Pawn> __result)
        {
            if (faction == null || faction != Patch_InitArenaMap_Context.Defender || !BaseSiege.IsBaseSiegeMap(map))
                return true;
            List<Pawn> garrison = BaseSiege.Garrison(map, faction);
            if (garrison.Count == 0)
            {
                Log.Warning("[DD Arena Fixes] Settlement map for the battle has no garrison; the defender spawns at the map edge instead.");
                return true;
            }
            BaseSiege.ActiveGarrison = garrison;
            __result = garrison;
            return false;
        }
    }

    /// <summary>Better Settlement Defense only handles hostile settlement maps; base siege arenas count as one.</summary>
    [HarmonyPatch]
    public static class Patch_BetterSettlementDefense_Scope
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("TechnoFox.RaidRoles.RaidRoleScope"), "IsEnemySettlementMap");

        public static void Postfix(Map map, ref bool __result)
        {
            if (!__result && BaseSiege.IsBaseSiegeMap(map))
                __result = true;
        }
    }
}
