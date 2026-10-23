"""Retain all normal pixels losslessly in compact replay archives and make owner panels.

Usage: package_captures.py CAPTURE_ROOT EVIDENCE_DIRECTORY
"""
import hashlib
import json
import lzma
from pathlib import Path
import shutil
import sys
import numpy as np
from PIL import Image, ImageDraw
from analyze_captures import BUILDS, CONDITIONS, MODES, guard


def main():
    root, out = map(Path, sys.argv[1:3])
    guard()
    look = out/'owner-look'
    look.mkdir(parents=True, exist_ok=True)
    for build in BUILDS:
        for condition in CONDITIONS:
            run = root/build/condition
            dest = out/'captures'/build/condition
            guard()
            dest.mkdir(parents=True, exist_ok=True)
            meta = json.loads((run/'analysis-provenance.json').read_text())
            metrics = json.loads((run/'metrics.json').read_text())
            # The only normalized field is an output location, never a measurement or renderer setting.
            metrics['Options']['Output'] = str(dest.relative_to(out))
            (dest/'metrics.json').write_text(json.dumps(metrics, indent=2)+'\n')
            shutil.copy2(run/'metrics.txt',dest/'metrics.txt')
            shutil.copy2(run/'analysis-provenance.json',dest/'analysis-provenance.json')
            shutil.copy2(run/'analysis-regions.bin.gz',dest/'analysis-regions.bin.gz')
            sheets = [np.asarray(Image.open(run/f'contact-sheet-{i:03d}.png').convert('RGB')) for i in range(16)]
            raw = np.stack(sheets).tobytes()
            guard()
            (dest/'analysis-frames.rgb.xz').write_bytes(lzma.compress(raw, preset=6))
            if condition == 'back15':
                shutil.copy2(run/'contact-sheet-015.png', look/f'{build}-back15-contact-sheet-015.png')
            if condition in ('back15','ground15'):
                region = 'character' if condition == 'back15' else 'ground'
                if condition == 'back15':
                    # Preserve the archive's fixed character window and nearest-neighbour 4x sampling.
                    sheet = Image.fromarray(sheets[-1])
                    panels = [sheet.crop((j*meta['Width']+144,200,j*meta['Width']+240,296)).resize((384,384),Image.Resampling.NEAREST) for j in range(3)]
                else:
                    edge = Image.open(run/'edge-ground-015-4x.png').convert('RGB')
                    panels = [edge.crop((j*384,0,(j+1)*384,384)) for j in range(3)]
                combined = Image.new('RGB',(1152,408),'#202020')
                draw = ImageDraw.Draw(combined)
                for j,(mode,panel) in enumerate(zip(MODES,panels)):
                    guard()
                    panel.save(look/f'{build}-{condition}-{region}-{mode}-4x.png')
                    combined.paste(panel,(384*j,24))
                    draw.text((384*j+8,6),mode,fill='white')
                guard()
                combined.save(look/f'{build}-{condition}-{region}-all-modes-4x.png')
            print(build,condition,len(raw),'RGB bytes retained, sha256',hashlib.sha256(raw).hexdigest(),flush=True)

    guard()
    comparisons = look/'comparisons'
    comparisons.mkdir(exist_ok=True)
    for condition in CONDITIONS:
        for region in ('character', 'ground'):
            sheet = Image.new('RGB', (1152, 816), '#202020')
            draw = ImageDraw.Draw(sheet)
            for row, build in enumerate(('control', 'final')):
                im = Image.open(root/build/condition/f'edge-{region}-015-4x.png').convert('RGB')
                sheet.paste(im, (0, row*408+24))
                for col, mode in enumerate(MODES):
                    draw.text((col*384+8,row*408+6), build+' '+mode, fill='white')
            guard()
            sheet.save(comparisons/f'{condition}-{region}-control-vs-final-4x.png')


if __name__ == '__main__':
    main()
