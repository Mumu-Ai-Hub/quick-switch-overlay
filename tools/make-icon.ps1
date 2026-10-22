# 生成应用图标 Assets/app.ico（构建前置步骤）。
# 仓库内不携带二进制图标；首次构建前运行本脚本，或放入自定义 app.ico 后跳过。
$ErrorActionPreference = "Stop"

$ProjectDir = Split-Path -Parent $PSScriptRoot
$AssetsDir = Join-Path $ProjectDir "src\QuickAppsOverlay\Assets"
$IconPath = Join-Path $AssetsDir "app.ico"

if (Test-Path $IconPath) {
    Write-Host "已存在图标，跳过：$IconPath"
    return
}

New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null

Add-Type -AssemblyName System.Drawing

$Size = 32
$Bitmap = New-Object System.Drawing.Bitmap $Size, $Size
$Graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
$Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$Graphics.Clear([System.Drawing.Color]::Transparent)

$BackBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 38, 50, 56))
$Rect = New-Object System.Drawing.Rectangle 0, 0, $Size, $Size
$Graphics.FillRectangle($BackBrush, $Rect)

$ForePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 240, 240, 235)), 3
$ForePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$ForePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

# 上下双向箭头：表示在窗口之间来回切换
$Graphics.DrawLine($ForePen, 16, 6, 16, 26)
$Graphics.DrawLine($ForePen, 10, 12, 16, 6)
$Graphics.DrawLine($ForePen, 22, 12, 16, 6)
$Graphics.DrawLine($ForePen, 10, 20, 16, 26)
$Graphics.DrawLine($ForePen, 22, 20, 16, 26)

$Graphics.Dispose()
$BackBrush.Dispose()
$ForePen.Dispose()

$HIcon = $Bitmap.GetHicon()
$Icon = [System.Drawing.Icon]::FromHandle($HIcon)

$Stream = [System.IO.File]::Create($IconPath)
$Icon.Save($Stream)
$Stream.Close()
$Icon.Dispose()
$Bitmap.Dispose()

Write-Host "图标已生成：$IconPath"
