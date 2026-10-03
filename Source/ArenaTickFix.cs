using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// MapParentNPCArena uses tickFightStarted == 0 as "not started", so a fight started at game tick 0 never runs its end check.
    /// Bumps such fights to tick 1, including arenas already saved in that state.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MapParentNPCArena_Tick
    {
        private static readonly System.Type ArenaType = AccessTools.TypeByName("DynamicDiplomacy.MapParentNPCArena");
        private static readonly AccessTools.FieldRef<object, int> TickFightStarted =
            ArenaType == null ? null : AccessTools.FieldRefAccess<int>(ArenaType, "tickFightStarted");
        private static readonly AccessTools.FieldRef<object, bool> IsCombatEnded =
            ArenaType == null ? null : AccessTools.FieldRefAccess<bool>(ArenaType, "isCombatEnded");
        private static readonly AccessTools.FieldRef<object, List<Pawn>> Lhs =
            ArenaType == null ? null : AccessTools.FieldRefAccess<List<Pawn>>(ArenaType, "lhs");

        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => ArenaType == null ? null : AccessTools.Method(ArenaType, "Tick");

        public static void Prefix(object __instance)
        {
            if (TickFightStarted(__instance) != 0 || IsCombatEnded(__instance))
                return;
            List<Pawn> lhs = Lhs(__instance);
            if (lhs != null && lhs.Count > 0)
                TickFightStarted(__instance) = 1;
        }
    }
}
