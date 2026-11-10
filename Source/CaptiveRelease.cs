using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Battle survivors taken by the winner (ArenaSurvivors) sit in vanilla's kidnapped pawn tracker, whose only way out
    /// is joining the captor (mean 30 days). For NPC pawns held by an NPC faction this adds the other ways out a player's
    /// prisoners have: they are released when the two factions are no longer hostile or the captor is defeated, they
    /// escape now and then (mean 60 days, rolled alongside recruitment), and unwaveringly loyal ones are never recruited.
    /// Pawns whose own faction is gone, and the player's kidnapped colonists, are left to vanilla.
    /// </summary>
    [HarmonyPatch(typeof(KidnappedPawnsTracker), nameof(KidnappedPawnsTracker.KidnappedPawnsTrackerTick))]
    public static class Patch_KidnappedPawns_Release
    {
        // Vanilla's check interval and recruitment mean time.
        private const int CheckInterval = 15051;
        private const float RecruitMTBDays = 30f;
        public const float EscapeMTBDays = 60f;

        private static readonly AccessTools.FieldRef<KidnappedPawnsTracker, List<Pawn>> Pawns =
            AccessTools.FieldRefAccess<KidnappedPawnsTracker, List<Pawn>>("kidnappedPawns");
        private static readonly AccessTools.FieldRef<KidnappedPawnsTracker, Faction> Owner =
            AccessTools.FieldRefAccess<KidnappedPawnsTracker, Faction>("faction");

        /// <summary>Replaces vanilla's check tick for an NPC captor; other ticks and the player's tracker run vanilla.</summary>
        public static bool Prefix(KidnappedPawnsTracker __instance)
        {
            if (!ArenaSurvivors.Enabled || Find.TickManager.TicksGame % CheckInterval != 0)
                return true;
            return !Check(__instance);
        }

        /// <summary>One check of an NPC captor's tracker; false (nothing done) for the player's tracker.</summary>
        public static bool Check(KidnappedPawnsTracker tracker)
        {
            List<Pawn> pawns = Pawns(tracker);
            Faction captor = Owner(tracker);
            if (pawns == null || captor == null || captor.IsPlayer)
                return false;

            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                Pawn pawn = pawns[i];
                if (pawn.DestroyedOrNull())
                {
                    pawns.RemoveAt(i);
                    continue;
                }
                Faction home = pawn.Faction;
                bool npcCaptive = home != null && !home.IsPlayer && !home.defeated;
                if (npcCaptive && Freed(captor, home))
                {
                    // Out of the tracker the pawn is a world pawn of its own faction again.
                    pawns.RemoveAt(i);
                    continue;
                }
                if (npcCaptive && pawn.guest != null && !pawn.guest.Recruitable)
                    continue;
                if (Rand.MTBEventOccurs(RecruitMTBDays, GenDate.TicksPerDay, CheckInterval))
                {
                    pawn.SetFaction(captor);
                    pawns.RemoveAt(i);
                }
            }
            return true;
        }

        private static bool Freed(Faction captor, Faction home) =>
            captor.defeated || !captor.HostileTo(home) || Rand.MTBEventOccurs(EscapeMTBDays, GenDate.TicksPerDay, CheckInterval);
    }
}
