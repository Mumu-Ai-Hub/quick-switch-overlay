$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $env:APPDATA 'QuickAppsOverlay\settings.json'
$saved = $null
if (Test-Path -LiteralPath $settingsPath) { $saved = [IO.File]::ReadAllBytes($settingsPath) }
try {
    dotnet run --project (Join-Path $PSScriptRoot 'P049Checks\P049Checks.csproj') -c Release
    $checkExit = $LASTEXITCODE
}
finally {
    if ($null -ne $saved) { [IO.File]::WriteAllBytes($settingsPath, $saved) }
    elseif (Test-Path -LiteralPath $settingsPath) { Remove-Item -LiteralPath $settingsPath }
}
exit $checkExit
