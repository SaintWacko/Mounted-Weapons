using System;
using CombatExtended;
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
                mount.WarmupTicksLeft = value;
            }
            return false;
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
    /// </summary>
    [HarmonyPatch(typeof(Verb_ShootCE), nameof(Verb_ShootCE.WarmupComplete))]
    public static class Patch_VerbShootCE_WarmupComplete
    {
        internal static readonly Func<Verb_ShootCE, bool> ShouldAim =
            AccessTools.MethodDelegate<Func<Verb_ShootCE, bool>>(AccessTools.PropertyGetter(typeof(Verb_ShootCE), "ShouldAim"));

        public static bool Prefix(Verb_ShootCE __instance, ref bool ____isAiming)
        {
            if (!(__instance.caster is Building_WeaponMount mount)) return true;
            if (____isAiming || !ShouldAim(__instance)) return true;
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
    /// </summary>
    [HarmonyPatch(typeof(Verb_ShootCE), nameof(Verb_ShootCE.VerbTickCE))]
    public static class Patch_VerbTickCE
    {
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
