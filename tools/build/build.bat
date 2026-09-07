@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
cd /d "%~dp0..\.."

set "LOG=%~dp0سجل_البناء_الكامل.txt"
set "OUT=%~dp0أرسل_هذا_الملف.txt"

echo.
echo ══════════════════════════════════════════════════════════════
echo    DateERP — بناء واختبار وتجميع الأخطاء
echo ══════════════════════════════════════════════════════════════
echo.
echo    المجلد: %CD%
echo.

REM ─────────────────────────────────────────────────────────────
REM  0) التحقق من وجود .NET 8 SDK
REM ─────────────────────────────────────────────────────────────
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [X] .NET SDK غير مثبّت على هذا الجهاز.
    echo     نزّله من: https://dotnet.microsoft.com/download/dotnet/8.0
    echo     اختر: .NET 8.0 SDK — Windows x64
    echo.
    pause
    exit /b 1
)
for /f "delims=" %%v in ('dotnet --version 2^>nul') do set "SDKVER=%%v"
echo    [1/4] .NET SDK: !SDKVER!

if not exist "DateERP.sln" (
    echo.
    echo [X] لم يُعثر على DateERP.sln في: %CD%
    echo     ضع هذا السكربت داخل مجلد المشروع في tools\build\
    echo.
    pause
    exit /b 1
)

REM ─────────────────────────────────────────────────────────────
REM  1) استعادة الحزم
REM ─────────────────────────────────────────────────────────────
echo    [2/4] استعادة الحزم... (قد تستغرق دقائق في أول مرة)
echo ===== RESTORE ===== > "%LOG%"
dotnet restore DateERP.sln >> "%LOG%" 2>&1

REM ─────────────────────────────────────────────────────────────
REM  2) البناء
REM ─────────────────────────────────────────────────────────────
echo    [3/4] البناء (Release)...
echo. >> "%LOG%"
echo ===== BUILD ===== >> "%LOG%"
dotnet build DateERP.sln -c Release --no-restore >> "%LOG%" 2>&1
set "BUILDRC=!errorlevel!"

REM ─────────────────────────────────────────────────────────────
REM  3) الاختبارات — فقط إن نجح البناء
REM ─────────────────────────────────────────────────────────────
if "!BUILDRC!"=="0" (
    echo    [4/4] البناء نجح ✓ — تشغيل الاختبارات...
    echo. >> "%LOG%"
    echo ===== TESTS ===== >> "%LOG%"
    dotnet test tests\DatesErp.Tests\DatesErp.Tests.csproj -c Release --no-build >> "%LOG%" 2>&1
) else (
    echo    [4/4] البناء فشل — تُخطّى الاختبارات.
)

REM ─────────────────────────────────────────────────────────────
REM  4) تجميع ملف موجز يُرسل
REM ─────────────────────────────────────────────────────────────
> "%OUT%" echo DateERP — نتيجة البناء
>> "%OUT%" echo التاريخ: %DATE% %TIME%
>> "%OUT%" echo .NET SDK: !SDKVER!
>> "%OUT%" echo رمز خروج البناء: !BUILDRC!
>> "%OUT%" echo.
>> "%OUT%" echo ==================== الأخطاء ====================
findstr /C:": error " "%LOG%" >> "%OUT%" 2>nul
>> "%OUT%" echo.
>> "%OUT%" echo ================ ملخص البناء/الاختبار ================
findstr /C:"Build succeeded" /C:"Build FAILED" /C:"Warning(s)" /C:"Error(s)" /C:"Passed!" /C:"Failed!" /C:"Passed:" /C:"Failed:" /C:"Total tests" "%LOG%" >> "%OUT%" 2>nul
>> "%OUT%" echo.
>> "%OUT%" echo ================ الاختبارات الفاشلة ================
findstr /C:"[FAIL]" /C:"Assert." "%LOG%" >> "%OUT%" 2>nul

echo.
echo ══════════════════════════════════════════════════════════════
if "!BUILDRC!"=="0" (
    echo    ✓ البناء نجح
) else (
    echo    ✗ البناء فشل — الأخطاء مجمّعة في الملف أدناه
)
echo.
echo    📤 أرسل هذا الملف:
echo       %OUT%
echo.
echo    (السجل الكامل — إن طُلب لاحقاً — في: %LOG%)
echo ══════════════════════════════════════════════════════════════
echo.

REM فتح الملف تلقائياً ليسهل نسخه
start "" notepad "%OUT%"
pause
