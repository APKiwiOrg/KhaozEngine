import sys
import numpy as np
from PIL import Image
a_dir, b_dir = sys.argv[1], sys.argv[2]
for mode in ['taa-native','taa-quality','fxaa']:
    tot=0; mx=0; first=None
    for i in range(16):
        a=np.asarray(Image.open(f'{a_dir}/{mode}/frame-{i:03d}.png').convert('RGB')).astype(int)
        b=np.asarray(Image.open(f'{b_dir}/{mode}/frame-{i:03d}.png').convert('RGB')).astype(int)
        d=np.abs(a-b).max(axis=2)
        n=int((d>0).sum()); tot+=n; mx=max(mx,int(d.max()))
        if n and first is None: first=i
    print(mode, 'differing pixels over 16 frames', tot, 'max channel diff', mx, 'first differing frame', first)
