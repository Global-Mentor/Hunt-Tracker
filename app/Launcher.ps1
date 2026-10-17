param([ValidateSet('Start','Stop')][string]$Action)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Files.ps1')
$runtime=Join-Path $root '_runtime'
[void][IO.Directory]::CreateDirectory($runtime)
$stateFile=Join-Path $runtime 'state.json'
function Show-Error($Text) {
    Add-Type -AssemblyName System.Windows.Forms
    [void][Windows.Forms.MessageBox]::Show([string]$Text,'Hunt Tracker')
}
if ($Action -eq 'Stop') {
    try {
        $state=(Read-SharedText $stateFile) | ConvertFrom-Json
        if (-not $state.running -or ([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($state.updated_at)).TotalSeconds -gt 10) { exit }
        Write-AtomicText (Join-Path $runtime 'stop.json') ((@{session=$state.session} | ConvertTo-Json -Compress))
        $deadline=[DateTime]::UtcNow.AddSeconds(4)
        do {
            Start-Sleep -Milliseconds 50
            $state=(Read-SharedText $stateFile) | ConvertFrom-Json
            if (-not $state.running) { exit }
        } while ([DateTime]::UtcNow -lt $deadline)
        Show-Error 'Не получено подтверждение остановки. Подождите и повторите Stop.bat.'
    } catch { if ([IO.File]::Exists($stateFile)) { Show-Error ('Не удалось отправить остановку: '+(Protect-LogText $_.Exception.Message)) } }
    exit
}
$name=Get-AppMutexName $root
$mutex=[Threading.Mutex]::new($false,$name)
$available=$false
try {
    try { $available=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $available=$true }
    if (-not $available) {
        try {
            $state=(Read-SharedText $stateFile) | ConvertFrom-Json
            Write-AtomicText (Join-Path $runtime 'show.json') (@{session=$state.session} | ConvertTo-Json -Compress)
            $deadline=[DateTime]::UtcNow.AddSeconds(4)
            do {
                Start-Sleep -Milliseconds 50
                $state=(Read-SharedText $stateFile) | ConvertFrom-Json
                if ($state.running -and $state.panel_visible -and -not [IO.File]::Exists((Join-Path $runtime 'show.json'))) { exit }
            } while ([DateTime]::UtcNow -lt $deadline)
            Show-Error 'Трекер запущен, но панель не подтвердила открытие. Выполните Stop.bat, затем Start.bat.'
        } catch { Show-Error 'Не удалось открыть панель. Повторите Start.bat через несколько секунд.' }
        exit
    }
    $mutex.ReleaseMutex()
} finally { $mutex.Dispose() }
$errorFile=Join-Path $runtime 'startup-error.txt'
if ([IO.File]::Exists($errorFile)) { [IO.File]::Delete($errorFile) }
$oldSession=''
if ([IO.File]::Exists($stateFile)) { try { $oldSession=((Read-SharedText $stateFile) | ConvertFrom-Json).session } catch {} }
$tracker=Join-Path $PSScriptRoot 'Tracker.ps1'
try { $null=Read-AppConfig $root } catch { Show-Error $_.Exception.Message; exit 1 }
$startInfo=[Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName=Join-Path $PSHOME 'powershell.exe'
$startInfo.WorkingDirectory=$root
$startInfo.UseShellExecute=$false
$startInfo.CreateNoWindow=$true
$startInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.Arguments='-NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "'+$tracker+'" -Panel'
$process=[Diagnostics.Process]::Start($startInfo)
$deadline=[DateTime]::UtcNow.AddSeconds(8)
do {
    Start-Sleep -Milliseconds 100
    if ([IO.File]::Exists($stateFile)) {
        try { $state=(Read-SharedText $stateFile) | ConvertFrom-Json; if ($state.running -and $state.panel_visible -and $state.session -ne $oldSession) { exit } } catch {}
    }
    $process.Refresh()
} while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline)
if ([IO.File]::Exists($errorFile)) { Show-Error (Read-SharedText $errorFile) }
elseif ($process.HasExited) { Show-Error 'Программа не запустилась. Проверьте config.json и права записи в папку.' }
else { Show-Error 'Запуск занимает больше обычного. Проверьте панель приложения и папку Logs.' }
