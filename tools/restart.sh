#!/bin/sh
# Force-closes the game, installs the freshly built plugin and starts the game through Steam (dev helper).
. "$(dirname "$0")/local.env" || exit 1  # HBG_DIR (the game folder): copy local.env.example to local.env
G="$HBG_DIR"
DLL="$(dirname "$0")/../HeberekeBunnyGardenRemix/bin/Release/netstandard2.1/net.noeleve.HeberekeBunnyGardenRemix.dll"
P=$(tasklist //FI "IMAGENAME eq HEBEREKE BUNNY GARDEN.exe" //FO CSV //NH | cut -d, -f2 | tr -d '"')
if [ -n "$P" ] && [ "$P" -eq "$P" ] 2>/dev/null; then
  taskkill //F //PID "$P" >/dev/null
  for i in $(seq 1 20); do tasklist //FI "PID eq $P" | grep -q "$P" || break; sleep 1; done
fi
cp "$DLL" "$G/BepInEx/plugins/HeberekeBunnyGardenRemix/" || exit 1
# 前回のログの行を今回のものと取り違えないよう、ゲームを閉じたあとに消しておく（BepInEx が作り直す）
rm -f "$G/BepInEx/LogOutput.log"
# 前の検証の HDR の生データ（1 枚 63MB）と撮った絵を捨てる。消し忘れて 88GB たまった（2026-11-12）。要る絵は別の場所に写してから
rm -f "${G:?}/BepInEx/plugins/HeberekeBunnyGardenRemix/shots"/*.bin "${G:?}/BepInEx/plugins/HeberekeBunnyGardenRemix/shots"/*.png
start "" "steam://rungameid/3575730"
for i in $(seq 1 90); do sleep 1; grep -q "RTX Remix にゲームの D3D11 デバイスを渡しました\|RTX Remix を読み込めません\|止めました" "$G/BepInEx/LogOutput.log" 2>/dev/null && break; done
sleep 3
grep -E "RTX Remix に|読み込めません|止めました" "$G/BepInEx/LogOutput.log"
powershell -NoProfile -Command '$w = New-Object -ComObject WScript.Shell; $null = $w.AppActivate((Get-Process "HEBEREKE BUNNY GARDEN").Id)'
# 引数があれば、タイトルに着いたところでそのステージへ直接入る（例: restart.sh Stage01）
if [ -n "$1" ]; then
  for i in $(seq 1 90); do sleep 1; grep -q "AdvNightEnv に入って 3 秒" "$G/BepInEx/LogOutput.log" 2>/dev/null && break; done
  sh "$(dirname "$0")/remixconf.sh" "mod.stage = $1"
  powershell -NoProfile -Command '$w = New-Object -ComObject WScript.Shell; $null = $w.AppActivate((Get-Process "HEBEREKE BUNNY GARDEN").Id)'
  for i in $(seq 1 120); do sleep 1; grep -q "\[$1\] メッシュを GPU" "$G/BepInEx/LogOutput.log" 2>/dev/null && break; done
  grep -E "直接入ります|飛べません|\[$1\] メッシュを GPU" "$G/BepInEx/LogOutput.log"
fi
