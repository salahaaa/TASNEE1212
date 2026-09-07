using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B98 — تشغيل اليوم بإدخال يدوي موجّه + دورة أمر التشغيل (بدء/إيقاف/استئناف/إقفال) + سجل التنفيذ.
/// </summary>
public class B98RunTests
{
    private static readonly DateTime Today = DateTime.Today;
    private static string D(int offset) => (Today.AddDays(offset)).ToString("yyyy-MM-dd");
    private static string DdMmYy(string iso)
    {
        var p = iso.Split('-');
        return $"{p[2]}/{p[1]}/{p[0]}";
    }

    /// <summary>
    /// خطة متعددة الأيام من دفعة خام (إيراد ← مورد) — المنتج 3 بالعبوة 1 (5 كجم/كرتون).
    /// </summary>
    private static (int planId, Dictionary<string, int> itemByDay) SeedPlan(TestHost host, (string day, int cartons)[] days)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 8000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 8000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        {
            new() { ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 }
        });
        Assert.True(s.Ok, s.Message);
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.OrderBy(l => l.Id).Last().Id;

        var planSvc = host.Get<IPlanningService>();
        var items = days.Select(d => new PlanItemDto
        {
            SourceType = "FromReceiving", LotId = lot, CustomerId = 1,
            ProductId = 3, PackagingTypeId = 1,
            PlannedQtyKg = d.cartons * 5.0, PlannedCartons = d.cartons,
            ScheduledDate = d.day, SuggestedShiftId = 1, SuggestedLineId = 1
        }).ToList();
        var p = planSvc.SavePlan("خطة B98", days.Length > 1 ? "Period" : "Daily", days[0].day, days[^1].day, 1, 1, items);
        Assert.True(p.Ok, p.Message);
        Assert.True(planSvc.ApprovePlan(p.Id).Ok);

        var itemByDay = new Dictionary<string, int>();
        foreach (var (day, _) in days)
            itemByDay[day] = db.ProductionPlanItems.Single(i => i.PlanId == p.Id && i.ScheduledDate!.Value.Date == (DateTime.Parse(day).Date)).Id;
        return (p.Id, itemByDay);
    }

    /// <summary>إصدار يدوي لسطر واحد — يعيد رقم الأمر المنشأ.</summary>
    private static int IssueOne(TestHost host, int planId, string day, int itemId, int cartons)
    {
        var svc = host.Get<IDayRunService>();
        var r = svc.IssueSelected(planId, day, new List<DayRunIssueLineDto> { new() { ItemId = itemId, Cartons = cartons } });
        Assert.True(r.Ok, r.Message);
        return host.Get<DatesErpDbContext>().ProductionOrders.OrderByDescending(o => o.Id).First().Id;
    }

    [Fact]
    public void DayRun_Prefills_Remaining_And_DetectsOverdue()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        var (planId, _) = SeedPlan(host, new (string, int)[] { (D(-2), 50), (D(0), 100), (D(+1), 60) });

        var svc = host.Get<IDayRunService>();
        var today = svc.GetDayRun(planId, D(0));
        Assert.Equal(1, today.Rows.Count);
        var row = today.Rows[0];
        Assert.True(row.IsChecked);
        Assert.Equal(100, row.PlannedCartons);
        Assert.Equal(0, row.OrderedCartons);
        Assert.Equal(100, row.RemainingCartons);
        Assert.Equal(100, row.OrderCartons);          // مُعبأ بالمتبقي للمدخل اليدوي
        Assert.Equal(500.0, row.OrderKg, 1);          // 100 × 5 كجم
        Assert.NotEqual("—", row.ShiftName);           // وردية 1 معبأة من الخطة
        Assert.False(today.AllIssued);
        Assert.False(today.IsOverdue);

        var past = svc.GetDayRun(planId, D(-2));
        Assert.True(past.IsOverdue);
        Assert.Equal(50, past.Rows[0].RemainingCartons);
    }

    [Fact]
    public void IssueSelected_Creates_Linked_Scheduled_Order()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });

        int orderId = IssueOne(host, planId, D(0), byDay[D(0)], 60);

        var db = host.Get<DatesErpDbContext>();
        var order = db.ProductionOrders.Include(o => o.Items).Single(o => o.Id == orderId);
        Assert.Equal(DocStatuses.Scheduled, order.Status);          // معتمد + مجدول — جاهز للبدء
        Assert.Equal(planId, order.SourcePlanId.Value);
        Assert.Single(order.Items);
        Assert.Equal(byDay[D(0)], order.Items[0].PlanItemId);        // مرجع بند الخطة — تتبع كامل
        Assert.Equal(60, order.Items[0].PlannedCartons);
        Assert.Equal(300.0, order.Items[0].PlannedQtyKg, 1);

        // المتبقي انخفض في سياق التشغيل
        var ctx = host.Get<IDayRunService>().GetDayRun(planId, D(0));
        Assert.Equal(40, ctx.Rows[0].RemainingCartons);
        Assert.Equal(40, ctx.Rows[0].OrderCartons);
        Assert.False(ctx.AllIssued);
    }

    [Fact]
    public void IssueSelected_Rejects_OverRemaining_Zero_And_Empty()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });
        var svc = host.Get<IDayRunService>();
        int itemId = byDay[D(0)];

        Assert.False(svc.IssueSelected(planId, D(0), new List<DayRunIssueLineDto>()).Ok);
        Assert.False(svc.IssueSelected(planId, D(0), new List<DayRunIssueLineDto> { new() { ItemId = itemId, Cartons = 0 } }).Ok);
        Assert.False(svc.IssueSelected(planId, D(0), new List<DayRunIssueLineDto> { new() { ItemId = itemId, Cartons = 101 } }).Ok);

        // 40 + 40 = 80، ثم 21 يجب أن تُرفض (المتبقي 20)
        Assert.True(svc.IssueSelected(planId, D(0), new List<DayRunIssueLineDto> { new() { ItemId = itemId, Cartons = 40 } }).Ok);
        Assert.True(svc.IssueSelected(planId, D(0), new List<DayRunIssueLineDto> { new() { ItemId = itemId, Cartons = 40 } }).Ok);
        var over = svc.IssueSelected(planId, D(0), new List<DayRunIssueLineDto> { new() { ItemId = itemId, Cartons = 21 } });
        Assert.False(over.Ok);
        Assert.Contains("تتجاوز المتبقي", over.Message);

        // بعد 80، المتبقي 20 فقط — أمر أخير يملؤه ← اليوم كله مُشغَّل
        Assert.Equal(20, svc.GetDayRun(planId, D(0)).Rows[0].RemainingCartons);
        Assert.True(svc.IssueSelected(planId, D(0), new List<DayRunIssueLineDto> { new() { ItemId = itemId, Cartons = 20 } }).Ok);
        Assert.True(svc.GetDayRun(planId, D(0)).AllIssued);
    }

    [Fact]
    public void Lifecycle_Start_Stop_Resume_With_Reason_OnCard()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });
        int orderId = IssueOne(host, planId, D(0), byDay[D(0)], 100);

        host.LoginAs("production");
        var orders = host.Get<IProductionOrderService>();
        var db = host.Get<DatesErpDbContext>();

        Assert.True(orders.StartOrder(orderId).Ok);
        Assert.Equal(DocStatuses.InProgress, db.ProductionOrders.Single(o => o.Id == orderId).Status);

        var shortStop = orders.StopOrder(orderId, "عطل");
        Assert.False(shortStop.Ok); // الحارس على الخادم: لا إيقاف بلا سبب كافٍ
        var stop = orders.StopOrder(orderId, "عطل في ماكينة العجن — بانتظار الصيانة");
        Assert.True(stop.Ok);
        var stoppedOrder = db.ProductionOrders.Single(o => o.Id == orderId);
        Assert.Equal(DocStatuses.Stopped, stoppedOrder.Status);
        Assert.Equal("عطل في ماكينة العجن — بانتظار الصيانة", stoppedOrder.StatusReason);

        Assert.False(orders.StopOrder(orderId, "توقف مرة أخرى").Ok); // مكرر — ليس قيد التنفيذ

        // السبب يظهر على بطاقة المهمة (لا في الملاحظات فقط)
        var board = host.Get<ITaskCenterService>().GetBoard();
        Assert.Contains(board.InFlight, c => c.DocId == orderId && c.Reason == "عطل في ماكينة العجن — بانتظار الصيانة");

        Assert.True(orders.ResumeOrder(orderId).Ok);
        var resumed = db.ProductionOrders.Single(o => o.Id == orderId);
        Assert.Equal(DocStatuses.InProgress, resumed.Status);
        Assert.Null(resumed.StatusReason);
    }

    [Fact]
    public void CloseDay_Writes_Production_QC_And_PlanSync()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });
        int orderId = IssueOne(host, planId, D(0), byDay[D(0)], 60); // أمر 60 كرتون = 300 كجم

        host.LoginAs("production");
        Assert.True(host.Get<IProductionOrderService>().StartOrder(orderId).Ok);
        var exe = host.Get<IExecutionService>();
        var r = exe.CloseProductionDay(orderId, 300, 60, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(r.Ok, r.Message);

        var db = host.Get<DatesErpDbContext>();
        var order = db.ProductionOrders.Include(o => o.Items).Single(o => o.Id == orderId);
        Assert.Equal(300.0, order.Items.Single().ProducedQtyKg, 1);
        // اكتملت كل بنود الأمر → «مكتمل»
        Assert.Equal(DocStatuses.Completed, order.Status);

        // المزامنة إلى الخطة: المنتج يُجمع على بند الخطة
        var planItem = db.ProductionPlanItems.Single(i => i.Id == byDay[D(0)]);
        Assert.True(planItem.ProducedQtyKg >= 299.9, $"بند الخطة يجمع المنتج: {planItem.ProducedQtyKg}");

        // جودة: أمر فحص (QC) مُنشأ للأمر عند الإرسال للجودة
        // §B102 — تصحيح الاختبار نفسه: كان يفحص وجود «أمر إنتاج ثانٍ» بلا علاقة بالجودة (لم يُشغَّل قط في خطه الأصلي)
        Assert.True(db.QualityChecks.Any(c => c.OrderId == orderId && c.Status == DocStatuses.Submitted),
            "أمر جودة (QC) يجب أن يُنشأ عند الإرسال للجودة");
    }

    [Fact]
    public void ExecutionLog_Shows_Day_States()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        // يوم مضى (يُشغَّل ويُقفل جزئياً) + اليوم (مُشغَّل) + غداً (مُشغَّل) + بعد غد (لم يُشغَّل)
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(-2), 50), (D(0), 100), (D(+1), 60), (D(+2), 40) });
        int pastOrder = IssueOne(host, planId, D(-2), byDay[D(-2)], 50);
        IssueOne(host, planId, D(0), byDay[D(0)], 100);
        IssueOne(host, planId, D(+1), byDay[D(+1)], 60);

        host.LoginAs("production");
        Assert.True(host.Get<IProductionOrderService>().StartOrder(pastOrder).Ok);
        Assert.True(host.Get<IExecutionService>()
            .CloseProductionDay(pastOrder, 125, 25, 0, 0, 0, false, new List<DowntimeDto>(), false).Ok);

        var log = host.Get<IPlanProgressService>().GetExecutionLog(planId);
        Assert.Equal(4, log.Count);

        var dPast = log.Single(x => x.Date == DdMmYy(D(-2)));
        Assert.Equal("جزئي 🟠", dPast.StatusAr);
        Assert.True(dPast.Overdue);
        Assert.Equal(125.0, dPast.ProducedKg, 1);

        Assert.Equal("بانتظار التشغيل ⚡", log[1].StatusAr);
        Assert.False(log[1].Overdue);
        Assert.Equal("بانتظار التشغيل ⚡", log[2].StatusAr);

        Assert.Equal("لم يبدأ ⚪", log[3].StatusAr);
        Assert.Equal(0, log[3].OrderedKg, 1);
    }

    [Fact]
    public void DueDayCard_Becomes_ReadyOrderCard_After_Issue()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });

        host.LoginAs("production");
        var board0 = host.Get<ITaskCenterService>().GetBoard();
        var due = board0.Action.Single(c => c.DocType == "Plan" && c.DocId == planId);
        Assert.Equal("RunDay", due.Action); // §B98 — البطاقة تفتح «تشغيل اليوم»
        Assert.False(due.Overdue);

        IssueOne(host, planId, D(0), byDay[D(0)], 100); // تشغيل كامل لليوم

        var board1 = host.Get<ITaskCenterService>().GetBoard();
        Assert.DoesNotContain(board1.Action, c => c.DocType == "Plan" && c.DocId == planId); // غادر «المطلوب اليوم»
        Assert.Contains(board1.Action, c => c.DocType == "Order" && c.Title.Contains("جاهز للبدء"));
    }
}
