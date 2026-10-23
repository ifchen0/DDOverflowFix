using System;
using System.Collections.Generic;
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
    /// Event frequency in Dynamic Diplomacy comes from two places: storyteller comps its patch adds to every storyteller
    /// (diplomacy changes 2-4 and conquests 3-5 per 60 days, not shown in its settings), and the "extra chance per 6 hours"
    /// sliders (whole percent, so at least 2.4 events a year). Both become "times per year" settings here, shown together.
    /// </summary>
    public static class Frequency
    {
        public const int DiploChange = 0, Conquest = 1, Expansion = 2, Convert = 3;

        public static readonly string[] IncidentNames = { "NPC_DiploChange", "NPC_Conquest", "NPC_Expansion", "NPC_Convert" };
        private static readonly string[] SliderKeys =
            { "RepExtraIncidentChanceNPCDiploChange", "RepExtraIncidentChanceNPCConquest", "RepExtraIncidentChanceNPCExpansion", "RepExtraIncidentChanceNPCConvert" };

        // Dynamic Diplomacy's storyteller patch: 2-4 diplomacy changes and 3-5 conquests per 60-day cycle.
        public const float DefaultBaseDiplo = 3f, DefaultBaseConquest = 4f;
        public const float MaxPerYear = 24f;
        private const float ChecksPerYear = GenDate.TicksPerYear / 15000f;

        public static float BaseDiplo = DefaultBaseDiplo, BaseConquest = DefaultBaseConquest;
        // -1 = not set yet, taken over from Dynamic Diplomacy's own percent once its settings are loaded.
        public static float[] ExtraPerYear = { -1f, -1f, -1f, -1f };

        public static float PercentToPerYear(int percent) => percent / 100f * ChecksPerYear;

        public static float ChancePerCheck(float perYear) => Mathf.Clamp01(perYear / ChecksPerYear);

        public static int IndexOf(string sliderKey) => Array.IndexOf(SliderKeys, sliderKey);

        public static float BasePerYear(int index) => index == DiploChange ? BaseDiplo : index == Conquest ? BaseConquest : 0f;

        public static void ExposeData(NPCDiploModSettings dd)
        {
            Scribe_Values.Look(ref BaseDiplo, "ddafBaseDiploPerYear", DefaultBaseDiplo);
            Scribe_Values.Look(ref BaseConquest, "ddafBaseConquestPerYear", DefaultBaseConquest);
            Scribe_Values.Look(ref ExtraPerYear[DiploChange], "ddafExtraDiploPerYear", -1f);
            Scribe_Values.Look(ref ExtraPerYear[Conquest], "ddafExtraConquestPerYear", -1f);
            Scribe_Values.Look(ref ExtraPerYear[Expansion], "ddafExtraExpansionPerYear", -1f);
            Scribe_Values.Look(ref ExtraPerYear[Convert], "ddafExtraConvertPerYear", -1f);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                int[] percent = { dd.repExtraDiploChancePer6H, dd.repExtraConquestChancePer6H, dd.repExtraExpansionChancePer6H, dd.repExtraConvertChancePer6H };
                for (int i = 0; i < ExtraPerYear.Length; i++)
                {
                    if (ExtraPerYear[i] < 0f)
                        ExtraPerYear[i] = PercentToPerYear(percent[i]);
                }
            }
        }

        /// <summary>Writes the base counts into the storyteller comps Dynamic Diplomacy added; running games read them live.</summary>
        public static void ApplyBase()
        {
            foreach (StorytellerDef storyteller in DefDatabase<StorytellerDef>.AllDefsListForReading)
            {
                foreach (StorytellerCompProperties comp in storyteller.comps)
                {
                    if (!(comp is StorytellerCompProperties_OnOffCycle cycle) || cycle.incident == null)
                        continue;
                    int index = Array.IndexOf(IncidentNames, cycle.incident.defName);
                    if (index != DiploChange && index != Conquest)
                        continue;
                    float perCycle = BasePerYear(index) * (cycle.onDays + cycle.offDays) / GenDate.DaysPerYear;
                    cycle.numIncidentsRange = new FloatRange(perCycle, perCycle);
                    // Keep the hit list solvable when many events share one cycle.
                    if (perCycle > 0f)
                        cycle.minSpacingDays = Mathf.Min(cycle.minSpacingDays, cycle.onDays * 0.5f / perCycle);
                }
            }
        }

        public static float Slider(Listing_Standard listing, TaggedString label, float value, string tooltip)
        {
            listing.Label(label, -1f, tooltip);
            return Mathf.Round(listing.Slider(value, 0f, MaxPerYear) * 10f) / 10f;
        }

        public static string Format(float perYear) => perYear.ToString("0.#");
    }

    /// <summary>Dynamic Diplomacy's 6-hour roll, using the per-year extra counts instead of its whole-percent sliders.</summary>
    [HarmonyPatch]
    public static class Patch_IncidentManager_Tick
    {
        private const int CheckOffset = 1234;

        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicDiplomacy.IncidentManager"), "WorldComponentTick");

        public static bool Prefix()
        {
            if (Find.TickManager.TicksGame % 15000 != CheckOffset)
                return false;
            NPCDiploSettings.UpdateAllSettings();
            NPCDiploModSettings dd = NPCDiploSettings.Instance.settings;
            bool[] enabled = { dd.repEnableDiplo, dd.repEnableConquest, dd.repEnableExpansion, dd.repAllowConvert };
            for (int i = 0; i < Frequency.IncidentNames.Length; i++)
            {
                // Roll every incident each time so one roll never shifts the others.
                bool hit = Rand.Chance(Frequency.ChancePerCheck(Frequency.ExtraPerYear[i]));
                if (hit && enabled[i])
                    Run(Frequency.IncidentNames[i]);
            }
            return false;
        }

        private static void Run(string defName)
        {
            IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
            if (def == null)
                return;
            try
            {
                // Same parms as Dynamic Diplomacy; a worker finding no target is normal, so no error for that.
                def.Worker.TryExecute(StorytellerUtility.DefaultParmsNow(def.category, Find.World));
            }
            catch (Exception e)
            {
                Log.Error($"[DD Arena Fixes] {defName} failed: {e}");
            }
        }
    }

    /// <summary>
    /// Replaces each "extra chance per 6 hours" label + slider in Dynamic Diplomacy's settings with per-year sliders,
    /// plus the base (storyteller) count and the total for diplomacy changes and conquests.
    /// </summary>
    [HarmonyPatch(typeof(NPCDiploSettings), nameof(NPCDiploSettings.DoSettingsWindowContents))]
    public static class Patch_NPCDiploSettings_FrequencySliders
    {
        private static readonly MethodInfo LabelMethod = AccessTools.Method(typeof(Listing_Standard), nameof(Listing_Standard.Label),
            new[] { typeof(TaggedString), typeof(float), typeof(string) });
        private static readonly MethodInfo SliderMethod = AccessTools.Method(typeof(Listing_Standard), nameof(Listing_Standard.Slider),
            new[] { typeof(float), typeof(float), typeof(float) });
        private static readonly MethodInfo SkipLabelMethod = AccessTools.Method(typeof(Patch_NPCDiploSettings_FrequencySliders), nameof(SkipLabel));
        private static readonly MethodInfo RowsMethod = AccessTools.Method(typeof(Patch_NPCDiploSettings_FrequencySliders), nameof(FrequencyRows));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int pending = -1, replaced = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction ins = codes[i];
                if (ins.opcode == OpCodes.Ldstr && ins.operand is string key && Frequency.IndexOf(key) >= 0)
                    pending = Frequency.IndexOf(key);
                else if (pending >= 0 && ins.Calls(LabelMethod))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = SkipLabelMethod;
                }
                else if (pending >= 0 && ins.Calls(SliderMethod))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = RowsMethod;
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldc_I4, pending));
                    i++;
                    pending = -1;
                    replaced++;
                }
            }
            if (replaced != Frequency.IncidentNames.Length)
                Log.Warning($"[DD Arena Fixes] Found {replaced} of {Frequency.IncidentNames.Length} event chance sliders in Dynamic Diplomacy settings.");
            return codes;
        }

        /// <summary>Drop-in for the percent label; the per-year rows draw their own.</summary>
        public static Rect SkipLabel(Listing_Standard listing, TaggedString label, float maxHeight, string tooltip) => default;

        /// <summary>Drop-in for the percent slider: draws the per-year rows and hands Dynamic Diplomacy its own value back.</summary>
        public static float FrequencyRows(Listing_Standard listing, float value, float min, float max, int index)
        {
            bool hasBase = index == Frequency.DiploChange || index == Frequency.Conquest;
            if (hasBase)
            {
                float before = Frequency.BasePerYear(index);
                float after = Frequency.Slider(listing, "DDAF_BasePerYear".Translate(Frequency.Format(before)), before, "DDAF_BasePerYearDesc".Translate());
                if (after != before)
                {
                    if (index == Frequency.DiploChange)
                        Frequency.BaseDiplo = after;
                    else
                        Frequency.BaseConquest = after;
                    Frequency.ApplyBase();
                }
                listing.Gap(4f);
            }
            float extra = Frequency.ExtraPerYear[index];
            Frequency.ExtraPerYear[index] = Frequency.Slider(listing, "DDAF_ExtraPerYear".Translate(Frequency.Format(extra)), extra, "DDAF_ExtraPerYearDesc".Translate());
            if (hasBase)
            {
                GUI.color = Color.gray;
                listing.Label("DDAF_TotalPerYear".Translate(Frequency.Format(Frequency.BasePerYear(index) + Frequency.ExtraPerYear[index])));
                GUI.color = Color.white;
            }
            return value;
        }
    }
}
