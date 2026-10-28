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
    /// A faction another mod still uses waits for a later day; mod data is only changed after vanilla removed it.
    /// </summary>
    [HarmonyPatch(typeof(FactionManager), nameof(FactionManager.FactionManagerTick))]
    public static class FactionCleanup
    {
        private const int CheckInterval = GenDate.TicksPerDay;
        private const int CheckOffset = 30011;

        private static readonly MethodInfo RemoveMethod = AccessTools.Method(typeof(FactionManager), "Remove", new[] { typeof(Faction) });
        private static readonly MethodInfo CanBeRemovedMethod = AccessTools.Method(typeof(FactionManager), "FactionCanBeRemoved", new[] { typeof(Faction) });
        private static readonly Assembly VanillaAssembly = typeof(Faction).Assembly;
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
                // Read only up to here: mod data is changed only once vanilla has actually removed the faction.
                var refs = new ModReferences(f);
                refs.Scan();
                if (refs.Blockers.Count > 0)
                {
                    blocked.Add((f, "mod data still uses it: " + string.Join(", ", refs.Blockers.Take(3))));
                    continue;
                }
                string name = f.Name;
                int id = f.loadID;
                List<Pawn> modHeldPawns = ModHeldPawns();
                List<Ideo> ideos = f.ideos?.AllIdeos.ToList() ?? new List<Ideo>();
                f.temporary = true;
                try
                {
                    RemoveMethod.Invoke(Find.FactionManager, new object[] { f });
                }
                catch (Exception e)
                {
                    if (Find.FactionManager.AllFactionsListForReading.Contains(f))
                    {
                        f.temporary = false;
                        blocked.Add((f, "vanilla Remove failed"));
                        Log.Warning($"[DD Arena Fixes] Vanilla could not remove {name} (Faction_{id}); nothing else was changed: {e.InnerException ?? e}");
                        continue;
                    }
                    // Removed, but something hooked to the removal threw; the leftovers below still need clearing.
                    Log.Warning($"[DD Arena Fixes] Error while removing {name} (Faction_{id}): {e.InnerException ?? e}");
                }
                if (Find.FactionManager.AllFactionsListForReading.Contains(f))
                {
                    f.temporary = false;
                    blocked.Add((f, "vanilla Remove refused"));
                    Log.Warning($"[DD Arena Fixes] Vanilla refused to remove {name} (Faction_{id}).");
                    continue;
                }
                refs.ApplyRemovals();
                (EventsAffectingFaction?.GetValue(Find.HistoryEventsManager) as IDictionary)?.Remove(f);
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
            }
            return null;
        }

        /// <summary>
        /// Finds where mod data refers to a faction, without changing anything. Membership entries (a Faction in a list or
        /// set, a dictionary keyed by it, a known per-faction record in a list) can be dropped once the faction is gone;
        /// any other reference, such as a Faction field in a mod's own object, means the mod still uses the faction, and
        /// the removal waits (nulling it would leave that mod with a record it never expects).
        /// </summary>
        private class ModReferences
        {
            private const int MaxDepth = 6;
            private const int MaxObjects = 200000;

            // List items that only track a faction's activity and are dropped as a whole, by type and faction field.
            private static readonly Dictionary<string, string> RemovableItems = new Dictionary<string, string>
            {
                // Vanilla Expanded Framework raid tracking; it drops these itself once their pawns are gone.
                { "VEF.Storyteller.RaidGroup", "faction" },
            };

            public readonly List<string> Blockers = new List<string>();
            private readonly List<Action> removals = new List<Action>();
            private readonly Faction target;
            private readonly HashSet<object> visited = new HashSet<object>(ReferenceComparer.Instance);

            public ModReferences(Faction target)
            {
                this.target = target;
            }

            public void Scan()
            {
                foreach (GameComponent comp in Current.Game.components)
                    Walk(comp, comp.GetType().Name, 0);
                foreach (WorldComponent comp in Find.World.components)
                    Walk(comp, comp.GetType().Name, 0);
                foreach (Map map in Find.Maps)
                {
                    foreach (MapComponent comp in map.components)
                        Walk(comp, comp.GetType().Name, 0);
                }
                foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
                    Walk(wo, "world object " + wo.Label, 0);
                foreach (Quest q in Find.QuestManager.QuestsListForReading)
                {
                    if (q.State != QuestState.Ongoing && q.State != QuestState.NotYetAccepted)
                        continue;
                    foreach (QuestPart part in q.PartsListForReading)
                        Walk(part, "quest " + q.name, 0);
                }
                foreach (FieldInfo field in ModStaticFactionFields())
                {
                    object value;
                    try { value = field.GetValue(null); }
                    catch { continue; }
                    Value(value, $"{field.DeclaringType.Name}.{field.Name}", 0);
                }
            }

            public void ApplyRemovals()
            {
                foreach (Action removal in removals)
                {
                    try
                    {
                        removal();
                    }
                    catch (Exception e)
                    {
                        Log.Warning("[DD Arena Fixes] Could not drop a removed faction from mod data: " + e.Message);
                    }
                }
            }

            /// <summary>Fields of an object from a mod assembly; vanilla objects are left to vanilla's own removal.</summary>
            private void Walk(object obj, string path, int depth)
            {
                if (obj == null || depth > MaxDepth || IsVanilla(obj.GetType()) || !IsModType(obj.GetType()) || !visited.Add(obj))
                    return;
                if (visited.Count > MaxObjects)
                {
                    if (!Blockers.Contains("mod data too large to check"))
                        Blockers.Add("mod data too large to check");
                    return;
                }
                foreach (FieldInfo field in AllFields(obj.GetType()))
                {
                    if (field.IsLiteral)
                        continue;
                    Value(field.GetValue(obj), path + "." + field.Name, depth);
                }
            }

            private void Value(object value, string path, int depth)
            {
                switch (value)
                {
                    case null:
                    case string _:
                        return;
                    case Faction faction:
                        if (faction == target)
                            Blockers.Add(path);
                        return;
                    case List<Faction> factions:
                        if (factions.Contains(target))
                            removals.Add(() => factions.RemoveAll(x => x == target));
                        return;
                    case HashSet<Faction> set:
                        if (set.Contains(target))
                            removals.Add(() => set.Remove(target));
                        return;
                    case IDictionary dict:
                        foreach (DictionaryEntry entry in dict)
                        {
                            if (entry.Key == target)
                            {
                                removals.Add(() => dict.Remove(target));
                                continue;
                            }
                            Value(entry.Key, path + "{key}", depth + 1);
                            Value(entry.Value, $"{path}[{entry.Key}]", depth + 1);
                        }
                        return;
                    case IList list:
                        for (int i = 0; i < list.Count; i++)
                        {
                            object item = list[i];
                            if (IsRemovableItem(item))
                            {
                                removals.Add(() => list.Remove(item));
                                continue;
                            }
                            Value(item, $"{path}[{i}]", depth + 1);
                        }
                        return;
                    case IEnumerable items:
                        int n = 0;
                        foreach (object item in items)
                            Value(item, $"{path}[{n++}]", depth + 1);
                        return;
                    default:
                        Walk(value, path, depth + 1);
                        return;
                }
            }

            private bool IsRemovableItem(object item)
            {
                if (item == null || !RemovableItems.TryGetValue(item.GetType().FullName, out string fieldName))
                    return false;
                FieldInfo field = AccessTools.Field(item.GetType(), fieldName);
                return field != null && field.GetValue(item) == target;
            }
        }

        private class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private static HashSet<Assembly> modAssemblies;

        private static bool IsModType(Type t)
        {
            if (modAssemblies == null)
                modAssemblies = new HashSet<Assembly>(LoadedModManager.RunningModsListForReading.SelectMany(m => m.assemblies.loadedAssemblies));
            return !t.IsPrimitive && !t.IsEnum && modAssemblies.Contains(t.Assembly);
        }

        /// <summary>Static fields of mod types whose type mentions Faction (for example a raid cooldown mod's dictionary).</summary>
        private static List<FieldInfo> ModStaticFactionFields()
        {
            if (modStaticFactionFields != null)
                return modStaticFactionFields;
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
            return modStaticFactionFields;
        }

        private static bool MentionsFaction(Type t) =>
            t == typeof(Faction) || (t.IsGenericType && t.GetGenericArguments().Any(MentionsFaction));

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

        private static bool IsVanilla(Type t) => t.Assembly == VanillaAssembly;

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
