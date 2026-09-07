using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§B79 — الاختبارات الإلزامية العشرة لشاشة «إقفال خطة الإنتاج».</summary>
public class PlanClosureTests
{
    private static T S<T>(TestHost h) => h.Services.CreateScope().ServiceProvider.GetRequiredService<T>();
    private static DatesErpDbContext Db(TestHost h)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(h.Connection).Options);

    private sealed class Env
    {
        public int PlanId; public int LotA; public int LotB; public int FinId; public int Fin2Id;
        public int CustA; public int CustB; public int Shift1; public int Shift2;
    }

    private static Env Setup(TestHost host)
    {
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = S<MasterDataService>(host);
        var rcv = S<IReceivingService>(host);
        var plan = S<IPlanningService>(host);
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var a = master.SaveCustomer(null, "CA", "شركة أ", "جملة", "1", "-", true);
        var b = master.SaveCustomer(null, "CB", "شركة ب", "جملة", "2", "-", true);
        var raw = master.SaveProductFull(null, "001-900", "خام الإقفال", "001", "Raw", "كجم", 0, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-900", "سكري", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var fin2 = master.SaveProductFull(null, "002-901", "خلاص", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var shA = rcv.SaveShipment(a.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { ProductId = raw.Id, QtyKg = 300000, PackageCount = 15000, UnitWeightKg = 20 } });
        rcv.ApproveShipment(shA.Id);
        var shB = rcv.SaveShipment(b.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { ProductId = raw.Id, QtyKg = 300000, PackageCount = 15000, UnitWeightKg = 20 } });
        rcv.ApproveShipment(shB.Id);
        var lotA = db.Lots.Single(l => l.ShipmentId == shA.Id).Id;
        var lotB = db.Lots.Single(l => l.ShipmentId == shB.Id).Id;
        var pl = plan.SavePlan("إقفال", "Daily", "2026-09-01", "2026-09-30", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            {
                new() { SourceType="FromReceiving", LotId=lotA, CustomerId=a.Id, ProductId=fin.Id, PlannedCartons=100, PlannedQtyKg=1000, ScheduledDate="2026-09-01" },
                new() { SourceType="FromReceiving", LotId=lotB, CustomerId=b.Id, ProductId=fin2.Id, PlannedCartons=100, PlannedQtyKg=1000, ScheduledDate="2026-09-02" }
            });
        Assert.True(pl.Ok, pl.Message);
        Assert.True(plan.ApprovePlan(pl.Id).Ok);
        var shifts = db.Shifts.AsNoTracking().OrderBy(s => s.Id).ToList();
        return new Env { PlanId = pl.Id, LotA = lotA, LotB = lotB, FinId = fin.Id, Fin2Id = fin2.Id, CustA = a.Id, CustB = b.Id, Shift1 = shifts[0].Id, Shift2 = shifts[1].Id };
    }

    private static int AddOrder(TestHost host, Env e, int cust, int lot, int fin, string date, int shift, int qty = 1000)
    {
        var orders = S<IProductionOrderService>(host);
        var db = host.Services.CreateScope().ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var piId = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == e.PlanId && i.ProductId == fin).Id;
        var r = orders.SaveOrder("FromPlan", e.PlanId, cust, date, shift, 1,
            new System.Collections.Generic.List<OrderItemDto>
            { new() { PlanItemId = piId, LotId = lot, ProductId = fin, PlannedQtyKg = qty, PlannedCartons = qty / 10 } });
        Assert.True(r.Ok, r.Message);
        return r.Id;
    }

    private static void CloseOrderFully(TestHost host, int orderId, int qty = 1000)
    {
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(orderId).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(orderId).Ok);
        var c = S<IExecutionService>(host).CloseProductionDay(orderId, qty, qty / 10, 0, 0, 0, false,
            new System.Collections.Generic.List<DowntimeDto>(), false, "تجربة", null);
        Assert.True(c.Ok, c.Message);
        var cr = S<IProductionOrderService>(host).CloseOrder(orderId); Assert.True(cr.Ok, cr.Message);
    }

    [Fact]
    public void T01_Single_Closed_Order_Allows_Plan_Closure()
    {
        using var host = new TestHost();
        var e = Setup(host);
        var o = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1);
        CloseOrderFully(host, o);
        var info = S<IPlanClosureService>(host).GetInfo(e.PlanId);
        Assert.True(info.CanClose);
        Assert.True(S<IPlanClosureService>(host).ClosePlanFinal(e.PlanId).Ok);
        Assert.Equal("مقفلة", S<IPlanClosureService>(host).GetInfo(e.PlanId).StatusAr);
    }

    [Fact]
    public void T02_Four_Closed_One_InProgress_Blocks()
    {
        using var host = new TestHost();
        var e = Setup(host);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1, 250), 250);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-02", e.Shift1, 250), 250);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.Fin2Id, "2026-09-03", e.Shift1, 250), 250);
        CloseOrderFully(host, AddOrder(host, e, e.CustB, e.LotB, e.Fin2Id, "2026-09-04", e.Shift1, 250), 250);
        var open = AddOrder(host, e, e.CustB, e.LotB, e.FinId, "2026-09-05", e.Shift2, 250);
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(open).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(open).Ok);   // قيد الإنتاج
        var svc = S<IPlanClosureService>(host);
        var r = svc.ClosePlanFinal(e.PlanId);
        Assert.False(r.Ok);
        Assert.Contains("قيد الإنتاج", r.Message);
    }

    [Fact]
    public void T03_MultiCustomer_All_Processed_Then_Summaries()
    {
        using var host = new TestHost();
        var e = Setup(host);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        CloseOrderFully(host, AddOrder(host, e, e.CustB, e.LotB, e.Fin2Id, "2026-09-02", e.Shift2));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        var info = svc.GetInfo(e.PlanId);
        Assert.Equal(2, info.Customers.Count);   // ملخص مستقل لكل عميل
    }

    [Fact]
    public void T04_MultiProduct_All_Completed_Product_Summaries()
    {
        using var host = new TestHost();
        var e = Setup(host);
        var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var fin2 = e.Fin2Id;
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, fin2, "2026-09-02", e.Shift1));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        Assert.Equal(2, svc.GetInfo(e.PlanId).Products.Count);
    }

    [Fact]
    public void T05_Monthly_Plan_One_Day_Closed_Not_Enough()
    {
        using var host = new TestHost();
        var e = Setup(host);   // خطة حتى 30/09
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1, 500), 500);
        var later = AddOrder(host, e, e.CustB, e.LotB, e.FinId, "2026-09-20", e.Shift1, 500);   // أمر لاحق مفتوح
        var svc = S<IPlanClosureService>(host);
        Assert.False(svc.ClosePlanFinal(e.PlanId).Ok);   // يوم واحد مقفل لا يكفي
        Assert.Contains("مفتوح", svc.GetInfo(e.PlanId).Blockers.Single(x => x.Contains("مفتوح")));
    }

    [Fact]
    public void T06_MultiShift_Open_Shift_Blocks()
    {
        using var host = new TestHost();
        var e = Setup(host);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1, 500), 500);
        var o2 = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift2, 500);  // نفس اليوم وردية ثانية مفتوحة
        var svc = S<IPlanClosureService>(host);
        Assert.False(svc.ClosePlanFinal(e.PlanId).Ok);
    }

    [Fact]
    public void T07_Cancelled_Order_Does_Not_Block_And_Is_Shown()
    {
        using var host = new TestHost();
        var e = Setup(host);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        var cancelled = AddOrder(host, e, e.CustB, e.LotB, e.Fin2Id, "2026-09-03", e.Shift2);
        Assert.True(S<IProductionOrderService>(host).CancelOrder(cancelled, "تجربة إلغاء").Ok);
        var svc = S<IPlanClosureService>(host);
        var info = svc.GetInfo(e.PlanId);
        Assert.True(info.CanClose);
        Assert.Contains(info.Orders, o => o.IsCancelled && o.StateAr == "ملغى");
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
    }

    [Fact]
    public void T08_Double_Closure_Prevented()
    {
        using var host = new TestHost();
        var e = Setup(host);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        var second = svc.ClosePlanFinal(e.PlanId);
        Assert.False(second.Ok);
        Assert.Contains("مرتين", second.Message);
    }

    [Fact]
    public void T09_Reopen_Recalculates_Status()
    {
        using var host = new TestHost();
        var e = Setup(host);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        var re = svc.ReopenPlan(e.PlanId, "تصحيح كمية");
        Assert.True(re.Ok, re.Message);
        var info = svc.GetInfo(e.PlanId);
        Assert.False(info.IsClosed);
        Assert.Equal("مكتملة", info.StatusAr);   // أُعيد الاحتساب من الأوامر
    }

    [Fact]
    public void T10_Editing_Closed_Plan_Rejected()
    {
        using var host = new TestHost();
        var e = Setup(host);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        var plan = S<IPlanningService>(host);
        var up = plan.UpdatePlan(e.PlanId, "تعديل", "Daily", "2026-09-01", "2026-09-30", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = e.LotA, CustomerId = e.CustA, ProductId = e.FinId, PlannedCartons = 10, PlannedQtyKg = 100 } });
        Assert.False(up.Ok);   // خطة مقفلة لا تُعدَّل
    }

    // ══════════ §B83: حالات المواصفة الإضافية (3، 7، 8) ══════════

    /// <summary>الحالة 3 — أمر مكتمل الإنتاج لكن غير مقفل: يمنع الإقفال حتى يُقفل الأمر رسمياً.</summary>
    [Fact]
    public void T11_Completed_But_Not_Closed_Order_Blocks()
    {
        using var host = new TestHost();
        var e = Setup(host);
        var o = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1);
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(o).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(o).Ok);
        // إنتاج كامل ← الأمر «مكتمل» — لكن الإقفال الرسمي لم يتم
        var c = S<IExecutionService>(host).CloseProductionDay(o, 1000, 100, 0, 0, 0, false,
            new System.Collections.Generic.List<DowntimeDto>(), false, "إنتاج كامل", null);
        Assert.True(c.Ok, c.Message);

        var svc = S<IPlanClosureService>(host);
        var info = svc.GetInfo(e.PlanId);
        Assert.Equal(1, info.CompletedOrders);
        Assert.Equal(1, info.UnprocessedOrders);
        Assert.False(info.CanClose);
        var blocked = svc.ClosePlanFinal(e.PlanId);
        Assert.False(blocked.Ok);
        Assert.Contains("مكتمل", blocked.Message);

        // الإقفال الرسمي للأمر ← تزول الممانعة
        Assert.True(S<IProductionOrderService>(host).CloseOrder(o).Ok);
        Assert.True(svc.GetInfo(e.PlanId).CanClose);
        Assert.True(svc.ClosePlanFinal(e.PlanId).Ok);
    }

    /// <summary>§B95 — الحالة 7: عجز 200 من 1000 معالج بتسوية موثقة (سبب إغلاق + توقف مسجل + عودة المتبقي للخام): يسمح بالإقفال.</summary>
    [Fact]
    public void T12_Settled_Shortfall_With_Reason_Allows_Closure()
    {
        using var host = new TestHost();
        var e = Setup(host);
        var o = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1);
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(o).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(o).Ok);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        double lotBefore = db.Lots.AsNoTracking().Single(l => l.Id == e.LotA).InStockQtyKg;

        // إنتاج 800 من 1000 + توقف معتمد ساعة — المتبقي يعود للخام بحركة موثقة
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var r = exec.CloseProductionDay(o, 800, 80, 0, 0, 0, false,
            new System.Collections.Generic.List<DowntimeDto>
            { new() { Hours = 1, ReasonAr = "عطل معتمد في خط الإنتاج" } }, false, "عجز معالج");
        Assert.True(r.Ok, r.Message);

        // §B85/H1: صُرف الخام (1,000) وعاد المتبقي (200) — الفرق «معالج» لا مفقود
        double lotAfter = db.Lots.AsNoTracking().Single(l => l.Id == e.LotA).InStockQtyKg;
        Assert.Equal(lotBefore - 800, lotAfter, 1);

        // الأمر يُقفل بتسوية موثقة (سبب العجز) ثم الخطة تُقفل والفرق يظهر كفروقات معالجة
        var co = S<IProductionOrderService>(host).CloseOrder(o, "عجز معالج: عطل معتمد في خط الإنتاج");
        Assert.True(co.Ok, co.Message);
        using var chk = Db(host);
        Assert.Equal("عجز معالج: عطل معتمد في خط الإنتاج",
            chk.ProductionOrders.AsNoTracking().Single(x => x.Id == o).CloseReason);
        var svc = S<IPlanClosureService>(host);
        var info = svc.GetInfo(e.PlanId);
        Assert.True(info.CanClose, string.Join(" | ", info.Blockers));
        Assert.Equal(200, info.SettledVariance, 1);
        Assert.True(svc.ClosePlanFinal(e.PlanId).Ok);
    }

    /// <summary>الحالة 8 — عجز 200 غير معالج: الأمر لا يُقفل والخطة لا تُقفل.</summary>
    [Fact]
    public void T13_Unsettled_Shortfall_Blocks_Closure()
    {
        using var host = new TestHost();
        var e = Setup(host);
        var o = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1);
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(o).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(o).Ok);
        // إنتاج 800 من 1000 — والـ200 الباقية غير معالجة (لا إرجاع ولا توثيق)
        var c = S<IExecutionService>(host).CloseProductionDay(o, 800, 80, 0, 0, 0, false,
            new System.Collections.Generic.List<DowntimeDto>(), false, "جزئي", null);
        Assert.True(c.Ok, c.Message);

        var closeOrder = S<IProductionOrderService>(host).CloseOrder(o);
        Assert.False(closeOrder.Ok);   // INCOMPLETE_ORDER — العجز غير معالج

        var svc = S<IPlanClosureService>(host);
        var info = svc.GetInfo(e.PlanId);
        Assert.False(info.CanClose);
        Assert.Equal(0, info.SettledVariance, 1);
        var blocked = svc.ClosePlanFinal(e.PlanId);
        Assert.False(blocked.Ok);
        Assert.Contains("أمر", blocked.Message);   // الأمر المخالف مذكور بالاسم
    }
}
