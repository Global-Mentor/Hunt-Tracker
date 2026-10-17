function Write-AtomicText {
    param([string]$Path, [string]$Text)
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $backup = $temp + '.bak'
    [IO.File]::WriteAllText($temp, $Text, [Text.UTF8Encoding]::new($false))
    try {
        for ($attempt=0; $attempt -lt 5; $attempt++) {
            try {
                if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, $backup) }
                else { [IO.File]::Move($temp, $Path) }
                break
            } catch [IO.IOException] {
                if ($attempt -eq 4) { throw }
                [Threading.Thread]::Sleep(15)
            }
        }
    } finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
        if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
    }
}

function Read-SharedText {
    param([string]$Path, [long]$MaximumBytes = 1048576)
    $stream = [IO.File]::Open($Path, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    if ($stream.Length -gt $MaximumBytes) { $stream.Dispose(); throw 'Файл превышает допустимый размер.' }
    $reader = [IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function New-Tail {
    param([string]$Path, [bool]$SkipExisting)
    $state = [pscustomobject]@{
        Path=$Path; Position=0L; Created=[DateTime]::MinValue; Anchor=[byte[]]@()
        Decoder=[Text.Encoding]::UTF8.GetDecoder(); Remainder=''; Healthy=$false
        Generation=0; Buffer=[byte[]]::new(8192); Chars=[char[]]::new(8193); CaughtUp=$true
    }
    if ($SkipExisting -and [IO.File]::Exists($Path)) {
        $stream = $null
        try {
            $stream = [IO.File]::Open($Path, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $state.Created = [IO.File]::GetCreationTimeUtc($Path)
            $state.Position = $stream.Length
            $state.Anchor = [byte[]]::new([int][Math]::Min(64,$state.Position))
            $stream.Position = $state.Position - $state.Anchor.Length
            [void]$stream.Read($state.Anchor,0,$state.Anchor.Length)
            $state.Healthy = $true
        } catch { $state.Healthy = $false } finally { if ($stream) { $stream.Dispose() } }
    }
    return $state
}

function Read-Tail {
    param($State)
    $stream = $null
    $lines = [Collections.Generic.List[string]]::new()
    try {
        $stream = [IO.File]::Open($State.Path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $created = [IO.File]::GetCreationTimeUtc($State.Path)
        $reset = $created -ne $State.Created -or $stream.Length -lt $State.Position
        if (-not $reset -and $State.Anchor.Length) {
            $stream.Position = $State.Position - $State.Anchor.Length
            $check = [byte[]]::new($State.Anchor.Length)
            $read = $stream.Read($check,0,$check.Length)
            $reset = $read -ne $check.Length -or [Convert]::ToBase64String($check) -ne [Convert]::ToBase64String($State.Anchor)
        }
        if ($reset) {
            $State.Position=0L; $State.Remainder=''; $State.Decoder.Reset()
            $State.Created=$created; $State.Generation++
        }
        $stream.Position = $State.Position
        $limit = [Math]::Min($stream.Length,$State.Position+1048576)
        while ($stream.Position -lt $limit) {
            $read = $stream.Read($State.Buffer,0,[int][Math]::Min($State.Buffer.Length,$limit-$stream.Position))
            if (-not $read) { break }
            $count = $State.Decoder.GetChars($State.Buffer,0,$read,$State.Chars,0,$false)
            $parts = [regex]::Split(($State.Remainder+[string]::new($State.Chars,0,$count)), '\r?\n')
            for ($i=0; $i -lt $parts.Length-1; $i++) { $lines.Add($parts[$i].TrimStart([char]0xFEFF)) }
            $State.Remainder = $parts[-1]
            if ($State.Remainder.Length -gt 1048576) { $State.Remainder='' }
            $State.Position = $stream.Position
        }
        $State.CaughtUp = $State.Position -ge $stream.Length
        $State.Anchor = [byte[]]::new([int][Math]::Min(64,$State.Position))
        $stream.Position = $State.Position-$State.Anchor.Length
        [void]$stream.Read($State.Anchor,0,$State.Anchor.Length)
        $State.Healthy=$true
    } catch { $State.Healthy=$false } finally { if ($stream) { $stream.Dispose() } }
    return $lines.ToArray()
}

function Get-AppMutexName {
    param([string]$Root)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Root.ToLowerInvariant()))).Replace('-','').Substring(0,24) }
    finally { $sha.Dispose() }
    return 'Local\HuntKillFiles-' + $hash
}

function Find-HuntFolder {
    $roots = [Collections.Generic.List[string]]::new()
    foreach ($entry in @(@('HKCU:\Software\Valve\Steam','SteamPath'),@('HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','InstallPath'))) {
        $value = Get-ItemProperty -LiteralPath $entry[0] -Name $entry[1] -ErrorAction SilentlyContinue
        if ($value) { $roots.Add([string]$value.($entry[1])) }
    }
    $roots.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam'))
    $libraries = [Collections.Generic.List[string]]::new()
    foreach ($root in $roots) {
        $libraries.Add($root)
        $vdf = Join-Path $root 'steamapps/libraryfolders.vdf'
        if ([IO.File]::Exists($vdf)) {
            foreach ($match in [regex]::Matches([IO.File]::ReadAllText($vdf),'"path"\s+"([^"]+)"')) { $libraries.Add($match.Groups[1].Value.Replace('\\','\')) }
        }
    }
    foreach ($library in $libraries) {
        $candidate = Join-Path $library 'steamapps/common/Hunt Showdown 1896'
        if ([IO.Directory]::Exists($candidate)) { return $candidate }
    }
    return Join-Path $roots[-1] 'steamapps/common/Hunt Showdown 1896'
}

function Get-PortablePath {
    param([string]$Path)
    if ($env:LOCALAPPDATA -and $Path.StartsWith($env:LOCALAPPDATA,[StringComparison]::OrdinalIgnoreCase)) { return '%LOCALAPPDATA%' + $Path.Substring($env:LOCALAPPDATA.Length).Replace('\','/') }
    if ($env:USERPROFILE -and $Path.StartsWith($env:USERPROFILE,[StringComparison]::OrdinalIgnoreCase)) { return '%USERPROFILE%' + $Path.Substring($env:USERPROFILE.Length).Replace('\','/') }
    return $Path.Replace('\','/')
}

function Resolve-SettingPath {
    param([string]$Value,[string]$Root)
    $expanded = [Environment]::ExpandEnvironmentVariables($Value)
    if ([IO.Path]::IsPathRooted($expanded)) { return [IO.Path]::GetFullPath($expanded) }
    return [IO.Path]::GetFullPath((Join-Path $Root $expanded))
}

function Protect-LogText {
    param([string]$Text)
    foreach ($entry in @(@($env:LOCALAPPDATA,'%LOCALAPPDATA%'),@($env:USERPROFILE,'%USERPROFILE%'))) {
        if ($entry[0]) {
            $Text = [regex]::Replace($Text,[regex]::Escape($entry[0]),$entry[1],[Text.RegularExpressions.RegexOptions]::IgnoreCase)
            $Text = [regex]::Replace($Text,[regex]::Escape($entry[0].Replace('\','/')),$entry[1],[Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }
    return $Text.Replace("`r",' ').Replace("`n",' ')
}

function Read-AppConfig {
    param([string]$Root, $Candidate = $null)
    try {
        $file=Join-Path $Root 'config.json'
        if (-not $Candidate -and -not [IO.File]::Exists($file)) {
            Write-AtomicText $file (Read-SharedText (Join-Path $Root 'config.example.json'))
        }
        $config = if ($Candidate) { $Candidate } else { (Read-SharedText $file 16384) | ConvertFrom-Json }
        foreach ($name in @('gameFolder','nvidiaLogs','volume','durationMs')) {
            if (-not $config.PSObject.Properties[$name]) { throw "Отсутствует параметр $name." }
        }
        foreach ($name in @('gameFolder','nvidiaLogs')) {
            if ($config.$name -isnot [string] -or [string]::IsNullOrWhiteSpace($config.$name)) { throw "Параметр $name должен содержать путь." }
            $config.$name=$config.$name.Trim()
        }
        if ($config.volume -is [bool] -or $config.volume -is [string] -or $null -eq $config.volume -or
            [double]::IsNaN([double]$config.volume) -or [double]::IsInfinity([double]$config.volume) -or
            [double]$config.volume -lt 0 -or [double]$config.volume -gt 1) { throw 'volume должен быть числом от 0 до 1.' }
        if ($config.durationMs -is [bool] -or $config.durationMs -is [string] -or $null -eq $config.durationMs -or
            [double]$config.durationMs -lt 300 -or [double]$config.durationMs -gt 10000 -or
            [double]$config.durationMs -ne [Math]::Truncate([double]$config.durationMs)) { throw 'durationMs должен быть целым числом от 300 до 10000.' }
        if (-not $config.PSObject.Properties['image']) { $config | Add-Member image 'Images/kill.png' }
        if ($config.image -isnot [string] -or $config.image -notmatch '^Images/[a-zA-Z0-9 _.-]+\.(png|svg|webp|gif|jpg|jpeg)$') { throw 'image должен указывать на файл в Images, например Images/kill.png.' }
        if (-not $config.PSObject.Properties['display']) {
            $config | Add-Member display ([pscustomobject]@{alerts=$true;stats=$true;scale=100;opacity=90;fields=@('kills','deaths','kd','games','survival','maps','current','unknown')})
        }
        foreach ($key in @('alerts','stats')) {
            if ($config.display.$key -isnot [bool]) { throw "display.$key должен быть true или false." }
        }
        foreach ($key in @('scale','opacity')) {
            $value=$config.display.$key
            $min=if ($key -eq 'scale') { 50 } else { 0 }
            $max=if ($key -eq 'scale') { 150 } else { 100 }
            if ($null -eq $value -or $value -is [bool] -or $value -is [string] -or [double]$value -lt $min -or [double]$value -gt $max -or [double]$value -ne [Math]::Truncate([double]$value)) { throw "display.$key должен быть целым числом от $min до $max." }
        }
        if ($config.display.fields -isnot [Array]) { throw 'display.fields должен быть массивом.' }
        foreach ($field in $config.display.fields) {
            if ($field -notin @('kills','deaths','kd','games','survival','maps','current','unknown')) { throw "Неизвестное поле статистики: $field" }
        }
        $config.display.fields=@($config.display.fields | Select-Object -Unique)
        return $config
    } catch { throw ('Ошибка config.json: ' + (Protect-LogText $_.Exception.Message)) }
}
