using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// When a battle with no player pawns ends, Dynamic Diplomacy destroys every fighter with DestroyMode.Vanish. Destroyed
    /// pawns can never be generated again, so mods that bring world pawns back (and vanilla's own world pawn reuse) never
    /// see them. With the option on, living fighters leave the map like vanilla raiders do and become world pawns.
    /// </summary>
    public static class ArenaSurvivors
    {
        public static bool Enabled = true;

        /// <summary>Drop-in for DD's Thing.Destroy(mode) call on each fighter at the end of a battle.</summary>
        public static void DestroyOrRelease(Thing thing, DestroyMode mode)
        {
            if (Enabled && thing is Pawn pawn && !pawn.Dead)
            {
                // Fled pawns that already left the map are world pawns; keep them.
                if (pawn.IsWorldPawn())
                    return;
                // A carried pawn leaves (or is kidnapped) together with its carrier.
                if (pawn.ParentHolder is Pawn_CarryTracker)
                    return;
                if (pawn.Spawned)
                {
                    pawn.ExitMap(false, Rot4.Invalid);
                    return;
                }
            }
            thing.Destroy(mode);
        }

        public static void DrawSetting(Listing_Standard listing)
        {
            listing.Gap(4f);
            listing.CheckboxLabeled("DDAF_KeepSurvivors".Translate(), ref Enabled, "DDAF_KeepSurvivorsDesc".Translate());
        }
    }

    [HarmonyPatch]
    public static class Patch_MapParentNPCArena_Tick_Survivors
    {
        private static readonly MethodInfo Destroy = AccessTools.Method(typeof(Thing), nameof(Thing.Destroy));
        private static readonly MethodInfo Replacement = AccessTools.Method(typeof(ArenaSurvivors), nameof(ArenaSurvivors.DestroyOrRelease));

        public static bool Prepare() => Arena.ArenaType != null;

        public static MethodBase TargetMethod() => AccessTools.Method(Arena.ArenaType, "Tick");

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(Destroy))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = Replacement;
                    replaced++;
                }
                yield return code;
            }
            if (replaced == 0)
                Log.Warning("[DD Arena Fixes] Fighter cleanup not found in the battle site tick; survivors are still removed.");
        }
    }
}
