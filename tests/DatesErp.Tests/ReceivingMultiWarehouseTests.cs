using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §المخازن المتعددة للخام — الثلاجة مخزن خام (التمر الممتاز يتلف بالانتظار فيُحفظ مبرّداً
/// حتى دوره في التصنيع). شحنة واحدة قد توزَّع أصنافها على أكثر من مخزن (هذا هنا وهذا هناك)
/// أو كلها في مخزن واحد. القرار على مستوى سطر الصنف، والمعالجة تعيد الكمية إلى مخزن الدفعة.
/// </summary>
public class ReceivingMultiWarehouseTests
{
    private static ShipmentItemDto Item(int productId, int? warehouseId, bool? treatment = false, string until = null, double qty = 1000, int packages = 50)
        => new()
        {
            ProductId = productId,
            PackagingTypeId = 3,
            PackageCount = packages,
            UnitWeightKg = 20,
            QtyKg = qty,
            ReceiptUnit = "سلة",
            RequiresTreatment = treatment,
            TreatmentUntil = until,
            WarehouseId = warehouseId
        };

    private static IReceivingService Receiving(TestHost h) => h.Get<IReceivingService>();

    private static int Wh(TestHost h, string code)
        => h.Get<DatesErpDbContext>().Warehouses.Single(w => w.WarehouseCode == code).Id;

    // ───────────────────────────────────────────────
    // 1) شحنة واحدة توزَّع أصنافها على مخزنين (خام + ثلاجة)
    // ───────────────────────────────────────────────
    [Fact]
    public void Shipment_Items_Split_Across_Raw_Warehouses()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw1 = db.Products.First(p => p.ProductCode == "001-001");
        var raw2 = db.Products.First(p => p.ProductCode == "001-002");
        int wrm = Wh(host, "WRM");
        int wcld = Wh(host, "WCLD");

        // صنف ممتاز إلى الثلاجة، وصنف آخر إلى مخزن الخام العادي — في نفس السند
        var s = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026", new List<ShipmentItemDto>
        {
            Item(raw1.Id, wcld, qty: 1000),
            Item(raw2.Id, wrm, qty: 500)
        });
        Assert.True(s.Ok, s.Message);
        Assert.True(Receiving(host).ApproveShipment(s.Id).Ok);

        var lotCold = db.Lots.AsNoTracking().Single(l => l.ProductId == raw1.Id && l.ShipmentId == s.Id);
        var lotRaw = db.Lots.AsNoTracking().Single(l => l.ProductId == raw2.Id && l.ShipmentId == s.Id);

        Assert.Equal(wcld, lotCold.WarehouseId);
        Assert.Equal(wrm, lotRaw.WarehouseId);

        // الرصيد قيّد في المخزن الصحيح لكل صنف
        Assert.Equal(1000, db.StockBalances.Where(b => b.WarehouseId == wcld && b.LotId == lotCold.Id).Sum(b => b.QtyKg), 1);
        Assert.Equal(500, db.StockBalances.Where(b => b.WarehouseId == wrm && b.LotId == lotRaw.Id).Sum(b => b.QtyKg), 1);

        // الثلاجة مخزن تخزين لا معالجة — الكمية متاحة فوراً
        Assert.Equal(0, lotCold.UnderTreatmentQtyKg, 1);
        Assert.Equal(1000, lotCold.AvailableQtyKg, 1);
    }

    // ───────────────────────────────────────────────
    // 2) مخزن السند هو الافتراضي لبند لم يحدد مخزناً (كل الشحنة في مخزن واحد)
    // ───────────────────────────────────────────────
    [Fact]
    public void Item_Without_Warehouse_Defaults_To_Shipment_Warehouse()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");
        int wcld = Wh(host, "WCLD");

        // السند كله موجَّه إلى الثلاجة (مخزن السند)، والبنود بلا مخزن صريح → ترث الثلاجة
        var s = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026",
            new List<ShipmentItemDto> { Item(raw.Id, null, qty: 1000) },
            warehouseId: wcld);
        Assert.True(s.Ok, s.Message);
        Assert.True(Receiving(host).ApproveShipment(s.Id).Ok);

        var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == s.Id);
        Assert.Equal(wcld, lot.WarehouseId);
        Assert.Equal(1000, db.StockBalances.Where(b => b.WarehouseId == wcld && b.LotId == lot.Id).Sum(b => b.QtyKg), 1);
    }

    // ───────────────────────────────────────────────
    // 3) الإفراج من المعالجة يعيد الكمية إلى مخزن الدفعة (لا إلى WRM الثابت)
    // ───────────────────────────────────────────────
    [Fact]
    public void Treatment_Release_Returns_To_The_Lots_Own_Warehouse()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");

        // مخزن خام ثانٍ (غير WRM) لاختبار أن المعالجة لا ترجع إلى WRM الثابت
        var wh2 = new Warehouse { WarehouseCode = "WRAW2", WarehouseNameAr = "مخزن الخام الثاني", WarehouseType = "Raw" };
        db.Warehouses.Add(wh2);
        db.SaveChanges();
        int wh2Id = wh2.Id;

        var received = DateTime.Today.AddDays(-14);
        var until = DateTime.Today.AddDays(-7);
        var s = Receiving(host).SaveShipment(1, received.ToString("dd/MM/yyyy"), received.ToString("dd/MM/yyyy"),
            new List<ShipmentItemDto> { Item(raw.Id, wh2Id, treatment: true, until: until.ToString("dd/MM/yyyy"), qty: 1000) });
        Assert.True(s.Ok, s.Message);
        Assert.True(Receiving(host).ApproveShipment(s.Id).Ok);

        var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == s.Id);
        Assert.Equal(wh2Id, lot.WarehouseId);
        // دخل المعالجة من مخزن الدفعة (الخام الثاني) لا من WRM
        var trt = db.RawTreatments.AsNoTracking().Single(t => t.LotId == lot.Id);
        Assert.True(trt.IsReadyByTime);

        var rel = host.Get<IRawTreatmentService>().Release(trt.Id, 1000);
        Assert.True(rel.Ok, rel.Message);

        Assert.Equal(0, db.StockBalances.Where(b => b.WarehouseId == Wh(host, "WTRT") && b.LotId == lot.Id).Sum(b => b.QtyKg), 1);
        // عادت الكمية إلى مخزن الدفعة الأصلي، لا إلى WRM
        Assert.Equal(1000, db.StockBalances.Where(b => b.WarehouseId == wh2Id && b.LotId == lot.Id).Sum(b => b.QtyKg), 1);
    }

    // ───────────────────────────────────────────────
    // 4) مخزن وجهة غير خام/غير نشط مرفوض عند الحفظ
    // ───────────────────────────────────────────────
    [Fact]
    public void Save_Rejected_When_Destination_Is_Not_A_Raw_Warehouse()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");
        int wfg = Wh(host, "WFG"); // مخزن التام — ليس خاماً

        var s = Receiving(host).SaveShipment(1, "08/09/2026", "08/09/2026",
            new List<ShipmentItemDto> { Item(raw.Id, wfg) });
        Assert.False(s.Ok);
        Assert.Contains("مخزن", s.Message);
    }
}
