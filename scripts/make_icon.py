import struct
from pathlib import Path

# A small, original vector-like L mark. ICO includes multiple native DPI sizes.
def render(n):
    pixels = []
    for y in range(n):
        row = []
        for x in range(n):
            u, v = (x + .5) / n, (y + .5) / n
            dx, dy = max(.16-u, u-.84, 0), max(.16-v, v-.84, 0)
            inside = dx*dx + dy*dy <= .15**2
            # Original slanted L monogram for Loadout.
            a = u + .12*(v-.5)
            stem = .29 < a < .44 and .22 < v < .78
            foot = .29 < a < .72 and .63 < v < .78
            row.append((28,23,21,255) if inside and (stem or foot) else (250,145,104,255) if inside else (0,0,0,0))
        pixels.append(row)
    rgba = bytes(c for row in reversed(pixels) for r,g,b,a in row for c in (b,g,r,a))
    mask_size = ((n+31)//32)*4*n
    header = struct.pack('<IIIHHIIIIII',40,n,n*2,1,32,0,len(rgba),0,0,0,0)
    return header+rgba+bytes(mask_size)

sizes=[16,24,32,48,64,128,256]
blobs=[render(n) for n in sizes]
offset=6+16*len(sizes)
entries=[]
for n,blob in zip(sizes,blobs):
    entries.append(struct.pack('<BBBBHHII',n%256,n%256,0,0,1,32,len(blob),offset))
    offset+=len(blob)
path=Path('src/Pitstop.Windows/Assets/loadout.ico')
path.parent.mkdir(parents=True,exist_ok=True)
path.write_bytes(struct.pack('<HHH',0,1,len(sizes))+b''.join(entries)+b''.join(blobs))
