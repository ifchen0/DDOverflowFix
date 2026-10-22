using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Dynamic Diplomacy replaces conquered and rebelling settlements by taking the old one off the world with
    /// WorldObjectsHolder.Remove instead of Destroy, so it is never marked destroyed and world object comps never get
    /// PostDestroy. Mods that clean up after a settlement in PostDestroy (World Domination's turrets, advanced buildings and
    /// build projects) then leave everything behind, and quests targeting it never get the Destroyed signal.
    /// Every Remove call in DD is routed through a helper that destroys settlements properly; other objects are still removed.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_DD_RemoveSettlement
    {
        private static readonly Assembly DdAssembly = AccessTools.TypeByName("DynamicDiplomacy.IncidentWorker_NPCConquest")?.Assembly;
        private static readonly MethodInfo RemoveMethod = AccessTools.Method(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Remove));
        private static readonly MethodInfo RemoveOrDestroyMethod = AccessTools.Method(typeof(Patch_DD_RemoveSettlement), nameof(RemoveOrDestroy));

        public static bool Prepare() => DdAssembly != null && RemoveMethod != null;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (System.Type type in AccessTools.GetTypesFromAssembly(DdAssembly))
            {
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                {
                    if (!method.IsAbstract && !method.ContainsGenericParameters && CallsRemove(method))
                        yield return method;
                }
            }
        }

        private static bool CallsRemove(MethodBase method)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(method).Any(op => op.Value is MethodInfo m && m == RemoveMethod);
            }
            catch
            {
                return false;
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if (ins.Calls(RemoveMethod))
                {
                    ins.opcode = System.Reflection.Emit.OpCodes.Call;
                    ins.operand = RemoveOrDestroyMethod;
                }
                yield return ins;
            }
        }

        public static void RemoveOrDestroy(WorldObjectsHolder holder, WorldObject o)
        {
            if (o is Settlement && !o.Destroyed && o.Spawned)
            {
                o.Destroy();
                if (Prefs.DevMode)
                    Log.Message($"[DD Arena Fixes] Destroyed replaced settlement {o.Label} instead of removing it.");
                return;
            }
            holder.Remove(o);
        }
    }
}
