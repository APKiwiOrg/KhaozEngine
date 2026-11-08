#!/usr/bin/env python3
"""Select free tooling validation, independently of solution-based engine selection."""
import argparse
import fnmatch
import json
import pathlib
import re
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
WATCHED = (
    'tools/Renderer.Perf.Tooling.Tests/**', 'tools/Renderer.Perf.Verify/**',
    'tools/Renderer.Perf.Tests/**', 'scripts/renderer-perf-*.ps1',
    'scripts/renderer-tooling-selection.py', 'scripts/tests/renderer-tooling-selection.test.py',
    'scripts/build-release-tests.sh', 'scripts/tests/build-release-tests.test.sh',
    '.github/workflows/gpu-hosted-bench.yml', '.github/workflows/tooling-validation.yml',
    'test-shared/RendererPerfShared.props', 'test-shared/RendererPerfSceneInputs.cs',
    'test-shared/AllocationMeasurement.props', 'test-shared/AllocationRegion.cs',
    'test-shared/AllocationMeasurement.cs', 'test-shared/SerializedReportingTestFramework.cs',
    'test-shared/AllocationIsolationContractTests.cs', 'KhaozEngine.Render.Tests/Assets/Roboto-Regular.ttf',
    '.gitattributes', '.testhealth-baseline',
    '.filesize-baseline', 'Directory.Build.props', 'Directory.Build.targets',
    'Directory.Packages.props', 'nuget.config', 'global.json', '.config/dotnet-tools.json',
    'docs/RENDERER-PERFORMANCE.md', 'docs/CONTRIBUTOR-RULES.md', 'docs/CROSS-PLATFORM.md',
)
PLATFORMS = (
    ('ubuntu-26.04', 'vulkan-native'),
    ('windows-latest', 'direct3d11-native'),
    ('macos-26', 'metal-native'),
)
ITEMS_WATCHED = (
    'tools/Items.Benchmark.Tests/**', 'KhaozEngine.Benchmarks/Items/**',
    'KhaozEngine.Benchmarks/KhaozEngine.Benchmarks.csproj',
    'KhaozEngine.Benchmarks/Baselines/items-*.json',
    'test-shared/ItemsBenchmarkFields.cs', 'test-shared/ItemsBenchmarkFixtureDirectory.cs',
    'KhaozEngine.Server.Tests/AllocSensitiveCollection.cs',
    'scripts/renderer-tooling-selection.py', 'scripts/tests/renderer-tooling-selection.test.py',
    'scripts/build-release-tests.sh', 'scripts/tests/build-release-tests.test.sh',
    '.github/workflows/tooling-validation.yml', '.gitattributes', '.testhealth-baseline',
    '.filesize-baseline', 'Directory.Build.props', 'Directory.Build.targets',
    'Directory.Packages.props', 'nuget.config', 'global.json', '.config/dotnet-tools.json',
)
NAV_WATCHED = (
    'tools/NavCapture.Research.Tests/**', 'KhaozEngine.Movement/AssemblyInfo.cs',
    'KhaozEngine.Movement/PhysicsNav*.cs',
    'scripts/renderer-tooling-selection.py', 'scripts/tests/renderer-tooling-selection.test.py',
    'scripts/build-release-tests.sh', 'scripts/tests/build-release-tests.test.sh',
    '.github/workflows/tooling-validation.yml', '.gitattributes', '.testhealth-baseline',
    '.filesize-baseline', 'Directory.Build.props', 'Directory.Build.targets',
    'Directory.Packages.props', 'nuget.config', 'global.json', '.config/dotnet-tools.json',
)
COVERAGE_WATCHED = (
    'tools/Coverage.Verify/**', 'tools/Coverage.Verify.Tests/**',
    'KhaozEngine.Render3D/Coverage/**', 'KhaozEngine.Render3D/Scene3D.Coverage*.cs',
    'KhaozEngine.Render3D/Models/GltfLoader*.cs',
    'KhaozEngine.Attachments/EquipmentCoverage*.cs',
    'KhaozEngine.Attachments.Render3D/HostCoverageBinding*.cs',
    'KhaozEngine.Render.Tests/Render3D/CoverageFixture.cs',
    'KhaozEngine.Render.Tests/Gpu/CoverageRecession*.cs',
    'test-shared/SkinnedAttachmentGlbFixture.cs',
    'scripts/gpu-test-shards.py', 'scripts/tests/gpu-test-shards.test.sh',
    'scripts/build-release-tests.sh', 'scripts/tests/build-release-tests.test.sh',
    'scripts/renderer-tooling-selection.py', 'scripts/tests/renderer-tooling-selection.test.py',
    '.github/workflows/tooling-validation.yml', '.github/workflows/cross-platform-gpu.yml',
    '.gitattributes', '.testhealth-baseline', '.filesize-baseline',
    'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
    'nuget.config', 'global.json', '.config/dotnet-tools.json',
)


def watched_paths(suite='renderer'):
    if suite == 'items':
        return ITEMS_WATCHED
    if suite == 'nav':
        return NAV_WATCHED
    if suite == 'coverage':
        return COVERAGE_WATCHED
    links = ET.parse(ROOT / 'test-shared/RendererPerfShared.props').iter('Compile')
    return (*WATCHED, *(node.attrib['Include'].removeprefix('$(RendererPerfRepositoryRoot)/') for node in links))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--event', required=True, choices=('push', 'pull_request', 'workflow_call', 'workflow_dispatch'))
    parser.add_argument('--suite', default='renderer', choices=('renderer', 'items', 'nav', 'coverage'))
    parser.add_argument('--ref', required=True, dest='requested')
    parser.add_argument('--trusted-ref', default='')
    parser.add_argument('--changed', action='append', default=[])
    parser.add_argument('--changed-file', type=pathlib.Path)
    parser.add_argument('--unknown-diff', action='store_true')
    args = parser.parse_args()
    if args.event == 'workflow_call' and not args.trusted_ref:
        parser.error('reusable validation requires both requested and trusted commit SHAs')
    refs = list(dict.fromkeys(ref for ref in (args.requested, args.trusted_ref) if ref))
    if not args.requested or any(not re.fullmatch('[0-9a-f]{40}', ref) for ref in refs):
        parser.error('validation requires immutable full commit SHAs')
    changed = args.changed + (args.changed_file.read_text().splitlines() if args.changed_file else [])
    selected = (args.event in ('workflow_call', 'workflow_dispatch') or args.unknown_diff
                or any(fnmatch.fnmatchcase(path, pattern) for path in changed for pattern in watched_paths(args.suite)))
    rows = ([dict(ref=ref, os=os, backend=backend) for ref in refs for os, backend in PLATFORMS]
            if args.suite == 'renderer' else [dict(ref=ref, os='ubuntu-26.04') for ref in refs])
    print(json.dumps({'include': rows if selected else []}))


if __name__ == '__main__':
    main()
