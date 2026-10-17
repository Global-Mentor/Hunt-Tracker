param([string]$GameProcessName = 'HuntGame', [switch]$Panel)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
. (Join-Path $PSScriptRoot 'Files.ps1')
. (Join-Path $PSScriptRoot 'Statistics.ps1')
$mutex = [Threading.Mutex]::new($false,(Get-AppMutexName $root))
$owns = $false
$journal = $null
$readableJournal = $null
$controlPanel = $null
$runtime = Join-Path $root '_runtime'
[void][IO.Directory]::CreateDirectory($runtime)
try {
    try { $owns = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owns=$true }
    if (-not $owns) { exit }
    $config = Read-AppConfig $root
    $statistics = Read-Statistics $root
    $statisticsDirty=$true
    $gameFolder = if ($config.gameFolder -eq 'AUTO') { Find-HuntFolder } else { Resolve-SettingPath ([string]$config.gameFolder) $root }
    $sdkFolder = Resolve-SettingPath ([string]$config.nvidiaLogs) $root
    $commandsFolder = Join-Path $runtime 'commands'
    [void][IO.Directory]::CreateDirectory($commandsFolder)
    $session = [guid]::NewGuid().ToString('N')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $game = New-Tail (Join-Path $gameFolder 'USER/Game.log') $true
    $gameGeneration=$game.Generation
    $statisticsRebaseInProgress=$false
    $sdk=$null; $gamePid=0; $firstDiscovery=$true; $sdkGeneration=0; $epoch=0; $deathOpen=$false
    $pending=[Collections.Generic.List[object]]::new()
    $signals=[Collections.Generic.List[object]]::new()
    $seen=[Collections.Generic.HashSet[string]]::new()
    $seenOrder=[Collections.Generic.Queue[string]]::new()
    $commandIds=[Collections.Generic.HashSet[string]]::new()
    $random=[Random]::new()
    $lastAlertSound=$null
    $testKills=[Collections.Generic.List[double]]::new()
    $events=[Collections.Generic.List[object]]::new()
    $sounds=@(); $sequence=0L; $kills=$statistics.kills; $deaths=$statistics.deaths; $unknown=$statistics.unknown
    $nextDiscovery=0; $nextSounds=0; $nextCommands=0; $nextPublish=0; $lastHealth=''
    $running=$true; $dirty=$true
    $logsFolder = Join-Path $root 'Logs'
    [void][IO.Directory]::CreateDirectory($logsFolder)
    $journalBase = Join-Path $logsFolder ('events-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + $session.Substring(0,8))
    $journal = [IO.StreamWriter]::new(($journalBase+'.jsonl'),$false,[Text.UTF8Encoding]::new($false))
    $readableJournal = [IO.StreamWriter]::new(($journalBase+'.log'),$false,[Text.UTF8Encoding]::new($true))
    $journal.AutoFlush=$true
    $readableJournal.AutoFlush=$true
    $logError=''
    if ($Panel) {
        . (Join-Path $PSScriptRoot 'Panel.ps1')
        $controlPanel = New-ControlPanel $config
    }
    function Record([string]$Type,$Data) {
        $text = switch ($Type) {
            'started' { 'Трекер запущен. Ожидание: 300 мс. Game.log: '+$Data.game_log+'; NVIDIA: '+$Data.nvidia_logs }
            'stopped' { 'Трекер остановлен. Убийств: '+$Data.kills+'; смертей: '+$Data.deaths+'; неопределённых событий: '+$Data.unknown }
            'game_connection' { if ($Data.pid) { 'Игра запущена. PID: '+$Data.pid } else { 'Ожидание запуска игры.' } }
            'log_status' { 'Чтение логов: Game.log - '+$(if ($Data.game) { 'доступен' } else { 'не готов' })+'; NVIDIA - '+$(if ($Data.nvidia) { 'доступен' } else { 'не готов' }) }
            'highlight' { 'Событие NVIDIA. Запрос: '+$Data.request }
            'death_screen_opened' { 'Открылся экран смерти. Время чтения от запуска: '+$Data.read_ms+' мс' }
            'kill' { 'Предполагаемое убийство. Событие передано оверлею.' }
            'death' { 'Смерть по экрану смерти. Алерт подавлен.' }
            'unknown' { 'Событие не определено. Алерт подавлен. Причина: '+$Data.reason }
            'test' { if ($Data.event_type -eq 'kill') { 'Тест убийства принят.' } else { 'Тест смерти принят. Алерт подавлен.' } }
            'test_alert' { 'Тестовый алерт отправлен.' }
            'error' { 'Ошибка: '+$Data.message }
            default { $Type }
        }
        if ($Type -in @('kill','death','unknown')) {
            $text += ' Запрос: '+$Data.request+'; ожидание: '+$Data.wait_ms+' мс'
            if ($null -ne $Data.screen_gap_ms) { $text += '; экран смерти прочитан на '+[Math]::Abs($Data.screen_gap_ms)+' мс '+$(if ($Data.screen_gap_ms -lt 0) { 'раньше' } else { 'позже' })+' события NVIDIA' }
        }
        if ($Type -in @('kill','test_alert')) { $text += ' Звук: '+$(if ($script:lastAlertSound) { [Uri]::UnescapeDataString($script:lastAlertSound) } else { 'нет файлов' }) }
        $text=Protect-LogText $text
        try {
            $journal.WriteLine(([ordered]@{at=[DateTimeOffset]::Now.ToString('o');type=$Type;message=$text;data=$Data} | ConvertTo-Json -Depth 5 -Compress))
            $readableJournal.WriteLine(('['+[DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff zzz')+'] '+$text))
        } catch { $script:logError='Не удалось записать журнал. Проверьте свободное место и доступ к папке Logs.'; $script:dirty=$true }
    }
    function Emit-Kill([bool]$Test) {
        $script:sequence++
        $script:lastAlertSound=if ($sounds.Count) { $sounds[$random.Next($sounds.Count)] } else { $null }
        $events.Add(@{id=$script:sequence;type='kill';at=[DateTimeOffset]::UtcNow.ToString('o');test=$Test;sound=$script:lastAlertSound})
        if ($events.Count -gt 64) { $events.RemoveAt(0) }
        $script:dirty=$true
    }
    function Publish {
        if ($script:statisticsDirty) {
            Save-Statistics $root $statistics
            $script:statisticsDirty=$false
        }
        $state = [ordered]@{
            session=$session; running=$script:running; updated_at=[DateTimeOffset]::UtcNow.ToString('o'); delay_ms=300
            game_log=(Get-PortablePath $game.Path); game_readable=($game.Healthy -and $game.CaughtUp)
            nvidia_folder=(Get-PortablePath $sdkFolder); sdk_log=$(if ($sdk) { Get-PortablePath $sdk.Path } else { '' })
            sdk_readable=($null -ne $sdk -and $sdk.Healthy); game_pid=$gamePid
            kills=$kills; deaths=$deaths; unknown=$unknown; image_found=[IO.File]::Exists((Join-Path $root $config.image))
            image=$config.image; display=$config.display; statistics=$statistics
            sounds=@($sounds); volume=[double]$config.volume; duration_ms=[int]$config.durationMs; events=@($events.ToArray())
            warning=$logError; tracker_pid=$PID
            panel_visible=($null -ne $controlPanel -and [HuntKillWindow]::IsWindowVisible($controlPanel.Form.Handle))
        }
        $json = $state | ConvertTo-Json -Depth 6 -Compress
        Write-AtomicText (Join-Path $runtime 'state.json') $json
        Write-AtomicText (Join-Path $runtime 'state.js') ('window.HuntKillState=' + $json + ';')
        if ($controlPanel) { Update-ControlPanel $controlPanel $state }
        $script:dirty=$false
    }
    $stopFile = Join-Path $runtime 'stop.json'
    if ([IO.File]::Exists($stopFile)) { [IO.File]::Delete($stopFile) }
    Record 'started' @{delay_ms=300;game_log=(Get-PortablePath $game.Path);nvidia_logs=(Get-PortablePath $sdkFolder)}
    if ($controlPanel) { Show-ControlPanel $controlPanel }
    while ($running) {
        if ($controlPanel) {
            [Windows.Forms.Application]::DoEvents()
            while ($controlPanel.Actions.Count) {
                $action=$controlPanel.Actions.Dequeue()
                if ($action -eq 'stop') {
                    $running=$false
                    $pending.Clear(); $testKills.Clear()
                    break
                } elseif ($action -eq 'overlay') {
                    try { Start-Process -FilePath (Join-Path $root 'Overlay.html') -ErrorAction Stop }
                    catch { $controlPanel.Feedback.Text='Откройте Overlay.html вручную в браузере или OBS.' }
                } elseif ($action -eq 'stats-overlay') {
                    try { Start-Process -FilePath (Join-Path $root 'Statistics.html') -ErrorAction Stop }
                    catch { $controlPanel.Feedback.Text='Откройте Statistics.html вручную в браузере или OBS.' }
                } elseif ($action -eq 'save') {
                    try {
                        $candidate=Get-ControlPanelConfig $controlPanel $config
                        $candidate=Read-AppConfig $root $candidate
                        $pathsChanged=$candidate.gameFolder -ne $config.gameFolder -or $candidate.nvidiaLogs -ne $config.nvidiaLogs
                        Write-AtomicText (Join-Path $root 'config.json') ($candidate | ConvertTo-Json -Depth 6)
                        $config=$candidate; $dirty=$true
                        $controlPanel.Feedback.Text=if ($pathsChanged) { 'Сохранено. Новые пути заработают после закрытия и запуска приложения.' } else { 'Настройки сохранены и применены.' }
                    } catch { $controlPanel.Feedback.Text=$_.Exception.Message }
                } elseif ($action -eq 'new-session') {
                    try {
                        $statistics=Start-NewStatisticsSession $root $statistics
                        $kills=0; $deaths=0; $unknown=0
                        $pending.Clear(); $signals.Clear(); $events.Clear(); $testKills.Clear()
                        $statisticsDirty=$false; $dirty=$true
                        $controlPanel.Feedback.Text='Начата новая сессия. Предыдущая сохранена в data/sessions.'
                    } catch { $controlPanel.Feedback.Text='Не удалось сохранить сессию. Сбор продолжается с прежними счетчиками. Проверьте доступ к папке data.' }
                } elseif ($action -in @('kill','death')) {
                    if ($testKills.Count -ge 16) { $controlPanel.Feedback.Text='Очередь тестов заполнена. Подождите.'; continue }
                    if ($action -eq 'kill') { $testKills.Add($clock.Elapsed.TotalMilliseconds+300) }
                    Record 'test' @{event_type=$action;suppressed=($action -eq 'death')}
                    $controlPanel.Feedback.Text=if ($action -eq 'kill') { 'Тест убийства принят. Алерт через 300 мс.' } else { 'Тест смерти: алерт подавлен, картинки и звука нет.' }
                }
            }
        }
        if (-not $running) { break }
        $now = $clock.Elapsed.TotalMilliseconds
        if ($now -ge $nextDiscovery) {
            $nextDiscovery=$now+1000
            $process = Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue | Sort-Object Id -Descending | Select-Object -First 1
            $activePid = if ($process) { $process.Id } else { 0 }
            if ($activePid -ne $gamePid -or $firstDiscovery) {
                $skipExisting=$firstDiscovery; $firstDiscovery=$false; $gamePid=$activePid
                $sdk = if ($gamePid) { New-Tail (Join-Path $sdkFolder ('HuntGame_'+$gamePid+'.log')) $skipExisting } else { $null }
                $sdkGeneration=if ($sdk) { $sdk.Generation } else { 0 }
                foreach ($h in $pending) { $unknown++; Add-StatisticsCombat $statistics 'unknown' $h.MatchId; Record 'unknown' @{request=$h.Id;reason='сменился процесс игры';wait_ms=[Math]::Round($now-$h.At,3);screen_gap_ms=$null} }
                Complete-StatisticsMatch $statistics 'process_changed'
                $statisticsDirty=$true
                $pending.Clear(); $signals.Clear(); $seen.Clear(); $seenOrder.Clear(); $epoch++; $deathOpen=$false; $dirty=$true
                Record 'game_connection' @{pid=$gamePid}
            }
        }
        $gameLines=@(Read-Tail $game)
        $gameReadAt=$clock.Elapsed.TotalMilliseconds
        $statisticsRebased=$game.Generation -ne $gameGeneration
        if ($statisticsRebased) { $statisticsRebaseInProgress=$true; $gameGeneration=$game.Generation; $epoch++; $deathOpen=$false; Complete-StatisticsMatch $statistics 'log_replaced'; $statisticsDirty=$true; $dirty=$true }
        if (-not $game.Healthy -or -not $game.CaughtUp) {
            foreach ($h in $pending) { $h.GameAvailable=$false }
            if ($statistics.current -and $statistics.current.status -ne 'complete' -and $statistics.current.full) { $statistics.current.full=$false; $statisticsDirty=$true; $dirty=$true }
        }
        foreach ($line in $gameLines) {
            # A replaced log can contain old matches. Only new appended lines count.
            if (-not $statisticsRebaseInProgress -and (Update-StatisticsLine $statistics $line)) { $statisticsDirty=$true; $dirty=$true }
            if ($line.Contains("Starting UI state machine 'loading/StateMachine'")) { $epoch++; $deathOpen=$false }
            if ($line.Contains("Starting UI state machine 'death/StateMachine'")) {
                if (-not $deathOpen) {
                    $at=$gameReadAt
                    $signals.Add([pscustomobject]@{At=$at;Epoch=$epoch;Used=$false})
                    Record 'death_screen_opened' @{read_ms=[Math]::Round($at,3);epoch=$epoch}
                }
                $deathOpen=$true
            }
            if ($line.Contains("Stopping UI state machine 'death/StateMachine'")) { $deathOpen=$false }
        }
        if ($game.CaughtUp) { $statisticsRebaseInProgress=$false }
        if ($sdk) {
            $lines=@(Read-Tail $sdk)
            $sdkReadAt=$clock.Elapsed.TotalMilliseconds
            # Request IDs remain deduplicated if NVIDIA rewrites the same process log.
            if ($sdk.Generation -ne $sdkGeneration) { $sdkGeneration=$sdk.Generation }
            foreach ($line in $lines) {
                if ($line -notmatch 'Posted Request HLSetVideo\[([a-zA-Z0-9]+)\]') { continue }
                $id=$Matches[1]
                if (-not $seen.Add($id)) { continue }
                $seenOrder.Enqueue($id)
                if ($seenOrder.Count -gt 8192) { [void]$seen.Remove($seenOrder.Dequeue()) }
                $at=$sdkReadAt
                $pending.Add([pscustomobject]@{Id=$id;At=$at;Epoch=$epoch;GameAvailable=($game.Healthy -and $game.CaughtUp);GameGeneration=$game.Generation;MatchId=$(if ($statistics.current) { $statistics.current.id } else { '' })})
                Record 'highlight' @{request=$id;read_ms=[Math]::Round($at,3);epoch=$epoch}
            }
        }
        $now=$clock.Elapsed.TotalMilliseconds
        foreach ($h in $pending.ToArray()) {
            if ($now-$h.At -lt 300 -or $now-$gameReadAt -gt 100) { continue }
            $ambiguous=$false
            $signal = $signals | Where-Object { -not $_.Used -and $_.Epoch -eq $h.Epoch -and [Math]::Abs($_.At-$h.At) -le 400 } | Sort-Object { [Math]::Abs($_.At-$h.At) } | Select-Object -First 1
            if ($signal) {
                $ambiguous = $null -ne ($pending | Where-Object { $_ -ne $h -and $_.Epoch -eq $signal.Epoch -and [Math]::Abs([Math]::Abs($_.At-$signal.At)-[Math]::Abs($h.At-$signal.At)) -le 25 } | Select-Object -First 1)
                $closer = $pending | Where-Object { $_ -ne $h -and $_.Epoch -eq $signal.Epoch -and [Math]::Abs($_.At-$signal.At) -lt [Math]::Abs($h.At-$signal.At) } | Select-Object -First 1
                if ($closer) { $signal=$null }
            }
            $reason=''
            if ($ambiguous) { $kind='unknown'; $unknown++; $reason='несколько событий NVIDIA слишком близко к экрану смерти' }
            elseif ($signal) { $signal.Used=$true; $kind='death'; $deaths++ }
            elseif (-not $h.GameAvailable -or -not $game.Healthy -or -not $game.CaughtUp -or $h.GameGeneration -ne $game.Generation) { $kind='unknown'; $unknown++; $reason='Game.log был недоступен, заменён или ещё не прочитан' }
            else { $kind='kill'; $kills++; Emit-Kill $false }
            Add-StatisticsCombat $statistics $kind $h.MatchId
            $statisticsDirty=$true
            Record $kind @{request=$h.Id;reason=$reason;wait_ms=[Math]::Round($now-$h.At,3);screen_gap_ms=$(if ($signal) { [Math]::Round($signal.At-$h.At,3) } else { $null })}
            [void]$pending.Remove($h); $dirty=$true
        }
        foreach ($signal in @($signals.ToArray())) { if ($signal.Used -or $now-$signal.At -gt 1500) { [void]$signals.Remove($signal) } }
        if ($now -ge $nextCommands) {
            $nextCommands=$now+100
            $showFile=Join-Path $runtime 'show.json'
            if ([IO.File]::Exists($showFile)) {
                try {
                    $show=(Read-SharedText $showFile 2048) | ConvertFrom-Json
                    if ($controlPanel -and $show.session -eq $session) {
                        Show-ControlPanel $controlPanel
                        $dirty=$true
                    }
                    [IO.File]::Delete($showFile)
                } catch {}
            }
            foreach ($file in @(Get-ChildItem -LiteralPath $commandsFolder -Filter '*.json' -File | Select-Object -First 32)) {
                try {
                    if ($file.Length -gt 2048) { [IO.File]::Delete($file.FullName); continue }
                    $command = (Read-SharedText $file.FullName 2048) | ConvertFrom-Json
                    if (-not $command -or -not $command.PSObject.Properties['session'] -or -not $command.PSObject.Properties['type'] -or $command.session -isnot [string] -or $command.type -isnot [string]) { [IO.File]::Delete($file.FullName); continue }
                    $commandId=[guid]::Empty
                    $validId=$command.PSObject.Properties['id'] -and [guid]::TryParse([string]$command.id,[ref]$commandId)
                    if ($validId -and $command.session -eq $session -and $command.type -in @('kill','death') -and $testKills.Count -lt 16 -and $commandIds.Count -lt 10000 -and $commandIds.Add($commandId.ToString('N'))) {
                        if ($command.type -eq 'kill') { $testKills.Add($clock.Elapsed.TotalMilliseconds+300) }
                        Record 'test' @{event_type=$command.type;suppressed=($command.type -eq 'death')}
                    }
                    [IO.File]::Delete($file.FullName)
                } catch { if (($file.LastWriteTimeUtc-[DateTime]::UtcNow).TotalSeconds -lt -10) { try { [IO.File]::Delete($file.FullName) } catch {} } }
            }
            if ([IO.File]::Exists($stopFile)) {
                try { $stop = (Read-SharedText $stopFile) | ConvertFrom-Json; if ($stop.session -eq $session) { $running=$false }; [IO.File]::Delete($stopFile) } catch {}
            }
        }
        if (-not $running) { $pending.Clear(); $testKills.Clear(); break }
        foreach ($due in $testKills.ToArray()) { if ($now -ge $due) { Emit-Kill $true; Record 'test_alert' @{}; [void]$testKills.Remove($due) } }
        if ($now -ge $nextSounds) {
            $nextSounds=$now+2000
            $soundFolder = Join-Path $root 'Sounds'
            $newSounds=@(Get-ChildItem -LiteralPath $soundFolder -File -ErrorAction SilentlyContinue | Where-Object { $_.Length -gt 0 -and $_.Extension.ToLowerInvariant() -in @('.mp3','.wav','.ogg','.m4a','.flac','.opus','.webm') } | Sort-Object Name | ForEach-Object { 'Sounds/'+[Uri]::EscapeDataString($_.Name) })
            if (($newSounds -join "`n") -cne ($sounds -join "`n")) { $sounds=$newSounds; $dirty=$true }
        }
        $health=[string]$game.Healthy+':'+[string]$game.CaughtUp+':'+[string]($sdk -and $sdk.Healthy)
        if ($health -ne $lastHealth) { $dirty=$true; $lastHealth=$health; Record 'log_status' @{game=($game.Healthy -and $game.CaughtUp);nvidia=($sdk -and $sdk.Healthy)} }
        if ($dirty -or $now -ge $nextPublish) {
            try { Publish; $nextPublish=$now+3000 }
            catch [IO.IOException] {
                Record 'error' @{message='Не удалось обновить файлы оверлея. Повтор через секунду.'}
                $dirty=$false; $nextPublish=$now+1000
            }
        }
        # Start-Sleep can discard queued Windows restore commands in this UI loop.
        if ($running) { [Threading.Thread]::Sleep($(if ($gamePid -or $testKills.Count) { 25 } else { 100 })) }
    }
    $events.Clear()
    Complete-StatisticsMatch $statistics 'tracker_stopped'
    $statisticsDirty=$true
    if ($controlPanel) { $controlPanel.Form.Hide() }
    Record 'stopped' @{kills=$kills;deaths=$deaths;unknown=$unknown}
    Publish
} catch {
    $errorText = Protect-LogText $_.Exception.Message
    Write-AtomicText (Join-Path $runtime 'startup-error.txt') $errorText
    if ($journal) { try { Record 'error' @{message=$errorText} } catch {} }
    if ($owns -and (Test-Path variable:running)) { $running=$false; try { Publish } catch {} }
    exit 1
} finally {
    if ($controlPanel) { $controlPanel.Form.Dispose() }
    if ($journal) { $journal.Dispose() }
    if ($readableJournal) { $readableJournal.Dispose() }
    if ($owns) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
