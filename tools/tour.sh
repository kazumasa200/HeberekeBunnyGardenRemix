#!/bin/sh
# Walks the camera along the stage's waypoints (player, pants, goal: mod.waypoints) and dumps the game's and Remix's HDR image at each (dev helper).
# Usage: tour.sh name outdir [max views]   ->  outdir/name_tour.jpg (per view: Remix | game | log2 ratio heat map) and per-view stats
D="$(dirname "$0")"
. "$(dirname "$0")/local.env" || exit 1  # HBG_DIR (the game folder): copy local.env.example to local.env
G="$HBG_DIR/BepInEx"
# 読み込み中だと返事が遅れる。前の場面の道順を拾わないよう、新しい行が出るまで待つ（20 秒まで）
N=$(grep -c "道順: " "$G/LogOutput.log")
sh "$D/remixconf.sh" 'mod.waypoints = 1'
for i in $(seq 1 40); do sleep 0.5; [ "$(grep -c "道順: " "$G/LogOutput.log")" -gt "$N" ] && break; done
WP=$(grep "道順: " "$G/LogOutput.log" | tail -n +$((N+1)) | tail -1 | sed 's/.*道順: //')
[ -n "$WP" ] || { echo "no waypoints (not in a stage?)" >&2; exit 1; }
python -I "$D/tour.py" cams "$WP" "${3:-12}" | while read -r i cam; do
  sh "$D/remixconf.sh" "mod.cam = $cam"; sleep 5
  sh "$D/remixconf.sh" "mod.hdrdump = $1-$i"; sleep 2
  echo "$i: $cam"
done
sleep 2
sh "$D/remixconf.sh" 'mod.cam = off'
mkdir -p "$2"
python -I "$D/tour.py" sheet "$G/plugins/HeberekeBunnyGardenRemix/shots" "$1" "$2/$1_tour.jpg"
