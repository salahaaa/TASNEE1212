@echo off
REM ═══════════════════════════════════════════════════════════════════════
REM  DateERP — إلغاء التنصيب
REM  يحذف البرنامج والاختصارات، ويسألك قبل حذف البيانات.
REM ═══════════════════════════════════════════════════════════════════════
chcp 65001 >nul
setlocal

set "APPNAME=DateERP"
set "DEST=%ProgramFiles%\%APPNAME%"
set "DATADIR=%LocalAppData%\%APPNAME%"

echo.
echo ══════════════════════════════════════════════════════════════
echo    إلغاء تنصيب %APPNAME%
echo ══════════════════════════════════════════════════════════════
echo.

REM ── إغلاق البرنامج إن كان يعمل ──
tasklist /fi "imagename eq DateERP.exe" 2>nul | find /i "DateERP.exe" >nul
if not errorlevel 1 (
    echo    البرنامج يعمل — سيُغلق الآن.
    taskkill /im DateERP.exe /f >nul 2>&1
    timeout /t 2 /nobreak >nul
)

REM ── حذف الاختصارات ──
echo [1/3] حذف الاختصارات...
del /q "%USERPROFILE%\Desktop\%APPNAME%.lnk" >nul 2>&1
rmdir /s /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\%APPNAME%" >nul 2>&1
rmdir /s /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\%APPNAME%" >nul 2>&1
echo        تم.
echo.

REM ── حذف ملفات البرنامج ──
echo [2/3] حذف ملفات البرنامج...
if exist "%DEST%" (
    rmdir /s /q "%DEST%"
    echo        حُذف %DEST%
) else (
    echo        لا يوجد تنصيب في %DEST%
)
echo.

REM ── البيانات: سؤال صريح ──
echo [3/3] البيانات والإعدادات...
echo        الموقع: %DATADIR%
if exist "%DATADIR%" (
    echo.
    echo        يحتوي على: config.json، قاعدة البيانات المحلية، والسجلات.
    choice /c YN /m "        هل تريد حذف البيانات أيضاً؟ (Y=حذف N=إبقاء)"
    if errorlevel 2 (
        echo        أُبقيت البيانات في %DATADIR%
    ) else (
        rmdir /s /q "%DATADIR%"
        echo        حُذفت البيانات.
    )
) else (
    echo        لا توجد بيانات محفوظة.
)
echo.

echo ══════════════════════════════════════════════════════════════
echo    ✓ تم إلغاء التنصيب
echo.
echo    ملاحظة: إن كان النظام متصلاً بقاعدة SQL Server على خادم،
echo    فقاعدة البيانات على الخادم لم تُمسّ.
echo ══════════════════════════════════════════════════════════════
echo.
pause
endlocal
