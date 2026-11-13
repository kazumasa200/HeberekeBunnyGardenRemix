using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// 今の場面が何で描かれているかをログに書く（カメラ、Canvas、Renderer の種類とシェーダー）。
/// Remix に何を渡すかを決めるための診断。
/// </summary>
internal static class SceneCensus
{
    public static void Log(ManualLogSource log, string why)
    {
        var sb = new StringBuilder();
        var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name);
        sb.AppendLine($"── 場面の中身（{why}）: 読み込み中の場面 {string.Join(", ", scenes)}、有効 {SceneManager.GetActiveScene().name}");

        foreach (var c in Camera.allCameras.OrderBy(c => c.depth))
        {
            var urp = c.GetComponent<UniversalAdditionalCameraData>();
            var stack = urp != null && urp.renderType == CameraRenderType.Base ? $"、重ねるカメラ {urp.cameraStack.Count}" : "";
            sb.AppendLine($"  カメラ {c.name}: depth {c.depth}、{urp?.renderType}{stack}、出力 {(c.targetTexture != null ? c.targetTexture.name : "画面")}、" +
                          $"cullingMask 0x{c.cullingMask:X}、{(c.orthographic ? "平行投影" : $"fov {c.fieldOfView:0.#}")}、位置 {c.transform.position}");
        }

        foreach (var cv in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).Where(cv => cv.isRootCanvas && cv.isActiveAndEnabled))
            sb.AppendLine($"  Canvas {cv.name}: {cv.renderMode}{(cv.worldCamera != null ? $"（カメラ {cv.worldCamera.name}）" : "")}、sortingOrder {cv.sortingOrder}");

        var renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        foreach (var g in renderers.GroupBy(r => r.GetType().Name).OrderByDescending(g => g.Count()))
        {
            int visible = g.Count(r => r.isVisible);
            var shaders = g.SelectMany(r => r.sharedMaterials).Where(m => m != null && m.shader != null)
                .GroupBy(m => m.shader.name).OrderByDescending(s => s.Count()).Take(6)
                .Select(s => $"{s.Key}×{s.Count()}");
            sb.AppendLine($"  {g.Key} {g.Count()} 個（見えている {visible}）: {string.Join(", ", shaders)}");
            if (g.Key == nameof(MeshRenderer))
            {
                var meshes = g.Select(r => r.GetComponent<MeshFilter>()?.sharedMesh).Where(m => m != null).ToList();
                sb.AppendLine($"    メッシュ {meshes.Count}（CPU から読める {meshes.Count(m => m.isReadable)}）");
            }
            if (g.Key == nameof(SkinnedMeshRenderer))
            {
                var meshes = g.Cast<SkinnedMeshRenderer>().Select(r => r.sharedMesh).Where(m => m != null).ToList();
                sb.AppendLine($"    メッシュ {meshes.Count}（CPU から読める {meshes.Count(m => m.isReadable)}）");
            }
        }
        sb.Append($"  光源 {Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Length} 個: " +
                  string.Join(", ", Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Take(8).Select(l => $"{l.name}({l.type}, {l.intensity:0.##})")));
        log.LogInfo(sb.ToString());
    }
}
