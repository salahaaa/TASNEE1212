using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B96 — فصل أمر تسليم الإنتاج (إدارة الإنتاج) عن أمر الاستلام (المخازن):
/// مصادر التسليم (محضر/خطة/إقفال) + التجاوز بصلاحية وسبب + تعدد العملاء على مستوى البنود.
/// </summary>
public class B96DeliveryTests
{
    private static (int orderId, int lotId) SeedAndClose(TestHost host, int producedKg = 500, int producedCtn = 67, bool sendToQuality = false)
    {
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out var lotId);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, producedKg, producedCtn, 0, 0, 0, false, new List<DowntimeDto>(), sendToQuality);
        Assert.True(close.Ok, close.Message);
        return (oid, lotId);
    }

    private static (int planId, int orderId, int lotId) SeedPlanWithProduction(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 5000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 5000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        rcv.ApproveShipment(s.Id);
        int lot = db.Lots.OrderBy(l => l.Id).Last().Id;

        var plan = host.Get<IPlanningService>();
        var p = plan.SavePlan("خطة B96", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 } });
        Assert.True(p.Ok, p.Message);
        Assert.True(plan.ApprovePlan(p.Id).Ok);
        int planItem = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == p.Id).Select(i => i.Id).First();

        var orders = host.Get<IProductionOrderService>();
        var o = orders.SaveOrder("FromPlan", p.Id, 1, "2026-08-20", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItem, LotId = lot, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 } });
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);

        var c = host.Get<IExecutionService>()
            .CloseProductionDay(o.Id, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(c.Ok, c.Message);
        return (p.Id, o.Id, lot);
    }

    private static (int orderId, int lotA, int lotB, int custB) SeedMultiCustomer(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 9000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 9000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s1 = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        rcv.ApproveShipment(s1.Id);
        int lotA = db.Lots.OrderBy(l => l.Id).Last().Id;
        var s2 = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        rcv.ApproveShipment(s2.Id);
        int lotB = db.Lots.OrderBy(l => l.Id).Last().Id;

        var cust2 = host.Get<MasterDataService>().SaveCustomer(null, "B96-C2", "عميل ثانٍ", "جملة", "777", "-", true);
        var orders = host.Get<IProductionOrderService>();
        var o = orders.SaveOrder("Manual", null, 1, "2026-08-21", 1, 1, new List<OrderItemDto>
        {
            new() { LotId = lotA, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 },
            new() { LotId = lotB, CustomerId = cust2.Id, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 }
        });
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        return (o.Id, lotA, lotB, cust2.Id);
    }

    private static int SaveApprovedCheck(TestHost host, int orderId, List<QualityItemDto> items)
    {
        var quality = host.Get<IQualityService>();
        var q = quality.SaveCheck(orderId, null, "2026-08-23", "نهائي", items);
        Assert.True(q.Ok, q.Message);
        Assert.True(quality.ApproveCheck(q.Id).Ok);
        return q.Id;
    }

    // ── 1) التسليم من محضر معتمد: ملء آلي بالمقبول + سقف المتبقي ──
    [Fact]
    public void Delivery_FromApprovedCheck_AutoFill_And_Caps_Remaining()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = SeedAndClose(host);
        int checkId = SaveApprovedCheck(host, oid, new List<QualityItemDto>
        { new() { ProductId = 3, LotId = lot, CheckedQtyKg = 500, AcceptedQtyKg = 490, RejectedQtyKg = 10 } });

        var del = host.Get<IProductionDeliveryService>();
        var ctx = del.GetSourceContext(DeliverySources.FromCheck, checkId);
        var line = Assert.Single(ctx.Lines);
        Assert.Equal(490, line.RemainingQtyKg, 1);
        Assert.Equal(1, line.CustomerId);

        var r = del.SaveDelivery(DeliverySources.FromCheck, checkId, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { OrderId = oid, ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 490 } });
        Assert.True(r.Ok, r.Message);

        var over = del.SaveDelivery(DeliverySources.FromCheck, checkId, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 1 } });
        Assert.False(over.Ok);
        Assert.Contains("المتبقي", over.Message);
    }

    // ── 2) لا تسليم من محضر غير معتمد ولا من فحص يدوي ──
    [Fact]
    public void Delivery_FromUnapproved_Or_Manual_Check_IsRejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = SeedAndClose(host);
        var quality = host.Get<IQualityService>();
        var q = quality.SaveCheck(oid, null, "2026-08-23", "نهائي", new List<QualityItemDto>
        { new() { ProductId = 3, LotId = lot, CheckedQtyKg = 500, AcceptedQtyKg = 490, RejectedQtyKg = 10 } });
        Assert.True(q.Ok, q.Message);

        var del = host.Get<IProductionDeliveryService>();
        var r = del.SaveDelivery(DeliverySources.FromCheck, q.Id, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 490 } });
        Assert.False(r.Ok);
        Assert.Contains("غير معتمد", r.Message);

        var m = quality.SaveCheck(null, null, "2026-08-24", "نهائي", new List<QualityItemDto>
        { new() { ProductId = 3, AcceptedQtyKg = 100 } });
        Assert.True(m.Ok, m.Message);
        var r2 = del.SaveDelivery(DeliverySources.FromCheck, m.Id, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, QtyKg = 100 } });
        Assert.False(r2.Ok);
        Assert.Contains("اليدوي", r2.Message);
    }

    // ── 3) التسليم من الخطة: تجاوز بصلاحية وسبب — بدونهما مرفوض ──
    [Fact]
    public void Delivery_FromPlan_Requires_BypassPermission_And_Reason()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, _, lot) = SeedPlanWithProduction(host);
        var del = host.Get<IProductionDeliveryService>();
        var session = host.Services.GetRequiredService<SessionContext>();
        List<ProductionDeliveryItemDto> Items() => new()
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 500 } };

        // سحب صلاحية التجاوز ← رفض استثنائي
        session.PermissionCache[("production", "BypassInspection")] = false;
        Assert.Throws<PermissionDeniedException>(() =>
            del.SaveDelivery(DeliverySources.FromPlan, planId, "2026-08-24", Items(), "سبب تجريبي"));

        // إعادة المنح بلا سبب ← رفض برسالة
        session.PermissionCache[("production", "BypassInspection")] = true;
        var noReason = del.SaveDelivery(DeliverySources.FromPlan, planId, "2026-08-24", Items());
        Assert.False(noReason.Ok);
        Assert.Contains("سبب", noReason.Message);

        // صلاحية + سبب ← قبول، والسبب محفوظ موثقاً
        var ok = del.SaveDelivery(DeliverySources.FromPlan, planId, "2026-08-24", Items(), "استعجال معتمد من الإدارة — عميل موسمي");
        Assert.True(ok.Ok, ok.Message);
        var db = host.Get<DatesErpDbContext>();
        Assert.Contains("استعجال", db.ProductionDeliveries.Single(d => d.Id == ok.Id).BypassReason);
    }

    // ── 4) التسليم من الإقفال يتطلب خطة مقفلة ──
    [Fact]
    public void Delivery_FromClosing_Requires_ClosedPlan()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, orderId, lot) = SeedPlanWithProduction(host);
        var del = host.Get<IProductionDeliveryService>();
        List<ProductionDeliveryItemDto> Items() => new()
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 500 } };

        var early = del.SaveDelivery(DeliverySources.FromClosing, planId, "2026-08-24", Items(), "تسوية إقفال");
        Assert.False(early.Ok);
        Assert.Contains("مقفلة", early.Message);

        Assert.True(host.Get<IProductionOrderService>().CloseOrder(orderId).Ok);
        Assert.True(host.Get<IPlanClosureService>().ClosePlanFinal(planId).Ok);
        var ok = del.SaveDelivery(DeliverySources.FromClosing, planId, "2026-08-24", Items(), "تسوية إقفال");
        Assert.True(ok.Ok, ok.Message);
    }

    // ── 5) تعدد العملاء: بند لكل عميل ← التام يُقيَّد بعميل البند ──
    [Fact]
    public void MultiCustomer_Delivery_And_Receipt_Posts_PerLineCustomer()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lotA, lotB, custB) = SeedMultiCustomer(host);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 1000, 134, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(close.Ok, close.Message);

        int checkId = SaveApprovedCheck(host, oid, new List<QualityItemDto>
        {
            new() { ProductId = 3, LotId = lotA, CheckedQtyKg = 500, AcceptedQtyKg = 490, RejectedQtyKg = 10 },
            new() { ProductId = 3, LotId = lotB, CheckedQtyKg = 500, AcceptedQtyKg = 480, RejectedQtyKg = 20 }
        });

        var del = host.Get<IProductionDeliveryService>();
        var ctx = del.GetSourceContext(DeliverySources.FromCheck, checkId);
        Assert.Equal(2, ctx.Lines.Count);
        var r = del.SaveDelivery(DeliverySources.FromCheck, checkId, "2026-08-24",
            ctx.Lines.Select(l => new ProductionDeliveryItemDto
            {
                OrderId = l.OrderId, ProductId = l.ProductId, LotId = l.LotId,
                CustomerId = l.CustomerId, QtyKg = l.RemainingQtyKg
            }).ToList());
        Assert.True(r.Ok, r.Message);
        Assert.True(del.IssueDelivery(r.Id).Ok);

        var fg = host.Get<IFinishedGoodsService>();
        var card = del.GetDelivery(r.Id);
        var fr = fg.SaveReceipt(oid, null, "2026-08-24",
            card.Lines.Select(l => new FinishedGoodsItemDto
            {
                ProductId = l.ProductId, LotId = l.LotId, PackageCount = 0,
                NetWeightKg = l.QtyKg, CustomerId = l.CustomerId, DeliveryItemId = l.Id
            }).ToList(), r.Id);
        Assert.True(fr.Ok, fr.Message);
        Assert.True(fg.Issue(fr.Id).Ok);
        var recv = fg.Receive(fr.Id, null);
        Assert.True(recv.Ok, recv.Message);

        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        var balA = db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.LotId == lotA && b.CustomerId == 1);
        Assert.Equal(490, balA.QtyKg, 1);
        var balB = db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.LotId == lotB && b.CustomerId == custB);
        Assert.Equal(480, balB.QtyKg, 1);
        Assert.Equal("Full", db.ProductionDeliveries.Single(d => d.Id == r.Id).ReceiptStatus);
    }

    // ── 6) الاستلام الجزئي يتراكم + فوق المتبقي مرفوض + الاكتمال يُكمل الأمر ──
    [Fact]
    public void Receipt_Partial_Accumulates_And_OverRemaining_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lot) = SeedAndClose(host);
        int checkId = SaveApprovedCheck(host, oid, new List<QualityItemDto>
        { new() { ProductId = 3, LotId = lot, CheckedQtyKg = 500, AcceptedQtyKg = 490, RejectedQtyKg = 10 } });

        var del = host.Get<IProductionDeliveryService>();
        var r = del.SaveDelivery(DeliverySources.FromCheck, checkId, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 490 } });
        Assert.True(r.Ok, r.Message);
        Assert.True(del.IssueDelivery(r.Id).Ok);
        int lineId = db.ProductionDeliveryItems.Single(i => i.DeliveryId == r.Id).Id;

        var fg = host.Get<IFinishedGoodsService>();
        var fr1 = fg.SaveReceipt(oid, null, "2026-08-24", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 490, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.True(fr1.Ok, fr1.Message);
        Assert.True(fg.Issue(fr1.Id).Ok);
        int item1 = db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == fr1.Id).Id;
        var p1 = fg.Receive(fr1.Id, new Dictionary<int, double> { [item1] = 200 });
        Assert.True(p1.Ok, p1.Message);
        Assert.Equal("Partial", db.ProductionDeliveries.Single(d => d.Id == r.Id).ReceiptStatus);

        // فوق المتبقي (290) مرفوض
        var over = fg.SaveReceipt(oid, null, "2026-08-25", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 291, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.False(over.Ok);
        Assert.Contains("المتبقي", over.Message);

        // استكمال 290 ← الأمر مكتمل
        var fr2 = fg.SaveReceipt(oid, null, "2026-08-25", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 290, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.True(fr2.Ok, fr2.Message);
        Assert.True(fg.Issue(fr2.Id).Ok);
        Assert.True(fg.Receive(fr2.Id, null).Ok);
        var d = db.ProductionDeliveries.Single(x => x.Id == r.Id);
        Assert.Equal("Full", d.ReceiptStatus);
        Assert.Equal(DocStatuses.Completed, d.Status);
    }

    // ── 7) إلغاء أمر مستلَم مرفوض ← إلغاء السند يعكس ويعيد الفتح ← الإلغاء يُقبل ──
    [Fact]
    public void CancelDelivery_Blocked_By_Receipts_Then_Unapprove_Reopens()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lot) = SeedAndClose(host);
        int checkId = SaveApprovedCheck(host, oid, new List<QualityItemDto>
        { new() { ProductId = 3, LotId = lot, CheckedQtyKg = 500, AcceptedQtyKg = 490, RejectedQtyKg = 10 } });

        var del = host.Get<IProductionDeliveryService>();
        var r = del.SaveDelivery(DeliverySources.FromCheck, checkId, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 490 } });
        Assert.True(del.IssueDelivery(r.Id).Ok);
        int lineId = db.ProductionDeliveryItems.Single(i => i.DeliveryId == r.Id).Id;

        var fg = host.Get<IFinishedGoodsService>();
        var fr = fg.SaveReceipt(oid, null, "2026-08-24", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 490, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.True(fg.Issue(fr.Id).Ok);
        Assert.True(fg.Receive(fr.Id, null).Ok);

        var blocked = del.CancelDelivery(r.Id);
        Assert.False(blocked.Ok);
        Assert.Contains("مستلم", blocked.Message);

        Assert.True(fg.Unapprove(fr.Id).Ok);
        var reopened = db.ProductionDeliveries.Single(d => d.Id == r.Id);
        Assert.Equal("None", reopened.ReceiptStatus);
        Assert.Equal(DocStatuses.Issued, reopened.Status);
        Assert.True(del.CancelDelivery(r.Id).Ok);

        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.Equal(0, db.StockBalances.Where(b => b.WarehouseId == wfg && b.ProductId == 3).Sum(b => b.QtyKg), 1);
    }

    // ── 8) المسار المباشر القديم يعمل كما هو (توافق) ──
    [Fact]
    public void Legacy_Direct_Receipt_Path_Unchanged()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lot) = SeedAndClose(host, sendToQuality: true);
        var fg = host.Get<IFinishedGoodsService>();

        var fr = fg.SaveReceipt(oid, null, "2026-08-24", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 500 } });
        Assert.True(fr.Ok, fr.Message);
        Assert.Contains("الجودة سمحت", fr.Message);
        Assert.True(fg.Issue(fr.Id).Ok);
        Assert.True(fg.Receive(fr.Id, null).Ok);

        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.Equal(500, db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.CustomerId == 1).QtyKg, 1);
    }
}
