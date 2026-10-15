using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MountedWeapons
{
    /// <summary>
    /// Fills the colour of fully transparent pixels from their nearest visible neighbours
    /// ("alpha bleed"), leaving alpha at 0, and swaps the result into a material.
    ///
    /// Why: RimWorld loads mod PNGs with mipmaps and trilinear filtering, so every edge
    /// pixel is drawn as a blend with the transparent pixels beside it. Most editors (Pinta
    /// included, with no option to change it) save transparent pixels as RGB 0,0,0, so that
    /// blend pulls edges toward black. On an outer outline nobody notices; on a layer cut
    /// across a flat surface, like the gun shield's front layer, it shows as a dark seam
    /// that flickers in and out as the zoom picks a different mip and pixel alignment.
    /// Bleeding the colour outward makes the blend land on the surface's own colour.
    ///
    /// Done at runtime rather than asking the artist to export it, so every future shield
    /// gets it for free. RimWorld makes loaded textures unreadable, so the pixels are read
    /// back through a RenderTexture blit. Runs once per texture per session, main thread
    /// only (it is called from the lazy shield graphic, which is first touched in DrawAt).
    /// </summary>
    public static class TextureEdgeBleed
    {
        // Keyed by the material so two mount defs sharing one shield texture (or the same
        // def asked twice) don't redo the work or bleed an already bled copy.
        private static readonly HashSet<Material> done = new HashSet<Material>();

        public static void Apply(Material mat)
        {
            if (mat == null || !done.Add(mat)) return;
            if (!(mat.mainTexture is Texture2D source)) return;
            try
            {
                Texture2D bled = Bleed(source);
                if (bled != null) mat.mainTexture = bled;
            }
            catch (Exception e)
            {
                // Cosmetic only: keep the original texture and say why once.
                Log.Warning($"[Mounted Weapons] Could not clean up edges of shield texture {source.name}; drawing it as is. {e.Message}");
            }
        }

        private static Texture2D Bleed(Texture2D source)
        {
            int w = source.width, h = source.height;
            Color32[] px = ReadPixels(source);
            int n = w * h;

            bool[] solid = new bool[n];
            bool[] queued = new bool[n];
            for (int i = 0; i < n; i++) solid[i] = px[i].a > 0;

            // Seed: every transparent pixel touching a visible one.
            List<int> layer = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (solid[i]) continue;
                if (TouchesSolid(i, w, h, solid))
                {
                    queued[i] = true;
                    layer.Add(i);
                }
            }
            if (layer.Count == 0) return null; // fully opaque or fully transparent: nothing to do

            // Grow outward one ring at a time. Each ring averages only rings before it, so the
            // fill comes from the nearest visible colour rather than smearing within a ring.
            while (layer.Count > 0)
            {
                foreach (int i in layer) px[i] = AverageSolidNeighbours(i, w, h, px, solid);
                foreach (int i in layer) solid[i] = true;

                List<int> next = new List<int>();
                foreach (int i in layer)
                {
                    int x = i % w, y = i / w;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = ny * w + nx;
                        if (solid[j] || queued[j]) continue;
                        queued[j] = true;
                        next.Add(j);
                    }
                }
                layer = next;
            }

            Texture2D result = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true)
            {
                name = source.name + "_MWEdgeBleed",
                filterMode = source.filterMode,
                anisoLevel = source.anisoLevel,
                wrapMode = source.wrapMode,
            };
            result.SetPixels32(px);
            result.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return result;
        }

        private static Color32[] ReadPixels(Texture2D source)
        {
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, mipChain: false);
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readable.Apply();
                Color32[] px = readable.GetPixels32();
                UnityEngine.Object.Destroy(readable);
                return px;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static bool TouchesSolid(int i, int w, int h, bool[] solid)
        {
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                if (solid[ny * w + nx]) return true;
            }
            return false;
        }

        private static Color32 AverageSolidNeighbours(int i, int w, int h, Color32[] px, bool[] solid)
        {
            int x = i % w, y = i / w;
            int r = 0, g = 0, b = 0, count = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (!solid[j]) continue;
                r += px[j].r; g += px[j].g; b += px[j].b; count++;
            }
            if (count == 0) return px[i];
            return new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 0);
        }
    }
}
