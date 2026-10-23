"""Add a fixed cobble-edge look window without changing the archived world-band masks.

Usage: make_ground_look.py EVIDENCE_DIRECTORY
The existing automatic world-band windows are retained alongside the cobble views.
"""
import json
import lzma
from pathlib import Path
import shutil
import sys
import numpy as np
from PIL import Image, ImageDraw
from analyze_captures import BUILDS, MODES, guard


def main():
    evidence = Path(sys.argv[1])
    look = evidence/'owner-look'
    guard()
    world = look/'world-window'
    world.mkdir(exist_ok=True)
    for old in list(look.glob('*-ground15-ground-*.png')) + [look/'comparisons/ground15-ground-control-vs-final-4x.png']:
        dest = world/old.name
        if not dest.exists():
            guard()
            shutil.copy2(old, dest)
    panels = {}
    for build in BUILDS:
        run = evidence/'captures'/build/'ground15'
        meta = json.loads((run/'analysis-provenance.json').read_text())
        w, h = meta['Width'], meta['Height']
        frames = np.frombuffer(lzma.decompress((run/'analysis-frames.rgb.xz').read_bytes()),dtype=np.uint8).reshape(16,h,w*3,3)
        source = Image.fromarray(frames[-1])
        # The archive's ground-edge transects occupy x=280..330, y=270..380.
        # This fixed window crosses the same cobble shadow edge and excludes the player and signboard.
        panels[build] = [source.crop((j*w+260,270,j*w+356,366)).resize((384,384),Image.Resampling.NEAREST) for j in range(3)]
        combined = Image.new('RGB',(1152,408),'#202020')
        draw = ImageDraw.Draw(combined)
        for j,(mode,panel) in enumerate(zip(MODES,panels[build])):
            guard()
            panel.save(look/f'{build}-ground15-ground-{mode}-4x.png')
            combined.paste(panel,(j*384,24))
            draw.text((j*384+8,6),mode,fill='white')
        guard()
        combined.save(look/f'{build}-ground15-ground-all-modes-4x.png')
    comparison = Image.new('RGB',(1152,816),'#202020')
    draw = ImageDraw.Draw(comparison)
    for row,build in enumerate(('control','final')):
        for col,(mode,panel) in enumerate(zip(MODES,panels[build])):
            comparison.paste(panel,(col*384,row*408+24))
            draw.text((col*384+8,row*408+6),build+' '+mode,fill='white')
    guard()
    comparison.save(look/'comparisons/ground15-ground-control-vs-final-4x.png')
    print('Fixed cobble window (260,270)-(356,366), frame 15. Automatic world-band windows preserved.')


if __name__ == '__main__':
    main()
