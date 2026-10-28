using System.IO;
using System.Reflection;
using DynamicDiplomacy;
using HarmonyLib;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Most fixes here only matter with simulated conquest, semi-simulation and distance calculation on, all off in
    /// Dynamic Diplomacy's defaults. A fresh install (no Dynamic Diplomacy settings file yet) starts from the setup this
    /// mod is played and tested with; existing settings files are left alone.
    /// </summary>
    public static class DefaultSettings
    {
        private static readonly MethodInfo SettingsFilename = AccessTools.Method(typeof(LoadedModManager), "GetSettingsFilename");

        public static bool IsFreshInstall(Mod dd) =>
            !File.Exists((string)SettingsFilename.Invoke(null, new object[] { dd.Content.FolderName, dd.GetType().Name }));

        public static void Apply(NPCDiploModSettings s)
        {
            s.repEnableExpansion = true;
            s.repAllowPerm = false;
            s.repAllowDistanceCalc = true;
            s.repAllowAlliance = true;
            s.repAllowConvert = false;
            s.repAllowCloneFaction = true;
            s.repAllowRazeClear = false;
            s.repExcludeEmpire = true;
            s.repAllowSimulatedConquest = true;
            s.repUsingSemiSimulation = true;
            s.repExtraDiploChancePer6H = 1;
            s.repExtraConquestChancePer6H = 1;
            s.repExtraExpansionChancePer6H = 1;

            // Without a settings file the per-year values are never taken over from the percents above.
            Frequency.ExtraPerYear[Frequency.DiploChange] = Frequency.PercentToPerYear(s.repExtraDiploChancePer6H);
            Frequency.ExtraPerYear[Frequency.Conquest] = Frequency.PercentToPerYear(s.repExtraConquestChancePer6H);
            Frequency.ExtraPerYear[Frequency.Expansion] = Frequency.PercentToPerYear(s.repExtraExpansionChancePer6H);
            Frequency.ExtraPerYear[Frequency.Convert] = Frequency.PercentToPerYear(s.repExtraConvertChancePer6H);
            PlayerDiplomacy.PerYear = Frequency.PercentToPerYear(1);
        }
    }
}
