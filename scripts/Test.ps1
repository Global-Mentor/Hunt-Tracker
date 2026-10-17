param([switch]$IncludeWindowRestore,[switch]$KeepArtifacts)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot=Join-Path $temporaryRoot ('HuntTrackerTests-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$previousTestRoot=$env:HUNT_TEST_ROOT
$env:HUNT_TEST_ROOT=$testRoot
try {
    foreach ($directory in @('app','scripts','tests')) {
        foreach ($file in @(Get-ChildItem (Join-Path $root $directory) -Filter *.ps1)) {
            $tokens=$null; $errors=$null
            $null=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
            if ($errors.Count) { throw ($errors | Out-String) }
        }
    }
    foreach ($test in @('Statistics.ps1','test_panel.ps1','Package.ps1')) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root ('tests/'+$test))
        if ($LASTEXITCODE -ne 0) { throw "Failed: $test" }
    }
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
