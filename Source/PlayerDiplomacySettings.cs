using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using DynamicDiplomacy;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Keeps the player diplomacy option in Dynamic Diplomacy's own settings: saved in its settings file and drawn in its
    /// settings window right after the ideo bloc checkbox.
    /// </summary>
    public static class PlayerDiplomacySettings
    {
        /// <summary>
        /// Dynamic Diplomacy reads its settings when the mod is constructed, before Harmony patches exist, so the extra
        /// values are read once more after patching.
        /// </summary>
        public static void Reload()
        {
            NPCDiploSettings dd = NPCDiploSettings.Instance;
            if (dd == null)
                return;
            LoadedModManager.ReadModSettings<NPCDiploModSettings>(dd.Content.FolderName, dd.GetType().Name);
        }
    }

    [HarmonyPatch(typeof(NPCDiploModSettings), nameof(NPCDiploModSettings.ExposeData))]
    public static class Patch_NPCDiploModSettings_ExposeData
    {
        public static void Postfix()
        {
            Scribe_Values.Look(ref PlayerDiplomacy.Enabled, "ddafPlayerDiplomacy", false);
            Scribe_Values.Look(ref PlayerDiplomacy.Chance, "ddafPlayerDiplomacyChance", 1);
        }
    }

    [HarmonyPatch(typeof(NPCDiploSettings), nameof(NPCDiploSettings.DoSettingsWindowContents))]
    public static class Patch_NPCDiploSettings_PlayerDiplomacyOption
    {
        private const float ExtraHeight = 100f;

        private static readonly FieldInfo IdeoBloc = AccessTools.Field(typeof(NPCDiploModSettings), nameof(NPCDiploModSettings.repAllowIdeoBloc));
        private static readonly MethodInfo Drawer = AccessTools.Method(typeof(Patch_NPCDiploSettings_PlayerDiplomacyOption), nameof(CheckboxThenOption));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            bool heightDone = false, optionDone = false, afterIdeoBloc = false;
            foreach (CodeInstruction ins in codes)
            {
                // The scroll view is a fixed inRect.height + 500; make room for the extra rows.
                if (!heightDone && ins.LoadsConstant(500f))
                {
                    ins.operand = 500f + ExtraHeight;
                    heightDone = true;
                }
                else if (!optionDone && ins.LoadsField(IdeoBloc, byAddress: true))
                    afterIdeoBloc = true;
                else if (afterIdeoBloc && !optionDone && ins.operand is MethodInfo m && m.Name == nameof(Listing_Standard.CheckboxLabeled)
                    && m.GetParameters().Length == 5)
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = Drawer;
                    optionDone = true;
                }
            }
            if (!optionDone)
                Log.Warning("[DD Arena Fixes] Ideo bloc checkbox not found in Dynamic Diplomacy settings; the player diplomacy option is not shown.");
            return codes;
        }

        /// <summary>Drop-in for the ideo bloc CheckboxLabeled call: draws it, then the player diplomacy option.</summary>
        public static void CheckboxThenOption(Listing_Standard listing, string label, ref bool checkOn, string tooltip, float height, float labelPct)
        {
            listing.CheckboxLabeled(label, ref checkOn, tooltip, height, labelPct);
            listing.Gap(4f);
            listing.CheckboxLabeled("DDAF_PlayerDiplomacy".Translate(), ref PlayerDiplomacy.Enabled, "DDAF_PlayerDiplomacyDesc".Translate());
            if (PlayerDiplomacy.Enabled)
            {
                listing.Gap(4f);
                listing.Label("DDAF_PlayerDiplomacyChance".Translate(PlayerDiplomacy.Chance));
                PlayerDiplomacy.Chance = Mathf.RoundToInt(listing.Slider(PlayerDiplomacy.Chance, 1f, 100f));
            }
        }
    }
}
