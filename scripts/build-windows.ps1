param(
    [string]$MihomoPath = '',
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist\RouteMeter-Windows-win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try {
    $tests = 'windows/RouteMeter.Tests/RouteMeter.Tests.csproj'
    & $Dotnet build $tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
    $executable = Join-Path $projectRoot 'windows\RouteMeter.Windows\bin\Release\net8.0-windows\RouteMeter.exe'
    $testArgs = @('--relay-exe', $executable)
    if ($MihomoPath) { $testArgs += @('--mihomo', [IO.Path]::GetFullPath($MihomoPath)) }
    & $Dotnet run --project $tests -c Release --no-build -- @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed' }
    & node windows/test-profile.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Profile script tests failed' }
    & $Dotnet publish windows/RouteMeter.Windows/RouteMeter.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'windows\README.zh-CN.md') -Destination (Join-Path $OutputDirectory '使用说明.md')
    # Include the notices from the exact restored .NET runtime packages.
    $assets = Get-Content -Raw windows/RouteMeter.Windows/obj/project.assets.json | ConvertFrom-Json
    $packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    $downloads = @($assets.project.frameworks.PSObject.Properties | ForEach-Object { $_.Value.downloadDependencies })
    $corePackage = $downloads | Where-Object { $_.name -eq 'Microsoft.NETCore.App.Runtime.win-x64' } | Select-Object -First 1
    $desktopPackage = $downloads | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App.Runtime.win-x64' } | Select-Object -First 1
    if ($packageRoot -and $corePackage -and $desktopPackage) {
        $coreVersion = $corePackage.version.Trim('[', ']').Split(',')[0].Trim()
        $desktopVersion = $desktopPackage.version.Trim('[', ']').Split(',')[0].Trim()
        $corePath = Join-Path $packageRoot ('microsoft.netcore.app.runtime.win-x64/' + $coreVersion)
        $desktopPath = Join-Path $packageRoot ('microsoft.windowsdesktop.app.runtime.win-x64/' + $desktopVersion)
        Copy-Item -LiteralPath (Join-Path $corePath 'LICENSE.TXT') -Destination (Join-Path $OutputDirectory 'DOTNET-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $corePath 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $OutputDirectory 'DOTNET-THIRD-PARTY-NOTICES.txt')
        Copy-Item -LiteralPath (Join-Path $desktopPath 'LICENSE') -Destination (Join-Path $OutputDirectory 'WINDOWSDESKTOP-LICENSE.txt')
    } else { throw 'Could not locate .NET runtime license files' }
    $zip = $OutputDirectory + '.zip'
    $files = @('RouteMeter.exe', 'LICENSE.txt', '使用说明.md', 'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt', 'WINDOWSDESKTOP-LICENSE.txt') | ForEach-Object { Join-Path $OutputDirectory $_ }
    Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($zip) + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Created $zip"
} finally { Pop-Location }
