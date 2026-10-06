using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MountedWeapons
{
    /// <summary>
    /// A mount's firing arc: a cone of <c>arc</c> degrees centred on the direction the
    /// building faces (Rot4.AsAngle, which uses the same 0 = north, clockwise convention
    /// as Vector3.AngleFlat and TurretTop.CurRotation). 360 or more means unlimited.
    /// </summary>
    public static class FiringArc
    {
        public static bool Unlimited(float arc) => arc >= 359.5f;

        public static bool Contains(Vector3 origin, Rot4 facing, float arc, Vector3 point)
        {
            if (Unlimited(arc)) return true;
            Vector3 d = point - origin;
            d.y = 0f;
            if (d.sqrMagnitude < 0.01f) return true;
            return Mathf.Abs(Mathf.DeltaAngle(facing.AsAngle, d.AngleFlat())) <= arc / 2f;
        }

        /// <summary>Clamps an aim angle to the arc's edges.</summary>
        public static float Clamp(Rot4 facing, float arc, float angle)
        {
            if (Unlimited(arc)) return angle;
            float delta = Mathf.Clamp(Mathf.DeltaAngle(facing.AsAngle, angle), -arc / 2f, arc / 2f);
            return (facing.AsAngle + delta + 360f) % 360f;
        }

        // The cell set is rebuilt only when what it depends on changes; selection overlays
        // draw every frame.
        private static readonly List<IntVec3> cells = new List<IntVec3>();
        private static IntVec3 cachedCenter = IntVec3.Invalid;
        private static Rot4 cachedRot;
        private static float cachedArc = -1f;
        private static float cachedRadius = -1f;

        public static void DrawField(IntVec3 center, Rot4 facing, float arc, float radius, Map map)
        {
            if (Unlimited(arc) || radius <= 0.5f) return;
            if (center != cachedCenter || facing != cachedRot || arc != cachedArc || radius != cachedRadius)
            {
                cachedCenter = center;
                cachedRot = facing;
                cachedArc = arc;
                cachedRadius = radius;
                cells.Clear();
                Vector3 origin = center.ToVector3Shifted();
                int r = Mathf.CeilToInt(radius);
                float rSq = radius * radius;
                for (int x = -r; x <= r; x++)
                {
                    for (int z = -r; z <= r; z++)
                    {
                        if (x * x + z * z > rSq) continue;
                        IntVec3 c = new IntVec3(center.x + x, 0, center.z + z);
                        if (map != null && !c.InBounds(map)) continue;
                        if (Contains(origin, facing, arc, c.ToVector3Shifted())) cells.Add(c);
                    }
                }
            }
            GenDraw.DrawFieldEdges(cells);
        }
    }

    /// <summary>Shows the firing arc while placing or rotating a mount blueprint.</summary>
    public class PlaceWorker_FiringArc : PlaceWorker
    {
        private const float GhostRadius = 12f;

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            float radius = GhostRadius;
            if (thing is Building_WeaponMount mount && mount.HasWeapon)
            {
                radius = mount.AttackVerb.verbProps.range;
            }
            FiringArc.DrawField(center, rot, MountSettings.Get.ArcFor(def), radius, Find.CurrentMap);
        }
    }

    /// <summary>
    /// Finds where a weapon's art actually sits inside its texture. Many weapon textures are
    /// not centred (the minigun's body is well off to one side) and pawns hide that because
    /// they hold the gun 0.4 cells forward of themselves. On a mount the gun rotates around
    /// a visible pivot, so the mount puts the art's alpha-weighted centroid on the pivot.
    ///
    /// Game textures are not CPU-readable, so the texture is blitted into a small render
    /// texture and read back once per texture. Main thread only (it runs from DrawAt).
    /// </summary>
    public static class WeaponArtCentroid
    {
        private const int Sample = 64;
        private static readonly Dictionary<Texture, Vector2> cache = new Dictionary<Texture, Vector2>();

        /// <summary>Centroid offset from the texture centre, in UV units (-0.5..0.5), +x right, +y up.</summary>
        public static Vector2 OffsetFor(Texture tex)
        {
            if (tex == null) return Vector2.zero;
            if (cache.TryGetValue(tex, out Vector2 v)) return v;
            v = Measure(tex);
            cache[tex] = v;
            return v;
        }

        private static Vector2 Measure(Texture tex)
        {
            RenderTexture rt = RenderTexture.GetTemporary(Sample, Sample, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            Texture2D read = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                read = new Texture2D(Sample, Sample, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, Sample, Sample), 0, 0);
                read.Apply();
                Color32[] px = read.GetPixels32();
                double sum = 0, sx = 0, sy = 0;
                for (int y = 0; y < Sample; y++)
                {
                    for (int x = 0; x < Sample; x++)
                    {
                        byte a = px[y * Sample + x].a;
                        if (a < 16) continue;
                        sum += a;
                        sx += a * (x + 0.5);
                        sy += a * (y + 0.5);
                    }
                }
                if (sum <= 0) return Vector2.zero;
                return new Vector2((float)(sx / sum / Sample - 0.5), (float)(sy / sum / Sample - 0.5));
            }
            catch (System.Exception e)
            {
                Log.WarningOnce("[Mounted Weapons] Could not measure weapon art " + tex.name + ", drawing it centred: " + e.Message, tex.GetHashCode());
                return Vector2.zero;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (read != null) Object.Destroy(read);
            }
        }
    }
}
