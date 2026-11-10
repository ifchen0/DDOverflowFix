using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Dynamic Diplomacy's "clear ruins" option removes every ruin on each raze, which leaves roads that led to them
    /// ending in empty land. Ruins on a road are kept instead, by priority road end > junction > mid-road: a road end
    /// always stays, a junction stays unless a road-end ruin is nearby, and a mid-road ruin stays only with no other ruin
    /// nearby (checked one ruin at a time, so a mid-road cluster keeps one). Ruins off the road network are cleared.
    /// </summary>
    public static class RuinClearing
    {
        public const float NearbyTiles = 5f;

        public static bool Keep(DestroyedSettlement ruin)
        {
            int roads = RoadLinks(ruin.Tile);
            if (roads == 0)
                return false;
            if (roads == 1)
                return true;
            bool junction = roads > 2;
            List<DestroyedSettlement> ruins = Find.WorldObjects.DestroyedSettlements;
            for (int i = 0; i < ruins.Count; i++)
            {
                DestroyedSettlement other = ruins[i];
                if (other == ruin || !other.Spawned || other.Tile.Layer != ruin.Tile.Layer
                    || Find.WorldGrid.ApproxDistanceInTiles(other.Tile, ruin.Tile) > NearbyTiles)
                    continue;
                if (!junction || RoadLinks(other.Tile) == 1)
                    return false;
            }
            return true;
        }

        private static int RoadLinks(PlanetTile tile) =>
            tile.Valid && Find.WorldGrid[tile] is SurfaceTile surface ? surface.potentialRoads?.Count ?? 0 : 0;
    }
}
