using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// A semi-simulation battle site has no map until a player caravan stands on its tile, so the vanilla "Enter" option
    /// only shows up after arriving and waiting for map generation. This arrival action can be picked before the map
    /// exists: on arrival it generates the arena the same way DD's Tick does and moves the caravan in.
    /// </summary>
    public class CaravanArrivalAction_EnterArena : CaravanArrivalAction
    {
        private MapParent arena;

        public override string Label => "EnterMap".Translate(arena.Label);

        public override string ReportString => "CaravanEntering".Translate(arena.Label);

        public CaravanArrivalAction_EnterArena()
        {
        }

        public CaravanArrivalAction_EnterArena(MapParent arena)
        {
            this.arena = arena;
        }

        public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
        {
            FloatMenuAcceptanceReport report = base.StillValid(caravan, destinationTile);
            if (!report)
                return report;
            if (arena != null && arena.Tile != destinationTile)
                return false;
            return arena != null && arena.Spawned && (arena.HasMap || CanGenerate(arena));
        }

        public override void Arrived(Caravan caravan)
        {
            if (!arena.HasMap && CanGenerate(arena))
            {
                Faction attacker = Arena.AttackerFaction(arena);
                Faction defender = Arena.DefenderFaction(arena);
                try
                {
                    object lhs = Arena.GenerateGroup.Invoke(null, new object[] { attacker, Arena.Points });
                    object rhs = Arena.GenerateGroup.Invoke(null, new object[] { defender, Arena.Points });
                    Arena.InitArenaMap.Invoke(null, new object[] { arena, attacker, defender, Arena.CombatLoc(arena), lhs, rhs, true, null });
                }
                catch (Exception e)
                {
                    Log.Error($"[DD Overflow Fix] Could not generate the battle site for the arriving caravan: {e}");
                }
            }
            // Same as vanilla Enter; if the map is still missing the caravan waits on the tile as after a plain move.
            if (arena.HasMap)
                new CaravanArrivalAction_Enter(arena).Arrived(caravan);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref arena, "arena");
        }

        /// <summary>A battle that DD would still start for a caravan on its tile (see MapParentNPCArena.Tick).</summary>
        public static bool CanGenerate(MapParent arena) =>
            Arena.SemiSimActive && !arena.HasMap && !Arena.IsCombatEnded(arena) && Arena.TickFightStarted(arena) == 0
            && Arena.CombatLoc(arena) != null && !Patch_MapParentNPCArena_OnTimeOut.Queued(arena);

        public static IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan, MapParent arena) =>
            CaravanArrivalActionUtility.GetFloatMenuOptions(() => CanGenerate(arena),
                () => new CaravanArrivalAction_EnterArena(arena), "EnterMap".Translate(arena.Label), caravan, arena.Tile, arena);
    }

    /// <summary>Offers the enter option on battle sites without a map; once the map exists vanilla's option takes over.</summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.GetFloatMenuOptions))]
    public static class Patch_ArenaCaravanFloatMenu
    {
        public static bool Prepare() => Arena.Available;

        public static IEnumerable<FloatMenuOption> Postfix(IEnumerable<FloatMenuOption> options, MapParent __instance, Caravan caravan)
        {
            foreach (FloatMenuOption option in options)
                yield return option;
            if (__instance.HasMap || !Arena.ArenaType.IsInstanceOfType(__instance))
                yield break;
            foreach (FloatMenuOption option in CaravanArrivalAction_EnterArena.GetFloatMenuOptions(caravan, __instance))
                yield return option;
        }
    }
}
