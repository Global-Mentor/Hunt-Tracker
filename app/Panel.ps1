function New-ControlPanel($Config) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    if (-not ('HuntKillWindow' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HuntKillWindow {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
}
'@
    }
    [Windows.Forms.Application]::EnableVisualStyles()
    $actions = [Collections.Generic.Queue[string]]::new()
    $form = [Windows.Forms.Form]::new()
    $form.Text = 'Hunt Tracker'
    $form.ClientSize = [Drawing.Size]::new(780,720)
    $form.MinimumSize = $form.Size
    $form.StartPosition = 'CenterScreen'
    $form.AutoScaleMode = 'Dpi'
    $form.Font = [Drawing.Font]::new('Segoe UI',10)
    $form.BackColor = [Drawing.Color]::FromArgb(28,30,29)
    $form.ForeColor = [Drawing.Color]::FromArgb(230,228,220)
    $form.Tag = @{Actions=$actions;Closing=$false}
    $title = [Windows.Forms.Label]::new()
    $title.Text = 'Hunt Tracker'
    $title.Font = [Drawing.Font]::new('Georgia',21)
    $title.SetBounds(20,16,720,40)
    $form.Controls.Add($title)
    $feedback = [Windows.Forms.Label]::new()
    $feedback.ForeColor = [Drawing.Color]::FromArgb(214,209,170)
    $form.Controls.Add($feedback)
    $form.Add_FormClosing({ param($sender,$eventArgs)
        $eventArgs.Cancel=$true
        if (-not $sender.Tag.Closing) {
            $sender.Tag.Closing=$true
            $sender.Tag.Actions.Clear()
            $sender.Tag.Actions.Enqueue('stop')
        }
    })
    $panel=[pscustomobject]@{Form=$form;Statuses=@{};Paths=$null;Feedback=$feedback;Actions=$actions;Buttons=@{}}
    Add-Dashboard $panel $Config
    return $panel
}

. (Join-Path $PSScriptRoot 'Dashboard.ps1')

function Show-ControlPanel($Panel) {
    if ($Panel.Form.Tag.Closing) { return }
    if ($Panel.Form.WindowState -eq 'Minimized') { $Panel.Form.WindowState='Normal' }
    $Panel.Form.Show()
    # The hidden PowerShell launch can suppress the first native window show.
    [void][HuntKillWindow]::ShowWindow($Panel.Form.Handle,5)
    $Panel.Form.BringToFront()
    $Panel.Form.Activate()
}

function Update-ControlPanel($Panel,$State) {
    $green=[Drawing.Color]::FromArgb(135,210,145)
    $red=[Drawing.Color]::FromArgb(245,125,115)
    $amber=[Drawing.Color]::FromArgb(235,193,108)
    $values=@{
        game=@(('Игра: '+$(if ($State.game_pid) { 'запущена' } else { 'ожидание запуска' })), $(if ($State.game_pid) { $green } else { $amber }))
        gameLog=@(('Game.log: '+$(if ($State.game_readable) { 'читается' } else { 'не найден или недоступен' })), $(if ($State.game_readable) { $green } else { $red }))
        nvidia=@(('NVIDIA: '+$(if ($State.sdk_readable) { 'лог читается' } elseif ($State.game_pid) { 'лог не найден или недоступен' } else { 'ожидание игры' })), $(if ($State.sdk_readable) { $green } elseif ($State.game_pid) { $red } else { $amber }))
        image=@(('Картинка: '+$(if ($State.image_found) { 'найдена' } else { 'не найдена' })), $(if ($State.image_found) { $green } else { $red }))
        sounds=@(('Звуков: '+$State.sounds.Count+$(if (-not $State.sounds.Count) { ' (алерт без звука)' })), $(if ($State.sounds.Count) { $green } else { $amber }))
        counts=@(('Убийств: '+$State.kills+'   |   Смертей: '+$State.deaths+'   |   Не определено: '+$State.unknown), $Panel.Form.ForeColor)
    }
    foreach ($name in $values.Keys) {
        $Panel.Statuses[$name].Text=$values[$name][0]
        $Panel.Statuses[$name].ForeColor=$values[$name][1]
    }
    $Panel.Paths.Text = 'Game.log: '+$State.game_log+"`r`nNVIDIA: "+$(if ($State.sdk_log) { $State.sdk_log } else { $State.nvidia_folder })
    if ($State.warning) { $Panel.Feedback.Text=$State.warning; $Panel.Feedback.ForeColor=$red }
    Update-Dashboard $Panel $State.statistics
}
