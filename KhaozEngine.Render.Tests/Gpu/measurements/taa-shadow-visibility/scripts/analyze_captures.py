"""Replay the archived real-scene definitions from lossless sheets and compressed masks.

Usage: analyze_captures.py CAPTURE_ROOT OUTPUT_DIRECTORY
The preserved archive_shadow_filter_report.py records the original definitions.
"""
import gzip
import json
import lzma
from pathlib import Path
import subprocess
import sys
import numpy as np
from PIL import Image

BUILDS = ('baseline', 'control', 'final')
CONDITIONS = ('back15', 'day30', 'back60', 'ground15')
MODES = ('taa-native', 'taa-quality', 'fxaa')
REGIONS = {'character': 1, 'ground': 2}
LUMA = np.array([0.2126, 0.7152, 0.0722])


def guard():
    root = Path(__file__).resolve().parents[5]
    assert Path(subprocess.check_output(['git', 'rev-parse', '--show-toplevel'], text=True).strip()) == root
    assert subprocess.check_output(['git', 'branch', '--show-current'], text=True).strip() == 'fix/taa-shadow-visibility'


def luma(a):
    return a @ LUMA / 255.0


def median3(a):
    p = np.pad(a, 1, mode='edge')
    return np.median(np.stack([p[y:y+a.shape[0], x:x+a.shape[1]]
                              for y in range(3) for x in range(3)]), axis=0)


def read_run(run):
    metrics = json.loads((run/'metrics.json').read_text())
    meta = json.loads((run/'analysis-provenance.json').read_text())
    w, h, n = meta['Width'], meta['Height'], meta['Frames']
    masks = np.frombuffer(gzip.decompress((run/'analysis-regions.bin.gz').read_bytes()), dtype=np.uint8).reshape(n, h, w)
    packed = run/'analysis-frames.rgb.xz'
    if packed.exists():
        sheets = np.frombuffer(lzma.decompress(packed.read_bytes()), dtype=np.uint8).reshape(n, h, w*3, 3).astype(np.int32)
    else:
        sheets = [np.asarray(Image.open(run/f'contact-sheet-{i:03d}.png').convert('RGB'), dtype=np.int32) for i in range(n)]
    assert all(s.shape == (h, w*3, 3) for s in sheets)
    assert metrics['SheetColumns'] == list(MODES)
    result = {}
    for j, mode in enumerate(MODES):
        values = [luma(s[:, j*w:(j+1)*w]) for s in sheets]
        fxaa = [luma(s[:, 2*w:3*w]) for s in sheets]
        residual = [v-median3(v) for v in values]
        result[mode] = {}
        for region, bit in REGIONS.items():
            raw = [(m & bit) != 0 for m in masks]
            interior = [m.copy() for m in raw]
            for m in interior:
                m[[0, -1], :] = False
                m[:, [0, -1]] = False
            grain = np.concatenate([np.abs(r)[m] for r, m in zip(residual, interior)])
            change = np.concatenate([np.abs(values[i]-values[i-1])[interior[i] & interior[i-1]] for i in range(1, n)])
            dots = sum(int((m & (r > 0.05) & (v-f > 0.05)).sum()) for m,r,v,f in zip(raw,residual,values,fxaa))
            row = {'grain': float(grain.mean()) if grain.size else None,
                   'p95': float(np.sort(grain)[int(np.ceil(grain.size*0.95))-1]) if grain.size else None,
                   'change': float(change.mean()) if change.size else None,
                   'dots': dots, 'band_pixels': sum(int(m.sum()) for m in raw),
                   'grain_samples': int(grain.size), 'change_samples': int(change.size),
                   'locked_share': None, 'lock_status': 'not-applicable' if mode == 'fxaa' else 'unavailable'}
            original = next(r for r in metrics['Modes'] if r['Name'] == mode)['Regions'][region]
            for key, old in [('grain','GrainMean'), ('p95','GrainP95'), ('change','ChangeMean')]:
                assert (row[key] is None and original[old] is None) or abs(row[key]-original[old]) < 1e-12, (run,mode,region,key,row[key],original[old])
            assert row['grain_samples'] == original['Pixels']
            assert row['change_samples'] == original['ChangePixels']
            result[mode][region] = row
    return result


def main():
    root, out = map(Path, sys.argv[1:3])
    report = {build: {condition: read_run(root/build/condition) for condition in CONDITIONS} for build in BUILDS}
    guard()
    out.mkdir(parents=True, exist_ok=True)
    (out/'summary.json').write_text(json.dumps(report, indent=2)+'\n')
    lines = ['| Build | Condition | Mode | Region | Grain mean | Grain p95 | Change mean | Dots | Locked share |',
             '| --- | --- | --- | --- | ---: | ---: | ---: | ---: | --- |']
    for build in BUILDS:
        for condition in CONDITIONS:
            for mode in MODES:
                for region in REGIONS:
                    r = report[build][condition][mode][region]
                    def number(v):
                        return 'n/a' if v is None else f'{v:.9f}'
                    lines.append(f'| {build} | {condition} | {mode} | {region} | {number(r["grain"])} | {number(r["p95"])} | {number(r["change"])} | {r["dots"]} | {r["lock_status"]} |')
    guard()
    (out/'tables.md').write_text('\n'.join(lines)+'\n')
    print('Replayed 12 captures, 72 build/condition/mode/region rows. Capture grain, p95 and change agree within 1e-12.')


if __name__ == '__main__':
    main()
