using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    // Read-only overview of every free colonist's bio: backstories, traits, incapabilities and skills.
    // Purely local UI, nothing here touches simulation state.
    public class MainTabWindow_Bios : MainTabWindow
    {
        private const float RowHeight = 140f;
        private const float PortraitWidth = 100f;
        private const float BioColWidth = 290f;
        private const float TraitColWidth = 250f;
        private const float Gap = 10f;

        private Vector2 scroll;

        public override Vector2 RequestedTabSize => new(1100f, Mathf.Min(UI.screenHeight - 35f, 820f));

        private static List<Pawn> Pawns =>
            PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists
                .Where(p => p.story != null)
                .OrderBy(p => p.LabelShort)
                .ToList();

        public override void DoWindowContents(Rect inRect)
        {
            var pawns = Pawns;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 35f), $"Colonist bios ({pawns.Count})");
            Text.Font = GameFont.Small;

            var outRect = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, pawns.Count * RowHeight);

            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            for (int i = 0; i < pawns.Count; i++)
            {
                var row = new Rect(0f, i * RowHeight, viewRect.width, RowHeight);
                if (row.yMax < scroll.y || row.y > scroll.y + outRect.height) continue;

                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                DrawPawnRow(row.ContractedBy(4f), pawns[i]);
            }
            Widgets.EndScrollView();

            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        private static void DrawPawnRow(Rect rect, Pawn pawn)
        {
            float x = rect.x;

            // Portrait, click to jump to the pawn
            var portraitRect = new Rect(x, rect.y, PortraitWidth, rect.height);
            GUI.DrawTexture(portraitRect, PortraitsCache.Get(pawn, new Vector2(PortraitWidth, rect.height), Rot4.South, cameraZoom: 1.2f));
            Widgets.DrawHighlightIfMouseover(portraitRect);
            TooltipHandler.TipRegion(portraitRect, "Click to jump to " + pawn.LabelShort);
            if (Widgets.ButtonInvisible(portraitRect))
            {
                CameraJumper.TryJumpAndSelect(pawn);
                Find.MainTabsRoot.EscapeCurrentTab();
            }
            x += PortraitWidth + Gap;

            DrawBioColumn(new Rect(x, rect.y, BioColWidth, rect.height), pawn);
            x += BioColWidth + Gap;

            DrawTraitColumn(new Rect(x, rect.y, TraitColWidth, rect.height), pawn);
            x += TraitColWidth + Gap;

            DrawSkillColumn(new Rect(x, rect.y, rect.xMax - x, rect.height), pawn);
        }

        private static void DrawBioColumn(Rect rect, Pawn pawn)
        {
            float y = rect.y;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, y, rect.width, 32f), pawn.Name?.ToStringShort ?? pawn.LabelShort);
            Text.Font = GameFont.Small;
            y += 30f;

            var sub = new List<string>();
            if (pawn.story.TitleCap is { Length: > 0 } title) sub.Add(title);
            sub.Add($"{pawn.gender.GetLabel(pawn.AnimalOrWildMan()).CapitalizeFirst()}, age {pawn.ageTracker.AgeBiologicalYears}");
            if (ModsConfig.BiotechActive && pawn.genes?.XenotypeLabelCap is { Length: > 0 } xeno) sub.Add(xeno);
            if (pawn.Faction != null && pawn.Faction != Faction.OfPlayer) sub.Add(pawn.Faction.Name);

            GUI.color = ColoredText.SubtleGrayColor;
            Widgets.Label(new Rect(rect.x, y, rect.width, 22f), string.Join(" · ", sub).Truncate(rect.width));
            GUI.color = Color.white;
            y += 26f;

            DrawBackstory(new Rect(rect.x, y, rect.width, 22f), "Childhood", pawn.story.Childhood, pawn);
            y += 24f;
            DrawBackstory(new Rect(rect.x, y, rect.width, 22f), "Adulthood", pawn.story.Adulthood, pawn);
            y += 24f;

            if (pawn.Ideo != null)
            {
                var ideoRect = new Rect(rect.x, y, rect.width, 22f);
                Widgets.Label(ideoRect, $"<color=#999999>Ideoligion:</color> {pawn.Ideo.name}".Truncate(rect.width));
                Widgets.DrawHighlightIfMouseover(ideoRect);
                TooltipHandler.TipRegion(ideoRect, pawn.Ideo.description);
            }
        }

        private static void DrawBackstory(Rect rect, string label, BackstoryDef backstory, Pawn pawn)
        {
            if (backstory == null) return;

            Widgets.Label(rect, $"<color=#999999>{label}:</color> {backstory.TitleCapFor(pawn.gender)}".Truncate(rect.width));
            Widgets.DrawHighlightIfMouseover(rect);
            TooltipHandler.TipRegion(rect, () => backstory.FullDescriptionFor(pawn).Resolve(), backstory.GetHashCode() ^ pawn.thingIDNumber);
        }

        private static void DrawTraitColumn(Rect rect, Pawn pawn)
        {
            GUI.color = ColoredText.SubtleGrayColor;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 22f), "Traits");
            GUI.color = Color.white;

            // Flowing trait chips
            float x = rect.x, y = rect.y + 22f;
            foreach (var trait in pawn.story.traits.allTraits)
            {
                if (trait.Suppressed) continue;

                string label = trait.LabelCap;
                float w = Text.CalcSize(label).x + 10f;
                if (x + w > rect.xMax && x > rect.x)
                {
                    x = rect.x;
                    y += 24f;
                }

                var chip = new Rect(x, y, w, 22f);
                Widgets.DrawBoxSolid(chip, new Color(1f, 1f, 1f, 0.08f));
                Widgets.DrawHighlightIfMouseover(chip);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(chip, label);
                Text.Anchor = TextAnchor.UpperLeft;
                var t = trait;
                TooltipHandler.TipRegion(chip, () => t.TipString(pawn), t.GetHashCode() ^ pawn.thingIDNumber);

                x += w + 4f;
            }
            y += 30f;

            var disabled = pawn.GetDisabledWorkTypes(permanentOnly: true);
            if (disabled.Count == 0) return;

            GUI.color = ColoredText.SubtleGrayColor;
            Widgets.Label(new Rect(rect.x, y, rect.width, 22f), "Incapable of");
            y += 22f;
            GUI.color = new Color(1f, 0.5f, 0.5f);
            var incapRect = new Rect(rect.x, y, rect.width, rect.yMax - y);
            Text.Font = GameFont.Tiny;
            Widgets.Label(incapRect, string.Join(", ", disabled.Select(w => w.labelShort.CapitalizeFirst())));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        private static void DrawSkillColumn(Rect rect, Pawn pawn)
        {
            if (pawn.skills == null) return;

            var skills = pawn.skills.skills;
            int perCol = Mathf.CeilToInt(skills.Count / 2f);
            float colWidth = rect.width / 2f;
            float lineHeight = Mathf.Min(22f, rect.height / perCol);

            Text.Font = GameFont.Tiny;
            for (int i = 0; i < skills.Count; i++)
            {
                var skill = skills[i];
                var line = new Rect(rect.x + (i / perCol) * colWidth, rect.y + (i % perCol) * lineHeight, colWidth - 6f, lineHeight);

                if (skill.TotallyDisabled)
                    GUI.color = new Color(1f, 1f, 1f, 0.3f);

                // Level bar background
                if (!skill.TotallyDisabled)
                {
                    var bar = line.ContractedBy(1f);
                    bar.width *= skill.Level / (float)SkillRecord.MaxLevel;
                    Widgets.DrawBoxSolid(bar, new Color(1f, 1f, 1f, 0.07f));
                }

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(line.x + 2f, line.y, line.width - 44f, line.height), skill.def.skillLabel.CapitalizeFirst());

                if (skill.passion != Passion.None)
                {
                    var icon = skill.passion == Passion.Major ? SkillUI.PassionMajorIcon : SkillUI.PassionMinorIcon;
                    GUI.DrawTexture(new Rect(line.xMax - 42f, line.y + (line.height - 16f) / 2f, 16f, 16f), icon);
                }

                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(line.xMax - 24f, line.y, 22f, line.height), skill.TotallyDisabled ? "-" : skill.Level.ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            Text.Font = GameFont.Small;
        }
    }
}
