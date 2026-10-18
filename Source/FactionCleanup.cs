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
        private static readonly FieldInfo EventsAffectingFaction = AccessTools.Field(typeof(HistoryEventsManager), "eventsAffectingFaction");
        private static readonly AccessTools.FieldRef<IdeoManager, List<Ideo>> IdeoToRemove =
            AccessTools.FieldRefAccess<IdeoManager, List<Ideo>>("toRemove");
        private static List<FieldInfo> modStaticFactionFields;

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
                List<Pawn> modHeldPawns = ModHeldPawns();
                List<Ideo> ideos = f.ideos?.AllIdeos.ToList() ?? new List<Ideo>();
                ScrubModComponents(f);
                ScrubModStatics(f);
                (EventsAffectingFaction?.GetValue(Find.HistoryEventsManager) as IDictionary)?.Remove(f);
                f.temporary = true;
                RemoveMethod.Invoke(Find.FactionManager, new object[] { f });
                if (Find.FactionManager.AllFactionsListForReading.Contains(f))
                {
                    f.temporary = false;
                    blocked.Add((f, "vanilla Remove refused"));
                    Log.Warning($"[DD Arena Fixes] Vanilla refused to remove {name} (Faction_{id}).");
                    continue;
                }
                // Vanilla only clears the faction of pawns it can find; mods may keep pawns inside their own world objects.
                foreach (Pawn p in modHeldPawns)
                {
                    if (p.Faction == f)
                        p.SetFaction(null);
                }
                KeepUsedIdeos(ideos, modHeldPawns);
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

        /// <summary>
        /// Clears static faction collections kept by mods (for example a raid cooldown mod's Dictionary&lt;Faction, ...&gt;
        /// that its game component saves).
        /// </summary>
        private static void ScrubModStatics(Faction f)
        {
            if (modStaticFactionFields == null)
            {
                modStaticFactionFields = new List<FieldInfo>();
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    foreach (Assembly asm in mod.assemblies.loadedAssemblies)
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                        foreach (Type t in types)
                        {
                            if (t.ContainsGenericParameters)
                                continue;
                            foreach (FieldInfo fi in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            {
                                if (!fi.IsLiteral && MentionsFaction(fi.FieldType))
                                    modStaticFactionFields.Add(fi);
                            }
                        }
                    }
                }
            }
            foreach (FieldInfo field in modStaticFactionFields)
            {
                object value;
                try { value = field.GetValue(null); }
                catch { continue; }
                if (value == f && !field.IsInitOnly)
                    field.SetValue(null, null);
                else if (value is List<Faction> list)
                    list.RemoveAll(x => x == f);
                else if (value is HashSet<Faction> set)
                    set.Remove(f);
                else if (value is IDictionary dict && dict.Contains(f))
                    dict.Remove(f);
            }
        }

        private static bool MentionsFaction(Type t) =>
            t == typeof(Faction) || (t.IsGenericType && t.GetGenericArguments().Contains(typeof(Faction)));

        /// <summary>Pawns stored inside mod world objects (for example World Domination outpost occupants and prisoners).</summary>
        private static List<Pawn> ModHeldPawns()
        {
            var pawns = new List<Pawn>();
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (IsVanilla(wo.GetType()))
                    continue;
                foreach (FieldInfo field in AllFields(wo.GetType()))
                {
                    object value = field.GetValue(wo);
                    if (value is Pawn p)
                        pawns.Add(p);
                    else if (value is IEnumerable items && !(value is string))
                        foreach (object item in items)
                            if (item is Pawn ip)
                                pawns.Add(ip);
                }
            }
            return pawns;
        }

        /// <summary>
        /// Vanilla queues the faction's ideos for removal when no map pawn follows them, even if world pawns or
        /// mod-held pawns still do; keep those so the pawns do not lose their ideoligion on the next load.
        /// </summary>
        private static void KeepUsedIdeos(List<Ideo> ideos, List<Pawn> modHeldPawns)
        {
            List<Ideo> queue = IdeoToRemove?.Invoke(Find.IdeoManager);
            if (queue == null || ideos.Count == 0)
                return;
            foreach (Ideo ideo in ideos)
            {
                if (!queue.Contains(ideo))
                    continue;
                if (PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.Concat(modHeldPawns).Any(p => p.ideo?.Ideo == ideo))
                    queue.Remove(ideo);
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
