using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// Dynamic Diplomacy rebellions create new factions but only ever mark beaten ones as defeated, so they pile up in the save.
    /// Once a day, removes defeated factions that have an older faction of the same def (Dynamic Diplomacy's own notion of a
    /// clone) through vanilla's temporary-faction removal. The oldest faction of each def is always kept.
    /// </summary>
    [HarmonyPatch(typeof(FactionManager), nameof(FactionManager.FactionManagerTick))]
    public static class FactionCleanup
    {
        private const int CheckInterval = GenDate.TicksPerDay;
        private const int CheckOffset = 30011;

        private static readonly MethodInfo RemoveMethod = AccessTools.Method(typeof(FactionManager), "Remove", new[] { typeof(Faction) });
        private static readonly MethodInfo CanBeRemovedMethod = AccessTools.Method(typeof(FactionManager), "FactionCanBeRemoved", new[] { typeof(Faction) });
        private static readonly Assembly VanillaAssembly = typeof(Faction).Assembly;
        private static readonly Dictionary<Type, List<FieldInfo>> factionFieldCache = new Dictionary<Type, List<FieldInfo>>();
        private static readonly Dictionary<Type, List<FieldInfo>> allFieldCache = new Dictionary<Type, List<FieldInfo>>();

        public static bool Prepare() => RemoveMethod != null && CanBeRemovedMethod != null;

        public static void Postfix()
        {
            if (Find.TickManager.TicksGame % CheckInterval != CheckOffset)
                return;
            try
            {
                RemoveEligible(out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("[DD Arena Fixes] Faction cleanup failed: " + e);
            }
        }

        /// <summary>Defeated factions that have an older faction of the same def.</summary>
        public static List<Faction> Candidates()
        {
            List<Faction> all = Find.FactionManager.AllFactionsListForReading;
            var result = new List<Faction>();
            foreach (Faction f in all)
            {
                if (!f.defeated || f.temporary || f.IsPlayer || f.def.isPlayer || f.def.hidden)
                    continue;
                if (all.Any(o => o != f && o.def == f.def && o.loadID < f.loadID))
                    result.Add(f);
            }
            return result;
        }

        public static void RemoveEligible(out List<Faction> removed, out List<(Faction faction, string reason)> blocked)
        {
            removed = new List<Faction>();
            blocked = new List<(Faction, string)>();
            foreach (Faction f in Candidates())
            {
                string reason = BlockReason(f);
                if (reason != null)
                {
                    blocked.Add((f, reason));
                    continue;
                }
                string name = f.Name;
                int id = f.loadID;
                ScrubModComponents(f);
                f.temporary = true;
                RemoveMethod.Invoke(Find.FactionManager, new object[] { f });
                if (Find.FactionManager.AllFactionsListForReading.Contains(f))
                {
                    f.temporary = false;
                    blocked.Add((f, "vanilla Remove refused"));
                    Log.Warning($"[DD Arena Fixes] Vanilla refused to remove {name} (Faction_{id}).");
                    continue;
                }
                removed.Add(f);
                Log.Message($"[DD Arena Fixes] Removed defeated duplicate faction {name} ({f.def.defName}, Faction_{id}).");
            }
        }

        /// <summary>Returns why the faction cannot be removed yet, or null when it can.</summary>
        private static string BlockReason(Faction f)
        {
            // Vanilla's own check (quests, pawns on maps or in caravans, world objects) only accepts temporary factions.
            f.temporary = true;
            bool vanillaOk;
            try
            {
                vanillaOk = (bool)CanBeRemovedMethod.Invoke(Find.FactionManager, new object[] { f });
            }
            finally
            {
                f.temporary = false;
            }
            if (!vanillaOk)
                return "vanilla check (quest, pawns or world objects)";

            foreach (Pawn p in PawnsFinder.AllMapsWorldAndTemporary_Alive)
            {
                if (p.royalty != null && p.royalty.AllTitlesForReading.Any(t => t.faction == f))
                    return "royal title held by " + p.LabelShort;
            }
            foreach (Map map in Find.Maps)
            {
                if (map.lordManager.lords.Any(l => l.faction == f))
                    return "lord on map " + map;
                foreach (MapComponent comp in map.components)
                {
                    if (!IsVanilla(comp.GetType()) && References(comp, f))
                        return "map component " + comp.GetType().Name;
                }
            }
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (!IsVanilla(wo.GetType()) && References(wo, f))
                    return "world object " + wo.Label;
            }
            foreach (Quest q in Find.QuestManager.QuestsListForReading)
            {
                if (q.State != QuestState.Ongoing && q.State != QuestState.NotYetAccepted)
                    continue;
                foreach (QuestPart part in q.PartsListForReading)
                {
                    if (!IsVanilla(part.GetType()) && References(part, f))
                        return "quest " + q.name;
                }
            }
            return null;
        }

        /// <summary>
        /// Clears references to the faction that mod world/game components keep (World Domination's leader, underdog and
        /// coalition state, fortification owners, logs), so they do not point at a removed faction.
        /// </summary>
        private static void ScrubModComponents(Faction f)
        {
            IEnumerable<object> comps = Find.World.components.Cast<object>().Concat(Current.Game.components.Cast<object>());
            foreach (object comp in comps)
            {
                if (IsVanilla(comp.GetType()))
                    continue;
                foreach (FieldInfo field in AllFields(comp.GetType()))
                {
                    object value = field.GetValue(comp);
                    if (value == f)
                        field.SetValue(comp, null);
                    else if (value is List<Faction> list)
                        list.RemoveAll(x => x == f);
                    else if (value is HashSet<Faction> set)
                        set.Remove(f);
                    else if (value is IDictionary dict)
                    {
                        if (dict.Contains(f))
                            dict.Remove(f);
                        foreach (object v in dict.Values)
                            ClearFactionFields(v, f);
                    }
                    else if (value is IList items)
                        foreach (object v in items)
                            ClearFactionFields(v, f);
                }
            }
        }

        private static void ClearFactionFields(object obj, Faction f)
        {
            if (obj == null || obj.GetType().IsPrimitive || obj is string || IsVanilla(obj.GetType()))
                return;
            foreach (FieldInfo field in FactionFields(obj.GetType()))
            {
                if (field.GetValue(obj) == f)
                    field.SetValue(obj, null);
            }
        }

        private static bool References(object obj, Faction f)
        {
            foreach (FieldInfo field in FactionFields(obj.GetType()))
            {
                if (field.GetValue(obj) == f)
                    return true;
            }
            return false;
        }

        private static bool IsVanilla(Type t) => t.Assembly == VanillaAssembly;

        /// <summary>Instance fields of type Faction declared by mod types in the hierarchy.</summary>
        private static List<FieldInfo> FactionFields(Type type)
        {
            if (!factionFieldCache.TryGetValue(type, out List<FieldInfo> fields))
            {
                fields = AllFields(type).Where(fi => fi.FieldType == typeof(Faction)).ToList();
                factionFieldCache[type] = fields;
            }
            return fields;
        }

        private static List<FieldInfo> AllFields(Type type)
        {
            if (!allFieldCache.TryGetValue(type, out List<FieldInfo> fields))
            {
                fields = new List<FieldInfo>();
                for (Type t = type; t != null && !IsVanilla(t) && t != typeof(object); t = t.BaseType)
                    fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                allFieldCache[type] = fields;
            }
            return fields;
        }

        [DebugAction("Dynamic Diplomacy Fix", "Remove defeated duplicate factions now", allowedGameStates = AllowedGameStates.Playing)]
        private static void RemoveNow()
        {
            RemoveEligible(out List<Faction> removed, out List<(Faction faction, string reason)> blocked);
            foreach ((Faction faction, string reason) in blocked)
                Log.Message($"[DD Arena Fixes] Kept {faction.Name} (Faction_{faction.loadID}) for now: {reason}.");
            Messages.Message($"Removed {removed.Count} defeated duplicate faction(s), {blocked.Count} kept for now (see log).", MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
