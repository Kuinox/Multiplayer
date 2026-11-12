using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Factions;

// Quests are shared between factions. A quest belongs to the player whose base is the quest's map (picked at generation).
static class QuestOwnership
{
    static readonly Dictionary<Type, FieldInfo> mapParentFields = new();

    static FieldInfo MapParentField(Type type)
    {
        if (!mapParentFields.TryGetValue(type, out var field))
        {
            field = AccessTools.Field(type, "mapParent");
            if (field != null && !typeof(MapParent).IsAssignableFrom(field.FieldType)) field = null;
            mapParentFields[type] = field;
        }
        return field;
    }

    // null when the quest isn't tied to a player's base
    public static Faction Owner(Quest quest)
    {
        if (quest?.parts == null) return null;

        foreach (var part in quest.parts)
        {
            if (part == null) continue;
            if (MapParentField(part.GetType())?.GetValue(part) is MapParent mapParent &&
                (mapParent.Map?.ParentFaction ?? mapParent.Faction) is { IsPlayer: true } owner &&
                owner != Multiplayer.WorldComp.spectatorFaction)
                return owner;
        }

        // Not tied to a base (world sites, trade requests...): the faction whose storyteller (or command) gave it
        if (Multiplayer.WorldComp != null)
            foreach (var data in Multiplayer.WorldComp.factionData.Values)
                if (data.givenQuests.Contains(quest.id) && Find.FactionManager.GetById(data.factionId) is { } giver &&
                    giver != Multiplayer.WorldComp.spectatorFaction)
                    return giver;

        return quest.TryGetPlayerFaction(out var faction) && faction.IsPlayer ? faction : null;
    }

    public static bool BelongsToOther(Quest quest, Faction faction) =>
        Owner(quest) is { } owner && owner != faction;

    static bool Active => Multiplayer.Client != null && Multiplayer.GameComp.multifaction;

    // Quest countdowns ("shuttle arriving in 8 hours") are shown from every quest of the game
    [HarmonyPatch(typeof(QuestPartActivable), nameof(QuestPartActivable.CachedAlert), MethodType.Getter)]
    static class HideOtherPlayersQuestAlerts
    {
        static void Postfix(QuestPartActivable __instance, ref Alert __result)
        {
            if (__result != null && Active && BelongsToOther(__instance.quest, Multiplayer.RealPlayerFaction))
                __result = null;
        }
    }

    // "Quest expires in x hours" for the offers of other players
    [HarmonyPatch(typeof(Alert_QuestExpiresSoon), "QuestExpiring", MethodType.Getter)]
    static class HideOtherPlayersExpiringQuests
    {
        static bool Prefix(ref Quest ___questExpiring, ref Quest __result)
        {
            if (!Active) return true;

            Quest expiring = null;
            foreach (var quest in Find.QuestManager.questsInDisplayOrder)
            {
                if (!quest.dismissed && !quest.Historical && !quest.initiallyAccepted &&
                    quest.State == QuestState.NotYetAccepted && quest.TicksUntilExpiry > 0 &&
                    quest.TicksUntilExpiry < Alert_QuestExpiresSoon.TicksToAlert &&
                    (expiring == null || quest.TicksUntilExpiry < expiring.TicksUntilExpiry) &&
                    !BelongsToOther(quest, Multiplayer.RealPlayerFaction))
                    expiring = quest;
            }

            __result = ___questExpiring = expiring;
            return false;
        }
    }

    // Accepting another player's quest sent its things/pawns to their base
    [HarmonyPatch(typeof(Quest), nameof(Quest.Accept))]
    static class DontAcceptOtherPlayersQuests
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(Quest __instance)
        {
            if (!Active || !Multiplayer.ExecutingCmds) return true;
            if (!BelongsToOther(__instance, Faction.OfPlayer)) return true;

            Messages.Message($"{__instance.name}: this quest belongs to {Owner(__instance).Name}.", MessageTypeDefOf.RejectInput, false);
            return false;
        }
    }
}
