using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Factions;

// Quests are shared, so dismissing one hid it for every player.
// In multifaction each faction keeps its own dismissed quests, Quest.dismissed is only the local player's view (UI only in vanilla).
static class QuestDismissPerFaction
{
    static bool Active => Multiplayer.Client != null && Multiplayer.GameComp.multifaction;

    static FactionWorldData Data(Faction faction) =>
        faction != null && Multiplayer.WorldComp.factionData.TryGetValue(faction.loadID, out var data) ? data : null;

    public static void ApplyLocalView()
    {
        var data = Data(Multiplayer.RealPlayerFaction);
        foreach (var quest in Find.QuestManager.QuestsListForReading)
            quest.dismissed = data != null && data.dismissedQuests.Contains(quest.id);
    }

    [SyncMethod]
    static void SetDismissed(Quest quest, bool dismissed)
    {
        var data = Data(Faction.OfPlayer);
        if (data == null) return;

        foreach (var q in quest.GetSubquests().Prepend(quest))
        {
            if (dismissed) data.dismissedQuests.Add(q.id);
            else data.dismissedQuests.Remove(q.id);

            if (Faction.OfPlayer == Multiplayer.RealPlayerFaction)
                q.dismissed = dismissed;
        }
    }

    [HarmonyPatch(typeof(MainTabWindow_Quests), nameof(MainTabWindow_Quests.DoDismissButton))]
    static class DismissButton
    {
        static void Prefix(Quest ___selected, out (Quest quest, bool dismissed) __state) =>
            __state = (___selected, ___selected?.dismissed ?? false);

        static void Postfix((Quest quest, bool dismissed) __state)
        {
            if (!Active || __state.quest == null || __state.quest.Historical) return;
            if (__state.quest.dismissed != __state.dismissed)
                SetDismissed(__state.quest, __state.quest.dismissed);
        }
    }

    // Vanilla un-dismisses on accept, only do it for the accepting faction
    [HarmonyPatch(typeof(Quest), nameof(Quest.Accept))]
    static class UndismissOnAccept
    {
        static void Postfix(Quest __instance)
        {
            // Also runs when the sync intercepts the local call, only touch the data in the command
            if (!Active || !(Multiplayer.ExecutingCmds || Multiplayer.Ticking)) return;
            Data(Faction.OfPlayer)?.dismissedQuests.Remove(__instance.id);
            __instance.dismissed = Data(Multiplayer.RealPlayerFaction)?.dismissedQuests.Contains(__instance.id) ?? false;
        }
    }

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Remove))]
    static class ForgetRemovedQuest
    {
        static void Postfix(Quest quest)
        {
            if (!Active || quest == null || !(Multiplayer.Ticking || Multiplayer.ExecutingCmds)) return;
            foreach (var data in Multiplayer.WorldComp.factionData.Values)
                data.dismissedQuests.Remove(quest.id);
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    static class ApplyOnLoad
    {
        [HarmonyPriority(Priority.Last)]
        static void Postfix()
        {
            if (Active) ApplyLocalView();
        }
    }
}
