using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DDOverflowFix
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            new Harmony("ifchen0.ddoverflowfix").PatchAll();
        }
    }

    /// <summary>
    /// Replaces the int.Parse(tickCreated + uniqueID) seed in InitArenaMap with an overflow-safe version.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_InitArenaMap
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicDiplomacy.IncidentWorker_NPCConquest"), "InitArenaMap");

        private static readonly MethodInfo IntParse = AccessTools.Method(typeof(int), nameof(int.Parse), new[] { typeof(string) });
        private static readonly MethodInfo SafeParse = AccessTools.Method(typeof(Patch_InitArenaMap), nameof(SafeSeed));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(IntParse))
                {
                    ins.operand = SafeParse;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced == 0)
                Log.Warning("[DD Overflow Fix] int.Parse not found in InitArenaMap; the mod may have been updated and this patch is no longer needed.");
        }

        /// <summary>
        /// Returns the original value when it fits in Int32, otherwise a deterministic folded value.
        /// </summary>
        public static int SafeSeed(string s)
        {
            if (int.TryParse(s, out int v))
                return v;
            if (long.TryParse(s, out long l))
                return (int)(l % int.MaxValue);
            return Gen.HashCombineInt(s.Length, s[0]);
        }
    }
}
