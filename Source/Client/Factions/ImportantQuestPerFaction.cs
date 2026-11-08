using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Factions;

// StorytellerComp_ImportantQuest (Odyssey MechanoidSignal, the grav engine quest chain) only fires if the quest
// was never given in the whole game. In multifaction the QuestManager is shared, so only the first faction got it.
[HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Add))]
static class RecordQuestGivenToFaction
{
    static void Postfix(Quest quest)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction || quest == null) return;
        if (!(Multiplayer.Ticking || Multiplayer.ExecutingCmds)) return;

        var faction = Faction.OfPlayer;
        if (faction == Multiplayer.WorldComp.spectatorFaction) return;
        if (Multiplayer.WorldComp.factionData.TryGetValue(faction.loadID, out var data))
            data.givenQuests.Add(quest.id);
    }
}

[HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Remove))]
static class ForgetQuestGivenToFaction
{
    static void Postfix(Quest quest)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction || quest == null) return;
        foreach (var data in Multiplayer.WorldComp.factionData.Values)
            data.givenQuests.Remove(quest.id);
    }
}

[HarmonyPatch(typeof(StorytellerComp_ImportantQuest), nameof(StorytellerComp_ImportantQuest.BeenGivenQuest), MethodType.Getter)]
static class ImportantQuestGivenPerFaction
{
    static bool Prefix(StorytellerComp_ImportantQuest __instance, ref bool __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction) return true;

        var questDef = ((StorytellerCompProperties_ImportantQuest)__instance.props).questDef;
        var faction = Faction.OfPlayer;
        __result = Find.QuestManager.QuestsListForReading.Any(q => q.root == questDef && GivenTo(q) == faction);
        return false;
    }

    static Faction GivenTo(Quest quest)
    {
        foreach (var data in Multiplayer.WorldComp.factionData.Values)
            if (data.givenQuests.Contains(quest.id))
                return Find.FactionManager.GetById(data.factionId);

        // Quests given before this was tracked: the game's first player faction got them
        return Multiplayer.WorldComp.factionData.Keys
            .Where(id => id != Multiplayer.WorldComp.spectatorFaction.loadID)
            .Select(id => Find.FactionManager.GetById(id))
            .FirstOrDefault(f => f != null);
    }
}
