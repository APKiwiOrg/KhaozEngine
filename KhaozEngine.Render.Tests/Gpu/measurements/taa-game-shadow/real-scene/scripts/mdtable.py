import sys, os
root=sys.argv[1]; runs=sys.argv[2:]
def load(d):
    rows={}
    for line in open(os.path.join(root,d,'metrics.txt')):
        p=line.split()
        if len(p)==7 and p[0] in ('taa-native','taa-quality','fxaa'):
            rows[(p[0],p[1])]=[float(x) for x in p[3:]]
    return rows
print('| Run | Mode | Character grain mean | Character grain p95 | Character change mean | Ground grain mean | Ground grain p95 | Ground change mean |')
print('| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |')
fx_done=False
for r in runs:
    rows=load(r)
    for m,label in (('taa-native','Native'),('taa-quality','Quality')):
        c=rows[(m,'character')]; g=rows[(m,'ground')]
        print(f'| `{r}` | {label} | {c[0]:.6f} | {c[1]:.5f} | {c[2]:.5f} | {g[0]:.6f} | {g[1]:.5f} | {g[2]:.5f} |')
rows=load(runs[0]); c=rows[('fxaa','character')]; g=rows[('fxaa','ground')]
print(f'| every run | FXAA | {c[0]:.6f} | {c[1]:.5f} | {c[2]:.5f} | {g[0]:.6f} | {g[1]:.5f} | {g[2]:.5f} |')
