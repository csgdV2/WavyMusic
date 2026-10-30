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
    $close.Text = 'Close'
    $close.SetBounds(244,104,78,28)
    $close.Visible = $false
    $close.Add_Click({ $form.Close() })
    $form.Controls.AddRange(@($title,$status,$bar,$close))
    $script:installerProcess = $null
    $script:finishedAt = $null
    $script:failed = $false
    $script:marquee = 0
    $timer = New-Object Windows.Forms.Timer
    $timer.Interval = 150
    $timer.Add_Tick({
        try {
            if ($script:failed) { return }
            if ($script:finishedAt) {
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
                $script:finishedAt = [DateTime]::UtcNow
            }
        }
        catch {
            $script:failed = $true
            $title.Text = 'Wavy update could not finish'
            $status.Text = "Reinstall Wavy to try again. Log: $logFile"
            $status.SetBounds(18,39,304,58)
            $form.Height = 148
            $bar.Visible = $false
            $close.Visible = $true
            $_.Exception.Message | Set-Content -LiteralPath $errorFile
        }
    })
    $form.Add_Shown({
        try {
            $arguments = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /WAVYUPDATE=1 /DIR="' + $InstallDirectory + '" /WAVYPROGRESS="' + $progressFile + '" /LOG="' + $logFile + '"'
            $script:installerProcess = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
            'started' | Set-Content -LiteralPath $readyFile
            $timer.Start()
        }
        catch { $_.Exception.Message | Set-Content -LiteralPath $errorFile; $form.Close() }
    })
    if ($ValidateOnly) {
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
