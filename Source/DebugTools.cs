using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace DDOverflowFix
{
    public enum ForcedArenaAI
    {
        None,
        Shelling,
        Basic,
        FactionalWar
    }

    /// <summary>
    /// Replaces the two branch rolls in InitArenaMap so a debug action can force a specific arena AI.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_InitArenaMap_ForcedRoll
    {
        internal static ForcedArenaAI Forced = ForcedArenaAI.None;
        private static int rollIndex;

        // With Deferred Raid Generation the arena is queued and InitArenaMap really runs about 20 s later. The debug
        // action then leaves its mode here; the next InitArenaMap that actually runs applies it and jumps to the map.
        internal static bool PendingArena;
        internal static ForcedArenaAI PendingForced = ForcedArenaAI.None;
        private static bool applyingPending;

        private static readonly MethodInfo RandRange = AccessTools.Method(typeof(Rand), nameof(Rand.Range), new[] { typeof(int), typeof(int) });
        private static readonly MethodInfo RollMethod = AccessTools.Method(typeof(Patch_InitArenaMap_ForcedRoll), nameof(Roll));

        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicDiplomacy.IncidentWorker_NPCConquest"), "InitArenaMap");

        public static void Prefix()
        {
            rollIndex = 0;
            if (PendingArena && Forced == ForcedArenaAI.None)
            {
                Forced = PendingForced;
                applyingPending = true;
            }
        }

        /// <summary>Runs after both a real InitArenaMap and one that another mod skipped (deferred).</summary>
        public static void Postfix(object[] __args, bool __runOriginal)
        {
            if (!applyingPending)
                return;
            Forced = ForcedArenaAI.None;
            applyingPending = false;
            if (!__runOriginal)
                return;
            PendingArena = false;
            PendingForced = ForcedArenaAI.None;
            Map map = (__args[0] as MapParent)?.Map;
            if (map != null)
                CameraJumper.TryJump(map.Center, map);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if (ins.Calls(RandRange))
                    ins.operand = RollMethod;
                yield return ins;
            }
        }

        /// <summary>
        /// Roll 0 picks shelling (&lt;=25), roll 1 picks basic (&lt;=50) or factional war.
        /// </summary>
        public static int Roll(int min, int max)
        {
            int index = rollIndex++;
            switch (Forced)
            {
                case ForcedArenaAI.Shelling:
                    return index == 0 ? min : max;
                case ForcedArenaAI.Basic:
                    return index == 0 ? max : min;
                case ForcedArenaAI.FactionalWar:
                    return max;
                default:
                    return Rand.Range(min, max);
            }
        }
    }

    public static class DebugTools
    {
        private const string Category = "Dynamic Diplomacy Fix";

        [DebugAction(Category, "Conquest arena: random AI", allowedGameStates = AllowedGameStates.Playing)]
        private static void ConquestRandom() => RunConquest(ForcedArenaAI.None);

        [DebugAction(Category, "Conquest arena: basic AI", allowedGameStates = AllowedGameStates.Playing)]
        private static void ConquestBasic() => RunConquest(ForcedArenaAI.Basic);

        [DebugAction(Category, "Conquest arena: factional war AI", allowedGameStates = AllowedGameStates.Playing)]
        private static void ConquestFactionalWar() => RunConquest(ForcedArenaAI.FactionalWar);

        [DebugAction(Category, "Conquest arena: shelling AI", allowedGameStates = AllowedGameStates.Playing)]
        private static void ConquestShelling() => RunConquest(ForcedArenaAI.Shelling);

        [DebugAction(Category, "Jump to latest arena", allowedGameStates = AllowedGameStates.Playing)]
        private static void JumpToLatestArena()
        {
            if (!TryJumpToLatestArena())
                Messages.Message("No active Dynamic Diplomacy arena map.", MessageTypeDefOf.RejectInput, false);
        }

        [DebugAction(Category, "Arena status (who blocks the end check)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ArenaStatus()
        {
            Map map = Find.CurrentMap;
            MapParent parent = map?.Parent;
            if (parent == null || !parent.def.defName.StartsWith("NPCArena"))
            {
                Messages.Message("Open a Dynamic Diplomacy arena map first (use Jump to latest arena).", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var t = Traverse.Create(parent);
            var lhs = t.Field("lhs").GetValue<List<Pawn>>() ?? new List<Pawn>();
            var rhs = t.Field("rhs").GetValue<List<Pawn>>() ?? new List<Pawn>();
            int started = t.Field("tickFightStarted").GetValue<int>();
            bool ended = t.Field("isCombatEnded").GetValue<bool>();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[DD Overflow Fix] Arena '{parent.Label}' started={started} elapsed={Find.TickManager.TicksGame - started} ended={ended}");
            List<Pawn> activeAtt = Dump(sb, "Attacker (lhs)", lhs);
            List<Pawn> activeDef = Dump(sb, "Defender (rhs)", rhs);
            Log.Message(sb.ToString());

            Messages.Message($"Arena: attacker active {activeAtt.Count}/{lhs.Count}, defender active {activeDef.Count}/{rhs.Count}. Details in log.", MessageTypeDefOf.NeutralEvent, false);
            List<Pawn> blockers = activeAtt.Count <= activeDef.Count ? activeAtt : activeDef;
            if (blockers.Count > 0 && blockers.Count <= 5)
                CameraJumper.TryJumpAndSelect(blockers[0]);
        }

        private static List<Pawn> Dump(System.Text.StringBuilder sb, string label, List<Pawn> pawns)
        {
            var active = new List<Pawn>();
            sb.AppendLine($"== {label}: {pawns.Count}");
            foreach (Pawn p in pawns)
            {
                if (p == null)
                {
                    sb.AppendLine("   (null)");
                    continue;
                }
                bool isActive = !p.Dead && !p.Downed && p.Spawned;
                if (isActive)
                    active.Add(p);
                string state = p.Dead ? "DEAD" : p.Downed ? "downed" : !p.Spawned ? "not spawned" : "ACTIVE";
                string pos = p.Spawned ? p.Position.ToString() : "-";
                string lord = p.GetLord()?.LordJob?.GetType().Name ?? "no lord";
                string toil = p.GetLord()?.CurLordToil?.GetType().Name ?? "-";
                string job = p.CurJobDef?.defName ?? "-";
                sb.AppendLine($"   {state,-11} {p.LabelShort} ({p.kindDef?.defName}) faction={p.Faction?.Name} pos={pos} lord={lord}/{toil} job={job}");
            }
            sb.AppendLine($"   -> active: {active.Count}");
            return active;
        }

        private static void RunConquest(ForcedArenaAI mode)
        {
            bool simulated = Traverse.CreateWithType("DynamicDiplomacy.NPCDiploSettings")
                .Property("Instance").Field("settings").Field("repAllowSimulatedConquest").GetValue<bool>();
            if (!simulated)
            {
                Messages.Message("Enable simulated conquest in Dynamic Diplomacy mod settings first; otherwise no arena is created.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("NPC_Conquest");
            if (def == null)
            {
                Messages.Message("IncidentDef NPC_Conquest not found.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var before = new HashSet<Map>(Find.Maps);
            int deferredBefore = DeferredCount();
            Patch_InitArenaMap_ForcedRoll.Forced = mode;
            bool ok;
            try
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, Find.World);
                ok = def.Worker.TryExecute(parms);
            }
            finally
            {
                Patch_InitArenaMap_ForcedRoll.Forced = ForcedArenaAI.None;
            }

            Map created = Find.Maps.FirstOrDefault(m => !before.Contains(m));
            if (ok && created == null && DeferredCount() > deferredBefore)
            {
                Patch_InitArenaMap_ForcedRoll.PendingArena = true;
                Patch_InitArenaMap_ForcedRoll.PendingForced = mode;
                Messages.Message("Conquest arena queued by Deferred Raid Generation; the camera jumps to it when it starts (after any raids queued before it).", MessageTypeDefOf.NeutralEvent, false);
                if (mode == ForcedArenaAI.Shelling)
                    Messages.Message("Shelling AI requires both factions to be Industrial or higher; otherwise it falls back to factional war AI.", MessageTypeDefOf.NeutralEvent, false);
                return;
            }
            if (!ok || created == null)
            {
                Messages.Message("Conquest did not create an arena this time (no valid target, or target already has a map). Try again.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (mode == ForcedArenaAI.Shelling)
                Messages.Message("Shelling AI requires both factions to be Industrial or higher; otherwise it falls back to factional war AI.", MessageTypeDefOf.NeutralEvent, false);
            CameraJumper.TryJump(created.Center, created);
        }

        /// <summary>Number of groups queued by Deferred Raid Generation, or 0 when that mod is not loaded.</summary>
        private static int DeferredCount()
        {
            System.Type type = AccessTools.TypeByName("DeferredRaidGeneration.DeferredRaids");
            object instance = type == null ? null : Traverse.Create(type).Property("Instance").GetValue();
            return instance == null ? 0 : Traverse.Create(instance).Field("pending").Property("Count").GetValue<int>();
        }

        private static bool TryJumpToLatestArena()
        {
            Map map = Find.Maps.LastOrDefault(m => m.Parent != null && m.Parent.def.defName.StartsWith("NPCArena"));
            if (map == null)
                return false;
            CameraJumper.TryJump(map.Center, map);
            return true;
        }
    }
}
