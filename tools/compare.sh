#!/bin/sh
# Takes an in-game screenshot with Remix and one with the game's own renderer, side by side (dev helper).
# Usage: compare.sh out.jpg
D="$(dirname "$0")"
sh "$D/remixconf.sh" 'mod.remix = 1'; sleep 1; A=$(sh "$D/shot.sh") || exit 1
sh "$D/remixconf.sh" 'mod.remix = 0'; sleep 1; B=$(sh "$D/shot.sh") || exit 1
sh "$D/remixconf.sh" 'mod.remix = 1'
python -I -c "
from PIL import Image; import sys
a=Image.open(sys.argv[1]).convert('RGB').resize((960,540)); b=Image.open(sys.argv[2]).convert('RGB').resize((960,540))
o=Image.new('RGB',(960,1080)); o.paste(a,(0,0)); o.paste(b,(0,540)); o.save(sys.argv[3],quality=85)" "$A" "$B" "$1"
echo "$1 (top: Remix, bottom: game)"
