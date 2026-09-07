/* ═══════════════════════════════════════════════════════════════════════
   DateERP — إعداد قاعدة البيانات على الخادم
   يُشغَّل مرة واحدة على جهاز الخادم (SQL Server 2016 أو أحدث).

   الطريقة:
     sqlcmd -S SERVER01 -E -i إعداد_قاعدة_البيانات.sql
   أو من SQL Server Management Studio بفتح الملف وتنفيذه.

   ملاحظة: جداول النظام نفسها ينشئها البرنامج تلقائياً عند أول اتصال
   (SchemaMigrator). هذا الملف ينشئ القاعدة والصلاحيات والنسخ الاحتياطي فقط.
   ═══════════════════════════════════════════════════════════════════════ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

PRINT '════════════════════════════════════════════════════════';
PRINT '  DateERP — إعداد قاعدة البيانات';
PRINT '════════════════════════════════════════════════════════';
GO

/* ── 1) إنشاء القاعدة إن لم توجد ── */
IF DB_ID(N'DateFactory') IS NULL
BEGIN
    PRINT '→ إنشاء قاعدة البيانات DateFactory ...';
    CREATE DATABASE [DateFactory]
        COLLATE Arabic_CI_AS;
    PRINT '   تم.';
END
ELSE
    PRINT '→ قاعدة البيانات DateFactory موجودة مسبقاً — تخطّي الإنشاء.';
GO

USE [DateFactory];
GO

/* ── 2) ضبط خيارات القاعدة ── */
PRINT '→ ضبط خيارات القاعدة ...';
ALTER DATABASE [DateFactory] SET RECOVERY FULL;
ALTER DATABASE [DateFactory] SET AUTO_CREATE_STATISTICS ON;
ALTER DATABASE [DateFactory] SET AUTO_UPDATE_STATISTICS ON;
ALTER DATABASE [DateFactory] SET AUTO_SHRINK OFF;   /* مهم: التصغير يفسد الأداء */
ALTER DATABASE [DateFactory] SET AUTO_CLOSE  OFF;
GO

/* ── 3) حساب التطبيق (اختياري — إن لم يُستخدم Windows Authentication) ──
   ألغِ التعليق إن أردت حساب SQL مخصص بدل المصادقة المتكاملة.
   كلمة المرور أدناه مثال — غيّرها قبل الاستخدام.
──────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'dateerp_app')
    CREATE LOGIN [dateerp_app] WITH PASSWORD = N'غيّر_هذه_الكلمة_2026!',
        CHECK_POLICY = ON, DEFAULT_DATABASE = [DateFactory];

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'dateerp_app')
    CREATE USER [dateerp_app] FOR LOGIN [dateerp_app];

ALTER ROLE [db_datareader] ADD MEMBER [dateerp_app];
ALTER ROLE [db_datawriter] ADD MEMBER [dateerp_app];
-- الصلاحيات التالية مطلوبة لأن البرنامج ينشئ الجداول ويزيد الأعمدة تلقائياً
ALTER ROLE [db_ddladmin]   ADD MEMBER [dateerp_app];
GO
────────────────────────────────────────────────────────────────────────── */

/* ── 4) التحقق ── */
PRINT '';
PRINT '→ التحقق ...';
SELECT
    DB_NAME()                                   AS [القاعدة],
    DATABASEPROPERTYEX(DB_NAME(), 'Recovery')   AS [نمط الاسترداد],
    DATABASEPROPERTYEX(DB_NAME(), 'Collation')  AS [الترميز],
    DATABASEPROPERTYEX(DB_NAME(), 'Status')     AS [الحالة];
GO

PRINT '';
PRINT '════════════════════════════════════════════════════════';
PRINT '  ✓ تم إعداد قاعدة البيانات';
PRINT '';
PRINT '  الخطوة التالية: شغّل "إعداد_جدار_الحماية.ps1" على الخادم،';
PRINT '  ثم "إعداد_النسخ_الاحتياطي.sql" لتفعيل النسخ اليومي.';
PRINT '════════════════════════════════════════════════════════';
GO
