function Get-AlertDelay([string]$Mode) {
    if ($Mode -eq 'trades') { return 500 }
    return 300
}

function Test-AlertEnabled([string]$Mode,[string]$Kind) {
    return $Kind -eq 'kill' -or ($Kind -eq 'death' -and $Mode -in @('kills-deaths','trades')) -or ($Kind -eq 'trade' -and $Mode -eq 'trades')
}

function Find-TradePair($First,$Pending,$Signals,$Game,[int]$Epoch) {
    if ($First.TradeBlocked -or $First.Mode -ne 'trades' -or $First.Epoch -ne $Epoch -or -not $Game.Healthy -or -not $Game.CaughtUp) { return $null }
    $group=@($Pending | Where-Object { $_.Epoch -eq $First.Epoch -and $_.MatchId -eq $First.MatchId -and [Math]::Abs($_.At-$First.At) -le 500 })
    if ($group.Count -gt 2) { foreach ($item in $group) { $item.TradeBlocked=$true } }
    if ($group.Count -ne 2) { return $null }
    foreach ($item in $group) {
        if ($item.TradeBlocked -or $item.Mode -ne 'trades' -or -not $item.GameAvailable -or $item.GameGeneration -ne $Game.Generation) { return $null }
    }
    $screens=@($Signals | Where-Object { -not $_.Used -and $_.Epoch -eq $First.Epoch -and [Math]::Abs($_.At-$First.At) -le 500 })
    if ($screens.Count -gt 1) { foreach ($item in $group) { $item.TradeBlocked=$true } }
    if ($screens.Count -ne 1) { return $null }
    $times=@($group[0].At,$group[1].At,$screens[0].At) | Measure-Object -Minimum -Maximum
    if ($times.Maximum-$times.Minimum -gt 500) { return $null }
    # A third nearby request makes attribution uncertain, even across the group boundary.
    $nearScreen=@($Pending | Where-Object { $_.Epoch -eq $First.Epoch -and [Math]::Abs($_.At-$screens[0].At) -le 500 })
    if ($nearScreen.Count -ne 2) { foreach ($item in $nearScreen) { $item.TradeBlocked=$true }; return $null }
    return [pscustomobject]@{Other=($group | Where-Object { $_ -ne $First } | Select-Object -First 1);Signal=$screens[0]}
}
