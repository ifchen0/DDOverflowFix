using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DynamicDiplomacy;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DDOverflowFix
{
    internal static class Relations
    {
        private const int HostileGoodwill = -100;
        private const int AllyGoodwill = 100;

        /// <summary>
        /// Sets a relation kind between two NPC factions and moves goodwill to match it on both sides. Vanilla derives the
        /// kind from goodwill, so a kind written alone is reverted by the next goodwill check (a rebellion's war ends silently).
        /// </summary>
        public static void Set(Faction a, Faction b, FactionRelationKind kind)
        {
            FactionRelation ab = a.RelationWith(b, allowNull: true);
            FactionRelation ba = b.RelationWith(a, allowNull: true);
            if (ab == null || ba == null)
                return;
            ab.kind = kind;
            ba.kind = kind;
            if (a.IsPlayer || b.IsPlayer)
                return;
            int goodwill = MatchingGoodwill(ab.baseGoodwill, kind);
            ab.baseGoodwill = goodwill;
            ba.baseGoodwill = goodwill;
        }

        private static int MatchingGoodwill(int goodwill, FactionRelationKind kind)
        {
            switch (kind)
            {
                case FactionRelationKind.Hostile:
                    return goodwill > -75 ? HostileGoodwill : goodwill;
                case FactionRelationKind.Ally:
                    return goodwill < 75 ? AllyGoodwill : goodwill;
                default:
                    return goodwill <= -75 || goodwill >= 75 ? 0 : goodwill;
            }
        }

        /// <summary>Drop-in for "relation.kind = value" in Dynamic Diplomacy.</summary>
        public static void SetKindField(FactionRelation relation, FactionRelationKind kind)
        {
            Faction owner = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f != relation.other && f.RelationWith(relation.other, allowNull: true) == relation);
            if (owner == null)
            {
                relation.kind = kind;
                return;
            }
            Set(owner, relation.other, kind);
        }
    }

    /// <summary>
    /// Rebellions (conquest) and arena battles set FactionRelation.kind without touching goodwill; route those writes
    /// through Relations.Set so goodwill matches and the other side follows.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_DD_RelationKindWrites
    {
        private static readonly FieldInfo KindField = AccessTools.Field(typeof(FactionRelation), nameof(FactionRelation.kind));
        private static readonly MethodInfo Setter = AccessTools.Method(typeof(Relations), nameof(Relations.SetKindField));

        public static bool Prepare() => Arena.ConquestType != null && Arena.ArenaType != null;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(Arena.ConquestType, "TryExecuteWorker");
            yield return AccessTools.Method(Arena.ArenaType, "Tick");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int replaced = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.StoresField(KindField))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = Setter;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced == 0)
                Log.Warning($"[DD Arena Fixes] No relation kind writes found in {original.DeclaringType?.Name}.{original.Name}.");
        }
    }

    /// <summary>
    /// With simulated conquest on, Dynamic Diplomacy returns as soon as the battle is set up, before its alliance check and
    /// the alliance cooldown countdown, so the alliance option never does anything. Runs that step for the battle's attacker, as the non-simulated
    /// conquest would (with distance calculation off that is only the cooldown countdown).
    /// Its candidate list also ignored "exclude Empire" and "permanent enemies" (a missing else); both are honoured here.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Conquest_SimulatedAlliance
    {
        internal static Faction battleAttacker;
        internal static bool inConquest;

        public static bool Prepare() => Arena.ConquestType != null;

        public static MethodBase TargetMethod() => AccessTools.Method(Arena.ConquestType, "TryExecuteWorker");

        public static void Prefix()
        {
            inConquest = true;
            battleAttacker = null;
        }

        public static void Postfix(bool __result)
        {
            inConquest = false;
            Faction attacker = battleAttacker;
            battleAttacker = null;
            if (__result && attacker != null)
                AllianceStep(attacker);
        }

        private static void AllianceStep(Faction attacker)
        {
            NPCDiploModSettings dd = NPCDiploSettings.Instance.settings;
            // Without distance calculation Dynamic Diplomacy's own conquest only counts the cooldown down.
            if (dd.repAllowDistanceCalc && dd.repAllowAlliance && DiplomacyWorldComponent.allianceCooldown <= 0)
                TryFormAlliance(attacker, dd);
            if (DiplomacyWorldComponent.allianceCooldown > 0)
                DiplomacyWorldComponent.allianceCooldown--;
        }

        /// <summary>Dynamic Diplomacy's rule: a faction holding 40-60% (and more than 9) of all NPC settlements faces a coalition.</summary>
        private static void TryFormAlliance(Faction attacker, NPCDiploModSettings dd)
        {
            int total = 0, own = 0;
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                if (s.Faction == attacker)
                    own++;
                if (s.Faction != null && !s.Faction.IsPlayer && s.Faction.def.settlementGenerationWeight > 0f && !s.def.defName.StartsWith("City_"))
                    total++;
            }
            List<Faction> candidates = Find.FactionManager.AllFactionsVisible.Where(f => f.def.settlementGenerationWeight > 0f
                && !f.def.hidden && !f.IsPlayer && !f.defeated && f != attacker && f.leader != null && !f.leader.IsPrisoner && !f.leader.Spawned
                && (dd.repAllowPerm || !f.def.permanentEnemy) && (!dd.repExcludeEmpire || f.def != FactionDefOf.Empire)).ToList();
            if (candidates.Count < 2 || own <= 9 || own < total * 0.4f || own > total * 0.6f)
                return;

            List<Faction> members = candidates.Where(_ => Rand.Chance(0.8f)).ToList();
            foreach (Faction member in members)
                Relations.Set(attacker, member, FactionRelationKind.Hostile);
            for (int i = 0; i < members.Count; i++)
            {
                for (int j = i + 1; j < members.Count; j++)
                    Relations.Set(members[i], members[j], FactionRelationKind.Ally);
            }
            string names = string.Join(", ", members.Select(f => f.ToString()));
            Find.LetterStack.ReceiveLetter("LabelAlliance".Translate(), "DescAlliance".Translate(names, attacker), LetterDefOf.NeutralEvent);
            DiplomacyWorldComponent.allianceCooldown = 11;
        }
    }

    /// <summary>Records the attacker when a conquest sets up a battle (see Patch_Conquest_SimulatedAlliance).</summary>
    [HarmonyPatch]
    public static class Patch_Conquest_BeginArenaFight
    {
        public static bool Prepare() => Arena.ConquestType != null;

        public static MethodBase TargetMethod() => AccessTools.Method(Arena.ConquestType, "BeginArenaFight");

        public static void Prefix(Faction baseAttacker)
        {
            if (Patch_Conquest_SimulatedAlliance.inConquest)
                Patch_Conquest_SimulatedAlliance.battleAttacker = baseAttacker;
        }
    }

    /// <summary>
    /// A battle site can wait up to 2 days. If the settlement it is for was destroyed or changed owner meanwhile (another
    /// conquest, World Domination), the attacker's win must not replace or raze it.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Arena_OnAttackerWin
    {
        public static bool Prepare() => Arena.Available;

        public static MethodBase TargetMethod() => AccessTools.Method(Arena.ArenaType, "OnAttackerWin");

        public static bool Prefix(MapParent __instance)
        {
            Settlement combatLoc = Arena.CombatLoc(__instance);
            Faction defender = Arena.DefenderFaction(__instance);
            if (combatLoc != null && !combatLoc.Destroyed && Find.WorldObjects.Contains(combatLoc) && combatLoc.Faction == defender)
                return true;
            Log.Message($"[DD Arena Fixes] {combatLoc?.Name ?? "Battle site settlement"} is gone or no longer held by {defender?.Name}; the attacker's win changes nothing.");
            return false;
        }
    }
}
