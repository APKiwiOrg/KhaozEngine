param([ValidateSet('Select', 'Authorize', 'Verify', 'VerifyRehearsal')][string]$Mode)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require([bool]$Condition, [string]$Reason) {
    if (!$Condition) { throw $Reason }
}
function Git-Read([string[]]$Arguments) {
    $lines = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "git $Arguments failed" }
    return $lines
}
function Read-Authorization {
    $head = (Git-Read @('rev-parse', 'HEAD')) -join ''
    Require ($head -ceq $env:TESTED_REF) 'Checkout differs from the immutable tested ref'
    $parents = ((Git-Read @('rev-list', '--parents', '-n', '1', 'HEAD')) -join '') -split ' '
    Require ($parents.Count -eq 2 -and $parents[1] -ceq $env:RENDERER_PERF_WORKLOAD_SHA -and $head -cne $parents[1]) 'Authorization must have exactly the workload as its sole parent'
    $changes = @(Git-Read @('diff', '--name-status', '--no-renames', $parents[1], 'HEAD'))
    Require ($changes.Count -eq 1 -and $changes[0] -ceq "A`t$manifestPath") "Authorization must add only $manifestPath"
    return [ordered]@{
        manifestSha256 = $env:RENDERER_PERF_MANIFEST_SHA256
        workloadSha = $parents[1]
        authorizationSha = $head
        parentSha = $parents[1]
        parentCount = 1
        changedPaths = @($manifestPath)
        runAttempt = 1
        leg = $env:LEGS
        runner = $runner
        workflow = 'gpu-hosted-bench.yml'
        manifestChangeKind = 'A'
        previousRunId = $null
    }
}

# All values enter through environment variables, never shell source interpolation.
Require ([string]::IsNullOrEmpty($env:COVERAGE_PERF_UNIT) -and [string]::IsNullOrEmpty($env:COVERAGE_PERF_MANIFEST_SHA256) -and [string]::IsNullOrEmpty($env:COVERAGE_PERF_WORKLOAD_SHA)) 'Coverage and renderer modes cannot be combined'
Require ($env:WORKFLOW_RUN_ATTEMPT -ceq '1') 'Only first-attempt renderer runs are admissible'
Require ($env:LEGS -cin @('linux-vulkan', 'windows-d3d11', 'windows-vulkan')) 'Select exactly one renderer leg, never all'
Require ($env:FILTER -ceq 'FullyQualifiedName~GpuCapabilitiesDiagTests') 'Renderer mode refuses a free-form filter'
Require ($env:TESTED_REF -cmatch '\A[0-9a-f]{40}\z') 'Renderer mode requires an immutable 40-digit tested ref'
Require ($env:RENDERER_PERF_UNIT -cmatch '\A[a-z0-9][a-z0-9-]{0,63}\z') 'Invalid renderer unit filename'
Require ($env:RENDERER_PERF_MANIFEST_SHA256 -cmatch '\A[0-9a-f]{64}\z') 'Invalid renderer manifest hash'
Require ($env:RENDERER_PERF_WORKLOAD_SHA -cmatch '\A[0-9a-f]{40}\z') 'Invalid renderer workload hash'
$manifestPath = "tools/Renderer.Perf.Tests/authorized/$($env:RENDERER_PERF_UNIT).json"
Require (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'Approved manifest is missing'
Require ((Get-Item -LiteralPath $manifestPath).Length -le 65536) 'Manifest exceeds 64 KiB'
$hash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
Require ($hash -ceq $env:RENDERER_PERF_MANIFEST_SHA256) 'Manifest byte hash differs from the dispatch'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable -Depth 32
Require ($manifest.preset -cin @('pilot', 'full', 'rehearsal', 'rehearsal-full')) 'Unknown renderer preset'
$rehearsal = $manifest.preset -cin @('rehearsal', 'rehearsal-full')
$rehearsalPolicy = if ($rehearsal) {
    switch -CaseSensitive ("$($manifest.preset)/$($env:LEGS)") {
        'rehearsal/linux-vulkan' { @{ runner = 'ubuntu-24.04'; adapter = 'mesa-lavapipe-software'; unitSeconds = 5400; minutes = 120 } }
        'rehearsal/windows-d3d11' { @{ runner = 'windows-2025-vs2026'; adapter = 'microsoft-warp-software'; unitSeconds = 5400; minutes = 120 } }
        'rehearsal-full/linux-vulkan' { @{ runner = 'ubuntu-24.04'; adapter = 'mesa-lavapipe-software'; unitSeconds = 19800; minutes = 350 } }
        'rehearsal-full/windows-d3d11' { @{ runner = 'windows-2025-vs2026'; adapter = 'microsoft-warp-software'; unitSeconds = 19800; minutes = 350 } }
        default { throw 'No software rehearsal class admits this preset and leg' }
    }
} else { $null }
$runner = if ($rehearsal) { $rehearsalPolicy.runner } elseif ($env:LEGS -ceq 'linux-vulkan') { 'gpu-t4-linux' } else { 'gpu-t4-windows' }
$backend = if ($env:LEGS -ceq 'windows-d3d11') { 'd3d11' } else { 'vulkan' }
Require ($manifest.schema -ceq 'khaozengine.renderer-perf-unit/1' -and $manifest.unitId -ceq $env:RENDERER_PERF_UNIT -and $manifest.workloadSha -ceq $env:RENDERER_PERF_WORKLOAD_SHA) 'Manifest identity differs from the dispatch'
Require ($manifest.execution.leg -ceq $env:LEGS -and $manifest.execution.runner -ceq $runner -and $manifest.execution.backend -ceq $backend -and $manifest.execution.workflow -ceq 'gpu-hosted-bench.yml') 'Manifest execution differs from the selected leg'
Require ($manifest.execution.adapterPolicy -ceq $(if ($rehearsal) { $rehearsalPolicy.adapter } else { 'nvidia-t4-hardware' }) -and $manifest.execution.completionPolicy -ceq 'retained-fence-batch-3/1') 'Manifest adapter or completion policy differs from the execution class'
Require ($manifest.preset -cne 'pilot' -or $env:LEGS -cne 'windows-vulkan') 'Windows Vulkan has no initial pilot'
$minutes = if ($rehearsal) { $rehearsalPolicy.minutes } elseif ($manifest.preset -ceq 'pilot') { 12 } else { 35 }
Require ($manifest.limits.jobMinutes -eq $minutes -and $manifest.budget.maximumBilledMinutes -eq $minutes) 'Preset job and billing caps disagree'
if ($rehearsal) {
    Require ($manifest.limits.unitSeconds -eq $rehearsalPolicy.unitSeconds -and $manifest.limits.frameMilliseconds -eq 60000) 'Rehearsal unit or frame limits differ'
    Require ($manifest.budget.ratePerMinute -eq 0 -and $manifest.budget.maximumCostUsd -eq 0 -and $manifest.budget.currency -ceq 'USD' -and $manifest.approval.campaignMaximumUsd -eq 0) 'Rehearsal requires zero USD cost and campaign budget'
    Require (![string]::IsNullOrWhiteSpace($manifest.approval.reference) -and ![string]::IsNullOrWhiteSpace($manifest.approval.purpose) -and ![string]::IsNullOrWhiteSpace($manifest.approval.campaignReference)) 'Rehearsal requires exact owner approval references'
}
Require ($Mode -cne 'Verify' -or !$rehearsal) 'Paid Verify refuses rehearsal evidence'
Require ($Mode -cne 'VerifyRehearsal' -or $rehearsal) 'Rehearsal Verify refuses performance evidence'

if ($Mode -ceq 'Select') {
    $nativeBackend = if ($backend -ceq 'd3d11') { 'direct3d11-native' } else { 'vulkan-native' }
    $class = if ($rehearsal) { 'rehearsal' } else { 'performance' }
    $device = if ($rehearsal) { if ($backend -ceq 'vulkan') { 'llvmpipe' } else { '' } } else { 'discrete' }
    $d3d = if ($rehearsal) { if ($backend -ceq 'd3d11') { 'warp' } else { '' } } else { 'NVIDIA' }
    $matrix = @{ include = @(@{ leg = $env:LEGS; runner = $runner; backend = $nativeBackend; jobMinutes = $minutes; executionClass = $class; vulkanDevice = $device; d3dAdapter = $d3d }) } | ConvertTo-Json -Compress -Depth 4
    "matrix=$matrix"
    "jobMinutes=$minutes"
    exit 0
}
$authorization = Read-Authorization
if ($Mode -ceq 'Authorize') {
    Require (![string]::IsNullOrWhiteSpace($env:KE_RENDERER_PERF_REPORTS)) 'Explicit renderer evidence directory is required'
    New-Item -ItemType Directory -Force $env:KE_RENDERER_PERF_REPORTS | Out-Null
    [IO.File]::WriteAllText((Join-Path $env:KE_RENDERER_PERF_REPORTS 'authorization.json'), ($authorization | ConvertTo-Json -Depth 4))
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $env:KE_RENDERER_PERF_REPORTS 'manifest.json')
    "authorizationSha=$($authorization.authorizationSha)"
    exit 0
}

$directory = $env:KE_RENDERER_PERF_REPORTS
$trxPath = Join-Path $directory 'measurement.trx'
Require (Test-Path -LiteralPath $trxPath -PathType Leaf) 'Measurement TRX is missing'
[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$counters = @($trx.SelectNodes('//*[local-name()="Counters"]'))
Require ($counters.Count -eq 1) 'TRX requires exactly one Counters element'
$counts = $counters[0]
Require ($counts.total -ceq '1' -and $counts.executed -ceq '1' -and $counts.passed -ceq '1' -and $counts.failed -ceq '0' -and $counts.notExecuted -ceq '0') 'Exactly one passed executed test and zero skips are required'
$tests = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
Require ($tests.Count -eq 1 -and $tests[0].outcome -ceq 'Passed' -and $tests[0].testName -ceq 'Renderer.Perf.Tests.RendererPerfTests.Measure_authorized_unit') 'TRX does not identify the one fixed performance fact'
$json = Join-Path $directory ($env:RENDERER_PERF_UNIT + '.json')
$markdown = Join-Path $directory ($env:RENDERER_PERF_UNIT + '.md')
Require ((Test-Path -LiteralPath $json -PathType Leaf) -and (Get-Item -LiteralPath $json).Length -gt 0 -and (Get-Item -LiteralPath $json).Length -le 268435456) 'Final JSON is missing, empty or oversized'
Require ((Test-Path -LiteralPath $markdown -PathType Leaf) -and (Get-Item -LiteralPath $markdown).Length -gt 0) 'Final Markdown is missing or empty'
$stream = [IO.File]::OpenRead($json)
$document = $null
try {
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 32
    $document = [System.Text.Json.JsonDocument]::Parse($stream, $options)
    $root = $document.RootElement
    $identity = $root.GetProperty('identity')
    $publication = $root.GetProperty('publication')
    Require ($root.GetProperty('schema').GetString() -cne 'khaozengine.renderer-perf-report/1') 'Unsupported renderer report/1, expected report/2'
    Require ($root.GetProperty('schema').GetString() -ceq 'khaozengine.renderer-perf-report/2' -and $root.GetProperty('raw').GetProperty('format').GetString() -ceq 'metric-vectors/2') 'Unexpected renderer report format'
    Require ($root.GetProperty('manifest').GetProperty('preset').GetString() -ceq $manifest.preset) 'Final report execution class differs from the approved manifest'
    if ($rehearsal) {
        $assessment = $root.GetProperty('assessment')
        $reasons = $assessment.GetProperty('reasons')
        Require ($publication.GetProperty('state').GetString() -ceq 'completed' -and $publication.GetProperty('returnedByRun').GetBoolean() -and $publication.GetProperty('finalUnitPublished').GetBoolean() -and !$assessment.GetProperty('validForPromotion').GetBoolean()) 'Rehearsal requires completed non-promotable publication'
        Require ($reasons.GetArrayLength() -eq 1 -and $reasons[0].GetString() -ceq 'SoftwareRehearsalNotPerformanceEvidence' -and $root.GetProperty('compiler').GetProperty('state').GetString() -ceq 'complete') 'Rehearsal requires only its fixed non-promotion reason and Complete compiler collection'
    } else {
        Require ($publication.GetProperty('state').GetString() -ceq 'completed' -and $publication.GetProperty('returnedByRun').GetBoolean() -and $publication.GetProperty('finalUnitPublished').GetBoolean() -and $root.GetProperty('assessment').GetProperty('validForPromotion').GetBoolean()) 'Final report is unfinalized or invalid for promotion'
    }
    foreach ($pair in @(@('unitId', $env:RENDERER_PERF_UNIT), @('manifestSha256', $env:RENDERER_PERF_MANIFEST_SHA256), @('workloadSha', $env:RENDERER_PERF_WORKLOAD_SHA), @('authorizationSha', $authorization.authorizationSha), @('workflowSha', $env:GITHUB_WORKFLOW_SHA), @('runId', $env:GITHUB_RUN_ID), @('backend', $backend))) {
        Require ($identity.GetProperty($pair[0]).GetString() -ceq $pair[1]) "Final report $($pair[0]) differs from execution"
    }
    if ($rehearsal) {
        Require ($identity.GetProperty('attempt').GetInt32() -eq 1 -and $identity.GetProperty('softwareAdapter').GetBoolean() -and $identity.GetProperty('backend').GetString() -ceq $backend) 'Rehearsal requires actual first-attempt software identity on the approved leg'
    } else {
        Require ($identity.GetProperty('attempt').GetInt32() -eq 1 -and $identity.GetProperty('vendorId').GetUInt32() -eq 0x10de -and $identity.GetProperty('deviceId').GetUInt32() -eq 0x1eb8 -and !$identity.GetProperty('softwareAdapter').GetBoolean()) 'Final report lacks first-attempt native NVIDIA T4 identity'
    }
} finally {
    if ($null -ne $document) { $document.Dispose() }
    $stream.Dispose()
}
$verifier = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/Renderer.Perf.Verify/bin/Release/net10.0/Renderer.Perf.Verify.dll'
Require (Test-Path -LiteralPath $verifier -PathType Leaf) 'Trusted managed renderer verifier was not built'
& dotnet $verifier $directory ($env:RENDERER_PERF_UNIT + '.json')
Require ($LASTEXITCODE -eq 0) 'Managed renderer directory verification refused the received evidence'
if ($rehearsal) { 'Renderer correctness evidence accepted' } else { 'Renderer evidence accepted' }
