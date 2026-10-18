param([ValidateRange(10,600)][int]$Seconds=30,[string]$BuildDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture=Join-Path $temporaryRoot ('HuntTrackerMeasure-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
if (-not $BuildDirectory) { $BuildDirectory=Join-Path $root 'src/hunt-tracker-desktop/bin/Release/net10.0-windows' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BackgroundMeasurement {
    [StructLayout(LayoutKind.Sequential)]
    public struct Counters {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool GetProcessIoCounters(IntPtr process, out Counters counters);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr window);
}
'@
function Get-Sample($Process) {
    $Process.Refresh()
    $io=New-Object BackgroundMeasurement+Counters
    if (-not [BackgroundMeasurement]::GetProcessIoCounters($Process.Handle,[ref]$io)) { throw 'Cannot measure process I/O' }
    [pscustomobject]@{Cpu=$Process.TotalProcessorTime.TotalSeconds;Private=$Process.PrivateMemorySize64;WorkingSet=$Process.WorkingSet64;Handles=$Process.HandleCount;Io=$io}
}
$game=$null
$process=$null
try {
    $gameName='HuntMeasure'+[guid]::NewGuid().ToString('N').Substring(0,8)
    $gameExe=Join-Path $fixture ($gameName+'.exe')
    Add-Type -TypeDefinition 'public static class Fixture { public static void Main() { System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite); } }' -OutputAssembly $gameExe -OutputType WindowsApplication
    $game=Start-Process -FilePath $gameExe -WindowStyle Hidden -PassThru
    foreach ($scenario in @('waiting','active','growing')) {
        $app=Join-Path $fixture $scenario
        [void][IO.Directory]::CreateDirectory((Join-Path $app 'game/USER'))
        [void][IO.Directory]::CreateDirectory((Join-Path $app 'sdk'))
        $config=Get-Content -LiteralPath (Join-Path $root 'config.example.json') -Raw | ConvertFrom-Json
        $config.gameFolder='./game'; $config.nvidiaLogs='./sdk'
        $config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $app 'config.json') -Encoding UTF8
        $gameLog=Join-Path $app 'game/USER/Game.log'
        if ($scenario -ne 'waiting') {
            [IO.File]::WriteAllText($gameLog,'')
            [IO.File]::WriteAllText((Join-Path $app ('sdk/HuntGame_'+$game.Id+'.log')),'')
        }
        $processName=if ($scenario -eq 'waiting') { 'NoSuchHuntMeasure' } else { $gameName }
        $info=[Diagnostics.ProcessStartInfo]::new()
        $info.FileName=Join-Path $BuildDirectory 'HuntTracker.exe'
        $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle='Hidden'
        $info.Arguments='--root "'+$app+'" --game-process '+$processName
        $process=[Diagnostics.Process]::Start($info)
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        do {
            [Threading.Thread]::Sleep(50)
            $process.Refresh()
            if ($process.HasExited) { throw 'Tracker exited before measurement' }
        } while (($process.MainWindowHandle -eq [IntPtr]::Zero -or -not [IO.File]::Exists((Join-Path $app '_runtime/state.json'))) -and [DateTime]::UtcNow -lt $deadline)
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'No tracker window' }
        [void][BackgroundMeasurement]::ShowWindow($process.MainWindowHandle,6)
        [Threading.Thread]::Sleep(5000)
        if (-not [BackgroundMeasurement]::IsIconic($process.MainWindowHandle)) { throw 'Tracker is not minimized' }
        $before=Get-Sample $process
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
            if ($scenario -eq 'growing') { [IO.File]::AppendAllText($gameLog,(("Unrelated synthetic game log line`n"*10))) }
            [Threading.Thread]::Sleep(250)
        }
        $after=Get-Sample $process
        $elapsed=$clock.Elapsed.TotalSeconds
        [pscustomobject]@{
            Scenario=$scenario;Seconds=[Math]::Round($elapsed,2)
            CpuOneCorePercent=[Math]::Round(100*($after.Cpu-$before.Cpu)/$elapsed,3)
            CpuMachinePercent=[Math]::Round(100*($after.Cpu-$before.Cpu)/$elapsed/[Environment]::ProcessorCount,3)
            PrivateMiB=[Math]::Round($after.Private/1MB,2)
            PrivateDeltaMiB=[Math]::Round(($after.Private-$before.Private)/1MB,2)
            WorkingSetMiB=[Math]::Round($after.WorkingSet/1MB,2)
            HandleDelta=$after.Handles-$before.Handles
            ReadOperationsPerSecond=[Math]::Round(($after.Io.ReadOperations-$before.Io.ReadOperations)/$elapsed,2)
            WriteOperationsPerSecond=[Math]::Round(($after.Io.WriteOperations-$before.Io.WriteOperations)/$elapsed,2)
            ReadKiBPerSecond=[Math]::Round(($after.Io.ReadBytes-$before.Io.ReadBytes)/$elapsed/1KB,2)
            WriteKiBPerSecond=[Math]::Round(($after.Io.WriteBytes-$before.Io.WriteBytes)/$elapsed/1KB,2)
        } | ConvertTo-Json -Compress
        $state=Get-Content -LiteralPath (Join-Path $app '_runtime/state.json') -Raw | ConvertFrom-Json
        @{session=$state.session} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $app '_runtime/stop.json') -Encoding UTF8
        if (-not $process.WaitForExit(8000) -or $process.ExitCode -ne 0) { throw 'Tracker did not stop cleanly' }
        $process.Dispose(); $process=$null
    }
} finally {
    foreach ($owned in @($process,$game)) {
        if ($owned) { if (-not $owned.HasExited) { $owned.Kill(); $owned.WaitForExit() }; $owned.Dispose() }
    }
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($temporaryRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid measurement path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
