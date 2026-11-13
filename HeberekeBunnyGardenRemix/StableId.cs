using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// Remix に渡すメッシュ・素材・テクスチャの hash を、起動のたびに同じ値にする。
/// RTX Remix Toolkit の置き換え（mod.usda の mesh_XXXX / mat_XXXX）は hash で当てるので、Unity のインスタンス ID や場面を読んだ回数から作ると、
/// 次の起動で当たらない（2026-11-10 までの作り）。アセットの名前と中身（頂点の数、素材の欄の値、絵の大きさ）から作る。
/// 名前も中身も同じ物は同じ hash になるが、見た目も同じなので 1 つを使い回してよい。
/// </summary>
internal static class StableId
{
    private const ulong Offset = 1469598103934665603UL, Prime = 1099511628211UL;

    // Unity のインスタンス ID → hash の元。場面ごとに捨てる（ID は使い回される）
    private static readonly Dictionary<int, ulong> Cache = new();

    public static void Clear() => Cache.Clear();

    public static ulong Mix(ulong h, ulong x) { unchecked { h ^= x; h *= Prime; return h; } }

    public static ulong Mix(ulong h, string s)
    {
        foreach (char c in s ?? "") h = Mix(h, c);
        return Mix(h, 0xFFFF);
    }

    // 小数は 1/1000 に丸めてから（読むたびの誤差で hash が変わらないように）
    public static ulong Mix(ulong h, float f) => Mix(h, unchecked((ulong)(long)Mathf.RoundToInt(Mathf.Clamp(f, -1e6f, 1e6f) * 1000f)));

    /// <summary>種類（kind）と元の値から、Remix の取っ手にする 0 以外の 64 ビット。</summary>
    public static ulong Handle(byte kind, ulong id, ulong key = 0)
    {
        ulong h = Mix(Mix(Mix(Offset, kind), id), key);
        return h == 0 ? 1 : h;
    }

    public static ulong Of(Material m)
    {
        if (m == null) return 0;
        if (Cache.TryGetValue(m.GetInstanceID(), out var known)) return known;
        var shader = m.shader;
        ulong h = Mix(Mix(Offset, m.name.Replace(" (Instance)", "")), shader != null ? shader.name : "");
        h = Mix(h, (ulong)m.renderQueue);
        foreach (var keyword in m.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal)) h = Mix(h, keyword);
        if (shader != null)
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                int id = shader.GetPropertyNameId(i);
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        var c = m.GetColor(id);
                        h = Mix(Mix(Mix(Mix(h, c.r), c.g), c.b), c.a);
                        break;
                    case ShaderPropertyType.Vector:
                        var v = m.GetVector(id);
                        h = Mix(Mix(Mix(Mix(h, v.x), v.y), v.z), v.w);
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        h = Mix(h, m.GetFloat(id));
                        break;
                    case ShaderPropertyType.Texture:
                        var t = m.GetTexture(id);
                        h = Mix(h, t != null ? t.name : "");
                        if (t != null)
                        {
                            Vector2 scale = m.GetTextureScale(id), offset = m.GetTextureOffset(id);
                            h = Mix(Mix(Mix(Mix(h, scale.x), scale.y), offset.x), offset.y);
                        }
                        break;
                }
            }
        return Cache[m.GetInstanceID()] = h;
    }

    public static ulong Of(Mesh mesh)
    {
        if (mesh == null) return 0;
        if (Cache.TryGetValue(mesh.GetInstanceID(), out var known)) return known;
        var b = mesh.bounds;
        ulong h = Mix(Mix(Mix(Mix(Offset, mesh.name), (ulong)mesh.vertexCount), (ulong)mesh.subMeshCount), (ulong)mesh.GetIndexCount(0));
        h = Mix(Mix(Mix(Mix(Mix(Mix(h, b.center.x), b.center.y), b.center.z), b.size.x), b.size.y), b.size.z);
        return Cache[mesh.GetInstanceID()] = h;
    }

    public static ulong Of(Texture t)
    {
        if (t == null) return 0;
        if (Cache.TryGetValue(t.GetInstanceID(), out var known)) return known;
        ulong h = Mix(Mix(Mix(Mix(Mix(Offset, t.name), (ulong)t.width), (ulong)t.height), (ulong)t.graphicsFormat), (ulong)t.mipmapCount);
        return Cache[t.GetInstanceID()] = h;
    }

    /// <summary>場面の中の Renderer（道筋と場所、1 cm 刻み）。同じ素材でも Renderer ごとに分ける物（看板・木の葉）に使う。</summary>
    public static ulong Of(Renderer r)
    {
        if (r == null) return 0;
        if (Cache.TryGetValue(r.GetInstanceID(), out var known)) return known;
        ulong h = Offset;
        for (var t = r.transform; t != null; t = t.parent) h = Mix(h, t.name);
        var p = r.bounds.center;
        h = Mix(Mix(Mix(h, (ulong)(long)Mathf.RoundToInt(p.x * 100f)), (ulong)(long)Mathf.RoundToInt(p.y * 100f)), (ulong)(long)Mathf.RoundToInt(p.z * 100f));
        return Cache[r.GetInstanceID()] = h;
    }
}
