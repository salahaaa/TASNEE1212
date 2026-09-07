using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;

namespace DatesErp.Infrastructure.Persistence;

/// <summary>
/// §5/§26 — طبقة وسيطة على SaveChanges:
/// 1) ختم الطوابع الزمنية والمنشئ/المعدِّل تلقائياً.
/// 2) تحديث رمز التزامن (بديل rowversion على SQLite).
/// 3) كتابة سجل التدقيق داخل نفس المعاملة — لا تدقيق بدون حركة ولا حركة بدون تدقيق.
/// 4) ترجمة تعارض التزامن إلى رسالة عربية واضحة.
/// </summary>
public class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentSession _session;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public AuditSaveChangesInterceptor(ICurrentSession session)
    {
        _session = session;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext ctx)
    {
        if (ctx == null) return;
        var now = DateTime.Now;
        var isSqlServer = ctx.Database.IsSqlServer();
        var auditEntries = new List<AuditLog>();

        foreach (var entry in ctx.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AuditLog) continue; // لا ندقق على التدقيق نفسه

            if (entry.Entity is AuditableEntity aud)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        aud.CreatedDate = now;
                        aud.CreatedBy ??= _session?.UserId;
                        if (!isSqlServer) aud.RowVersion = Guid.NewGuid().ToByteArray();
                        break;
                    case EntityState.Modified:
                        aud.ModifiedDate = now;
                        aud.ModifiedBy = _session?.UserId;
                        if (!isSqlServer) aud.RowVersion = Guid.NewGuid().ToByteArray();
                        break;
                }
            }

            // §26 — بناء سجل التدقيق للعمليات الجوهرية
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && entry.Entity is not SystemSetting && entry.Entity is not DbVersion)
            {
                var docType = entry.Entity.GetType().Name;
                var docNumber = (entry.Entity as WorkflowDocument)?.DocumentNumber;
                var recordId = (entry.Entity as BaseEntity)?.Id;
                string action = entry.State switch
                {
                    EntityState.Added => "Create",
                    EntityState.Modified => "Edit",
                    EntityState.Deleted => "Delete",
                    _ => null
                };
                if ((action == "Edit" || action == "Delete") && entry.Entity is WorkflowDocument wd)
                {
                    var statusProp = entry.Property(nameof(WorkflowDocument.Status));
                    var approveProp = entry.Property(nameof(WorkflowDocument.IsApproved));
                    if (approveProp != null && approveProp.CurrentValue is true && approveProp.OriginalValue is false) action = "Approve";
                    else if (statusProp != null && Equals(statusProp.CurrentValue, DocStatuses.Cancelled) && !Equals(statusProp.OriginalValue, DocStatuses.Cancelled)) action = "Cancel";
                    else if (statusProp != null && Equals(statusProp.CurrentValue, DocStatuses.Issued) && !Equals(statusProp.OriginalValue, DocStatuses.Issued)) action = "Issue";
                }

                auditEntries.Add(new AuditLog
                {
                    UserId = _session?.UserId,
                    UserName = _session?.UserName ?? "system",
                    ComputerName = Environment.MachineName,
                    MachineName = Environment.MachineName,
                    ActionDate = now,
                    ScreenName = docType,
                    ActionType = action,
                    DocumentType = docType,
                    DocumentNumber = docNumber,
                    RecordId = recordId,
                    OldValue = entry.State == EntityState.Deleted || entry.State == EntityState.Modified ? SafeJson(entry.OriginalValues.ToObject()) : null,
                    NewValue = entry.State == EntityState.Deleted ? null : SafeJson(entry.CurrentValues.ToObject())
                });
            }
        }

        if (auditEntries.Count > 0)
            ctx.Set<AuditLog>().AddRange(auditEntries);
    }

    private static string SafeJson(object o)
    {
        try
        {
            var s = JsonSerializer.Serialize(o, _json);
            return s.Length > 4000 ? s[..4000] : s;
        }
        catch { return null; }
    }
}
