#!/usr/bin/env python3
"""#1755. Exercise real MSBuild policy and both pack scripts in a bounded throwaway repository."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--artifact-proof', action='store_true', help='Also build the real physics project in both modes and inspect a private package.')
options = parser.parse_args()


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
    properties = ('IsPackable,OutputPath,OutDir,TargetDir,TargetPath,IntermediateOutputPath,MSBuildProjectExtensionsPath,ProjectAssetsFile')
    # Query the real engine root too, so dropping a root import cannot leave the fixture passing.
    engine_project = ROOT / 'KhaozEngine.TileWorld.Physics/KhaozEngine.TileWorld.Physics.csproj'
    normal = json.loads(run(ROOT, 'dotnet', 'msbuild', str(engine_project), '-p:Configuration=Release',
                            '-getProperty:' + properties))['Properties']
    census = json.loads(run(ROOT, 'dotnet', 'msbuild', str(engine_project), '-p:Configuration=Release',
                            '-p:WaterPrismCensus=true', '-getProperty:' + properties))['Properties']
    assert normal['IsPackable'] == 'true' and census['IsPackable'] == 'false'
    for key, base in (('OutDir', 'bin'), ('TargetDir', 'bin'), ('TargetPath', 'bin'), ('IntermediateOutputPath', 'obj'),
                      ('MSBuildProjectExtensionsPath', 'obj'), ('ProjectAssetsFile', 'obj')):
        actual = (engine_project.parent / census[key]).resolve()
        assert actual.is_relative_to(engine_project.parent / base / 'water-prism-census'), (key, actual)
        assert actual != (engine_project.parent / normal[key]).resolve(), key
    run(root, 'dotnet', 'msbuild', 'Library.csproj', '-target:ValidateWaterPrismCensusOutputPaths')
    for override in ('OutDir=bin/Release/net10.0/', 'OutputPath=bin/Release/', 'BaseOutputPath=bin/',
                     'BaseIntermediateOutputPath=obj/', 'IntermediateOutputPath=obj/Release/',
                     'MSBuildProjectExtensionsPath=obj/', 'TargetDir=bin/Release/net10.0/',
                     'TargetPath=bin/Release/net10.0/Library.dll', 'TargetRefPath=bin/Release/ref/Library.dll',
                     'RestoreOutputPath=obj/', 'RestoreOutputAbsolutePath=obj/', 'ProjectAssetsFile=obj/project.assets.json'):
        run(root, 'dotnet', 'msbuild', 'Library.csproj', '-target:ValidateWaterPrismCensusOutputPaths',
            '-p:WaterPrismCensus=true', '-p:' + override, code=1, diagnostic='KECENSUS002')
    run(root, 'dotnet', 'msbuild', 'Library.csproj', '-target:ValidateWaterPrismCensusOutputPaths',
        '-p:WaterPrismCensus=true', env=dict(os.environ, OutDir='bin/Release/net10.0/'), code=1, diagnostic='KECENSUS002')
    response = root / 'unsafe.rsp'
    response.write_text('-p:OutDir=bin/Release/net10.0/\n')
    run(root, 'dotnet', 'msbuild', 'Library.csproj', '@' + str(response), '-target:ValidateWaterPrismCensusOutputPaths',
        '-p:WaterPrismCensus=true', code=1, diagnostic='KECENSUS002')
    # Layout inputs are allowed when their effective destinations remain isolated.
    for flags in (['-p:AppendTargetFrameworkToOutputPath=false'],
                  ['-p:ArtifactsPath=ordinary-artifacts', '-p:UseArtifactsOutput=true'],
                  ['-p:ArtifactsPath=ordinary-artifacts', '-p:UseArtifactsOutput=true', '-p:UseArtifactsIntermediateOutput=false']):
        run(root, 'dotnet', 'msbuild', 'Library.csproj', '-target:ValidateWaterPrismCensusOutputPaths',
            '-p:WaterPrismCensus=true', *flags)
        run(root, 'dotnet', 'msbuild', 'Library.csproj', '-target:ValidateWaterPrismCensusOutputPaths',
            '-p:WaterPrismCensus=true', *flags, '-p:OutDir=bin/Release/net10.0/', code=1, diagnostic='KECENSUS002')
    # A bad referenced project must fail even when its parent has safe destinations.
    child = root / 'Child'
    child.mkdir()
    (child / 'Child.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutDir Condition="'$(WaterPrismCensus)' == 'true'">bin/Release/net10.0/</OutDir>
</PropertyGroup></Project>''')
    (root / 'Parent.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<ProjectReference Include="Child/Child.csproj" /></ItemGroup></Project>''')
    run(root, 'dotnet', 'restore', 'Parent.csproj', '-p:WaterPrismCensus=true', code=1, diagnostic='KECENSUS002')
    (root / 'BadIntermediate.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
<IntermediateAssembly Include="obj/Release/normal.dll" /></ItemGroup></Project>''')
    run(root, 'dotnet', 'msbuild', 'BadIntermediate.csproj', '-target:ValidateWaterPrismCensusOutputPaths',
        '-p:WaterPrismCensus=true', code=1, diagnostic='KECENSUS002')
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

if options.artifact_proof:
    start = time.monotonic()
    project = ROOT / 'KhaozEngine.TileWorld.Physics/KhaozEngine.TileWorld.Physics.csproj'
    normal_dll = project.parent / 'bin/Release/net10.0/KhaozEngine.TileWorld.Physics.dll'
    census_dll = project.parent / 'bin/water-prism-census/Release/net10.0/KhaozEngine.TileWorld.Physics.dll'
    (ROOT / 'local-feed').mkdir(exist_ok=True)
    run(ROOT, 'dotnet', 'build', str(project), '-c', 'Release')
    normal_bytes = normal_dll.read_bytes()
    assert b'TileWaterCensus' not in normal_bytes and b'KhaozEngine.Benchmarks' not in normal_bytes
    normal_hash = hashlib.sha256(normal_bytes).hexdigest()
    normal_time = normal_dll.stat().st_mtime_ns
    run(ROOT, 'dotnet', 'build', str(project), '-c', 'Release', '-p:WaterPrismCensus=true')
    assert hashlib.sha256(normal_dll.read_bytes()).hexdigest() == normal_hash
    assert normal_dll.stat().st_mtime_ns == normal_time
    research = census_dll.read_bytes()
    assert b'TileWaterCensus' in research and b'KhaozEngine.Benchmarks' in research
    with tempfile.TemporaryDirectory(prefix='khaozengine-census-package-') as temporary:
        output = Path(temporary).resolve()
        run(ROOT, 'dotnet', 'pack', str(project), '-c', 'Release', '--no-build', '--no-restore', '-o', str(output),
            env=dict(os.environ, KHAOZENGINE_FEED=str(output)))
        packages = list(output.glob('KhaozEngine.TileWorld.Physics.*.nupkg'))
        assert len(packages) == 1
        with zipfile.ZipFile(packages[0]) as package:
            data = package.read('lib/net10.0/KhaozEngine.TileWorld.Physics.dll')
            assert data == normal_bytes
            assert b'TileWaterCensus' not in data and b'KhaozEngine.Benchmarks' not in data
    print(json.dumps({'artifactProofSeconds': round(time.monotonic() - start, 3),
                      'normalDllSha256': normal_hash, 'normalDllUnchanged': True, 'normalPackageClean': True}))
