$ErrorActionPreference='Stop'
$source=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Fixtures.ps1')
$fixture=New-TestDirectory 'restore'
foreach ($name in @('source','config.example.json')) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $fixture -Recurse }
$binaries=if ($env:HUNT_BUILD_DIR) { $env:HUNT_BUILD_DIR } else { Join-Path $source 'src/hunt-tracker-desktop/bin/Release/net10.0-windows' }
Copy-Item -LiteralPath $binaries -Destination (Join-Path $fixture 'app') -Recurse
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
$info.FileName=Join-Path $fixture 'app/HuntTracker.exe'
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.WindowStyle='Hidden'
$info.Arguments='--root "'+$fixture+'" --game-process NoSuchHuntFixture'
$process=[Diagnostics.Process]::Start($info)
$stateFile=Join-Path $fixture '_runtime/state.json'
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        [Threading.Thread]::Sleep(50)
        if ($process.HasExited) { throw 'Tracker exited during test startup' }
        $process.Refresh()
        $window=if ($process.MainWindowTitle -eq 'Hunt Tracker (by Global Mentor)') { $process.MainWindowHandle } else { [IntPtr]::Zero }
    } while (($window -eq [IntPtr]::Zero -or -not [IO.File]::Exists($stateFile)) -and [DateTime]::UtcNow -lt $deadline)
    if ($window -eq [IntPtr]::Zero) { throw 'Test window not found' }
    [Threading.Thread]::Sleep(500)
    $failures=0
    $longestRestore=0
    for ($attempt=0; $attempt -lt 12; $attempt++) {
        [void][RestoreTest]::ShowWindow($window,6)
        [Threading.Thread]::Sleep(250)
        if (-not [RestoreTest]::IsIconic($window)) { throw "Test did not minimize window on attempt $attempt (handle $window, title $($process.MainWindowTitle))" }
        $responsive=[RestoreTest]::Restore($window)
        $restoreClock=[Diagnostics.Stopwatch]::StartNew()
        do { [Threading.Thread]::Sleep(25) } while ([RestoreTest]::IsIconic($window) -and $restoreClock.ElapsedMilliseconds -lt 2000)
        $longestRestore=[Math]::Max($longestRestore,$restoreClock.ElapsedMilliseconds)
        if (-not $responsive -or [RestoreTest]::IsIconic($window)) { $failures++ }
    }
    if ($failures) { throw "Windows SC_RESTORE was ignored in $failures of 12 attempts" }
    [void][RestoreTest]::ShowWindow($window,6)
    [Threading.Thread]::Sleep(100)
    $second=[Diagnostics.Process]::Start($info)
    if (-not $second.WaitForExit(5000)) { throw 'Second launch did not exit' }
    $second.Dispose()
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    while ([RestoreTest]::IsIconic($window) -and [DateTime]::UtcNow -lt $deadline) { [Threading.Thread]::Sleep(25) }
    if ([RestoreTest]::IsIconic($window)) { throw 'Second launch did not restore original window' }
    [void][RestoreTest]::PostMessage($window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    if (-not $process.WaitForExit(8000) -or $process.ExitCode -ne 0) { throw 'Closing WPF window did not stop the app' }
    $state=Read-TestJson $stateFile
    if ($state.running) { throw 'Closing left tracking alive' }
    "PASS: WPF close, single instance, 12 native Windows restore requests (slowest: $longestRestore ms)."
} finally {
    if (-not $process.HasExited) {
        if ([IO.File]::Exists($stateFile)) {
            $state=Read-TestJson $stateFile
            @{session=$state.session} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture '_runtime/stop.json') -Encoding UTF8
        }
        if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    $process.Dispose()
}
