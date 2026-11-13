#!/bin/sh
# Sends "key = value" lines to the running mod (dev helper). Usage: remixconf.sh 'rtx.debugView.debugViewIdx = 23' ...
# Each call writes its own config-<time>.req, so quick successive calls do not overwrite each other.
. "$(dirname "$0")/local.env" || exit 1  # HBG_DIR (the game folder): copy local.env.example to local.env
D="$HBG_DIR/BepInEx/plugins/HeberekeBunnyGardenRemix"
N="config-$(date +%s%N).req"
printf '%s\n' "$@" > "$D/$N.tmp" && mv "$D/$N.tmp" "$D/$N"
