#!/bin/sh
# Shoots one view with the UI hidden, Remix and game, into out_dir/name_remix.jpg and name_game.jpg (1920 wide) (dev helper for the README).
# Usage: readmeshots.sh out_dir name 'mod.line;mod.line' ...   (the lines set up the view: mod.goto, mod.cam, mod.timescale ...)
D="$(dirname "$0")"
OUT="$1"; NAME="$2"; shift 2
sh "$D/remixconf.sh" 'mod.timescale = 1'
for set in "$@"; do echo "$set" | tr ';' '\n' | while read -r line; do [ -n "$line" ] && sh "$D/remixconf.sh" "$line"; sleep 0.5; done; done
sleep 5
sh "$D/remixconf.sh" 'mod.timescale = 0' 'mod.hideui = 1'
sleep 3
A=$(sh "$D/shot.sh") || exit 1
sh "$D/remixconf.sh" 'mod.remix = 0'
sleep 3
B=$(sh "$D/shot.sh") || exit 1
sh "$D/remixconf.sh" 'mod.remix = 1' 'mod.hideui = 0' 'mod.timescale = 1'
python -I -c "
from PIL import Image; import sys
for src, tag in ((sys.argv[1], 'remix'), (sys.argv[2], 'game')):
    a = Image.open(src).convert('RGB')
    a.resize((1920, a.height * 1920 // a.width), Image.LANCZOS).save('%s/%s_%s.jpg' % (sys.argv[3], sys.argv[4], tag), quality=88)
" "$(cygpath -w "$A")" "$(cygpath -w "$B")" "$OUT" "$NAME"
echo "$OUT/${NAME}_remix.jpg"
