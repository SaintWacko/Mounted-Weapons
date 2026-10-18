using System.Collections.Generic;
using System.Reflection;
using CombatExtended;
using CombatExtended.Compatibility;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MountedWeapons.CE
{
    /// <summary>
    /// Combat Extended support. This assembly references CombatExtended.dll, so
    /// LoadFolders.xml loads it only when Combat Extended is active.
    ///
    /// The mount stays a vanilla Building_TurretGun. Combat Extended's own turret class
    /// would make every weapon on it go through CE's verbs, and a heavy weapon from
    /// another mod that CE has no patch for keeps its own verb and vanilla projectile,
    /// which CE's turret can't fire. Kept vanilla, the mount fires both: CE-patched
    /// weapons through CE's verbs and projectiles, everything else as it does without CE.
    ///
    /// CE looks after third-party turrets through TurretRegistry (reload jobs, ammo
    /// lookups) and finds turrets to reload through each map's TurretTracker, so the mount
    /// is registered with both.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CombatExtendedCompat
    {
        /// <summary>
        /// Mounts a pawn is reloading right now. CE keeps this flag on its own turrets;
        /// for a registered turret type it asks us. Not saved: a reload in progress at save
        /// time starts again after loading, and the job sets it again.
        /// </summary>
        internal static readonly HashSet<Building_WeaponMount> reloading = new HashSet<Building_WeaponMount>();

        static CombatExtendedCompat()
        {
            TurretRegistry.RegisterReloadableTurret(typeof(Building_WeaponMount),
                setReload: (turret, value) =>
                {
                    Building_WeaponMount mount = (Building_WeaponMount)turret;
                    if (value) reloading.Add(mount);
                    else reloading.Remove(mount);
                },
                getReload: turret => reloading.Contains((Building_WeaponMount)turret),
                gun: turret => ((Building_WeaponMount)turret).MountedWeapon,
                ammo: turret => ((Building_WeaponMount)turret).MountedWeapon?.TryGetComp<CompAmmoUser>());
            MountCombatHandler.Active = new CEMountCombatHandler();
            new Harmony(MountedWeaponsMod.HarmonyId + ".CombatExtended").PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    public class CEMountCombatHandler : MountCombatHandler
    {
        public override void Notify_Spawned(Building_WeaponMount mount)
        {
            mount.Map.GetComponent<TurretTracker>()?.Register(mount);
            SetTurret(mount.MountedWeapon, mount);
        }

        public override void Notify_DeSpawned(Building_WeaponMount mount)
        {
            mount.Map?.GetComponent<TurretTracker>()?.Unregister(mount);
            CombatExtendedCompat.reloading.Remove(mount);
        }

        public override void Notify_WeaponInstalled(Building_WeaponMount mount, Thing weapon)
        {
            SetTurret(weapon, mount);
        }

        public override void Notify_WeaponRemoved(Building_WeaponMount mount, Thing weapon)
        {
            // Left set, the comp would keep reporting the mount's position and map after a
            // pawn picked the weapon up.
            SetTurret(weapon, null);
            CombatExtendedCompat.reloading.Remove(mount);
        }

        public override bool VerbTrainsGunner(Verb verb) => verb is Verb_ShootCE;

        /// <summary>
        /// CE converts guns to Verb_ShootCE, which derives from Verb directly, so the vanilla
        /// verb check refuses them. Single-use launchers (Verb_ShootCEOneUse and its static
        /// variant) stay excluded, as their vanilla counterparts are.
        /// </summary>
        public override bool IsMountableVerb(VerbProperties primary)
        {
            System.Type verbClass = primary.verbClass;
            return typeof(Verb_ShootCE).IsAssignableFrom(verbClass)
                && !typeof(Verb_ShootCEOneUse).IsAssignableFrom(verbClass);
        }

        /// <summary>
        /// CompAmmoUser reads its position and map from <c>turret</c> when it is set, and
        /// sends reload orders to it. The magazine count is saved on the comp, so it stays
        /// with the weapon on and off the mount.
        /// </summary>
        private static void SetTurret(Thing weapon, Building_Turret turret)
        {
            CompAmmoUser ammo = weapon?.TryGetComp<CompAmmoUser>();
            if (ammo != null)
            {
                ammo.turret = turret;
            }
        }
    }
}
