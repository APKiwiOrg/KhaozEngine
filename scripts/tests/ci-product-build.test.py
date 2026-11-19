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
    action = (ROOT / '.github/actions/native-ci-scope/action.yml').read_text()
    pin = re.search(r'/ci-scope@([0-9a-f]{40})', action)[1]
    assert f'ref: {pin}\n          path: obj/ci-workflows' in workflow
    assert 'python3 -B scripts/tests/ci-product-build.test.py --matcher obj/ci-workflows/' in stage
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


def scope_inputs(workflow, rules):
    match = re.search(r"^\s+uses: \./\.github/actions/native-ci-scope\n\s+with:\n"
                      r"\s+segments: '([^\n]+)'(?:\n\s+base-events: ([^\n]+))?", workflow, re.M)
    assert match, 'Expected a native scope caller with a literal segment list'
    segments = json.loads(match[1])
    assert isinstance(segments, list) and segments and all(isinstance(item, str) for item in segments)
    assert len(segments) == len(set(segments)) and set(segments) <= rules.segments.keys(), segments
    return segments, tuple(match[2].split()) if match[2] else ()


NON_CODE = re.compile(r'//[^\n]*|/\*.*?\*/|("{3,}).*?\1|@"(?:""|[^"])*"|'
                      r'"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'', re.S)
FACT_TYPE = re.compile(r'\bclass\s+(\w+Attribute)\s*:\s*(?:[\w.]+\.)?'
                       r'(?:FactAttribute|TheoryAttribute)\s*\{')
TEST_GATE = re.compile(r'\b(?:GpuTestGate\s*\.\s*SkipReason\s*\(|'
                       r'Environment\s*\.\s*GetEnvironmentVariable\(\s*"KE_GPU_TESTS"\s*\))')


def class_body(source, start):
    depth = 1
    for brace in re.compile(r'[{}]').finditer(source, start):
        depth += 1 if brace[0] == '{' else -1
        if not depth:
            return source[start:brace.start()]
    raise AssertionError('Unclosed test attribute class')


def source_code(path):
    return NON_CODE.sub(lambda match: match[0] if match[0] == '"KE_GPU_TESTS"' else ' ',
                        (ROOT / path).read_text())


def device_tests(canonical, manifest):
    test_roots = {
        Path(path).parent: path for entry in manifest.values() if entry['role'] == 'test'
        for path in entry['roots'] if path in canonical
    }
    tracked = subprocess.check_output(['git', 'ls-files', '-z', '--', '*.cs'], cwd=ROOT).decode().split('\0')
    definitions = []
    sources = []
    for path in map(Path, filter(None, tracked)):
        test_root = next((test_roots[parent] for parent in path.parents if parent in test_roots), None)
        if not test_root and path.parts[0] != 'KhaozEngine.TestSupport.Gpu':
            continue
        code = source_code(path)
        definitions.append(code)
        if test_root:
            sources.append((test_root, code))
    gated = {declaration[1].removesuffix('Attribute')
             for code in definitions for declaration in FACT_TYPE.finditer(code)
             if TEST_GATE.search(class_body(code, declaration.end()))}
    assert {'GpuFact', 'GpuTheory'} <= gated, gated
    names = '|'.join(re.escape(name) for name in sorted(gated))
    use = re.compile(r'\[(?:[^\[\]\r\n]*,)?\s*'
                     rf'(?:[\w.:]+\.)?(?:{names})(?:Attribute)?(?=\s*[\](,])')
    return {root for root, code in sources if use.search(code)}, use


def runs_path(matcher, rules, requested, path):
    full, segments, _ = matcher.select(rules, [path])
    return full or bool(set(requested).intersection(segments))


def assert_external_routes(matcher, rules, requested, workflow, paths):
    # Include scripts run directly by shell steps, even when their command uses substitution.
    commands = re.compile(r'(?:\b(?:python3|bash|sh)\s+(?:-B\s+)?|\./)'
                          r'(scripts/[\w./-]+\.(?:sh|py|ps1))\b')
    external = set(paths) | set(commands.findall(workflow))
    missed = sorted(path for path in external if not runs_path(matcher, rules, requested, path))
    assert not missed, f'Native inputs outside scope: {missed}'


def check_scope_request(matcher, rules, requested, scratch):
    action = (ROOT / '.github/actions/native-ci-scope/action.yml').read_text()
    route = textwrap.dedent(action.split('      run: |\n', 1)[1])
    output = scratch / 'gpu-scope-output'

    def invoke(value, full, affected, reason='', cwd=ROOT):
        output.write_text('')
        return subprocess.run(
            ['bash', '-c', route],
            env={**os.environ, 'FULL': str(full).lower(), 'AFFECTED': json.dumps(affected),
                 'REQUESTED': value, 'REASON': reason, 'GITHUB_OUTPUT': str(output),
                 'GITHUB_STEP_SUMMARY': str(scratch / 'gpu-scope-summary')},
            cwd=cwd, text=True, capture_output=True,
        )

    for path, expected in (
        ('README.md', 'false'),
        ('KhaozEngine.Render.Tests/Gpu/Source.cs', 'true'),
        ('KhaozEngine.TileEdit.Tests/RenderServiceTests.cs', 'true'),
        ('KhaozEngine.WorldMap.Gui.Tests/WorldMapViewSnapshotGoldenTests.cs', 'true'),
        ('tools/Renderer.Perf.Tooling.Tests/Gpu/RendererPerf/RendererPerfWaterOpticsRecipeTests.cs', 'false'),
    ):
        full, segments, reason = matcher.select(rules, [path])
        result = invoke(json.dumps(requested), full, segments, reason)
        assert result.returncode == 0, result.stderr
        assert output.read_text().splitlines() == [f'selected={expected}'], path

    for invalid in ('bad json', '[]', '{"segment":"render"}', '["render", 1]', '["full"]', '["native-segment-missing"]'):
        result = invoke(invalid, True, list(rules.segments))
        assert result.returncode != 0 and not output.read_text()
        assert invalid in result.stderr, result.stderr
    result = invoke(json.dumps(requested), True, [], cwd=scratch)
    assert result.returncode == 0 and output.read_text().splitlines() == ['selected=true']


def check_gpu_push_base(matcher, requested, base_events):
    # A failed relevant push followed by a green narrowed dispatch must still be visible
    # in subsequent prose diffs until an eligible run covers the changed tree.
    repo = 'owner/engine'
    path = '.github/workflows/cross-platform-gpu.yml'
    workflow = matcher.Workflow(repo, path, 'main', 'current', base_events)
    a, b, c, d = (letter * 40 for letter in 'abcd')
    rules = (ROOT / '.github/ci-scope.txt').read_text()
    changed = b'KhaozEngine.TileEdit.Tests/RenderServiceTests.cs\0README.md\0'
    prose = b'README.md\0'

    def run(event, conclusion, sha, minute):
        time = f'2026-01-01T01:{minute:02}:00Z'
        return {'event': event, 'conclusion': conclusion, 'head_sha': sha, 'id': minute,
                'created_at': time, 'updated_at': time, 'path': path,
                'repository': {'full_name': repo}, 'head_repository': {'full_name': repo}}

    def git(args):
        if args[0] in ('cat-file', 'merge-base'):
            return 0, b''
        assert args[0] == 'diff', args
        return 0, changed if args[-1].startswith(a + '..') else prose

    history = [run('push', 'success', a, 1), run('push', 'failure', b, 2),
               run('workflow_dispatch', 'success', b, 3)]

    def fetch(_endpoint):
        return {'workflow_runs': history}

    for head in (c, d):
        decision = matcher.decide('push', {}, 'branch', rules, head, git, fetch, workflow)
        assert not decision.full and set(decision.segments).intersection(requested), (head, decision)
        history.append(run('push', 'failure', head, len(history) + 1))
    history.pop()
    history[-1]['conclusion'] = 'success'
    decision = matcher.decide('push', {}, 'branch', rules, d, git, fetch, workflow)
    assert not decision.full and not decision.segments, decision

    history.append(run('workflow_dispatch', 'failure', c, 5))
    assert matcher.decide('push', {}, 'branch', rules, d, git, fetch, workflow).full


def check_gpu_scope(matcher, rules, manifest, canonical, scratch):
    workflow = (ROOT / '.github/workflows/cross-platform-gpu.yml').read_text()
    requested, base_events = scope_inputs(workflow, rules)
    assert base_events == ('push', 'schedule'), base_events
    check_gpu_push_base(matcher, requested, base_events)
    gpu_roots, use = device_tests(canonical, manifest)
    assert all(manifest[segment]['role'] == 'test' for segment in requested), requested
    selected = {path for segment in requested for path in manifest[segment]['roots']}
    assert selected == gpu_roots, f'GPU segment roots differ from device tests: {sorted(selected ^ gpu_roots)}'
    outside = 'tools/Renderer.Perf.Tooling.Tests/Gpu/RendererPerf/RendererPerfWaterOpticsRecipeTests.cs'
    assert use.search(source_code(outside)) and not any(
        outside.startswith(str(Path(project).parent) + '/') for project in canonical
    ), outside
    check_scope_request(matcher, rules, requested, scratch)
    assert_external_routes(matcher, rules, requested, workflow, (
        'KhaozEngine.slnx', 'Directory.Packages.props', 'nuget.config',
        'KhaozEngine.Showcase/assets/dungeon/dungeon_stair.glb',
        'KhaozEngine.MapDoc.Tests/Foliage/FarFoliageGenerationFixtures.cs',
        'KhaozEngine.MapDoc.Tests/FormatFourFixtures.cs',
        'KhaozEngine.MapDoc.Physics.Tests/NativeWorldFixtures.Nested.cs',
        'docs/examples/EquipmentCoverageConsumer.cs', 'test-shared/D3D11CompileQuiesce.cs',
    ))
    selector, golden = workflow.split('\n  gpu-golden:\n', 1)
    gate, matrix_steps = golden.split('    steps:\n', 1)
    matrix_steps = matrix_steps.split('\n  gpu-full-suite:', 1)[0]
    assert re.findall(r'^    if: (.+)$', gate, re.M) == ["needs.legs.outputs.selected == 'true'"]
    assert "selected: ${{ steps.scope.outputs.selected || 'true' }}" in selector
    assert 'matrix.skip' not in matrix_steps and 'Report the GPU skip' not in matrix_steps

    pick = textwrap.dedent(selector.split('      - id: pick\n', 1)[1].split('        run: |\n', 1)[1])
    output = scratch / 'gpu-leg-output'
    subprocess.run(
        ['bash', '-c', pick],
        env={**os.environ, 'WANT': 'all', 'EVENT': 'pull_request', 'BAKE': 'false',
             'RENDER_FILTER': '', 'GITHUB_OUTPUT': str(output)},
        cwd=ROOT, check=True, capture_output=True,
    )
    values = dict(line.split('=', 1) for line in output.read_text().splitlines())
    rows = json.loads(values['matrix'])['include']
    assert sorted(row['backend'] for row in rows) == ['direct3d11-native', 'metal-native', 'vulkan-native']
    assert all(row['shard'] == '' and row['primary'] == '1' for row in rows)
    assert values['sharded'] == '[]' and values['vulkan_native'] == 'true'


def check_native_call_sites(matcher, rules, manifest, scratch):
    shared_build = ('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'nuget.config')
    files = {
        'foliage-conformance': (
            'KhaozEngine.slnx', 'scripts/outside-solution-projects.py', '.github/ci-projects.json',
            'KhaozEngine.Showcase/assets/dungeon/dungeon_stair.glb',
            'docs/examples/EquipmentCoverageConsumer.cs', 'test-shared/CapacityDemandProbe.cs',
        ),
        'terrain-format': (
            'KhaozEngine.TileWorld.Physics.Tests/Fixtures/ContactTerrainFormatFour.navbake',
            'KhaozEngine.Game.Tests/Locomotion/Contacts/LocomotionStateBits.cs',
            'KhaozEngine.Movement.Tests/Fixtures/ArrivalReference.cs',
            'test-shared/WaterPrismCensus/WaterPrismScenario.cs',
        ),
        'windows-console': tuple(path.relative_to(ROOT).as_posix()
                                 for path in (ROOT / 'tools/WindowsConsoleProbe/Shared').glob('*.cs')),
    }
    assert files['windows-console'], 'Expected shared Windows console sources'
    for name, external in files.items():
        workflow = (ROOT / f'.github/workflows/{name}.yml').read_text()
        requested, base_events = scope_inputs(workflow, rules)
        assert not base_events, (name, base_events)
        assert_external_routes(matcher, rules, requested, workflow, (*shared_build, *external))
        role = 'compile' if name == 'windows-console' else 'test'
        assert all(manifest[segment]['role'] == role for segment in requested), requested
        roots = {path for segment in requested for path in manifest[segment]['roots']}
        if name == 'windows-console':
            smoke = (ROOT / 'scripts/tests/windows-console-smoke.ps1').read_text()
            targets = re.search(r"foreach \(\$head in @\(([^)]+)\)\)", smoke)[1]
            expected = {f'tools/WindowsConsoleProbe/{target}.csproj'
                        for target in re.findall(r"'([^']+)'", targets)}
        else:
            expected = set(re.findall(r'\bdotnet test ([\w./-]+\.csproj)\b', workflow))
        assert expected and roots == expected, (name, roots, expected)
        if name == 'foliage-conformance':
            build = re.search(r"scripts/ci-product-build\.py false '([^']+)'", workflow)
            assert build, 'Expected a foliage solution planner call'
            build_segments = json.loads(build[1])
            assert isinstance(build_segments, list) and len(build_segments) == len(requested)
            assert set(build_segments) == set(requested), (build_segments, requested)
            foliage = plan(False, build_segments, scratch)
            assert set(projects(scratch / 'product.slnx')) == set(foliage['test_roots']) == expected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--matcher', required=True, type=Path)
    parser.add_argument('--replay', action='store_true')
    parser.add_argument('--gpu-only', action='store_true', help='Check GPU before the other native callers land')
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
        check_gpu_scope(matcher, rules, manifest, set(canonical), scratch)
        if not args.gpu_only:
            check_native_call_sites(matcher, rules, manifest, scratch)
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
