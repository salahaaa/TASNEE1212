@echo off
REM ═══════════════════════════════════════════════════════════════════════
REM  DateERP — التنصيب بضغطة زر
REM  انسخ المجلد كاملاً إلى الجهاز، ثم انقر هذا الملف نقراً مزدوجاً.
REM  لا يحتاج .NET (الحزمة مكتفية ذاتياً) ولا صلاحيات مدير في الوضع العادي.
REM ═══════════════════════════════════════════════════════════════════════
chcp 65001 >nul
setlocal EnableDelayedExpansion

set "SRC=%~dp0"
set "APPNAME=DateERP"
set "DEST=%ProgramFiles%\%APPNAME%"
set "DATADIR=%LocalAppData%\%APPNAME%"

echo.
echo ══════════════════════════════════════════════════════════════
echo    تنصيب %APPNAME% — نظام إدارة وتصنيع التمور
echo ══════════════════════════════════════════════════════════════
echo.
echo    المصدر : %SRC%
echo    الهدف  : %DEST%
echo.

REM ── 0) فحص المتطلبات ──
echo [1/6] فحص متطلبات النظام...
ver | findstr /i "10.0 11.0" >nul
if errorlevel 1 (
    echo        [تحذير] النظام ليس ويندوز 10 أو 11 — قد لا يعمل البرنامج.
)
echo        نظام التشغيل: مقبول.
echo.

REM ── 1) التحقق من سلامة الحزمة ──
echo [2/6] التحقق من سلامة الحزمة...
if not exist "%SRC%DateERP.exe" (
    echo        [خطأ] لم يُعثر على DateERP.exe في هذا المجلد.
    echo                تأكد أنك نسخت المجلد كاملاً بعد البناء.
    pause
    exit /b 1
)
if exist "%SRC%SHA256.txt" (
    for /f "delims=" %%H in (%SRC%SHA256.txt) do set "EXPECT=%%H"
    for /f "delims=" %%H in ('powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 '%SRC%DateERP.exe').Hash"') do set "ACTUAL=%%H"
    if /i "!EXPECT!"=="!ACTUAL!" (
        echo        البصمة مطابقة: !ACTUAL!
    ) else (
        echo        [خطأ] البصمة غير مطابقة — الحزمة تالفة أو معدَّلة.
        echo                المتوقع: !EXPECT!
        echo                الفعلي  : !ACTUAL!
        pause
        exit /b 1
    )
) else (
    echo        [تنبيه] لا يوجد ملف SHA256.txt — تخطّي التحقق.
)
echo.

REM ── 2) حماية البيانات القائمة ──
echo [3/6] حماية البيانات القائمة...
if not exist "%DATADIR%" mkdir "%DATADIR%"
if exist "%DATADIR%\config.json" (
    copy /y "%DATADIR%\config.json" "%DATADIR%\config.backup.json" >nul
    echo        نسخة احتياطية من config.json
)
if exist "%DATADIR%\dateerp_local.db" (
    set "STAMP=%date:~-4%%date:~3,2%%date:~0,2%_%time:~0,2%%time:~3,2%"
    set "STAMP=!STAMP: =0!"
    copy /y "%DATADIR%\dateerp_local.db" "%DATADIR%\dateerp_local_!STAMP!.db" >nul
    echo        نسخة احتياطية من قاعدة البيانات المحلية
)
echo.

REM ── 3) البحث عن تنصيبات قديمة ──
echo [4/6] البحث عن تنصيبات قديمة...
set "FOUNDOLD=0"
for %%P in ("%ProgramFiles%\%APPNAME%" "%ProgramFiles(x86)%\%APPNAME%" "%LocalAppData%\%APPNAME%\app" "%LocalAppData%\Programs\%APPNAME%") do (
    if exist "%%~P\DateERP.exe" (
        echo        [تنبيه] تنصيب سابق في: %%~P
        set "FOUNDOLD=1"
    )
)
if "!FOUNDOLD!"=="1" (
    echo.
    echo        سيُستبدل التنصيب السابق. بياناتك محفوظة في %DATADIR%
    choice /c YN /m "        متابعة؟ (Y=نعم N=إلغاء)"
    if errorlevel 2 ( echo        أُلغي التنصيب. & pause & exit /b 0 )
)
echo.

REM ── 4) النسخ ──
echo [5/6] نسخ الملفات...
if exist "%DEST%" rmdir /s /q "%DEST%"
mkdir "%DEST%"
xcopy /e /i /y /q "%SRC%*.*" "%DEST%\" >nul
if errorlevel 1 (
    echo        [خطأ] فشل النسخ إلى %DEST%
    echo                إن ظهر خطأ صلاحيات، انقر الملف بزر الفأرة الأيمن
    echo                واختر "تشغيل كمسؤول".
    pause
    exit /b 1
)
echo        تم النسخ إلى %DEST%
echo.

REM ── 5) الاختصارات ──
echo [6/6] إنشاء الاختصارات...
powershell -NoProfile -ExecutionPolicy Bypass -File "%DEST%\إنشاء_اختصار.ps1" -Target "%DEST%\DateERP.exe" -WorkDir "%DEST%"
if errorlevel 1 (
    echo        [تنبيه] تعذر إنشاء الاختصارات تلقائياً.
    echo                يمكنك إنشاء اختصار يدوياً إلى: %DEST%\DateERP.exe
) else (
    echo        اختصار سطح المكتب  ✓
    echo        اختصار قائمة ابدأ   ✓
)
echo.

echo ══════════════════════════════════════════════════════════════
echo    ✓ تم التنصيب بنجاح
echo.
echo    التشغيل   : اختصار "%APPNAME%" على سطح المكتب
echo    الإعدادات : %DATADIR%\config.json
echo    السجلات   : %DATADIR%\logs\
echo    إلغاء     : "%DEST%\إلغاء_التنصيب.bat"
echo.
echo    بعد التشغيل تحقّق من شريط العنوان: يجب أن يظهر ختم الإصدار.
echo ══════════════════════════════════════════════════════════════
echo.
choice /c YN /m "تشغيل البرنامج الآن؟ (Y=نعم N=لاحقاً)"
if errorlevel 2 goto :end
start "" "%DEST%\DateERP.exe"
:end
echo.
pause
endlocal
