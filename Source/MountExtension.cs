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

        /// <summary>
        /// Optional gun shield drawn under the weapon and turned with its aim, so it looks
        /// attached to the gun. Texture path relative to Textures/, drawn as if the gun
        /// points north (up the image). Purely visual: cover still comes from fillPercent.
        /// </summary>
        public string shieldTexPath;

        /// <summary>
        /// Optional second shield layer drawn OVER the weapon, same size, position and turn as
        /// shieldTexPath. Holds only the pixels that should hide the barrel (the plate around
        /// the gun slot), so the barrel reads as passing through the slot. Altitude decides
        /// layering on the top-down map camera, not draw order, which is why one sprite can't
        /// sit both under and over the gun. The canvas must match shieldTexPath's exactly.
        /// </summary>
        public string shieldFrontTexPath;

        /// <summary>Size of the shield texture in cells.</summary>
        public Vector2 shieldDrawSize = Vector2.one;

        /// <summary>How far in front of the weapon's pivot the shield sits, in cells, along the aim.</summary>
        public float shieldOffset = 0f;

        // Built lazily on first draw: GraphicDatabase has to be touched on the main
        // thread after textures load, which DefModExtension construction is not.
        [Unsaved] private Graphic shieldGraphic;
        [Unsaved] private Graphic shieldFrontGraphic;

        public Graphic ShieldGraphic =>
            shieldGraphic ?? (shieldGraphic = MakeShieldGraphic(shieldTexPath));

        public Graphic ShieldFrontGraphic =>
            shieldFrontGraphic ?? (shieldFrontGraphic = MakeShieldGraphic(shieldFrontTexPath));

        private Graphic MakeShieldGraphic(string texPath)
        {
            if (texPath.NullOrEmpty()) return null;
            Graphic graphic = GraphicDatabase.Get<Graphic_Single>(texPath, ShaderDatabase.Cutout, shieldDrawSize, Color.white);
            // Shield art is cut into layers, and the cut edges otherwise filter toward the
            // black that image editors store in transparent pixels: a dark seam that
            // flickers with zoom. See TextureEdgeBleed for the why.
            TextureEdgeBleed.Apply(graphic.MatSingle);
            return graphic;
        }
    }
}
