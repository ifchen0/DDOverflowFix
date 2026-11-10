using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Dynamic Diplomacy's expansion founds the new settlement on a random tile. Half of the time it is resettled on a
    /// ruin instead, so razed places (and the roads leading to them) come back into use; the ruin is removed once the
    /// settlement stands. Which faction expands is still decided by the nearby settlements.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Expansion_Ruins
    {
        public const float RuinChance = 0.5f;

        private static readonly MethodInfo RandomTile = AccessTools.Method(typeof(TileFinder), nameof(TileFinder.RandomStartingTile));
        private static readonly MethodInfo IsValidTile = AccessTools.Method(typeof(TileFinder), nameof(TileFinder.IsValidTileForNewSettlement));
        private static readonly MethodInfo PickTileMethod = AccessTools.Method(typeof(Patch_Expansion_Ruins), nameof(PickTile));
        private static readonly MethodInfo ValidMethod = AccessTools.Method(typeof(Patch_Expansion_Ruins), nameof(Valid));

        // The ruin picked for the expansion currently running.
        private static DestroyedSettlement chosen;

        public static bool Prepare() => RandomTile != null && IsValidTile != null && TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicDiplomacy.IncidentWorker_NPCExpansion"), "TryExecuteWorker");

        public static void Prefix() => chosen = null;

        public static void Postfix(bool __result)
        {
            DestroyedSettlement ruin = chosen;
            chosen = null;
            if (__result && ruin != null && ruin.Spawned && Find.WorldObjects.SettlementBaseAt(ruin.Tile) != null)
                ruin.Destroy();
        }

        public static Exception Finalizer(Exception __exception)
        {
            chosen = null;
            return __exception;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int tiles = 0, checks = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(RandomTile))
                {
                    code.operand = PickTileMethod;
                    tiles++;
                }
                else if (code.Calls(IsValidTile))
                {
                    code.operand = ValidMethod;
                    checks++;
                }
                yield return code;
            }
            if (tiles != 1 || checks != 1)
                Log.Warning($"[DD Fixes & Tweaks] Expansion tile pick found {tiles}/{checks} times instead of once; ruins may not be resettled.");
        }

        public static PlanetTile PickTile()
        {
            chosen = null;
            if (Rand.Chance(RuinChance))
            {
                List<DestroyedSettlement> ruins = Find.WorldObjects.DestroyedSettlements.Where(CanResettle).ToList();
                if (ruins.Count > 0)
                {
                    chosen = ruins.RandomElement();
                    return chosen.Tile;
                }
            }
            return TileFinder.RandomStartingTile();
        }

        /// <summary>Vanilla's check counts the ruin itself as occupying the tile; the chosen ruin was checked without it.</summary>
        public static bool Valid(PlanetTile tile, StringBuilder reason, bool forGravship) =>
            (chosen != null && chosen.Tile == tile) || TileFinder.IsValidTileForNewSettlement(tile, reason, forGravship);

        /// <summary>Vanilla's new-settlement checks, ignoring the ruin on the tile.</summary>
        private static bool CanResettle(DestroyedSettlement ruin)
        {
            if (ruin == null || !ruin.Spawned || ruin.HasMap || !ruin.Tile.Valid || !(ruin.Tile.Layer is SurfaceLayer))
                return false;
            Tile tile = Find.WorldGrid[ruin.Tile];
            if (!tile.PrimaryBiome.canBuildBase || !tile.PrimaryBiome.implemented || tile.hilliness == Hilliness.Impassable)
                return false;
            if (Find.WorldObjects.AnySettlementBaseAtOrAdjacent(ruin.Tile, out _) || Current.Game.FindMap(ruin.Tile) != null)
                return false;
            return Find.WorldObjects.ObjectsAt(ruin.Tile).All(o => o == ruin);
        }
    }
}
