$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish src/OtterMonitor.App/OtterMonitor.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o app --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Bygget misslyckades.' }
    Write-Host 'Klart: app\OtterMonitor.exe'
} finally {
    Pop-Location
}
