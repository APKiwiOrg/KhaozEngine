"""Validate committed capture evidence, source identities and probe packages without rendering.

GRIMHOLLOW_PROBE may name a checkout containing the durable probe commits.
By default use the sibling Grimhollow checkout. No package restore is required.
"""
import gzip
import hashlib
import io
import json
import lzma
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

BUILDS = {'baseline': '21.5.1', 'control': '21.6.0-svctrl.1', 'final': '21.6.0-svfinal.1'}
CONDITIONS = {'back15': ('back-edge', 1/15, 5), 'day30': ('back-edge', 1/15, 30),
              'back60': ('back-edge', 1/60, 5), 'ground15': ('ground-edge', 1/15, 5)}
MODES = ('taa-native', 'taa-quality', 'fxaa')


def sha(data):
    return hashlib.sha256(data).hexdigest()


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args])


def relative(path):
    p = Path(path)
    assert not p.is_absolute() and '..' not in p.parts, f'Non-relative artifact path {path}'
    return p


def verify(path):
    engine = Path(__file__).resolve().parents[4]
    common = Path(git(engine, 'rev-parse', '--path-format=absolute', '--git-common-dir').decode().strip())
    probe = Path(os.environ.get('GRIMHOLLOW_PROBE', str(common.parent.parent/'Grimhollow')))
    data = json.loads(path.read_text())
    assert data['schema_version'] == 1
    assert data['acceptance'] == 'pending-owner-decision'
    assert set(data['builds']) == set(BUILDS)
    assert data['capture_settings'] == {'width': 2560, 'height': 1440, 'warmup': 80,
                                         'frames': 16, 'start_time': 0.49777777777777776,
                                         'backend': 'MetalNative', 'device': 'Apple M2 Max'}
    locks = data['lock_overlay']
    assert locks['status'] == 'unavailable' and locks['reason']
    assert locks['normal_overlay_pairs'] == []
    assert data['method']['dot_threshold'] == 0.05
    assert data['method']['dot_reference'] == 'own-fxaa'
    assert set(data['method']['regions']) == {'character', 'ground'}
    assert data['probe_base_commit'] == '88ac70e7d8bb748a668490f45be8437261cbcf9b'
    expected_artifacts = {str(p.relative_to(engine)) for p in Path(__file__).resolve().parent.rglob('*')
                          if p.is_file() and p.name != 'manifest.json' and '__pycache__' not in p.parts}
    assert {item['path'] for item in data['artifacts']} == expected_artifacts
    for item in data['artifacts']:
        p = relative(item['path'])
        assert sha((engine/p).read_bytes()) == item['sha256'], f'Artifact hash mismatch {p}'
    archive = data['method']
    archived = git(engine, 'show', archive['archive_commit']+':'+archive['archive_script'])
    assert archived == (Path(__file__).resolve().parent/'scripts/archive_shadow_filter_report.py').read_bytes()
    for control in ('baseline', 'control'):
        for item in data['builds'][control]['source_files']:
            if 'Lighting.cs' not in item['path']:
                assert item['sha256'] == sha(git(engine, 'show', 'v21.4.0:'+item['path']))
    summary = json.loads((engine/relative(data['summary_path'])).read_text())
    assert set(summary) == set(BUILDS)
    for build, version in BUILDS.items():
        record = data['builds'][build]
        source, commit = record['source_commit'], record['probe_commit']
        assert git(engine, 'rev-parse', source).decode().strip() == source
        assert git(engine, 'rev-parse', source+'^{tree}').decode().strip() == record['source_tree']
        subprocess.run(['git', '-C', str(probe), 'merge-base', '--is-ancestor', data['probe_base_commit'], commit], check=True)
        assert record['package_version'] == version
        assert f'<KhaozEngineVersion>{version}</KhaozEngineVersion>'.encode() in git(probe, 'show', commit+':Directory.Build.props')
        tools = json.loads(git(probe, 'show', commit+':.config/dotnet-tools.json'))['tools']
        assert tools['khaozengine.tileedit.tool']['version'] == version
        assert tools['khaozengine.sfx.tool']['version'] == version
        for source_file in record['source_files']:
            blob = git(engine, 'show', source+':'+str(relative(source_file['path'])))
            assert sha(blob) == source_file['sha256']
        assert len(record['packages']) == 222
        listed = {str(relative(p['path'])) for p in record['packages']}
        actual = set(git(probe, 'ls-tree', '-r', '--name-only', commit, 'vendor/khaozengine').decode().splitlines())
        actual = {p for p in actual if p.endswith(('.nupkg', '.snupkg'))}
        assert listed == actual
        renderer = None
        for package in record['packages']:
            blob = git(probe, 'show', commit+':'+package['path'])
            assert sha(blob) == package['sha256'], f'Package hash mismatch {package["path"]}'
            assert package['path'].endswith(('.'+version+'.nupkg', '.'+version+'.snupkg'))
            if 'renderer_member' in package:
                with zipfile.ZipFile(io.BytesIO(blob)) as z:
                    renderer = sha(z.read(package['renderer_member']))
                    assert renderer == package['renderer_sha256'] == record['loaded_renderer_sha256'], f'Loaded renderer identity mismatch {build}'
                    nuspec = ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
                    repository = next(n for n in nuspec.iter() if n.tag.rsplit('}',1)[-1] == 'repository')
                    assert repository.attrib['commit'] == source == package['repository_commit']
        assert renderer is not None
        assert set(record['captures']) == set(CONDITIONS), f'Condition inventory mismatch {build}'
        assert set(summary[build]) == set(CONDITIONS)
        for condition, (spot, dt, day) in CONDITIONS.items():
            capture = record['captures'][condition]
            run = engine/relative(capture['path'])
            metrics = json.loads((run/'metrics.json').read_text())
            meta = json.loads((run/'analysis-provenance.json').read_text())
            opts = metrics['Options']
            assert opts['Spot']['Name'] == spot and opts['FrameSeconds'] == dt and opts['DayMinutes'] == day
            assert opts['StartTime'] == 0.49777777777777776 and opts['Warmup'] == 80 and opts['Frames'] == 16
            assert opts['Width'] == 2560 and opts['Height'] == 1440 and not opts['KeepFrames']
            assert opts['Sharpen'] is None and opts['HoldTexels'] is None and opts['TemporalResolve'] is None
            assert metrics['TemporalResolveOverride'] is None
            assert metrics['ValidRegions']
            assert metrics['SheetColumns'] == list(MODES)
            assert meta['RendererSha256'] == renderer == capture['loaded_renderer_sha256']
            assert version in meta['RendererVersion'] and source in meta['RendererVersion']
            assert meta['Frames'] == 16
            assert capture['command_exit_code'] == 0
            assert capture['deleted_intermediate_images'] == 160
            raw = gzip.decompress((run/'analysis-regions.bin.gz').read_bytes())
            assert len(raw) == meta['Width']*meta['Height']*16
            assert set(raw) <= set(range(16))
            pixels = lzma.decompress((run/'analysis-frames.rgb.xz').read_bytes())
            assert len(pixels) == meta['Width']*meta['Height']*3*3*16
            assert sha(pixels) == capture['normal_rgb_sha256']
            assert not list(run.rglob('frame-*.png')) and not list(run.rglob('crop-*.png'))
            assert set(summary[build][condition]) == set(MODES)
            assert {m['Name'] for m in metrics['Modes']} == set(MODES)
            for mode in metrics['Modes']:
                assert len(mode['Frames']) == 16
                assert all(f['Diagnostics']['Backend'] == 'MetalNative' for f in mode['Frames'])
                assert all(f['Diagnostics']['ShadowActive'] for f in mode['Frames'])
                if mode['Name'] != 'fxaa':
                    assert all(f['Diagnostics']['HistoryValid'] for f in mode['Frames'])
                assert all(f['Diagnostics']['DisplayWidth'] == 2560 and f['Diagnostics']['DisplayHeight'] == 1440 for f in mode['Frames'])
                for region in ('character', 'ground'):
                    assert set(summary[build][condition][mode['Name']]) == {'character', 'ground'}
                    row = summary[build][condition][mode['Name']][region]
                    assert row['locked_share'] is None
                    assert row['dots'] >= 0 and row['grain_samples'] > 0
                    assert row['lock_status'] == ('not-applicable' if mode['Name'] == 'fxaa' else 'unavailable')
                    if mode['Name'] == 'fxaa':
                        assert row['dots'] == 0
                    original = mode['Regions'][region]
                    for key, name in [('grain','GrainMean'), ('p95','GrainP95'), ('change','ChangeMean')]:
                        assert abs(row[key]-original[name]) < 1e-12
    print('PASS. Three source and package sets, loaded renderer hashes, 12 captures, 72 rows and all artifact hashes verified. Locks unavailable with explicit reason. No rendering performed.')


if __name__ == '__main__':
    try:
        verify(Path(sys.argv[1]))
    except (AssertionError, KeyError, ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f'FAIL. {error}', file=sys.stderr)
        sys.exit(1)
