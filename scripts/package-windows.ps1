param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$version = (Get-Content VERSION -Raw).Trim()
$stage = Join-Path $root "dist/windows/$Runtime"
dotnet publish windows/RouteMeter.Windows/RouteMeter.Windows.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "Windows viewer build failed" }
$core = Join-Path $root "dist/core/windows-amd64/RouteMeterCore.exe"
if (!(Test-Path $core)) { throw "Build the matching RouteMeter Core first" }
Copy-Item $core $stage
Copy-Item engine/licenses/GPL-3.0.txt (Join-Path $stage "LICENSE-Core-GPL-3.0.txt")
Copy-Item LICENSE (Join-Path $stage "LICENSE-Viewer-MIT.txt")
Copy-Item docs/CORE-SETUP.md $stage
Copy-Item THIRD-PARTY-NOTICES.md $stage
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget/packages" }
foreach ($package in @("microsoft.netcore.app.runtime.win-x64", "microsoft.windowsdesktop.app.runtime.win-x64")) {
  $folder = Join-Path $cache $package
  if (Test-Path $folder) {
    Get-ChildItem $folder -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.?PARTY.?NOTICES).*(txt|md)$' } | ForEach-Object {
      Copy-Item $_.FullName (Join-Path $stage ($package + "-" + $_.Name)) -Force
    }
  }
}
$archive = Join-Path $root "dist/RouteMeter-$version-windows-x64.zip"
Compress-Archive -Path "$stage/*" -DestinationPath $archive -Force
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$name = Split-Path $archive -Leaf
[IO.File]::WriteAllText("$archive.sha256", "$hash  $name`n", [Text.UTF8Encoding]::new($false))
