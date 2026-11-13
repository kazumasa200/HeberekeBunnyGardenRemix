using System;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using GB;
using GB.InGame.Object;
using GB.InGame.Player;
using GB.MasterData;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace HeberekeBunnyGardenRemix;

/// <summary>
/// 開発用: タイトルなどから、ADV を飛ばして直接ステージに入る。ステージ選択の GoToInGameScene と同じく
/// GameState.SetStage でステージと難易度を決め、リトライと同じ道（ChangeInGame(retry: true)）で ADV を飛ばす。
/// キャラクターはセーブデータの今のキャラのまま。
/// </summary>
internal static class DebugJump
{
    /// <summary>spec は「Stage01」や「Stage03,Hard」。3 つ目に adv を付けると（「Stage01,Normal,adv」）、ADV を飛ばさずに始まりの ADV から入る。</summary>
    public static void ToStage(string spec, ManualLogSource log)
    {
        var parts = spec.Split(',');
        if (!Enum.TryParse(parts[0].Trim(), out StagesLabel stage) || stage == StagesLabel.None)
        {
            log.LogWarning($"ステージの名前が違います: {spec}（Stage01〜Stage09）");
            return;
        }
        var difficulty = parts.Length > 1 && Enum.TryParse(parts[1].Trim(), out DifficultiesLabel d) ? d : DifficultiesLabel.Normal;
        if (GBSystem.GameState == null || GBSystem.Scene == null)
        {
            log.LogWarning("ゲームの準備ができていないので、ステージに飛べません");
            return;
        }
        GBSystem.GameState.SetStage(stage, difficulty);
        bool adv = parts.Length > 2 && parts[2].Trim() == "adv";
        // リトライの道は、前のステージのパンツの取得状況（PtaRetryData）を引き継ぐ。別のステージの物を渡すと配列の外を読んで、読み込みが黙って止まった
        // （ステージは読み込まれるが無効のまま）。今の分を消し、今のステージの片付け（InGameScene.Teardown）が入れ直す分も 1 回だけ捨てる
        Patch();
        GBSystem.GameState.SetPtaRetryData(null);
        _dropRetryData = GBSystem.StageScene != null && GBSystem.StageScene.HasInGame;
        _ = GBSystem.Scene.ChangeInGame(retry: !adv);
        log.LogInfo($"{stage}（{difficulty}）に直接入ります{(adv ? "（始まりの ADV から）" : "")}");
    }

    /// <summary>
    /// 開発用（mod.goto = pants / goal）: プレイヤーを、いちばん近いまだ取っていないパンツか、ゴール（StageClearTrigger）の当たり判定へ瞬間移動させる
    /// （ゲームの PlayerActor.Teleport）。当たり判定はゲームのまま働くので、取得の演出・ゴールのあとの流れがそのまま見られる。
    /// </summary>
    public static void Goto(string what, ManualLogSource log)
    {
        var player = Object.FindFirstObjectByType<PlayerActor>();
        if (player == null) { log.LogWarning("プレイヤーがいないので、瞬間移動できません"); return; }
        var here = player.transform.position;
        // 「x, y, z」なら、その場所へ（映り込みを確かめるために、ガラスの前に立たせるなど）
        var xyz = what.Split(',');
        if (xyz.Length == 3 && xyz.All(v => float.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            var to = new Vector3(float.Parse(xyz[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xyz[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xyz[2], System.Globalization.CultureInfo.InvariantCulture));
            player.Teleport(to, null);
            log.LogInfo($"プレイヤーを {to} へ移しました");
            return;
        }
        Collider target = what.Trim() switch
        {
            "goal" => Object.FindFirstObjectByType<StageClearTrigger>()?.GetComponent<Collider>(),
            "pants" => Object.FindObjectsByType<PtaItemObject>(FindObjectsSortMode.None)
                .Where(p => !p.IsObtained && p.gameObject.activeInHierarchy && p.Collider != null)
                .OrderBy(p => (p.transform.position - here).sqrMagnitude).FirstOrDefault()?.Collider,
            _ => null,
        };
        if (target == null) { log.LogWarning($"行き先が見つかりません: {what}（pants か goal）"); return; }
        player.Teleport(target.bounds.center, null);
        log.LogInfo($"プレイヤーを {target.name}（{target.bounds.center}）へ移しました");
    }

    /// <summary>
    /// 開発用（mod.waypoints）: ステージの道順の目印（プレイヤー、パンツ、ゴール）を、プレイヤーから近い順につないで 1 行に書く（「x y z; x y z; …」）。
    /// tools/tour.sh が、この順にカメラを置いてゲームの絵と比べる。
    /// </summary>
    public static void LogWaypoints(ManualLogSource log)
    {
        var player = Object.FindFirstObjectByType<PlayerActor>();
        if (player == null) { log.LogWarning("プレイヤーがいないので、道順を書けません"); return; }
        var rest = Object.FindObjectsByType<PtaItemObject>(FindObjectsSortMode.None).Where(p => p.gameObject.activeInHierarchy).Select(p => p.transform.position).ToList();
        var goal = Object.FindFirstObjectByType<StageClearTrigger>();
        var route = new System.Collections.Generic.List<Vector3> { player.transform.position };
        while (rest.Count > 0)
        {
            var next = rest.OrderBy(p => (p - route[route.Count - 1]).sqrMagnitude).First();
            rest.Remove(next);
            route.Add(next);
        }
        if (goal != null) route.Add(goal.transform.position);
        log.LogInfo("道順: " + string.Join("; ", route.Select(p => FormattableString.Invariant($"{p.x:0.##} {p.y:0.##} {p.z:0.##}"))));
    }

    /// <summary>
    /// 開発用（mod.scene = Title / CharaSelection / StageSelection / OutfitSelection / NightRoutine / Gallery）: ゲームの場面の切り替え（Scenes.ChangeXxx）を呼ぶ。
    /// </summary>
    public static void ToScene(string name, ManualLogSource log)
    {
        var method = GBSystem.Scene?.GetType().GetMethod("Change" + name.Trim(), Type.EmptyTypes);
        if (method == null) { log.LogWarning($"場面の名前が違います: {name}（Title / CharaSelection / StageSelection / OutfitSelection / NightRoutine / Gallery）"); return; }
        method.Invoke(GBSystem.Scene, null);
        log.LogInfo($"{name} の場面へ切り替えます");
    }

    /// <summary>
    /// 開発用（mod.damage = 数 / torn）: プレイヤーにゲームのダメージの処理（PlayerActor.TakeDamage）でダメージを与える。torn は、服が破れる体力までちょうど減らす
    /// （ノックバックと、服が破れる演出のタイムラインまで本物の流れで起きる。2026-11-10 に、この演出で GPU が止まった）。
    /// </summary>
    public static void Damage(string value, ManualLogSource log)
    {
        var player = Object.FindFirstObjectByType<PlayerActor>();
        if (player == null) { log.LogWarning("プレイヤーがいないので、ダメージを与えられません"); return; }
        int damage = value.Trim() == "torn" ? Mathf.Max(player.Hp.Value - player.Param.Hp.Torn, 1) : int.TryParse(value, out var n) ? n : 0;
        player.TakeDamage(damage, -player.transform.forward * 0.5f);
        log.LogInfo($"ダメージ {damage}（体力 {player.Hp.Value} / {player.Hp.Max}、服が破れる体力 {player.Param.Hp.Torn}）");
    }

    private static readonly System.Reflection.FieldInfo AdvAutoField =
        typeof(ConversationWindow).GetField("m_isAuto", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// 開発用（mod.advauto = 1）: 会話（ConversationWindow）をオートで進める。画面に鍵がかかって操作できないときに、ステージクリアのあとのミニゲームや
    /// ADV まで進めて見るため。会話ごとに窓が作り直されるので、見回りのたびに付け直す（SceneMirror から呼ぶ）。
    /// </summary>
    public static void KeepAdvAuto()
    {
        foreach (var window in Object.FindObjectsByType<ConversationWindow>(FindObjectsSortMode.None))
            if (AdvAutoField?.GetValue(window) is false) AdvAutoField.SetValue(window, true);
    }

    private static string _pressAction;
    private static int _pressFrame = -1;
    private static bool _patched, _dropRetryData;

    private static void Patch()
    {
        if (_patched) return;
        var harmony = new Harmony("net.noeleve.HeberekeBunnyGardenRemix.dev");
        harmony.Patch(AccessTools.Method(typeof(GBInput), "isTriggered"), prefix: new HarmonyMethod(typeof(DebugJump), nameof(TriggeredPrefix)));
        harmony.Patch(AccessTools.Method(typeof(GB.Game.GameState), "SetPtaRetryData"), prefix: new HarmonyMethod(typeof(DebugJump), nameof(RetryDataPrefix)));
        _patched = true;
    }

    private static void RetryDataPrefix(ref BG.Pta.PtaSystem.PtaRetryData data)
    {
        if (_dropRetryData) data = null;
        _dropRetryData = false;
    }

    /// <summary>
    /// 開発用（mod.press = A / B / X / Y）: 次のフレームだけ、そのボタンが押されたことにする（GBInput.isTriggered に Harmony で割り込む）。
    /// 画面に鍵がかかってキーを送れないときに、「A 次へ」などを進めるため。仮のゲームパッドを足して押す形は、窓が前に無いと Input System が
    /// 受け付けず、効かなかった（2026-11-10）
    /// </summary>
    public static void Press(string button)
    {
        Patch();
        _pressAction = button.Trim().ToUpperInvariant() + "Button";
        _pressFrame = Time.frameCount + 1;
    }

    private static bool TriggeredPrefix(InputAction button, ref bool __result)
    {
        if (_pressFrame != Time.frameCount || button == null || button.name.Replace(" ", "") != _pressAction) return true;
        __result = true;
        return false;
    }
}
