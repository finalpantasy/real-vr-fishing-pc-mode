using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

namespace RealVRFishingPCMode
{
    // The stages draw their skyline and shore from big photo textures that ship without mipmaps, so they shimmer
    // at a distance on a monitor. This copies each one into a GPU texture with a full mip chain and 16x anisotropic
    // filtering, points the scene's materials at the copy, and raises the level-of-detail distance.
    // Nothing is saved to disk; the copies are rebuilt from the game's own textures every time a scene loads.
    class Scenery : MonoBehaviour
    {
        const int MinSize = 2048;
        const long BudgetBytes = 1024L * 1024 * 1024;

        readonly Dictionary<Texture2D, RenderTexture> copies = new Dictionary<Texture2D, RenderTexture>();
        readonly HashSet<Material> done = new HashSet<Material>();
        long usedBytes;
        float scanUntil, nextScan;

        void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
        void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ReleaseOrphans();
            // stage scenery streams in after the scene itself loads, so keep looking for a while
            scanUntil = Time.unscaledTime + 30f;
            nextScan = 0f;
        }

        void Update()
        {
            if (!Plugin.SharpenScenery.Value) return;
            QualitySettings.lodBias = Mathf.Max(QualitySettings.lodBias, Plugin.LodBias.Value);
            QualitySettings.maximumLODLevel = 0;
            if (Time.unscaledTime > scanUntil || Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 3f;
            Scan();
        }

        void Scan()
        {
            // only what is on screen in this scene (the lobby keeps hidden props loaded), biggest textures first,
            // so the far shore and skyline get the memory before small props do
            var slots = new List<(Material mat, int id, Texture2D tex)>();
            var seen = new HashSet<Material>();
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!r.enabled || r.GetType().Name == "ParticleSystemRenderer") continue;
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || done.Contains(mat) || !seen.Add(mat)) continue;
                    foreach (int id in mat.GetTexturePropertyNameIDs())
                    {
                        if (!(mat.GetTexture(id) is Texture2D tex)) continue;
                        if (tex.mipmapCount > 1)
                        {
                            if (tex.anisoLevel < 16) tex.anisoLevel = 16;
                            continue;
                        }
                        if (Mathf.Max(tex.width, tex.height) >= MinSize) slots.Add((mat, id, tex));
                    }
                }
            }
            slots.Sort((a, b) => ((long)b.tex.width * b.tex.height).CompareTo((long)a.tex.width * a.tex.height));

            int made = 0;
            var skipped = new HashSet<Material>();
            foreach (var (mat, id, tex) in slots)
            {
                var copy = CopyWithMips(tex, ref made);
                if (copy != null) mat.SetTexture(id, copy);
                else skipped.Add(mat);
            }
            foreach (var mat in seen)
                if (!skipped.Contains(mat)) done.Add(mat);
            if (made > 0)
                Plugin.Log.LogInfo($"scenery: rebuilt {made} textures with mipmaps, {usedBytes / (1024 * 1024)} MB in use, lodBias {QualitySettings.lodBias}");
        }

        RenderTexture CopyWithMips(Texture2D tex, ref int made)
        {
            if (copies.TryGetValue(tex, out var existing)) return existing;
            long bytes = (long)tex.width * tex.height * 4 * 4 / 3;
            if (usedBytes + bytes > BudgetBytes) return null;

            var desc = new RenderTextureDescriptor(tex.width, tex.height)
            {
                graphicsFormat = tex.isDataSRGB ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm,
                depthBufferBits = 0,
                useMipMap = true,
                autoGenerateMips = true,
                msaaSamples = 1,
            };
            var rt = new RenderTexture(desc)
            {
                name = tex.name + " (mips)",
                filterMode = FilterMode.Trilinear,
                anisoLevel = 16,
                wrapMode = tex.wrapMode,
            };
            rt.Create();
            Graphics.Blit(tex, rt);
            copies[tex] = rt;
            usedBytes += bytes;
            made++;
            Plugin.Log.LogDebug($"scenery: {tex.name} {tex.width}x{tex.height} {tex.format}");
            return rt;
        }

        // textures from a scene that unloaded are gone; free their copies
        void ReleaseOrphans()
        {
            var gone = new List<Texture2D>();
            foreach (var kv in copies)
                if (kv.Key == null) gone.Add(kv.Key);
            foreach (var k in gone)
            {
                var rt = copies[k];
                usedBytes -= (long)rt.width * rt.height * 4 * 4 / 3;
                rt.Release();
                Destroy(rt);
                copies.Remove(k);
            }
            done.RemoveWhere(m => m == null);
        }
    }
}
