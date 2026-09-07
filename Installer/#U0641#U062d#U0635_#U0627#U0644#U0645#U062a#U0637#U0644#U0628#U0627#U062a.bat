@echo off
REM ═══════════════════════════════════════════════════════════════════════
REM  DateERP — فحص متطلبات الجهاز قبل التنصيب
REM ═══════════════════════════════════════════════════════════════════════
chcp 65001 >nul
setlocal EnableDelayedExpansion

echo.
echo ══════════════════════════════════════════════════════════════
echo    فحص متطلبات تشغيل DateERP
echo ══════════════════════════════════════════════════════════════
echo.

set "OK=1"

REM ── 1) نظام التشغيل ──
echo [1/6] نظام التشغيل
ver
ver | findstr /i "10.0 11.0" >nul
if errorlevel 1 (
    echo        [تحذير] يُنصح بويندوز 10 أو 11.
) else (
    echo        [✓] مقبول
)
echo.

REM ── 2) المعمارية ──
echo [2/6] المعمارية
echo        PROCESSOR_ARCHITECTURE = %PROCESSOR_ARCHITECTURE%
if /i "%PROCESSOR_ARCHITECTURE%"=="AMD64" (
    echo        [✓] 64 بت — متوافق مع win-x64
) else (
    echo        [تحذير] الحزمة مبنية لـ win-x64.
    set "OK=0"
)
echo.

REM ── 3) ذاكرة ──
echo [3/6] الذاكرة
for /f "skip=1 tokens=2 delims==" %%A in ('wmic ComputerSystem get TotalPhysicalMemory /value 2^>nul') do set "RAM=%%A"
if defined RAM (
    set /a RAMGB=!RAM:~0,-9!
    echo        الذاكرة الكلية ≈ !RAMGB! جيجابايت
    if !RAMGB! LSS 4 echo        [تحذير] يُنصح بـ 4 جيجابايت على الأقل.
)
echo.

REM ── 4) مساحة القرص ──
echo [4/6] مساحة القرص
for /f "tokens=3" %%A in ('dir "%SystemDrive%\" ^| findstr /i "bytes free"') do echo        المتاح على %SystemDrive%: %%A بايت
echo.

REM ── 5) .NET (غير مطلوب للحزمة المكتفية ذاتياً) ──
echo [5/6] بيئة .NET
where dotnet >nul 2>&1
if errorlevel 1 (
    echo        [✓] غير مثبّت — ولا حاجة له: الحزمة مكتفية ذاتياً.
) else (
    echo        [✓] موجود ^(غير مطلوب^):
    dotnet --version
)
echo.

REM ── 6) الاتصال بالخادم (إن كان الوضع شبكياً) ──
echo [6/6] الاتصال بقاعدة البيانات
set "CFG=%LocalAppData%\DateERP\config.json"
if exist "%CFG%" (
    echo        ملف الإعداد موجود: %CFG%
    powershell -NoProfile -Command ^
      "try { $c = Get-Content '%CFG%' -Raw | ConvertFrom-Json; Write-Host ('        الخادم : ' + $c.Server); Write-Host ('        القاعدة: ' + $c.Database); Write-Host ('        الوضع  : ' + $c.AuthMode) } catch { Write-Host '        [تحذير] تعذر قراءة ملف الإعداد.' }"
    echo.
    echo        اختبار المنفذ 1433...
    powershell -NoProfile -Command ^
      "try { $c = Get-Content '%CFG%' -Raw | ConvertFrom-Json; $h = ($c.Server -split '\\')[0] -split ','; $t = New-Object Net.Sockets.TcpClient; $r = $t.BeginConnect($h[0], 1433, $null, $null); if ($r.AsyncWaitHandle.WaitOne(4000)) { Write-Host '        [✓] المنفذ 1433 مفتوح' -ForegroundColor Green } else { Write-Host '        [✗] لا استجابة على المنفذ 1433' -ForegroundColor Red } } catch { Write-Host ('        [✗] ' + $_.Exception.Message) -ForegroundColor Red }"
) else (
    echo        لا يوجد ملف إعداد بعد — سيعمل البرنامج في الوضع المحلي
    echo        عند أول تشغيل، ويمكنك ضبط الخادم من شاشة "معلومات النظام".
)
echo.

echo ══════════════════════════════════════════════════════════════
if "%OK%"=="1" (
    echo    ✓ الجهاز مستعد للتنصيب — شغّل "2-تنصيب.bat"
) else (
    echo    ⚠ راجع التحذيرات أعلاه قبل التنصيب
)
echo ══════════════════════════════════════════════════════════════
echo.
pause
endlocal
