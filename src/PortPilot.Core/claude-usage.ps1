# PortPilot usage bridge v1. Only normalized quota fields are saved; stdin is never logged.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$quotaTemporary = $null
try {
    $quotaInput = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $quotaWindows = @()
    foreach ($quotaEntry in @(@{Key='five_hour'; Minutes=300}, @{Key='seven_day'; Minutes=10080})) {
        $quotaWindow = $quotaInput.rate_limits.($quotaEntry.Key)
        if ($null -eq $quotaWindow.used_percentage -or $quotaWindow.used_percentage -is [string]) { continue }
        $quotaUsed = [double]$quotaWindow.used_percentage
        if ([double]::IsNaN($quotaUsed) -or [double]::IsInfinity($quotaUsed)) { continue }
        $quotaReset = $null
        if ($null -ne $quotaWindow.resets_at -and $quotaWindow.resets_at -isnot [string]) {
            $quotaSeconds = [long]$quotaWindow.resets_at
            if ($quotaSeconds -gt 0 -and $quotaSeconds -le 253402300799) { $quotaReset = $quotaSeconds }
        }
        $quotaWindows += @{ minutes=$quotaEntry.Minutes; usedPercent=[Math]::Max(0, [Math]::Min(100, $quotaUsed)); resetsAt=$quotaReset }
    }
    if ($quotaWindows.Count -eq 0) {
        [Console]::Write('Claude Code | usage pending')
        exit 0
    }
    $quotaFile = Join-Path $PSScriptRoot 'claude-usage.json'
    $quotaTemporary = Join-Path $PSScriptRoot ('claude-usage.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $quotaJson = @{ observedAt=[DateTimeOffset]::UtcNow.ToString('o'); windows=@($quotaWindows) } | ConvertTo-Json -Depth 4 -Compress
    [IO.File]::WriteAllText($quotaTemporary, $quotaJson, [Text.UTF8Encoding]::new($false))
    # Same-directory replacement: the app cannot read a half-written snapshot.
    if ([IO.File]::Exists($quotaFile)) { [IO.File]::Replace($quotaTemporary, $quotaFile, [NullString]::Value) }
    else { [IO.File]::Move($quotaTemporary, $quotaFile) }
    $quotaText = @($quotaWindows | ForEach-Object {
        $quotaPeriod = if ($_.minutes -eq 300) { '5h' } else { '7d' }
        if ($null -ne $_.resetsAt -and $_.resetsAt -le [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) { "$quotaPeriod --" }
        else { $quotaPeriod + ' ' + (100 - $_.usedPercent).ToString('0.#', [Globalization.CultureInfo]::InvariantCulture) + '% left' }
    })
    [Console]::Write('Claude Code | ' + ($quotaText -join ' | '))
} catch {
    [Console]::Write('Claude Code | usage unavailable')
} finally {
    if ($quotaTemporary -and [IO.File]::Exists($quotaTemporary)) { [IO.File]::Delete($quotaTemporary) }
}
