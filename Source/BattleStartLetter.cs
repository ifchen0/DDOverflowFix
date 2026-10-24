using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// DD's battle start letter says the battle's fate "will soon be decided", but semi-simulation battle sites now wait
    /// 2 days before the armies fight. Swaps the letter text key for one that says the fighting will start soon.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_BattleStartLetter
    {
        private const string OldKey = "DescConquestBattleStart";
        private const string NewKey = "DDAF_ConquestBattleStart";

        public static bool Prepare() => Arena.ConquestType != null;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "BeginArenaFight", "InitArenaMap" })
            {
                MethodInfo method = AccessTools.Method(Arena.ConquestType, name);
                if (method != null)
                    yield return method;
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int replaced = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Is(System.Reflection.Emit.OpCodes.Ldstr, OldKey))
                {
                    code.operand = NewKey;
                    replaced++;
                }
                yield return code;
            }
            if (replaced == 0)
                Log.Warning("[DD Arena Fixes] Battle start letter not found in " + original.Name + "; keeping the original text.");
        }
    }
}
