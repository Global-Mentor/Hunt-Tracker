$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Fixtures.ps1')
$fixture=New-TestDirectory 'package'
$zip=& (Join-Path $root 'scripts/Package.ps1') -OutputDirectory $fixture
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries=@($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    $forbidden=@($entries | Where-Object { $_ -match '(^|/)(config.json|data|Logs|_runtime|AGENTS.md|\.local|\.git|tests)(/|$)' })
    if ($forbidden.Count) { throw 'Private or development files in package' }
    foreach ($asset in @('Images/kill.png','Sounds/kill.mp3')) {
        if (('HuntTracker/'+$asset) -notin $entries) { throw "Missing bundled media: $asset" }
    }
} finally { $archive.Dispose() }
$out=Join-Path $fixture 'unpacked'
Expand-Archive -LiteralPath $zip -DestinationPath $out
$app=Join-Path $out 'HuntTracker'
foreach ($asset in @('Images/kill.png','Sounds/kill.mp3')) {
    if ((Get-FileHash (Join-Path $root $asset)).Hash -ne (Get-FileHash (Join-Path $app $asset)).Hash) { throw "Changed bundled media: $asset" }
}
# First launch must generate config.json without reading the user's live logs.
$defaults=Get-Content (Join-Path $app 'config.example.json') -Raw | ConvertFrom-Json
if ($defaults.image -ne 'Images/kill.png' -or $defaults.volume -ne 0.5 -or $defaults.durationMs -ne 1800) { throw 'Standalone defaults changed' }
$defaults.gameFolder='./game'; $defaults.nvidiaLogs='./sdk'
$defaults | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $app 'config.example.json') -Encoding utf8
$info=[Diagnostics.ProcessStartInfo]::new()
$info.FileName=Join-Path $PSHOME 'powershell.exe'
$info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle='Hidden'
$info.Arguments='-NoProfile -STA -ExecutionPolicy Bypass -File "'+(Join-Path $app 'app/Tracker.ps1')+'" -Panel -GameProcessName NoSuchPackageFixture'
$process=[Diagnostics.Process]::Start($info)
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    $file=Join-Path $app '_runtime/state.json'
    while (-not (Test-Path -LiteralPath $file) -and [DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) { [Threading.Thread]::Sleep(50) }
    if (-not (Test-Path -LiteralPath $file)) { throw 'Clean package did not start' }
    $state=Get-Content $file -Raw | ConvertFrom-Json
    if (-not $state.running -or -not $state.panel_visible -or -not $state.image_found -or $state.sounds.Count -ne 1 -or $state.sounds[0] -ne 'Sounds/kill.mp3') { throw 'Invalid clean package state' }
    $config=Get-Content (Join-Path $app 'config.json') -Raw | ConvertFrom-Json
    if ($config.gameFolder -ne './game') { throw 'Default config missing' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $app 'app/Launcher.ps1') -Action Start
    if ($LASTEXITCODE -ne 0) { throw 'Reopen failed' }
    $reopened=Get-Content $file -Raw | ConvertFrom-Json
    if ($reopened.tracker_pid -ne $state.tracker_pid -or $reopened.session -ne $state.session) { throw 'Reopen created second tracker' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $app 'app/Launcher.ps1') -Action Stop
    if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'Package did not stop cleanly' }
    'PASS: clean package, original media, Standalone defaults, first launch, single instance, graceful stop.'
} finally { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() }
