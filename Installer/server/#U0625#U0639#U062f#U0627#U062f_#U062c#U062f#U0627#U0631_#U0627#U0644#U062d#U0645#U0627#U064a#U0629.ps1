<#
═══════════════════════════════════════════════════════════════════════
 DateERP — إعداد جدار الحماية على الخادم
 يُشغَّل مرة واحدة على جهاز الخادم، بصلاحيات مدير.

 الطريقة:
   انقر الملف بزر الفأرة الأيمن ← "تشغيل PowerShell كمسؤول"
   أو من موجه أوامر كمسؤول:
     powershell -ExecutionPolicy Bypass -File إعداد_جدار_الحماية.ps1
═══════════════════════════════════════════════════════════════════════
#>

$ErrorActionPreference = 'Stop'

# التحقق من صلاحيات المدير
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "[خطأ] يجب تشغيل هذا الملف كمسؤول." -ForegroundColor Red
    Write-Host "        انقره بزر الفأرة الأيمن واختر `"تشغيل PowerShell كمسؤول`"."
    exit 1
}

Write-Host ""
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  DateERP — إعداد جدار الحماية على الخادم" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

function Add-Rule {
    param([string]$Name, [string]$Protocol, [int]$Port, [string]$Direction = 'Inbound')
    $existing = Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "  [=] $Name موجود مسبقاً" -ForegroundColor DarkGray
        return
    }
    New-NetFirewallRule -DisplayName $Name `
        -Direction $Direction -Action Allow `
        -Protocol $Protocol -LocalPort $Port `
        -Profile Domain,Private | Out-Null
    Write-Host "  [+] $Name  ($Protocol/$Port)" -ForegroundColor Green
}

Write-Host "→ إضافة قواعد جدار الحماية..."
Add-Rule -Name 'DateERP - SQL Server TCP'        -Protocol TCP -Port 1433
Add-Rule -Name 'DateERP - SQL Server DAC'        -Protocol TCP -Port 1434
Add-Rule -Name 'DateERP - SQL Browser UDP'       -Protocol UDP -Port 1434
Write-Host ""

# ── إظهار القواعد ──
Write-Host "→ القواعد الحالية:"
Get-NetFirewallRule -DisplayName 'DateERP*' |
    Select-Object DisplayName, Direction, Action, Enabled, Profile |
    Format-Table -AutoSize | Out-Host

Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  ✓ تم إعداد جدار الحماية" -ForegroundColor Green
Write-Host ""
Write-Host "  إن كنت تستخدم Named Instance (مثل SERVER01\SQLEXPRESS):"
Write-Host "    • فعّل بروتوكول TCP/IP من SQL Server Configuration Manager"
Write-Host "    • ثبّت المنفذ على 1433 أو استخدم SQL Browser (UDP 1434)"
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
