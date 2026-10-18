param([switch]$IncludeWindowRestore,[switch]$IncludePackage,[switch]$KeepArtifacts)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot=Join-Path $temporaryRoot ('HuntTrackerTests-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$previousTestRoot=$env:HUNT_TEST_ROOT
$env:HUNT_TEST_ROOT=$testRoot
try {
    & python (Join-Path $root 'tests/test_repository.py')
    if ($LASTEXITCODE -ne 0) { throw 'Repository privacy checks failed' }
    & dotnet build (Join-Path $root 'HuntTracker.slnx') -c Release -p:RestoreLockedMode=true --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    & dotnet run --project (Join-Path $root 'tests/hunt-tracker-tests/HuntTracker.Tests.csproj') -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    & dotnet run --project (Join-Path $root 'tests/hunt-tracker-desktop-tests/HuntTracker.DesktopTests.csproj') -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Desktop tests failed' }
    foreach ($test in @('test_client.cjs','test_statistics.cjs')) {
        & node (Join-Path $root ('tests/'+$test))
        if ($LASTEXITCODE -ne 0) { throw "Failed: $test" }
    }
    & python (Join-Path $root 'tests/test_app.py')
    if ($LASTEXITCODE -ne 0) { throw 'Failed: test_app.py' }
    if ($IncludeWindowRestore) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/test_window_restore.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Failed: test_window_restore.ps1' }
    }
    if ($IncludePackage) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests/Package.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Failed: Package.ps1' }
    }
    'All checks passed.'
} finally {
    $env:HUNT_TEST_ROOT=$previousTestRoot
    if ($KeepArtifacts) { Write-Output ('Test artifacts: '+$testRoot) }
    else {
        $resolved=[IO.Path]::GetFullPath($testRoot)
        if (-not $resolved.StartsWith($temporaryRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test path' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
