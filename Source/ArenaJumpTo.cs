using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// The world "Jump to location" menu lists maps, quest sites and caravans only, so a battle site shows up there just
    /// while its map exists. Battle sites without a map (waiting for the timeout or a caravan) are added to the menu.
    /// </summary>
    [HarmonyPatch(typeof(WorldGizmoUtility), nameof(WorldGizmoUtility.GetJumpToGizmo))]
    public static class Patch_ArenaJumpTo
    {
        // Set while the jump gizmo builds its menu, so only that FloatMenu gets the extra options.
        private static bool building;

        public static bool Prepare() => Arena.ArenaType != null;

        public static void Postfix(Gizmo __result)
        {
            if (!(__result is Command_Action command) || command.action == null)
                return;
            Action original = command.action;
            command.action = delegate
            {
                building = true;
                try
                {
                    original();
                }
                finally
                {
                    building = false;
                }
            };
        }

        [HarmonyPatch(typeof(FloatMenu), MethodType.Constructor, new[] { typeof(List<FloatMenuOption>) })]
        public static class Patch_FloatMenu
        {
            public static bool Prepare() => Arena.ArenaType != null;

            public static void Prefix(List<FloatMenuOption> options)
            {
                if (!building || options == null)
                    return;
                bool added = false;
                foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects)
                {
                    // Sites with a map are already listed by vanilla through Find.Maps.
                    if (!(worldObject is MapParent arena) || arena.HasMap || !Arena.ArenaType.IsInstanceOfType(arena))
                        continue;
                    options.Add(new FloatMenuOption(arena.LabelCap, delegate
                    {
                        CameraJumper.TryJumpAndSelect(arena);
                    }, arena.ExpandingIcon, arena.ExpandingIconColor));
                    added = true;
                }
                // Drop vanilla's "Nothing to jump to" placeholder once there is something to jump to.
                if (added)
                    options.RemoveAll(option => option.action == null && option.Label == "NothingToJumpTo".Translate());
            }
        }
    }
}
