using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// DescConquestRaze reads "{1} has conquered and razed one of {0} settlement!", and DD's other raze letters pass
    /// (razed faction, conqueror). The arena's OnAttackerWinRaze passes (attacker, defender), so the letter names the
    /// razed defender as the conqueror. Swap the two arguments of that one Translate call.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MapParentNPCArena_RazeLetter
    {
        private static readonly MethodInfo Translate2 = AccessTools.Method(typeof(TranslatorFormattedStringExtensions),
            nameof(TranslatorFormattedStringExtensions.Translate), new[] { typeof(string), typeof(NamedArgument), typeof(NamedArgument) });

        private static readonly MethodInfo Swapped = AccessTools.Method(typeof(Patch_MapParentNPCArena_RazeLetter), nameof(TranslateSwapped));

        public static bool Prepare() => Arena.ArenaType != null && TargetMethod() != null && Translate2 != null;

        public static MethodBase TargetMethod() => AccessTools.Method(Arena.ArenaType, "OnAttackerWinRaze");

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            bool afterKey = false;
            int replaced = 0;
            foreach (var ins in instructions)
            {
                if (ins.LoadsConstant("DescConquestRaze"))
                    afterKey = true;
                else if (afterKey && ins.Calls(Translate2))
                {
                    ins.operand = Swapped;
                    afterKey = false;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced == 0)
                Log.Warning("[DD Fixes & Tweaks] Raze letter call not found in OnAttackerWinRaze; the mod may have been updated and this patch is no longer needed.");
        }

        public static TaggedString TranslateSwapped(string key, NamedArgument attacker, NamedArgument defender) =>
            key.Translate(defender, attacker);
    }
}
