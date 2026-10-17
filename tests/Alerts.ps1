$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'app/Alerts.ps1')
function Assert($Condition,$Message) { if (-not $Condition) { throw $Message } }
function Highlight($At=0) { return [pscustomobject]@{At=$At;Mode='trades';TradeBlocked=$false;Epoch=1;MatchId='match';GameAvailable=$true;GameGeneration=1} }
function Screen($At=150) { return [pscustomobject]@{At=$At;Epoch=1;Used=$false} }
$game=[pscustomobject]@{Healthy=$true;CaughtUp=$true;Generation=1}
Assert ((Get-AlertDelay 'kills') -eq 300 -and (Get-AlertDelay 'kills-deaths') -eq 300 -and (Get-AlertDelay 'trades') -eq 500) 'Wrong delay'
foreach ($mode in @('kills','kills-deaths','trades')) {
    Assert (Test-AlertEnabled $mode 'kill') 'Kill missing'
    Assert ((Test-AlertEnabled $mode 'death') -eq ($mode -ne 'kills')) 'Death filter wrong'
    Assert ((Test-AlertEnabled $mode 'trade') -eq ($mode -eq 'trades')) 'Trade filter wrong'
    Assert (-not (Test-AlertEnabled $mode 'unknown')) 'Unknown event displayed'
}
$a=Highlight; $b=Highlight 500; $screen=Screen 400
Assert ($null -ne (Find-TradePair $a @($a,$b) @($screen) $game 1)) '500ms boundary rejected'
$b.At=501
Assert ($null -eq (Find-TradePair $a @($a,$b) @($screen) $game 1)) 'Outside window paired'
$b.At=100
Assert ($null -eq (Find-TradePair $a @($a,$b) @((Screen -450)) $game 1)) 'Overall trade span exceeded 500ms'
Assert ($null -eq (Find-TradePair $a @($a) @($screen) $game 1)) 'One highlight became trade'
Assert ($null -eq (Find-TradePair $a @($a,$b) @() $game 1)) 'No death screen became trade'
$b.MatchId='other'
Assert ($null -eq (Find-TradePair $a @($a,$b) @($screen) $game 1)) 'Cross-match pair'
$b.MatchId='match'; $b.Epoch=2
Assert ($null -eq (Find-TradePair $a @($a,$b) @($screen) $game 1)) 'Cross-epoch pair'
$b.Epoch=1; $b.GameAvailable=$false
Assert ($null -eq (Find-TradePair $a @($a,$b) @($screen) $game 1)) 'Read gap paired'
$b.GameAvailable=$true; $game.Generation=2
Assert ($null -eq (Find-TradePair $a @($a,$b) @($screen) $game 1)) 'Replaced log paired'
$game.Generation=1; $game.Healthy=$false
Assert ($null -eq (Find-TradePair $a @($a,$b) @($screen) $game 1)) 'Missing log paired'
$game.Healthy=$true
Assert ($null -eq (Find-TradePair $a @($a,$b) @($screen) $game 2)) 'Changed epoch paired'
$c=Highlight 200
Assert ($null -eq (Find-TradePair $a @($a,$b,$c) @($screen) $game 1)) 'Three highlights paired'
Assert ($null -eq (Find-TradePair $b @($b,$c) @($screen) $game 1)) 'Ambiguous group became trade after removing a request'
$a=Highlight; $b=Highlight 200
Assert ($null -eq (Find-TradePair $a @($a,$b) @((Screen 100),(Screen 200)) $game 1)) 'Two screens paired'
Assert ($a.TradeBlocked -and $b.TradeBlocked) 'Ambiguity not retained'
'PASS: alert modes, 300/500ms gates, trade boundary, single events, ambiguity, missing/replaced logs, match isolation.'
