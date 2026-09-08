using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §المعالجة ضمن أمر الاستلام — قرار سطر الاستلام (نعم/لا) هو **مصدر الحقيقة** للصرف
/// والإنتاج، وليس <c>Product.RequiresTreatment</c> على بطاقة الصنف (إعداد عام/قديم).
///
/// نفس الصنف قد يصل في شحنة تحتاج معالجة وأخرى لا، فيُعالَج كلٌّ منهما حسب قرار سطره،
/// على مستوى الدفعة (Lot) — فلا تختلط كميات معالجة بغير معالجة لنفس الصنف.
///
/// كل الحالات تُختبر عبر مسار الصرف الحقيقي: أمر إنتاج يدوي ثم إقفال يوم الإنتاج
/// (ConsumeLot → GuardTreatedStock)، لا عبر مناداة الحارس مباشرة.
/// </summary>
public class ReceiptLineTreatmentAuthorityTests
{
    private const double BasketKg = 20;

    private static ShipmentItemDto Item(int productId, bool? treatment, string until = null, double qty = 5000)
        => new()
        {
            ProductId = productId,
            PackagingTypeId = 3,
            PackageCount = (int)(qty / BasketKg),
            UnitWeightKg = BasketKg,
            QtyKg = qty,
            ReceiptUnit = "سلة",
            RequiresTreatment = treatment,
            TreatmentUntil = until
        };

    /// <summary>مضيف اختبار + ضبط علم بطاقة الصنف (المصدر القديم) حسب الحاجة.</summary>
    private static TestHost NewHost(bool productRequiresTreatment = false)
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");
        raw.RequiresTreatment = productRequiresTreatment;
        db.SaveChanges();
        return host;
    }

    /// <summary>استلام بند واحد بمعالجة (نعم/لا) وقرار سطر صريح، ويعيد معرف الدفعة الناتجة.</summary>
    private static int ReceiveOn(TestHost host, bool? lineTreatment, string until = null, string received = null)
    {
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");
        var recv = received ?? DateTime.Today.AddDays(-1).ToString("dd/MM/yyyy");
        var r = host.Get<IReceivingService>().SaveShipment(1, recv, recv,
            new List<ShipmentItemDto> { Item(raw.Id, lineTreatment, until) });
        Assert.True(r.Ok, r.Message);
        Assert.True(host.Get<IReceivingService>().ApproveShipment(r.Id).Ok);
        return db.Lots.AsNoTracking().Single(l => l.ShipmentId == r.Id).Id;
    }

    private static int ManualOrder(TestHost host, int lotId, double kg)
    {
        var orders = host.Get<IProductionOrderService>();
        var o = orders.SaveOrder("Manual", null, 1, DateTime.Today.ToString("dd/MM/yyyy"), 1, 1,
            new List<OrderItemDto>
            {
                new() { LotId = lotId, ProductId = 3, PlannedQtyKg = kg, PlannedCartons = (int)(kg / 7.5) }
            });
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        return o.Id;
    }

    private static OpResult Close(TestHost host, int orderId, double kg)
        => host.Get<IExecutionService>().CloseProductionDay(
            orderId, kg, (int)(kg / 7.5), 0, 0, 0, false, null, false, consumedRawKg: kg);

    // ═══════════════════════════════════════════════════════════════
    // 1) نفس الصنف + شحنة معالجة = لا يُصرف قبل انتهاء المعالجة
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Treatment_Line_Yes_Blocks_Issuance_Before_Ready()
    {
        using var host = NewHost(productRequiresTreatment: false);
        int lotId = ReceiveOn(host, lineTreatment: true, until: DateTime.Today.AddDays(30).ToString("dd/MM/yyyy"));

        // الدفعة ورثت قرار السطر «نعم» صراحةً
        var lot = host.Get<DatesErpDbContext>().Lots.AsNoTracking().Single(l => l.Id == lotId);
        Assert.True(lot.RequiresTreatment);

        int orderId = ManualOrder(host, lotId, 5000);
        var closed = Close(host, orderId, 5000);
        Assert.False(closed.Ok);
        Assert.Contains("لم تكتمل معالجتها", closed.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // 2) نفس الصنف + شحنة غير معالجة = يُصرف إذا استوفى باقي الشروط
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Treatment_Line_No_Allows_Issuance()
    {
        using var host = NewHost(productRequiresTreatment: false);
        int lotId = ReceiveOn(host, lineTreatment: false);

        var lot = host.Get<DatesErpDbContext>().Lots.AsNoTracking().Single(l => l.Id == lotId);
        Assert.False(lot.RequiresTreatment == true);

        int orderId = ManualOrder(host, lotId, 5000);
        var closed = Close(host, orderId, 5000);
        Assert.True(closed.Ok, closed.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // 3) Product.RequiresTreatment = true + سطر «لا» = لا يمنع الصرف
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Product_Flag_True_But_Line_No_Does_Not_Block()
    {
        using var host = NewHost(productRequiresTreatment: true);
        int lotId = ReceiveOn(host, lineTreatment: false);

        // قرار السطر «لا» يعلو على علم البطاقة «true»
        var lot = host.Get<DatesErpDbContext>().Lots.AsNoTracking().Single(l => l.Id == lotId);
        Assert.False(lot.RequiresTreatment == true);

        int orderId = ManualOrder(host, lotId, 5000);
        var closed = Close(host, orderId, 5000);
        Assert.True(closed.Ok, closed.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4) Product.RequiresTreatment = false + سطر «نعم» = يمنع الصرف حتى انتهاء المعالجة
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Product_Flag_False_But_Line_Yes_Blocks_Until_Released()
    {
        using var host = NewHost(productRequiresTreatment: false);
        var received = DateTime.Today.AddDays(-14).ToString("dd/MM/yyyy");
        var until = DateTime.Today.AddDays(-7).ToString("dd/MM/yyyy");
        int lotId = ReceiveOn(host, lineTreatment: true, until: until, received: received);

        var db = host.Get<DatesErpDbContext>();
        var lot = db.Lots.AsNoTracking().Single(l => l.Id == lotId);
        Assert.True(lot.RequiresTreatment); // قرار السطر «نعم» يعلو على علم البطاقة «false»

        // قبل الإفراج: الصرف ممنوع
        int orderId = ManualOrder(host, lotId, 5000);
        var blocked = Close(host, orderId, 5000);
        Assert.False(blocked.Ok);
        Assert.Contains("لم تكتمل معالجتها", blocked.Message);

        // بعد الإفراج (المدة انقضت): الصرف يمر
        var trt = db.RawTreatments.AsNoTracking().Single(t => t.LotId == lotId);
        Assert.True(trt.IsReadyByTime);
        var rel = host.Get<IRawTreatmentService>().Release(trt.Id, 5000);
        Assert.True(rel.Ok, rel.Message);

        int orderId2 = ManualOrder(host, lotId, 5000);
        var allowed = Close(host, orderId2, 5000);
        Assert.True(allowed.Ok, allowed.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // 5) دفعتان لنفس الصنف (معالجة + غير معالجة) = كلٌّ حسب قرار سطره
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Two_Lots_Same_Product_Treated_And_Untreated_Handled_Independently()
    {
        using var host = NewHost(productRequiresTreatment: false);

        int treatedLotId = ReceiveOn(host, lineTreatment: true, until: DateTime.Today.AddDays(30).ToString("dd/MM/yyyy"));
        int plainLotId = ReceiveOn(host, lineTreatment: false);

        // الدفعة المعالجة ممنوعة
        int o1 = ManualOrder(host, treatedLotId, 5000);
        var c1 = Close(host, o1, 5000);
        Assert.False(c1.Ok);
        Assert.Contains("لم تكتمل معالجتها", c1.Message);

        // الدفعة غير المعالجة مسموحة — لنفس الصنف تماماً
        int o2 = ManualOrder(host, plainLotId, 5000);
        var c2 = Close(host, o2, 5000);
        Assert.True(c2.Ok, c2.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    // 8) إثبات أن GuardTreatedStock لم يعد يعتمد على Product.RequiresTreatment
    //    كمصدر وحيد للحقيقة: علم البطاقة معكوس عن قرار السطر في الحالتين،
    //    والسلوك يتبع قرار السطر دائماً.
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void GuardTreatedStock_Follows_Receipt_Line_Not_Product_Flag()
    {
        using var host = NewHost(productRequiresTreatment: true); // البطاقة تقول «نعم»
        int lotId = ReceiveOn(host, lineTreatment: false);         // السطر يقول «لا»

        // البطاقة تتطلب معالجة لكن السطر «لا» ⟵ الصرف مسموح (لا اعتماد على البطاقة)
        var db = host.Get<DatesErpDbContext>();
        Assert.True(db.Products.AsNoTracking().Single(p => p.ProductCode == "001-001").RequiresTreatment);
        Assert.False(db.Lots.AsNoTracking().Single(l => l.Id == lotId).RequiresTreatment == true);

        int o = ManualOrder(host, lotId, 5000);
        Assert.True(Close(host, o, 5000).Ok);
    }
}
