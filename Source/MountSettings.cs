using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MountedWeapons
{
    /// <summary>Per-mount numbers the player can change. Stored by defName.</summary>
    public class MountTuning : IExposable
    {
        public float rangeMultiplier = 1f;
        public float stability;
        public float warmupFactor = 1f;
        /// <summary>Automated mounts only: ShootingAccuracyTurret.</summary>
        public float turretAccuracy = 0.96f;

        public MountTuning Copy() => (MountTuning)MemberwiseClone();

        public void ExposeData()
        {
            Scribe_Values.Look(ref rangeMultiplier, "range", 1f);
            Scribe_Values.Look(ref stability, "stability", 0f);
            Scribe_Values.Look(ref warmupFactor, "aim", 1f);
            Scribe_Values.Look(ref turretAccuracy, "accuracy", 0.96f);
        }
    }

    /// <summary>
    /// Player settings. The per-mount values override what the mount ThingDefs say in XML:
    /// at startup the XML values are recorded as the defaults (so "Reset" returns to them and
    /// a balance patch from another mod is respected), then the player's values are written
    /// onto the defs' MountExtension and statBases. Everything else reads the defs as before.
    /// </summary>
    public class MountSettings : ModSettings
    {
        public const float DefaultMannedArc = 120f;
        public const float DefaultAutomatedArc = 360f;
        public const float DefaultCoreLoss = 0.5f;
        public const float DefaultWeaponDamage = 0.4f;

        /// <summary>Total firing arc of manned mounts in degrees, centred on the mount's facing. 360 = unlimited.</summary>
        public float mannedArc = DefaultMannedArc;
        public float automatedArc = DefaultAutomatedArc;
        public float coreLossChance = DefaultCoreLoss;
        public float maxWeaponDamage = DefaultWeaponDamage;
        /// <summary>Draw the weapon with its visual centre on the pivot instead of its texture centre.</summary>
        public bool centerWeapons = true;

        public Dictionary<string, MountTuning> tunings = new Dictionary<string, MountTuning>();

        // Filled once defs exist (MountSettingsStartup). Not saved.
        public static readonly Dictionary<string, MountTuning> Defaults = new Dictionary<string, MountTuning>();

        public static MountSettings Get => MountedWeaponsMod.Settings;

        public static IEnumerable<ThingDef> MountDefs => DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.HasModExtension<MountExtension>() && typeof(Building_WeaponMount).IsAssignableFrom(d.thingClass));

        public static bool IsManned(ThingDef def) => def.HasComp(typeof(CompMannable));

        public float ArcFor(ThingDef def) => IsManned(def) ? mannedArc : automatedArc;

        public MountTuning TuningFor(ThingDef def)
        {
            if (!tunings.TryGetValue(def.defName, out MountTuning t))
            {
                t = Defaults.TryGetValue(def.defName, out MountTuning d) ? d.Copy() : new MountTuning();
                tunings[def.defName] = t;
            }
            return t;
        }

        public void ResetGeneral()
        {
            mannedArc = DefaultMannedArc;
            automatedArc = DefaultAutomatedArc;
            coreLossChance = DefaultCoreLoss;
            maxWeaponDamage = DefaultWeaponDamage;
            centerWeapons = true;
        }

        public void ResetMounts(bool manned)
        {
            foreach (ThingDef def in MountDefs.Where(d => IsManned(d) == manned))
            {
                if (Defaults.TryGetValue(def.defName, out MountTuning d))
                {
                    tunings[def.defName] = d.Copy();
                }
            }
        }

        /// <summary>Writes the current values onto the defs and refreshes mounts in the running game.</summary>
        public void Apply()
        {
            if (Defaults.Count == 0) return; // defs not loaded yet
            foreach (ThingDef def in MountDefs)
            {
                // Read without inserting: only mounts the player has opened in the window
                // get a saved entry, so XML and balance patches keep working for the rest.
                if (!tunings.TryGetValue(def.defName, out MountTuning t) && !Defaults.TryGetValue(def.defName, out t)) continue;
                MountExtension ext = def.GetModExtension<MountExtension>();
                ext.rangeMultiplier = t.rangeMultiplier;
                ext.stability = t.stability;
                ext.warmupFactor = t.warmupFactor;
                if (!IsManned(def))
                {
                    StatUtility.SetStatValueInList(ref def.statBases, StatDefOf.ShootingAccuracyTurret, t.turretAccuracy);
                }
            }
            if (Current.Game == null) return;
            foreach (Map map in Find.Maps)
            {
                foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                {
                    (thing as Building_WeaponMount)?.Notify_SettingsChanged();
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref mannedArc, "mannedArc", DefaultMannedArc);
            Scribe_Values.Look(ref automatedArc, "automatedArc", DefaultAutomatedArc);
            Scribe_Values.Look(ref coreLossChance, "coreLossChance", DefaultCoreLoss);
            Scribe_Values.Look(ref maxWeaponDamage, "maxWeaponDamage", DefaultWeaponDamage);
            Scribe_Values.Look(ref centerWeapons, "centerWeapons", true);
            Scribe_Collections.Look(ref tunings, "tunings", LookMode.Value, LookMode.Deep);
            if (tunings == null) tunings = new Dictionary<string, MountTuning>();
            mannedArc = Mathf.Clamp(mannedArc, 30f, 360f);
            automatedArc = Mathf.Clamp(automatedArc, 30f, 360f);
            coreLossChance = Mathf.Clamp01(coreLossChance);
            maxWeaponDamage = Mathf.Clamp01(maxWeaponDamage);
        }
    }

    /// <summary>Records the XML values as defaults, then applies the saved settings, once defs are loaded.</summary>
    [StaticConstructorOnStartup]
    public static class MountSettingsStartup
    {
        static MountSettingsStartup()
        {
            foreach (ThingDef def in MountSettings.MountDefs)
            {
                MountExtension ext = def.GetModExtension<MountExtension>();
                MountSettings.Defaults[def.defName] = new MountTuning
                {
                    rangeMultiplier = ext.rangeMultiplier,
                    stability = ext.stability,
                    warmupFactor = ext.warmupFactor,
                    turretAccuracy = def.GetStatValueAbstract(StatDefOf.ShootingAccuracyTurret),
                };
            }
            MountSettings.Get.Apply();
        }
    }
}
