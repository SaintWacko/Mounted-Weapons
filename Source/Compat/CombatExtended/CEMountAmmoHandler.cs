using System.Collections.Generic;
using CombatExtended;
using CombatExtended.Compatibility;
using RimWorld;
using Verse;

namespace MountedWeapons.CE
{
    /// <summary>
    /// Combat Extended's ammo on a mount. The magazine lives on the installed weapon's
    /// CompAmmoUser, as it does in a pawn's hands, so it stays with the weapon on and off
    /// the mount. CE's own reload job (JobDriver_ReloadTurret) does the loading: it reaches
    /// the mount through TurretRegistry, which CombatExtendedCompat registers.
    ///
    /// Progression: Ammunition stands down under CE (LoadFolders.xml), so this is the only
    /// MountAmmoHandler loaded when CE is active.
    /// </summary>
    public class CEMountAmmoHandler : MountAmmoHandler
    {
        private const int GunnerCheckInterval = 30;
        private const int RetryTicks = 250;

        // Not saved: after a load the gunner simply looks again on the next check.
        private readonly Dictionary<int, int> nextGunnerTry = new Dictionary<int, int>();

        public static CompAmmoUser AmmoOf(Building_WeaponMount mount) => mount.MountedWeapon?.TryGetComp<CompAmmoUser>();

        /// <summary>
        /// Empty, or being reloaded. Without this gate the mount kept finding targets and
        /// aiming at them, and CE's reload check on Verb.TryStartCastOn refused every shot
        /// silently, so an empty mount aimed over and over (run #13). CE's own turret does
        /// not fire while reloading either. CE weapons that take ammo but have no magazine
        /// can't be mounted (CEMountCombatHandler.IsMountableVerb).
        /// </summary>
        public override bool OutOfAmmo(Building_WeaponMount mount)
        {
            CompAmmoUser ammo = AmmoOf(mount);
            if (ammo == null) return false;
            return !ammo.CanBeFiredNow || CombatExtendedCompat.reloading.Contains(mount);
        }

        /// <summary>
        /// A gunner whose mount runs dry goes for ammo and comes back to keep manning, as
        /// CE's manned turrets do. TurretRegistry.TryOrderReload (not forced) acts only on
        /// an empty magazine and starts CE's reload job with the manning job resumed after.
        /// Haulers can also reload any mount through CE's own work giver, which finds it
        /// through the map's TurretTracker.
        ///
        /// Not for Perspective Shift's inhabited pawn: the player is steering them, so they
        /// reload when the player clicks the mount (OrderReload).
        /// </summary>
        public override void MountTick(Building_WeaponMount mount)
        {
            if (!mount.IsHashIntervalTick(GunnerCheckInterval)) return;
            Pawn gunner = mount.Gunner;
            if (gunner == null || gunner.CurJobDef != JobDefOf.ManTurret || ModCompat.IsAvatar(gunner)) return;
            CompAmmoUser ammo = AmmoOf(mount);
            if (ammo == null || !ammo.HasMagazine || ammo.CurMagCount > 0) return;
            if (CombatExtendedCompat.reloading.Contains(mount)) return;

            int now = Find.TickManager.TicksGame;
            // The span check matters after loading an earlier save: thing IDs repeat but the
            // clock goes back, and a stored tick far in the future would block the gunner.
            if (nextGunnerTry.TryGetValue(mount.thingIDNumber, out int next) && now < next && next - now <= RetryTicks) return;
            nextGunnerTry[mount.thingIDNumber] = now + RetryTicks;
            mount.TryOrderReload();
        }

        /// <summary>
        /// Perspective Shift's inhabited gunner clicked the mount they man. Forced, so a
        /// part-empty magazine or a change of ammo type is reloaded too, as CE's reload
        /// button does.
        /// </summary>
        public override void OrderReload(Pawn pawn, Building_WeaponMount mount)
        {
            if (pawn.CurJobDef != JobDefOf.ManTurret || pawn.CurJob.targetA.Thing != mount) return;
            if (AmmoOf(mount) == null) return;
            mount.TryForceReload();
        }

        public override string InspectLine(Building_WeaponMount mount)
        {
            CompAmmoUser ammo = AmmoOf(mount);
            if (ammo == null) return null;
            if (CombatExtendedCompat.reloading.Contains(mount)) return "CE_TurretReloading".Translate();
            if (!ammo.HasMagazine) return null;
            string count = ammo.CurMagCount + " / " + ammo.MagSize;
            AmmoDef current = ammo.CurrentAmmo;
            if (current == null) return "MW_CE_InspectMagazine".Translate(count);
            string type = current.ammoClass != null ? (string)current.ammoClass.LabelCap : (string)current.LabelCap;
            return "MW_CE_InspectMagazineAmmo".Translate(count, type);
        }

        /// <summary>
        /// The weapon's own CE gizmos, as Building_TurretGunCE shows them: the magazine bar,
        /// reload and ammo type (CompAmmoUser), then fire and aim mode (CompFireModes). The
        /// mount only asks for these on a player mount.
        /// </summary>
        public override IEnumerable<Gizmo> Gizmos(Building_WeaponMount mount)
        {
            Thing weapon = mount.MountedWeapon;
            if (weapon == null) yield break;
            CompAmmoUser ammo = weapon.TryGetComp<CompAmmoUser>();
            if (ammo != null)
            {
                foreach (Gizmo gizmo in ammo.CompGetGizmosExtra())
                {
                    yield return gizmo;
                }
            }
            CompFireModes modes = weapon.TryGetComp<CompFireModes>();
            if (modes != null)
            {
                foreach (Command command in modes.GenerateGizmos())
                {
                    yield return command;
                }
            }
        }
    }
}
