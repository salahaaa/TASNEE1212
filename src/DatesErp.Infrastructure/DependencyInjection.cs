using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Infrastructure;

/// <summary>تركيب طبقة البنية التحتية في حاوية الخدمات.</summary>
public static class DependencyInjection
{
    /// <summary>تسجيل سياق قاعدة البيانات حسب الإعداد: SQL Server مركزي أو SQLite (اختبارات).</summary>
    public static IServiceCollection AddDatesErpInfrastructure(this IServiceCollection services, Action<DbContextOptionsBuilder> configure = null)
    {
        services.AddSingleton<SessionContext>();
        services.AddSingleton<ICurrentSession>(sp => sp.GetRequiredService<SessionContext>());
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddSingleton<ConnectionTester>();
        // §مهم: ترقيم المستندات يجب أن يكون Scoped حتى تُحفظ زيادة التسلسل مع معاملة المستند نفسها
        // (كان Singleton من قبل فلا تُحفظ الزيادة أبداً ← أرقام مكررة ← UNIQUE constraint)
        services.AddScoped<INumberingService, NumberingService>();

        services.AddDbContext<DatesErpDbContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
            if (configure != null) configure(options);
            else
            {
                var cfg = AppConfig.Load();
                if (cfg != null) options.UseSqlServer(cfg.BuildSqlServerConnectionString());
                else options.UseSqlite("Data Source=dateerp_dev.db");
            }
        });

        return services;
    }
}

/// <summary>
/// ترقيم المستندات المركزي — تسلسل آمن داخل معاملة.
/// §الحماية من التكرار (طبقتان):
///  • يُصلح المخططات المفقودة تلقائياً (ينشئ المخطط إن لم يوجد).
///  • حلقة ضمان: يتقدّم حتى رقم غير مستخدم — فيُصلح تلقائياً أي قاعدة تسلسلها غير
///    متزامن (كان الرقم يُكرر قديماً لأن الخدمة كانت Singleton فلا تُحفظ الزيادة أبداً).
/// §B84/C1 (صدق توثيقي): الحلقة تفحص المحفوظ فقط، فجهازان متزامنان قد يولّدان نفس الرقم.
///    يُعالَج بإعادة المحاولة التلقائية في ServiceBase.RunInTransaction عند تعارض القيد الفريد.
/// </summary>
public class NumberingService : INumberingService
{
    private readonly DatesErpDbContext _db;

    public NumberingService(DatesErpDbContext db)
    {
        _db = db;
    }

    public string Next(string schemeCode)
    {
        var scheme = _db.NumberingSchemes.FirstOrDefault(s => s.SchemeCode == schemeCode);
        if (scheme == null)
        {
            // §إصلاح ذاتي: مخطط مفقود ← يُنشأ بدل الانهيار أو توليد أرقام وقتية متصادمة
            scheme = new NumberingScheme
            {
                SchemeCode = schemeCode,
                SchemeName = schemeCode,
                Prefix = schemeCode,
                LastSequence = 0
            };
            _db.NumberingSchemes.Add(scheme);
        }

        // §تقدّم حتى رقم غير مستخدم: يضمن عدم التكرار حتى لو كان التسلسل متأخراً عن البيانات
        string number;
        do
        {
            scheme.LastSequence += 1;
            number = $"{scheme.Prefix}-{DateTime.Now:yyyy}-{scheme.LastSequence:D4}";
        } while (IsUsed(schemeCode, number));

        return number;
    }

    /// <summary>هل هذا الرقم مستخدم فعلاً في جدول المخطط؟</summary>
    private bool IsUsed(string schemeCode, string number) => schemeCode switch
    {
        "SHIP" => _db.Shipments.Any(x => x.DocumentNumber == number),
        "PLAN" => _db.ProductionPlans.Any(x => x.DocumentNumber == number),
        "ORD" => _db.ProductionOrders.Any(x => x.DocumentNumber == number),
        "EXE" => _db.ProductionExecutions.Any(x => x.DocumentNumber == number),
        "QC" => _db.QualityChecks.Any(x => x.DocumentNumber == number),
        "FGR" => _db.FinishedGoodsReceipts.Any(x => x.DocumentNumber == number),
        "RCV" => _db.FinishedGoodsReceipts.Any(x => x.DocumentNumber == number),
        "CD" => _db.CustomerDeliveries.Any(x => x.DocumentNumber == number),
        "TXN" => _db.InventoryTransactions.Any(x => x.TxnNumber == number),
        "CTX" => _db.InventoryTransactions.Any(x => x.TxnNumber == number),
        "CCD" => _db.CartonCountDocs.Any(x => x.DocumentNumber == number),
        "CSD" => _db.CartonSaleDocs.Any(x => x.DocumentNumber == number),
        "PCL" => _db.PlanClosings.Any(x => x.DocumentNumber == number),
        "LOT" => _db.Lots.Any(x => x.LotCode == number),
        "TASK" => _db.WorkflowTasks.Any(x => x.TaskNumber == number),
        _ => false
    };
}
