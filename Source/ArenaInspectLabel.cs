using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// DD's battle site inspect string ends with a hardcoded "Attacker VS Defender", which hides who is attacking.
    /// Replaces that line with a translatable "Attacker attacking Defender".
    /// </summary>
    [HarmonyPatch]
    public static class Patch_ArenaInspectString
    {
        private static readonly Type ArenaType = AccessTools.TypeByName("DynamicDiplomacy.MapParentNPCArena");
        private static readonly FieldInfo AttackerField = AccessTools.Field(ArenaType, "attackerFaction");
        private static readonly FieldInfo DefenderField = AccessTools.Field(ArenaType, "defenderFaction");

        public static bool Prepare() => TargetMethod() != null && AttackerField != null && DefenderField != null;

        public static MethodBase TargetMethod() => AccessTools.Method(ArenaType, "GetInspectString");

        public static void Postfix(object __instance, ref string __result)
        {
            if (__result == null || !(AttackerField.GetValue(__instance) is Faction attacker) || !(DefenderField.GetValue(__instance) is Faction defender))
                return;
            string vanillaLine = attacker.Name + " VS " + defender.Name;
            if (__result.EndsWith(vanillaLine))
                __result = __result.Substring(0, __result.Length - vanillaLine.Length) + "DDAF_ArenaAttacking".Translate(attacker.Name, defender.Name);
            // Battle sites have no faction (ArenaNoFaction), so the base string DD appends its line to can be empty.
            __result = __result.TrimStart('\n');
        }
    }
}
