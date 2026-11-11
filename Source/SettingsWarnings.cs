using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DynamicDiplomacy;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Some setting combinations quietly switch other options off. The affected rows in Dynamic Diplomacy's settings
    /// get a short red note after their label, with the reason in the tooltip.
    /// </summary>
    public static class SettingsWarnings
    {
        public const int LowRazeChance = 20;

        private static NPCDiploModSettings DD => NPCDiploSettings.Instance.settings;

        /// <summary>Adds the red note to the label and puts the reason before the original tooltip.</summary>
        public static void Warn(ref TaggedString label, ref string tooltip, TaggedString reason)
        {
            label = label + " " + "DDAF_SettingNoEffect".Translate().Colorize(ColorLibrary.RedReadable);
            tooltip = tooltip.NullOrEmpty() ? reason.Resolve() : reason.Resolve() + "\n\n" + tooltip;
        }

        /// <summary>For options that only matter in battles on a map (simulated conquest).</summary>
        public static void WarnIfNoBattles(ref TaggedString label, ref string tooltip)
        {
            if (!DD.repAllowSimulatedConquest)
                Warn(ref label, ref tooltip, "DDAF_WarnNoSimulatedConquest".Translate());
        }

        public static void WarnIfNoExpansion(ref TaggedString label, ref string tooltip)
        {
            if (!DD.repEnableExpansion)
                Warn(ref label, ref tooltip, "DDAF_WarnNoExpansion".Translate());
        }

        /// <summary>Settlements Dynamic Diplomacy counts against the expansion limit, or -1 outside a game.</summary>
        public static int SettlementCount()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.World == null)
                return -1;
            return Find.WorldObjects.Settlements.Count(s => s.Biome != BiomeDefOf.Orbit && s.Biome != BiomeDefOf.Space);
        }

        public static Rect RazeLabel(Listing_Standard listing, TaggedString label, float maxHeight, string tooltip)
        {
            if (DD.repEnableExpansion && DD.repRazeChance < LowRazeChance)
            {
                label = label + " " + "DDAF_RazeChanceLow".Translate().Colorize(ColorLibrary.RedReadable);
                tooltip = "DDAF_RazeChanceLowDesc".Translate(LowRazeChance);
            }
            return listing.Label(label, maxHeight, tooltip);
        }

        public static Rect LimitLabel(Listing_Standard listing, TaggedString label, float maxHeight, string tooltip)
        {
            int count = SettlementCount();
            if (count >= 0)
            {
                if (DD.repEnableExpansion && count > DD.repMaxExpansionLimit)
                {
                    label = label + " " + "DDAF_ExpansionLimitReached".Translate(count).Colorize(ColorLibrary.RedReadable);
                    tooltip = "DDAF_ExpansionLimitReachedDesc".Translate(count);
                }
                else
                    label = label + " " + "DDAF_ExpansionLimitCurrent".Translate(count).Colorize(Color.gray);
            }
            Rect rect = listing.Label(label, maxHeight, tooltip);
            CleanupButton(listing, count);
            return rect;
        }

        /// <summary>One-off cleanup down to 80% of the limit, shown in a game while the world is above that.</summary>
        private static void CleanupButton(Listing_Standard listing, int count)
        {
            int target = SettlementCleanup.Target(DD.repMaxExpansionLimit);
            if (count < 0 || count <= target)
                return;
            Rect row = listing.GetRect(30f);
            Rect button = row.LeftPart(0.45f);
            if (Widgets.ButtonText(button, "DDAF_SettlementCleanup".Translate(target)))
                SettlementCleanup.Confirm(DD.repMaxExpansionLimit);
            TooltipHandler.TipRegion(button, "DDAF_SettlementCleanupDesc".Translate(target, SettlementCleanup.MinKeep));
            listing.Gap(4f);
        }

        public static void AllianceCheckbox(Listing_Standard listing, string label, ref bool checkOn, string tooltip, float height, float labelPct)
        {
            TaggedString text = label;
            if (checkOn && !DD.repAllowDistanceCalc)
                Warn(ref text, ref tooltip, "DDAF_WarnNoDistanceCalc".Translate());
            listing.CheckboxLabeled(text, ref checkOn, tooltip, height, labelPct);
        }
    }

    /// <summary>Swaps the raze chance, expansion limit and alliance rows of Dynamic Diplomacy's settings for warning versions.</summary>
    [HarmonyPatch(typeof(NPCDiploSettings), nameof(NPCDiploSettings.DoSettingsWindowContents))]
    public static class Patch_NPCDiploSettings_Warnings
    {
        private static readonly MethodInfo LabelMethod = AccessTools.Method(typeof(Listing_Standard), nameof(Listing_Standard.Label),
            new[] { typeof(TaggedString), typeof(float), typeof(string) });
        private static readonly MethodInfo CheckboxMethod = AccessTools.Method(typeof(Listing_Standard), nameof(Listing_Standard.CheckboxLabeled),
            new[] { typeof(string), typeof(bool).MakeByRefType(), typeof(string), typeof(float), typeof(float) });

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var targets = new Dictionary<string, (MethodInfo call, MethodInfo replacement)>
            {
                ["RepRazeChanceExp"] = (LabelMethod, AccessTools.Method(typeof(SettingsWarnings), nameof(SettingsWarnings.RazeLabel))),
                ["RepMaxExpansionLimitExp"] = (LabelMethod, AccessTools.Method(typeof(SettingsWarnings), nameof(SettingsWarnings.LimitLabel))),
                ["RepAllowAllianceExp"] = (CheckboxMethod, AccessTools.Method(typeof(SettingsWarnings), nameof(SettingsWarnings.AllianceCheckbox))),
            };
            string pending = null;
            var found = new HashSet<string>();
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldstr && ins.operand is string key && targets.ContainsKey(key))
                    pending = key;
                else if (pending != null && ins.Calls(targets[pending].call))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = targets[pending].replacement;
                    found.Add(pending);
                    pending = null;
                }
                yield return ins;
            }
            foreach (string key in targets.Keys.Where(k => !found.Contains(k)))
                Log.Warning($"[DD Fixes & Tweaks] {key} row not found in Dynamic Diplomacy settings; no warning for it.");
        }
    }
}
