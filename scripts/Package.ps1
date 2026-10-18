param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'))
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($outputRoot)
$staging=Join-Path $outputRoot ('.build-'+[guid]::NewGuid().ToString('N'))
$build=Join-Path $staging 'hunt-tracker'
[void][IO.Directory]::CreateDirectory($build)
try {
    & dotnet publish (Join-Path $root 'src/hunt-tracker-desktop/HuntTracker.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $build 'app') -p:RestoreLockedMode=true --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    foreach ($name in @('Start.bat','Stop.bat','Overlay.html','Statistics.html','client.js','statistics.js','statistics.css','config.example.json','README.md','THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $build
    }
    foreach ($asset in @('source/images/kill/skull.png','source/images/death/skull.png','source/images/trade/skull.png','source/sounds/kill/kill.mp3','source/sounds/death/death.wav','source/sounds/trade/trade.wav','docs/architecture.md')) {
        $destination=Join-Path $build $asset
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        Copy-Item -LiteralPath (Join-Path $root $asset) -Destination $destination
    }
    $temporaryZip=Join-Path $staging 'HuntTracker.zip'
    Compress-Archive -LiteralPath $build -DestinationPath $temporaryZip
    $result=Join-Path $outputRoot 'HuntTracker.zip'
    Move-Item -LiteralPath $temporaryZip -Destination $result -Force
    Write-Output $result
} finally {
    $resolved=[IO.Path]::GetFullPath($staging)
    if (-not $resolved.StartsWith($outputRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
