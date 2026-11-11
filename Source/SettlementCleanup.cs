using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    /// <summary>
    /// A world above Dynamic Diplomacy's expansion limit (often straight from world generation) never expands again and
    /// only shrinks through razes. This one-off cleanup brings it down to 80% of the limit: each faction gives up
    /// settlements in proportion to how many it has, keeping at least two. Within a faction, settlements off the road
    /// network go first and road ends last, and every removed settlement becomes a ruin that ruin clearing then judges,
    /// so roads keep their anchors and expansion can resettle them.
    /// </summary>
    public static class SettlementCleanup
    {
        public const float TargetShare = 0.8f;
        public const int MinKeep = 2;

        public static int Target(int limit) => (int)(limit * TargetShare);

        public class Plan
        {
            public int Before;
            public int Target;
            public readonly Dictionary<Faction, int> Counts = new Dictionary<Faction, int>();
            public readonly Dictionary<Faction, List<Settlement>> Remove = new Dictionary<Faction, List<Settlement>>();
            public int Total => Remove.Values.Sum(l => l.Count);
        }

        /// <summary>Settlements that count against the limit, grouped by faction, in removal order.</summary>
        public static Plan MakePlan(int limit)
        {
            var plan = new Plan { Before = SettingsWarnings.SettlementCount(), Target = Target(limit) };
            int excess = plan.Before - plan.Target;
            if (plan.Before < 0 || excess <= 0)
                return plan;

            var candidates = new Dictionary<Faction, List<Settlement>>();
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                if (s.Biome == BiomeDefOf.Orbit || s.Biome == BiomeDefOf.Space || s.Faction == null || s.Faction.IsPlayer || s.Faction.Hidden)
                    continue;
                plan.Counts.TryGetValue(s.Faction, out int n);
                plan.Counts[s.Faction] = n + 1;
                if (CanRemove(s))
                {
                    if (!candidates.TryGetValue(s.Faction, out var list))
                        candidates[s.Faction] = list = new List<Settlement>();
                    list.Add(s);
                }
            }

            // Proportional quotas by largest remainder, capped by what each faction can give; whatever a capped faction
            // cannot give is shared out again among the others.
            var quota = plan.Counts.Keys.ToDictionary(f => f, f => 0);
            int Room(Faction f) => System.Math.Min(candidates.TryGetValue(f, out var c) ? c.Count : 0, plan.Counts[f] - MinKeep) - quota[f];
            int left = excess;
            while (left > 0)
            {
                List<Faction> open = quota.Keys.Where(f => Room(f) > 0).ToList();
                if (open.Count == 0)
                    break;
                float weight = open.Sum(f => plan.Counts[f]);
                var shares = open.Select(f => (f, exact: left * plan.Counts[f] / weight)).ToList();
                int given = 0;
                foreach (var (f, exact) in shares)
                {
                    int add = System.Math.Min((int)exact, Room(f));
                    quota[f] += add;
                    given += add;
                }
                // Remainders one at a time, largest first.
                foreach (var (f, exact) in shares.OrderByDescending(x => x.exact - (int)x.exact))
                {
                    if (given >= left)
                        break;
                    if (Room(f) > 0)
                    {
                        quota[f]++;
                        given++;
                    }
                }
                if (given == 0)
                    break;
                left -= given;
            }

            foreach (var pair in quota.Where(q => q.Value > 0))
            {
                List<Settlement> list = candidates[pair.Key];
                plan.Remove[pair.Key] = list.InRandomOrder().OrderBy(s => Rank(s, list)).Take(pair.Value).ToList();
            }
            return plan;
        }

        /// <summary>Off-road first, then crowded mid-road, lone mid-road, junctions, and road ends last.</summary>
        private static int Rank(Settlement s, List<Settlement> sameFaction)
        {
            int roads = RuinClearing.RoadLinks(s.Tile);
            if (roads == 0)
                return 0;
            if (roads == 1)
                return 4;
            if (roads > 2)
                return 3;
            bool crowded = sameFaction.Any(o => o != s && o.Tile.Layer == s.Tile.Layer
                && Find.WorldGrid.ApproxDistanceInTiles(o.Tile, s.Tile) <= RuinClearing.NearbyTiles);
            return crowded ? 1 : 2;
        }

        /// <summary>Leaves alone settlements the player is involved with right now.</summary>
        public static bool CanRemove(Settlement s)
        {
            if (!s.Spawned || s.Destroyed || s.HasMap || Current.Game.FindMap(s.Tile) != null)
                return false;
            if (s.GetComponent<TradeRequestComp>()?.ActiveRequest == true)
                return false;
            foreach (Caravan c in Find.WorldObjects.Caravans)
            {
                if (c.IsPlayerControlled && (c.Tile == s.Tile || (c.pather.Moving && c.pather.Destination == s.Tile)))
                    return false;
            }
            foreach (Quest q in Find.QuestManager.QuestsListForReading)
            {
                if (q.State == QuestState.Ongoing && q.QuestLookTargets.Any(t => t.WorldObject == s))
                    return false;
            }
            return true;
        }

        /// <summary>Destroys the planned settlements; each leaves a ruin that ruin clearing keeps only on roads.</summary>
        public static (int removed, int ruins) Execute(Plan plan)
        {
            int removed = 0, ruins = 0;
            foreach (Settlement s in plan.Remove.Values.SelectMany(l => l))
            {
                if (!CanRemove(s))
                    continue;
                PlanetTile tile = s.Tile;
                Faction faction = s.Faction;
                s.Destroy();
                removed++;
                var ruin = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.DestroyedSettlement);
                ruin.Tile = tile;
                ruin.SetFaction(faction);
                Find.WorldObjects.Add(ruin);
                if (RuinClearing.Keep(ruin))
                    ruins++;
                else
                    ruin.Destroy();
            }
            Log.Message($"[DD Fixes & Tweaks] Settlement cleanup: {plan.Before} -> {plan.Before - removed} settlements, {ruins} ruins left on roads.");
            return (removed, ruins);
        }

        /// <summary>Asks for confirmation with the per-faction numbers, then runs the cleanup.</summary>
        public static void Confirm(int limit)
        {
            Plan plan = MakePlan(limit);
            if (plan.Total == 0)
            {
                Messages.Message("DDAF_SettlementCleanupNothing".Translate(MinKeep), MessageTypeDefOf.RejectInput, false);
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("DDAF_SettlementCleanupConfirm".Translate(plan.Before, plan.Before - plan.Total, plan.Target, MinKeep));
            sb.AppendLine();
            foreach (var pair in plan.Counts.OrderByDescending(p => p.Value))
            {
                int gone = plan.Remove.TryGetValue(pair.Key, out var l) ? l.Count : 0;
                sb.AppendLine($"{pair.Key.Name}: {pair.Value} -> {pair.Value - gone}");
            }
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(sb.ToString().TrimEnd(), () =>
            {
                var (removed, ruins) = Execute(plan);
                Messages.Message("DDAF_SettlementCleanupDone".Translate(removed, ruins), MessageTypeDefOf.TaskCompletion, false);
            }, destructive: true));
        }
    }
}
