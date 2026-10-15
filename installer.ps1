$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & ./build.ps1
    $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) { throw 'Inno Setup 6 saknas. Installera med: winget install --id JRSoftware.InnoSetup -e --scope user' }
    $version = (Get-Item app\OtterMonitor.exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    & $iscc /Qp "/DAppVersion=$version" installer\OtterMonitor.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installern kunde inte byggas.' }
    Write-Host "Klart: dist\OtterMonitor-Setup-$version.exe"
} finally {
    Pop-Location
}
