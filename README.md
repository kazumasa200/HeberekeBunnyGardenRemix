# HeberekeBunnyGardenRemix

へべれけばにーがーでん（Merry Bunny Garden）を RTX Remix で描く BepInEx 5 / 6 用の Mod（開発中）。
環境だけをレイトレにする。ステージの街（不透明な静的メッシュ・テクスチャ・ノーマルマップ・粗さ・発光・光源・反射するガラス）とゲームの空と霧を Remix Plus で描き、
その上にゲーム自身が、キャラクター（トゥーンのまま）とアイテムを描き直し、空・デカール（濡れた路面の汚れ）・パーティクル・半透明をいつもどおり描く。
キャラクターは Remix では影だけを落とす。焼き込み（ライトマップ）にだけある照明と看板の明かりは、ライトマップを読んで Remix の光に戻す。
最後にゲームの UI とポストプロセス（色調補正・ブルーム）が乗る。明るさは、ステージごとにライトマップから空の光と照明を読み取って合わせる。Stage01〜09 で、道順に沿ってゲームの HDR と比べてある（視点ごとの中央値でおよそ ±0.5 段以内）。

## 動かし方（開発用）

1. `dotnet build -c Release`（BepInEx 6 なら `-c Release_BIE6`）
2. `HeberekeBunnyGardenRemix/bin/Release/netstandard2.1/net.noeleve.HeberekeBunnyGardenRemix.dll` を
   `BepInEx/plugins/HeberekeBunnyGardenRemix/` に置く
3. [Remix Plus v2.0.0](https://github.com/RemixProjGroup/dxvk-remix) の zip の `game folder\.trex` の**中身**を、
   ゲームの exe と同じフォルダに置く（`d3d9.dll` ほか）
4. `native/forwarders/build.cmd` でビルドした `d3d11.dll` と `dxgi.dll` も、同じフォルダに置く。
   ゲームの D3D11 が Remix の DXVK の上で動くようになる
5. [docs/rtx.conf](docs/rtx.conf) をゲームのフォルダに置く（Remix の入力用の窓を止める、夜の空と露出）
6. Steam から起動する（exe を直接起動すると Steam API の初期化に失敗して進まない）

Remix のメニューは Alt+X。設定（`rtx.conf`）とログ（`rtx-remix\logs\remix-dxvk.log`）はゲームのフォルダに置かれる。
外すときは、ゲームのフォルダの `remixplus-installed.txt` に並んだファイルを消す。

設計は [docs/design.md](docs/design.md)。

## キー（開発用）

- F8: Remix の絵とゲームの絵を切り替える
- Ctrl+F8: ADV を飛ばして Stage01 に直接入る
- F7: 今の場面が何で描かれているか（カメラ・Canvas・Renderer・光源）をログに書く

## RTX Remix Toolkit で編集する

メッシュと素材の hash は、起動のたびに同じ値になる（アセットの名前と中身から作る）。Remix の実行時メニューでキャプチャを取り、Toolkit で開いて置き換え（`rtx-remix\mods\<名前>\mod.usda`）を作れば、次の起動でも同じメッシュに当たる見込み。

- 実際に Toolkit で置き換えを作って当たるかは、まだ確かめていない
- 光源は Mod が Remix の API で作っているので、Toolkit の置き換えの対象にならない見込み
- 照明の当てはめの結果は `BepInEx\cache\HeberekeBunnyGardenRemix\lamps_<場面>.txt` に保存される。消せば、次にその場面を読むときに作り直す
