#!/bin/sh
# Shoots the same view several times, applying one set of mod.* lines before each shot, and tiles a crop of each (dev helper).
# Usage: variants.sh out.jpg "x,y,w,h (fractions of the frame)" 'line;line' 'line;line' ...   (an empty set = no change)
D="$(dirname "$0")"
OUT="$1"; BOX="$2"; shift 2
FILES=""
for set in "$@"; do
  if [ -n "$set" ]; then
    echo "$set" | tr ';' '\n' | while read -r line; do [ -n "$line" ] && sh "$D/remixconf.sh" "$line"; sleep 0.3; done
  fi
  sleep 3
  F=$(sh "$D/shot.sh") || exit 1
  FILES="$FILES|$(cygpath -w "$F")"
done
python -I -c "
from PIL import Image; import sys
fs=[f for f in sys.argv[3].split('|') if f]
x,y,w,h=[float(v) for v in sys.argv[2].split(',')]
W,H=Image.open(fs[0]).size
box=(int(W*x),int(H*y),int(W*(x+w)),int(H*(y+h)))
cw,ch=box[2]-box[0],box[3]-box[1]
cols=2 if len(fs)>1 else 1
o=Image.new('RGB',(cw*cols,ch*((len(fs)+cols-1)//cols)))
for i,f in enumerate(fs):
    o.paste(Image.open(f).convert('RGB').crop(box),((i%cols)*cw,(i//cols)*ch))
o.save(sys.argv[1],quality=88); print(sys.argv[1],o.size)" "$OUT" "$BOX" "$FILES"
