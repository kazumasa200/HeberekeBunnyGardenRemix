#!/bin/sh
# Applies one set of mod.* lines, dumps the game's and Remix's HDR image, and prints region statistics; repeated per set (dev helper).
# Usage: hdrvariants.sh "x0 x1 y0 y1 [...]" 'tag:line;line' 'tag:line;line' ...   (tag: letters only; an empty line list = no change)
D="$(dirname "$0")"
. "$(dirname "$0")/local.env" || exit 1  # HBG_DIR (the game folder): copy local.env.example to local.env
S="$HBG_DIR/BepInEx/plugins/HeberekeBunnyGardenRemix/shots"
REG="$1"; shift
for set in "$@"; do
  tag="${set%%:*}"; lines="${set#*:}"
  echo "$lines" | tr ';' '\n' | while read -r line; do [ -n "$line" ] && sh "$D/remixconf.sh" "$line"; sleep 0.3; done
  sleep 4
  sh "$D/remixconf.sh" "mod.hdrdump = $tag"; sleep 2.5
  python -I "$D/hdrstat.py" "$S" "$tag" $REG
done
