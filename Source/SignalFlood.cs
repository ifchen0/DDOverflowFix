using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// LordToilShell.Init places a whole siege camp of blueprints in one frame, and vanilla PlaceBlueprintForBuild sends a
    /// "PlacedBlueprint" signal per blueprint for every quest tag the faction carries (old quests leave theirs behind).
    /// That passes the 3000 signals per frame cap, and SignalManager logs an error that opens the log window in dev
    /// mode. Only the overflow notice is changed to a warning while that Init runs; the signals are still dropped as before.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_LordToilShell_Init_SignalFlood
    {
        internal static int Depth;

        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicDiplomacy.LordToilShell"), "Init");

        public static void Prefix() => Depth++;

        public static void Finalizer() => Depth--;
    }

    [HarmonyPatch(typeof(SignalManager), nameof(SignalManager.SendSignal))]
    public static class Patch_SignalManager_SendSignal_Flood
    {
        public static bool Prepare() => Patch_LordToilShell_Init_SignalFlood.TargetMethod() != null;

        public static bool Prefix(ref int ___signalsThisFrame)
        {
            if (Patch_LordToilShell_Init_SignalFlood.Depth == 0 || ___signalsThisFrame != 3000)
                return true;
            Log.Warning("Reached max signals per frame (3000) while Dynamic Diplomacy placed siege blueprints. Ignoring further signals.");
            ___signalsThisFrame++;
            return false;
        }
    }
}
