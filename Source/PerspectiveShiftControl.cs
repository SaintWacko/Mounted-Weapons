using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MountedWeapons
{
    /// <summary>
    /// Perspective Shift (ferny.PerspectiveShift) lets the player inhabit one pawn and fire
    /// that pawn's own weapon with the mouse. It has no turret control: a click from an
    /// inhabited gunner fired their sidearm (drafted) or ran the work click (undrafted),
    /// and the mount went on choosing targets by itself. While the inhabited pawn mans a
    /// mount, these patches hand Perspective Shift's mouse to the mount instead:
    ///
    ///   - the weapon follows the cursor, held at the edge of the firing arc;
    ///   - a click (or Perspective Shift's hold-to-fire) fires one burst at what is under
    ///     the cursor, and the mount never picks a target itself (Building_WeaponMount);
    ///   - a click on the mount itself reloads it (MountAmmoHandler.OrderReload);
    ///   - Perspective Shift's reticle is drawn for the mount: green for a shot it can take,
    ///     red for one outside the arc, range or sight, white while aiming, orange while
    ///     firing or cooling down.
    ///
    /// The three targets are private methods on PerspectiveShift.Avatar, found by name.
    /// Each patch class's Prepare returns false when Perspective Shift isn't loaded, and
    /// logs one warning when it is loaded but the method has gone (renamed in an update):
    /// that patch is then skipped and the inhabited gunner behaves as it did before.
    /// </summary>
    public static class AvatarMountControl
    {
        private const string AvatarType = "PerspectiveShift.Avatar";
        private static readonly HashSet<string> warned = new HashSet<string>();

        /// <summary>The private Avatar method to patch, or null when it can't be used.</summary>
        public static MethodBase Target(string method, params System.Type[] args)
        {
            System.Type type = GenTypes.GetTypeInAnyAssembly(AvatarType);
            if (type == null) return null;
            MethodInfo found = AccessTools.Method(type, method, args);
            if (found == null && warned.Add(method))
            {
                Log.Warning("[Mounted Weapons] Perspective Shift is loaded but Avatar." + method
                    + " was not found; firing mounts with the mouse is partly off.");
            }
            return found;
        }

        /// <summary>The mount this pawn is manning with a weapon on it, or null.</summary>
        public static Building_WeaponMount ControlledMount(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.CurJobDef != JobDefOf.ManTurret) return null;
            return pawn.CurJob.targetA.Thing is Building_WeaponMount mount && mount.HasWeapon && mount.Map == pawn.Map
                ? mount
                : null;
        }

        /// <summary>Perspective Shift's own choice of target in a cell (Avatar.GetBestTarget).</summary>
        public static LocalTargetInfo TargetAt(Pawn pawn, IntVec3 cell)
        {
            List<Thing> things = cell.GetThingList(pawn.Map);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Pawn && things[i] != pawn) return things[i];
            }
            for (int i = 0; i < things.Count; i++)
            {
                ThingCategory category = things[i].def.category;
                if (category == ThingCategory.Building || category == ThingCategory.Item) return things[i];
            }
            return cell;
        }

        private static bool MouseOnMount(Building_WeaponMount mount, IntVec3 cell) => mount.OccupiedRect().Contains(cell);

        /// <summary>
        /// A click, or a hold-to-fire frame (<paramref name="mouseDown"/> false). Only a real
        /// click reloads, so holding the button over the mount doesn't order a job per frame.
        /// </summary>
        public static void Click(Pawn pawn, Building_WeaponMount mount, bool mouseDown)
        {
            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(mount.Map)) return;
            if (MouseOnMount(mount, cell))
            {
                if (mouseDown) MountAmmoHandler.Active?.OrderReload(pawn, mount);
                return;
            }
            mount.TryAvatarFire(TargetAt(pawn, cell));
        }

        /// <summary>
        /// Stands in for Avatar.UpdateCursorAndReticle while the pawn mans a mount. Mirrors
        /// that method's deselect and blocked checks, then aims the mount and draws the
        /// reticle in place of the pawn's own.
        /// </summary>
        public static void CursorAndReticle(Pawn pawn, Building_WeaponMount mount, bool mouseOverGizmo, bool mouseOverUI)
        {
            bool paused = Find.TickManager.Paused;
            if (pawn.Drafted && !paused && Find.Selector.IsSelected(pawn) && !Find.Targeter.IsTargeting
                && !ModCompat.AvatarCustomGizmosOff)
            {
                Find.Selector.Deselect(pawn);
            }
            bool blocked = mouseOverUI || mouseOverGizmo || ModCompat.AvatarControlsFrozen || Find.Targeter.IsTargeting
                || WorldRendererUtility.WorldSelected || paused;
            IntVec3 cell = UI.MouseCell();
            if (blocked || !cell.InBounds(mount.Map) || MouseOnMount(mount, cell))
            {
                Cursor.visible = true;
                return;
            }
            Cursor.visible = false;
            if (Event.current.type != EventType.Repaint) return;

            mount.AimAvatarAt(UI.MouseMapPosition());
            Texture2D tex;
            Color color;
            switch (mount.AvatarStateFor(TargetAt(pawn, cell)))
            {
                case Building_WeaponMount.AvatarAimState.Ready:
                    tex = ModCompat.AvatarReticle;
                    color = Color.green;
                    break;
                case Building_WeaponMount.AvatarAimState.Aiming:
                    tex = ModCompat.AvatarReticle;
                    color = Color.white;
                    break;
                case Building_WeaponMount.AvatarAimState.Cooldown:
                    tex = ModCompat.AvatarReticleCooldown;
                    color = new Color(1f, 0.65f, 0f);
                    break;
                default:
                    tex = ModCompat.AvatarReticleNoShot;
                    color = Color.red;
                    break;
            }
            if (tex == null)
            {
                // Without the textures there is nothing to draw; the system cursor stays.
                Cursor.visible = true;
                return;
            }
            Vector2 center = UI.MousePositionOnUIInverted;
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(center.x - 16f, center.y - 16f, 32f, 32f), tex);
            GUI.color = prev;
        }
    }

    /// <summary>Drafted click and hold-to-fire: fire the mount instead of the pawn's own weapon.</summary>
    [HarmonyPatch]
    public static class PerspectiveShift_HandleFiring_Patch
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => AvatarMountControl.Target("HandleFiring");

        public static bool Prefix(object __instance)
        {
            Pawn pawn = ModCompat.AvatarPawnOf(__instance);
            Building_WeaponMount mount = AvatarMountControl.ControlledMount(pawn);
            if (mount == null) return true;
            AvatarMountControl.Click(pawn, mount, Event.current != null && Event.current.type == EventType.MouseDown);
            return false;
        }
    }

    /// <summary>Undrafted click: fire the mount, or reload it when the click is on the mount.</summary>
    [HarmonyPatch]
    public static class PerspectiveShift_HandleLeftClick_Patch
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => AvatarMountControl.Target("HandleLeftClick");

        public static bool Prefix(object __instance, ref bool __result)
        {
            Pawn pawn = ModCompat.AvatarPawnOf(__instance);
            Building_WeaponMount mount = AvatarMountControl.ControlledMount(pawn);
            if (mount == null) return true;
            AvatarMountControl.Click(pawn, mount, mouseDown: true);
            __result = true;
            return false;
        }
    }

    /// <summary>Drafted or not, the reticle and the aim follow the mouse while manning.</summary>
    [HarmonyPatch]
    public static class PerspectiveShift_UpdateCursorAndReticle_Patch
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod() => AvatarMountControl.Target("UpdateCursorAndReticle", typeof(bool), typeof(bool));

        public static bool Prefix(object __instance, bool mouseOverGizmo, bool mouseOverUI)
        {
            Pawn pawn = ModCompat.AvatarPawnOf(__instance);
            if (pawn == null || pawn.InMentalState) return true;
            Building_WeaponMount mount = AvatarMountControl.ControlledMount(pawn);
            if (mount == null) return true;
            AvatarMountControl.CursorAndReticle(pawn, mount, mouseOverGizmo, mouseOverUI);
            return false;
        }
    }
}
