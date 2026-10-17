param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'))
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($outputRoot)
$staging=Join-Path $outputRoot ('.build-'+[guid]::NewGuid().ToString('N'))
$build=Join-Path $staging 'HuntTracker'
[void][IO.Directory]::CreateDirectory($build)
try {
    foreach ($name in @('Start.bat','Stop.bat','Overlay.html','Statistics.html','client.js','statistics.js','statistics.css','config.example.json','README.md')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $build
    }
    [void][IO.Directory]::CreateDirectory((Join-Path $build 'app'))
    foreach ($name in @('Tracker.ps1','Alerts.ps1','Files.ps1','Panel.ps1','Dashboard.ps1','Statistics.ps1','Launcher.ps1')) {
        Copy-Item -LiteralPath (Join-Path $root ('app/'+$name)) -Destination (Join-Path $build 'app')
    }
    foreach ($asset in @('Images/kill.png','Sounds/kill.mp3','docs/architecture.md')) {
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
