using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace MountedWeapons
{
    public static class MountUtility
    {
        // Def-level answers never change during a session, so they are cached.
        private static readonly Dictionary<ThingDef, bool> mountableCache = new Dictionary<ThingDef, bool>();
        private static ThingCategoryDef grenadesCategory;
        private static bool grenadesSearched;
        private static WeaponClassDef neolithicClass;
        private static bool neolithicSearched;

        /// <summary>
        /// Whether this kind of weapon can ever go on a mount: a ranged primary weapon
        /// that fires projectiles or a beam, not a single-use launcher, not a grenade,
        /// not a muscle-powered neolithic weapon, and not a turret's built-in gun.
        /// </summary>
        public static bool IsMountableDef(ThingDef def)
        {
            if (def == null) return false;
            if (mountableCache.TryGetValue(def, out bool cached)) return cached;
            bool result = ComputeMountable(def);
            mountableCache[def] = result;
            return result;
        }

        private static bool ComputeMountable(ThingDef def)
        {
            if (!def.IsRangedWeapon || def.equipmentType != EquipmentType.Primary) return false;
            // The generic overload accepts subclasses; HasComp(Type) matches the exact class
            // only, and refused the hellcat rifle (CompEquippableAbilityReloadable).
            if (!def.HasComp<CompEquippable>()) return false;
            if (def.destroyOnDrop) return false; // turret guns, mech-only built-ins
            if (def.weaponTags != null && def.weaponTags.Contains("TurretGun")) return false;

            if (!grenadesSearched)
            {
                grenadesCategory = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("Grenades");
                grenadesSearched = true;
            }
            if (grenadesCategory != null && def.IsWithinCategory(grenadesCategory)) return false;

            // Muscle-powered weapons: bows, slings, pila, throwing knives and axes, shuriken.
            // Vanilla's BaseWeaponNeolithic gives them the Neolithic weapon class, and the
            // modded ones (VWE Tribal, VFE Medieval 2, T's Samurai) inherit from that parent,
            // so the class catches them even when the def's own techLevel says Medieval.
            // A frame can't draw a bow or throw a knife. Crossbows are the exception: the
            // string is held by the mechanism, so a frame can shoot one. They are matched by
            // defName (not label, which a translation changes), since no class or tag marks
            // them apart from bows.
            if (!neolithicSearched)
            {
                neolithicClass = DefDatabase<WeaponClassDef>.GetNamedSilentFail("Neolithic");
                neolithicSearched = true;
            }
            if (neolithicClass != null && def.weaponClasses != null && def.weaponClasses.Contains(neolithicClass)
                && !IsCrossbow(def)) return false;

            List<VerbProperties> verbs = def.Verbs;
            if (verbs.NullOrEmpty()) return false;
            VerbProperties primary = verbs.FirstOrDefault(v => v.isPrimary) ?? verbs[0];
            Type verbClass = primary.verbClass;
            if (verbClass == null || primary.range <= 0f) return false;
            if (MountCombatHandler.Active != null && MountCombatHandler.Active.IsMountableVerb(def, primary)) return true;
            if (typeof(Verb_ShootOneUse).IsAssignableFrom(verbClass)) return false;
            if (typeof(Verb_LaunchProjectile).IsAssignableFrom(verbClass)
                || typeof(Verb_ShootBeam).IsAssignableFrom(verbClass)) return true;
            // Spray verbs sweep a path of cells instead of aiming one shot. Dead Man's Switch's
            // gatlings (Fortified.Verb_ArcSprayProjectile) are Verb_Spray subclasses that launch
            // a real projectile at each cell, so they mount like any gun. A spray with no
            // projectile (vanilla incinerator-style effect sprays) stays excluded.
            return typeof(Verb_Spray).IsAssignableFrom(verbClass) && primary.defaultProjectile != null;
        }

        private static readonly string[] crossbowWords = { "crossbow", "arbalest", "arbalist", "ballista" };

        private static bool IsCrossbow(ThingDef def)
        {
            string name = def.defName.ToLowerInvariant();
            for (int i = 0; i < crossbowWords.Length; i++)
            {
                if (name.Contains(crossbowWords[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// Null when the pawn may man a mount holding this weapon, otherwise the reason they
        /// can't. A pawn needs the hands to equip the weapon (vanilla's equip rule: any
        /// Manipulation above zero), and Progression: Education's proficiency when it is loaded.
        /// Capacities are cached, so this is cheap enough for the 60-tick gunner check.
        /// </summary>
        public static string CannotFireReason(Pawn pawn, Thing weapon)
        {
            if (pawn == null || weapon == null) return null;
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return "IncapableOfCapacity".Translate(PawnCapacityDefOf.Manipulation.label).CapitalizeFirst() + ".";
            }
            return ModCompat.CannotFireReason(pawn, weapon);
        }

        /// <summary>Whether this particular weapon can go on a mount right now.</summary>
        public static bool CanMount(Thing weapon, out string reason)
        {
            reason = null;
            if (weapon == null || !IsMountableDef(weapon.def))
            {
                reason = "MW_ReasonNotMountable".Translate();
                return false;
            }
            CompBiocodable bio = weapon.TryGetComp<CompBiocodable>();
            if (bio != null && bio.Biocoded)
            {
                reason = "MW_ReasonBiocoded".Translate();
                return false;
            }
            return true;
        }
    }
}
