param([string]$PackagePath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Fixtures.ps1')
$fixture=New-TestDirectory 'package'
$zip=if ($PackagePath) { [IO.Path]::GetFullPath($PackagePath) } else { & (Join-Path $root 'scripts/Package.ps1') -OutputDirectory $fixture }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries=@($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    foreach ($entry in $archive.Entries) {
        $parts=$entry.FullName.Replace('\','/').Split('/')
        for ($i=0; $i -lt $parts.Count-1; $i++) {
            if ($parts[$i] -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { throw ('Inconsistent package directory name: '+$parts[$i]) }
        }
    }
    $forbidden=@($entries | Where-Object { $_ -match '(^|/)(config.json|data|logs|_runtime|AGENTS.md|\.local|\.git|tests)(/|$)' })
    if ($forbidden.Count) { throw 'Private or development files in package' }
    if ('hunt-tracker/app/HuntTracker.exe' -notin $entries -or @($entries | Where-Object { $_ -match '\.(ps1|pdb)$' }).Count) { throw 'Invalid native package contents' }
    if (@($entries | Where-Object { $_ -match '^hunt-tracker/(Image|Images|Sounds)/' }).Count) { throw 'Media outside source in package' }
    foreach ($asset in @('source/images/kill/skull.png','source/images/death/skull.png','source/images/trade/skull.png','source/sounds/kill/kill.mp3','source/sounds/death/death.wav','source/sounds/trade/trade.wav')) {
        if (('hunt-tracker/'+$asset) -notin $entries) { throw "Missing bundled media: $asset" }
    }
} finally { $archive.Dispose() }
$out=Join-Path $fixture 'unpacked'
Expand-Archive -LiteralPath $zip -DestinationPath $out
$app=Join-Path $out 'hunt-tracker'
foreach ($asset in @('source/images/kill/skull.png','source/images/death/skull.png','source/images/trade/skull.png','source/sounds/kill/kill.mp3','source/sounds/death/death.wav','source/sounds/trade/trade.wav')) {
    if ((Get-FileHash (Join-Path $root $asset)).Hash -ne (Get-FileHash (Join-Path $app $asset)).Hash) { throw "Changed bundled media: $asset" }
}
$defaults=Get-Content (Join-Path $app 'config.example.json') -Raw | ConvertFrom-Json
if ($defaults.volume -ne 0.5 -or $defaults.durationMs -ne 1800) { throw 'Standalone defaults changed' }
$defaults.gameFolder='./game'; $defaults.nvidiaLogs='./sdk'
$defaults | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $app 'config.example.json') -Encoding utf8
$info=[Diagnostics.ProcessStartInfo]::new()
$info.FileName=Join-Path $app 'app/HuntTracker.exe'
$info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle='Hidden'
$info.Arguments='--game-process NoSuchPackageFixture'
$bundle=Join-Path $fixture 'bundled-runtime'
$info.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=$bundle
$info.EnvironmentVariables['DOTNET_ROOT']=Join-Path $fixture 'no-installed-dotnet'
$info.EnvironmentVariables['DOTNET_ROOT_X64']=$info.EnvironmentVariables['DOTNET_ROOT']
$info.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP']='0'
$process=[Diagnostics.Process]::Start($info)
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    $file=Join-Path $app '_runtime/state.json'
    do {
        [Threading.Thread]::Sleep(50)
        $state=if (Test-Path -LiteralPath $file) { Read-TestJson $file } else { $null }
    } while ((-not $state -or -not $state.panel_visible) -and [DateTime]::UtcNow -lt $deadline -and -not $process.HasExited)
    if (-not (Test-Path -LiteralPath $file)) { throw 'Clean package did not start' }
    $state=Read-TestJson $file
    if (-not $state.running -or -not $state.panel_visible -or $state.media.kill.images.Count -ne 1 -or $state.media.kill.sounds.Count -ne 1 -or $state.media.kill.sounds[0] -ne 'source/sounds/kill/kill.mp3') { throw 'Invalid clean package state' }
    $process.Refresh()
    foreach ($library in @('wpfgfx_cor3.dll','PresentationNative_cor3.dll')) {
        $module=@($process.Modules | Where-Object { $_.ModuleName -ieq $library })
        if ($module.Count -ne 1 -or -not $module[0].FileName.StartsWith($bundle+'\',[StringComparison]::OrdinalIgnoreCase)) { throw "Installed runtime used or bundled library missing: $library; loaded: $($module | ForEach-Object { $_.FileName }); expected: $bundle" }
    }
    if (@($process.Modules | Where-Object { $_.ModuleName -in @('coreclr.dll','clrjit.dll','hostfxr.dll') }).Count) { throw 'External CLR module loaded by single-file EXE' }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BundledRuntimeCheck {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    public static bool HasRuntime(string file) {
        var module=LoadLibraryEx(file, IntPtr.Zero, 1);
        if (module==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { return GetProcAddress(module,"DotNetRuntimeInfo")!=IntPtr.Zero && GetProcAddress(module,"g_CLREngineMetrics")!=IntPtr.Zero; }
        finally { FreeLibrary(module); }
    }
}
'@
    if (-not [BundledRuntimeCheck]::HasRuntime($info.FileName)) { throw 'CoreCLR is not linked into the EXE' }
    Add-Type -AssemblyName System.Drawing
    $icon=[Drawing.Icon]::ExtractAssociatedIcon($info.FileName)
    try { if (-not $icon -or $icon.Width -lt 16) { throw 'EXE icon missing' } } finally { if ($icon) { $icon.Dispose() } }
    $config=Get-Content (Join-Path $app 'config.json') -Raw | ConvertFrom-Json
    if ($config.gameFolder -ne './game') { throw 'Default config missing' }
    $second=[Diagnostics.Process]::Start($info)
    try { if (-not $second.WaitForExit(5000) -or $second.ExitCode -ne 0) { throw 'Reopen failed' } } finally { if (-not $second.HasExited) { $second.Kill() }; $second.Dispose() }
    $reopened=Read-TestJson $file
    if ($reopened.tracker_pid -ne $state.tracker_pid -or $reopened.session -ne $state.session) { throw 'Reopen created second tracker' }
    $info.Arguments='--stop'
    for ($attempt=0; ; $attempt++) {
        try {
            $locked=[IO.FileStream]::new($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
            break
        } catch [IO.IOException] {
            if ($attempt -ge 39) { throw }
            [Threading.Thread]::Sleep(25)
        }
    }
    try {
        $stopper=[Diagnostics.Process]::Start($info)
        [Threading.Thread]::Sleep(250)
    } finally { $locked.Dispose() }
    try { if (-not $stopper.WaitForExit(15000) -or $stopper.ExitCode -ne 0) { throw 'Stop launcher failed' } } finally { if (-not $stopper.HasExited) { $stopper.Kill() }; $stopper.Dispose() }
    if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'Package did not stop cleanly' }
    'PASS: bundled CLR and WPF loaded, EXE icon, clean package, original media, Standalone defaults, first launch, single instance, transient file lock, graceful stop.'
} finally { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() }
