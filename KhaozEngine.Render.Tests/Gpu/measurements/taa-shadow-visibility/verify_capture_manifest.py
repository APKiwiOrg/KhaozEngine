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


def verify_shipping(engine, probe, data):
    record = data['shipping_build']
    source, commit = record['source_commit'], record['probe_commit']
    version = '21.6.0-svship.1'
    assert source == '8307e93361ee66ef30b9a379d2b1eeaf0eb7501b'
    assert git(engine, 'rev-parse', source).decode().strip() == source
    assert git(engine, 'rev-parse', source+'^{tree}').decode().strip() == record['source_tree']
    assert record['package_version'] == version
    subprocess.run(['git', '-C', str(probe), 'merge-base', '--is-ancestor', data['probe_base_commit'], commit], check=True)
    assert f'<KhaozEngineVersion>{version}</KhaozEngineVersion>'.encode() in git(probe, 'show', commit+':Directory.Build.props')
    tools = json.loads(git(probe, 'show', commit+':.config/dotnet-tools.json'))['tools']
    assert tools['khaozengine.tileedit.tool']['version'] == version
    assert tools['khaozengine.sfx.tool']['version'] == version
    for item in record['source_files']:
        assert sha(git(engine, 'show', source+':'+str(relative(item['path'])))) == item['sha256']
    assert {item['path'] for item in record['source_files']} == {item['path'] for item in data['builds']['final']['source_files']}
    assert len(record['packages']) == 222
    listed = {str(relative(p['path'])) for p in record['packages']}
    actual = set(git(probe, 'ls-tree', '-r', '--name-only', commit, 'vendor/khaozengine').decode().splitlines())
    assert listed == {p for p in actual if p.endswith(('.nupkg', '.snupkg'))}
    renderer = None
    for package in record['packages']:
        blob = git(probe, 'show', commit+':'+package['path'])
        assert sha(blob) == package['sha256'], f'Shipping package hash mismatch {package["path"]}'
        assert package['path'].endswith(('.'+version+'.nupkg', '.'+version+'.snupkg'))
        if 'renderer_member' in package:
            assert package['path'] == f'vendor/khaozengine/KhaozEngine.Render3D.{version}.nupkg'
            with zipfile.ZipFile(io.BytesIO(blob)) as z:
                renderer = sha(z.read(package['renderer_member']))
                assert renderer == package['renderer_sha256'] == record['loaded_renderer_sha256'], 'Shipping loaded renderer identity mismatch'
                nuspec = ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
                repository = next(n for n in nuspec.iter() if n.tag.rsplit('}', 1)[-1] == 'repository')
                assert repository.attrib['commit'] == source == package['repository_commit']
    assert renderer is not None
    assert record['lock_overlay']['status'] == 'unavailable' and record['lock_overlay']['reason']
    assert record['lock_overlay']['normal_overlay_pairs'] == []
    required = {'back15', 'ground15'}
    assert set(record['captures']) == required, 'Shipping condition inventory mismatch'
    summary = json.loads((engine/relative(record['summary_path'])).read_text())
    reference = data['shipping_reference']
    assert reference['candidate'] == 'D'
    assert reference['probe_commit'] == 'e9e994d8017c3ec2ff98ee72a839e18bc519aa78'
    assert reference['package_version'] == '21.6.0-svtune-d.1'
    reference_package = git(probe, 'show', reference['probe_commit']+':vendor/khaozengine/KhaozEngine.Render3D.21.6.0-svtune-d.1.nupkg')
    assert sha(reference_package) == reference['renderer_package_sha256']
    with zipfile.ZipFile(io.BytesIO(reference_package)) as z:
        assert sha(z.read('lib/net10.0/KhaozEngine.Render3D.dll')) == reference['loaded_renderer_sha256']
    old = json.loads((engine/relative(reference['summary_path'])).read_text())
    assert set(summary) == set(old) == set(reference['captures']) == required
    matches = True
    pixels_match = True
    for condition in sorted(required):
        capture = record['captures'][condition]
        run = engine/relative(capture['path'])
        metrics = json.loads((run/'metrics.json').read_text())
        meta = json.loads((run/'analysis-provenance.json').read_text())
        opts = metrics['Options']
        spot, dt, day = CONDITIONS[condition]
        assert opts['Spot']['Name'] == spot and opts['FrameSeconds'] == dt and opts['DayMinutes'] == day
        assert opts['StartTime'] == 0.49777777777777776 and opts['Warmup'] == 80 and opts['Frames'] == 16
        assert opts['Width'] == 2560 and opts['Height'] == 1440 and not opts['KeepFrames']
        assert opts['Sharpen'] is None and opts['HoldTexels'] is None and opts['TemporalResolve'] is None
        assert metrics['TemporalResolveOverride'] is None and metrics['ValidRegions']
        assert metrics['SheetColumns'] == list(MODES)
        assert meta['RendererSha256'] == renderer == capture['loaded_renderer_sha256']
        assert version in meta['RendererVersion'] and source in meta['RendererVersion']
        assert meta['Frames'] == 16 and capture['command_exit_code'] == 0
        assert capture['deleted_intermediate_images'] == 160
        raw = gzip.decompress((run/'analysis-regions.bin.gz').read_bytes())
        assert len(raw) == meta['Width']*meta['Height']*16 and set(raw) <= set(range(16))
        pixels = lzma.decompress((run/'analysis-frames.rgb.xz').read_bytes())
        assert len(pixels) == meta['Width']*meta['Height']*3*3*16
        assert sha(pixels) == capture['normal_rgb_sha256']
        assert not list(run.rglob('frame-*.png')) and not list(run.rglob('crop-*.png'))
        pixels_match &= sha(pixels) == reference['captures'][condition]['normal_rgb_sha256']
        pixels_match &= sha(raw) == reference['captures'][condition]['region_bits_sha256']
        assert set(summary[condition]) == set(old[condition]) == set(MODES)
        assert len(metrics['Modes']) == 3 and {m['Name'] for m in metrics['Modes']} == set(MODES)
        for mode in metrics['Modes']:
            assert len(mode['Frames']) == 16
            assert all(f['Diagnostics']['Backend'] == 'MetalNative' for f in mode['Frames'])
            assert all(f['Diagnostics']['ShadowActive'] for f in mode['Frames'])
            if mode['Name'] != 'fxaa':
                assert all(f['Diagnostics']['HistoryValid'] for f in mode['Frames'])
            assert all(f['Diagnostics']['DisplayWidth'] == 2560 and f['Diagnostics']['DisplayHeight'] == 1440 for f in mode['Frames'])
            assert set(summary[condition][mode['Name']]) == set(old[condition][mode['Name']]) == {'character', 'ground'}
            for region in ('character', 'ground'):
                row = summary[condition][mode['Name']][region]
                assert row['locked_share'] is None and row['dots'] >= 0 and row['grain_samples'] > 0
                assert row['lock_status'] == ('not-applicable' if mode['Name'] == 'fxaa' else 'unavailable')
                if mode['Name'] == 'fxaa':
                    assert row['dots'] == 0
                original = mode['Regions'][region]
                for key, name in [('grain', 'GrainMean'), ('p95', 'GrainP95'), ('change', 'ChangeMean')]:
                    assert abs(row[key]-original[name]) < 1e-12
                assert row['grain_samples'] == original['Pixels'] and row['change_samples'] == original['ChangePixels']
                matches &= row == old[condition][mode['Name']][region]
    assert not matches or pixels_match, 'Shipping pixels or masks differ despite equal D rows'
    acceptance = data['acceptance']
    base = 'KhaozEngine.Render.Tests/Gpu/measurements/taa-shadow-visibility/'
    assert acceptance['candidate'] == 'D' and acceptance['owner_acceptance_date'] == '2026-10-19'
    assert acceptance['statement'] == 'The owner accepted D on 2026-10-19 after real-scene clips.'
    assert set(acceptance['evidence_links']) == {base+'reset-tuning.md', base+'owner-release-only.md'}
    assert all((engine/relative(p)).is_file() for p in acceptance['evidence_links'])
    assert acceptance['shipping_matches_D'] is matches
    assert acceptance['status'] == ('accepted' if matches else 'pending-owner-decision'), 'Shipping acceptance does not match D comparison'
    return matches


def verify_cost_cut(engine, probe, data):
    record = data['cost_cut_build']
    source = '8fe6c5927c6fd0fa4960bcd6dda88e2c209f8ff5'
    version = '21.8.0-svcut.1'
    commit = record['probe_commit']
    assert record['source_commit'] == source and record['package_version'] == version
    assert git(engine, 'rev-parse', source+'^{tree}').decode().strip() == record['source_tree']
    subprocess.run(['git', '-C', str(probe), 'merge-base', '--is-ancestor',
                    data['shipping_build']['probe_commit'], commit], check=True)
    assert f'<KhaozEngineVersion>{version}</KhaozEngineVersion>'.encode() in git(probe, 'show', commit+':Directory.Build.props')
    tools = json.loads(git(probe, 'show', commit+':.config/dotnet-tools.json'))['tools']
    assert tools['khaozengine.tileedit.tool']['version'] == tools['khaozengine.sfx.tool']['version'] == version
    for item in record['source_files']:
        assert sha(git(engine, 'show', source+':'+str(relative(item['path'])))) == item['sha256']
    paths = {item['path'] for item in data['shipping_build']['source_files']}
    paths.update(git(engine, 'diff', '--name-only', data['shipping_build']['source_commit'], source,
                     '--', 'KhaozEngine.Render3D').decode().splitlines())
    assert {item['path'] for item in record['source_files']} == paths
    packages = record['packages']
    assert len(packages) == 222
    actual = set(git(probe, 'ls-tree', '-r', '--name-only', commit, 'vendor/khaozengine').decode().splitlines())
    assert {str(relative(p['path'])) for p in packages} == {p for p in actual if p.endswith(('.nupkg', '.snupkg'))}
    renderer = None
    for package in packages:
        blob = git(probe, 'show', commit+':'+package['path'])
        assert sha(blob) == package['sha256'], f'7f package hash mismatch {package["path"]}'
        assert package['path'].endswith(('.'+version+'.nupkg', '.'+version+'.snupkg'))
        if 'renderer_member' in package:
            assert package['path'] == f'vendor/khaozengine/KhaozEngine.Render3D.{version}.nupkg'
            with zipfile.ZipFile(io.BytesIO(blob)) as z:
                renderer = sha(z.read(package['renderer_member']))
                assert renderer == package['renderer_sha256'] == record['loaded_renderer_sha256']
                nuspec = ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
                repository = next(n for n in nuspec.iter() if n.tag.rsplit('}', 1)[-1] == 'repository')
                assert repository.attrib['commit'] == source == package['repository_commit']
    assert renderer is not None
    summary = json.loads((engine/relative(record['summary_path'])).read_text())
    shipping = data['shipping_build']
    old = json.loads((engine/relative(shipping['summary_path'])).read_text())
    assert summary == old, '7f full-precision metric rows differ from shipping D'
    assert record['identical_to_shipping_D'] is True
    assert set(record['captures']) == set(summary) == {'back15', 'ground15'}
    assert record['lock_overlay'] == shipping['lock_overlay']
    for condition, capture in record['captures'].items():
        run = engine/relative(capture['path'])
        prior = engine/relative(shipping['captures'][condition]['path'])
        metrics = json.loads((run/'metrics.json').read_text())
        old_metrics = json.loads((prior/'metrics.json').read_text())
        assert {k:v for k,v in metrics['Options'].items() if k != 'Output'} == {k:v for k,v in old_metrics['Options'].items() if k != 'Output'}
        assert metrics['TemporalResolveOverride'] is None and metrics['ValidRegions']
        assert metrics['SheetColumns'] == list(MODES)
        meta = json.loads((run/'analysis-provenance.json').read_text())
        old_meta = json.loads((prior/'analysis-provenance.json').read_text())
        assert {k:v for k,v in meta.items() if k not in ('RendererSha256', 'RendererVersion')} == {k:v for k,v in old_meta.items() if k not in ('RendererSha256', 'RendererVersion')}
        assert meta['RendererSha256'] == renderer == capture['loaded_renderer_sha256']
        assert version in meta['RendererVersion'] and source in meta['RendererVersion']
        assert capture['command_exit_code'] == 0 and capture['deleted_intermediate_images'] == 160
        raw = gzip.decompress((run/'analysis-regions.bin.gz').read_bytes())
        pixels = lzma.decompress((run/'analysis-frames.rgb.xz').read_bytes())
        assert len(raw) == meta['Width']*meta['Height']*16
        assert len(pixels) == meta['Width']*meta['Height']*3*3*16
        assert sha(pixels) == capture['normal_rgb_sha256']
        assert sha(raw) == capture['region_bits_sha256']
        assert pixels == lzma.decompress((prior/'analysis-frames.rgb.xz').read_bytes()), '7f RGB differs from shipping D'
        assert raw == gzip.decompress((prior/'analysis-regions.bin.gz').read_bytes()), '7f region masks differ from shipping D'
        assert not list(run.rglob('frame-*.png')) and not list(run.rglob('crop-*.png'))
        assert len(metrics['Modes']) == 3 and {m['Name'] for m in metrics['Modes']} == set(MODES)
        for mode in metrics['Modes']:
            assert len(mode['Frames']) == 16
            assert all(f['Diagnostics']['Backend'] == 'MetalNative' and f['Diagnostics']['ShadowActive'] for f in mode['Frames'])
            if mode['Name'] != 'fxaa':
                assert all(f['Diagnostics']['HistoryValid'] for f in mode['Frames'])
            assert all(f['Diagnostics']['DisplayWidth'] == 2560 and f['Diagnostics']['DisplayHeight'] == 1440 for f in mode['Frames'])
            for region in ('character', 'ground'):
                row, original = summary[condition][mode['Name']][region], mode['Regions'][region]
                for key, name in [('grain', 'GrainMean'), ('p95', 'GrainP95'), ('change', 'ChangeMean')]:
                    assert abs(row[key]-original[name]) < 1e-12
                assert row['grain_samples'] == original['Pixels'] and row['change_samples'] == original['ChangePixels']
    local = json.loads((engine/relative(record['local_results_path'])).read_text())
    assert local['source_commit'] == source
    assert local['build'] == {'exit_code': 0, 'warnings': 0, 'errors': 0}
    headless, metal = local['headless'], local['metal']
    assert headless['exit_code'] == 0 and len(headless['assemblies']) == 41
    assert headless['totals'] == {'failed': 0, 'passed': 37110, 'skipped': 1484, 'total': 38594}
    assert all(r['failed'] == 0 for r in headless['assemblies'])
    assert metal['exit_code'] == metal['inventory_exit_code'] == 0
    assert metal['inventory_count'] == metal['executed'] == metal['passed'] == 1687
    assert metal['failed'] == metal['skipped'] == metal['missing'] == metal['extra'] == 0
    assert metal['skipped_names'] == []
    assert metal['filter'] == '((FullyQualifiedName~KhaozEngine.Tests.Gpu.Temporal&FullyQualifiedName!~CostPerf)|((FullyQualifiedName~Shadow|FullyQualifiedName~Golden)&FullyQualifiedName!~Perf&FullyQualifiedName!~Bench)|FullyQualifiedName~MotionTargetLifecycleGpuTests)'
    inventory = [json.loads(line) for line in (engine/relative(record['metal_inventory_path'])).read_text().splitlines()]
    assert len(inventory) == 1687 and all(r['outcome'] == 'Passed' for r in inventory)
    names = sorted(r['name'] for r in inventory)
    assert sha(('\n'.join(names)+'\n').encode()) == metal['inventory_names_sha256']
    assert all(not any(word in name.split('(')[0].rsplit('.', 1)[0].lower() for word in ('cost', 'perf', 'bench')) for name in names)
    print('PASS. 7f local gate, 222 private packages, loaded renderer, 12 exact metric rows, RGB and region-mask identity to shipping D verified.')


def verify_barrier(engine, probe, data):
    record = data['barrier_build']
    source = record['source_commit']
    version = '21.8.0-svbar.1'
    commit = record['probe_commit']
    cut = data['cost_cut_build']
    assert record['package_version'] == version
    assert git(engine, 'rev-parse', source+'^{tree}').decode().strip() == record['source_tree']
    subprocess.run(['git', '-C', str(engine), 'merge-base', '--is-ancestor', cut['source_commit'], source], check=True)
    subprocess.run(['git', '-C', str(probe), 'merge-base', '--is-ancestor', cut['probe_commit'], commit], check=True)
    assert f'<KhaozEngineVersion>{version}</KhaozEngineVersion>'.encode() in git(probe, 'show', commit+':Directory.Build.props')
    tools = json.loads(git(probe, 'show', commit+':.config/dotnet-tools.json'))['tools']
    assert tools['khaozengine.tileedit.tool']['version'] == tools['khaozengine.sfx.tool']['version'] == version
    for item in record['source_files']:
        assert sha(git(engine, 'show', source+':'+str(relative(item['path'])))) == item['sha256']
    paths = {item['path'] for item in cut['source_files']}
    paths.update(git(engine, 'diff', '--name-only', cut['source_commit'], source,
                     '--', 'KhaozEngine.Render3D').decode().splitlines())
    assert {item['path'] for item in record['source_files']} == paths
    lock = git(engine, 'show', source+':KhaozEngine.Render3D/Internal/ShaderSources.TemporalLockRelease.cs').decode()
    assert 'precise' not in lock.split('@"', 1)[1] and '0.3333333432674407958984375' in lock
    packages = record['packages']
    assert len(packages) == 222
    actual = set(git(probe, 'ls-tree', '-r', '--name-only', commit, 'vendor/khaozengine').decode().splitlines())
    assert {str(relative(p['path'])) for p in packages} == {p for p in actual if p.endswith(('.nupkg', '.snupkg'))}
    renderer = None
    for package in packages:
        blob = git(probe, 'show', commit+':'+package['path'])
        assert sha(blob) == package['sha256'], f'7g.3a package hash mismatch {package["path"]}'
        assert package['path'].endswith(('.'+version+'.nupkg', '.'+version+'.snupkg'))
        if 'renderer_member' in package:
            assert package['path'] == f'vendor/khaozengine/KhaozEngine.Render3D.{version}.nupkg'
            with zipfile.ZipFile(io.BytesIO(blob)) as z:
                renderer = sha(z.read(package['renderer_member']))
                assert renderer == package['renderer_sha256'] == record['loaded_renderer_sha256']
                nuspec = ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
                repository = next(n for n in nuspec.iter() if n.tag.rsplit('}', 1)[-1] == 'repository')
                assert repository.attrib['commit'] == source == package['repository_commit']
    assert renderer is not None and renderer != cut['loaded_renderer_sha256']
    summary = json.loads((engine/relative(record['summary_path'])).read_text())
    shipping = data['shipping_build']
    old = json.loads((engine/relative(shipping['summary_path'])).read_text())
    assert summary == old, '7g.3a full-precision metric rows differ from shipping D'
    assert record['identical_to_shipping_D'] is True
    comparison = json.loads((engine/relative(record['comparison_path'])).read_text())
    assert comparison['identical'] is True and len(comparison['all_rows']) == 12
    assert all(r['identical'] for r in comparison['all_rows'])
    assert set(record['captures']) == set(summary) == {'back15', 'ground15'}
    assert record['lock_overlay'] == shipping['lock_overlay']
    for condition, capture in record['captures'].items():
        run = engine/relative(capture['path'])
        prior = engine/relative(shipping['captures'][condition]['path'])
        metrics = json.loads((run/'metrics.json').read_text())
        old_metrics = json.loads((prior/'metrics.json').read_text())
        assert {k:v for k,v in metrics['Options'].items() if k != 'Output'} == {k:v for k,v in old_metrics['Options'].items() if k != 'Output'}
        assert metrics['TemporalResolveOverride'] is None and metrics['ValidRegions']
        assert metrics['SheetColumns'] == list(MODES)
        meta = json.loads((run/'analysis-provenance.json').read_text())
        old_meta = json.loads((prior/'analysis-provenance.json').read_text())
        assert {k:v for k,v in meta.items() if k not in ('RendererSha256', 'RendererVersion')} == {k:v for k,v in old_meta.items() if k not in ('RendererSha256', 'RendererVersion')}
        assert meta['RendererSha256'] == renderer == capture['loaded_renderer_sha256']
        assert version in meta['RendererVersion'] and source in meta['RendererVersion']
        assert capture['command_exit_code'] == 0 and capture['deleted_intermediate_images'] == 160
        raw = gzip.decompress((run/'analysis-regions.bin.gz').read_bytes())
        pixels = lzma.decompress((run/'analysis-frames.rgb.xz').read_bytes())
        assert len(raw) == meta['Width']*meta['Height']*16
        assert len(pixels) == meta['Width']*meta['Height']*3*3*16
        assert sha(pixels) == capture['normal_rgb_sha256']
        assert sha(raw) == capture['region_bits_sha256']
        assert pixels == lzma.decompress((prior/'analysis-frames.rgb.xz').read_bytes()), '7g.3a RGB differs from shipping D'
        assert raw == gzip.decompress((prior/'analysis-regions.bin.gz').read_bytes()), '7g.3a region masks differ from shipping D'
        assert not list(run.rglob('frame-*.png')) and not list(run.rglob('crop-*.png'))
        assert len(metrics['Modes']) == 3 and {m['Name'] for m in metrics['Modes']} == set(MODES)
        for mode in metrics['Modes']:
            assert len(mode['Frames']) == 16
            assert all(f['Diagnostics']['Backend'] == 'MetalNative' and f['Diagnostics']['ShadowActive'] for f in mode['Frames'])
            if mode['Name'] != 'fxaa':
                assert all(f['Diagnostics']['HistoryValid'] for f in mode['Frames'])
            assert all(f['Diagnostics']['DisplayWidth'] == 2560 and f['Diagnostics']['DisplayHeight'] == 1440 for f in mode['Frames'])
            for region in ('character', 'ground'):
                row, original = summary[condition][mode['Name']][region], mode['Regions'][region]
                for key, name in [('grain', 'GrainMean'), ('p95', 'GrainP95'), ('change', 'ChangeMean')]:
                    assert abs(row[key]-original[name]) < 1e-12
                assert row['grain_samples'] == original['Pixels'] and row['change_samples'] == original['ChangePixels']
    local = json.loads((engine/relative(record['local_results_path'])).read_text())
    assert local['source_commit'] == source
    assert local['build'] == {'exit_code': 0, 'warnings': 0, 'errors': 0}
    headless, metal = local['headless'], local['metal']
    assert len(headless['assemblies']) == 41
    assert headless['totals'] == {'failed': 1, 'passed': 37158, 'skipped': 1491, 'total': 38650}
    assert [r['assembly'] for r in headless['assemblies'] if r['failed']] == ['KhaozEngine.Catalog.Tests.dll']
    assert len(headless['failures']) == 1 and headless['failures'][0]['isolated_rerun']['exit_code'] == 0
    assert metal['resolve_ab_production_vs_shipped_D_byte_identical_cases'] == 12
    assert local['allocation']['bytes_per_steady_frame'] == 0
    assert metal['exit_code'] == metal['inventory_exit_code'] == 0
    assert metal['inventory_count'] == metal['executed'] == metal['passed'] == 1692
    assert metal['failed'] == metal['skipped'] == metal['missing'] == metal['extra'] == 0
    assert metal['skipped_names'] == []
    inventory = [json.loads(line) for line in (engine/relative(record['metal_inventory_path'])).read_text().splitlines()]
    assert len(inventory) == 1692 and all(r['outcome'] == 'Passed' for r in inventory)
    names = sorted(r['name'] for r in inventory)
    assert sha(('\n'.join(names)+'\n').encode()) == metal['inventory_names_sha256']
    assert all(not any(word in name.split('(')[0].rsplit('.', 1)[0].lower() for word in ('cost', 'perf', 'bench')) for name in names)
    print('PASS. 7g.3a local gate, 222 private packages, loaded renderer, 12 exact metric rows, RGB and region-mask identity to shipping D verified.')


def verify(path):
    engine = Path(__file__).resolve().parents[4]
    common = Path(git(engine, 'rev-parse', '--path-format=absolute', '--git-common-dir').decode().strip())
    probe = Path(os.environ.get('GRIMHOLLOW_PROBE', str(common.parent.parent/'Grimhollow')))
    data = json.loads(path.read_text())
    assert data['schema_version'] == 1
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
    matches = verify_shipping(engine, probe, data)
    verify_cost_cut(engine, probe, data)
    verify_barrier(engine, probe, data)
    print('PASS. Three historical source and package sets, 12 captures and 72 rows retain every check. Shipping source and 222 packages, loaded renderer, two captures and 12 rows verified. D match '+str(matches)+'. Acceptance '+data['acceptance']['status']+'. All artifact hashes verified. Locks unavailable with explicit reason. No rendering performed.')


if __name__ == '__main__':
    try:
        verify(Path(sys.argv[1]))
    except (AssertionError, KeyError, ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f'FAIL. {error}', file=sys.stderr)
        sys.exit(1)
