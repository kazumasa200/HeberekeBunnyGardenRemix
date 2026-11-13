#if BIE6
using BepInEx.Unity.Mono;
#endif
using System;
using BepInEx;
using BepInEx.Configuration;
using HeberekeBunnyGardenRemix.Remix;

namespace HeberekeBunnyGardenRemix;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private ConfigEntry<bool> _enabled;

    internal static bool FourBoneSkinning;

    /// <summary>骨の数の設定を 4 本までに保つ（ゲームが画質の設定を選び直すと Unlimited に戻るので、SceneMirror が 1 秒ごとに呼ぶ）。</summary>
    internal static void KeepFourBones()
    {
        if (FourBoneSkinning && UnityEngine.QualitySettings.skinWeights == UnityEngine.SkinWeights.Unlimited)
            UnityEngine.QualitySettings.skinWeights = UnityEngine.SkinWeights.FourBones;
    }

    /// <summary>
    /// フルスクリーンの解像度を、モニターの解像度までに抑える。Remix のランタイム（DXVK）のフルスクリーンは、モニターより大きい絵を作らない。
    /// HeberekeBunnyGardenMod の解像度の設定がモニターより大きい（7680x4320 など）と、ゲームの毎フレームの見直し（GBSystem.UpdateResolution）が
    /// 「まだ 7680 になっていない」とフルスクリーンに戻し続け、ゲーム内の設定でウィンドウ表示に切り替えられなくなった（2026-11-12）
    /// </summary>
    private static void ClampFullScreen(ref (int width, int height, bool adjustByWidth) __result)
    {
        var monitor = UnityEngine.Screen.currentResolution;
        float scale = Math.Min((float)monitor.width / __result.width, (float)monitor.height / __result.height);
        if (scale < 1f) __result = ((int)Math.Round(__result.width * scale), (int)Math.Round(__result.height * scale), __result.adjustByWidth);
    }

    private void Awake()
    {
        _enabled = Config.Bind("General", "Enabled", true, "RTX Remix で描く。変更は再起動後に反映されます。");
        if (!_enabled.Value) return;

        new HarmonyLib.Harmony("net.noeleve.HeberekeBunnyGardenRemix").Patch(
            HarmonyLib.AccessTools.Method(typeof(GB.GBSystem), "CalcFullScreenResolution"),
            postfix: new HarmonyLib.HarmonyMethod(typeof(Plugin), nameof(ClampFullScreen)));

        RemixApi api;
        try
        {
            // ランタイムは Remix Plus の .trex の中身を、ゲームの exe と同じフォルダに置いたもの
            api = RemixApi.Load(Paths.GameRootPath);
        }
        catch (Exception e)
        {
            Logger.LogError($"RTX Remix を読み込めませんでした: {e}");
            return;
        }
        Logger.LogInfo($"RTX Remix の関数表を受け取りました（{Paths.GameRootPath}）");

        // GPU の停止の対策。Remix の D3D11（DXVK）の上で、Unity の GPU スキニングのコンピュートシェーダー（unity default resources の inVertices / inMatrices / outVertices）
        // の実行中に GPU が 20 秒進まず落ちた（2026-11-10 服が破れる演出、11-11 衣装選択からステージ選択へ。Aftermath: active compute shader CS_9f677486…）。
        // 止まった版は、骨の数が無制限（SkinWeights.Unlimited）のときの、頂点ごとの骨の数だけ回るループを持つ。骨 4 本までにすると、ループの無い版が使われる
        var fixedBones = Config.Bind("Compat", "FourBoneSkinning", true,
            "キャラクターのスキニングを 1 頂点あたり骨 4 本までにする（GPU が止まって落ちるのを防ぐ）。変更は再起動後に反映されます。");
        Logger.LogInfo($"スキニングの骨の数: {UnityEngine.QualitySettings.skinWeights}{(fixedBones.Value && UnityEngine.QualitySettings.skinWeights == UnityEngine.SkinWeights.Unlimited ? " → FourBones にします" : "")}");
        FourBoneSkinning = fixedBones.Value;
        KeepFourBones();
        gameObject.AddComponent<SceneMirror>().Init(api, Logger);
    }
}
