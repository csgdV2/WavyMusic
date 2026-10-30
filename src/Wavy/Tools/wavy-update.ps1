param(
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [Parameter(Mandatory=$true)][string]$ExpectedSha256,
    [Parameter(Mandatory=$true)][long]$ExpectedSize,
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$readyFile = Join-Path $WorkDirectory 'ready'
$errorFile = Join-Path $WorkDirectory 'error.txt'
$progressFile = Join-Path $WorkDirectory 'install-progress.txt'
$logFile = Join-Path $WorkDirectory 'install.log'
function Get-UpdateWaitError([double]$ElapsedSeconds, [double]$IdleSeconds) {
    if ($ElapsedSeconds -ge 900) { return 'Installation is taking longer than expected. It may still be running.' }
    if ($IdleSeconds -ge 300) { return 'Installer progress has stopped. It may still be running.' }
    return $null
}
function Remove-CompletedInstaller {
    try {
        $cacheFolder = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $WorkDirectory))).TrimEnd('\')
        $installerFolder = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($InstallerPath)).TrimEnd('\')
        $name = [IO.Path]::GetFileName($InstallerPath)
        if ([string]::Equals($cacheFolder, $installerFolder, [StringComparison]::OrdinalIgnoreCase) -and $name -match '^Wavy-\d+\.\d+(?:\.\d+){0,2}-setup\.exe$') {
            if (Test-Path -LiteralPath $InstallerPath -PathType Leaf) {
                Remove-Item -LiteralPath $InstallerPath -Force -ErrorAction Stop
            }
        }
    }
    catch { }
}
try {
    $installerFile = Get-Item -LiteralPath $InstallerPath
    if ($installerFile.Length -ne $ExpectedSize -or (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash -ne $ExpectedSha256) {
        throw 'The update installer failed verification. Download it again.'
    }
    $InstallerPath = $installerFile.FullName
    $InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [Windows.Forms.Application]::EnableVisualStyles()
    $light = $false
    try { $light = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize').AppsUseLightTheme -eq 1 } catch { }
    $back = if ($light) { [Drawing.Color]::FromArgb(245,245,245) } else { [Drawing.Color]::FromArgb(26,26,29) }
    $text = if ($light) { [Drawing.Color]::FromArgb(30,30,30) } else { [Drawing.Color]::FromArgb(240,240,240) }
    $muted = if ($light) { [Drawing.Color]::FromArgb(105,105,105) } else { [Drawing.Color]::FromArgb(160,160,160) }
    $form = New-Object Windows.Forms.Form
    $form.Text = 'Updating Wavy'
    $form.FormBorderStyle = 'None'
    $form.Size = New-Object Drawing.Size(340,108)
    $form.StartPosition = 'Manual'
    $area = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $form.Location = New-Object Drawing.Point(($area.Right - 360),($area.Top + 20))
    $form.ShowInTaskbar = $false
    $form.TopMost = $true
    $form.BackColor = $back
    $form.ForeColor = $text
    $form.Font = New-Object Drawing.Font('Segoe UI',10)
    $title = New-Object Windows.Forms.Label
    $title.Text = 'Updating Wavy'
    $title.SetBounds(18,14,250,22)
    $title.Font = New-Object Drawing.Font('Segoe UI',11,[Drawing.FontStyle]::Bold)
    $status = New-Object Windows.Forms.Label
    $status.Text = 'Preparing installation…'
    $status.ForeColor = $muted
    $status.SetBounds(18,39,304,24)
    $bar = New-Object Windows.Forms.Panel
    $bar.SetBounds(18,78,304,4)
    $bar.BackColor = if ($light) { [Drawing.Color]::FromArgb(215,215,215) } else { [Drawing.Color]::FromArgb(65,65,65) }
    $fill = New-Object Windows.Forms.Panel
    $fill.BackColor = $text
    $fill.SetBounds(0,0,60,4)
    $bar.Controls.Add($fill)
    $close = New-Object Windows.Forms.Button
    $close.Text = '×'
    $close.SetBounds(298,10,28,26)
    $close.FlatStyle = 'Flat'
    $close.FlatAppearance.BorderSize = 0
    $close.ForeColor = $muted
    $close.TabStop = $false
    $close.Visible = $true
    $close.Add_Click({
        if ($script:failed -or $script:finishedAt) { $form.Close() }
        else { $form.Hide() }
    })
    $form.Controls.AddRange(@($title,$status,$bar,$close))
    $script:installerProcess = $null
    $script:finishedAt = $null
    $script:failed = $false
    $script:marquee = 0
    $script:failedAt = $null
    $script:installationWatch = [Diagnostics.Stopwatch]::new()
    $script:lastProgressSeconds = 0.0
    $script:lastPercentage = -1
    $form.Add_FormClosing({
        param($sender, $eventArgs)
        if ($eventArgs.CloseReason -eq [Windows.Forms.CloseReason]::UserClosing -and -not $script:failed -and -not $script:finishedAt) {
            $eventArgs.Cancel = $true
            $form.Hide()
        }
    })
    $timer = New-Object Windows.Forms.Timer
    $timer.Interval = 150
    $timer.Add_Tick({
        try {
            if ($script:failed) {
                if (([DateTime]::UtcNow - $script:failedAt).TotalSeconds -ge 30) { $timer.Stop(); $form.Close() }
                return
            }
            if ($script:finishedAt) {
                Remove-CompletedInstaller
                if (([DateTime]::UtcNow - $script:finishedAt).TotalSeconds -ge 1) { $timer.Stop(); $form.Close() }
                return
            }
            $percentage = -1
            if (Test-Path -LiteralPath $progressFile) {
                $raw = Get-Content -LiteralPath $progressFile -Raw -ErrorAction SilentlyContinue
                $parsed = 0
                if ($null -ne $raw -and [int]::TryParse($raw.Trim(), [ref]$parsed)) { $percentage = [Math]::Max(0,[Math]::Min(99,$parsed)) }
            }
            if ($percentage -ge 0) {
                if ($percentage -ne $script:lastPercentage) {
                    $script:lastPercentage = $percentage
                    $script:lastProgressSeconds = $script:installationWatch.Elapsed.TotalSeconds
                }
                $fill.Left = 0
                $fill.Width = [int]($bar.Width * $percentage / 100)
                $status.Text = "Installing… $percentage%"
            }
            else {
                $script:marquee = ($script:marquee + 14) % ($bar.Width + 60)
                $fill.Width = 60
                $fill.Left = $script:marquee - 60
            }
            if ($script:installerProcess -and $script:installerProcess.HasExited) {
                $script:installerProcess.WaitForExit()
                if ($script:installerProcess.ExitCode -ne 0 -and $script:installerProcess.ExitCode -ne 3010) {
                    throw "Installation failed (exit code $($script:installerProcess.ExitCode)). Log: $logFile"
                }
                $fill.Left = 0
                $fill.Width = $bar.Width
                $status.Text = 'Installed. Restarting Wavy…'
                Remove-CompletedInstaller
                $script:finishedAt = [DateTime]::UtcNow
            }
            else {
                $waitError = Get-UpdateWaitError $script:installationWatch.Elapsed.TotalSeconds ($script:installationWatch.Elapsed.TotalSeconds - $script:lastProgressSeconds)
                if ($waitError) { throw $waitError }
            }
        }
        catch {
            $script:failed = $true
            $script:failedAt = [DateTime]::UtcNow
            $form.TopMost = $false
            $title.Text = 'Wavy update needs attention'
            $status.Text = 'You can dismiss this notice. Check Wavy before trying another update.'
            $status.SetBounds(18,39,304,58)
            $form.Height = 148
            $bar.Visible = $false
            $close.Visible = $true
            try { $_.Exception.Message | Set-Content -LiteralPath $errorFile } catch { }
        }
    })
    $form.Add_Shown({
        try {
            $arguments = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /WAVYUPDATE=1 /DIR="' + $InstallDirectory + '" /WAVYPROGRESS="' + $progressFile + '" /LOG="' + $logFile + '"'
            $script:installationWatch.Start()
            $script:installerProcess = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
            'started' | Set-Content -LiteralPath $readyFile
            $timer.Start()
        }
        catch { $_.Exception.Message | Set-Content -LiteralPath $errorFile; $form.Close() }
    })
    if ($ValidateOnly) {
        $closingMethod = $form.GetType().GetMethod('OnFormClosing', [Reflection.BindingFlags]'Instance,NonPublic')
        $activeClose = New-Object Windows.Forms.FormClosingEventArgs([Windows.Forms.CloseReason]::UserClosing, $false)
        $closingMethod.Invoke($form, @($activeClose.PSObject.BaseObject))
        if (-not $activeClose.Cancel) { throw 'Dismissing the progress notice would stop monitoring an active install.' }
        $script:failed = $true
        $failedClose = New-Object Windows.Forms.FormClosingEventArgs([Windows.Forms.CloseReason]::UserClosing, $false)
        $closingMethod.Invoke($form, @($failedClose.PSObject.BaseObject))
        if ($failedClose.Cancel) { throw 'The error notice could not be dismissed.' }
        $timer.Dispose()
        $form.Dispose()
        Write-Output 'Update helper validated without starting installation.'
        exit 0
    }
    [Windows.Forms.Application]::Run($form)
    $timer.Dispose()
    $form.Dispose()
}
catch {
    $_.Exception.Message | Set-Content -LiteralPath $errorFile
    exit 1
}
