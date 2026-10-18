import json, sys
for d in sys.argv[1:]:
    a=json.load(open(d+'/metrics.json'))
    for m in a['Modes']:
        f=[x['Diagnostics'] for x in m['Frames']]
        ch=[x['LightMatrixChanged'] for x in f]; rr=[x['ShadowRendered'] for x in f]
        print(d.split('/')[-1], m['Name'], 'LightMatrixChanged', sum(ch), '/', len(ch), 'Rendered', sum(rr), 'sun', f[0]['SunDirection'], f[-1]['SunDirection'])
    print('  diag keys', list(f[0].keys()))
