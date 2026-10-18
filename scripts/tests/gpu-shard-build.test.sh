#!/bin/sh
# Real MSBuild regression: a reduced solution must keep transitive references in Release.
set -eu
here=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
builder=${1:-$here/../gpu-shard-build.sh}
builder=$(CDPATH='' cd -- "$(dirname -- "$builder")" && pwd)/$(basename -- "$builder")
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/Root.Tests" "$fixture/Middle" "$fixture/Leaf"
cat > "$fixture/nuget.config" <<'XML'
<configuration><packageSources><clear /></packageSources></configuration>
XML
for name in Root.Tests Middle Leaf; do
  cat > "$fixture/$name/$name.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
XML
  cat > "$fixture/$name/ReleaseOnly.cs" <<'CS'
#if DEBUG
#error A scoped Release solution must not build references in Debug.
#endif
public class ReleaseOnly { }
CS
done
python3 - "$fixture" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
base = pathlib.Path(sys.argv[1])
for name, dependency in (("Root.Tests", "Middle"), ("Middle", "Leaf")):
    path = base / name / (name + ".csproj")
    project = ET.parse(path)
    ET.SubElement(ET.SubElement(project.getroot(), "ItemGroup"), "ProjectReference",
                  Include=f"../{dependency}/{dependency}.csproj")
    project.write(path)
(base / "Shard.slnx").write_text('<Solution><Project Path="Root.Tests/Root.Tests.csproj" /></Solution>')
PY
cd "$fixture"
dotnet restore Shard.slnx --verbosity quiet
sh "$builder" Shard.slnx
for name in Root.Tests Middle Leaf; do
  test -f "$name/bin/Release/net10.0/$name.dll"
  test ! -d "$name/bin/Debug"
done
echo 'gpu-shard-build.test: root and both transitive references built in Release'
