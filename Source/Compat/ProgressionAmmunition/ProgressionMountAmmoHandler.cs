using System.Collections.Generic;
using HarmonyLib;
using ProgressionAmmunition;
using RimWorld;
using Verse;
using Verse.AI;

namespace MountedWeapons.Ammo
{
    public class ProgressionMountAmmoHandler : MountAmmoHandler
    {
        private const int GunnerCheckInterval = 30;
        private const int RetryTicks = 250;
        private const int MessageTicks = 2500;

        // Not saved: after a load the gunner simply looks again on the next check.
        private readonly Dictionary<int, int> nextGunnerTry = new Dictionary<int, int>();
        private readonly Dictionary<int, int> nextMessage = new Dictionary<int, int>();

        public override bool OutOfAmmo(Building_WeaponMount mount) => MountAmmo.OutOfAmmo(mount);

        /// <summary>
        /// A gunner whose mount runs dry goes for ammo themselves and comes back to keep
        /// manning, the way vanilla mortar crews fetch shells. This ignores work settings,
        /// like the mortar case, and works while drafted: the reload job is started
        /// directly and the manning job is suspended behind it.
        ///
        /// Not for Perspective Shift's inhabited pawn: the player is steering them, so they
        /// reload when the player clicks the mount, as that mod does for mortar shells.
        /// </summary>
        public override void MountTick(Building_WeaponMount mount)
        {
            if (!mount.IsHashIntervalTick(GunnerCheckInterval)) return;
            Pawn gunner = mount.Gunner;
            if (gunner == null || gunner.CurJobDef != JobDefOf.ManTurret || ModCompat.IsAvatar(gunner)) return;
            if (!MountAmmo.UsesAmmo(mount, out CompAmmo comp) || !comp.IsOutOfAmmo) return;

            int now = Find.TickManager.TicksGame;
            // The span check matters after loading an earlier save: thing IDs repeat but the
            // clock goes back, and a stored tick far in the future would block the gunner.
            if (nextGunnerTry.TryGetValue(mount.thingIDNumber, out int next) && now < next && next - now <= RetryTicks) return;
            nextGunnerTry[mount.thingIDNumber] = now + RetryTicks;

            // Someone else (a loader or hauler) is already bringing ammo.
            if (!gunner.CanReserve(mount, 1, -1, AmmoDefOf.MW_MountReload)) return;

            if (!MountAmmo.TryFindSource(gunner, comp, out Thing source))
            {
                if (mount.Faction == Faction.OfPlayer
                    && (!nextMessage.TryGetValue(mount.thingIDNumber, out int msg) || now >= msg || msg - now > MessageTicks))
                {
                    nextMessage[mount.thingIDNumber] = now + MessageTicks;
                    Messages.Message("MW_PA_GunnerNoAmmo".Translate(gunner.Named("PAWN"), mount.Named("MOUNT"),
                        comp.WeaponAmmoType.ToStringHuman().Named("AMMO")), mount, MessageTypeDefOf.NegativeEvent, historical: false);
                }
                return;
            }
            gunner.jobs.StartJob(MountAmmo.MakeReloadJob(mount, source), JobCondition.InterruptForced,
                resumeCurJobAfterwards: true);
        }

        public override string InspectLine(Building_WeaponMount mount)
        {
            if (!MountAmmo.UsesAmmo(mount, out CompAmmo comp)) return null;
            return comp.IsOutOfAmmo ? "MW_PA_InspectOutOfAmmo".Translate() : "PA_AmmoRemaining".Translate(comp.CurAmmo, comp.MaxAmmo);
        }

        public override IEnumerable<Gizmo> Gizmos(Building_WeaponMount mount)
        {
            if (MountAmmo.UsesAmmo(mount, out CompAmmo comp))
            {
                yield return new Gizmo_MountAmmo(comp);
            }
        }
    }

    /// <summary>
    /// An empty mount's verb is unavailable. Verb.TryCastNextBurstShot checks Available()
    /// before every shot, so this also ends a burst when the last round goes out mid-burst.
    /// </summary>
    [HarmonyPatch(typeof(Verb_LaunchProjectile), nameof(Verb_LaunchProjectile.Available))]
    public static class Verb_LaunchProjectile_Available_MountAmmo
    {
        public static void Postfix(Verb_LaunchProjectile __instance, ref bool __result)
        {
            if (__result && __instance.caster is Building_WeaponMount mount && MountAmmo.OutOfAmmo(mount))
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// Counts a mount's shots. Progression: Ammunition patches the same method but only
    /// acts when a pawn is the caster; on a mount the building is, so its patch passes
    /// through and this one does the counting. Same scope as theirs: projectile verbs only.
    /// </summary>
    [HarmonyPatch(typeof(Verb_LaunchProjectile), "TryCastShot")]
    public static class Verb_LaunchProjectile_TryCastShot_MountAmmo
    {
        public static void Postfix(Verb_LaunchProjectile __instance, bool __result)
        {
            if (!__result || !(__instance.caster is Building_WeaponMount mount)) return;
            if (!MountAmmo.UsesAmmo(mount, out CompAmmo comp)) return;
            comp.ConsumeAmmo();
            if (comp.IsOutOfAmmo && mount.Spawned)
            {
                MoteMaker.ThrowText(mount.DrawPos, mount.Map, "PA_MoteOutOfAmmo".Translate());
            }
        }
    }
}
