using System.Collections.Generic;
using System.Linq;
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
    /// see them. With the option on, living fighters leave the map like vanilla raiders do and become world pawns; downed
    /// humanlikes of the losing side are kidnapped by the winner instead (vanilla tracker: recruited over time).
    /// </summary>
    public static class ArenaSurvivors
    {
        public static bool Enabled = true;

        private static readonly AccessTools.FieldRef<object, List<Pawn>> Lhs =
            Arena.ArenaType == null ? null : AccessTools.FieldRefAccess<List<Pawn>>(Arena.ArenaType, "lhs");
        private static readonly AccessTools.FieldRef<object, List<Pawn>> Rhs =
            Arena.ArenaType == null ? null : AccessTools.FieldRefAccess<List<Pawn>>(Arena.ArenaType, "rhs");

        // The outcome is read once per battle end: pawns leaving the map during the cleanup loop would change it.
        private static object outcomeArena;
        private static int outcomeTick = -1;
        private static List<Pawn> losers;
        private static Faction winner;

        /// <summary>Drop-in for DD's Thing.Destroy(mode) call on each fighter at the end of a battle.</summary>
        public static void DestroyOrRelease(Thing thing, DestroyMode mode, object arena)
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
                    Faction captor = pawn.Downed && pawn.RaceProps.Humanlike ? Captor(arena, pawn) : null;
                    if (captor != null)
                        captor.kidnapped.Kidnap(pawn, null);
                    else
                        pawn.ExitMap(false, Rot4.Invalid);
                    return;
                }
            }
            thing.Destroy(mode);
        }

        /// <summary>The winning faction if the pawn fought on the losing side, otherwise null. Mirrors DD's win check.</summary>
        private static Faction Captor(object arena, Pawn pawn)
        {
            if (arena == null || Lhs == null)
                return null;
            int tick = Find.TickManager.TicksGame;
            if (outcomeArena != arena || outcomeTick != tick)
            {
                outcomeArena = arena;
                outcomeTick = tick;
                bool attackersDown = !Lhs(arena).Any(Standing);
                bool defendersDown = !Rhs(arena).Any(Standing);
                if (attackersDown == defendersDown)
                {
                    losers = null;
                    winner = null;
                }
                else
                {
                    losers = attackersDown ? Lhs(arena) : Rhs(arena);
                    winner = attackersDown ? Arena.DefenderFaction(arena) : Arena.AttackerFaction(arena);
                }
            }
            if (winner == null || winner.defeated || pawn.Faction == winner || !losers.Contains(pawn))
                return null;
            return winner;
        }

        private static bool Standing(Pawn pawn) => pawn != null && !pawn.Dead && !pawn.Downed && pawn.Spawned;

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
                    // Pass the battle site along so the replacement can tell winners from losers.
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
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
