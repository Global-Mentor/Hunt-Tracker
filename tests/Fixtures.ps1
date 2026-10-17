function New-TestDirectory([string]$Name) {
    if (-not $env:HUNT_TEST_ROOT) { throw 'Run tests through scripts/Test.ps1.' }
    $path=Join-Path $env:HUNT_TEST_ROOT ($Name+'-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($path)
    return $path
}
