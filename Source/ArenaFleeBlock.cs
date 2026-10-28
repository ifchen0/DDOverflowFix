using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace DDOverflowFix
{
    /// <summary>
    /// Both sides of a battle site carry the vanilla auto-flee toil and count only their own losses, so after one side
    /// breaks, the other can still bleed past its own threshold and flee too. Once a faction on a battle site map has
    /// fled, factions hostile to it no longer flee there and hold the field.
    /// </summary>
    [HarmonyPatch(typeof(Trigger_FractionPawnsLost), nameof(Trigger_FractionPawnsLost.ActivateOn))]
    public static class Patch_FractionPawnsLost_ArenaFlee
    {
        public static bool Prefix(Lord lord, ref bool __result)
        {
            Map map = lord?.Map;
            if (lord.faction == null || !BaseSiege.IsArena(map?.Parent))
                return true;

            if (ArenaFleeLog.EnemyFled(map, lord.faction))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Remembers which factions fled on each battle site map. A fled lord is destroyed as soon as its last standing pawn
    /// leaves, while pawns that got back up after being downed can keep the fight going, so the lord itself is no record.
    /// </summary>
    [HarmonyPatch(typeof(Lord), nameof(Lord.GotoToil))]
    public static class ArenaFleeLog
    {
        private static readonly Dictionary<int, List<Faction>> Fled = new Dictionary<int, List<Faction>>();
        private static Game fledGame;

        public static void Postfix(Lord __instance, LordToil newLordToil)
        {
            if (!(newLordToil is LordToil_PanicFlee) || __instance.faction == null)
                return;
            Map map = __instance.Map;
            if (!BaseSiege.IsArena(map?.Parent))
                return;

            Reset();
            if (!Fled.TryGetValue(map.uniqueID, out List<Faction> factions))
                Fled[map.uniqueID] = factions = new List<Faction>();
            if (!factions.Contains(__instance.faction))
                factions.Add(__instance.faction);
        }

        internal static bool EnemyFled(Map map, Faction faction)
        {
            Reset();
            if (Fled.TryGetValue(map.uniqueID, out List<Faction> factions))
            {
                foreach (Faction other in factions)
                    if (other != faction && other.HostileTo(faction))
                        return true;
            }

            // After a reload the record is gone; a hostile lord still fleeing covers that case.
            foreach (Lord other in map.lordManager.lords)
            {
                if (other.CurLordToil is LordToil_PanicFlee && other.faction != null && other.faction.HostileTo(faction))
                    return true;
            }
            return false;
        }

        private static void Reset()
        {
            if (fledGame != Current.Game)
            {
                Fled.Clear();
                fledGame = Current.Game;
            }
        }
    }
}
