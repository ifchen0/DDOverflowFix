using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    internal static class Arena
    {
        public static readonly Type ArenaType = AccessTools.TypeByName("DynamicDiplomacy.MapParentNPCArena");
        public static readonly Type ConquestType = AccessTools.TypeByName("DynamicDiplomacy.IncidentWorker_NPCConquest");

        public static readonly AccessTools.FieldRef<object, int> TickFightStarted =
            ArenaType == null ? null : AccessTools.FieldRefAccess<int>(ArenaType, "tickFightStarted");
        public static readonly AccessTools.FieldRef<object, bool> IsCombatEnded =
            ArenaType == null ? null : AccessTools.FieldRefAccess<bool>(ArenaType, "isCombatEnded");
        public static readonly AccessTools.FieldRef<object, Faction> AttackerFaction =
            ArenaType == null ? null : AccessTools.FieldRefAccess<Faction>(ArenaType, "attackerFaction");
        public static readonly AccessTools.FieldRef<object, Faction> DefenderFaction =
            ArenaType == null ? null : AccessTools.FieldRefAccess<Faction>(ArenaType, "defenderFaction");
        public static readonly AccessTools.FieldRef<object, Settlement> CombatLoc =
            ArenaType == null ? null : AccessTools.FieldRefAccess<Settlement>(ArenaType, "combatLoc");

        public static readonly MethodInfo OnTimeOut = ArenaType == null ? null : AccessTools.Method(ArenaType, "OnTimeOut");
        public static readonly MethodInfo InitArenaMap = ConquestType == null ? null : AccessTools.Method(ConquestType, "InitArenaMap");
        public static readonly MethodInfo GenerateGroup = ConquestType == null ? null : AccessTools.Method(ConquestType, "GenerateFactionNPCGroup");
        private static readonly FieldInfo AllowSimulatedConquest = ConquestType == null ? null : AccessTools.Field(ConquestType, "allowSimulatedConquest");
        private static readonly FieldInfo UsingSemiSimulation = ConquestType == null ? null : AccessTools.Field(ConquestType, "usingSemiSimulation");
        private static readonly FieldInfo ThreatPoints = ConquestType == null ? null : AccessTools.Field(ConquestType, "simulatedConquestThreatPoint");

        public static bool Available =>
            TickFightStarted != null && IsCombatEnded != null && AttackerFaction != null && DefenderFaction != null && CombatLoc != null
            && OnTimeOut != null && InitArenaMap != null && GenerateGroup != null
            && AllowSimulatedConquest != null && UsingSemiSimulation != null && ThreatPoints != null;

        /// <summary>Follows Dynamic Diplomacy's own "simulated conquest" and "semi-simulation" settings.</summary>
        public static bool SemiSimActive => (bool)AllowSimulatedConquest.GetValue(null) && (bool)UsingSemiSimulation.GetValue(null);

        public static float Points => (float)ThreatPoints.GetValue(null);

        /// <summary>The settlement the battle is for still exists and is still held by the defender.</summary>
        public static bool StillContested(MapParent arena)
        {
            Settlement combatLoc = CombatLoc(arena);
            return combatLoc != null && !combatLoc.Destroyed && Find.WorldObjects.Contains(combatLoc) && combatLoc.Faction == DefenderFaction(arena);
        }
    }

    /// <summary>
    /// Semi-simulation: when nobody entered the battle site before the 2-day timeout, generate the arena map and let
    /// the armies fight (as full simulation does) instead of deciding the winner by auto-calc.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MapParentNPCArena_OnTimeOut
    {
        // Arenas whose InitArenaMap has been called; with Deferred Raid Generation the map appears some seconds later.
        private static readonly HashSet<int> Started = new HashSet<int>();
        // Loading a save creates a new Game; IDs from the previous one must not block the same sites in the loaded save.
        private static Game startedGame;

        public static bool Prepare() => Arena.Available;

        /// <summary>True while this site's arena is generated or waiting in Deferred Raid Generation's queue.</summary>
        public static bool Queued(MapParent arena) => startedGame == Current.Game && Started.Contains(arena.ID);

        public static MethodBase TargetMethod() => Arena.OnTimeOut;

        public static bool Prefix(MapParent __instance)
        {
            if (!Arena.SemiSimActive || __instance.HasMap || Arena.TickFightStarted(__instance) != 0)
                return true;
            Settlement combatLoc = Arena.CombatLoc(__instance);
            if (combatLoc == null)
                return true;
            if (!Arena.StillContested(__instance))
            {
                // Lost or taken by someone else while the site waited: no fight and no result (isCombatEnded is
                // already set, so DD removes the site).
                Log.Message($"[DD Overflow Fix] {combatLoc.Name} is gone or no longer held by the defender; the battle site is dropped.");
                return false;
            }

            // DD's Tick sets isCombatEnded before calling OnTimeOut; the battle goes on.
            Arena.IsCombatEnded(__instance) = false;
            if (startedGame != Current.Game)
            {
                Started.Clear();
                startedGame = Current.Game;
            }
            if (!Started.Add(__instance.ID))
                return false;

            Faction attacker = Arena.AttackerFaction(__instance);
            Faction defender = Arena.DefenderFaction(__instance);
            try
            {
                if (!attacker.HostileTo(defender))
                    Relations.Set(attacker, defender, FactionRelationKind.Hostile);
                object lhs = Arena.GenerateGroup.Invoke(null, new object[] { attacker, Arena.Points });
                object rhs = Arena.GenerateGroup.Invoke(null, new object[] { defender, Arena.Points });
                Log.Message($"[DD Overflow Fix] Nobody joined the battle for {combatLoc.Name}; starting the arena fight.");
                Arena.InitArenaMap.Invoke(null, new object[] { __instance, attacker, defender, combatLoc, lhs, rhs, false, null });
                return false;
            }
            catch (Exception e)
            {
                Log.Error($"[DD Overflow Fix] Could not start the arena fight, falling back to auto-calc: {e}");
                Arena.IsCombatEnded(__instance) = true;
                return true;
            }
        }
    }

    /// <summary>
    /// Semi-simulation arenas have no timeout once the fight started. For a fight with no player pawns on the map,
    /// let auto-calc decide after 2 days as full simulation does.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MapParentNPCArena_FightTimeout
    {
        private const int FightTimeoutTicks = 120000;

        public static bool Prepare() => Arena.Available;

        public static MethodBase TargetMethod() => AccessTools.Method(Arena.ArenaType, "Tick");

        public static bool Prefix(MapParent __instance)
        {
            int ticks = Find.TickManager.TicksGame;
            if (ticks % 60 != 0 || !__instance.HasMap || Arena.IsCombatEnded(__instance))
                return true;
            int started = Arena.TickFightStarted(__instance);
            if (started == 0 || ticks - started <= FightTimeoutTicks || !Arena.SemiSimActive)
                return true;
            if (__instance.Map.mapPawns.PawnsInFaction(Faction.OfPlayer).Any())
                return true;

            Arena.IsCombatEnded(__instance) = true;
            Arena.OnTimeOut.Invoke(__instance, null);
            return false;
        }
    }
}
