#!/bin/sh
# Asks the running mod for an in-game screenshot and prints its path (dev helper).
. "$(dirname "$0")/local.env" || exit 1  # HBG_DIR (the game folder): copy local.env.example to local.env
D="$HBG_DIR/BepInEx/plugins/HeberekeBunnyGardenRemix"
before=$(ls "$D/shots" 2>/dev/null | wc -l)
touch "$D/capture.req"
for i in $(seq 1 40); do
  sleep 0.5
  now=$(ls "$D/shots" 2>/dev/null | wc -l)
  [ "$now" -gt "$before" ] && { sleep 1; ls -t "$D/shots"/*.png | head -1; exit 0; }
done
echo "no screenshot (is the game running with the mod?)" >&2; exit 1
