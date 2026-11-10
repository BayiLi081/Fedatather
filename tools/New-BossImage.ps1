# Original, fictional report graphic. No external images, publications, or user data.
param([string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\boss_public.jpg'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap(1000, 1400)
$g = [System.Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)
$ink = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(32, 48, 64))
$muted = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(100, 116, 139))
$blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(37, 99, 235))
$title = New-Object System.Drawing.Font('Segoe UI', 34, ([System.Drawing.FontStyle]::Bold))
$heading = New-Object System.Drawing.Font('Segoe UI', 19, ([System.Drawing.FontStyle]::Bold))
$body = New-Object System.Drawing.Font('Segoe UI', 14)
$small = New-Object System.Drawing.Font('Segoe UI', 11)
$g.FillRectangle($blue, 70, 65, 58, 7)
$g.DrawString('QUARTERLY OVERVIEW', $small, $muted, 70, 92)
$g.DrawString('Project progress report', $title, $ink, 65, 125)
$g.DrawString('Planning, delivery, and next steps', $body, $muted, 70, 195)
$g.DrawString('01    Executive overview', $heading, $ink, 70, 280)
$g.DrawString("The team continues to make steady progress across the current work plan.`nThis report brings together delivery milestones and priorities for the next quarter.`nAll figures on this page are fictional demonstration data.", $body, $muted, 70, 330)
$g.DrawString('02    Delivery by month', $heading, $ink, 70, 465)
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(226, 232, 240), 1)
for ($i = 0; $i -le 4; $i++) { $g.DrawLine($pen, 100, (570 + $i * 65), 900, (570 + $i * 65)) }
$labels = 'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun'
$values = 90, 125, 160, 185, 210, 245
for ($i = 0; $i -lt 6; $i++) {
    $x = 135 + $i * 125
    $g.FillRectangle($blue, $x, (830 - $values[$i]), 65, $values[$i])
    $g.DrawString($labels[$i], $small, $muted, $x + 10, 845)
}
$g.DrawString('03    Priorities for the next quarter', $heading, $ink, 70, 970)
$g.DrawString("1. Review completed milestones and collect feedback.`n`n2. Refine the work plan and assign clear ownership.`n`n3. Track outcomes and share a concise progress update.", $body, $muted, 70, 1025)
$g.DrawLine($pen, 70, 1290, 930, 1290)
$g.DrawString('FEDATATHER  /  ORIGINAL DEMO REPORT', $small, $muted, 70, 1310)
$g.DrawString('01', $small, $muted, 900, 1310)
$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Jpeg)
$g.Dispose(); $bitmap.Dispose()
foreach ($item in @($ink, $muted, $blue, $pen, $title, $heading, $body, $small)) { $item.Dispose() }
