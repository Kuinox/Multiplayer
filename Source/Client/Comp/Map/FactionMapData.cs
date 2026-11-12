using System.Collections.Generic;
using Multiplayer.Client.Factions;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

// Per-faction storage for RimWorld managers
public class FactionMapData : IExposable
{
    public Map map;
    public int factionId;

    // Saved
    public DesignationManager designationManager;
    public AreaManager areaManager;
    public ZoneManager zoneManager;
    public PlanManager planManager;

    // Not saved
    public HaulDestinationManager haulDestinationManager;
    public ListerHaulables listerHaulables;
    public ResourceCounter resourceCounter;
    public ListerFilthInHomeArea listerFilthInHomeArea;
    public ListerMergeables listerMergeables;

    // ListerHaulables picks the slot group to recheck with static indices, shared by every instance.
    // With one lister per faction, they all advanced it in lockstep: with N factions on a map, a faction only ever
    // rechecked the slot groups at its offset modulo N (e.g. 5 factions and 5 stockpiles: only the first stockpile),
    // so things becoming invalid in place (rotting, low hit points) were never hauled out of the others.
    private int haulablesGroupCycleIndex;
    private readonly List<int> haulablesCellCycleIndices = new();

    private FactionMapData() { }

    // Loading ctor
    public FactionMapData(Map map)
    {
        this.map = map;

        haulDestinationManager = new HaulDestinationManager(map);
        listerHaulables = new ListerHaulables(map);
        resourceCounter = new ResourceCounter(map);
        listerFilthInHomeArea = new ListerFilthInHomeArea(map);
        listerMergeables = new ListerMergeables(map);
    }

    private FactionMapData(int factionId, Map map) : this(map)
    {
        this.factionId = factionId;

        designationManager = new DesignationManager(map);
        areaManager = new AreaManager(map);
        zoneManager = new ZoneManager(map);
        planManager = new PlanManager(map);
    }

    public void ExposeData()
    {
        ExposeActor.OnPostInit(() => map.PushFaction(factionId));

        Scribe_Values.Look(ref factionId, "factionId");
        Scribe_Deep.Look(ref designationManager, "designationManager", map);
        Scribe_Deep.Look(ref areaManager, "areaManager", map);
        Scribe_Deep.Look(ref zoneManager, "zoneManager", map);
        Scribe_Deep.Look(ref planManager, "planManager", map);

        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            designationManager ??= new DesignationManager(map);
            areaManager ??= new AreaManager(map);
            zoneManager ??= new ZoneManager(map);
            planManager ??= new PlanManager(map);
        }

        ExposeActor.OnPostInit(() => map.PopFaction());
    }

    public void ListerHaulablesTick()
    {
        ListerHaulables.groupCycleIndex = haulablesGroupCycleIndex;
        ListerHaulables.cellCycleIndices.Clear();
        ListerHaulables.cellCycleIndices.AddRange(haulablesCellCycleIndices);

        listerHaulables.ListerHaulablesTick();

        haulablesGroupCycleIndex = ListerHaulables.groupCycleIndex;
        haulablesCellCycleIndices.Clear();
        haulablesCellCycleIndices.AddRange(ListerHaulables.cellCycleIndices);
    }

    public static FactionMapData New(int factionId, Map map)
    {
        return new FactionMapData(factionId, map);
    }

    public static FactionMapData NewFromMap(Map map, int factionId)
    {
        return new FactionMapData(map)
        {
            factionId = factionId,

            designationManager = map.designationManager,
            areaManager = map.areaManager,
            zoneManager = map.zoneManager,
            planManager = map.planManager,

            haulDestinationManager = map.haulDestinationManager,
            listerHaulables = map.listerHaulables,
            resourceCounter = map.resourceCounter,
            listerFilthInHomeArea = map.listerFilthInHomeArea,
            listerMergeables = map.listerMergeables,
        };
    }
}
