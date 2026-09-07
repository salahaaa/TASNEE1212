using DatesErp.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DatesErp.Infrastructure.Persistence;

/// <summary>
/// سياق قاعدة البيانات المركزي — §3 كل البيانات مركزية في SQL Server على السيرفر.
/// §5 التزامن تفاؤلي عبر rowversion، §26 تدقيق تلقائي داخل نفس المعاملة.
/// </summary>
public class DatesErpDbContext : DbContext
{
    public DatesErpDbContext(DbContextOptions<DatesErpDbContext> options) : base(options)
    {
    }

    public bool IsSqlServer => Database.IsSqlServer();

    // ── البيانات الأساسية ──
    public DbSet<CompanyInfo> CompanyInfos => Set<CompanyInfo>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<ItemGroup> ItemGroups => Set<ItemGroup>();
    public DbSet<ItemCategory> ItemCategories => Set<ItemCategory>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<PackagingType> PackagingTypes => Set<PackagingType>();
    public DbSet<AuxiliaryMaterial> AuxiliaryMaterials => Set<AuxiliaryMaterial>();
    public DbSet<ConsumptionFormula> ConsumptionFormulas => Set<ConsumptionFormula>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<ProductShiftCapacity> ProductShiftCapacities => Set<ProductShiftCapacity>();
    public DbSet<ProductionLine> ProductionLines => Set<ProductionLine>();
    public DbSet<Employee> Employees => Set<Employee>();

    // ── الاستلام والدفعات ──
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentItem> ShipmentItems => Set<ShipmentItem>();
    public DbSet<Lot> Lots => Set<Lot>();

    // ── المعالجة والتعقيم ──
    public DbSet<RawTreatment> RawTreatments => Set<RawTreatment>();
    public DbSet<TreatmentType> TreatmentTypes => Set<TreatmentType>();

    // ── التخطيط والإنتاج ──
    public DbSet<ProductionPlan> ProductionPlans => Set<ProductionPlan>();
    public DbSet<ProductionPlanItem> ProductionPlanItems => Set<ProductionPlanItem>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<ProductionOrderItem> ProductionOrderItems => Set<ProductionOrderItem>();
    public DbSet<ProductionOrderMaterial> ProductionOrderMaterials => Set<ProductionOrderMaterial>();
    public DbSet<ProductionExecution> ProductionExecutions => Set<ProductionExecution>();
    public DbSet<ExecutionDowntime> ExecutionDowntimes => Set<ExecutionDowntime>();
    public DbSet<PlanClosing> PlanClosings => Set<PlanClosing>();
    public DbSet<PlanClosingItem> PlanClosingItems => Set<PlanClosingItem>();

    // ── الجودة ──
    public DbSet<QualityCheck> QualityChecks => Set<QualityCheck>();
    public DbSet<QualityCheckItem> QualityCheckItems => Set<QualityCheckItem>();
    public DbSet<QualityCorrection> QualityCorrections => Set<QualityCorrection>();
    public DbSet<ProductionDelivery> ProductionDeliveries => Set<ProductionDelivery>();
    public DbSet<ProductionDeliveryItem> ProductionDeliveryItems => Set<ProductionDeliveryItem>();
    public DbSet<ByProduct> ByProducts => Set<ByProduct>();
    public DbSet<QualityByProductRecord> QualityByProductRecords => Set<QualityByProductRecord>();
    public DbSet<QualityStandard> QualityStandards => Set<QualityStandard>();
    public DbSet<QualityStandardRecord> QualityStandardRecords => Set<QualityStandardRecord>();
    public DbSet<PlanClosingByProduct> PlanClosingByProducts => Set<PlanClosingByProduct>();
    public DbSet<ExecutionByProduct> ExecutionByProducts => Set<ExecutionByProduct>();
    // §الفحص الديناميكي: أنواع النتائج قابلة للتعريف + ملفات الأصناف + النتائج الفعلية + تحويلات الوحدات
    public DbSet<InspectionResultType> InspectionResultTypes => Set<InspectionResultType>();
    public DbSet<ItemInspectionProfile> ItemInspectionProfiles => Set<ItemInspectionProfile>();
    public DbSet<InspectionResult> InspectionResults => Set<InspectionResult>();
    public DbSet<UnitConversion> UnitConversions => Set<UnitConversion>();
    public DbSet<PermissionResource> PermissionResources => Set<PermissionResource>();
    public DbSet<PermissionOperation> PermissionOperations => Set<PermissionOperation>();
    public DbSet<RoleResourcePermission> RoleResourcePermissions => Set<RoleResourcePermission>();
    public DbSet<UserResourcePermission> UserResourcePermissions => Set<UserResourcePermission>();
    public DbSet<PermissionAuditLog> PermissionAuditLogs => Set<PermissionAuditLog>();
    public DbSet<CartonCountDoc> CartonCountDocs => Set<CartonCountDoc>();
    public DbSet<CartonCountItem> CartonCountItems => Set<CartonCountItem>();
    public DbSet<CartonSaleDoc> CartonSaleDocs => Set<CartonSaleDoc>();
    public DbSet<CartonSaleItem> CartonSaleItems => Set<CartonSaleItem>();
    public DbSet<AuxGroup> AuxGroups => Set<AuxGroup>();
    public DbSet<AuxCustomerSpec> AuxCustomerSpecs => Set<AuxCustomerSpec>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();

    // ── التسليم ──
    public DbSet<FinishedGoodsReceipt> FinishedGoodsReceipts => Set<FinishedGoodsReceipt>();
    public DbSet<FinishedGoodsReceiptItem> FinishedGoodsReceiptItems => Set<FinishedGoodsReceiptItem>();
    public DbSet<CustomerDelivery> CustomerDeliveries => Set<CustomerDelivery>();
    public DbSet<CustomerDeliveryItem> CustomerDeliveryItems => Set<CustomerDeliveryItem>();

    // ── المخزون ──
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();

    // ── الأمان والإدارة ──
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ClientMachine> ClientMachines => Set<ClientMachine>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<DbVersion> DbVersions => Set<DbVersion>();
    public DbSet<NumberingScheme> NumberingSchemes => Set<NumberingScheme>();

    // ── طبقة التوجيه بالمهام (§3) ──
    public DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();
    public DbSet<WorkflowTaskHistory> WorkflowTaskHistories => Set<WorkflowTaskHistory>();

    /// <summary>
    /// §decimal على SQLite: المزوّد لا يخزّن decimal افتراضياً، فيُحوَّل إلى TEXT بدقة ثابتة.
    /// يسمح بإضافة حقول decimal جديدة (كميات الفحص ومعاملات التحويل) دون كسر SQLite أو SQL Server.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HaveConversion<DecimalToStringConverter>();
        configurationBuilder.Properties<decimal?>().HaveConversion<DecimalToStringConverter>();
    }

    /// <summary>محوّل decimal ↔ نص بدقة 18,4 — ثابت بين SQLite وSQL Server.</summary>
    private sealed class DecimalToStringConverter : ValueConverter<decimal, string>
    {
        public DecimalToStringConverter()
            : base(v => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                   v => decimal.Parse(v, System.Globalization.CultureInfo.InvariantCulture)) { }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // §5 — التزامن التفاؤلي: كل كيان قابل للتدقيق يحمل نسخة صف
        foreach (var et in b.Model.GetEntityTypes())
        {
            if (typeof(Core.Common.AuditableEntity).IsAssignableFrom(et.ClrType))
            {
                var rv = et.FindProperty(nameof(Core.Common.AuditableEntity.RowVersion));
                if (rv != null)
                {
                    // rowversion أصلي في SQL Server: رمز التزامن + توليد تلقائي عند الإضافة والتحديث
                    rv.IsConcurrencyToken = true;
                    if (Database.IsSqlServer())
                        rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate;
                }
            }
        }

        // ── طبقة التوجيه بالمهام (§3) ──
        // CorrelationKey فريد على مستوى القاعدة: هو الضمان الحقيقي لمنع تكرار المهام،
        // لا الفحص في الكود — فجهازان متزامنان لا يستطيعان توليد مهمتين لنفس الحدث.
        b.Entity<WorkflowTask>().HasIndex(t => t.CorrelationKey).IsUnique();
        b.Entity<WorkflowTask>().HasIndex(t => t.TaskNumber).IsUnique();
        // فهرس الاستطلاع الدوري (Q5): استعلام العدّادات كل 60 ثانية يمر من هنا
        b.Entity<WorkflowTask>().HasIndex(t => new { t.RequiredCapability, t.State });
        b.Entity<WorkflowTask>().HasIndex(t => new { t.AssignedUserId, t.State });
        b.Entity<WorkflowTask>().HasIndex(t => new { t.DocumentType, t.DocumentId });
        b.Entity<WorkflowTask>().HasIndex(t => t.BusinessDate);
        b.Entity<WorkflowTask>().Ignore(t => t.IsOverdue); // محسوبة، لا تُخزَّن
        b.Entity<WorkflowTask>()
            .HasMany<WorkflowTaskHistory>().WithOne(h => h.Task).HasForeignKey(h => h.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkflowTaskHistory>().HasIndex(h => h.TaskId);

        // ── المعالجة والتعقيم ──
        b.Entity<RawTreatment>().HasIndex(t => t.TreatmentNo).IsUnique();
        // فهرس «المتاح حسب تاريخ الإنتاج»: التخطيط يجمع المعالجات الجارية
        // بـ ExpectedReadyAt <= D لكل دفعة — هذا مسار الاستعلام الساخن.
        b.Entity<RawTreatment>().HasIndex(t => new { t.LotId, t.Status });
        b.Entity<RawTreatment>().HasIndex(t => new { t.Status, t.ExpectedReadyAt });
        b.Entity<TreatmentType>().HasIndex(t => t.TypeCode).IsUnique();
        // محسوبة، لا تُخزَّن — تصريح واضح رغم أن EF يتجاهل ما لا setter له
        b.Entity<RawTreatment>().Ignore(t => t.RemainingQtyKg);
        b.Entity<RawTreatment>().Ignore(t => t.IsReadyByTime);
        b.Entity<RawTreatment>().Ignore(t => t.IsOverdue);
        b.Entity<Lot>().Ignore(l => l.AvailableQtyKg);

        // ── علاقات الاستلام ──
        b.Entity<Shipment>().HasMany(s => s.Items).WithOne().HasForeignKey(i => i.ShipmentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Shipment>().HasMany(s => s.Lots).WithOne().HasForeignKey(l => l.ShipmentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Shipment>().HasIndex(s => s.DocumentNumber).IsUnique();

        // ── التخطيط ──
        b.Entity<ProductionPlan>().HasMany(p => p.Items).WithOne().HasForeignKey(i => i.PlanId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionPlan>().HasIndex(p => p.DocumentNumber).IsUnique();
        b.Entity<ProductionPlanItem>().HasIndex(i => new { i.ScheduledDate, i.SuggestedShiftId, i.SuggestedLineId });

        // ── أوامر الإنتاج ──
        b.Entity<ProductionOrder>().HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionOrder>().HasMany(o => o.Materials).WithOne().HasForeignKey(m => m.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionOrder>().HasIndex(o => o.DocumentNumber).IsUnique();

        // ── التنفيذ والإقفال اليومي ──
        b.Entity<ProductionExecution>().HasMany(x => x.Downtimes).WithOne().HasForeignKey(d => d.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionExecution>().HasIndex(x => x.DocumentNumber).IsUnique();
        b.Entity<PlanClosing>().HasMany(c => c.Items).WithOne().HasForeignKey(i => i.ClosingId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanClosing>().HasMany(c => c.Downtimes).WithOne().HasForeignKey(d => d.ClosingId).OnDelete(DeleteBehavior.Cascade);
        // §عطل مُصلح: PlanClosingItem.ByProducts كانت تُحفظ بلا إعداد علاقة، فيبقى ClosingId = 0
        // وتُكتب الصفوف يتيمة لا يربطها شيء بأي إقفال. الاسم «ClosingId» تاريخي ومعناه
        // «بند الإقفال» — أُبقي الاسم حتى لا ينكسر عمود في قواعد المستخدمين القائمة.
        b.Entity<PlanClosingItem>().HasMany(i => i.ByProducts).WithOne().HasForeignKey(x => x.ClosingId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanClosing>().HasIndex(c => c.DocumentNumber).IsUnique();

        // ── الجودة ──
        b.Entity<QualityCheck>().HasMany(q => q.Items).WithOne().HasForeignKey(i => i.CheckId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<QualityCheck>().HasIndex(q => q.DocumentNumber).IsUnique();
        // §الفحص الديناميكي: نتائج الفحص تُحذف مع استمارتها — لا نتائج يتيمة
        b.Entity<QualityCheck>().HasMany(q => q.Results).WithOne().HasForeignKey(r => r.CheckId).OnDelete(DeleteBehavior.Cascade);
        // §مخرجات الجلسة تُحذف معها — لا سجلات يتيمة
        b.Entity<ProductionExecution>().HasMany(e => e.ByProducts).WithOne().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<InspectionResultType>().HasIndex(t => t.Code);
        b.Entity<InspectionResult>().HasIndex(r => r.CheckId);
        b.Entity<ItemInspectionProfile>().HasIndex(p => p.ProductId);

        // ── التسليم ──
        b.Entity<FinishedGoodsReceipt>().HasMany(r => r.Items).WithOne().HasForeignKey(i => i.ReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FinishedGoodsReceipt>().HasIndex(r => r.DocumentNumber).IsUnique();
        b.Entity<CustomerDelivery>().HasMany(d => d.Items).WithOne().HasForeignKey(i => i.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CustomerDelivery>().HasIndex(d => d.DocumentNumber).IsUnique();

        // ── المخزون §9 ──
        b.Entity<StockBalance>().HasIndex(x => new { x.WarehouseId, x.ProductId, x.MaterialId, x.LotId, x.CustomerId });
        b.Entity<InventoryTransaction>().HasIndex(x => x.TxnNumber).IsUnique();
        b.Entity<InventoryTransaction>().HasIndex(x => new { x.ReferenceDocType, x.ReferenceDocNumber });

        // ── الأمان ──
        b.Entity<AppUser>().HasIndex(u => u.UserName).IsUnique();
        b.Entity<AppUser>().HasMany(u => u.UserRoles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Role>().HasIndex(r => r.RoleCode).IsUnique();
        b.Entity<Role>().HasMany(r => r.Permissions).WithOne().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RolePermission>().HasIndex(p => new { p.RoleId, p.ModuleCode }).IsUnique();
        b.Entity<AuditLog>().HasIndex(a => a.ActionDate);
        b.Entity<ClientMachine>().HasIndex(m => m.MachineId).IsUnique();

        // ── الإعدادات ──
        b.Entity<SystemSetting>().HasIndex(s => s.SettingKey).IsUnique();
        b.Entity<NumberingScheme>().HasIndex(n => n.SchemeCode).IsUnique();
        b.Entity<Product>().HasIndex(p => p.ProductCode).IsUnique();
        b.Entity<Customer>().HasIndex(c => c.CustomerCode).IsUnique();
        b.Entity<AuxiliaryMaterial>().HasIndex(m => m.MaterialCode).IsUnique();
        b.Entity<Lot>().HasIndex(l => l.LotCode).IsUnique();

        // جميع الأعمدة النصية والثنائية (كالشعار) اختيارية — قاعدة بيانات عملية تحتمل القيم الفارغة
        foreach (var et in b.Model.GetEntityTypes())
            foreach (var prop in et.GetProperties())
                if ((prop.ClrType == typeof(string) || prop.ClrType == typeof(byte[])) && prop.Name != "RowVersion")
                    prop.IsNullable = true;
    }
}
