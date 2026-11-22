using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    // Read-only table of every free colonist's bio, one pawn per row: backstories, traits,
    // incapabilities and skills. Purely local UI, nothing here touches simulation state.
    public class MainTabWindow_Bios : MainTabWindow_PawnTable
    {
        private static PawnTableDef tableDef;

        public override PawnTableDef PawnTableDef => tableDef ??= BuildTableDef();

        public override IEnumerable<Pawn> Pawns =>
            PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Where(p => p.story != null);

        // Skill columns are generated so modded skills show up too
        private static PawnTableDef BuildTableDef()
        {
            var def = DefDatabase<PawnTableDef>.GetNamed("MpBios");
            int insertAt = def.columns.FindIndex(c => c.defName == "RemainingSpace");
            if (insertAt < 0) insertAt = def.columns.Count;

            var skillColumns = DefDatabase<SkillDef>.AllDefsListForReading
                .OrderByDescending(s => s.listOrder)
                .Select(s => new PawnColumnDef
                {
                    defName = "MpBios_Skill_" + s.defName,
                    label = s.skillLabel,
                    headerTip = s.LabelCap + "\n\n" + s.description,
                    workerClass = typeof(PawnColumnWorker_BioSkill),
                    sortable = true,
                    width = 52,
                });

            def.columns.InsertRange(insertAt, skillColumns);
            return def;
        }
    }

    public abstract class PawnColumnWorker_BioText : PawnColumnWorker_Text
    {
        protected virtual Color TextColor => Color.white;

        public override void DoCell(Rect rect, Pawn pawn, PawnTable table)
        {
            string text = GetTextFor(pawn);
            if (text.NullOrEmpty()) return;

            var cell = new Rect(rect.x, rect.y, rect.width - 4f, Mathf.Min(rect.height, 30f));
            Text.Font = GameFont.Small;
            Text.Anchor = Anchor;
            GUI.color = TextColor;
            Widgets.Label(cell, text.Truncate(cell.width));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (Mouse.IsOver(cell))
            {
                Widgets.DrawHighlight(cell);
                string tip = GetTip(pawn);
                if (!tip.NullOrEmpty())
                    TooltipHandler.TipRegion(cell, tip);
            }
        }
    }

    public class PawnColumnWorker_BioAge : PawnColumnWorker_BioText
    {
        public override TextAnchor Anchor => TextAnchor.MiddleCenter;
        public override string GetTextFor(Pawn pawn) => pawn.ageTracker.AgeBiologicalYears.ToString();
        public override string GetTip(Pawn pawn) => pawn.ageTracker.AgeTooltipString;
        public override int Compare(Pawn a, Pawn b) =>
            a.ageTracker.AgeBiologicalTicks.CompareTo(b.ageTracker.AgeBiologicalTicks);
    }

    public abstract class PawnColumnWorker_BioBackstory : PawnColumnWorker_BioText
    {
        protected abstract BackstoryDef Get(Pawn pawn);
        public override string GetTextFor(Pawn pawn) => Get(pawn)?.TitleCapFor(pawn.gender);
        public override string GetTip(Pawn pawn) => Get(pawn)?.FullDescriptionFor(pawn).Resolve();
    }

    public class PawnColumnWorker_BioChildhood : PawnColumnWorker_BioBackstory
    {
        protected override BackstoryDef Get(Pawn pawn) => pawn.story.Childhood;
    }

    public class PawnColumnWorker_BioAdulthood : PawnColumnWorker_BioBackstory
    {
        protected override BackstoryDef Get(Pawn pawn) => pawn.story.Adulthood;
    }

    public class PawnColumnWorker_BioTraits : PawnColumnWorker_BioText
    {
        private static IEnumerable<Trait> Traits(Pawn pawn) => pawn.story.traits.allTraits.Where(t => !t.Suppressed);

        public override string GetTextFor(Pawn pawn) => Traits(pawn).Select(t => t.LabelCap).ToCommaList();

        public override string GetTip(Pawn pawn) =>
            Traits(pawn).Select(t => t.LabelCap.AsTipTitle() + "\n" + t.TipString(pawn)).ToLineList("\n");
    }

    public class PawnColumnWorker_BioIncapable : PawnColumnWorker_BioText
    {
        protected override Color TextColor => new(1f, 0.5f, 0.5f);

        // Same source as the character card's "Incapable of" section: disabled work tags
        // (Violent, Social, Caring...) from backstories, traits, genes, titles, roles, hediffs...
        private static List<WorkTags> Disabled(Pawn pawn) =>
            CharacterCardUtility.WorkTagsFrom(pawn.CombinedDisabledWorkTags).ToList();

        public override string GetTextFor(Pawn pawn) =>
            Disabled(pawn).Select(t => t.LabelTranslated().CapitalizeFirst()).ToCommaList();

        public override string GetTip(Pawn pawn) =>
            Disabled(pawn)
                .Select(t => t.LabelTranslated().CapitalizeFirst().AsTipTitle() + "\n" +
                             CharacterCardUtility.GetWorkTypeDisabledCausedBy(pawn, t) + "\n" +
                             CharacterCardUtility.GetWorkTypesDisabledByWorkTag(t))
                .ToLineList("\n");
    }

    // Skill level, plus a flame icon for passion
    public class PawnColumnWorker_BioSkill : PawnColumnWorker
    {
        private SkillDef skill;
        private SkillDef Skill => skill ??= DefDatabase<SkillDef>.GetNamed(def.defName.Substring("MpBios_Skill_".Length));

        public override GameFont DefaultHeaderFont => GameFont.Tiny;
        public override TextAnchor DefaultHeaderAlignment => TextAnchor.LowerCenter;

        private SkillRecord Record(Pawn pawn) => pawn.skills?.GetSkill(Skill);

        public override void DoCell(Rect rect, Pawn pawn, PawnTable table)
        {
            var record = Record(pawn);
            if (record == null) return;

            var cell = new Rect(rect.x, rect.y, rect.width, Mathf.Min(rect.height, 30f));
            bool disabled = record.TotallyDisabled;

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = disabled ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
            Widgets.Label(new Rect(cell.x, cell.y, cell.width / 2f + 4f, cell.height), disabled ? "-" : record.Level.ToString());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (!disabled && record.passion != Passion.None)
            {
                var icon = record.passion == Passion.Major ? SkillUI.PassionMajorIcon : SkillUI.PassionMinorIcon;
                GUI.DrawTexture(new Rect(cell.center.x + 6f, cell.center.y - 8f, 16f, 16f), icon);
            }

            if (Mouse.IsOver(cell))
            {
                Widgets.DrawHighlight(cell);
                TooltipHandler.TipRegion(cell, () => SkillUI.GetSkillDescription(record), record.GetHashCode());
            }
        }

        public override int GetMinWidth(PawnTable table) => Mathf.Max(base.GetMinWidth(table), def.width);
        public override int GetMaxWidth(PawnTable table) => Mathf.Min(base.GetMaxWidth(table), GetMinWidth(table));

        private int SortKey(Pawn pawn)
        {
            var record = Record(pawn);
            if (record == null || record.TotallyDisabled) return -1;
            return record.Level * 3 + (int)record.passion;
        }

        public override int Compare(Pawn a, Pawn b) => SortKey(a).CompareTo(SortKey(b));
    }
}
