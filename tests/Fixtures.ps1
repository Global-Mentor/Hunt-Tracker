function New-TestDirectory([string]$Name) {
    if (-not $env:HUNT_TEST_ROOT) { throw 'Run tests through scripts/Test.ps1.' }
    $path=Join-Path $env:HUNT_TEST_ROOT ($Name+'-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($path)
    return $path
}

function Read-TestJson([string]$Path) {
    for ($attempt=0; ; $attempt++) {
        $stream=$null; $reader=$null
        try {
            $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader=[IO.StreamReader]::new($stream)
            return ($reader.ReadToEnd() | ConvertFrom-Json)
        } catch [IO.IOException] {
            if ($attempt -ge 19) { throw }
            [Threading.Thread]::Sleep(25)
        } finally {
            if ($reader) { $reader.Dispose() } elseif ($stream) { $stream.Dispose() }
        }
    }
}
