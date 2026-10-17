function Add-Dashboard($Panel,$Config) {
    $form=$Panel.Form
    $tabs=[Windows.Forms.TabControl]::new()
    $tabs.SetBounds(16,64,748,550); $tabs.Anchor='Top, Bottom, Left, Right'
    $pages=@{}
    foreach ($entry in @(@('stats','Статистика'),@('display','Оверлей'),@('connection','Подключение'))) {
        $page=[Windows.Forms.TabPage]::new($entry[1]); $page.BackColor=$form.BackColor; $page.ForeColor=$form.ForeColor
        $page.AutoScroll=$true; $tabs.TabPages.Add($page); $pages[$entry[0]]=$page
    }
    $form.Controls.Add($tabs)
    $row=0
    foreach ($name in @('game','gameLog','nvidia','image','sounds','counts')) {
        $label=New-DashboardLabel $pages.connection '' 16 (16+26*$row)
        $label.Font=[Drawing.Font]::new('Segoe UI',10,[Drawing.FontStyle]::Bold)
        $Panel.Statuses[$name]=$label; $row++
    }
    $paths=[Windows.Forms.TextBox]::new()
    $paths.Multiline=$true; $paths.ReadOnly=$true; $paths.BorderStyle='None'
    $paths.BackColor=$form.BackColor; $paths.ForeColor=$form.ForeColor
    $paths.SetBounds(16,184,690,72); $pages.connection.Controls.Add($paths)
    $paths.TabStop=$false; $Panel.Paths=$paths
    $Panel.Feedback.SetBounds(16,625,748,38); $Panel.Feedback.Anchor='Bottom, Left, Right'
    $footer=New-DashboardLabel $form 'Свернуть: продолжить сбор. Закрыть окно: остановить приложение.' 16 678 748 28
    $footer.Anchor='Bottom, Left, Right'
    $summary=New-DashboardLabel $pages.stats '' 16 16 690 150
    $summary.Font=[Drawing.Font]::new('Segoe UI',13)
    $sessionLabel=New-DashboardLabel $pages.stats '' 16 178 690 28
    $null=New-DashboardLabel $pages.stats 'Последние матчи' 16 220
    $history=[Windows.Forms.ListView]::new(); $history.SetBounds(16,254,690,170)
    $history.Anchor='Top, Left, Right'; $history.View='Details'; $history.FullRowSelect=$true
    $history.BackColor=$form.BackColor; $history.ForeColor=$form.ForeColor
    foreach ($column in @(@('Карта',150),@('Убийства',85),@('Смерти',85),@('Результат (оценка)',210),@('Время',130))) { [void]$history.Columns.Add($column[0],[int]$column[1]) }
    $pages.stats.Controls.Add($history)
    $null=New-DashboardLabel $pages.stats 'Сбор работает и без OBS. Убийства и исход матча определяются по признакам в логах, поэтому возможны ошибки.' 16 440 690 55
    $options=@{}; $fields=@{}; $numbers=@{}; $textFields=@{}
    foreach ($item in @(@('alerts','Показывать алерт убийства'),@('stats','Показывать статистику'))) {
        $check=[Windows.Forms.CheckBox]::new(); $check.Text=$item[1]; $check.Checked=$Config.display.($item[0])
        $check.SetBounds(16,(16+34*$options.Count),690,30); $pages.display.Controls.Add($check); $options[$item[0]]=$check
    }
    $null=New-DashboardLabel $pages.display 'Поля статистики' 16 92
    $labels=[ordered]@{kills='Убийства';deaths='Смерти';kd='K/D';games='Матчи';survival='Выживаемость (оценка)';maps='Частота карт';current='Текущий матч';unknown='Неопределенные события'}
    $index=0
    foreach ($key in $labels.Keys) {
        $check=[Windows.Forms.CheckBox]::new(); $check.Text=$labels[$key]; $check.Checked=$key -in $Config.display.fields
        $check.SetBounds((16+345*($index%2)),(124+30*[Math]::Floor($index/2)),340,28)
        $pages.display.Controls.Add($check); $fields[$key]=$check; $index++
    }
    $index=0
    foreach ($spec in @(@('scale','Масштаб, %',50,150,$Config.display.scale),@('opacity','Фон, %',0,100,$Config.display.opacity),@('volume','Громкость, %',0,100,($Config.volume*100)),@('durationMs','Алерт, мс',300,10000,$Config.durationMs))) {
        $x=16+172*$index; $null=New-DashboardLabel $pages.display $spec[1] $x 265 166
        $number=[Windows.Forms.NumericUpDown]::new(); $number.Minimum=$spec[2]; $number.Maximum=$spec[3]; $number.Value=$spec[4]
        $number.SetBounds($x,295,150,30); $pages.display.Controls.Add($number); $numbers[$spec[0]]=$number; $index++
    }
    foreach ($entry in @(@('kill','Тест убийства'),@('death','Тест смерти'),@('overlay','Открыть алерт'),@('stats-overlay','Открыть статистику'),@('new-session','Новая сессия'),@('save','Сохранить настройки'))) {
        $button=[Windows.Forms.Button]::new(); $button.Text=$entry[1]; $button.FlatStyle='Flat'
        $button.Tag=@{Actions=$Panel.Actions;Action=$entry[0]}
        $button.Add_Click({param($sender,$eventArgs) $sender.Tag.Actions.Enqueue($sender.Tag.Action)})
        $Panel.Buttons[$entry[0]]=$button
    }
    $index=0
    foreach ($key in @('kill','death','overlay','stats-overlay','new-session','save')) {
        $button=$Panel.Buttons[$key]; $pages.display.Controls.Add($button)
        $button.SetBounds((16+230*($index%3)),(352+52*[Math]::Floor($index/3)),218,42); $index++
    }
    $null=New-DashboardLabel $pages.display 'Изменения применяются после сохранения. Новая сессия сохраняет предыдущие счетчики в архив.' 16 468 690 45
    $index=0
    foreach ($spec in @(@('gameFolder','Папка игры или AUTO'),@('nvidiaLogs','Папка логов NVIDIA'),@('image','Картинка алерта, например Images/kill.png'))) {
        $null=New-DashboardLabel $pages.connection $spec[1] 16 (270+64*$index)
        $input=[Windows.Forms.TextBox]::new(); $input.Text=$Config.($spec[0]); $input.SetBounds(16,(298+64*$index),690,28)
        $input.Anchor='Top, Left, Right'; $pages.connection.Controls.Add($input); $textFields[$spec[0]]=$input; $index++
    }
    $null=New-DashboardLabel $pages.connection 'Сохранение на вкладке «Оверлей». Новые пути заработают после перезапуска.' 16 470 690 45
    foreach ($entry in @{Options=$options;Fields=$fields;Numbers=$numbers;TextFields=$textFields;Summary=$summary;SessionLabel=$sessionLabel;History=$history;HistorySignature='';Tabs=$tabs}.GetEnumerator()) { $Panel | Add-Member $entry.Key $entry.Value }
}

function New-DashboardLabel($Parent,[string]$Text,[int]$X,[int]$Y,[int]$Width=690,[int]$Height=28) {
    $label=[Windows.Forms.Label]::new(); $label.Text=$Text; $label.SetBounds($X,$Y,$Width,$Height)
    $Parent.Controls.Add($label); return $label
}

function Get-ControlPanelConfig($Panel,$Config) {
    $value=$Config | ConvertTo-Json -Depth 6 | ConvertFrom-Json
    foreach ($key in $Panel.Options.Keys) { $value.display.$key=$Panel.Options[$key].Checked }
    $value.display.fields=@('kills','deaths','kd','games','survival','maps','current','unknown' | Where-Object { $Panel.Fields[$_].Checked })
    $value.display.scale=[int]$Panel.Numbers.scale.Value; $value.display.opacity=[int]$Panel.Numbers.opacity.Value
    $value.volume=[double]$Panel.Numbers.volume.Value/100; $value.durationMs=[int]$Panel.Numbers.durationMs.Value
    foreach ($key in $Panel.TextFields.Keys) { $value.$key=$Panel.TextFields[$key].Text.Trim() }
    return $value
}

function Update-Dashboard($Panel,$Stats) {
    $kd=if ($Stats.deaths) { ($Stats.kills/$Stats.deaths).ToString('0.00') } elseif ($Stats.kills) { '∞' } else { '0.00' }
    $known=$Stats.survived+$Stats.lost
    $survival=if ($known) { [Math]::Round(100*$Stats.survived/$known).ToString()+'%' } else { 'нет данных' }
    $Panel.Summary.Text="Убийства: $($Stats.kills)     Смерти: $($Stats.deaths)     K/D: $kd`r`nМатчи: $($Stats.games)     Неопределенные события: $($Stats.unknown)`r`nВыживаемость (оценка): $survival   ($known матчей с оценкой)`r`nВыжили: $($Stats.survived)     Не выжили: $($Stats.lost)     Неизвестно: $($Stats.unresolved)"
    $Panel.SessionLabel.Text='Сессия с '+([DateTimeOffset]::Parse($Stats.started_at).ToLocalTime().ToString('dd.MM.yyyy HH:mm'))
    $signature=$Stats.recent | ConvertTo-Json -Depth 5 -Compress
    if ($Panel.HistorySignature -eq $signature) { return }
    $Panel.History.BeginUpdate(); $Panel.History.Items.Clear()
    foreach ($match in @($Stats.recent | Sort-Object ended_at -Descending)) {
        $names=@{cemetery='Stillwater';civilwar='Lawson';creek='DeSalle';colorado='Mammon'}
        $mapLabel=if ($names.ContainsKey([string]$match.map)) { $names[$match.map] } elseif ($match.map) { $match.map } else { '?' }
        $row=[Windows.Forms.ListViewItem]::new($mapLabel)
        [void]$row.SubItems.Add([string]$match.kills); [void]$row.SubItems.Add([string]$match.deaths)
        [void]$row.SubItems.Add(@{survived='Выжил';lost='Не выжил';unknown='Неизвестно'}[$match.outcome])
        [void]$row.SubItems.Add([DateTimeOffset]::Parse($match.ended_at).ToLocalTime().ToString('HH:mm:ss'))
        [void]$Panel.History.Items.Add($row)
    }
    $Panel.History.EndUpdate(); $Panel.HistorySignature=$signature
}
