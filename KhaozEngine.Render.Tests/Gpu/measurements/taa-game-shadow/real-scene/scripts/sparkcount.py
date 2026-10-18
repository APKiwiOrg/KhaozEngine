import sys
import numpy as np
from PIL import Image
D=sys.argv[1]; runs=sys.argv[2:]
def img(p): return np.asarray(Image.open(p).convert('RGB')).astype(np.int32)
def luma(a): return (0.2126*a[...,0]+0.7152*a[...,1]+0.0722*a[...,2])/255.0
def med3(l):
    p=np.pad(l,1,mode='edge'); s=np.stack([p[y:y+l.shape[0],x:x+l.shape[1]] for y in range(3) for x in range(3)]); return np.median(s,axis=0)
masks=[]; fx=[]
for i in range(16):
    reg=img(f'{D}/baseline/regions-{i:03d}.png'); masks.append((reg[...,0]==0)&(reg[...,1]==255)&(reg[...,2]==0))
    fx.append(luma(img(f'{D}/baseline/fxaa/crop-{i:03d}.png')))
print('bright dots in the character band over 16 frames: residual over 3x3 median > 0.05 and > 0.05 above FXAA')
for r in runs:
    out=[]
    for mode in ['taa-native','taa-quality']:
        n=0
        for i in range(16):
            l=luma(img(f'{D}/{r}/{mode}/crop-{i:03d}.png'))
            n+=int((masks[i]&(l-med3(l)>0.05)&(l-fx[i]>0.05)).sum())
        out.append(n)
    print(f'{r:28} native {out[0]:5d}  quality {out[1]:5d}')
