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
$archive = Join-Path $root "dist/RouteMeter-$version-windows-x64.zip"
Compress-Archive -Path "$stage/*" -DestinationPath $archive -Force
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$name = Split-Path $archive -Leaf
[IO.File]::WriteAllText("$archive.sha256", "$hash  $name`n", [Text.UTF8Encoding]::new($false))
