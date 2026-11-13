using System.Collections.Generic;
using UnityEngine;

namespace HeberekeBunnyGardenRemix;

internal sealed partial class BakedLamps
{
    /// <summary>
    /// ライトマップのある面の三角形の木（箱の入れ子）。視線がいちばん近くで当たる三角形を探す。
    /// 前は照明ごとに 6 m 以内の三角形を総当たりしていて、高い街灯（灯具が 7 m 上）から地面まで届かなかった。作ったあとは読むだけなので、どのスレッドからでも使える。
    /// </summary>
    private sealed class Bvh
    {
        private readonly Tri[] _tris;
        private readonly Vector3[] _centers;
        private readonly Vector3[] _min, _max;
        // 葉: _count > 0、_first は最初の三角形。枝: _count = 0、_first は左の子（右は _first + 1）
        private readonly int[] _first, _count;
        private int _nodes = 1;

        public int Triangles => _tris.Length;

        public Bvh(List<Surface> surfaces)
        {
            int total = 0;
            foreach (var s in surfaces) total += s.Idx.Length / 3;
            _tris = new Tri[total];
            _centers = new Vector3[total];
            int n = 0;
            foreach (var s in surfaces)
                for (int i = 0; i + 2 < s.Idx.Length; i += 3)
                {
                    Vector3 a = s.Pos[s.Idx[i]], b = s.Pos[s.Idx[i + 1]], c = s.Pos[s.Idx[i + 2]];
                    _tris[n] = new Tri { A = a, E1 = b - a, E2 = c - a, S = s, I = i };
                    _centers[n++] = (a + b + c) / 3f;
                }
            int capacity = Mathf.Max(2 * total, 1);
            _min = new Vector3[capacity]; _max = new Vector3[capacity];
            _first = new int[capacity]; _count = new int[capacity];
            if (total > 0) Build(0, 0, total);
        }

        private void Build(int node, int start, int count)
        {
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity, cmin = min, cmax = max;
            for (int i = start; i < start + count; i++)
            {
                var t = _tris[i];
                Vector3 b = t.A + t.E1, c = t.A + t.E2;
                min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(b, c)));
                max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(b, c)));
                cmin = Vector3.Min(cmin, _centers[i]);
                cmax = Vector3.Max(cmax, _centers[i]);
            }
            _min[node] = min; _max[node] = max;
            if (count <= 4) { _first[node] = start; _count[node] = count; return; }
            var size = cmax - cmin;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            float split = (cmin[axis] + cmax[axis]) * 0.5f;
            int lo = start, hi = start + count - 1;
            while (lo <= hi)
            {
                if (_centers[lo][axis] < split) { lo++; continue; }
                (_tris[lo], _tris[hi]) = (_tris[hi], _tris[lo]);
                (_centers[lo], _centers[hi]) = (_centers[hi], _centers[lo]);
                hi--;
            }
            int left = lo - start;
            if (left == 0 || left == count) left = count / 2; // 重心が同じ所に固まっている: 数で半分に
            int child = _nodes;
            _nodes += 2;
            _first[node] = child; _count[node] = 0;
            Build(child, start, left);
            Build(child + 1, start + left, count - left);
        }

        /// <summary>
        /// o から d（長さ 1）へ、tMin より先・tMax より手前でいちばん近い当たり。stack は呼ぶ側が持つ作業用（128 個）。
        /// solidOnly: 切り抜きの素材の面（木の葉）は当たらないことにする（光を遮るかを見るとき）
        /// </summary>
        public bool Closest(Vector3 o, Vector3 d, float tMin, float tMax, int[] stack, out float t, out float u, out float v, out Tri tri, bool solidOnly = false)
        {
            t = tMax; u = v = 0f; tri = default;
            if (_tris.Length == 0) return false;
            var inv = new Vector3(1f / (Mathf.Abs(d.x) < 1e-9f ? 1e-9f : d.x), 1f / (Mathf.Abs(d.y) < 1e-9f ? 1e-9f : d.y), 1f / (Mathf.Abs(d.z) < 1e-9f ? 1e-9f : d.z));
            bool found = false;
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                int node = stack[--top];
                Vector3 a = Vector3.Scale(_min[node] - o, inv), b = Vector3.Scale(_max[node] - o, inv);
                float near = Mathf.Max(Mathf.Max(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y)), Mathf.Min(a.z, b.z));
                float far = Mathf.Min(Mathf.Min(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)), Mathf.Max(a.z, b.z));
                if (near > far || far < tMin || near > t) continue;
                if (_count[node] == 0)
                {
                    stack[top++] = _first[node];
                    stack[top++] = _first[node] + 1;
                    continue;
                }
                for (int k = _first[node]; k < _first[node] + _count[node]; k++)
                {
                    var x = _tris[k];
                    if (solidOnly && x.S.Cutout) continue;
                    var pv = Vector3.Cross(d, x.E2);
                    float det = Vector3.Dot(x.E1, pv);
                    if (det > -1e-9f && det < 1e-9f) continue;
                    float invDet = 1f / det;
                    var tv = o - x.A;
                    float hu = Vector3.Dot(tv, pv) * invDet;
                    if (hu < 0f || hu > 1f) continue;
                    var qv = Vector3.Cross(tv, x.E1);
                    float hv = Vector3.Dot(d, qv) * invDet;
                    if (hv < 0f || hu + hv > 1f) continue;
                    float ht = Vector3.Dot(x.E2, qv) * invDet;
                    if (ht <= tMin || ht >= t) continue;
                    t = ht; u = hu; v = hv; tri = x; found = true;
                }
            }
            return found;
        }

        /// <summary>from から to までの間に、遮る面があるか（両端の 5 cm は見ない）。</summary>
        public bool Blocked(Vector3 from, Vector3 to, int[] stack)
        {
            var d = to - from;
            float length = d.magnitude;
            return length > 0.1f && Closest(from, d / length, 0.05f, length - 0.05f, stack, out _, out _, out _, out _, solidOnly: true);
        }
    }
}
