#!/bin/sh
# Takes n screenshots in a row and tiles the same crop of each, to see what flickers from frame to frame (dev helper).
# Usage: burst.sh out.jpg "x,y,w,h (fractions of the frame)" [n=8]
D="$(dirname "$0")"
FILES=""
for i in $(seq 1 "${3:-8}"); do
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
cols=4
o=Image.new('RGB',(cw*cols,ch*((len(fs)+cols-1)//cols)))
for i,f in enumerate(fs):
    o.paste(Image.open(f).convert('RGB').crop(box),((i%cols)*cw,(i//cols)*ch))
if o.width>1900: o=o.resize((1900,int(o.height*1900/o.width)))
o.save(sys.argv[1],quality=90); print(sys.argv[1],o.size)" "$1" "$2" "$FILES"
