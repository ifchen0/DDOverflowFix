using System;
using System.Linq;
using DynamicDiplomacy;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Optional: Dynamic Diplomacy's diplomacy changes only ever pair two NPC factions. Every 6 hours, with the configured
    /// chance, one eligible NPC faction makes peace with the player (if hostile) or turns hostile. Candidates follow Dynamic
    /// Diplomacy's own diplomacy settings. Goodwill moves just far enough to cross vanilla's threshold (peace: 0, war: -75)
    /// through vanilla's goodwill path, so relation kind and side effects stay consistent; the ideo surrender branch is skipped.
    /// </summary>
    [HarmonyPatch(typeof(FactionManager), nameof(FactionManager.FactionManagerTick))]
    public static class PlayerDiplomacy
    {
        private const int CheckInterval = 15000;
        private const int CheckOffset = 7321;

        // Stored in Dynamic Diplomacy's own settings file (see PlayerDiplomacySettings).
        public static bool Enabled;
        public static int Chance = 1;

        public static void Postfix()
        {
            if (!Enabled || Find.TickManager.TicksGame % CheckInterval != CheckOffset)
                return;
            if (Rand.Range(0, 100) >= Chance)
                return;
            try
            {
                TryRun(out _);
            }
            catch (Exception e)
            {
                Log.Error("[DD Arena Fixes] Player diplomacy event failed: " + e);
            }
        }

        public static bool TryRun(out string result)
        {
            NPCDiploModSettings dd = NPCDiploSettings.Instance?.settings;
            if (dd == null)
            {
                result = "Dynamic Diplomacy settings not found.";
                return false;
            }
            if (!dd.repEnableDiplo)
            {
                result = "Diplomacy changes are disabled in Dynamic Diplomacy.";
                return false;
            }
            // Dynamic Diplomacy skips diplomacy changes while an alliance is active.
            if (DiplomacyWorldComponent.allianceCooldown > 0)
            {
                result = "An alliance is active.";
                return false;
            }
            Faction player = Faction.OfPlayer;
            if (!Find.FactionManager.AllFactions.Where(f => Eligible(f, player, dd)).TryRandomElement(out Faction faction))
            {
                result = "No eligible faction.";
                return false;
            }
            return Apply(faction, player, out result);
        }

        private static bool Eligible(Faction f, Faction player, NPCDiploModSettings dd)
        {
            // Same base filter as Dynamic Diplomacy's diplomacy change.
            if (f.IsPlayer || f.def.hidden || f.defeated || f.leader == null || f.leader.IsPrisoner || f.leader.Spawned
                || f.def.settlementGenerationWeight <= 0f)
                return false;
            if (!dd.repAllowPerm && f.def.permanentEnemy)
                return false;
            if (dd.repExcludeEmpire && f.def == FactionDefOf.Empire)
                return false;
            bool hostile = f.HostileTo(player);
            // Ideo bloc: factions sharing the primary ideo only make peace, never go to war.
            if (dd.repAllowIdeoBloc && ModsConfig.IdeologyActive && !hostile)
            {
                Ideo ideo = player.ideos?.PrimaryIdeo;
                if (ideo != null && f.ideos != null && f.ideos.Has(ideo))
                    return false;
            }
            return player.CanChangeGoodwillFor(f, GoodwillChange(f, player));
        }

        /// <summary>
        /// Peace: up to 0 (vanilla turns hostile into neutral at >= 0). War: down to -75 (non-hostile turns hostile at <= -75).
        /// A relation already past the threshold gets 0 here and is resynced by the threshold check in Apply.
        /// </summary>
        private static int GoodwillChange(Faction f, Faction player)
        {
            if (f.HostileTo(player))
                return Math.Max(-player.GoodwillWith(f), 0);
            return Math.Min(player.GoodwillToMakeHostile(f), 0);
        }

        private static bool Apply(Faction f, Faction player, out string result)
        {
            bool wasHostile = f.HostileTo(player);
            int before = player.GoodwillWith(f);
            player.TryAffectGoodwillWith(f, GoodwillChange(f, player), canSendMessage: false, canSendHostilityLetter: false);
            // A zero or clamped change does not re-check the thresholds; force the check once.
            if (f.HostileTo(player) == wasHostile)
                player.Notify_GoodwillSituationsChanged(f, false, null, null);
            int after = player.GoodwillWith(f);
            if (f.HostileTo(player) == wasHostile)
            {
                result = $"Relation with {f.Name} did not change (goodwill {before} -> {after}).";
                Log.Warning("[DD Arena Fixes] " + result);
                return false;
            }

            TaggedString label = (wasHostile ? "LabelDCPeace" : "LabelDCWar").Translate();
            TaggedString text = (wasHostile ? "DescDCPeace" : "DescDCWar").Translate(f.Name, player.Name)
                + "\n\n" + "MessageGoodwillChanged".Translate(f.Name, before.ToString(), after.ToString());
            Find.LetterStack.ReceiveLetter(label, text, wasHostile ? LetterDefOf.PositiveEvent : LetterDefOf.NegativeEvent,
                LookTargets.Invalid, f);
            result = $"{(wasHostile ? "Peace" : "War")} with {f.Name} (goodwill {before} -> {after}).";
            return true;
        }
    }
}
