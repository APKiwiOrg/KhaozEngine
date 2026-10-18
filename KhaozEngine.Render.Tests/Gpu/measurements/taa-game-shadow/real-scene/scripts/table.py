import sys, os
root=sys.argv[1]; runs=sys.argv[2:]
def load(d):
    rows={}
    for line in open(os.path.join(root,d,'metrics.txt')):
        p=line.split()
        if len(p)==7 and p[0] in ('taa-native','taa-quality','fxaa'):
            rows[(p[0],p[1])]=[float(x) for x in p[3:]]
    return rows
print(f"{'run':16} {'mode':11} | {'char mean':>9} {'char p95':>8} {'chg mean':>8} | {'grnd mean':>9} {'grnd p95':>8} {'chg mean':>8}")
for r in runs:
    rows=load(r)
    for m in ('taa-native','taa-quality','fxaa'):
        c=rows[(m,'character')]; g=rows[(m,'ground')]
        print(f"{r:16} {m:11} | {c[0]:9.6f} {c[1]:8.5f} {c[2]:8.5f} | {g[0]:9.6f} {g[1]:8.5f} {g[2]:8.5f}")
