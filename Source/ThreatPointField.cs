using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Replaces the simulated conquest threat point slider in Dynamic Diplomacy's settings with a typed number field.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_ThreatPointField
    {
        private const float Min = 1000f;
        private const float Max = 10000f;

        private static string buffer;
        private static float lastDrawn = -10f;
        private const string ControlName = "DDArenaFixes_ThreatPoint";

        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicDiplomacy.NPCDiploSettings"), "DoSettingsWindowContents");

        private static readonly MethodInfo Slider = AccessTools.Method(typeof(Listing_Standard), nameof(Listing_Standard.Slider),
            new[] { typeof(float), typeof(float), typeof(float) });
        private static readonly MethodInfo Field = AccessTools.Method(typeof(Patch_ThreatPointField), nameof(NumberField));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 2; i < codes.Count; i++)
            {
                // Only the 1000-10000 slider is the threat point one; the others are 0-100 or 10-100.
                if (codes[i].Calls(Slider) && codes[i - 2].LoadsConstant(Min) && codes[i - 1].LoadsConstant(Max))
                {
                    codes[i].opcode = System.Reflection.Emit.OpCodes.Call;
                    codes[i].operand = Field;
                    replaced++;
                }
            }
            if (replaced == 0)
                Log.Warning("[DD Arena Fixes] Threat point slider not found in Dynamic Diplomacy settings; keeping the slider.");
            return codes;
        }

        /// <summary>
        /// Drop-in for Listing_Standard.Slider(val, min, max): draws a numeric text field clamped to [min, max].
        /// Out-of-range text is kept while typing and only clamped once the field loses focus;
        /// vanilla TextFieldNumeric clamps per keystroke, so "5" of "5000" snaps to the minimum.
        /// </summary>
        public static float NumberField(Listing_Standard listing, float val, float min, float max)
        {
            int lo = (int)min, hi = (int)max;
            int value = Mathf.Clamp(Mathf.RoundToInt(val), lo, hi);

            // Re-sync the text after the window was closed or the value changed elsewhere.
            if (Time.realtimeSinceStartup - lastDrawn > 1f)
                buffer = null;
            lastDrawn = Time.realtimeSinceStartup;
            if (buffer == null)
                buffer = value.ToString();

            Rect row = listing.GetRect(Text.LineHeight);
            Rect field = row.LeftPartPixels(120f);
            GUI.SetNextControlName(ControlName);
            string text = Widgets.TextField(field, buffer);
            if (text.Length <= hi.ToString().Length && text.All(char.IsDigit))
                buffer = text;

            bool focused = GUI.GetNameOfFocusedControl() == ControlName;
            bool parsed = int.TryParse(buffer, out int typed);
            bool inRange = parsed && typed >= lo && typed <= hi;
            if (inRange)
                value = typed;
            else if (!focused)
            {
                if (parsed)
                    value = Mathf.Clamp(typed, lo, hi);
                buffer = value.ToString();
                inRange = true;
            }

            // Show the allowed range next to the field; red while the typed text is outside it.
            Rect hint = new Rect(field.xMax + 8f, row.y, row.width - field.width - 8f, row.height);
            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = inRange ? Color.gray : ColorLibrary.RedReadable;
            Widgets.Label(hint, $"({lo} - {hi})");
            GUI.color = Color.white;
            Text.Anchor = anchor;
            return value;
        }
    }
}
