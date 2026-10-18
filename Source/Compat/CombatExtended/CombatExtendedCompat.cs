using System.Collections.Generic;
using System.Reflection;
using CombatExtended;
using CombatExtended.Compatibility;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

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
            MountAmmoHandler.Active = new CEMountAmmoHandler();
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
            EndReloadJobs(mount);
            mount.Map?.GetComponent<TurretTracker>()?.Unregister(mount);
            CombatExtendedCompat.reloading.Remove(mount);
            CEMountAmmoHandler.reportedNoAmmo.Remove(mount.thingIDNumber);
        }

        public override void Notify_WeaponInstalled(Building_WeaponMount mount, Thing weapon)
        {
            SetTurret(weapon, mount);
        }

        public override void Notify_WeaponRemoved(Building_WeaponMount mount, Thing weapon)
        {
            // Before the turret link is cleared: the job's finish action reports through it.
            EndReloadJobs(mount);
            // Left set, the comp would keep reporting the mount's position and map after a
            // pawn picked the weapon up.
            SetTurret(weapon, null);
            CombatExtendedCompat.reloading.Remove(mount);
            CEMountAmmoHandler.reportedNoAmmo.Remove(mount.thingIDNumber);
        }

        /// <summary>
        /// CE's reload job never checks that the gun is still on the turret, so a pawn
        /// reloading a mount whose weapon was removed, or which was minified, finished the
        /// job and loaded the loose weapon, and CE logged "tried loading ammo with no owner"
        /// (test run #1, 18 Oct). Ending the job first stops that. Only runs when a weapon
        /// comes off or a mount despawns, so the pass over the map's pawns is rare.
        /// </summary>
        private static void EndReloadJobs(Building_WeaponMount mount)
        {
            Map map = mount.Map;
            if (map == null) return;
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                Pawn pawn = pawns[i];
                if (pawn.CurJobDef == CE_JobDefOf.ReloadTurret && pawn.CurJob.targetA.Thing == mount)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                }
            }
        }

        public override bool VerbTrainsGunner(Verb verb) => verb is Verb_ShootCE;

        /// <summary>
        /// CE converts guns to Verb_ShootCE, which derives from Verb directly, so the vanilla
        /// verb check refuses them. Single-use launchers (Verb_ShootCEOneUse and its static
        /// variant) stay excluded, as their vanilla counterparts are.
        ///
        /// So does a weapon that takes ammo but has no magazine. CE feeds those (bows, and
        /// some modded crossbows) a round at a time from the wielder's inventory, and a mount
        /// has none, so it would never fire. Bows are already refused as neolithic before
        /// this is asked; this catches the crossbows. It is refused even with CE's ammo
        /// system switched off, so the answer doesn't change with a setting after the
        /// mountable list has been cached.
        /// </summary>
        public override bool IsMountableVerb(ThingDef def, VerbProperties primary)
        {
            System.Type verbClass = primary.verbClass;
            if (!typeof(Verb_ShootCE).IsAssignableFrom(verbClass)
                || typeof(Verb_ShootCEOneUse).IsAssignableFrom(verbClass)) return false;
            CompProperties_AmmoUser ammo = def.GetCompProperties<CompProperties_AmmoUser>();
            return ammo == null || ammo.ammoSet == null || ammo.magazineSize > 0;
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
