using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Tests;

/// <summary>مضيف اختبار: قاعدة SQLite معزولة + كل الخدمات الحقيقية.</summary>
public class TestHost : IDisposable
{
    public ServiceProvider Services { get; }
    public SqliteConnection Connection { get; }

    public TestHost()
    {
        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();

        Services = new ServiceCollection()
            .AddDatesErpInfrastructure(options => options.UseSqlite(Connection))
            .AddScoped<IAuditService, AuditService>()
            .AddScoped<IAuthService, AuthService>()
            .AddScoped<IReceivingService, ReceivingService>()
            .AddScoped<IPlanningService, PlanningService>()
                .AddScoped<IPlanClosureService, PlanClosureService>()
            .AddScoped<IProductionOrderService, ProductionOrderService>()
            .AddScoped<IExecutionService, ExecutionService>()
            .AddScoped<IQualityService, QualityService>()
            .AddScoped<IInspectionService, InspectionService>()
            .AddScoped<IFinishedGoodsService, FinishedGoodsService>()
            .AddScoped<IProductionDeliveryService, ProductionDeliveryService>()
            .AddScoped<ICustomerDeliveryService, CustomerDeliveryService>()
            .AddScoped<IInventoryService, InventoryService>()
            .AddScoped<IAdminService, AdminService>()
            .AddScoped<IReportService, ReportService>()
            .AddScoped<IBackupService, BackupService>()
            .AddScoped<Application.Services.MasterDataService>()
            .AddScoped<ICapacityService, DatesErp.Application.Services.CapacityService>()
            .AddScoped<IShiftService, DatesErp.Application.Services.ShiftService>()
            .AddScoped<IPlanProgressService, DatesErp.Application.Services.PlanProgressService>()
            .AddScoped<ITraceabilityService, DatesErp.Application.Services.TraceabilityService>()
            .AddScoped<IWorkflowTaskService, DatesErp.Application.Services.WorkflowTaskService>()
            .AddScoped<IRawTreatmentService, DatesErp.Application.Services.RawTreatmentService>()
            .AddScoped<ITaskCenterService, DatesErp.Application.Services.TaskCenterService>()
            .AddScoped<IDayRunService, DatesErp.Application.Services.DayRunService>()
            .AddScoped<ICustomerAvailabilityService, DatesErp.Application.Services.CustomerAvailabilityService>()
            .AddScoped<MachineRegistry>()
            .BuildServiceProvider();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        db.Database.EnsureCreated();
        DbSeeder.Seed(db);
    }

    /// <summary>تسجيل دخول كـ مدير النظام (صلاحيات كاملة).</summary>
    public SessionContext LoginAsAdmin() => LoginAs("admin");

    /// <summary>§B102 (سحب B97) — تسجيل دخول كأي مستخدم مُبذّر (admin/production/warehouse/quality).</summary>
    public SessionContext LoginAs(string userName)
    {
        var session = Services.GetRequiredService<DatesErp.Infrastructure.Session.SessionContext>();
        var auth = Services.GetRequiredService<IAuthService>();
        var r = auth.Login(userName, DbSeeder.InitialAdminPassword);
        if (!r.Success) throw new InvalidOperationException(r.Message);
        return session;
    }

    public T Get<T>() => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Services.Dispose();
        Connection.Dispose();
    }
}
