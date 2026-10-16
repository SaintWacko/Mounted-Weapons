using System;
using System.Linq.Expressions;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace MountedWeapons
{
    /// <summary>
    /// Soft compatibility with mods that need only a call or two, bound by name once at
    /// startup. Each delegate stays null when its mod isn't loaded, so every check below
    /// costs one null test without it and there is no load-order requirement.
    ///
    /// Progression: Education (ferny.ProgressionEducation): a colonist mans a mount only
    /// when they have the proficiency to equip its weapon. That mod gates equipping only;
    /// it never looks at turrets, so without this any colonist could fire any mounted gun.
    ///
    /// Perspective Shift (ferny.PerspectiveShift): the inhabited pawn ("avatar") is
    /// controlled by the player, so the ammo compat leaves reloading to the player's
    /// clicks instead of sending the gunner off on its own.
    ///
    /// As above, So below II (astryl.AsAboveSoBelow2): stacks vertical levels ("bands")
    /// on one map at offset z coordinates. A target on another band is at a raw position
    /// far from where it is in the mount's own band, so the firing-arc test translates it
    /// first, the same way that mod's own aiming code does.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ModCompat
    {
        // Progression: Education
        private static readonly Func<Pawn, Thing, bool> peCanEquipItem;
        private static readonly Func<ThingDef, string> peLevelString;

        // Perspective Shift
        private static readonly Func<Pawn, bool> psIsAvatar;
        private static readonly Func<bool> psIsAvatarLeftClick;

        // As above, So below II
        private static readonly Func<Map, MapComponent> abCompOf;
        private static readonly Func<MapComponent, IntVec3, int> abBandOf;
        private static readonly Func<MapComponent, IntVec3, int, IntVec3> abTranslate;

        static ModCompat()
        {
            Type pe = GenTypes.GetTypeInAnyAssembly("ProgressionEducation.ProficiencyUtility");
            if (pe != null)
            {
                peCanEquipItem = Bind<Func<Pawn, Thing, bool>>(pe, "CanEquipItem", typeof(Pawn), typeof(Thing));
                peLevelString = Bind<Func<ThingDef, string>>(pe, "GetProficiencyLevelString", typeof(ThingDef));
                Report("Progression: Education", peCanEquipItem != null && peLevelString != null);
            }

            Type psState = GenTypes.GetTypeInAnyAssembly("PerspectiveShift.State");
            Type psAvatar = GenTypes.GetTypeInAnyAssembly("PerspectiveShift.Avatar");
            if (psState != null && psAvatar != null)
            {
                psIsAvatar = Bind<Func<Pawn, bool>>(psState, "IsAvatar", typeof(Pawn));
                FieldInfo leftClick = psAvatar.GetField("IsAvatarLeftClick", BindingFlags.Public | BindingFlags.Static);
                if (leftClick != null && leftClick.FieldType == typeof(bool))
                {
                    psIsAvatarLeftClick = Expression.Lambda<Func<bool>>(Expression.Field(null, leftClick)).Compile();
                }
                Report("Perspective Shift", psIsAvatar != null && psIsAvatarLeftClick != null);
            }

            Type abBands = GenTypes.GetTypeInAnyAssembly("AsAboveSoBelow.ABBands");
            Type abMap = GenTypes.GetTypeInAnyAssembly("AsAboveSoBelow.ABBandMap");
            if (abBands != null && abMap != null)
            {
                abCompOf = Bind<Func<Map, MapComponent>>(abBands, "CompOf", typeof(Map));
                MethodInfo bandOf = abMap.GetMethod("BandOf", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(IntVec3) }, null);
                MethodInfo translate = abMap.GetMethod("Translate", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(IntVec3), typeof(int) }, null);
                if (bandOf != null && bandOf.ReturnType == typeof(int) && translate != null && translate.ReturnType == typeof(IntVec3))
                {
                    // Instance methods on a type this assembly can't name: compiled once into
                    // delegates that cast the MapComponent, so a call costs no reflection.
                    ParameterExpression comp = Expression.Parameter(typeof(MapComponent), "comp");
                    ParameterExpression cell = Expression.Parameter(typeof(IntVec3), "cell");
                    ParameterExpression band = Expression.Parameter(typeof(int), "band");
                    Expression typed = Expression.Convert(comp, abMap);
                    abBandOf = Expression.Lambda<Func<MapComponent, IntVec3, int>>(
                        Expression.Call(typed, bandOf, cell), comp, cell).Compile();
                    abTranslate = Expression.Lambda<Func<MapComponent, IntVec3, int, IntVec3>>(
                        Expression.Call(typed, translate, cell, band), comp, cell, band).Compile();
                }
                Report("As above, So below II", abCompOf != null && abBandOf != null && abTranslate != null);
            }
        }

        private static T Bind<T>(Type type, string name, params Type[] args) where T : Delegate
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null);
            if (method == null) return null;
            try
            {
                return (T)Delegate.CreateDelegate(typeof(T), method);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static void Report(string mod, bool ok)
        {
            if (!ok)
            {
                Log.Warning("[Mounted Weapons] " + mod + " is loaded but its API has changed; that compatibility is off.");
            }
        }

        // ---------------------------------------------------------------- Progression: Education

        /// <summary>
        /// Null when the pawn may fire this weapon from a mount, otherwise the reason they
        /// can't, in Progression: Education's own words. Its check already skips
        /// non-colonists, mechs and animals, and follows its settings switches.
        /// </summary>
        public static string CannotFireReason(Pawn pawn, Thing weapon)
        {
            if (peCanEquipItem == null || pawn == null || weapon == null) return null;
            if (peCanEquipItem(pawn, weapon)) return null;
            return "PE_CannotEquipItem".Translate(peLevelString(weapon.def));
        }

        // ---------------------------------------------------------------- Perspective Shift

        /// <summary>The pawn the player is inhabiting.</summary>
        public static bool IsAvatar(Pawn pawn) => psIsAvatar != null && pawn != null && psIsAvatar(pawn);

        /// <summary>
        /// The inhabited pawn is acting on a left click. Perspective Shift runs work givers
        /// with forced on for that click, ahead of the float menu.
        /// </summary>
        public static bool IsAvatarLeftClick(Pawn pawn) => psIsAvatarLeftClick != null && psIsAvatarLeftClick() && IsAvatar(pawn);

        // ---------------------------------------------------------------- As above, So below II

        /// <summary>
        /// Moves a point on another band of the map to the matching spot in the band
        /// containing <paramref name="origin"/>. Unchanged on an unbanded map, on the same
        /// band, or without the mod.
        /// </summary>
        public static Vector3 ToBandOf(Map map, IntVec3 origin, IntVec3 targetCell, Vector3 point)
        {
            if (abCompOf == null || map == null) return point;
            MapComponent comp = abCompOf(map);
            if (comp == null) return point;
            int ownBand = abBandOf(comp, origin);
            if (abBandOf(comp, targetCell) == ownBand) return point;
            IntVec3 shifted = abTranslate(comp, targetCell, ownBand);
            return point + (shifted - targetCell).ToVector3();
        }
    }
}
