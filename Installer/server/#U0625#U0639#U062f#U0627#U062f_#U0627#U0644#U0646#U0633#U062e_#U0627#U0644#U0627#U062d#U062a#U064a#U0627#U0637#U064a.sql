/* ═══════════════════════════════════════════════════════════════════════
   DateERP — تفعيل النسخ الاحتياطي اليومي التلقائي
   يُشغَّل مرة واحدة على الخادم بعد إنشاء القاعدة.

   الطريقة:
     sqlcmd -S SERVER01 -E -d DateFactory -i إعداد_النسخ_الاحتياطي.sql

   يتطلب SQL Server Agent (غير متوفر في SQL Server Express — انظر الملاحظة أسفل الملف).
   ═══════════════════════════════════════════════════════════════════════ */

SET NOCOUNT ON;
GO

PRINT '════════════════════════════════════════════════════════';
PRINT '  DateERP — النسخ الاحتياطي اليومي';
PRINT '════════════════════════════════════════════════════════';
GO

USE [msdb];
GO

/* ── 1) مجلد النسخ ── */
DECLARE @dir NVARCHAR(260) = N'C:\DateERP_Backups';
PRINT '→ مجلد النسخ: ' + @dir;
PRINT '   إن لم يكن موجوداً فأنشئه يدوياً على الخادم.';
GO

/* ── 2) حذف المهمة القديمة إن وجدت ── */
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'DateERP_DailyBackup')
BEGIN
    PRINT '→ حذف المهمة القديمة ...';
    EXEC msdb.dbo.sp_delete_job @job_name = N'DateERP_DailyBackup', @delete_unused_schedule = 1;
END
GO

/* ── 3) إنشاء المهمة ── */
PRINT '→ إنشاء مهمة النسخ اليومي ...';

DECLARE @jobId UNIQUEIDENTIFIER;
EXEC msdb.dbo.sp_add_job
    @job_name = N'DateERP_DailyBackup',
    @description = N'نسخة احتياطية يومية كاملة لقاعدة DateERP مع تحقق من السلامة',
    @owner_login_name = N'sa',
    @enabled = 1,
    @job_id = @jobId OUTPUT;

EXEC msdb.dbo.sp_add_jobstep
    @job_id = @jobId,
    @step_name = N'Full Backup with CHECKSUM',
    @subsystem = N'TSQL',
    @database_name = N'DateFactory',
    @command = N'
BACKUP DATABASE [DateFactory]
    TO DISK = N''C:\DateERP_Backups\DateFactory_Full.bak''
    WITH INIT, COMPRESSION, CHECKSUM, STATS = 10;
',
    @on_success_action = 3,   /* الانتقال للخطوة التالية */
    @on_fail_action = 2;

EXEC msdb.dbo.sp_add_jobstep
    @job_id = @jobId,
    @step_name = N'Verify Backup Integrity',
    @subsystem = N'TSQL',
    @database_name = N'DateFactory',
    @command = N'RESTORE VERIFYONLY FROM DISK = N''C:\DateERP_Backups\DateFactory_Full.bak'' WITH CHECKSUM;',
    @on_success_action = 1,
    @on_fail_action = 2;

EXEC msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1;

/* ── 4) الجدولة: يومياً 23:00 ── */
EXEC msdb.dbo.sp_add_jobschedule
    @job_id = @jobId,
    @name = N'Daily 23:00',
    @freq_type = 4,              /* يومي */
    @freq_interval = 1,
    @active_start_time = 230000;

EXEC msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(LOCAL)';
GO

PRINT '';
PRINT '════════════════════════════════════════════════════════';
PRINT '  ✓ تم تفعيل النسخ الاحتياطي اليومي الساعة 23:00';
PRINT '';
PRINT '  الموقع: C:\DateERP_Backups\DateFactory_Full.bak';
PRINT '  مع RESTORE VERIFYONLY بعد كل نسخة — لا تُعتبر النسخة';
PRINT '  ناجحة بدون تحقق.';
PRINT '';
PRINT '  ملاحظة: SQL Server Express لا يحتوي SQL Server Agent.';
PRINT '  الحل: استخدم "مجدول مهام ويندوز" مع الأمر:';
PRINT '    sqlcmd -S SERVER01 -E -d DateFactory -Q "BACKUP DATABASE';
PRINT '    [DateFactory] TO DISK = N''C:\DateERP_Backups\DateFactory_Full.bak''';
PRINT '    WITH INIT, COMPRESSION, CHECKSUM"';
PRINT '════════════════════════════════════════════════════════';
GO
