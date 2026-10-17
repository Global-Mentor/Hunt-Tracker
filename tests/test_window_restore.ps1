$ErrorActionPreference='Stop'
$source=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Fixtures.ps1')
$fixture=New-TestDirectory 'restore'
foreach ($name in @('app','Images','Sounds','config.example.json')) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $fixture -Recurse }
@{gameFolder='./game';nvidiaLogs='./sdk';volume=0.5;durationMs=1800} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'config.json') -Encoding UTF8
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RestoreTest {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
    public static bool Restore(IntPtr window) {
        return PostMessage(window,0x0112,new IntPtr(0xF120),IntPtr.Zero);
    }
}
'@
$info=[Diagnostics.ProcessStartInfo]::new()
$info.FileName=Join-Path $PSHOME 'powershell.exe'
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.WindowStyle='Hidden'
$info.Arguments='-NoProfile -STA -ExecutionPolicy Bypass -File "'+(Join-Path $fixture 'app/Tracker.ps1')+'" -GameProcessName NoSuchHuntFixture -Panel'
$process=[Diagnostics.Process]::Start($info)
$stateFile=Join-Path $fixture '_runtime/state.json'
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        [Threading.Thread]::Sleep(50)
        if ($process.HasExited) { throw 'Tracker exited during test startup' }
        $process.Refresh()
        $window=$process.MainWindowHandle
    } while ((-not $window -or -not [IO.File]::Exists($stateFile)) -and [DateTime]::UtcNow -lt $deadline)
    if (-not $window) { throw 'Test window not found' }
    [Threading.Thread]::Sleep(500)
    $failures=0
    for ($attempt=0; $attempt -lt 12; $attempt++) {
        [void][RestoreTest]::ShowWindow($window,6)
        [Threading.Thread]::Sleep(250)
        if (-not [RestoreTest]::IsIconic($window)) { throw "Test did not minimize window on attempt $attempt" }
        $responsive=[RestoreTest]::Restore($window)
        [Threading.Thread]::Sleep(250)
        if (-not $responsive -or [RestoreTest]::IsIconic($window)) { $failures++ }
    }
    if ($failures) { throw "Windows SC_RESTORE was ignored in $failures of 12 attempts" }
    'PASS: 12 native Windows restore requests without Start.bat or direct Form.WindowState restoration.'
} finally {
    if (-not $process.HasExited) {
        if ([IO.File]::Exists($stateFile)) {
            $state=Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
            @{session=$state.session} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture '_runtime/stop.json') -Encoding UTF8
        }
        if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    $process.Dispose()
}
