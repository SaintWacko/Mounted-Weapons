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
    }
}
