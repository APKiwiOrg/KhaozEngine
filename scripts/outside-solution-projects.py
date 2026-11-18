#!/usr/bin/env python3
"""Compile the tracked projects KhaozEngine.slnx does not list, without running a test.

Ordinary CI builds the solution. A project outside it still compiles engine code, by project reference
and by source files linked from other projects' folders, so an engine change can stop it compiling
while every solution build stays green. This script is how ordinary CI sees that. It builds and never
tests: each tooling suite keeps its own workflow, trigger and prerequisites.

  list                                   the projects, one per line
  build [--changed F --affected F]       build them all, or with both files only those the diff reaches

--changed is `git diff --name-only`. --affected is the affected-project tool's text output, one absolute
project path per line. A project is reached when the tool names it or the diff edits its project file.

The tool already follows what this needs: it names a project for a changed source the project links from
another folder, for a changed props or targets file the project imports, and for a project it references
(measured against this repository with dotnet-affected 6.2.0). The selective path lost these projects
only because it intersects the tool's answer with the solution. The project-file case is kept apart
because the selective script already treats a project file edit the tool reports nothing for as real.

The projects are found, never listed by hand: a hand-kept list is the kind of inventory that misses the
next project. Every failure is reported before the script exits 1, so one run names every broken project.
"""
import argparse
import pathlib
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOLUTION = 'KhaozEngine.slnx'
# A project that targets Windows still compiles on the Linux runner once the SDK may fetch its targeting pack.
PROPERTIES = ('-p:EnableWindowsTargeting=true',)


def outside(root):
    """Tracked project files the solution does not list, as repository paths with forward slashes."""
    tracked = subprocess.run(['git', 'ls-files', '--', '*.csproj'], cwd=root, capture_output=True, text=True,
                             check=True).stdout.splitlines()
    listed = {project.attrib['Path'].replace('\\', '/') for project in ET.parse(root / SOLUTION).iter('Project')}
    return sorted(path for path in tracked if path and path not in listed)


def reached(projects, changed, affected):
    """The projects a diff can stop compiling, each with the reason, in the order given."""
    changed = {path.strip().replace('\\', '/') for path in changed if path.strip()}
    affected = [line.strip().replace('\\', '/') for line in affected if line.strip()]
    selected = []
    for project in projects:
        if project in changed:
            selected.append((project, 'its project file changed'))
        elif any(line == project or line.endswith('/' + project) for line in affected):
            selected.append((project, 'the affected-project tool names it'))
    return selected


def build(root, projects, extra):
    failed = []
    for project in projects:
        print(f'outside-solution build: {project}', flush=True)
        command = ['dotnet', 'build', project, '-c', 'Release', *PROPERTIES, *extra]
        if subprocess.run(command, cwd=root, check=False).returncode != 0:
            failed.append(project)
    return failed


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('command', choices=('list', 'build'))
    parser.add_argument('--root', type=pathlib.Path, default=ROOT)
    parser.add_argument('--changed', type=pathlib.Path)
    parser.add_argument('--affected', type=pathlib.Path)
    parser.add_argument('extra', nargs='*', help='arguments after -- go to dotnet build')
    args = parser.parse_args(argv)
    if (args.changed is None) != (args.affected is None):
        parser.error('--changed and --affected go together')
    projects = outside(args.root)
    if args.command == 'list':
        print('\n'.join(projects))
        return 0
    if args.changed is not None:
        selection = reached(projects, args.changed.read_text().splitlines(), args.affected.read_text().splitlines())
        for project, reason in selection:
            print(f'outside-solution: {project} selected, {reason}')
        skipped = len(projects) - len(selection)
        if skipped:
            print(f'outside-solution: {skipped} of {len(projects)} projects not reached by this diff')
        projects = [project for project, _ in selection]
    failed = build(args.root, projects, args.extra)
    if failed:
        print('outside-solution: these projects no longer compile:\n  ' + '\n  '.join(failed), file=sys.stderr)
        return 1
    print(f'outside-solution: {len(projects)} projects compiled')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
