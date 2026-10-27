param([Parameter(Mandatory)][ValidateSet('Lock', 'Reset')][string]$Mode)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

if ($env:RENDERER_PERF_EXECUTION_CLASS -cne 'performance') { throw 'GpuClockNotLocked. Only paid performance jobs may change GPU clocks' }
if ($env:RUNNER_OS -cnotin @('Linux', 'Windows')) { throw 'GpuClockNotLocked. Unsupported runner platform' }
if ([string]::IsNullOrWhiteSpace($env:KE_RENDERER_PERF_REPORTS)) { throw 'GpuClockNotLocked. Explicit evidence directory is required' }
New-Item -ItemType Directory -Force $env:KE_RENDERER_PERF_REPORTS | Out-Null
$stem = if ($Mode -ceq 'Lock') { 'gpu-clock-lock' } else { 'gpu-clock-reset' }
$log = Join-Path $env:KE_RENDERER_PERF_REPORTS ($stem + '.log')
$evidence = [ordered]@{
    schema = 'renderer-gpu-clock-lock/1'
    mode = $Mode
    state = 'refused'
    code = 'GpuClockNotLocked'
    requestedGraphicsClockMHz = $null
    appliedMinimumMHz = $null
    appliedMaximumMHz = $null
    observedGraphicsClockMHz = $null
    maximumGraphicsClockMHz = $null
    reason = $null
}
function Invoke-Nvidia([string[]]$Arguments, [bool]$Elevated = $false) {
    $prefix = if ($Elevated -and $env:RUNNER_OS -ceq 'Linux') { 'sudo nvidia-smi' } else { 'nvidia-smi' }
    [IO.File]::AppendAllText($log, $prefix + ' ' + ($Arguments -join ' ') + [Environment]::NewLine)
    try {
        $lines = if ($Elevated -and $env:RUNNER_OS -ceq 'Linux') { @(& sudo nvidia-smi @Arguments 2>&1) } else { @(& nvidia-smi @Arguments 2>&1) }
        $code = $LASTEXITCODE
        $output = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        [IO.File]::AppendAllText($log, $output + [Environment]::NewLine + "exit=$code" + [Environment]::NewLine)
        if ($code -ne 0) { throw "nvidia-smi exited $code. See $stem.log" }
        return $output
    } catch {
        [IO.File]::AppendAllText($log, $_.Exception.Message + [Environment]::NewLine)
        throw
    }
}
try {
    if ($Mode -ceq 'Reset') {
        $null = Invoke-Nvidia @('--reset-gpu-clocks') $true
        $evidence.state = 'reset'
        $evidence.code = $null
    } else {
        if ($env:RENDERER_PERF_LOCKED_GRAPHICS_CLOCK_MHZ -cne '1200') { throw 'The trusted matrix must supply the fixed paid graphics clock' }
        $clock = [int]$env:RENDERER_PERF_LOCKED_GRAPHICS_CLOCK_MHZ
        $evidence.requestedGraphicsClockMHz = $clock
        if ($env:RUNNER_OS -ceq 'Linux') { $null = Invoke-Nvidia @('--persistence-mode=1') $true }
        $reply = Invoke-Nvidia @("--lock-gpu-clocks=$clock,$clock") $true
        # NVIDIA returns its applied range in the command acknowledgement. Hardware max is separate.
        $ranges = [regex]::Matches($reply, 'gpuClkMin\s+(\d+),\s*gpuClkMax\s+(\d+)')
        if ($ranges.Count -ne 1) { throw 'Driver did not acknowledge one supported graphics-clock lock range' }
        $evidence.appliedMinimumMHz = [int]$ranges[0].Groups[1].Value
        $evidence.appliedMaximumMHz = [int]$ranges[0].Groups[2].Value
        if ($evidence.appliedMinimumMHz -ne $clock -or $evidence.appliedMaximumMHz -ne $clock) { throw 'Driver applied a different graphics-clock range' }
        $readback = Invoke-Nvidia @('--query-gpu=clocks.current.graphics,clocks.max.graphics', '--format=csv,noheader,nounits')
        $null = Invoke-Nvidia @('-q', '-d', 'CLOCK,PERFORMANCE,POWER')
        if ($readback -cnotmatch '\A\s*(\d+)\s*,\s*(\d+)\s*\z') { throw 'One identified numeric graphics-clock readback is required' }
        $evidence.observedGraphicsClockMHz = [int]$Matches[1]
        $evidence.maximumGraphicsClockMHz = [int]$Matches[2]
        # Current clocks may idle before the workload. Counted execution telemetry verifies the hold.
        $evidence.state = 'applied'
        $evidence.code = $null
    }
} catch {
    $evidence.reason = $_.Exception.Message
    throw "GpuClockNotLocked. $($evidence.reason)"
} finally {
    [IO.File]::WriteAllText((Join-Path $env:KE_RENDERER_PERF_REPORTS ($stem + '.json')), ($evidence | ConvertTo-Json -Depth 4))
}
