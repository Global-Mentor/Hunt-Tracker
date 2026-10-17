function New-DashboardLabel($Parent,[string]$Text,[int]$X,[int]$Y,[int]$Width=760,[int]$Height=28) {
    $label=[Windows.Forms.Label]::new(); $label.Text=$Text; $label.SetBounds($X,$Y,$Width,$Height)
    $Parent.Controls.Add($label); return $label
}

function New-DashboardButton($Parent,$Panel,[string]$Key,[string]$Text,[int]$X,[int]$Y,[int]$Width=240) {
    $button=[Windows.Forms.Button]::new(); $button.Text=$Text; $button.FlatStyle='Flat'
    $button.FlatAppearance.BorderColor=[Drawing.Color]::FromArgb(91,88,75)
    $button.FlatAppearance.MouseOverBackColor=[Drawing.Color]::FromArgb(49,48,41)
    $button.BackColor=[Drawing.Color]::FromArgb(29,30,27); $button.Cursor='Hand'
    $button.SetBounds($X,$Y,$Width,38)
    $button.Tag=@{Actions=$Panel.Actions;Action=$Key}
    $button.Add_Click({param($sender,$eventArgs) if ($sender.Tag.Action -eq 'statistics') { $sender.Tag.Tabs.SelectedIndex=0 } else { $sender.Tag.Actions.Enqueue($sender.Tag.Action) }})
    $Parent.Controls.Add($button); $Panel.Buttons[$Key]=$button
    return $button
}

function Add-Dashboard($Panel,$Config) {
    $form=$Panel.Form
    $gold=[Drawing.Color]::FromArgb(205,196,147)
    $muted=[Drawing.Color]::FromArgb(168,169,158)
    $tips=[Windows.Forms.ToolTip]::new(); $tips.AutoPopDelay=15000; $tips.InitialDelay=350; $tips.ReshowDelay=100; $tips.ShowAlways=$true
    $tabs=[Windows.Forms.TabControl]::new()
    $viewport=[Windows.Forms.Panel]::new(); $viewport.SetBounds(16,124,828,568); $viewport.Anchor='Top, Bottom, Left, Right'
    $viewport.BackColor=$form.BackColor; $form.Controls.Add($viewport)
    $tabs.SetBounds(-4,-42,836,614); $tabs.Anchor='Top, Bottom, Left, Right'
    $tabs.SizeMode='Fixed'; $tabs.ItemSize=[Drawing.Size]::new(265,38)
    $pages=@{}
    foreach ($entry in @(@('stats','Статистика'),@('display','Оверлей и алерты'),@('connection','Подключение'))) {
        $page=[Windows.Forms.TabPage]::new($entry[1]); $page.BackColor=$form.BackColor; $page.ForeColor=$form.ForeColor
        $page.AutoScroll=$true; $tabs.TabPages.Add($page); $pages[$entry[0]]=$page
    }
    $viewport.Controls.Add($tabs)
    $navigation=[Windows.Forms.TableLayoutPanel]::new(); $navigation.SetBounds(16,82,828,38); $navigation.Anchor='Top, Left, Right'
    $navigation.ColumnCount=3; $navigation.RowCount=1; $navigation.Margin=0; $navigation.Padding=0
    $navButtons=@()
    for ($i=0; $i -lt 3; $i++) {
        [void]$navigation.ColumnStyles.Add([Windows.Forms.ColumnStyle]::new([Windows.Forms.SizeType]::Percent,33.333))
        $nav=[Windows.Forms.Button]::new(); $nav.Text=$tabs.TabPages[$i].Text; $nav.Dock='Fill'; $nav.Margin=0; $nav.FlatStyle='Flat'
        $nav.FlatAppearance.BorderColor=[Drawing.Color]::FromArgb(85,83,68); $nav.Cursor='Hand'
        $nav.Tag=@{Tabs=$tabs;Index=$i}
        $nav.Add_Click({param($sender,$eventArgs) $sender.Tag.Tabs.SelectedIndex=$sender.Tag.Index})
        $navigation.Controls.Add($nav,$i,0); $navButtons+=@($nav)
    }
    $tabs.Tag=@{Navigation=$navButtons}
    $tabs.Add_SelectedIndexChanged({param($sender,$eventArgs)
        for ($i=0; $i -lt $sender.Tag.Navigation.Count; $i++) {
            $sender.Tag.Navigation[$i].BackColor=if ($sender.SelectedIndex -eq $i) { [Drawing.Color]::FromArgb(58,54,37) } else { [Drawing.Color]::FromArgb(24,26,23) }
            $sender.Tag.Navigation[$i].ForeColor=if ($sender.SelectedIndex -eq $i) { [Drawing.Color]::FromArgb(222,212,163) } else { [Drawing.Color]::FromArgb(198,198,183) }
        }
    })
    $navButtons[0].BackColor=[Drawing.Color]::FromArgb(58,54,37)
    $navButtons[0].ForeColor=$gold
    $form.Controls.Add($navigation)
    $open=New-DashboardButton $form $Panel 'statistics' 'Открыть статистику' 584 24 260
    $open.Anchor='Top, Right'; $open.Tag.Tabs=$tabs
    $tips.SetToolTip($open,'Полная статистика в приложении. Работает без OBS и без включённых оверлеев.')
    $Panel.Feedback.SetBounds(20,704,820,40); $Panel.Feedback.Anchor='Bottom, Left, Right'
    $footer=New-DashboardLabel $form 'Свернуть: продолжить сбор     /     Закрыть окно: остановить приложение' 20 755 820 24
    $footer.ForeColor=$muted; $footer.Font=[Drawing.Font]::new('Segoe UI',9); $footer.Anchor='Bottom, Left, Right'
    $sessionLabel=New-DashboardLabel $pages.stats '' 20 16 780 30
    $sessionLabel.Font=[Drawing.Font]::new('Georgia',14); $sessionLabel.ForeColor=$gold
    $cards=@{}; $index=0
    foreach ($entry in @(@('kills','УБИЙСТВА'),@('deaths','СМЕРТИ'),@('kd','K/D'),@('games','МАТЧИ'))) {
        $card=[Windows.Forms.Panel]::new(); $card.SetBounds((20+196*$index),58,184,105)
        $card.BackColor=[Drawing.Color]::FromArgb(28,30,27); $card.BorderStyle='FixedSingle'
        $pages.stats.Controls.Add($card)
        $caption=New-DashboardLabel $card $entry[1] 12 12 160 20
        $caption.Font=[Drawing.Font]::new('Segoe UI',9); $caption.ForeColor=$muted
        $number=New-DashboardLabel $card '0' 10 36 164 60
        $number.Font=[Drawing.Font]::new('Georgia',32); $number.ForeColor=if ($entry[0] -eq 'deaths') { [Drawing.Color]::FromArgb(203,124,112) } else { $form.ForeColor }
        $cards[$entry[0]]=$number; $index++
    }
    $summary=New-DashboardLabel $pages.stats '' 20 179 780 52
    $summary.ForeColor=$muted
    $tips.SetToolTip($summary,'Выживаемость рассчитывается только по матчам с оценкой исхода. Размены считаются при включённом распознавании. Неопределённые события не прибавляются к убийствам или смертям.')
    $heading=New-DashboardLabel $pages.stats 'Последний матч' 20 241 780 26
    $heading.Font=[Drawing.Font]::new('Georgia',14); $heading.ForeColor=$gold
    $current=New-DashboardLabel $pages.stats '' 20 276 780 54
    $heading=New-DashboardLabel $pages.stats 'История охоты' 20 342 780 30
    $heading.Font=[Drawing.Font]::new('Georgia',16)
    $history=[Windows.Forms.ListView]::new(); $history.SetBounds(20,381,780,124)
    $history.Anchor='Top, Left, Right'; $history.View='Details'; $history.FullRowSelect=$true; $history.HideSelection=$false
    $history.BackColor=[Drawing.Color]::FromArgb(26,28,25); $history.ForeColor=$form.ForeColor; $history.BorderStyle='FixedSingle'
    $history.OwnerDraw=$true
    $history.Add_DrawColumnHeader({param($sender,$e)
        $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(43,44,37))
        try { $e.Graphics.FillRectangle($brush,$e.Bounds) } finally { $brush.Dispose() }
        [Windows.Forms.TextRenderer]::DrawText($e.Graphics,$e.Header.Text,$sender.Font,$e.Bounds,[Drawing.Color]::FromArgb(219,214,183),[Windows.Forms.TextFormatFlags]::Left -bor [Windows.Forms.TextFormatFlags]::VerticalCenter)
    })
    $history.Add_DrawItem({param($sender,$e) $e.DrawDefault=$true})
    $history.Add_DrawSubItem({param($sender,$e) $e.DrawDefault=$true})
    foreach ($column in @(@('Карта',180),@('Убийства',95),@('Смерти',95),@('Исход (оценка)',260),@('Время',140))) { [void]$history.Columns.Add($column[0],[int]$column[1]) }
    $pages.stats.Controls.Add($history); $history.SetBounds(20,381,780,124)
    $pages.stats.Tag=$history
    $pages.stats.Add_SizeChanged({param($sender,$eventArgs) $sender.Tag.Width=[Math]::Max(780,$sender.ClientSize.Width-40)})
    $history.Add_SizeChanged({param($sender,$eventArgs)
        $sender.Columns[4].Width=[Math]::Max(100,$sender.ClientSize.Width-631)
    })
    $note=New-DashboardLabel $pages.stats 'По данным логов. Убийства, размены и исходы матчей могут определяться с ошибкой.' 20 520 510 44
    $note.ForeColor=$muted; $note.Font=[Drawing.Font]::new('Segoe UI',9)
    $newSession=New-DashboardButton $pages.stats $Panel 'new-session' 'Новая сессия' 560 523
    $tips.SetToolTip($newSession,'Сохраняет текущую сессию в data/sessions и начинает новые счётчики. Закрытие приложения само по себе их не сбрасывает.')
    $options=@{}; $fields=@{}; $numbers=@{}; $textFields=@{}
    $intro=New-DashboardLabel $pages.display 'Выберите, что увидят зрители' 20 16 780 30
    $intro.Font=[Drawing.Font]::new('Georgia',16); $intro.ForeColor=$gold
    foreach ($item in @(@('alerts','Алерты на стриме'),@('stats','Статистика на стриме'))) {
        $check=[Windows.Forms.CheckBox]::new(); $check.FlatStyle='Flat'; $check.FlatAppearance.CheckedBackColor=[Drawing.Color]::FromArgb(81,75,45); $check.Text=$item[1]; $check.Checked=$Config.display.($item[0])
        $check.SetBounds((20+392*$options.Count),55,380,30); $pages.display.Controls.Add($check); $options[$item[0]]=$check
        $tips.SetToolTip($check,'Управляет только видимостью в OBS. Сбор данных и статистика в приложении продолжают работать.')
    }
    $null=New-DashboardLabel $pages.display 'События алерта' 20 96 260
    $mode=[Windows.Forms.ComboBox]::new(); $mode.BackColor=[Drawing.Color]::FromArgb(32,34,30); $mode.ForeColor=$form.ForeColor; $mode.FlatStyle='Flat'; $mode.DropDownStyle='DropDownList'; $mode.SetBounds(20,125,370,30)
    [void]$mode.Items.AddRange(@('Только убийства','Убийства и смерти','Убийства, смерти и размены'))
    $mode.DrawMode='OwnerDrawFixed'; $mode.ItemHeight=23
    $mode.Add_DrawItem({param($sender,$e)
        if ($e.Index -lt 0) { return }
        $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(32,34,30))
        try { $e.Graphics.FillRectangle($brush,$e.Bounds) } finally { $brush.Dispose() }
        [Windows.Forms.TextRenderer]::DrawText($e.Graphics,[string]$sender.Items[$e.Index],$sender.Font,$e.Bounds,$sender.ForeColor,[Windows.Forms.TextFormatFlags]::Left -bor [Windows.Forms.TextFormatFlags]::VerticalCenter)
        $e.DrawFocusRectangle()
    })
    $mode.SelectedIndex=[Array]::IndexOf(@('kills','kills-deaths','trades'),$Config.alertMode)
    $pages.display.Controls.Add($mode)
    $delay=New-DashboardLabel $pages.display '' 412 123 388 36; $delay.ForeColor=$gold
    $mode.Tag=$delay
    $mode.Add_SelectedIndexChanged({param($sender,$eventArgs) $sender.Tag.Text=if ($sender.SelectedIndex -eq 2) { 'Ожидание: 500 мс · размены: оценка' } else { 'Ожидание: 300 мс' }})
    $delay.Text=if ($mode.SelectedIndex -eq 2) { 'Ожидание: 500 мс · размены: оценка' } else { 'Ожидание: 300 мс' }
    $tips.SetToolTip($mode,'Без разменов: 300 мс. С разменами: окно 500 мс для двух событий NVIDIA и одного экрана смерти. Это оценка по времени чтения логов, а не подтверждённый игрой размен. Режим применяется после сохранения.')
    $null=New-DashboardLabel $pages.display 'Поля статистики в OBS' 20 174 780
    $labels=[ordered]@{kills='Убийства';deaths='Смерти';kd='K/D';games='Матчи';survival='Выживаемость (оценка)';maps='Частота карт';current='Последний матч';unknown='Неопределённые события'}
    $descriptions=@{kills='Предполагаемые убийства по событиям NVIDIA без ближайшего экрана смерти.';deaths='События NVIDIA, сопоставленные с экраном смерти.';kd='Убийства / смерти за сессию. При нуле смертей и наличии убийств показана бесконечность.';games='Матчи, начало которых увидел трекер за эту сессию.';survival='Доля предположительно выживших среди матчей с оценкой исхода. Матчи без результата исключаются.';maps='Доля каждой карты среди распознанных карт за сессию.';current='Карта, состояние и счётчики последнего матча.';unknown='События, которые не удалось уверенно сопоставить из-за пропуска логов или неоднозначного порядка.'}
    $index=0
    foreach ($key in $labels.Keys) {
        $check=[Windows.Forms.CheckBox]::new(); $check.FlatStyle='Flat'; $check.FlatAppearance.CheckedBackColor=[Drawing.Color]::FromArgb(81,75,45); $check.Text=$labels[$key]; $check.Checked=$key -in $Config.display.fields
        $check.SetBounds((20+392*($index%2)),(205+28*[Math]::Floor($index/2)),380,28)
        $pages.display.Controls.Add($check); $fields[$key]=$check; $index++
        $tips.SetToolTip($check,$descriptions[$key]+' Скрытие поля не отключает сбор.')
    }
    $numberTips=@{scale='Размер панели статистики в OBS. На черепок и окно приложения не влияет.';opacity='Непрозрачность фона панели статистики. 0: полностью прозрачный фон.';volume='Громкость звука всех алертов. 0: без звука.';durationMs='Длительность анимации черепка. Не меняет задержку распознавания события.'}
    $index=0
    foreach ($spec in @(@('scale','Масштаб, %',50,150,$Config.display.scale),@('opacity','Фон, %',0,100,$Config.display.opacity),@('volume','Громкость, %',0,100,($Config.volume*100)),@('durationMs','Анимация, мс',300,10000,$Config.durationMs))) {
        $x=20+196*$index; $label=New-DashboardLabel $pages.display $spec[1] $x 331 180
        $number=[Windows.Forms.NumericUpDown]::new(); $number.Minimum=$spec[2]; $number.Maximum=$spec[3]; $number.Value=$spec[4]
        $number.BackColor=[Drawing.Color]::FromArgb(32,34,30); $number.ForeColor=$form.ForeColor
        $number.SetBounds($x,361,184,30); $pages.display.Controls.Add($number); $numbers[$spec[0]]=$number; $index++
        $tips.SetToolTip($number,$numberTips[$spec[0]]); $tips.SetToolTip($label,$numberTips[$spec[0]])
    }
    $index=0
    foreach ($entry in @(@('kill','Тест убийства'),@('death','Тест смерти'),@('trade','Тест размена'),@('overlay','Открыть алерт OBS'),@('stats-overlay','Статистика OBS'),@('save','Сохранить настройки'))) {
        $button=New-DashboardButton $pages.display $Panel $entry[0] $entry[1] (20+264*($index%3)) (411+48*[Math]::Floor($index/3)) 252
        $tip=if ($index -lt 3) { 'Отправляет тест в открытый Overlay.html с сохранёнными настройками. Настоящие счётчики не меняются. Оверлей должен быть включён.' } elseif ($entry[0] -eq 'save') { 'Применяет режим алерта, параметры звука и поля OBS. Новые пути требуют перезапуска.' } else { 'Открывает локальный HTML-файл. Для стрима добавьте его в OBS как источник «Браузер» с локальным файлом.' }
        $tips.SetToolTip($button,$tip); $index++
    }
    $Panel.Buttons.save.BackColor=[Drawing.Color]::FromArgb(68,61,35); $Panel.Buttons.save.ForeColor=$gold
    $note=New-DashboardLabel $pages.display 'Настройки применяются после сохранения. Для просмотра своих счётчиков OBS не нужен.' 20 517 780 44
    $note.ForeColor=$muted
    $row=0
    foreach ($name in @('game','gameLog','nvidia','image','sounds','counts')) {
        $label=New-DashboardLabel $pages.connection '' 20 (16+26*$row)
        $Panel.Statuses[$name]=$label; $row++
    }
    $paths=[Windows.Forms.TextBox]::new(); $paths.Multiline=$true; $paths.ReadOnly=$true; $paths.BorderStyle='None'
    $paths.BackColor=$form.BackColor; $paths.ForeColor=$muted; $paths.SetBounds(20,184,780,72)
    $pages.connection.Controls.Add($paths); $paths.TabStop=$false; $Panel.Paths=$paths
    $index=0
    foreach ($spec in @(@('gameFolder','Папка игры или AUTO','AUTO ищет установленную игру в библиотеках Steam. Можно указать папку с USER/Game.log.'),@('nvidiaLogs','Папка логов NVIDIA','Папка GfeSDK с файлами HuntGame_<PID>.log. Переменные окружения в пути поддерживаются.'),@('image','Картинка алерта','Файл внутри Images. Исходный черепок: Images/kill.png.'))) {
        $label=New-DashboardLabel $pages.connection $spec[1] 20 (270+64*$index)
        $input=[Windows.Forms.TextBox]::new(); $input.Text=$Config.($spec[0]); $input.SetBounds(20,(298+64*$index),780,28)
        $input.BackColor=[Drawing.Color]::FromArgb(32,34,30); $input.ForeColor=$form.ForeColor
        $pages.connection.Controls.Add($input); $input.Anchor='Top, Left, Right'; $textFields[$spec[0]]=$input; $index++
        $tips.SetToolTip($input,$spec[2]); $tips.SetToolTip($label,$spec[2])
    }
    $note=New-DashboardLabel $pages.connection 'Новые пути заработают после перезапуска приложения.' 20 478 510 50
    $note.ForeColor=$muted
    $savePaths=New-DashboardButton $pages.connection $Panel 'save-paths' 'Сохранить настройки' 560 479
    $savePaths.Tag.Action='save'; $tips.SetToolTip($savePaths,'Сохраняет все настройки, включая изменения на вкладке оверлея.')
    foreach ($entry in @{Options=$options;Fields=$fields;Numbers=$numbers;TextFields=$textFields;Summary=$summary;SessionLabel=$sessionLabel;History=$history;HistorySignature='';Tabs=$tabs;Cards=$cards;Current=$current;AlertMode=$mode;Tips=$tips}.GetEnumerator()) { $Panel | Add-Member $entry.Key $entry.Value }
}

function Get-ControlPanelConfig($Panel,$Config) {
    $value=$Config | ConvertTo-Json -Depth 6 | ConvertFrom-Json
    foreach ($key in $Panel.Options.Keys) { $value.display.$key=$Panel.Options[$key].Checked }
    $value.display.fields=@('kills','deaths','kd','games','survival','maps','current','unknown' | Where-Object { $Panel.Fields[$_].Checked })
    $value.alertMode=@('kills','kills-deaths','trades')[$Panel.AlertMode.SelectedIndex]
    $value.display.scale=[int]$Panel.Numbers.scale.Value; $value.display.opacity=[int]$Panel.Numbers.opacity.Value
    $value.volume=[double]$Panel.Numbers.volume.Value/100; $value.durationMs=[int]$Panel.Numbers.durationMs.Value
    foreach ($key in $Panel.TextFields.Keys) { $value.$key=$Panel.TextFields[$key].Text.Trim() }
    return $value
}

function Update-Dashboard($Panel,$Stats) {
    $kd=if ($Stats.deaths) { ($Stats.kills/$Stats.deaths).ToString('0.00') } elseif ($Stats.kills) { '∞' } else { '0.00' }
    $known=$Stats.survived+$Stats.lost
    $survival=if ($known) { [Math]::Round(100*$Stats.survived/$known,0,[MidpointRounding]::AwayFromZero).ToString()+'%' } else { 'нет данных' }
    foreach ($key in @('kills','deaths','games')) { $Panel.Cards[$key].Text=[string]$Stats.$key }
    $Panel.Cards.kd.Text=$kd
    $Panel.Current.Text=if ($Stats.current) {
        $names=@{cemetery='Stillwater Bayou';civilwar='Lawson Delta';creek='DeSalle';colorado='Mammon Gulch'}
        $map=if ($names.ContainsKey([string]$Stats.current.map)) { $names[$Stats.current.map] } else { 'Карта неизвестна' }
        $phase=@{loading='Загрузка';active='В игре';complete='Завершён'}[$Stats.current.status]
        "$map  /  $phase`r`nУбийства: $($Stats.current.kills)     Смерти: $($Stats.current.deaths)"
    } else { 'Ожидание первого матча' }
    $Panel.Summary.Text="Выживаемость: $survival  ·  Выжили: $($Stats.survived)  ·  Не выжили: $($Stats.lost)  ·  Без результата: $($Stats.unresolved)`r`nРазмены (оценка): $($Stats.trades)  ·  Неопределённые события: $($Stats.unknown)"
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
