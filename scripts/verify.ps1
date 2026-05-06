$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host '1/4 scan source for mojibake markers...'
$markers = @(
  [char]0x6D7C,
  [char]0x9422,
  [char]0x9365,
  [char]0x93B4,
  ([string]([char]0x951B) + [string]([char]0x6B7F)),
  [char]0x9208,
  [char]0x702D,
  [char]0x9427,
  ([string]([char]0x7BA1) + [string]([char]0xFF24)),
  ([string]([char]0x6FBE) + [string]([char]0x8FAB)),
  ([string]([char]0x6D93) + [string]([char]0x5DB6)),
  ([string]([char]0x9359) + [string]([char]0x508C)),
  [char]0x5A67
)
$pattern = ($markers | ForEach-Object { [regex]::Escape([string]$_) }) -join '|'
$mojibake = rg $pattern src/backend src/frontend/src -n --glob '!**/wwwroot/**' --glob '!**/dist/**' --glob '!**/node_modules/**'
if ($LASTEXITCODE -eq 0) {
  Write-Host $mojibake
  throw 'Mojibake markers found in source.'
}
if ($LASTEXITCODE -gt 1) {
  throw 'Source scan failed.'
}

Write-Host '2/4 validate backend configuration JSON...'
Get-ChildItem src/backend -Filter 'appsettings*.json' | ForEach-Object {
  try {
    Get-Content $_.FullName -Raw | ConvertFrom-Json | Out-Null
  } catch {
    throw "Invalid JSON config: $($_.FullName). $($_.Exception.Message)"
  }
}

Write-Host '3/4 build solution...'
dotnet build VeraMedia.sln
if ($LASTEXITCODE -ne 0) {
  throw 'dotnet build failed.'
}

Write-Host '4/4 run frontend lint...'
npm run lint --prefix src/frontend
if ($LASTEXITCODE -ne 0) {
  throw 'frontend lint failed.'
}

Write-Host 'Verification passed.'
