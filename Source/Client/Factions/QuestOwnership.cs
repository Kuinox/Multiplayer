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
