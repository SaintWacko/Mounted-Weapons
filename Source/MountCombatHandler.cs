using Verse;

namespace MountedWeapons
{
    /// <summary>
    /// Hook for a mod that replaces how weapons fire (Combat Extended). Like
    /// <see cref="MountAmmoHandler"/>, the main assembly never references that mod: a compat
    /// assembly that only loads beside it (see LoadFolders.xml) sets <see cref="Active"/> at
    /// startup. With no such mod, Active stays null and every call site skips it.
    ///
    /// Combat Extended keeps lists of turrets it has to look after (for reloading) and
    /// expects the gun's ammo comp to know which turret holds it, so the mount reports
    /// when it spawns, despawns and changes weapon.
    /// </summary>
    public abstract class MountCombatHandler
    {
        public static MountCombatHandler Active;

        /// <summary>After the mount spawns, including on load and when reinstalled from a minified item.</summary>
        public virtual void Notify_Spawned(Building_WeaponMount mount)
        {
        }

        /// <summary>Before the mount despawns, while it still has its map.</summary>
        public virtual void Notify_DeSpawned(Building_WeaponMount mount)
        {
        }

        /// <summary>A weapon has just gone on the mount.</summary>
        public virtual void Notify_WeaponInstalled(Building_WeaponMount mount, Thing weapon)
        {
        }

        /// <summary>A weapon is coming off the mount; its verbs are still the mount's.</summary>
        public virtual void Notify_WeaponRemoved(Building_WeaponMount mount, Thing weapon)
        {
        }

        /// <summary>
        /// True when this verb gives the gunner Shooting XP itself, so the mount must not
        /// give it a second time. Combat Extended's shooting verb trains whoever it treats as
        /// the shooter, and for a manned turret that is the gunner.
        /// </summary>
        public virtual bool VerbTrainsGunner(Verb verb) => false;

        /// <summary>
        /// True when a weapon whose primary verb is this one can go on a mount, for verbs the
        /// vanilla check in MountUtility doesn't know. Combat Extended's guns shoot with its own
        /// verb class, which doesn't derive from Verb_LaunchProjectile. Asked after the
        /// item checks (ranged, primary, not a grenade, not neolithic) have passed.
        /// </summary>
        public virtual bool IsMountableVerb(VerbProperties primary) => false;
    }
}
