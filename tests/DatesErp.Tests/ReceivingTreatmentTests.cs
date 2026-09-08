using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §المعالجة ضمن أمر الاستلام — دورة الاستلام المعدّلة:
/// لا نافذة مستقلة للمعالجة؛ قرار «نعم/لا» وتاريخ «حتى تاريخ» على مستوى سطر الصنف
/// داخل أمر استلام الشحنة نفسه. بند «نعم» يدخل مخزن المعالجة فور الاعتماد ويبقى
/// «قيد المعالجة» حتى تاريخه، ثم ينتقل لدورة الإفراج المعتمدة.
/// </summary>
public class ReceivingTreatmentTests
{
    private static ShipmentItemDto Item(int productId, bool? treatment, string until = null, double qty = 1000, int packages = 50)
        => new()
        {
            ProductId = productId,
            PackagingTypeId = 3,
            PackageCount = packages,
            UnitWeightKg = 20,
            QtyKg = qty,
            ReceiptUnit = "سلة",
            RequiresTreatment = treatment,
            TreatmentUntil = until
        };

    private static IReceivingService Receiving(TestHost h) => h.Get<IReceivingService>();

    private static Lot ReloadLot(TestHost h, int lotId)
    {
        var db = h.Get<DatesErpDbContext>();
        db.ChangeTracker.Clear();
        return db.Lots.AsNoTracking().First(l => l.Id == lotId);
    }

    // ───────────────────────────────────────────────
    // 1) استلام بدون معالجة — متاح فوراً بلا دورة معالجة
    // ───────────────────────────────────────────────
    [Fact]
    public void No_Treatment_Item_Is_Available_Immediately()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");

        var s = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026",
            new List<ShipmentItemDto> { Item(raw.Id, false) });
        Assert.True(s.Ok, s.Message);
        Assert.True(Receiving(host).ApproveShipment(s.Id).Ok);

        var item = db.ShipmentItems.AsNoTracking().Single(i => i.ShipmentId == s.Id);
        Assert.False(item.RequiresTreatment);
        Assert.Null(item.TreatmentUntil);
        Assert.Equal("بدون معالجة", item.TreatmentStatusAr);

        var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == s.Id);
        Assert.Equal(0, lot.UnderTreatmentQtyKg, 1);
        Assert.Equal(1000, lot.AvailableQtyKg, 1);          // متاح بالكامل
        Assert.Empty(db.RawTreatments.AsNoTracking().Where(t => t.LotId == lot.Id));
    }

    // ───────────────────────────────────────────────
    // 2) استلام مع معالجة لمدة أسبوع — يدخل مخزن المعالجة ويبقى قيد المعالجة
    // ───────────────────────────────────────────────
    [Fact]
    public void Treatment_Week_Item_Enters_Treatment_Warehouse_Until_Date()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");

        var s = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026",
            new List<ShipmentItemDto> { Item(raw.Id, true, "15/09/2026") });
        Assert.True(s.Ok, s.Message);
        Assert.True(Receiving(host).ApproveShipment(s.Id).Ok);

        var item = db.ShipmentItems.AsNoTracking().Single(i => i.ShipmentId == s.Id);
        Assert.True(item.RequiresTreatment);
        Assert.Equal(new DateTime(2026, 9, 15), item.TreatmentUntil.Value.Date);
        Assert.Equal("قيد المعالجة", item.TreatmentStatusAr);

        var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == s.Id);
        Assert.Equal(1000, lot.InStockQtyKg, 1);            // المخزون لم ينقص
        Assert.Equal(1000, lot.UnderTreatmentQtyKg, 1);     // كله تحت المعالجة
        Assert.Equal(0, lot.AvailableQtyKg, 1);             // غير متاح قبل انتهاء المدة

        // عملية معالجة تلقائية بموعد الجاهزية = نهاية يوم «حتى تاريخ»
        var trt = db.RawTreatments.AsNoTracking().Single(t => t.LotId == lot.Id);
        Assert.Equal(TreatmentStatuses.InProgress, trt.Status);
        Assert.Equal(new DateTime(2026, 9, 15).Date, trt.ExpectedReadyAt.Date);
        Assert.Equal(lot.ProductId, trt.ProductId);

        // الرصيد في مستودع المعالجة، والخام صفر — ثابت WRM + WTRT = InStock
        var wrm = db.Warehouses.Single(w => w.WarehouseCode == "WRM").Id;
        var wtrt = db.Warehouses.Single(w => w.WarehouseCode == "WTRT").Id;
        Assert.Equal(0, db.StockBalances.Where(b => b.WarehouseId == wrm && b.LotId == lot.Id).Sum(b => b.QtyKg), 1);
        Assert.Equal(1000, db.StockBalances.Where(b => b.WarehouseId == wtrt && b.LotId == lot.Id).Sum(b => b.QtyKg), 1);
    }

    // ───────────────────────────────────────────────
    // 3) استلام مختلط: صنف بمعالجة وصنف بلا معالجة
    // ───────────────────────────────────────────────
    [Fact]
    public void Mixed_Shipment_Treats_Each_Line_Independently()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw1 = db.Products.First(p => p.ProductCode == "001-001");
        var raw2 = db.Products.First(p => p.ProductCode == "001-002");

        var s = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026", new List<ShipmentItemDto>
        {
            Item(raw1.Id, true, "15/09/2026", 1000),
            Item(raw2.Id, false, qty: 500)
        });
        Assert.True(s.Ok, s.Message);
        Assert.True(Receiving(host).ApproveShipment(s.Id).Ok);

        var items = db.ShipmentItems.AsNoTracking().Where(i => i.ShipmentId == s.Id)
            .OrderBy(i => i.ProductId).ToList();
        Assert.Equal(2, items.Count);
        Assert.True(items[0].RequiresTreatment);
        Assert.False(items[1].RequiresTreatment);

        var lotTreated = db.Lots.AsNoTracking().Single(l => l.ProductId == raw1.Id && l.ShipmentId == s.Id);
        var lotPlain = db.Lots.AsNoTracking().Single(l => l.ProductId == raw2.Id && l.ShipmentId == s.Id);
        Assert.Equal(1000, lotTreated.UnderTreatmentQtyKg, 1);
        Assert.Equal(0, lotTreated.AvailableQtyKg, 1);
        Assert.Equal(0, lotPlain.UnderTreatmentQtyKg, 1);
        Assert.Equal(500, lotPlain.AvailableQtyKg, 1);
    }

    // ───────────────────────────────────────────────
    // 4) محاولة الحفظ بمعالجة «نعم» بلا تاريخ — مرفوض
    // ───────────────────────────────────────────────
    [Fact]
    public void Save_Rejected_When_Treatment_Yes_Without_Date()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");

        var s = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026",
            new List<ShipmentItemDto> { Item(raw.Id, true, null) });
        Assert.False(s.Ok);
        Assert.Contains("حتى تاريخ", s.Message);

        // فارغ النص أيضاً مرفوض
        var s2 = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026",
            new List<ShipmentItemDto> { Item(raw.Id, true, "") });
        Assert.False(s2.Ok);
        Assert.Contains("حتى تاريخ", s2.Message);
    }

    // ───────────────────────────────────────────────
    // 5) تاريخ معالجة غير صحيح (ماضٍ) — مرفوض
    // ───────────────────────────────────────────────
    [Fact]
    public void Save_Rejected_When_Treatment_Until_Before_Received()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");

        var s = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026",
            new List<ShipmentItemDto> { Item(raw.Id, true, "01/09/2026") });
        Assert.False(s.Ok);
        Assert.Contains("أكبر من أو يساوي", s.Message);
    }

    // ───────────────────────────────────────────────
    // 6) بعد انتهاء المدة: الإفراج يعيد الكمية جاهزة للإنتاج
    // ───────────────────────────────────────────────
    [Fact]
    public void After_Treatment_Ends_Release_Makes_Item_Ready()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");
        var trtSvc = host.Get<IRawTreatmentService>();

        // استلام بتاريخ ماضٍ ومدة انتهت بالفعل (حتى تاريخ مضى — بلا اعتماد على ساعة الجهاز)
        var received = DateTime.Today.AddDays(-14);
        var until = DateTime.Today.AddDays(-7);
        var s = Receiving(host).SaveShipment(1, received.ToString("dd/MM/yyyy"), received.ToString("dd/MM/yyyy"),
            new List<ShipmentItemDto> { Item(raw.Id, true, until.ToString("dd/MM/yyyy")) });
        Assert.True(s.Ok, s.Message);
        Assert.True(Receiving(host).ApproveShipment(s.Id).Ok);

        var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == s.Id);
        var trt = db.RawTreatments.AsNoTracking().Single(t => t.LotId == lot.Id);
        Assert.True(trt.IsReadyByTime, "انتهت مدة المعالجة فصارت جاهزة زمنياً للإفراج");

        var rel = trtSvc.Release(trt.Id, 1000);
        Assert.True(rel.Ok, rel.Message);

        var after = ReloadLot(host, lot.Id);
        Assert.Equal(0, after.UnderTreatmentQtyKg, 1);
        Assert.Equal(1000, after.TreatmentReadyQtyKg, 1);
        Assert.Equal(1000, after.AvailableQtyKg, 1);
        Assert.Equal("انتهت المعالجة — جاهز",
            db.ShipmentItems.AsNoTracking().Single(i => i.ShipmentId == s.Id).TreatmentStatusAr);
    }

    // ───────────────────────────────────────────────
    // 7) الإلغاء (Unapprove) يعكس دورة المعالجة التلقائية بالكامل
    // ───────────────────────────────────────────────
    [Fact]
    public void Unapprove_Reverses_Auto_Treatment()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");

        var s = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026",
            new List<ShipmentItemDto> { Item(raw.Id, true, "15/09/2026") });
        Assert.True(s.Ok, s.Message);
        Assert.True(Receiving(host).ApproveShipment(s.Id).Ok);

        int lotId = db.Lots.AsNoTracking().Single(l => l.ShipmentId == s.Id).Id;
        Assert.Single(db.RawTreatments.AsNoTracking().Where(t => t.LotId == lotId));

        var un = Receiving(host).UnapproveShipment(s.Id);
        Assert.True(un.Ok, un.Message);

        Assert.Empty(db.RawTreatments.AsNoTracking().Where(t => t.LotId == lotId));
        Assert.Empty(db.Lots.AsNoTracking().Where(l => l.Id == lotId));
        Assert.Empty(db.StockBalances.AsNoTracking().Where(b => b.LotId == lotId));
        // لا حركات معالجة متبقية
        var wtrt = db.Warehouses.Single(w => w.WarehouseCode == "WTRT").Id;
        Assert.Empty(db.InventoryTransactions.AsNoTracking().Where(t => t.WarehouseId == wtrt));
    }
}
