$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath((Join-Path $root '.local/build'))
$staging=[IO.Path]::GetFullPath((Join-Path $root ('.local/publish-'+[guid]::NewGuid().ToString('N'))))
[void][IO.Directory]::CreateDirectory($output)
try {
    & dotnet publish (Join-Path $root 'src/hunt-tracker-desktop/HuntTracker.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $staging -p:RestoreLockedMode=true --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $source=Join-Path $staging 'HuntTracker.exe'
    $destination=Join-Path $output 'HuntTracker.exe'
    for ($attempt=0; ; $attempt++) {
        try {
            if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($source,$destination,[System.Management.Automation.Language.NullString]::Value) }
            else { [IO.File]::Move($source,$destination) }
            break
        } catch [IO.IOException] {
            if ($attempt -ge 4) { throw }
            [Threading.Thread]::Sleep(100)
        }
    }
    foreach ($name in @('CommunityToolkit.Mvvm.dll','HuntTracker.Core.dll','HuntTracker.Core.pdb','HuntTracker.deps.json','HuntTracker.dll','HuntTracker.pdb','HuntTracker.runtimeconfig.json')) {
        $file=Join-Path $output $name
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
} finally {
    $allowed=[IO.Path]::GetFullPath((Join-Path $root '.local'))+'\'
    if (-not $staging.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path' }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
