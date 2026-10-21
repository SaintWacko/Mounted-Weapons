using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
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
    /// Stability is scaled by the gunner's Sight (capped at 100%): a rest steadies the
    /// weapon but cannot aim it for someone who cannot see. Without this a blind gunner,
    /// with far more miss chance to remove, gained several times what a sighted one did.
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
            float sight = Mathf.Clamp01(gunner.health.capacities.GetLevel(PawnCapacityDefOf.Sight));
            perCell = 1f - (1f - perCell) * (1f - mount.Ext.stability * sight);
            // Recurses with a pawn caster, which this postfix ignores.
            __result = ShotReport.HitFactorFromShooter(gunner, distance, perCell);
        }
    }

    /// <summary>
    /// When a turret is manned, Verb_LaunchProjectile.TryCastShot passes the BUILDING as
    /// the projectile's equipment (so a mortar kill reads "killed by mortar"). For a mount
    /// that drops the mounted weapon's quality damage multiplier and its unique-weapon
    /// traits, because Projectile reads both from the equipment. This hands the projectile
    /// the real weapon instead.
    ///
    /// Unique-weapon traits are applied in TryCastShot before Launch is called, against the
    /// building, so they are re-applied here, only on the swap path; an automated mount is
    /// never manned, already passes the weapon, and never reaches this branch.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), new[]
    {
        typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
        typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef)
    })]
    public static class Patch_Projectile_Launch
    {
        public static void Prefix(Projectile __instance, ref Thing equipment)
        {
            if (!(equipment is Building_WeaponMount mount)) return;
            Thing weapon = mount.MountedWeapon;
            if (weapon == null) return;
            equipment = weapon;

            CompUniqueWeapon unique = weapon.TryGetComp<CompUniqueWeapon>();
            if (unique == null) return;
            foreach (WeaponTraitDef trait in unique.TraitsListForReading)
            {
                if (trait.damageDefOverride != null)
                {
                    __instance.damageDefOverride = trait.damageDefOverride;
                }
                if (!trait.extraDamages.NullOrEmpty())
                {
                    if (__instance.extraDamages == null)
                    {
                        __instance.extraDamages = new List<ExtraDamage>();
                    }
                    __instance.extraDamages.AddRange(trait.extraDamages);
                }
            }
        }
    }

    /// <summary>
    /// Uninstalling a building (and anything else that minifies it) goes through
    /// MakeMinified, which despawns it with DestroyMode.Vanish. DeSpawn itself can't tell
    /// minifying from a Vanish destroy or an Anomaly skip, so the mount is told here,
    /// while it is still on the map, and drops its weapon. Gravship moves despawn with
    /// WillReplace and never come through here, so a mount keeps its gun on a gravship.
    /// </summary>
    [HarmonyPatch(typeof(MinifyUtility), nameof(MinifyUtility.MakeMinified))]
    public static class Patch_MinifyUtility_MakeMinified
    {
        public static void Prefix(Thing thing)
        {
            if (thing is Building_WeaponMount mount && thing.def.Minifiable)
            {
                mount.Notify_Minifying();
            }
        }
    }
}
