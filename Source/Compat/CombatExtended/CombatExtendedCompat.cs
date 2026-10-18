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
        static CombatExtendedCompat()
        {
            TurretRegistry.RegisterReloadableTurret(typeof(Building_WeaponMount),
                setReload: (turret, value) => CEMountState.SetReloading((Building_WeaponMount)turret, value),
                getReload: turret => CEMountState.IsReloading((Building_WeaponMount)turret),
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
            CEMountState.Forget(mount);
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
            CEMountState.Forget(mount);
        }

        /// <summary>
        /// CE's reload job never checks that the gun is still on the turret, so a pawn
        /// reloading a mount whose weapon was removed, or which was minified, finished the
        /// job and loaded the loose weapon, and CE logged "tried loading ammo with no owner"
        /// (test run #1, 18 Oct). Ending the job first stops that. Only runs when a weapon
        /// comes off or a mount despawns, so the pass over the map's pawns is rare.
        ///
        /// A reload job still waiting in a pawn's queue is dropped too: when it came up, CE
        /// would find no ammo comp on the empty mount and log an error.
        /// </summary>
        private static void EndReloadJobs(Building_WeaponMount mount)
        {
            Map map = mount.Map;
            if (map == null) return;
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                Pawn pawn = pawns[i];
                if (pawn.jobs == null) continue;
                if (pawn.jobs.jobQueue.Count > 0)
                {
                    pawn.jobs.jobQueue.RemoveAll(pawn, job => job.def == CE_JobDefOf.ReloadTurret && job.targetA.Thing == mount);
                }
                if (pawn.CurJobDef == CE_JobDefOf.ReloadTurret && pawn.CurJob.targetA.Thing == mount)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                }
            }
        }

        public override bool VerbTrainsGunner(Verb verb) => verb is Verb_ShootCE;

        /// <summary>
        /// CE converts guns to Verb_ShootCE, which derives from Verb through
        /// Verb_LaunchProjectileCE and never from Verb_LaunchProjectile, so the vanilla verb
        /// check refuses them. Single-use launchers (Verb_ShootCEOneUse and its static
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

    /// <summary>
    /// Per-mount state the CE compat keeps outside the save: which mounts are being
    /// reloaded (CE keeps that flag on its own turrets and asks us for a registered type),
    /// when the gunner may next try a reload, and the last no-ammo message shown.
    ///
    /// Keyed by thing ID and dropped whenever a different game is running, so nothing
    /// here holds a mount, a map or the previous game after a quit to the menu, and an ID
    /// repeated from another save starts clean. A reload in progress at save time starts
    /// again after loading, and the job sets its flag again.
    /// </summary>
    internal static class CEMountState
    {
        private static readonly HashSet<int> reloading = new HashSet<int>();
        private static readonly Dictionary<int, int> nextGunnerTry = new Dictionary<int, int>();
        private static readonly Dictionary<int, string> lastReport = new Dictionary<int, string>();
        private static System.WeakReference<Game> game;

        private static void Sync()
        {
            Game current = Current.Game;
            if (game != null && game.TryGetTarget(out Game known) && known == current) return;
            game = new System.WeakReference<Game>(current);
            reloading.Clear();
            nextGunnerTry.Clear();
            lastReport.Clear();
        }

        public static bool IsReloading(Building_WeaponMount mount)
        {
            Sync();
            return reloading.Contains(mount.thingIDNumber);
        }

        public static void SetReloading(Building_WeaponMount mount, bool value)
        {
            Sync();
            if (value) reloading.Add(mount.thingIDNumber);
            else reloading.Remove(mount.thingIDNumber);
        }

        /// <summary>
        /// True at most once per <paramref name="retryTicks"/> for each mount, and records the
        /// try. The span check matters within one game too: a stored tick far ahead of the
        /// clock would otherwise block the gunner.
        /// </summary>
        public static bool RetryDue(Building_WeaponMount mount, int retryTicks)
        {
            Sync();
            int now = Find.TickManager.TicksGame;
            if (nextGunnerTry.TryGetValue(mount.thingIDNumber, out int next) && now < next && next - now <= retryTicks) return false;
            nextGunnerTry[mount.thingIDNumber] = now + retryTicks;
            return true;
        }

        /// <summary>True when <paramref name="text"/> differs from the last message shown for the mount, and records it.</summary>
        public static bool ShouldReport(Building_WeaponMount mount, string text)
        {
            Sync();
            if (lastReport.TryGetValue(mount.thingIDNumber, out string last) && last == text) return false;
            lastReport[mount.thingIDNumber] = text;
            return true;
        }

        /// <summary>Rounds are loaded: the next time the mount runs dry may report again.</summary>
        public static void ClearReport(Building_WeaponMount mount)
        {
            Sync();
            lastReport.Remove(mount.thingIDNumber);
        }

        /// <summary>The weapon came off or the mount despawned.</summary>
        public static void Forget(Building_WeaponMount mount)
        {
            Sync();
            reloading.Remove(mount.thingIDNumber);
            nextGunnerTry.Remove(mount.thingIDNumber);
            lastReport.Remove(mount.thingIDNumber);
        }
    }
}
