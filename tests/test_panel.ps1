$ErrorActionPreference='Stop'
$source=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Fixtures.ps1')
$fixture=New-TestDirectory 'panel test Кириллица & пробел'
foreach ($name in @('app','Images','Sounds','config.example.json')) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $fixture -Recurse }
$config=@{gameFolder='./game';nvidiaLogs='./sdk';volume=0.5;durationMs=1800}
$config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'config.json') -Encoding UTF8
$harness=@'
$originalUpdate=(Get-Item Function:Update-ControlPanel).ScriptBlock
$panelTestPhase=0
function Update-ControlPanel($Panel,$State) {
    & $originalUpdate $Panel $State
    if ($script:panelTestPhase -eq 0) {
        $Panel.Tabs.SelectedIndex=1
        if (-not $State.panel_visible) { throw 'Native window is hidden after startup' }
        if ($State.game_log -ne (Get-PortablePath (Join-Path $root 'game/USER/Game.log'))) { throw 'Relative log path used wrong working directory' }
        if (-not $Panel.Form.MaximizeBox -or -not $Panel.Form.MinimizeBox) { throw 'Standard window controls disabled' }
        if ($Panel.Statuses['gameLog'].ForeColor.R -ne 245) { throw 'Missing game log must be red' }
        if ($Panel.Statuses['image'].ForeColor.G -ne 210) { throw 'Available image must be green' }
        if ($Panel.Statuses['nvidia'].ForeColor.R -ne 235) { throw 'Waiting for game must be amber' }
        $script:panelTestPhase=1
        $Panel.Buttons['kill'].PerformClick()
        if ($Panel.Actions.Count -ne 1) { throw 'Kill button did not enqueue its action' }
    } elseif ($script:panelTestPhase -eq 1 -and $State.events.Count -eq 1) {
        if (-not $State.events[0].test -or $State.kills -ne 0) { throw 'Native test changed real counters' }
        $script:panelTestPhase=2
        $Panel.Buttons['death'].PerformClick()
        $Panel.Form.WindowState='Minimized'
        if ($Panel.Form.IsDisposed -or -not $script:running) { throw 'Minimizing stopped the tracker' }
        Write-AtomicText (Join-Path $runtime 'show.json') (@{session=$State.session} | ConvertTo-Json -Compress)
    } elseif ($script:panelTestPhase -eq 2) {
        if ($Panel.Form.WindowState -ne 'Normal' -or -not $State.panel_visible) { throw 'Show command did not restore visible panel' }
        if ($State.events.Count -ne 1 -or $State.kills -ne 0 -or $State.deaths -ne 0) { throw 'Death test emitted alert or changed counters' }
        if ($Panel.Feedback.Text -notlike '*алерт подавлен*') { throw 'Death feedback missing' }
        $Panel.Form.WindowState='Maximized'
        Show-ControlPanel $Panel
        if ($Panel.Form.WindowState -ne 'Maximized') { throw 'Reopen lost maximized state' }
        $script:panelTestPhase=3
        $Panel.Options.alerts.Checked=$false
        $Panel.Options.stats.Checked=$false
        $Panel.Fields.kd.Checked=$false
        $Panel.Numbers.volume.Value=25
        $Panel.Buttons.save.PerformClick()
    } elseif ($script:panelTestPhase -eq 3) {
        if ($State.display.alerts -or $State.display.stats -or 'kd' -in $State.display.fields -or $State.volume -ne 0.25) { throw 'Saved settings not applied to publication' }
        $saved=Read-AppConfig $root
        if ($saved.display.alerts -or $saved.display.stats -or 'kd' -in $saved.display.fields) { throw 'Saved settings did not reach disk' }
        $script:oldStatsId=$statistics.id
        Add-StatisticsCombat $statistics 'kill' ''
        $script:kills=$statistics.kills
        $script:statisticsDirty=$true
        $script:panelTestPhase=4
        $Panel.Buttons['new-session'].PerformClick()
    } elseif ($script:panelTestPhase -eq 4) {
        if ($State.statistics.id -eq $script:oldStatsId -or $State.kills -ne 0) { throw 'New session did not reset counters' }
        $archived=Get-Content (Join-Path $root ('data/sessions/'+$script:oldStatsId+'.json')) -Raw | ConvertFrom-Json
        if ($archived.kills -ne 1) { throw 'New session did not preserve old statistics' }
        $script:panelTestPhase=5
        $Panel.Buttons['kill'].PerformClick()
        $Panel.Form.Close()
        if (-not $Panel.Form.Tag.Closing -or $Panel.Actions.Count -ne 1 -or $Panel.Actions.Peek() -ne 'stop') { throw 'Close must replace pending actions with stop' }
    } elseif ($script:panelTestPhase -eq 5 -and -not $State.running) {
        if ($State.events.Count -or $State.panel_visible) { throw 'Stopped tracker must clear alerts and hide window' }
        [IO.File]::WriteAllText((Join-Path $root 'panel-pass.txt'),'PASS: portable paths, controls, saved settings, independent displays, session archive, minimize/restore/maximize, close stops process.')
    }
}
'@
Add-Content -LiteralPath (Join-Path $fixture 'app/Panel.ps1') -Value $harness -Encoding UTF8
$startInfo=[Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName=Join-Path $PSHOME 'powershell.exe'
$startInfo.UseShellExecute=$false
$startInfo.CreateNoWindow=$true
$startInfo.WindowStyle='Hidden'
$startInfo.WorkingDirectory=$env:TEMP
$startInfo.Arguments='-NoProfile -STA -ExecutionPolicy Bypass -File "'+(Join-Path $fixture 'app/Tracker.ps1')+'" -GameProcessName NoSuchHuntFixture -Panel'
$process=[Diagnostics.Process]::Start($startInfo)
try {
    if (-not $process.WaitForExit(15000)) { throw 'Panel test timed out' }
    $result=Join-Path $fixture 'panel-pass.txt'
    if (-not (Test-Path -LiteralPath $result)) {
        $errorFile=Join-Path $fixture '_runtime/startup-error.txt'
        if (Test-Path -LiteralPath $errorFile) { throw ([IO.File]::ReadAllText($errorFile)) }
        throw 'Panel test failed'
    }
    Get-Content -LiteralPath $result
    if ($process.ExitCode -ne 0) { throw 'Panel process did not exit cleanly' }
    $state=Get-Content -LiteralPath (Join-Path $fixture '_runtime/state.json') -Raw | ConvertFrom-Json
    if ($state.running -or $state.events.Count) { throw 'Final state must be stopped and silent' }
    $last=Get-ChildItem -LiteralPath (Join-Path $fixture 'Logs') -Filter '*.jsonl' | Select-Object -First 1 | Get-Content -Tail 1 | ConvertFrom-Json
    if ($last.type -ne 'stopped') { throw 'Close did not finish the journal' }
} finally { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() }
