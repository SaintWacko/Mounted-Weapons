using System.Collections.Generic;
using Verse;

namespace MountedWeapons
{
    /// <summary>
    /// Hook for an ammunition mod. The main assembly never references one: a compat
    /// assembly that only loads beside that mod (see LoadFolders.xml) sets
    /// <see cref="Active"/> at startup, and the mount asks it before firing. With no
    /// ammunition mod loaded, Active stays null and every call site skips it, so mounts
    /// behave exactly as they did before the hook existed.
    /// </summary>
    public abstract class MountAmmoHandler
    {
        public static MountAmmoHandler Active;

        /// <summary>True when the installed weapon counts ammo and has none left.</summary>
        public abstract bool OutOfAmmo(Building_WeaponMount mount);

        /// <summary>Called every tick from the mount; implementations throttle themselves.</summary>
        public abstract void MountTick(Building_WeaponMount mount);

        /// <summary>One inspect-pane line, or null.</summary>
        public abstract string InspectLine(Building_WeaponMount mount);

        public abstract IEnumerable<Gizmo> Gizmos(Building_WeaponMount mount);

        /// <summary>
        /// The player told the mount's gunner to reload it (Perspective Shift's inhabited
        /// gunner clicking the mount it mans). Starts the job or says why not.
        /// </summary>
        public virtual void OrderReload(Pawn pawn, Building_WeaponMount mount)
        {
        }

        /// <summary>
        /// The weapon's ammo capacity changed because it went on this mount or the mount's
        /// capacity setting changed. For handlers that cache the capacity.
        /// </summary>
        public virtual void Notify_CapacityChanged(Building_WeaponMount mount, Thing weapon)
        {
        }

        /// <summary>
        /// The weapon has just come off the mount and is back to its own capacity. Ammo
        /// loaded above that capacity is trimmed here. The mount is still spawned when the
        /// weapon came off a spawned mount.
        /// </summary>
        public virtual void Notify_WeaponRemoved(Building_WeaponMount mount, Thing weapon)
        {
        }
    }
}
