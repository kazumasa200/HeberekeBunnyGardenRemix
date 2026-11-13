using System.Globalization;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// 開発用（mod.pick = x, y）: 画面の 1 点（左上が 0, 0、右下が 1, 1）に映っている物と、その素材の中身をログに書く。
/// 当たり判定があればそれで、無ければ視線が通る Renderer の箱の近い順に 3 つ。
/// </summary>
internal static class Picker
{
    public static void Log(Camera cam, string value, ManualLogSource log)
    {
        if (cam == null) return;
        var n = value.Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        var ray = cam.ViewportPointToRay(new Vector3(n[0], 1f - n[1], 0f));
        var sb = new StringBuilder($"── 画面の ({n[0]}, {n[1]}) に映っている物\n");
        if (Physics.Raycast(ray, out var hit, cam.farClipPlane))
        {
            sb.AppendLine($"  当たり: {Path(hit.collider.transform)}（{hit.distance:0.0} m、法線 {hit.normal}）");
            // ゲームの静的な物の明るさは「地の色 × ライトマップ」。その点の両方を読む（Remix の光の強さを合わせるため）
            var r = hit.collider is MeshCollider ? hit.collider.GetComponent<Renderer>() : null;
            if (r != null && r.lightmapIndex >= 0 && r.lightmapIndex < LightmapSettings.lightmaps.Length)
            {
                var lm = LightmapSettings.lightmaps[r.lightmapIndex].lightmapColor;
                var m = r.sharedMaterial;
                var albedo = m != null && m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null
                    ? Sample(m.GetTexture("_BaseMap"), Vector2.Scale(hit.textureCoord, m.GetTextureScale("_BaseMap")) + m.GetTextureOffset("_BaseMap")) : Color.white;
                if (m != null && m.HasProperty("_BaseColor")) albedo *= m.GetColor("_BaseColor").linear;
                sb.AppendLine($"  ライトマップ {lm.name}（{lm.graphicsFormat}）の値 {Sample(lm, hit.lightmapCoord)}、地の色（線形）{albedo}");
            }
        }
        var hits = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
            .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.bounds.size.magnitude < 500f)
            .Select(r => (r, ok: r.bounds.IntersectRay(ray, out float d), d))
            .Where(x => x.ok).OrderBy(x => x.d).Take(12);
        foreach (var (r, _, d) in hits)
        {
            sb.AppendLine($"  {r.GetType().Name} {Path(r.transform)}（箱まで {d:0.0} m）");
            foreach (var m in r.sharedMaterials.Where(m => m != null))
            {
                var colors = m.GetPropertyNames(MaterialPropertyType.Vector).Where(p => m.HasColor(p) && !p.EndsWith("_ST") && !p.EndsWith("_TexelSize") && !p.EndsWith("_HDR"))
                    .Select(p => $"{p}={m.GetColor(p)}");
                var floats = m.GetPropertyNames(MaterialPropertyType.Float).Select(p => $"{p}={m.GetFloat(p):0.###}");
                var textures = m.GetTexturePropertyNames().Where(p => m.GetTexture(p) != null).Select(p => $"{p}={m.GetTexture(p).name}");
                sb.AppendLine($"    素材 {m.name}（{m.shader.name}、順番 {m.renderQueue}、キーワード {string.Join(" ", m.shaderKeywords)}）");
                sb.AppendLine($"      色 {string.Join(", ", colors)}");
                sb.AppendLine($"      数 {string.Join(", ", floats)}");
                sb.AppendLine($"      絵 {string.Join(", ", textures)}");
            }
        }
        log.LogInfo(sb.ToString());
    }

    /// <summary>開発用（mod.lightsnear = x, y, z, 半径）: その辺りの光源を、無効な物も含めて近い順に書く。</summary>
    public static void LogLights(string value, ManualLogSource log)
    {
        var n = value.Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        var at = new Vector3(n[0], n[1], n[2]);
        var sb = new StringBuilder($"── {at} から {n[3]} m の光源\n");
        // その点のライトプローブ（ライトマップの無い静的な物と、動く物を照らす環境光）。上・横・下の値
        LightProbes.GetInterpolatedProbe(at, null, out var sh);
        var dirs = new[] { Vector3.up, Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.down };
        var v = new Color[6];
        sh.Evaluate(dirs, v);
        sb.AppendLine($"  ライトプローブ: 上 {v[0]}、横 {(v[1] + v[2] + v[3] + v[4]) * 0.25f}、下 {v[5]}");
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                     .Select(l => (l, d: Vector3.Distance(l.transform.position, at))).Where(x => x.d <= n[3]).OrderBy(x => x.d).Select(x => x.l))
        {
            var b = l.bakingOutput;
            sb.AppendLine($"  {Path(l.transform)}: {l.type} 色 {l.color} 強さ {l.intensity:0.###} 範囲 {l.range:0.#} 角度 {l.spotAngle:0}/{l.innerSpotAngle:0} 面 {l.areaSize} " +
                          $"位置 {l.transform.position} 前 {l.transform.forward} 焼き {b.lightmapBakeType}/{b.mixedLightingMode}（{b.isBaked}） 影 {l.shadows} " +
                          $"{(l.enabled && l.gameObject.activeInHierarchy ? "" : "（無効）")}");
        }
        log.LogInfo(sb.ToString());
    }

    /// <summary>開発用（mod.emissive = 1）: 光る素材（_EMISSION）を、強い順に、焼き込みに入るか（globalIlluminationFlags）と一緒に書く。</summary>
    public static void LogEmissive(ManualLogSource log)
    {
        var uses = new System.Collections.Generic.Dictionary<Material, (int count, Renderer first)>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            foreach (var m in r.sharedMaterials)
                if (m != null && m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor"))
                    uses[m] = uses.TryGetValue(m, out var u) ? (u.count + 1, u.first) : (1, r);
        var sb = new StringBuilder($"── 光る素材 {uses.Count} 個\n");
        foreach (var (m, u) in uses.OrderByDescending(x => x.Key.GetColor("_EmissionColor").maxColorComponent))
        {
            var e = m.GetColor("_EmissionColor");
            sb.AppendLine($"  {m.name}（{m.shader.name}）発光 {e.maxColorComponent:0.###} {e}、絵 {(m.HasProperty("_EmissionMap") && m.GetTexture("_EmissionMap") != null ? m.GetTexture("_EmissionMap").name : "-")}、" +
                          $"焼き込み {m.globalIlluminationFlags}、Renderer {u.count} 個（例 {Path(u.first.transform)}）");
        }
        log.LogInfo(sb.ToString());
    }

    /// <summary>開発用（mod.find = 名前の一部）: 道筋にその名前を含む Renderer と、素材の発光・ライトマップを書く（多いときは 40 個まで）。</summary>
    public static void LogFind(string part, ManualLogSource log)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"── 「{part}」を含む Renderer");
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(r => Path(r.transform).Contains(part)).Take(400))
        {
            var mats = string.Join(", ", r.sharedMaterials.Where(m => m != null).Select(m =>
                $"{m.name}{(m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor") ? $"（発光 {m.GetColor("_EmissionColor").maxColorComponent:0.##}）" : "")}"));
            sb.AppendLine($"  {Path(r.transform)}{(r.enabled && r.gameObject.activeInHierarchy ? "" : "（無効）")} ライトマップ {r.lightmapIndex} レイヤー {r.gameObject.layer} " +
                          $"影 {r.shadowCastingMode} 描かない {r.forceRenderingOff} 見えている {r.isVisible} 位置 {r.bounds.center}{Block(r)}: {mats}");
        }
        log.LogInfo(sb.ToString());
    }

    // Renderer ごとの上書き（MaterialPropertyBlock）。素材の値ではなく、こちらで描かれる
    private static string Block(Renderer r)
    {
        if (!r.HasPropertyBlock()) return "";
        var b = new MaterialPropertyBlock();
        r.GetPropertyBlock(b);
        return $" 上書き（発光 {b.GetColor("_EmissionColor")}、地の色 {b.GetColor("_BaseColor")}）";
    }

    /// <summary>
    /// 開発用（mod.overlaps）: 箱（中心と大きさ、5 cm 刻み）がぴったり重なっている Renderer の組（Remix に渡す物だけ: 有効、影だけの形と粗い LOD は除く）。
    /// 同じ形が同じ場所に 2 つあると、Remix では面がちらつく（Z-fighting）。
    /// </summary>
    public static void LogOverlaps(ManualLogSource log)
    {
        var coarse = SceneGeometry.CoarseLods();
        static Vector3Int Q(Vector3 v) => new(Mathf.RoundToInt(v.x * 20f), Mathf.RoundToInt(v.y * 20f), Mathf.RoundToInt(v.z * 20f));
        var groups = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
            .Where(r => r.enabled && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly && !coarse.Contains(r) && r.bounds.size.magnitude > 1f)
            .GroupBy(r => (Q(r.bounds.center), Q(r.bounds.size))).Where(g => g.Count() > 1).OrderByDescending(g => g.First().bounds.size.magnitude).ToList();
        var sb = new StringBuilder($"── 箱が重なっている Renderer {groups.Count} 組");
        sb.AppendLine();
        foreach (var g in groups.Take(30))
        {
            sb.AppendLine($"  箱 {g.First().bounds.center} {g.First().bounds.size}:");
            foreach (var r in g.Take(4))
                sb.AppendLine($"    {Path(r.transform)}（LOD {(r.GetComponentInParent<LODGroup>() is { } lod ? lod.name : "-")}、ライトマップ {r.lightmapIndex}）: {string.Join(", ", r.sharedMaterials.Where(m => m != null).Select(m => m.name).Take(4))}");
        }
        // 大きい物（5 m から）で、箱の重なりが 8 割を超える組（LOD の違う形が同時に出ている建物など）
        var big = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
            .Where(r => r.enabled && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly && !coarse.Contains(r) && r.bounds.size.magnitude > 5f).ToList();
        int pairs = 0;
        for (int i = 0; i < big.Count && pairs < 30; i++)
            for (int k = i + 1; k < big.Count && pairs < 30; k++)
            {
                Bounds a = big[i].bounds, b = big[k].bounds;
                var lo = Vector3.Max(a.min, b.min);
                var hi = Vector3.Min(a.max, b.max);
                if (hi.x <= lo.x || hi.y <= lo.y || hi.z <= lo.z) continue;
                float both = (hi.x - lo.x) * (hi.y - lo.y) * (hi.z - lo.z), va = a.size.x * a.size.y * a.size.z, vb = b.size.x * b.size.y * b.size.z;
                if (both < 0.8f * Mathf.Max(va, vb)) continue;
                pairs++;
                sb.AppendLine($"  ほぼ重なる: {Path(big[i].transform)}（箱 {a.size}）と {Path(big[k].transform)}（箱 {b.size}）");
            }
        log.LogInfo(sb.ToString());
    }

    /// <summary>開発用（mod.near = x, y, z, 半径）: 箱がその点から半径以内の Renderer（有効・LOD・影の扱いつき）。</summary>
    public static void LogNear(string value, ManualLogSource log)
    {
        var n = value.Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        var at = new Vector3(n[0], n[1], n[2]);
        var sb = new StringBuilder($"── {at} から {n[3]} m の Renderer");
        sb.AppendLine();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Where(r => r.bounds.SqrDistance(at) < n[3] * n[3]).Take(40))
        {
            var lod = r.GetComponentInParent<LODGroup>();
            sb.AppendLine($"  {Path(r.transform)}（{(r.enabled && r.gameObject.activeInHierarchy ? "" : "無効、")}見えている {r.isVisible}、影 {r.shadowCastingMode}、静的バッチ {r.isPartOfStaticBatch}、" +
                          $"LOD {(lod != null ? lod.name : "-")}、箱 {r.bounds.center} {r.bounds.size}）: {string.Join(", ", r.sharedMaterials.Where(m => m != null).Select(m => m.name))}");
        }
        log.LogInfo(sb.ToString());
    }

    /// <summary>開発用（mod.matinfo = 名前の一部）: 名前にそれを含む素材（場面の Renderer が使っている物）の、シェーダー・描く順番・キーワード・数の欄を全部書く。</summary>
    public static void LogMaterials(string part, ManualLogSource log)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"── 「{part}」を含む素材");
        var seen = new System.Collections.Generic.HashSet<Material>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || !m.name.Contains(part) || !seen.Add(m)) continue;
                var floats = m.GetPropertyNames(MaterialPropertyType.Float).Select(p => $"{p}={m.GetFloat(p):0.###}");
                var colors = m.GetPropertyNames(MaterialPropertyType.Vector).Where(p => m.HasColor(p) && !p.EndsWith("_ST") && !p.EndsWith("_TexelSize") && !p.EndsWith("_HDR"))
                    .Select(p => $"{p}={m.GetColor(p)}");
                var textures = m.GetTexturePropertyNames().Where(p => m.GetTexture(p) != null)
                    .Select(p => $"{p}={m.GetTexture(p).name}（{m.GetTexture(p).width}x{m.GetTexture(p).height}、繰り返し {m.GetTextureScale(p)} {m.GetTextureOffset(p)}）");
                sb.AppendLine($"  {m.name}（{m.shader.name}、順番 {m.renderQueue}、パス {m.passCount}、キーワード {string.Join(" ", m.shaderKeywords)}、" +
                              $"有効なキーワード {string.Join(" ", m.enabledKeywords.Select(k => k.name))}、GI {m.globalIlluminationFlags}）例 {Path(r.transform)}");
                sb.AppendLine($"    数 {string.Join(", ", floats)}");
                sb.AppendLine($"    色 {string.Join(", ", colors)}");
                sb.AppendLine($"    絵 {string.Join(", ", textures)}");
            }
        log.LogInfo(sb.ToString());
    }

    /// <summary>
    /// 開発用（mod.matset = 素材の名前の一部, 変更, 変更…）: ゲームの素材そのものを書き換える（ゲーム自身の絵のどの項が見た目を作っているかを切り分けるため。場面を読み直すと戻る）。
    /// 変更は「_欄=数」「+キーワード」「-キーワード」。
    /// </summary>
    public static void SetMaterial(string spec, ManualLogSource log)
    {
        var parts = spec.Split(',').Select(x => x.Trim()).ToArray();
        var seen = new System.Collections.Generic.HashSet<Material>();
        foreach (var m in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).SelectMany(r => r.sharedMaterials))
        {
            if (m == null || !m.name.Contains(parts[0]) || !seen.Add(m)) continue;
            foreach (var change in parts.Skip(1))
            {
                if (change.StartsWith("+")) m.EnableKeyword(change.Substring(1));
                else if (change.StartsWith("-")) m.DisableKeyword(change.Substring(1));
                else if (change.Split('=') is { Length: 2 } kv) m.SetFloat(kv[0].Trim(), float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        log.LogInfo($"素材 {string.Join("、", seen.Select(m => m.name))} を書き換えました: {string.Join(", ", parts.Skip(1))}");
    }

    /// <summary>開発用（mod.texprobe = 素材の名前の一部）: その素材の絵（_MainTexture・_BaseMap・_MainTex）を、隅と真ん中で読む。形がアルファと色のどちらにあるかを見るため。</summary>
    public static void LogTextureProbe(string part, ManualLogSource log)
    {
        var m = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).SelectMany(r => r.sharedMaterials).FirstOrDefault(x => x != null && x.name.Contains(part));
        if (m == null) { log.LogInfo($"素材 {part} が見つかりません"); return; }
        foreach (var name in new[] { "_MainTexture", "_BaseMap", "_MainTex", "_MetallicGlossMap", "_BumpMap", "_DetailNormalMap", "_DetailAlbedoMap", "_EmissionMap" })
        {
            if (!m.HasProperty(name) || m.GetTexture(name) is not Texture t) continue;
            var points = new[] { new Vector2(0.02f, 0.02f), new Vector2(0.25f, 0.25f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.3f) };
            log.LogInfo($"── {m.name} の {name}（{t.name}、{t.graphicsFormat}、{t.width}x{t.height}、いちばん明るい画素 {Brightest(t)}）: " + string.Join("、", points.Select(p => $"{p} = {Sample(t, p)}")));
        }
    }

    /// <summary>開発用（mod.texdump = 素材の名前の一部, 欄の名前）: その素材の絵を、見たままの色（sRGB）の PNG にして shots\ に書く。</summary>
    public static void DumpTexture(string spec, string dir, ManualLogSource log)
    {
        var parts = spec.Split(',');
        var m = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).SelectMany(r => r.sharedMaterials).FirstOrDefault(x => x != null && x.name.Contains(parts[0].Trim()));
        var name = parts.Length > 1 ? parts[1].Trim() : "_BaseMap";
        if (m == null || !m.HasProperty(name) || m.GetTexture(name) is not Texture tex) { log.LogInfo($"絵が見つかりません: {spec}"); return; }
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(tex, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var t = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, false);
        t.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var path = System.IO.Path.Combine(dir, $"tex_{tex.name}.png");
        System.IO.File.WriteAllBytes(path, t.EncodeToPNG());
        Object.Destroy(t);
        log.LogInfo($"絵を書きました: {path}");
    }

    /// <summary>
    /// 開発用（mod.shaders = 1）: 場面の Renderer が使うシェーダーを、数・描く順番・パスの LightMode・Remix での扱いと一緒に書く。
    /// URP の Renderer Feature（デカールなど）と DecalProjector の数も書く。ゲームが描き直さない層（デカール・別のパスの物）を探すため。
    /// </summary>
    public static void LogShaders(ManualLogSource log)
    {
        var sb = new StringBuilder("── 場面のシェーダー");
        sb.AppendLine();
        var mats = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).SelectMany(r => r.sharedMaterials.Select(m => (r, m))).Where(x => x.m != null && x.m.shader != null);
        foreach (var g in mats.GroupBy(x => x.m.shader).OrderByDescending(g => g.Count()))
        {
            var sh = g.Key;
            var passes = Enumerable.Range(0, sh.passCount).Select(i => sh.FindPassTagValue(i, new UnityEngine.Rendering.ShaderTagId("LightMode")).name).Distinct();
            var queues = string.Join("/", g.Select(x => x.m.renderQueue).Distinct().OrderBy(q => q).Take(6));
            var kinds = g.Select(x => x.r.GetType().Name).Distinct();
            var m0 = g.First().m;
            sb.AppendLine($"  {sh.name}: {g.Count()}（{string.Join(",", kinds)}）順番 {queues}、パス {string.Join(" ", passes)}、" +
                          $"環境 {MaterialLibrary.IsEnvironment(m0)} 半透明 {MaterialLibrary.IsTransparent(m0)}、例 {m0.name} {Path(g.First().r.transform)}");
        }
        var probes = Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None);
        sb.AppendLine($"  映り込み: reflectionIntensity {RenderSettings.reflectionIntensity}、defaultReflectionMode {RenderSettings.defaultReflectionMode}、" +
                      $"ReflectionProbe {probes.Length} 個（強さ {string.Join("/", probes.Select(p => p.intensity.ToString("0.##")).Distinct().Take(8))}、例 {string.Join(", ", probes.Take(3).Select(p => $"{p.name} {p.mode} 箱 {p.bounds.size}"))}）");
        if (RenderSettings.customReflectionTexture is { } env)
        {
            sb.AppendLine($"  映り込みの絵: {env.name}（{env.GetType().Name}、{env.width}、{env.graphicsFormat}、ミップ {env.mipmapCount}）。面ごとの平均は、このあとの行");
            MaterialLibrary.ReflectionAverage(env, faces => log.LogInfo("  映り込みの絵の面ごとの平均（+x -x +y -y +z -z）: " + string.Join("、", faces.Select(c => $"({c.r:0.###}, {c.g:0.###}, {c.b:0.###})"))));
            foreach (var probe in probes.Take(3))
                if (probe.texture != null)
                {
                    var name = probe.name;
                    MaterialLibrary.ReflectionAverage(probe.texture, faces => log.LogInfo($"  反射プローブ {name} の面ごとの平均: " + string.Join("、", faces.Select(c => $"({c.r:0.###}, {c.g:0.###}, {c.b:0.###})"))));
                }
        }
        // 静的な物の半透明の素材（Remix に渡していない物）
        foreach (var g in mats.Where(x => x.r is MeshRenderer && MaterialLibrary.IsTransparent(x.m)).GroupBy(x => x.m).OrderByDescending(g => g.Count()).Take(30))
        {
            var m = g.Key;
            float F(string n) => m.HasProperty(n) ? m.GetFloat(n) : -1f;
            sb.AppendLine($"  半透明 {m.name}（{m.shader.name}、順番 {m.renderQueue}、_Surface {F("_Surface")} _Blend {F("_Blend")} _Smoothness {F("_Smoothness")} _Metallic {F("_Metallic")}、" +
                          $"色 {(m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString() : "-")}）×{g.Count()}、例 {Path(g.First().r.transform)}");
        }
        foreach (var d in Object.FindObjectsByType<UnityEngine.Rendering.Universal.DecalProjector>(FindObjectsSortMode.None))
        {
            var m = d.material;
            var passes = m != null ? string.Join(" ", Enumerable.Range(0, m.shader.passCount).Select(i => m.shader.FindPassTagValue(i, new UnityEngine.Rendering.ShaderTagId("LightMode")).name)) : "-";
            sb.AppendLine($"  DecalProjector {Path(d.transform)}: 素材 {m?.name}（{m?.shader.name}、パス {passes}）大きさ {d.size} 位置 {d.transform.position}、有効 {d.isActiveAndEnabled}、" +
                          $"絵 {string.Join(", ", m != null ? m.GetTexturePropertyNames().Where(t => m.GetTexture(t) != null).Select(t => $"{t}={m.GetTexture(t).name}") : new string[0])}");
        }
        if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp)
        {
            var list = typeof(UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(urp)
                as UnityEngine.Rendering.Universal.ScriptableRendererData[];
            foreach (var data in list ?? new UnityEngine.Rendering.Universal.ScriptableRendererData[0])
                if (data != null)
                {
                    sb.AppendLine($"  Renderer {data.name}: " + string.Join(", ", data.rendererFeatures.Where(f => f != null).Select(f => $"{f.GetType().Name}（{f.name}、{(f.isActive ? "有効" : "無効")}）")));
                    foreach (var f in data.rendererFeatures.OfType<UnityEngine.Rendering.Universal.DecalRendererFeature>())
                    {
                        var settings = typeof(UnityEngine.Rendering.Universal.DecalRendererFeature).GetField("m_Settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(f);
                        var fields = settings?.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                            .Select(x => $"{x.Name}={x.GetValue(settings)}");
                        sb.AppendLine($"    デカールの設定: {string.Join(", ", fields ?? new string[0])}");
                    }
                }
        }
        log.LogInfo(sb.ToString());
    }

    /// <summary>絵のいちばん明るい画素（線形の値、輝度で比べる）。256×256 に写して読む。</summary>
    private static Color Brightest(Texture tex)
    {
        var rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var t = new Texture2D(256, 256, TextureFormat.RGBAHalf, false, true);
        t.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var best = t.GetPixels().OrderByDescending(c => c.grayscale).First();
        Object.Destroy(t);
        return best;
    }

    /// <summary>絵の 1 点を、線形の値で読む（GPU で 1 画素に写して読み戻す。開発用なので待つ）。</summary>
    internal static Color Sample(Texture tex, Vector2 uv)
    {
        var rt = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt, Vector2.zero, new Vector2(uv.x - Mathf.Floor(uv.x), uv.y - Mathf.Floor(uv.y)));
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var t = new Texture2D(1, 1, TextureFormat.RGBAHalf, false, true);
        t.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
        t.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var c = t.GetPixel(0, 0);
        Object.Destroy(t);
        return c;
    }

    private static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
}
