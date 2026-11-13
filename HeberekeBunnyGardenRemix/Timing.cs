using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// メインスレッドの処理の所要時間を、名前ごとに足し込む（開発用。場面の読み込みで固まる所を探すため）。
/// <c>using (Timing.Measure("名前")) { … }</c> で測り、SceneMirror が 1 秒ごとに <see cref="Report"/> を呼んで、合計が大きいときだけログに書く。
/// </summary>
internal static class Timing
{
    private static readonly Dictionary<string, (long ticks, int count)> Totals = new();

    public readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _start;
        public Scope(string name) { _name = name; _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            long d = Stopwatch.GetTimestamp() - _start;
            lock (Totals) Totals[_name] = Totals.TryGetValue(_name, out var t) ? (t.ticks + d, t.count + 1) : (d, 1);
        }
    }

    public static Scope Measure(string name) => new(name);

    /// <summary>前に呼んでからの合計が minMs を超えていれば、長い順の 1 行を返す（無ければ null）。合計は消す。</summary>
    public static string Report(double minMs)
    {
        lock (Totals)
        {
            double total = Totals.Values.Sum(t => t.ticks) * 1000.0 / Stopwatch.Frequency;
            if (total < minMs) { Totals.Clear(); return null; }
            var text = string.Join("、", Totals.OrderByDescending(t => t.Value.ticks).Select(t => $"{t.Key} {t.Value.ticks * 1000.0 / Stopwatch.Frequency:0} ms（{t.Value.count} 回）"));
            Totals.Clear();
            return $"メインスレッドの仕事 {total:0} ms: {text}";
        }
    }
}
