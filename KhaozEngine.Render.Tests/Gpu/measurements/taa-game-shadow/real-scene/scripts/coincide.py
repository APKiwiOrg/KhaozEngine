import sys
import numpy as np
from PIL import Image
D=sys.argv[1]
def img(p): return np.asarray(Image.open(p).convert('RGB')).astype(np.int32)
def luma(a): return (0.2126*a[...,0]+0.7152*a[...,1]+0.0722*a[...,2])/255.0
def med3(l):
    p=np.pad(l,1,mode='edge'); s=np.stack([p[y:y+l.shape[0],x:x+l.shape[1]] for y in range(3) for x in range(3)]); return np.median(s,axis=0)
for mode in ['taa-native','taa-quality']:
    tot=dict(band=0,rel_band=0,spark=0,spark_rel=0,spark_rel_prev=0,rel_spark=0,nonmag_diff=0)
    prev_rel=None
    for i in range(16):
        reg=img(f'{D}/baseline/regions-{i:03d}.png')
        band=(reg[...,0]==0)&(reg[...,1]==255)&(reg[...,2]==0)
        base=img(f'{D}/noop/{mode}/crop-{i:03d}.png')
        sr=img(f'{D}/show-released/{mode}/crop-{i:03d}.png')
        fx=img(f'{D}/baseline/fxaa/crop-{i:03d}.png')
        mag=(sr[...,0]==255)&(sr[...,1]==0)&(sr[...,2]==255)&~((base[...,0]==255)&(base[...,1]==0)&(base[...,2]==255))
        tot['nonmag_diff']+=int((np.abs(sr-base).max(axis=2)>0)[~mag].sum())
        lb=luma(base); res=lb-med3(lb)
        spark=band&(res>0.05)&(lb-luma(fx)>0.05)
        # grow released by 1 px to allow for the overlay sitting next to the dot
        relg=mag.copy(); relg[1:]|=mag[:-1]; relg[:-1]|=mag[1:]; relg[:,1:]|=mag[:,:-1]; relg[:,:-1]|=mag[:,1:]
        tot['band']+=int(band.sum()); tot['rel_band']+=int((band&mag).sum())
        tot['spark']+=int(spark.sum()); tot['spark_rel']+=int((spark&mag).sum()); tot['rel_spark']+=int((spark&relg).sum())
        if prev_rel is not None: tot['spark_rel_prev']+=int((spark&prev_rel).sum())
        prev_rel=mag
    print(mode, tot,
          'released share of band %.3f' % (tot['rel_band']/max(tot['band'],1)),
          'sparkles released same frame %.3f' % (tot['spark_rel']/max(tot['spark'],1)),
          'within 1px %.3f' % (tot['rel_spark']/max(tot['spark'],1)),
          'released previous frame %.3f' % (tot['spark_rel_prev']/max(tot['spark'],1)))
