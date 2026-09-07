@echo off
REM ═══════════════════════════════════════════════════════════════════════
REM  DateERP — جمع التشخيص (يُشغَّل عند ظهور أي عطل)
REM
REM  الغرض: بدل وصف المشكلة بالكلام، هذا الملف يجمع كل ما يحتاجه المطوّر
REM  في مجلد واحد مضغوط ترسله كما هو.
REM ═══════════════════════════════════════════════════════════════════════
chcp 65001 >nul
setlocal EnableDelayedExpansion

set "APPDIR=%LocalAppData%\DateERP"
set "LOGDIR=%APPDIR%\logs"
set "OUT=%USERPROFILE%\Desktop\DateERP_تشخيص"

echo.
echo ══════════════════════════════════════════════════════════════════════════
echo    DateERP — جمع التشخيص
echo ══════════════════════════════════════════════════════════════════════════
echo.
echo    مجلد البيانات : %APPDIR%
echo    مجلد السجلات  : %LOGDIR%
echo    مجلد الإخراج  : %OUT%
echo.

REM ── 1) هل البرنامج مثبّت أصلاً؟ ──
echo [1/6] التحقق من التنصيب...
if not exist "%ProgramFiles%\DateERP\DateERP.exe" (
    echo        [تنبيه] لم يُعثر على البرنامج في Program Files.
    echo                إن كنت تشغّله من مجلد آخر فتجاهل هذا السطر.
) else (
    echo        ✓ البرنامج موجود في Program Files.
)
echo.

REM ── 2) إنشاء مجلد الإخراج ──
echo [2/6] إنشاء مجلد الإخراج...
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"
echo        ✓ تم.
echo.

REM ── 3) تقرير الفحص الذاتي ──
echo [3/6] تقرير الفحص الذاتي...
if exist "%LOGDIR%\selftest.txt" (
    copy /y "%LOGDIR%\selftest.txt" "%OUT%\1_الفحص_الذاتي.txt" >nul
    echo        ✓ موجود — سيُفتح الآن. اقرأه: الأسطر التي عليها ❌ هي موضع العطل.
    start "" notepad "%OUT%\1_الفحص_الذاتي.txt"
) else (
    echo        [مهم] لا يوجد تقرير فحص ذاتي بعد.
    echo                شغّل البرنامج ← «معلومات النظام» ← «🩺 تشغيل الفحص الذاتي الآن»
    echo                ثم أعد تشغيل هذا الملف.
)
echo.

REM ── 4) سجل الأخطاء ──
echo [4/6] سجل الأخطاء...
if exist "%LOGDIR%\errors.log" (
    copy /y "%LOGDIR%\errors.log" "%OUT%\2_سجل_الأخطاء.log" >nul
    for %%F in ("%LOGDIR%\errors.log") do echo        ✓ حجمه %%~zF بايت
) else (
    echo        لا يوجد سجل أخطاء — وهذا يعني أنه لم يُسجَّل أي استثناء.
)
if exist "%LOGDIR%\boot.log" copy /y "%LOGDIR%\boot.log" "%OUT%\3_سجل_الإقلاع.log" >nul
echo.

REM ── 5) معلومات النظام والقاعدة ──
echo [5/6] معلومات النظام...
(
  echo ===== DateERP — معلومات النظام =====
  echo التاريخ          : %DATE% %TIME%
  echo اسم الجهاز       : %COMPUTERNAME%
  echo المستخدم         : %USERNAME%
  echo نظام التشغيل     : %OS%
  echo.
  echo ===== إصدار ويندوز =====
  ver
  systeminfo | findstr /B /C:"OS Name" /C:"OS Version" /C:"System Type"
  echo.
  echo ===== إصدارات .NET المثبتة =====
  where dotnet >nul 2>&1 && dotnet --list-runtimes || echo "dotnet غير موجود في PATH (لا مشكلة — الحزمة مكتفية ذاتياً)"
  echo.
  echo ===== Program Files\DateERP =====
  if exist "%ProgramFiles%\DateERP" (dir "%ProgramFiles%\DateERP" /b) else (echo غير موجود)
  echo.
  echo ===== مجلد البيانات =====
  if exist "%APPDIR%" (dir "%APPDIR%" /s /b) else (echo غير موجود)
) > "%OUT%\4_معلومات_النظام.txt" 2>&1
echo        ✓ تم.
echo.

REM ── 6) قاعدة البيانات (إن وُجدت) ──
echo [6/6] قاعدة البيانات...
set "DBFOUND="
for /r "%APPDIR%" %%F in (*.db) do (
    copy /y "%%F" "%OUT%\5_قاعدة_البيانات.db" >nul
    echo        ✓ نُسخت: %%F
    set "DBFOUND=1"
)
if not defined DBFOUND echo        لا توجد قاعدة SQLite محلية — النظام على SQL Server غالباً.
echo.

echo ══════════════════════════════════════════════════════════════════════════
echo    انتهى الجمع. المجلد: %OUT%
echo.
echo    أرسل هذا المجلد كاملاً ^(أو اضغطه(zip) وأرسله^).
echo    لا تصف المشكلة بالكلام — الملف يشرحها بدقة.
echo ══════════════════════════════════════════════════════════════════════════
echo.
start "" explorer "%OUT%"
pause
endlocal
