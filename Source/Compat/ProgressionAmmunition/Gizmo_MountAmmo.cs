using ProgressionAmmunition;
using RimWorld;
using UnityEngine;
using Verse;

namespace MountedWeapons.Ammo
{
    /// <summary>
    /// Ammo bar for a mount, laid out like Progression: Ammunition's own Gizmo_Ammo.
    /// Theirs can't be reused: its reload button orders the weapon's holder pawn, and a
    /// mounted weapon has none. The draggable marker sets the same autoReloadThreshold
    /// field theirs does, which here is the point where loaders top the mount up.
    /// </summary>
    [StaticConstructorOnStartup]
    public class Gizmo_MountAmmo : Gizmo
    {
        private const float Width = 212f;
        private static readonly Texture2D BarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.34f, 0.42f, 0.43f));
        private static readonly Texture2D BarHighlightTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.43f, 0.54f, 0.55f));
        private static readonly Texture2D EmptyBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.03f, 0.035f, 0.05f));
        private static readonly Texture2D TargetTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.74f, 0.97f, 0.8f));
        private static bool draggingBar;

        private readonly CompAmmo comp;

        public Gizmo_MountAmmo(CompAmmo comp)
        {
            this.comp = comp;
            Order = -95f;
        }

        public override float GetWidth(float maxWidth) => Width;

        // The mount and its gunner selected together both offer this bar; draw it once.
        public override bool GroupsWith(Gizmo other) => other is Gizmo_MountAmmo o && o.comp == comp;

        public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            Rect rect = new Rect(topLeft.x, topLeft.y, GetWidth(maxWidth), 75f);
            Rect inner = rect.ContractedBy(6f);
            Widgets.DrawWindowBackground(rect);

            Rect iconRect = new Rect(inner.x, inner.y, inner.height, inner.height);
            Widgets.DrawMenuSection(iconRect);
            Widgets.DrawTextureFitted(iconRect, comp.parent.def.uiIcon, 0.85f);
            TooltipHandler.TipRegion(iconRect, comp.parent.LabelCap);

            Rect content = new Rect(iconRect.xMax + 4f, inner.y, inner.xMax - iconRect.xMax - 4f, inner.height);
            Text.Font = GameFont.Small;
            Rect labelRect = new Rect(content.x, content.y + 4f, content.width, Text.LineHeight);
            Widgets.Label(labelRect, comp.parent.LabelNoParenthesisCap.Truncate(labelRect.width));

            Rect barRect = new Rect(content.x, content.y + 36f, content.width, 24f);
            int max = comp.MaxAmmo;
            float threshold = comp.autoReloadThreshold;
            Widgets.DraggableBar(barRect, BarTex, BarHighlightTex, EmptyBarTex, TargetTex, ref draggingBar,
                (float)comp.CurAmmo / max, ref threshold);
            comp.autoReloadThreshold = threshold;

            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(barRect, "PA_AmmoFraction".Translate(comp.CurAmmo, max));
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(barRect, "MW_PA_AmmoGizmoTip".Translate(comp.CurAmmo, max, (threshold * 100f).ToString("F0")));

            return new GizmoResult(GizmoState.Clear);
        }
    }
}
