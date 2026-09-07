<#
═══════════════════════════════════════════════════════════════════════
 DateERP — إنشاء الاختصارات (سطح المكتب + قائمة ابدأ)
 يُستدعى تلقائياً من "2-تنصيب.bat"، ويمكن تشغيله يدوياً.
═══════════════════════════════════════════════════════════════════════
#>
param(
    [string]$Target  = "$env:ProgramFiles\DateERP\DateERP.exe",
    [string]$WorkDir = "$env:ProgramFiles\DateERP",
    [string]$AppName = "DateERP"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Target)) {
    Write-Host "[خطأ] لم يُعثر على الملف: $Target" -ForegroundColor Red
    exit 1
}

try {
    $shell = New-Object -ComObject WScript.Shell

    # ── 1) سطح المكتب ──
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = $shell.CreateShortcut((Join-Path $desktop "$AppName.lnk"))
    $lnk.TargetPath       = $Target
    $lnk.WorkingDirectory = $WorkDir
    $lnk.IconLocation     = "$Target,0"
    $lnk.Description      = "DateERP — نظام إدارة وتصنيع التمور"
    $lnk.Save()

    # ── 2) قائمة ابدأ ──
    $startMenu = [Environment]::GetFolderPath('Programs')
    $folder = Join-Path $startMenu $AppName
    if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }

    $lnk2 = $shell.CreateShortcut((Join-Path $folder "$AppName.lnk"))
    $lnk2.TargetPath       = $Target
    $lnk2.WorkingDirectory = $WorkDir
    $lnk2.IconLocation     = "$Target,0"
    $lnk2.Description      = "DateERP — نظام إدارة وتصنيع التمور"
    $lnk2.Save()

    # اختصار إلغاء التنصيب داخل مجلد قائمة ابدأ
    $uninst = Join-Path $WorkDir 'إلغاء_التنصيب.bat'
    if (Test-Path $uninst) {
        $lnk3 = $shell.CreateShortcut((Join-Path $folder "إلغاء تنصيب $AppName.lnk"))
        $lnk3.TargetPath       = $uninst
        $lnk3.WorkingDirectory = $WorkDir
        $lnk3.Save()
    }

    Write-Host "تم إنشاء الاختصارات بنجاح." -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "[خطأ] تعذر إنشاء الاختصارات: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
