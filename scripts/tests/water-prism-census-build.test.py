#!/usr/bin/env python3
"""#1755. Exercise real MSBuild policy and both pack scripts in a bounded throwaway repository."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def run(root, *args, code=0, env=None, diagnostic=None):
    result = subprocess.run(args, cwd=root, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    assert result.returncode == code, (args, result.returncode, result.stdout)
    if diagnostic:
        assert f'error {diagnostic}:' in result.stdout, result.stdout
    return result.stdout


with tempfile.TemporaryDirectory(prefix='khaozengine-census-build-') as temporary:
    scratch = Path(temporary).resolve()
    root = scratch / 'repo'
    (root / 'build').mkdir(parents=True)
    (root / 'scripts').mkdir()
    for name in ('WaterPrismCensus.props', 'WaterPrismCensus.targets'):
        shutil.copy2(ROOT / 'build' / name, root / 'build' / name)
    (root / 'Directory.Build.props').write_text('''<Project><PropertyGroup>
<TargetFramework>net10.0</TargetFramework><KhaozEngineVersion>0.0.0</KhaozEngineVersion>
</PropertyGroup><Import Project="build/WaterPrismCensus.props" /></Project>''')
    (root / 'Directory.Build.targets').write_text('<Project><Import Project="build/WaterPrismCensus.targets" /></Project>')
    for name, packable in (('Library', 'true'), ('Tests', 'false')):
        (root / f'{name}.csproj').write_text(
            f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsPackable>{packable}</IsPackable></PropertyGroup></Project>')
    (root / 'KhaozEngine.slnx').write_text('<Solution><Project Path="Library.csproj" /><Project Path="Tests.csproj" /></Solution>')
    for name in ('pack-packages.sh', 'pack-local-feed.sh', 'pack-standard.sh', 'tag-standard.sh'):
        shutil.copy2(ROOT / 'scripts' / name, root / 'scripts' / name)
    (root / 'scripts/check-doc-versions.sh').touch()
    run(root, 'git', 'init', '-q', '--initial-branch=main')
    run(root, 'git', 'add', '.')
    run(root, 'git', '-c', 'user.name=Build fixture', '-c', 'user.email=build@example.invalid',
        '-c', 'commit.gpgsign=false', 'commit', '-qm', 'test(build): fixture')
    properties = ('IsPackable,OutputPath,IntermediateOutputPath,MSBuildProjectExtensionsPath')
    # Query the real engine root too, so dropping a root import cannot leave the fixture passing.
    engine_project = ROOT / 'KhaozEngine.TileWorld.Physics/KhaozEngine.TileWorld.Physics.csproj'
    normal = json.loads(run(ROOT, 'dotnet', 'msbuild', str(engine_project), '-p:Configuration=Release',
                            '-getProperty:' + properties))['Properties']
    census = json.loads(run(ROOT, 'dotnet', 'msbuild', str(engine_project), '-p:Configuration=Release',
                            '-p:WaterPrismCensus=true', '-getProperty:' + properties))['Properties']
    assert normal['IsPackable'] == 'true' and census['IsPackable'] == 'false'
    for key, base in (('OutputPath', 'bin'), ('IntermediateOutputPath', 'obj'), ('MSBuildProjectExtensionsPath', 'obj')):
        actual = (engine_project.parent / census[key]).resolve()
        assert actual.is_relative_to(engine_project.parent / base / 'water-prism-census'), (key, actual)
        assert actual != (engine_project.parent / normal[key]).resolve(), key
    run(root, 'dotnet', 'msbuild', 'Library.csproj', '-target:ValidateWaterPrismCensusOutputPaths')
    for override in ('OutputPath=bin/Release/', 'IntermediateOutputPath=obj/Release/', 'MSBuildProjectExtensionsPath=obj/'):
        run(root, 'dotnet', 'msbuild', 'Library.csproj', '-target:ValidateWaterPrismCensusOutputPaths',
            '-p:WaterPrismCensus=true', '-p:' + override, code=1, diagnostic='KECENSUS002')
    for project in ('Library.csproj', 'Tests.csproj', 'KhaozEngine.slnx'):
        run(root, 'dotnet', 'pack', project, '--no-build', '--no-restore', '-p:WaterPrismCensus=true',
            code=1, diagnostic='KECENSUS001')
    run(root, 'dotnet', 'publish', 'Library.csproj', '--no-build', '--no-restore', '-p:WaterPrismCensus=true',
        code=1, diagnostic='KECENSUS001')
    environment = dict(os.environ, KHAOZENGINE_FEED=str(scratch / 'private-feed'))
    for script in ('pack-packages.sh', 'pack-local-feed.sh'):
        run(root, 'sh', 'scripts/' + script, '--no-build', '--no-restore', '-p:WaterPrismCensus=true',
            code=1, env=environment, diagnostic='KECENSUS001')
    assert not list(scratch.rglob('*.nupkg'))
    assert not list(scratch.rglob('*.dll'))
print('census build policy passed for packable, nonpackable, solution, publish, overrides and both pack scripts')
