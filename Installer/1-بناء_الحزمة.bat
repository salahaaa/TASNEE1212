@echo off
REM ═══════════════════════════════════════════════════════════════════════
REM  DateERP — بناء حزمة التنصيب (يُشغَّل مرة واحدة على جهاز المطوّر)
REM  يُنتج مجلد "DateERP_Publish" مكتفياً ذاتياً: يعمل على أي جهاز ويندوز
REM  بدون تثبيت .NET مسبقاً.
REM ═══════════════════════════════════════════════════════════════════════
chcp 65001 >nul
setlocal

set "ROOT=%~dp0.."
set "OUT=%ROOT%\DateERP_Publish"

echo.
echo ══════════════════════════════════════════════════════════
echo   DateERP — بناء حزمة التنصيب
echo ══════════════════════════════════════════════════════════
echo.

REM ── 1) التحقق من وجود SDK ──
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [خطأ] لم يُعثر على .NET SDK.
    echo        ثبّته من: https://dotnet.microsoft.com/download/dotnet/8.0
    echo        ثم أعد تشغيل هذا الملف.
    pause
    exit /b 1
)

echo [1/5] إصدار .NET SDK:
dotnet --version
echo.

REM ── 2) تنظيف النواتج السابقة ──
echo [2/5] تنظيف النواتج السابقة...
if exist "%OUT%" rmdir /s /q "%OUT%"
for /d /r "%ROOT%\src" %%D in (bin obj) do if exist "%%D" rmdir /s /q "%%D"
for /d /r "%ROOT%\tests" %%D in (bin obj) do if exist "%%D" rmdir /s /q "%%D"
echo        تم.
echo.

REM ── 3) الاختبارات قبل البناء ──
echo [3/5] تشغيل الاختبارات...
dotnet test "%ROOT%\tests\DatesErp.Tests\DatesErp.Tests.csproj" -c Release --nologo
if errorlevel 1 (
    echo.
    echo [خطأ] فشلت الاختبارات — أُوقف البناء. لا تُسلَّم حزمة باختبارات فاشلة.
    pause
    exit /b 1
)
echo.

REM ── 4) النشر مكتفياً ذاتياً ──
echo [4/5] النشر (win-x64، مكتفٍ ذاتياً، بدون تثبيت .NET على الجهاز الهدف)...
dotnet publish "%ROOT%\src\DatesErp.Desktop\DatesErp.Desktop.csproj" ^
    -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=false ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUT%"
if errorlevel 1 (
    echo [خطأ] فشل النشر.
    pause
    exit /b 1
)
echo.

REM ── 5) نسخ أدوات التنصيب والتوثيق ──
echo [5/5] نسخ أدوات التنصيب...
copy /y "%ROOT%\Installer\2-تنصيب.bat"            "%OUT%\" >nul
copy /y "%ROOT%\Installer\إلغاء_التنصيب.bat"       "%OUT%\" >nul
copy /y "%ROOT%\Installer\فحص_المتطلبات.bat"        "%OUT%\" >nul
copy /y "%ROOT%\Installer\إنشاء_اختصار.ps1"        "%OUT%\" >nul
copy /y "%ROOT%\Installer\README_التنصيب.md"       "%OUT%\" >nul
if exist "%ROOT%\Installer\server" xcopy /e /i /y "%ROOT%\Installer\server" "%OUT%\server" >nul

REM ── البصمة للتحقق من سلامة الحزمة ──
powershell -NoProfile -Command ^
  "Get-FileHash -Algorithm SHA256 '%OUT%\DateERP.exe' | Select-Object -ExpandProperty Hash | Out-File -Encoding ascii '%OUT%\SHA256.txt'"
echo        البصمة:
type "%OUT%\SHA256.txt"
echo.

echo ══════════════════════════════════════════════════════════
echo   تم بناء الحزمة:  %OUT%
echo   انسخ المجلد كاملاً إلى أي جهاز وشغّل "2-تنصيب.bat"
echo ══════════════════════════════════════════════════════════
echo.
pause
endlocal
