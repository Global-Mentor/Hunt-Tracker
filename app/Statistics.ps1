function New-Statistics {
    return [pscustomobject]@{
        version=1; id=[guid]::NewGuid().ToString('N'); started_at=[DateTimeOffset]::Now.ToString('o')
        kills=0; deaths=0; trades=0; unknown=0; games=0; survived=0; lost=0; unresolved=0
        maps=[pscustomobject]@{cemetery=0;civilwar=0;creek=0;colorado=0}
        current=$null; recent=@()
    }
}

function Read-Statistics([string]$Root) {
    $file=Join-Path $Root 'data/statistics.json'
    if (-not [IO.File]::Exists($file)) { return New-Statistics }
    try {
        $stats=(Read-SharedText $file 4194304) | ConvertFrom-Json
        if ($stats.version -ne 1) { throw 'Unsupported version' }
        if (-not $stats.PSObject.Properties['trades']) { $stats | Add-Member trades 0 }
        foreach ($key in @('kills','deaths','trades','unknown','games','survived','lost','unresolved')) {
            if ($null -eq $stats.$key -or $stats.$key -is [string] -or $stats.$key -is [bool] -or [double]$stats.$key -lt 0 -or [double]$stats.$key -ne [Math]::Truncate([double]$stats.$key)) { throw "Invalid $key" }
        }
        foreach ($key in @('id','started_at','maps','current','recent')) {
            if (-not $stats.PSObject.Properties[$key]) { throw "Missing $key" }
        }
        $null=[guid]::ParseExact($stats.id,'N')
        $null=[DateTimeOffset]::Parse($stats.started_at)
        if ($stats.recent -isnot [Array]) { throw 'Invalid recent matches' }
        foreach ($key in @('cemetery','civilwar','creek','colorado')) {
            $count=$stats.maps.$key
            if ($null -eq $count -or $count -is [bool] -or $count -is [string] -or [double]$count -lt 0 -or [double]$count -ne [Math]::Truncate([double]$count)) { throw 'Invalid map count' }
        }
        # The interval while the application was closed was not observed.
        Complete-StatisticsMatch $stats 'tracker_restarted'
        return $stats
    } catch { throw 'Не удалось прочитать data/statistics.json. Сохраните этот файл отдельно перед восстановлением статистики.' }
}

function Save-Statistics([string]$Root,$Stats) {
    [void][IO.Directory]::CreateDirectory((Join-Path $Root 'data'))
    Write-AtomicText (Join-Path $Root 'data/statistics.json') ($Stats | ConvertTo-Json -Depth 8)
}

function Complete-StatisticsMatch($Stats,[string]$Reason) {
    $match=$Stats.current
    if (-not $match -or $match.status -eq 'complete') { return }
    $match.status='complete'; $match.ended_at=[DateTimeOffset]::Now.ToString('o'); $match.reason=$Reason
    if ($match.loaded -and $match.full -and $Reason -eq 'MissionRequested' -and -not $match.death_open) {
        $match.outcome='survived'; $Stats.survived++
    } elseif ($match.loaded -and $match.full -and $Reason -in @('GameSessionEnded','UserRequested') -and $match.death_open) {
        $match.outcome='lost'; $Stats.lost++
    } else { $match.outcome='unknown'; $Stats.unresolved++ }
    $Stats.recent=@(@($Stats.recent)+@($match) | Select-Object -Last 50)
}

function Start-StatisticsMatch($Stats,[bool]$Full) {
    Complete-StatisticsMatch $Stats 'next_match'
    $Stats.games++
    $Stats.current=[pscustomobject]@{
        id=[guid]::NewGuid().ToString('N'); status='loading'; map=''; loaded=$false; full=$Full
        death_open=$false; kills=0; deaths=0; unknown=0
        started_at=[DateTimeOffset]::Now.ToString('o'); ended_at=''; outcome=''; reason=''
    }
}

function Update-StatisticsLine($Stats,[string]$Line) {
    $changed=$false
    if ($Line.Contains("Starting UI state machine 'loading/StateMachine'") -or $Line.Contains('CGameLobby::OnGameStarting()')) {
        if (-not $Stats.current -or $Stats.current.status -ne 'loading') { Start-StatisticsMatch $Stats $true; $changed=$true }
    }
    if ($Line -match "PrepareLevel 'levels/([^']+)' \(client\)") {
        $map=$Matches[1]
        if (-not $Stats.current -or $Stats.current.status -eq 'complete') { Start-StatisticsMatch $Stats $false }
        if (-not $Stats.current.map) {
            $Stats.current.map=$map
            if ($Stats.maps.PSObject.Properties[$map]) { $Stats.maps.$map++ }
            $changed=$true
        }
    }
    if ($Line -match 'transition from state\(eLS_Loading\) to \(eLS_Game\)|entering state eLS_Game\(7\) from eLS_Loading') {
        if (-not $Stats.current -or $Stats.current.status -eq 'complete') { Start-StatisticsMatch $Stats $false }
        $changed=$changed -or -not $Stats.current.loaded
        $Stats.current.loaded=$true; $Stats.current.status='active'
    }
    if (-not $Stats.current -or $Stats.current.status -eq 'complete') { return $changed }
    if ($Line.Contains("Starting UI state machine 'death/StateMachine'")) { $changed=$changed -or -not $Stats.current.death_open; $Stats.current.death_open=$true }
    if ($Line.Contains("Stopping UI state machine 'death/StateMachine'")) { $changed=$changed -or $Stats.current.death_open; $Stats.current.death_open=$false }
    if ($Line -match "Client_OnDisconnect\(cause=(\d+),.*reasonType='([^']+)'") {
        $cause=[int]$Matches[1]; $reason=$Matches[2]
        if ($Stats.current.status -eq 'loading' -and $cause -eq 11 -and $reason -eq 'Unknown' -and $Line.Contains("desc='Nub destroyed'")) { return $changed }
        Complete-StatisticsMatch $Stats $reason; $changed=$true
    } elseif ($Line.Contains("PrepareLevel 'menu' (server)")) { Complete-StatisticsMatch $Stats 'menu_without_result'; $changed=$true }
    return $changed
}

function Start-NewStatisticsSession([string]$Root,$Stats) {
    $archived=$Stats | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    Complete-StatisticsMatch $archived 'new_session'
    [void][IO.Directory]::CreateDirectory((Join-Path $Root 'data/sessions'))
    Write-AtomicText (Join-Path $Root ('data/sessions/'+$archived.id+'.json')) ($archived | ConvertTo-Json -Depth 8)
    $next=New-Statistics
    Save-Statistics $Root $next
    return $next
}

function Add-StatisticsCombat($Stats,[string]$Kind,[string]$MatchId) {
    $field=@{kill='kills';death='deaths';unknown='unknown'}[$Kind]
    $Stats.$field++
    if (-not $MatchId) { return }
    $match=if ($Stats.current -and $Stats.current.id -eq $MatchId) { $Stats.current } else { $Stats.recent | Where-Object id -eq $MatchId | Select-Object -Last 1 }
    if ($match) { $match.$field++ }
}
