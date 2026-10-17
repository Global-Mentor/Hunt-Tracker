$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'app/Files.ps1')
. (Join-Path $root 'app/Statistics.ps1')
. (Join-Path $PSScriptRoot 'Fixtures.ps1')
function Assert($Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
$stats=New-Statistics
function Feed([string]$Line) { $null=Update-StatisticsLine $stats $Line }
Assert (-not (Update-StatisticsLine $stats 'Unrelated renderer output')) 'Unrelated lines dirty statistics'
Feed "Starting UI state machine 'loading/StateMachine'"
Feed 'CGameLobby::OnGameStarting()'
Feed "PrepareLevel 'levels/cemetery' (client)"
Feed "PrepareLevel 'levels/cemetery' (client)"
Feed "Client_OnDisconnect(cause=11, desc='Nub destroyed', reasonType='Unknown')"
Assert ($stats.games -eq 1 -and $stats.maps.cemetery -eq 1 -and $stats.current.status -eq 'loading') 'Loading transition counted as an exit'
Feed 'transition from state(eLS_Loading) to (eLS_Game)'
Add-StatisticsCombat $stats 'kill' $stats.current.id
Feed "Starting UI state machine 'death/StateMachine'"
Add-StatisticsCombat $stats 'death' $stats.current.id
Feed "Stopping UI state machine 'death/StateMachine'"
Feed "Client_OnDisconnect(cause=0, reasonType='MissionRequested')"
Feed "Client_OnDisconnect(cause=0, reasonType='MissionRequested')"
Assert ($stats.survived -eq 1 -and $stats.recent.Count -eq 1) 'Revive then inferred survival or disconnect dedup failed'
$first=$stats.current.id
Feed "Starting UI state machine 'loading/StateMachine'"
Feed "PrepareLevel 'levels/colorado' (client)"
Feed 'entering state eLS_Game(7) from eLS_Loading'
Add-StatisticsCombat $stats 'kill' $first
Assert ($stats.recent[0].kills -eq 2 -and $stats.current.kills -eq 0) 'Late classification assigned to next match'
Feed "Starting UI state machine 'death/StateMachine'"
Feed "Client_OnDisconnect(cause=0, reasonType='GameSessionEnded')"
Assert ($stats.lost -eq 1) 'Correlated loss missing'
Feed "PrepareLevel 'levels/creek' (client)"
Feed 'transition from state(eLS_Loading) to (eLS_Game)'
Feed "Client_OnDisconnect(cause=0, reasonType='MissionRequested')"
Assert ($stats.unresolved -eq 1 -and $stats.survived -eq 1) 'Partial match inferred as survival'
Feed "Starting UI state machine 'loading/StateMachine'"
Feed "Client_OnDisconnect(cause=0, reasonType='MissionRequested')"
Assert ($stats.unresolved -eq 2) 'Failed loading inferred as survival'
Feed "Starting UI state machine 'loading/StateMachine'"
Feed 'transition from state(eLS_Loading) to (eLS_Game)'
$stats.current.full=$false
Feed "Client_OnDisconnect(cause=0, reasonType='MissionRequested')"
Assert ($stats.unresolved -eq 3) 'Read gap inferred as survival'
$fixture=New-TestDirectory 'statistics'
Save-Statistics $fixture $stats
$restored=Read-Statistics $fixture
Assert ($restored.id -eq $stats.id -and $restored.kills -eq 2 -and $restored.games -eq 5) 'Persistence failed'
Feed "Starting UI state machine 'loading/StateMachine'"
Feed 'transition from state(eLS_Loading) to (eLS_Game)'
Feed "Starting UI state machine 'death/StateMachine'"
$stats.current.full=$false
Feed "Client_OnDisconnect(cause=0, reasonType='GameSessionEnded')"
Assert ($stats.unresolved -eq 4 -and $stats.lost -eq 1) 'Read gap inferred as a loss'
Feed "Starting UI state machine 'loading/StateMachine'"
Save-Statistics $fixture $stats
[IO.File]::WriteAllText((Join-Path $fixture 'data/sessions'),'blocks archive directory')
$rejected=$false
try { $null=Start-NewStatisticsSession $fixture $stats } catch { $rejected=$true }
Assert ($rejected -and $stats.current.status -eq 'loading' -and $stats.kills -eq 2) 'Failed archive changed live statistics'
$config=(Get-Content (Join-Path $root 'config.example.json') -Raw) | ConvertFrom-Json
$config.display.fields=@(); $config.display.stats=$false; $config.display.alerts=$false
$null=Read-AppConfig $root $config
$config.display.scale=0
$rejected=$false
try { $null=Read-AppConfig $root $config } catch { $rejected=$true }
Assert $rejected 'Invalid display configuration accepted'
Write-AtomicText (Join-Path $fixture 'data/statistics.json') '{broken'
$rejected=$false
try { $null=Read-Statistics $fixture } catch { $rejected=$true }
Assert $rejected 'Corrupted statistics silently reset'
'PASS: match lifecycle, loading transitions, dedup, revive, partial matches, gaps, late classification, persistence, config validation.'
