@echo off
REM ═══════════════════════════════════════════════════════════════════════
REM  DateERP — تنصيب الحزمة المصغّرة (25 ميجابايت)
REM  تتطلب .NET Desktop Runtime 8 — يتحقق منه هذا الملف ويوجّهك لتثبيته.
REM ═══════════════════════════════════════════════════════════════════════
chcp 65001 >nul
setlocal EnableDelayedExpansion

set "SRC=%~dp0"
set "APPNAME=DateERP"
set "DEST=%ProgramFiles%\%APPNAME%"
set "DATADIR=%LocalAppData%\%APPNAME%"
set "RUNTIME_URL=https://dotnet.microsoft.com/download/dotnet/8.0"

echo.
echo ══════════════════════════════════════════════════════════════
echo    تنصيب %APPNAME% — الحزمة المصغّرة
echo ══════════════════════════════════════════════════════════════
echo.

REM ── 1) فحص .NET Desktop Runtime 8 ──
echo [1/6] فحص .NET Desktop Runtime 8...
set "HASRT=0"
dotnet --list-runtimes 2>nul | findstr /i "Microsoft.WindowsDesktop.App 8." >nul 2>&1
if not errorlevel 1 set "HASRT=1"

if "!HASRT!"=="0" (
    echo.
    echo    [مطلوب] لم يُعثر على .NET Desktop Runtime 8.
    echo.
    echo    هذه الحزمة مصغّرة ^(25 ميجابايت^) لأنها لا تحتوي بيئة .NET،
    echo    فتحتاج تثبيتها مرة واحدة على كل جهاز.
    echo.
    echo    الرابط: %RUNTIME_URL%
    echo    اختر: ".NET Desktop Runtime 8.0.x" ← "Windows x64 Installer"
    echo.
    echo    بديل: استخدم الحزمة الكاملة ^(170 ميجابايت^) المكتفية ذاتياً
    echo          ولا تحتاج أي تثبيت.
    echo.
    choice /c YN /m "    فتح رابط التحميل الآن؟ (Y=نعم N=إلغاء التنصيب)"
    if errorlevel 2 ( echo    أُلغي التنصيب. & pause & exit /b 0 )
    start "" "%RUNTIME_URL%"
    echo.
    echo    بعد تثبيت .NET Desktop Runtime، أعد تشغيل هذا الملف.
    echo.
    pause
    exit /b 0
)
for /f "tokens=*" %%V in ('dotnet --list-runtimes 2^>nul ^| findstr /i "Microsoft.WindowsDesktop.App 8."') do echo        [✓] %%V
echo.

REM ── 2) التحقق من سلامة الحزمة ──
echo [2/6] التحقق من سلامة الحزمة...
if not exist "%SRC%DateERP.exe" (
    echo        [خطأ] لم يُعثر على DateERP.exe — انسخ المجلد كاملاً.
    pause & exit /b 1
)
if exist "%SRC%SHA256.txt" (
    for /f "delims=" %%H in (%SRC%SHA256.txt) do set "EXPECT=%%H"
    for /f "delims=" %%H in ('powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 '%SRC%DateERP.exe').Hash"') do set "ACTUAL=%%H"
    if /i "!EXPECT!"=="!ACTUAL!" ( echo        البصمة مطابقة: !ACTUAL! ) else (
        echo        [خطأ] البصمة غير مطابقة — الحزمة تالفة.
        echo                المتوقع: !EXPECT!
        echo                الفعلي  : !ACTUAL!
        pause & exit /b 1
    )
) else ( echo        [تنبيه] لا يوجد SHA256.txt — تخطّي التحقق. )
echo.

REM ── 3) حماية البيانات ──
echo [3/6] حماية البيانات القائمة...
if not exist "%DATADIR%" mkdir "%DATADIR%"
if exist "%DATADIR%\config.json" (
    copy /y "%DATADIR%\config.json" "%DATADIR%\config.backup.json" >nul
    echo        نسخة احتياطية من config.json
)
if exist "%DATADIR%\dateerp_local.db" (
    copy /y "%DATADIR%\dateerp_local.db" "%DATADIR%\dateerp_local.backup.db" >nul
    echo        نسخة احتياطية من قاعدة البيانات المحلية
)
echo.

REM ── 4) كشف التنصيبات القديمة ──
echo [4/6] البحث عن تنصيبات قديمة...
for %%P in ("%ProgramFiles%\%APPNAME%" "%ProgramFiles(x86)%\%APPNAME%" "%LocalAppData%\Programs\%APPNAME%") do (
    if exist "%%~P\DateERP.exe" echo        [تنبيه] تنصيب سابق في: %%~P
)
echo.

REM ── 5) النسخ ──
echo [5/6] نسخ الملفات...
if exist "%DEST%" rmdir /s /q "%DEST%"
mkdir "%DEST%"
xcopy /e /i /y /q "%SRC%*.*" "%DEST%\" >nul
if errorlevel 1 (
    echo        [خطأ] فشل النسخ — انقر الملف بزر الفأرة الأيمن واختر "تشغيل كمسؤول".
    pause & exit /b 1
)
echo        تم النسخ إلى %DEST%
echo.

REM ── 6) الاختصارات ──
echo [6/6] إنشاء الاختصارات...
powershell -NoProfile -ExecutionPolicy Bypass -File "%DEST%\إنشاء_اختصار.ps1" -Target "%DEST%\DateERP.exe" -WorkDir "%DEST%"
if errorlevel 1 ( echo        [تنبيه] أنشئ اختصاراً يدوياً إلى: %DEST%\DateERP.exe ) else (
    echo        اختصار سطح المكتب  ✓
    echo        اختصار قائمة ابدأ   ✓
)
echo.

echo ══════════════════════════════════════════════════════════════
echo    ✓ تم التنصيب بنجاح
echo.
echo    التشغيل   : اختصار "%APPNAME%" على سطح المكتب
echo    الإعدادات : %DATADIR%\config.json
echo    إلغاء     : "%DEST%\إلغاء_التنصيب.bat"
echo.
echo    عند أول تشغيل تُنشأ قاعدة البيانات والجداول تلقائياً.
echo    تحقّق من شريط العنوان: يجب أن يظهر ختم الإصدار.
echo ══════════════════════════════════════════════════════════════
echo.
choice /c YN /m "تشغيل البرنامج الآن؟ (Y=نعم N=لاحقاً)"
if errorlevel 2 goto :end
start "" "%DEST%\DateERP.exe"
:end
echo.
pause
endlocal
