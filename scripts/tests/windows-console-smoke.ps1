param(
    [switch] $CompileOnly,
    [string] $EvidenceDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('ke-windows-console-' + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (-not $CompileOnly -and -not $IsWindows) { throw 'Native console checks require Windows.' }
if (Test-Path $EvidenceDirectory) { throw "Evidence directory must be new. $EvidenceDirectory" }
New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $EvidenceDirectory 'nuget' }
$restoreConfig = Join-Path $EvidenceDirectory 'nuget.config'
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear />
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
</packageSources></configuration>
'@ | Set-Content -Path $restoreConfig -Encoding utf8

function Invoke-Dotnet([string] $Name, [string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $repo
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(180000)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) { throw "dotnet $Name cleanup exceeded five seconds." }
            throw "dotnet $Name exceeded 180 seconds."
        }
        if ($process.ExitCode -ne 0) { throw "dotnet $Name failed with exit code $($process.ExitCode). See $Name.log." }
    } finally {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { throw "dotnet $Name cleanup exceeded five seconds." }
            }
            $outputTasks = [Threading.Tasks.Task[]] @($stdout, $stderr)
            if (-not [Threading.Tasks.Task]::WaitAll($outputTasks, 5000)) { throw "dotnet $Name log capture exceeded five seconds." }
            ($stdout.Result + $stderr.Result) | Set-Content (Join-Path $EvidenceDirectory "$Name.log") -Encoding utf8
        } finally { $process.Dispose() }
    }
}

function Assert-Subsystem([string] $Path, [int] $Expected) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Missing DOS header. $Path" }
        $reader.BaseStream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $reader.BaseStream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Missing PE signature. $Path" }
        $reader.BaseStream.Position = $peOffset + 24 + 68
        $actual = $reader.ReadUInt16()
        if ($actual -ne $Expected) { throw "Wrong PE subsystem $actual, expected $Expected. $Path" }
    } finally { $reader.Dispose() }
}

$driver = $null
$probeExe = Join-Path $EvidenceDirectory 'probe/WindowsConsoleProbe.exe'
$driverExe = Join-Path $EvidenceDirectory 'driver/WindowsConsoleDriver.exe'
$resultsPath = Join-Path $EvidenceDirectory 'results.json'
try {
    $sources = @(Get-ChildItem (Join-Path $repo 'KhaozEngine.Platform/WindowsConsole*.cs') | ForEach-Object {
        @{ File = $_.Name; SHA256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    })
    @{ Commit = $env:GITHUB_SHA; Sources = $sources; Configuration = 'Release'; Runtime = 'win-x64' } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $EvidenceDirectory 'source.json') -Encoding utf8
    Invoke-Dotnet 'dotnet-info' @('--info')
    foreach ($head in @('Probe/WindowsConsoleProbe', 'Driver/WindowsConsoleDriver')) {
        $project = Join-Path $repo "tools/WindowsConsoleProbe/$head.csproj"
        $name = if ($head.StartsWith('Probe/')) { 'probe' } else { 'driver' }
        $output = Join-Path $EvidenceDirectory $name
        Invoke-Dotnet "$name-restore" @('restore', $project, '-r', 'win-x64', '--packages', $env:NUGET_PACKAGES,
            '--configfile', $restoreConfig)
        $assets = Get-Content (Join-Path (Split-Path $project) 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        foreach ($library in $assets.libraries.GetEnumerator()) {
            if ($library.Key -like 'KhaozEngine.*/*' -and $library.Value.type -eq 'package') {
                throw "Engine harness restored a package instead of engine source. $($library.Key)"
            }
        }
        if ($name -eq 'probe' -and @($assets.libraries.GetEnumerator() | Where-Object {
            $_.Key -like 'KhaozEngine.Platform/*' -and $_.Value.type -eq 'project'
        }).Count -ne 1) { throw 'Probe did not restore KhaozEngine.Platform as a project reference.' }
        # Build isolated executables. No solution change or packaging path is needed.
        Invoke-Dotnet "$name-build" @('build', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
            '--no-restore', '-o', $output, '-m:1', '-p:BuildInParallel=false')
    }
    Assert-Subsystem $probeExe 2
    Assert-Subsystem $driverExe 3
    @{ ProbeSubsystem = 2; DriverSubsystem = 3 } | ConvertTo-Json |
        Set-Content (Join-Path $EvidenceDirectory 'subsystems.json') -Encoding utf8
    if ($CompileOnly) {
        Write-Host "Compile proof only. WinExe subsystem 2 and driver subsystem 3. $EvidenceDirectory"
        return
    }

    $start = [Diagnostics.ProcessStartInfo]::new($driverExe)
    $start.UseShellExecute = $false
    $start.ArgumentList.Add($probeExe)
    $start.ArgumentList.Add($EvidenceDirectory)
    $driver = [Diagnostics.Process]::Start($start)
    if (-not $driver.WaitForExit(480000)) {
        $driver.Kill($true)
        if (-not $driver.WaitForExit(5000)) { throw 'Driver timeout cleanup exceeded five seconds.' }
        throw 'Windows console driver exceeded 480 seconds.'
    }
    if (-not (Test-Path $resultsPath)) { throw 'Driver produced no machine evidence.' }
    $results = Get-Content $resultsPath -Raw | ConvertFrom-Json
    Write-Host (Get-Content $resultsPath -Raw)
    $lines = @('## Windows console regression', '', "Engine source version $($results.EngineVersion)", '',
        '| Check | Result | Evidence |', '| --- | --- | --- |')
    foreach ($check in $results.Checks) {
        $detail = $check.Detail.Replace('|', '/').Replace("`r", ' ').Replace("`n", ' ')
        $lines += "| $($check.Name) | $($check.Status) | $detail |"
    }
    $lines | Set-Content (Join-Path $EvidenceDirectory 'summary.md') -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) { $lines | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
    if ($driver.ExitCode -ne 0) { throw "Windows console regression failed with exit code $($driver.ExitCode). See results.json." }
} catch {
    $_.Exception.ToString() | Set-Content (Join-Path $EvidenceDirectory 'failure.txt') -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) {
        "`nWindows console smoke failed. $($_.Exception.Message)" | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    }
    throw
} finally {
    if ($null -ne $driver) {
        try {
            if (-not $driver.HasExited) {
                $driver.Kill($true)
                if (-not $driver.WaitForExit(5000)) { throw 'Driver cleanup exceeded five seconds.' }
            }
        } finally { $driver.Dispose() }
    }
}
