using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Dynamic Diplomacy's expansion picks one nearby NPC settlement at random and gives the new settlement to its owner.
    /// The pick is weighted by the owner's tech level instead, so lower-tech factions expand more often, balancing their
    /// weaker armies. The number of expansions is unchanged.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Expansion_TechWeights
    {
        public static bool Enabled = true;

        // GenCollection.RandomElement has several overloads; DD calls the IEnumerable<T> one.
        private static readonly MethodInfo RandomSettlement = typeof(GenCollection).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == nameof(GenCollection.RandomElement) && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsGenericType
                && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.MakeGenericMethod(typeof(Settlement));

        private static readonly MethodInfo WeightedSettlement = AccessTools.Method(typeof(Patch_Expansion_TechWeights), nameof(Pick));

        public static bool Prepare() => RandomSettlement != null && WeightedSettlement != null && TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicDiplomacy.IncidentWorker_NPCExpansion"), "TryExecuteWorker");

        public static float Weight(TechLevel tech)
        {
            switch (tech)
            {
                case TechLevel.Neolithic: return 1.5f;
                case TechLevel.Medieval: return 1.3f;
                case TechLevel.Spacer: return 0.8f;
                case TechLevel.Ultra:
                case TechLevel.Archotech: return 0.6f;
                default: return 1f;
            }
        }

        public static Settlement Pick(IEnumerable<Settlement> candidates) =>
            Enabled
                ? candidates.RandomElementByWeight(s => Weight(s.Faction?.def.techLevel ?? TechLevel.Undefined))
                : candidates.RandomElement();

        public static void DrawSetting(Listing_Standard listing)
        {
            listing.Gap(4f);
            listing.CheckboxLabeled("DDAF_TechWeightedExpansion".Translate(), ref Enabled, "DDAF_TechWeightedExpansionDesc".Translate());
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(RandomSettlement))
                {
                    code.operand = WeightedSettlement;
                    replaced++;
                }
                yield return code;
            }
            if (replaced != 1)
                Log.Warning($"[DD Arena Fixes] Expansion pick found {replaced} times instead of once; tech level weights may not apply.");
        }
    }
}
