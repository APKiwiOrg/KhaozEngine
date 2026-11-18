#!/usr/bin/env python3
"""Exercise the product inventory and replay tracked paths with the pinned shared matcher."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import runpy
import subprocess
import sys
import tempfile
import textwrap
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def load_matcher(path):
    sys.path.insert(0, str(path.parent.parent / 'lib'))
    sys.path.insert(0, str(path.parent))
    spec = importlib.util.spec_from_file_location('ci_scope', path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def plan(full, segments, directory):
    result = subprocess.run(
        [sys.executable, '-B', str(ROOT / 'scripts/ci-product-build.py'), str(full).lower(),
         json.dumps(segments), '--solutions', str(directory)],
        cwd=ROOT, text=True, capture_output=True,
    )
    assert result.returncode == 0, result.stderr
    return json.loads(result.stdout)


def projects(path):
    return [(path.parent / item.attrib['Path']).resolve().relative_to(ROOT).as_posix()
            for item in ET.parse(path).iter('Project')]


def check_tooling_and_census_routes(matcher, rules):
    legacy = (ROOT / '.github/workflows/ci.yml').read_text()
    legacy = legacy.split('      - name: Repo convention checks', 1)[1].split(
        '      - uses: actions/setup-dotnet', 1)[0]
    command = re.compile(r'^          ((?:sh|python3) scripts/tests/\S+\.test\.(?:sh|py))$', re.M)
    expected = command.findall(legacy)
    assert len(expected) == 18, 'Expected eighteen legacy tooling registrations'
    workflow = (ROOT / '.github/workflows/product-ci.yml').read_text()
    stage = workflow.partition("      - name: 'Test: tooling'\n")[2].split('\n      - ', 1)[0]
    problems = []
    if command.findall(stage) != expected:
        problems.append('Test: tooling must execute the eighteen legacy fixture commands in order')

    for path in (
        'scripts/ci-prior-gate.sh', 'scripts/hooks/file-change-guard.pl', '.githooks/pre-push',
        '.claude/settings.json', '.codex/hooks.json', 'AGENTS.md', 'CLAUDE.md',
        '.github/workflows/tooling-validation.yml', '.github/workflows/ci.yml',
        'KhaozEngine.Showcase/KhaozEngine.Showcase.csproj',
        'KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj', 'test-shared/AllocationMeasurement.props',
    ):
        full, segments, _ = matcher.select(rules, [path])
        if not (full or 'tooling' in segments):
            problems.append(f'{path} must select the tooling fixtures')

    for path in (
        'scripts/pack-packages.sh', 'scripts/pack-local-feed.sh', 'scripts/pack-standard.sh',
        'scripts/tag-standard.sh', 'build/WaterPrismCensus.props', 'build/WaterPrismCensus.targets',
        'scripts/regenerate-water-prism-census.py', 'scripts/check-water-kernel-trx.py',
        'KhaozEngine.TileWorld.Physics.Tests/Fixtures/WaterPrismCensusBaseline.json',
    ):
        full, segments, _ = matcher.select(rules, [path])
        if not (full or 'water-kernel' in segments):
            problems.append(f'{path} must select the census and water proof')
    assert not problems, '\n'.join(problems)


def check_native_reference_growth(matcher, rules, manifest):
    build = runpy.run_path(str(ROOT / 'scripts/ci-product-build.py'))
    canonical = set(projects(ROOT / 'KhaozEngine.slnx'))
    outside = set(runpy.run_path(str(ROOT / 'scripts/outside-solution-projects.py'))['outside'](ROOT))
    graph = build['audit_inventory'](manifest, canonical, outside)
    assert build['audit_rules'](manifest, graph, full=True)

    render, = manifest['render']['roots']
    driver, = manifest['compile-windows-console-driver']['roots']
    gpu_roots = manifest['render']['roots'] + manifest['map-editor']['roots']
    assert driver not in build['closure'](graph, gpu_roots)
    full, segments, _ = matcher.select(rules, [str(Path(driver).with_name('Source.cs'))])
    assert not full and not {'render', 'map-editor'}.intersection(segments)
    graph[render].add(driver)
    assert driver in build['closure'](graph, gpu_roots)
    try:
        build['audit_rules'](manifest, graph, full=True)
    except ValueError:
        return
    raise AssertionError('Native scope audit accepted a stale render closure after adding a reference')


def check_gpu_skip(scratch):
    workflow = (ROOT / '.github/workflows/cross-platform-gpu.yml').read_text()
    selector = workflow.split('      - id: pick\n', 1)[1].split('\n  gpu-golden:', 1)[0]
    command = textwrap.dedent(selector.split('        run: |\n', 1)[1])
    output = scratch / 'gpu-skip-output'
    subprocess.run(
        ['bash', '-c', command],
        env={**os.environ, 'SELECTED': 'false', 'GITHUB_OUTPUT': str(output)},
        check=True,
    )
    values = dict(line.split('=', 1) for line in output.read_text().splitlines())
    matrix = json.loads(values['matrix'])
    assert len(matrix.get('include', [])) == 1, 'Off-scope GPU must have one report row'
    row = matrix['include'][0]
    assert row['skip'] is True and row['runner'].startswith('ubuntu-')
    assert values['sharded'] == '[]' and values['vulkan_native'] == 'false'

    golden = workflow.split('\n  gpu-golden:\n', 1)[1].split('\n  gpu-full-suite:\n', 1)[0]
    steps = re.split(r'^      - ', golden.split('    steps:\n', 1)[1], flags=re.M)[1:]
    report, *gpu_steps = steps
    assert re.search(r'^        if: matrix.skip == true$', report, re.M)
    assert gpu_steps
    for step in gpu_steps:
        assert re.search(r'^        if: matrix.skip != true(?: && .+)?$', step, re.M), step.splitlines()[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--matcher', required=True, type=Path)
    parser.add_argument('--replay', action='store_true')
    args = parser.parse_args()
    matcher = load_matcher(args.matcher.resolve())
    rules = matcher.parse_rules((ROOT / '.github/ci-scope.txt').read_text())
    assert not rules.error, rules.error
    manifest = json.loads((ROOT / '.github/ci-projects.json').read_text())
    assert set(rules.segments) == set(manifest)
    check_tooling_and_census_routes(matcher, rules)
    check_native_reference_growth(matcher, rules, manifest)

    for path in ('README.md', 'docs/CONTRIBUTOR-RULES.md'):
        assert matcher.select(rules, [path])[:2] == (False, []), path
    for path in ('KhaozEngine.App/KhaozEngine.App.csproj', 'KhaozEngine.Render.Tests/Gpu/FakeGpuDevice.cs',
                 'docs/examples/EquipmentCoverageConsumer.cs', 'UnknownProject/source.cs',
                 'khaozengine.audio/Source.cs', 'scripts/gpu-test-shards.py', 'scripts/gpu-shard-build.sh'):
        assert matcher.select(rules, [path])[0], path
    assert matcher.select(rules, ['KhaozEngine.Audio/Source.cs'])[0:2] != (True, sorted(rules.segments))
    assert 'audio' in matcher.select(rules, ['KhaozEngine.Audio/Source.cs'])[1]
    assert matcher.decide('pull_request', {}, 'branch', None, '', None, None, None).full

    outside = subprocess.check_output(
        [sys.executable, '-B', str(ROOT / 'scripts/outside-solution-projects.py'), 'list'],
        cwd=ROOT, text=True,
    ).splitlines()
    canonical = projects(ROOT / 'KhaozEngine.slnx')
    with tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR')) as scratch:
        scratch = Path(scratch)
        check_gpu_skip(scratch)
        full = plan(True, [], scratch)
        roots = projects(scratch / 'product.slnx')
        tests = projects(scratch / 'tests.slnx')
        assert sorted(roots) == sorted(canonical + outside)
        assert len(roots) == len(set(roots))
        assert len(tests) == 44 and len(outside) == 11
        assert sum(item['role'] == 'compile' for item in manifest.values()) - len(outside) == 12
        assert set(tests) == set(full['test_roots'])
        assert not set(tests) & set(outside)
        assert len(projects(scratch / 'water.slnx')) == 2
        assert set(full['overlays']) == {'sql-accounts', 'sql-catalog', 'sql-server', 'water-kernel'}

        _, tokens, _ = matcher.select(rules, ['tools/Renderer.Perf.Tooling.Tests/Source.cs'])
        partial = plan(False, tokens, scratch)
        assert not partial['test_roots'] and not partial['overlays']
        assert projects(scratch / 'product.slnx') == sorted(
            item for token in tokens for item in manifest[token]['roots'])
        assert all(token.startswith('compile-') for token in tokens)
        assert plan(False, [], scratch)['build_roots'] == []

    if args.replay:
        paths = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode().split('\0')
        totals = {'full': 0, 'selected': 0, 'ignored': 0}
        for path in filter(None, paths):
            full, segments, _ = matcher.select(rules, [path])
            assert set(segments) <= set(manifest), path
            totals['full' if full else 'selected' if segments else 'ignored'] += 1
        print('Tracked scope replay:', totals)
    print('Product CI scope and solution inventory passed')


if __name__ == '__main__':
    main()
