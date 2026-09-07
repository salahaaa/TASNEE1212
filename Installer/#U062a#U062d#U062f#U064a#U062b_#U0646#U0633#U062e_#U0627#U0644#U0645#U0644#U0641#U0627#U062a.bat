@echo off
REM ═══════════════════════════════════════════════════════════════════════
REM  DateERP — تحديث بالنسخ المباشر (B48 + B49 + B50)
REM  يستبدل الملفات العشرة التي تغيّرت فعلاً في مجلد DateERP_Publish الموجود.
REM  لا يعيد التنصيب ولا يلمس قاعدة البيانات ولا ملفات الإعداد.
REM  يُشغَّل بالنقر المزدوج، أو:  تحديث_نسخ_الملفات.bat "C:\...\DateERP_Publish"
REM ═══════════════════════════════════════════════════════════════════════
chcp 65001 >nul
setlocal EnableDelayedExpansion

set "SRC=%~dp0"
set "FILES=DateERP.exe DateERP.dll DateERP.pdb DateERP.deps.json DatesErp.Core.dll DatesErp.Core.pdb DatesErp.Application.dll DatesErp.Application.pdb DatesErp.Infrastructure.dll DatesErp.Infrastructure.pdb"

echo.
echo ══════════════════════════════════════════════════════════
echo   DateERP — تحديث B50 (الإصدار 1.26.0) بالنسخ المباشر
echo ══════════════════════════════════════════════════════════
echo.

REM ── 1) مجلد النظام ──
set "DEST=%~1"
if "%DEST%"=="" set /p DEST=اكتب المسار الكامل لمجلد DateERP_Publish: 
set DEST=%DEST:"=%
if "%DEST%"=="" (
    echo [خطأ] لم يُكتب مسار.
    pause & exit /b 1
)
if not exist "%DEST%\DateERP.exe" (
    echo [خطأ] لا يوجد DateERP.exe في:  %DEST%
    echo        تأكد من المسار — يجب أن يكون مجلد DateERP_Publish نفسه.
    pause & exit /b 1
)
echo [1/4] مجلد النظام: %DEST%
echo.

REM ── 2) إغلاق النظام إن كان يعمل (الملف المقفول لا يُستبدل) ──
echo [2/4] إغلاق النظام إن كان يعمل...
taskkill /f /im DateERP.exe >nul 2>&1
timeout /t 1 /nobreak >nul

REM ── 3) نسخة احتياطية من الملفات الحالية ──
set "BAK=%DEST%\_قبل_التحديث_B50"
if not exist "%BAK%" mkdir "%BAK%"
for %%F in (%FILES%) do if exist "%DEST%\%%F" copy /y "%DEST%\%%F" "%BAK%\" >nul
echo [3/4] الملفات السابقة محفوظة في: %BAK%
echo.

REM ── 4) النسخ والتحقق ──
echo [4/4] نسخ ملفات التحديث...
set "FAILED="
for %%F in (%FILES%) do (
    if not exist "%SRC%%%F" (
        echo   [ناقص] %%F غير موجود في حزمة التحديث
        set "FAILED=1"
    ) else (
        copy /y "%SRC%%%F" "%DEST%\" >nul
        if errorlevel 1 (
            echo   [فشل] %%F
            set "FAILED=1"
        ) else (
            echo   ✓ %%F
        )
    )
)
echo.

if defined FAILED (
    echo ══════════════════════════════════════════════════════════
    echo   ⚠ لم يكتمل التحديث — انظر الأسطر التي عليها [فشل] أو [ناقص]
    echo   غالباً السبب: النظام ما زال مفتوحاً، أو المسار خاطئ.
    echo ══════════════════════════════════════════════════════════
    pause & exit /b 1
)

REM ── البصمة بعد النسخ — للمطابقة مع SHA256_الملفات.txt ──
echo ── بصمات الملفات بعد النسخ ──
powershell -NoProfile -Command "Get-FileHash -Algorithm SHA256 '%DEST%\DateERP.dll','%DEST%\DateERP.exe','%DEST%\DatesErp.Application.dll','%DEST%\DatesErp.Infrastructure.dll','%DEST%\DatesErp.Core.dll' | ForEach-Object { '{0}  {1}' -f $_.Hash, (Split-Path $_.Path -Leaf) }"
echo.
echo ══════════════════════════════════════════════════════════
echo   ✓ تم التحديث إلى B50 (الإصدار 1.26.0)
echo   افتح النظام الآن — يجب أن يظهر في عنوان النافذة: 2026-09-02 B50
echo   وإن ظهر طابع أقدم فالملفات لم تُستبدل (النظام كان مفتوحاً).
echo ══════════════════════════════════════════════════════════
echo.
pause
endlocal
