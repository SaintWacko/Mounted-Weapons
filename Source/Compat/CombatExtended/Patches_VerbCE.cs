using System;
using System.Reflection;
using CombatExtended;
using Verse;
using HarmonyLib;
using UnityEngine;

namespace MountedWeapons.CE
{
    /// <summary>
    /// CE's verbs keep warmup and aiming on the caster: on CE's own turret class, or on a
    /// pawn's stance. A mount is neither, so CE logs "Verb caster is not a turret and does
    /// not have a WarmupStance" and returns -1. These read and write the mount's warmup
    /// instead.
    ///
    /// The setter only changes a warmup already running, as CE's does for its turrets.
    /// It is used to shorten repeat shots at the same target.
    /// </summary>
    [HarmonyPatch(typeof(Verb_LaunchProjectileCE), nameof(Verb_LaunchProjectileCE.BurstWarmupTicksLeft), MethodType.Getter)]
    public static class Patch_BurstWarmupTicksLeft_Get
    {
        public static bool Prefix(Verb_LaunchProjectileCE __instance, ref int __result)
        {
            if (!(__instance.caster is Building_WeaponMount mount)) return true;
            __result = mount.WarmupTicksLeft;
            return false;
        }
    }

    [HarmonyPatch(typeof(Verb_LaunchProjectileCE), nameof(Verb_LaunchProjectileCE.BurstWarmupTicksLeft), MethodType.Setter)]
    public static class Patch_BurstWarmupTicksLeft_Set
    {
        public static bool Prefix(Verb_LaunchProjectileCE __instance, int value)
        {
            if (!(__instance.caster is Building_WeaponMount mount)) return true;
            if (mount.WarmupTicksLeft > 0)
            {
                // CE scales the count down and truncates; 0 would cancel the shot instead of
                // firing it, because the mount only fires when a running warmup runs out.
                mount.WarmupTicksLeft = value < 1 ? 1 : value;
            }
            return false;
        }
    }

    /// <summary>
    /// A mount's stability under CE: it removes that fraction of the gun's sway, the slow
    /// drift CE adds to every shot from the shooter's skill and the weapon's sway factor.
    /// Without CE, stability removes that fraction of the gunner's per-cell miss chance
    /// (Patch_ShotReport_HitFactorFromShooter), which CE's ballistics never read.
    ///
    /// Patched on CE's base getter, which every sway figure goes through: the shot itself
    /// (GetSwayVec), the hit-chance report and tooltip (ShiftVecReportFor), and
    /// Verb_ShootCE's override and SwayAmplitudeFor, which both start from base.SwayAmplitude
    /// and then apply aiming. So the reduction applies once, before aiming, the same in
    /// every path. Recoil and spread are left alone.
    ///
    /// Manned mounts only, as without CE: an automated mount's accuracy is its own stat.
    /// </summary>
    [HarmonyPatch(typeof(Verb_LaunchProjectileCE), nameof(Verb_LaunchProjectileCE.SwayAmplitude), MethodType.Getter)]
    public static class Patch_SwayAmplitude
    {
        public static void Postfix(Verb_LaunchProjectileCE __instance, ref float __result)
        {
            if (!(__instance.caster is Building_WeaponMount mount) || mount.Gunner == null) return;
            __result *= 1f - Mathf.Clamp01(mount.Ext.stability);
        }
    }

    /// <summary>
    /// The two patches below take CE's private _isAiming field by Harmony's ____ name.
    /// Harmony throws inside PatchAll when a named field is missing, which would stop every
    /// patch in this assembly, so each one checks for it first and turns itself off.
    /// </summary>
    internal static class CEPatchChecks
    {
        private static bool? hasField;

        public static bool HasAimingField(string patch)
        {
            if (hasField == null)
            {
                FieldInfo field = AccessTools.Field(typeof(Verb_ShootCE), "_isAiming");
                hasField = field != null && field.FieldType == typeof(bool);
            }
            if (hasField == false)
            {
                Log.WarningOnce("[Mounted Weapons] Combat Extended's Verb_ShootCE._isAiming was not found; "
                    + "the mount patch on " + patch + " is off, and mounts in an aimed fire mode may not fire.",
                    ("MW_CE_" + patch).GetHashCode());
            }
            return hasField == true;
        }
    }

    /// <summary>
    /// An aimed shot (CE's default aim mode) adds 30 to 240 ticks of aiming, by distance,
    /// before the burst. CE's turret adds them to its own warmup; for any other caster, CE
    /// puts a warmup stance on the shooter, which for a manned mount is the gunner. That
    /// stance would run the mount's verb from the gunner's side and its "aiming" flag
    /// would be cleared on the next tick (see Patch_VerbTickCE). This adds the aiming time
    /// to the mount's warmup the way CE's turret does, so the shot fires through the
    /// mount's own BeginBurst when it runs out.
    ///
    /// ShouldAim is protected in CE and found by name, so it is looked up in Prepare (the
    /// delegate calls it virtually, so CE's own overrides still apply). If a CE
    /// update renames it, this logs one warning and mounts fire without the aiming time.
    /// Turning the patch off instead would leave CE's stance path in charge, and the shot
    /// would never fire (see above).
    /// </summary>
    [HarmonyPatch(typeof(Verb_ShootCE), nameof(Verb_ShootCE.WarmupComplete))]
    public static class Patch_VerbShootCE_WarmupComplete
    {
        private static Func<Verb_ShootCE, bool> shouldAim;
        private static bool warned;

        public static bool Prepare()
        {
            if (!CEPatchChecks.HasAimingField("WarmupComplete")) return false;
            if (shouldAim == null)
            {
                MethodInfo getter = AccessTools.PropertyGetter(typeof(Verb_ShootCE), "ShouldAim");
                if (getter != null && getter.ReturnType == typeof(bool) && getter.GetParameters().Length == 0)
                {
                    shouldAim = AccessTools.MethodDelegate<Func<Verb_ShootCE, bool>>(getter);
                }
                else if (!warned)
                {
                    warned = true;
                    Log.Warning("[Mounted Weapons] Combat Extended's Verb_ShootCE.ShouldAim was not found; "
                        + "mounts will fire without aiming time.");
                }
            }
            return true;
        }

        public static bool Prefix(Verb_ShootCE __instance, ref bool ____isAiming)
        {
            if (!(__instance.caster is Building_WeaponMount mount)) return true;
            if (shouldAim == null)
            {
                // Marks the aim as done, so CE's own body fires straight away.
                ____isAiming = true;
                return true;
            }
            // Perspective Shift's "disable aiming delay" zeroes only the vanilla warmup
            // (Building_WeaponMount.WarmupTicks for the mount, Verb.WarmupTime in hand).
            // CE's aimed-shot time below still applies to the avatar, as it does when the
            // avatar holds the weapon; Snap Shot mode skips it.
            if (____isAiming || !shouldAim(__instance)) return true;
            // CE's own numbers for its turrets (Verb_ShootCE.WarmupComplete).
            float distance = (__instance.CurrentTarget.Cell - mount.Position).LengthHorizontal;
            mount.WarmupTicksLeft = (int)Mathf.Lerp(30f, 240f, distance / 100f);
            ____isAiming = true;
            return false;
        }
    }

    /// <summary>
    /// CE's per-tick check clears the aiming flag for any caster that is not its own turret
    /// and whose shooter isn't in a warmup stance, which is always true for a mount. The
    /// aimed shot would then start over every time it was due to fire. For a mount, the
    /// flag lasts as long as the mount's warmup and is cleared when that is cancelled. The
    /// rest of CE's check (bipod setup) applies only to drafted pawns.
    ///
    /// CE's body also ends an aim early when ShouldAim turns false partway through (a
    /// suppressed pawn, for one). Skipping it means a mount never cuts an aim short: once
    /// the aiming time is added, the shot waits for it to run out.
    /// </summary>
    [HarmonyPatch(typeof(Verb_ShootCE), nameof(Verb_ShootCE.VerbTickCE))]
    public static class Patch_VerbTickCE
    {
        public static bool Prepare() => CEPatchChecks.HasAimingField("VerbTickCE");

        public static bool Prefix(Verb_ShootCE __instance, ref bool ____isAiming)
        {
            if (!(__instance.caster is Building_WeaponMount mount)) return true;
            if (____isAiming && mount.WarmupTicksLeft <= 0)
            {
                ____isAiming = false;
            }
            return false;
        }
    }
}
