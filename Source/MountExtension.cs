using UnityEngine;
using Verse;

namespace MountedWeapons
{
    /// <summary>
    /// Per-mount tuning, read by Building_WeaponMount. Lives on the mount's ThingDef
    /// so every tier is a pure XML change.
    /// </summary>
    public class MountExtension : DefModExtension
    {
        /// <summary>Mounted weapon's range is multiplied by this.</summary>
        public float rangeMultiplier = 1f;

        /// <summary>
        /// Manned mounts only: fraction of the gunner's per-cell miss chance the mount
        /// removes. A gunner at 0.96 per cell on a 0.25 mount shoots at 0.97 per cell.
        /// Automated mounts use the ShootingAccuracyTurret stat instead.
        /// </summary>
        public float stability = 0f;

        /// <summary>Weapon's own warmup (aim time) is multiplied by this.</summary>
        public float warmupFactor = 1f;

        /// <summary>Weapon's own cooldown is multiplied by this.</summary>
        public float cooldownFactor = 1f;

        /// <summary>
        /// Lets the player force-target an automated mount. Manned mounts can always be
        /// force-targeted by their gunner, as with vanilla manned turrets.
        /// </summary>
        public bool allowForcedTarget = false;

        /// <summary>
        /// The core item built into an automated mount. It must also be listed in
        /// building.leavingsBlacklist so vanilla's 50% deconstruct refund never rolls it;
        /// the mount refunds it itself.
        /// </summary>
        public ThingDef coreDef;

        // Core loss chance and weapon damage on destruction are global mod settings
        // (MountSettings), not per-def.

        /// <summary>Where the weapon sits on the base art, in cells, for a north-facing mount.</summary>
        public Vector2 weaponDrawOffset = Vector2.zero;
    }
}
