using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// 開発用（mod.dump = 1）: Remix に合わせるための、場面の見た目の材料を全部ログに書く。
/// 空・環境光・霧・ライトマップ、無効なものも含めた光源、素材の種類（半透明・ノーマルマップ・タイリング・発光）。
/// </summary>
internal static class SceneDump
{
    public static void Log(ManualLogSource log)
    {
        var sb = new StringBuilder("── 見た目の材料\n");
        var sky = RenderSettings.skybox;
        sb.AppendLine($"  空: {(sky != null ? $"{sky.name}（{sky.shader.name}）" : "無し")}" +
                      (sky != null ? " テクスチャ " + string.Join(", ", sky.GetTexturePropertyNames().Where(n => sky.GetTexture(n) != null).Select(n => $"{n}={sky.GetTexture(n).name}({sky.GetTexture(n).dimension})")) : ""));
        sb.AppendLine($"  環境光: {RenderSettings.ambientMode}、色 空 {RenderSettings.ambientSkyColor} 赤道 {RenderSettings.ambientEquatorColor} 地面 {RenderSettings.ambientGroundColor}、" +
                      $"強さ {RenderSettings.ambientIntensity}、反射 {RenderSettings.defaultReflectionMode} {RenderSettings.reflectionIntensity}、太陽 {(RenderSettings.sun != null ? RenderSettings.sun.name : "無し")}");
        sb.AppendLine($"  霧: {RenderSettings.fog} {RenderSettings.fogMode} 色 {RenderSettings.fogColor} 濃さ {RenderSettings.fogDensity} 距離 {RenderSettings.fogStartDistance}〜{RenderSettings.fogEndDistance}");
        sb.AppendLine($"  ライトマップ: {LightmapSettings.lightmaps.Length} 枚、{LightmapSettings.lightmapsMode}、ライトプローブ {(LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0)} 個");

        var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var g in lights.GroupBy(l => (l.type, active: l.isActiveAndEnabled, bake: l.bakingOutput.lightmapBakeType)).OrderBy(g => g.Key.type.ToString()))
            sb.AppendLine($"  光源 {g.Key.type} {(g.Key.active ? "有効" : "無効")} {g.Key.bake}: {g.Count()} 個（例 {string.Join(", ", g.Take(4).Select(l => $"{l.name} 強さ{l.intensity:0.##} 色{l.color}"))}）");

        var renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        var mats = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
        sb.AppendLine($"  素材 {mats.Count} 個。描く順番: " + string.Join(", ", mats.GroupBy(m => m.renderQueue >= 3000 ? "半透明" : m.renderQueue >= 2450 ? "切り抜き" : "不透明").Select(g => $"{g.Key} {g.Count()}")));
        foreach (var g in mats.Where(m => m.renderQueue >= 3000).GroupBy(m => m.shader.name))
        {
            var m = g.First();
            sb.AppendLine($"    半透明 {g.Key}: {g.Count()} 個（例 {m.name}、_Blend {(m.HasProperty("_Blend") ? m.GetFloat("_Blend") : -1)}、" +
                          $"_SrcBlend {(m.HasProperty("_SrcBlend") ? m.GetFloat("_SrcBlend") : -1)} _DstBlend {(m.HasProperty("_DstBlend") ? m.GetFloat("_DstBlend") : -1)}、" +
                          $"色 {(m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.clear)}、" +
                          $"テクスチャ {string.Join(", ", m.GetTexturePropertyNames().Where(n => m.GetTexture(n) != null).Select(n => n + "=" + m.GetTexture(n).name))}）");
        }
        var bumps = mats.Where(m => m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap") is Texture2D).ToList();
        sb.AppendLine($"  ノーマルマップのある素材 {bumps.Count} 個、形式 " + string.Join(", ", bumps.GroupBy(m => ((Texture2D)m.GetTexture("_BumpMap")).graphicsFormat).Select(g => $"{g.Key} {g.Count()}")) +
                      $"、_BumpScale 例 {string.Join(" ", bumps.Take(5).Select(m => m.HasProperty("_BumpScale") ? m.GetFloat("_BumpScale").ToString("0.##") : "-"))}");
        var tiled = mats.Where(m => m.HasProperty("_BaseMap") && (m.GetTextureScale("_BaseMap") != Vector2.one || m.GetTextureOffset("_BaseMap") != Vector2.zero)).ToList();
        sb.AppendLine($"  _BaseMap のタイリングが 1 でない素材 {tiled.Count} 個（例 {string.Join(", ", tiled.Take(6).Select(m => $"{m.name} {m.GetTextureScale("_BaseMap")}+{m.GetTextureOffset("_BaseMap")}"))}）");
        var lit = mats.Where(m => m.shader.name == "Universal Render Pipeline/Lit").ToList();
        sb.AppendLine($"  URP Lit {lit.Count} 個: 金属 例 {string.Join(" ", lit.Take(6).Select(m => m.HasProperty("_Metallic") ? m.GetFloat("_Metallic").ToString("0.##") : "-"))}、" +
                      $"なめらかさ 例 {string.Join(" ", lit.Take(6).Select(m => m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness").ToString("0.##") : "-"))}、" +
                      $"_MetallicGlossMap あり {lit.Count(m => m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap") != null)}、" +
                      $"_OcclusionMap あり {lit.Count(m => m.HasProperty("_OcclusionMap") && m.GetTexture("_OcclusionMap") != null)}、" +
                      $"発光 {lit.Count(m => m.IsKeywordEnabled("_EMISSION"))}、_DetailAlbedoMap あり {lit.Count(m => m.HasProperty("_DetailAlbedoMap") && m.GetTexture("_DetailAlbedoMap") != null)}");
        sb.AppendLine($"  ライトマップを使う Renderer {renderers.Count(r => r.lightmapIndex >= 0 && r.lightmapIndex < 0xFFFE)} / {renderers.Length}");
        sb.Append($"  無効な Renderer（含めて数える）{Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length - renderers.Length} 個");
        log.LogInfo(sb.ToString());
    }
}
