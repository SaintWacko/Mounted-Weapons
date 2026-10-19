using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MountedWeapons
{
    public class MountedWeaponsMod : Mod
    {
        public const string HarmonyId = "saintwacko.MountedWeapons";

        public static MountSettings Settings;

        public MountedWeaponsMod(ModContentPack content) : base(content)
        {
            // PatchAll only reflects over this assembly and GetSettings only reads the
            // settings file; no defs or Unity objects are touched here, which matters
            // because the Mod constructor runs before DefOfs are bound.
            Settings = GetSettings<MountSettings>();
            new Harmony(HarmonyId).PatchAll();
        }

        public override string SettingsCategory() => "Mounted Weapons";

        public override void WriteSettings()
        {
            base.WriteSettings();
            Settings.Apply();
        }

        // ---- window (same layout as Entad Framework and Titan Framework) -----------

        private enum Tab { General, Manned, Automated }
        private Tab tab;
        private Vector2 scroll;
        private float listingHeight = 900f;

        private static bool AnyAutomated => MountSettings.MountDefs.Any(d => !MountSettings.IsManned(d));

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // TabDrawer draws the strip above the rect it is given, so the body starts one
            // tab height down; the bottom 40 px hold the reset button.
            var body = new Rect(inRect.x, inRect.y + TabDrawer.TabHeight, inRect.width, inRect.height - TabDrawer.TabHeight - 40f);
            var tabs = new List<TabRecord>
            {
                new TabRecord("MW_Settings_TabGeneral".Translate(), () => SetTab(Tab.General), tab == Tab.General),
                new TabRecord("MW_Settings_TabManned".Translate(), () => SetTab(Tab.Manned), tab == Tab.Manned),
            };
            // The automated mounts only exist with Biotech.
            if (AnyAutomated)
            {
                tabs.Add(new TabRecord("MW_Settings_TabAutomated".Translate(), () => SetTab(Tab.Automated), tab == Tab.Automated));
            }
            else if (tab == Tab.Automated)
            {
                tab = Tab.General;
            }
            Widgets.DrawMenuSection(body);
            TabDrawer.DrawTabs(body, tabs);

            Rect inner = body.ContractedBy(10f);
            var view = new Rect(0f, 0f, inner.width - 16f, listingHeight);
            Widgets.BeginScrollView(inner, ref scroll, view);
            // maxOneColumn: right after switching to a taller tab the listing overruns
            // last frame's view height for one frame; without this it wraps into a
            // second column and the view never grows back.
            var list = new Listing_Standard { maxOneColumn = true };
            list.Begin(view);
            if (tab == Tab.General) DoGeneral(list);
            else DoMounts(list, tab == Tab.Manned);
            listingHeight = list.CurHeight + 20f;
            list.End();
            Widgets.EndScrollView();

            var reset = new Rect(inRect.x, inRect.yMax - 32f, 200f, 32f);
            if (Widgets.ButtonText(reset, "MW_Settings_Reset".Translate()))
            {
                if (tab == Tab.General) Settings.ResetGeneral();
                else Settings.ResetMounts(tab == Tab.Manned);
            }
            TooltipHandler.TipRegion(reset, "MW_Settings_ResetTip".Translate());
        }

        private void SetTab(Tab t)
        {
            tab = t;
            scroll = Vector2.zero;
        }

        private static void DoGeneral(Listing_Standard l)
        {
            Section(l, "MW_Settings_SectionArcs".Translate());
            Slider(l, "MW_Settings_MannedArc".Translate(), ref Settings.mannedArc, 30f, 360f, ArcLabel(Settings.mannedArc), "MW_Settings_MannedArcHint".Translate(), 5f);
            if (AnyAutomated)
            {
                Slider(l, "MW_Settings_AutomatedArc".Translate(), ref Settings.automatedArc, 30f, 360f, ArcLabel(Settings.automatedArc), "MW_Settings_AutomatedArcHint".Translate(), 5f);
            }
            l.Gap();

            Section(l, "MW_Settings_SectionDestruction".Translate());
            Slider(l, "MW_Settings_WeaponDamage".Translate(), ref Settings.maxWeaponDamage, 0f, 1f, Settings.maxWeaponDamage.ToStringPercent(), "MW_Settings_WeaponDamageHint".Translate(), 0.05f);
            if (AnyAutomated)
            {
                Slider(l, "MW_Settings_CoreLoss".Translate(), ref Settings.coreLossChance, 0f, 1f, Settings.coreLossChance.ToStringPercent(), "MW_Settings_CoreLossHint".Translate(), 0.05f);
            }
            l.Gap();

            Section(l, "MW_Settings_SectionDisplay".Translate());
            l.CheckboxLabeled("MW_Settings_CenterWeapons".Translate(), ref Settings.centerWeapons);
            Hint(l, "MW_Settings_CenterWeaponsHint".Translate());
        }

        private static void DoMounts(Listing_Standard l, bool manned)
        {
            Hint(l, (manned ? "MW_Settings_MannedIntro" : "MW_Settings_AutomatedIntro").Translate());
            l.Gap(6f);
            foreach (ThingDef def in MountSettings.MountDefs.Where(d => MountSettings.IsManned(d) == manned))
            {
                // Edit a copy and save it only once it differs from the def's own values,
                // so viewing the tab doesn't freeze XML values into the settings file.
                bool saved = Settings.tunings.TryGetValue(def.defName, out MountTuning t);
                MountTuning baseline = MountSettings.Defaults.TryGetValue(def.defName, out MountTuning d) ? d : new MountTuning();
                if (!saved) t = baseline.Copy();
                Section(l, def.LabelCap);
                Slider(l, "MW_Settings_Range".Translate(), ref t.rangeMultiplier, 1f, 2f, t.rangeMultiplier.ToStringPercent(), null, 0.05f);
                if (manned)
                {
                    // Under Combat Extended stability reduces sway instead (Patch_SwayAmplitude);
                    // those keys ship in the CE compat's own Keyed file, loaded only with CE.
                    bool ce = MountCombatHandler.Active != null;
                    Slider(l, (ce ? "MW_CE_Settings_Stability" : "MW_Settings_Stability").Translate(), ref t.stability, 0f, 0.9f, t.stability.ToStringPercent(),
                        (ce ? "MW_CE_Settings_StabilityHint" : "MW_Settings_StabilityHint").Translate(), 0.05f);
                }
                else
                {
                    Slider(l, "MW_Settings_Accuracy".Translate(), ref t.turretAccuracy, 0.9f, 0.995f, t.turretAccuracy.ToStringByStyle(ToStringStyle.PercentOne),
                        "MW_Settings_AccuracyHint".Translate(), 0.001f);
                }
                Slider(l, "MW_Settings_Aim".Translate(), ref t.warmupFactor, 0.25f, 1.5f, t.warmupFactor.ToStringPercent(), null, 0.05f);
                if (!saved && !t.SameAs(baseline))
                {
                    Settings.tunings[def.defName] = t;
                }
                l.Gap();
            }
        }

        private static string ArcLabel(float arc) =>
            FiringArc.Unlimited(arc) ? "MW_Settings_ArcUnlimited".Translate().ToString() : Mathf.RoundToInt(arc) + "°";

        private static void Section(Listing_Standard l, string label)
        {
            Text.Font = GameFont.Medium;
            l.Label(label);
            Text.Font = GameFont.Small;
            l.GapLine(4f);
        }

        private static void Hint(Listing_Standard l, string text)
        {
            if (text.NullOrEmpty()) return;
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            l.Label(text);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            l.Gap(4f);
        }

        private static void Slider(Listing_Standard l, string label, ref float value, float min, float max, string shown, string hint, float roundTo)
        {
            Rect row = l.GetRect(Text.LineHeight);
            Widgets.Label(row.LeftPart(0.75f), label);
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(row.RightPart(0.25f), shown);
            Text.Anchor = TextAnchor.UpperLeft;
            // Only snap when the player moves the slider, so an off-grid value from XML
            // isn't rewritten just by opening the tab.
            float moved = l.Slider(value, min, max);
            if (moved != value)
            {
                value = Mathf.Clamp(GenMath.RoundTo(moved, roundTo), min, max);
            }
            Hint(l, hint);
        }
    }
}
