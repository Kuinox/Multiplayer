using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Verse;

namespace Multiplayer.Client.Desyncs;

// Floats that drift silently: a tiny difference in a need or a hediff severity changes nothing until it crosses a
// threshold (mood >= 0.5 for random inspirations...), hours later, far from its cause. The random state can't see it.
// - Hash(map) goes into the sync opinion every map tick, so the first tick where the floats differ is a desync of its
//   own ("Pawn floats drifted"), close to the cause.
// - Record(map) keeps the raw values every RecordInterval map ticks. Both sides of a desync report write them, at the
//   same map ticks, so the drifting value and its drift over time can be compared.
// Spawned pawns only: AllPawns scans every container of the map and reuses shared result lists.
// Field reads only (no getters with caches, no Rand) so it can't change the simulation.
public static class PawnFloatState
{
    public const int RecordInterval = 250;
    private const int RecordsKept = 12;

    private struct Value
    {
        public int pawnId;
        public string what;
        public float value;
    }

    private class Snapshot
    {
        public int mapTicks;
        public List<Value> values;
    }

    private static readonly ConditionalWeakTable<Map, Queue<Snapshot>> records = new();

    public static int Hash(Map map)
    {
        int hash = map.uniqueID;
        foreach (var pawn in map.mapPawns.pawnsSpawned)
        {
            hash = Gen.HashCombineInt(hash, pawn.thingIDNumber);

            if (pawn.needs?.needs is { } needs)
                foreach (var need in needs)
                    hash = Gen.HashCombineInt(Gen.HashCombineInt(hash, need.def.shortHash), FloatBits(need.curLevelInt));

            if (pawn.health?.hediffSet?.hediffs is { } hediffs)
                foreach (var hediff in hediffs)
                    hash = Gen.HashCombineInt(Gen.HashCombineInt(hash, hediff.def.shortHash), FloatBits(hediff.severityInt));
        }

        return hash;
    }

    public static void Record(Map map, int mapTicks)
    {
        if (mapTicks % RecordInterval != 0) return;

        var values = new List<Value>();
        foreach (var pawn in map.mapPawns.pawnsSpawned)
        {
            if (pawn.needs?.needs is { } needs)
                foreach (var need in needs)
                    values.Add(new Value { pawnId = pawn.thingIDNumber, what = need.def.defName, value = need.curLevelInt });

            if (pawn.health?.hediffSet?.hediffs is { } hediffs)
                foreach (var hediff in hediffs)
                    values.Add(new Value { pawnId = pawn.thingIDNumber, what = hediff.def.defName, value = hediff.severityInt });
        }

        var queue = records.GetValue(map, _ => new Queue<Snapshot>());
        // A rejoin reloads the maps; a map's clock going back means it's a new timeline
        if (queue.Count > 0 && queue.Last().mapTicks >= mapTicks) queue.Clear();
        queue.Enqueue(new Snapshot { mapTicks = mapTicks, values = values });
        while (queue.Count > RecordsKept) queue.Dequeue();
    }

    // One line per pawn and record: "map mapTicks pawn what=value ...", diffable between local and host
    public static string Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Pawn needs and hediff severities every {RecordInterval} map ticks, last {RecordsKept} per map");
        sb.AppendLine("# map mapTicks pawn what=value...");

        foreach (var map in Find.Maps)
        {
            if (!records.TryGetValue(map, out var queue)) continue;
            foreach (var record in queue)
                foreach (var pawn in record.values.GroupBy(v => v.pawnId))
                {
                    sb.Append($"{map.uniqueID} {record.mapTicks} {pawn.Key}");
                    foreach (var v in pawn)
                        sb.Append($" {v.what}={v.value:R}");
                    sb.AppendLine();
                }
        }

        return sb.ToString();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FloatInt
    {
        [FieldOffset(0)] public float f;
        [FieldOffset(0)] public int i;
    }

    // Exact bits: any difference, however small, is drift
    private static int FloatBits(float f) => new FloatInt { f = f }.i;
}
