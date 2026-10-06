using HarmonyLib;
using RimWorld;
using Verse;

namespace MountedWeapons
{
    /// <summary>
    /// Vanilla scores a shot by its caster: a pawn uses ShootingAccuracyPawn (skill,
    /// sight, manipulation, traits), anything else uses ShootingAccuracyTurret. A manned
    /// turret fires as the building, so the gunner's skill is normally ignored. For a
    /// manned mount we recompute the factor from the gunner, with the mount's stability
    /// removing part of their per-cell miss chance.
    ///
    /// Postfix rather than a skipping prefix so other mods' prefixes on this method
    /// still run. The early returns keep it cheap: this runs for every shot and every
    /// hit-chance tooltip.
    /// </summary>
    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitFactorFromShooter))]
    public static class Patch_ShotReport_HitFactorFromShooter
    {
        public static void Postfix(Thing caster, float distance, float? acc, ref float __result)
        {
            if (acc.HasValue) return;
            if (!(caster is Building_WeaponMount mount)) return;
            Pawn gunner = mount.Gunner;
            if (gunner == null) return;

            float perCell = gunner.GetStatValue(StatDefOf.ShootingAccuracyPawn);
            perCell = 1f - (1f - perCell) * (1f - mount.Ext.stability);
            // Recurses with a pawn caster, which this postfix ignores.
            __result = ShotReport.HitFactorFromShooter(gunner, distance, perCell);
        }
    }
}
