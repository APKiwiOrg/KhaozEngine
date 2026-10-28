param([ValidateSet('Select', 'Authorize', 'Verify', 'VerifyRehearsal')][string]$Mode)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require([bool]$Condition, [string]$Reason) {
    if (!$Condition) { throw $Reason }
}
# Verified against the managed record constructors by the workflow field parity fact.
# Record|exact JSON field|kind (with nested record where needed)|nullability. Every field is required.
$manifestFieldTable = @'
RendererPerfManifest|schema|string|non-null
RendererPerfManifest|unitId|string|non-null
RendererPerfManifest|preset|string|non-null
RendererPerfManifest|workloadSha|string|non-null
RendererPerfManifest|baselineMainSha|string|non-null
RendererPerfManifest|recipes|array:RendererPerfRecipeIdentity|non-null
RendererPerfManifest|blocks|array:RendererPerfBlock|non-null
RendererPerfManifest|controls|array:RendererPerfControl|non-null
RendererPerfManifest|conditioningFrames|integer|non-null
RendererPerfManifest|batchSize|integer|non-null
RendererPerfManifest|maximumScopeOccurrences|integer|non-null
RendererPerfManifest|runtime|object:RendererPerfRuntimePolicy|non-null
RendererPerfManifest|execution|object:RendererPerfExecutionPolicy|non-null
RendererPerfManifest|limits|object:RendererPerfLimits|non-null
RendererPerfManifest|budget|object:RendererPerfBudget|non-null
RendererPerfManifest|approval|object:RendererPerfApproval|non-null
RendererPerfRecipeIdentity|sceneId|string|non-null
RendererPerfRecipeIdentity|version|string|non-null
RendererPerfRecipeIdentity|profile|string|non-null
RendererPerfRecipeIdentity|recipeSha256|string|non-null
RendererPerfRecipeIdentity|settingsSha256|string|non-null
RendererPerfRecipeIdentity|shaderSha256|string|non-null
RendererPerfRecipeIdentity|geometrySha256|string|non-null
RendererPerfRecipeIdentity|assetSha256|string|non-null
RendererPerfBlock|sceneId|string|non-null
RendererPerfBlock|layout|enum-string|non-null
RendererPerfBlock|iteration|integer|non-null
RendererPerfBlock|warmupFrames|integer|non-null
RendererPerfBlock|measuredFrames|integer|non-null
RendererPerfControl|id|integer|non-null
RendererPerfControl|version|string|non-null
RendererPerfControl|workSha256|string|non-null
RendererPerfControl|warmupFrames|integer|non-null
RendererPerfControl|measuredFrames|integer|non-null
RendererPerfRuntimePolicy|configuration|string|non-null
RendererPerfRuntimePolicy|runtimeVersion|string|non-null
RendererPerfRuntimePolicy|environment|string-map|non-null
RendererPerfRuntimePolicy|gcMode|string|non-null
RendererPerfRuntimePolicy|tieredCompilation|boolean|non-null
RendererPerfExecutionPolicy|backend|string|non-null
RendererPerfExecutionPolicy|adapterPolicy|string|non-null
RendererPerfExecutionPolicy|runner|string|non-null
RendererPerfExecutionPolicy|workflow|string|non-null
RendererPerfExecutionPolicy|leg|string|non-null
RendererPerfExecutionPolicy|completionPolicy|string|non-null
RendererPerfExecutionPolicy|lockedGraphicsClockMHz|integer|nullable
RendererPerfLimits|unitSeconds|integer|non-null
RendererPerfLimits|frameMilliseconds|integer|non-null
RendererPerfLimits|coldFrameMilliseconds|integer|non-null
RendererPerfLimits|jobMinutes|integer|non-null
RendererPerfBudget|maximumBilledMinutes|integer|non-null
RendererPerfBudget|ratePerMinute|decimal|non-null
RendererPerfBudget|currency|string|non-null
RendererPerfBudget|maximumCostUsd|decimal|non-null
RendererPerfApproval|reference|string|non-null
RendererPerfApproval|purpose|string|non-null
RendererPerfApproval|campaignReference|string|non-null
RendererPerfApproval|campaignMaximumUsd|decimal|non-null
'@
$manifestFields = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
foreach ($row in $manifestFieldTable -split '\r?\n') {
    $type, $name, $kind, $nullable = $row.Split('|')
    if (!$manifestFields.ContainsKey($type)) {
        $manifestFields.Add($type, [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal))
    }
    $manifestFields[$type].Add($name, @($kind, $nullable))
}
function Manifest-FieldPath([string]$Path, [string]$Name) {
    if ($Path -eq '') { return $Name }
    return "$Path.$Name"
}
function Assert-ManifestObject([object]$Value, [string]$Type, [string]$Path) {
    $objectPath = if ($Path -eq '') { '$' } else { $Path }
    Require ($Value -is [hashtable]) "Manifest field $objectPath is unknown, missing, null or of the wrong kind"
    $fields = $manifestFields[$Type]
    foreach ($entry in $Value.GetEnumerator()) {
        $fieldPath = Manifest-FieldPath $Path $entry.Key
        Require ($fields.ContainsKey($entry.Key)) "Manifest field $fieldPath is unknown, missing, null or of the wrong kind"
    }
    foreach ($field in $fields.GetEnumerator()) {
        $fieldPath = Manifest-FieldPath $Path $field.Key
        Require ($Value.ContainsKey($field.Key)) "Manifest field $fieldPath is unknown, missing, null or of the wrong kind"
        Assert-ManifestField $Value[$field.Key] $field.Value[0] ($field.Value[1] -ceq 'nullable') $fieldPath
    }
}
function Assert-ManifestField([object]$Value, [string]$Kind, [bool]$Nullable, [string]$Path) {
    $reason = "Manifest field $Path is unknown, missing, null or of the wrong kind"
    if ($null -eq $Value) {
        Require $Nullable $reason
        return
    }
    switch -CaseSensitive ($Kind) {
        string { Require ($Value -is [string]) $reason }
        integer { Require ($Value -is [long] -and $Value -ge [int]::MinValue -and $Value -le [int]::MaxValue) $reason }
        decimal { Require ($Value -is [long] -or $Value -is [decimal]) $reason }
        boolean { Require ($Value -is [bool]) $reason }
        enum-string { Require ($Value -is [string]) $reason }
        string-map {
            Require ($Value -is [hashtable]) $reason
            foreach ($entry in $Value.GetEnumerator()) {
                Assert-ManifestField $entry.Value 'string' $false (Manifest-FieldPath $Path $entry.Key)
            }
        }
        default {
            $container, $type = $Kind.Split(':')
            if ($container -ceq 'object') {
                Assert-ManifestObject $Value $type $Path
            } elseif ($container -ceq 'array') {
                Require ($Value -is [object[]]) $reason
                for ($index = 0; $index -lt $Value.Length; $index++) {
                    Assert-ManifestObject $Value[$index] $type "$Path[$index]"
                }
            } else { throw $reason }
        }
    }
}
# ConvertFrom-Json keeps the last duplicate, ignores key case and reads doubles that round exact caps.
# Match the managed reader with unique ordinal names and exact integer or decimal numbers throughout.
function Read-ManifestValue([System.Text.Json.JsonElement]$Element) {
    switch ($Element.ValueKind) {
        Object {
            $value = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                Require (!$value.ContainsKey($property.Name)) "Duplicate field '$($property.Name)'."
                $value.Add($property.Name, (Read-ManifestValue $property.Value))
            }
            return $value
        }
        Array {
            $items = [System.Collections.Generic.List[object]]::new()
            foreach ($item in $Element.EnumerateArray()) {
                Require ($item.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) 'Null array entries are forbidden.'
                $items.Add((Read-ManifestValue $item))
            }
            return ,$items.ToArray()
        }
        Number {
            $integer = [long]0
            if ($Element.TryGetInt64([ref]$integer)) { return $integer }
            $number = [decimal]0
            Require ($Element.TryGetDecimal([ref]$number)) 'Nonfinite or excessive numeric value.'
            return $number
        }
        String { return $Element.GetString() }
        True { return $true }
        False { return $false }
        Null { return $null }
        default { throw 'Unsupported manifest JSON value.' }
    }
}
function Read-Manifest([string]$Text) {
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 32
    $document = [System.Text.Json.JsonDocument]::Parse($Text, $options)
    try {
        $value = Read-ManifestValue $document.RootElement
        Assert-ManifestObject $value 'RendererPerfManifest' ''
        return $value
    } finally {
        $document.Dispose()
    }
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
$manifestText = Get-Content -LiteralPath $manifestPath -Raw
$manifest = Read-Manifest $manifestText
Require ($manifest.preset -cin @('pilot', 'full', 'rehearsal', 'rehearsal-full', 'rehearsal-controls', 'water-optics-v2', 'rehearsal-water-optics-v2', 'rehearsal-water-optics-v2-single')) 'Unknown renderer preset'
$rehearsal = $manifest.preset -cin @('rehearsal', 'rehearsal-full', 'rehearsal-controls', 'rehearsal-water-optics-v2', 'rehearsal-water-optics-v2-single')
$rehearsalPolicy = if ($rehearsal) {
    switch -CaseSensitive ("$($manifest.preset)/$($env:LEGS)") {
        'rehearsal/linux-vulkan' { @{ runner = 'ubuntu-24.04'; adapter = 'mesa-lavapipe-software'; unitSeconds = 5400; minutes = 120 } }
        'rehearsal/windows-d3d11' { @{ runner = 'windows-2025-vs2026'; adapter = 'microsoft-warp-software'; unitSeconds = 5400; minutes = 120 } }
        'rehearsal-full/linux-vulkan' { @{ runner = 'ubuntu-24.04'; adapter = 'mesa-lavapipe-software'; unitSeconds = 19800; minutes = 350 } }
        'rehearsal-full/windows-d3d11' { @{ runner = 'windows-2025-vs2026'; adapter = 'microsoft-warp-software'; unitSeconds = 19800; minutes = 350 } }
        'rehearsal-controls/linux-vulkan' { @{ runner = 'ubuntu-24.04'; adapter = 'mesa-lavapipe-software'; unitSeconds = 5400; minutes = 120 } }
        'rehearsal-controls/windows-d3d11' { @{ runner = 'windows-2025-vs2026'; adapter = 'microsoft-warp-software'; unitSeconds = 5400; minutes = 120 } }
        'rehearsal-water-optics-v2/linux-vulkan' { @{ runner = 'ubuntu-24.04'; adapter = 'mesa-lavapipe-software'; unitSeconds = 19800; minutes = 350 } }
        'rehearsal-water-optics-v2-single/windows-d3d11' { @{ runner = 'windows-2025-vs2026'; adapter = 'microsoft-warp-software'; unitSeconds = 19800; minutes = 350 } }
        default { throw 'No software rehearsal class admits this preset and leg' }
    }
} else { $null }
$runner = if ($rehearsal) { $rehearsalPolicy.runner } elseif ($env:LEGS -ceq 'linux-vulkan') { 'gpu-t4-linux' } else { 'gpu-t4-windows' }
$backend = if ($env:LEGS -ceq 'windows-d3d11') { 'd3d11' } else { 'vulkan' }
Require ($manifest.schema -ceq 'khaozengine.renderer-perf-unit/1' -and $manifest.unitId -ceq $env:RENDERER_PERF_UNIT -and $manifest.workloadSha -ceq $env:RENDERER_PERF_WORKLOAD_SHA) 'Manifest identity differs from the dispatch'
Require ($manifest.execution.leg -ceq $env:LEGS -and $manifest.execution.runner -ceq $runner -and $manifest.execution.backend -ceq $backend -and $manifest.execution.workflow -ceq 'gpu-hosted-bench.yml') 'Manifest execution differs from the selected leg'
Require ($manifest.execution.adapterPolicy -ceq $(if ($rehearsal) { $rehearsalPolicy.adapter } else { 'nvidia-t4-hardware' }) -and $manifest.execution.completionPolicy -ceq 'retained-fence-batch-3/1') 'Manifest adapter or completion policy differs from the execution class'
Require ($manifest.execution.ContainsKey('lockedGraphicsClockMHz')) 'Manifest requires an explicit graphics clock policy'
$lockedClock = $manifest.execution.lockedGraphicsClockMHz
if ($rehearsal) {
    Require ($null -eq $lockedClock) 'Software rehearsal requires a null graphics clock policy'
} else {
    Require ($lockedClock -is [long] -and $lockedClock -eq 1095) 'Paid T4 graphics clock must be an explicit 1095 MHz integer'
}
Require ($manifest.preset -cne 'pilot' -or $env:LEGS -cne 'windows-vulkan') 'Windows Vulkan has no initial pilot'
Require ($manifest.preset -cne 'water-optics-v2' -or $env:LEGS -cne 'windows-vulkan') 'Windows Vulkan is not an approved leg for the water-optics-v2 campaign.'
$campaignMaximumUsd = 22d
$waterOpticsV2CampaignMaximumUsd = 7.382d
$linuxT4RatePerMinuteUsd = 0.052d
$windowsT4RatePerMinuteUsd = 0.102d
# Windows full allows for the slow GRID setup and clock query seen in run 40599810511.
$minutes = if ($rehearsal) { $rehearsalPolicy.minutes } elseif ($manifest.preset -ceq 'pilot') { if ($env:LEGS -ceq 'linux-vulkan') { 12 } else { 20 } } elseif ($env:LEGS -ceq 'linux-vulkan') { 35 } else { 50 }
$billedMinutes = if ($rehearsal) { $minutes } else { $minutes + 3 } # Rounded-up cleanup overrun in run 40435928569.
Require ($manifest.limits.jobMinutes -eq $minutes -and $manifest.budget.maximumBilledMinutes -eq $billedMinutes) 'Preset job and billing caps disagree'
Require ($manifest.limits.coldFrameMilliseconds -is [long] -and $manifest.limits.coldFrameMilliseconds -eq 120000) 'Preset cold frame limit must be an explicit 120000 ms integer'
if ($rehearsal) {
    Require ($manifest.limits.unitSeconds -eq $rehearsalPolicy.unitSeconds -and $manifest.limits.frameMilliseconds -eq 60000) 'Rehearsal unit or frame limits differ'
    Require ($manifest.budget.ratePerMinute -eq 0 -and $manifest.budget.maximumCostUsd -eq 0 -and $manifest.budget.currency -ceq 'USD' -and $manifest.approval.campaignMaximumUsd -eq 0) 'Rehearsal requires zero USD cost and campaign budget'
    Require (![string]::IsNullOrWhiteSpace($manifest.approval.reference) -and ![string]::IsNullOrWhiteSpace($manifest.approval.purpose) -and ![string]::IsNullOrWhiteSpace($manifest.approval.campaignReference)) 'Rehearsal requires exact owner approval references'
} else {
    $campaignCap = $manifest.approval.campaignMaximumUsd
    $unitBudget = $manifest.budget.maximumCostUsd
    Require ($campaignCap -is [long] -or $campaignCap -is [decimal]) 'Manifest approval.campaignMaximumUsd must be an exact USD decimal'
    Require ($unitBudget -is [long] -or $unitBudget -is [decimal]) 'Manifest budget.maximumCostUsd must be an exact USD decimal'
    $rate = if ($env:LEGS -ceq 'linux-vulkan') { $linuxT4RatePerMinuteUsd } else { $windowsT4RatePerMinuteUsd }
    Require ($manifest.budget.maximumBilledMinutes -is [long] -and $manifest.budget.maximumBilledMinutes -eq $billedMinutes -and ($manifest.budget.ratePerMinute -is [long] -or $manifest.budget.ratePerMinute -is [decimal]) -and $manifest.budget.ratePerMinute -eq $rate -and $manifest.budget.currency -is [string] -and $manifest.budget.currency -ceq 'USD' -and $unitBudget -eq ($billedMinutes * $rate)) 'Paid billed minutes, rate, currency or cost cap differs from the preset'
    $campaignMaximum = if ($manifest.preset -ceq 'water-optics-v2') { $waterOpticsV2CampaignMaximumUsd } else { $campaignMaximumUsd }
    Require ($campaignCap -ge $unitBudget -and $campaignCap -le $campaignMaximum) 'Paid campaign cap must cover the unit budget and stay within the preset maximum'
    Require ($manifest.approval.reference -is [string] -and ![string]::IsNullOrWhiteSpace($manifest.approval.reference) -and $manifest.approval.purpose -is [string] -and ![string]::IsNullOrWhiteSpace($manifest.approval.purpose) -and $manifest.approval.campaignReference -is [string] -and ![string]::IsNullOrWhiteSpace($manifest.approval.campaignReference)) 'Paid requires exact owner approval references'
}
Require ($Mode -cne 'Verify' -or !$rehearsal) 'Paid Verify refuses rehearsal evidence'
Require ($Mode -cne 'VerifyRehearsal' -or $rehearsal) 'Rehearsal Verify refuses performance evidence'

if ($Mode -ceq 'Select') {
    $nativeBackend = if ($backend -ceq 'd3d11') { 'direct3d11-native' } else { 'vulkan-native' }
    $class = if ($rehearsal) { 'rehearsal' } else { 'performance' }
    $device = if ($rehearsal) { if ($backend -ceq 'vulkan') { 'llvmpipe' } else { '' } } else { 'discrete' }
    $d3d = if ($rehearsal) { if ($backend -ceq 'd3d11') { 'warp' } else { '' } } else { 'NVIDIA' }
    $matrix = @{ include = @(@{ leg = $env:LEGS; runner = $runner; backend = $nativeBackend; jobMinutes = $minutes; executionClass = $class; vulkanDevice = $device; d3dAdapter = $d3d; lockedGraphicsClockMHz = $lockedClock }) } | ConvertTo-Json -Compress -Depth 4
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
        $gpuClock = $root.GetProperty('gpuClock')
        Require ($gpuClock.GetProperty('state').GetString() -ceq 'verified' -and $gpuClock.GetProperty('lockedGraphicsClockMHz').GetInt32() -eq $manifest.execution.lockedGraphicsClockMHz -and $gpuClock.GetProperty('samples').GetInt32() -gt 0) 'GpuClockNotLocked. Final report lacks verified graphics-clock telemetry'
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
