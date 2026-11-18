#!/usr/bin/env python3
"""Exercise the product inventory and replay tracked paths with the pinned shared matcher."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
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

    for path in ('README.md', 'docs/CONTRIBUTOR-RULES.md'):
        assert matcher.select(rules, [path])[:2] == (False, []), path
    for path in ('KhaozEngine.App/KhaozEngine.App.csproj', 'KhaozEngine.Render.Tests/Gpu/FakeGpuDevice.cs',
                 'docs/examples/EquipmentCoverageConsumer.cs', 'UnknownProject/source.cs',
                 'khaozengine.audio/Source.cs'):
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
