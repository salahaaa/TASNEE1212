using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §B98 — تشغيل اليوم بإدخال يدوي موجّه:
/// المدير يفتح مهمة «المطلوب اليوم» فيرى سطور يومه (عميل/صنف/وردية/متبقي — من الخطة، بلا إعادة إدخال)،
/// يحدد الصفوف ويدخل كراتين كل أمر (معبأ بالمتبقي وقابل للتعديل)، ثم ينشئ الأوامر.
/// أمر واحد لكل (وردية×خط×عميل) — مرجعاً لبند الخطة، وكل شيء أو لا شيء (معاملة ذرية).
/// </summary>
public class DayRunService : ServiceBase, IDayRunService
{
    private readonly IProductionOrderService _orders;

    public DayRunService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IProductionOrderService orders)
        : base(db, session, numbering)
    {
        _orders = orders;
    }

    public DayRunContextDto GetDayRun(int planId, string date)
    {
        var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId)
                   ?? throw new DomainException("خطة الإنتاج غير موجودة.");
        var day = (UiFormat.TryParseDate(date, out var d) ? d : DateTime.Today).Date;

        var items = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.PlanId == planId && i.ScheduledDate != null && i.ScheduledDate.Value.Date == day)
            .OrderBy(i => i.Id).ToList();

        var products = Db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
        var packs = Db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);
        var customers = Db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
        var lots = Db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.LotCode);
        var shifts = Db.Shifts.AsNoTracking().ToDictionary(s => s.Id, s => s.ShiftNameAr);
        var lines = Db.ProductionLines.AsNoTracking().ToDictionary(l => l.Id, l => l.LineNameAr);

        var rows = new List<DayRunRowDto>();
        foreach (var it in items)
        {
            var ordered = Db.ProductionOrderItems.AsNoTracking()
                .Where(x => x.PlanItemId == it.Id)
                .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x, o })
                .Where(z => z.o.Status != DocStatuses.Cancelled)
                .ToList();
            double orderedKg = ordered.Sum(z => z.x.PlannedQtyKg);
            int orderedCtn = ordered.Sum(z => z.x.PlannedCartons);
            double remainingKg = Math.Max(0, it.PlannedQtyKg - orderedKg);
            int remainingCtn = Math.Max(0, it.PlannedCartons - orderedCtn);
            double packW = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId);

            rows.Add(new DayRunRowDto
            {
                ItemId = it.Id,
                IsChecked = remainingCtn > 0,
                CustomerName = it.CustomerId != null && customers.TryGetValue(it.CustomerId.Value, out var cu) ? cu : "—",
                LotCode = it.LotId != null && lots.TryGetValue(it.LotId.Value, out var lt) ? lt : "—",
                ProductName = products.TryGetValue(it.ProductId, out var pr) ? pr : $"#{it.ProductId}",
                PackName = it.PackagingTypeId != null && packs.TryGetValue(it.PackagingTypeId.Value, out var pk) ? pk : "—",
                ShiftName = it.SuggestedShiftId != null && shifts.TryGetValue(it.SuggestedShiftId.Value, out var sh) ? sh : "—",
                LineName = it.SuggestedLineId != null && lines.TryGetValue(it.SuggestedLineId.Value, out var ln) ? ln : "—",
                PlannedKg = it.PlannedQtyKg,
                PlannedCartons = it.PlannedCartons,
                OrderedKg = orderedKg,
                OrderedCartons = orderedCtn,
                RemainingKg = remainingKg,
                RemainingCartons = remainingCtn,
                // §B98 — التعبئة الأولية للمدخل اليدوي: المتبقي كاملاً (يعدّلها المدير كيف يشاء ضمن السقف)
                OrderCartons = remainingCtn,
                PackWeight = packW,
                OrderKg = remainingCtn * packW
            });
        }

        return new DayRunContextDto
        {
            PlanId = planId,
            PlanNumber = plan.DocumentNumber,
            PlanTitle = plan.PlanTitle,
            Date = day.ToString("dd/MM/yyyy"),
            IsOverdue = day < DateTime.Today,
            AllIssued = rows.Count == 0 || rows.All(r => r.RemainingCartons <= 0),
            Rows = rows
        };
    }

    public OpResult IssueSelected(int planId, string date, List<DayRunIssueLineDto> lines)
    {
        Require("production", "Create");
        if (lines == null || lines.Count == 0)
            return OpResult.Fail("حدد سطراً واحداً على الأقل للتشغيل — علّمه وأدخل كراتين الأمر.");

        var day = (UiFormat.TryParseDate(date, out var d) ? d : DateTime.Today).Date;
        var items = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.PlanId == planId && i.ScheduledDate != null && i.ScheduledDate.Value.Date == day)
            .ToDictionary(i => i.Id);

        // تحقق يدوي صارم: لا سطر غريب، لا صفر صامت، لا تجاوز للمتبقي — ثم الأمر يُنشأ «كامل المجموعة»
        var selected = new List<(ProductionPlanItem Item, int Cartons)>();
        foreach (var l in lines)
        {
            if (!items.TryGetValue(l.ItemId, out var it))
                return OpResult.Fail("سطر غير موجود في هذا اليوم — حدّث القائمة وأعد المحاولة.");
            if (l.Cartons <= 0)
                return OpResult.Fail($"سطر «{Db.Products.AsNoTracking().FirstOrDefault(p => p.Id == it.ProductId)?.ProductNameAr ?? it.ProductId.ToString()}»: الكمية صفر — ألغِ التحديد أو أدخل كمية.");
            var orderedCtn = Db.ProductionOrderItems.AsNoTracking()
                .Where(x => x.PlanItemId == it.Id)
                .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x, o })
                .Where(z => z.o.Status != DocStatuses.Cancelled)
                .Sum(z => z.x.PlannedCartons);
            int remainingCtn = it.PlannedCartons - orderedCtn;
            if (l.Cartons > remainingCtn)
                return OpResult.Fail(
                    $"⛔ الكمية تتجاوز المتبقي لبند «{Db.Products.AsNoTracking().FirstOrDefault(p => p.Id == it.ProductId)?.ProductNameAr ?? "-"}».\n" +
                    $"المتبقي: {remainingCtn:N0} كرتون — المخطط: {it.PlannedCartons:N0} | أوامر سابقة: {orderedCtn:N0}.");
            selected.Add((it, l.Cartons));
        }

        // §B102 (إصلاح علة B98): لا معاملة خارجية — SaveOrder/ApproveOrder معاملتان مستقلتان،
        // وRunOp الخارجي كان يفجر «The connection is already in a transaction».
        // كل مجموعة تُعالَج بذاتها، والنتيجة تُبلغ عن النجاح والجزئي بصدق.
        try
        {
            var customers = Db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
            var shifts = Db.Shifts.AsNoTracking().ToDictionary(s => s.Id, s => s.ShiftNameAr);
            var linesD = Db.ProductionLines.AsNoTracking().ToDictionary(l => l.Id, l => l.LineNameAr);

            // أمر واحد لكل (وردية×خط×عميل) — نفس شكل أوامر B93 نفسها، لكن بالكميات التي حددها المدير يدوياً
            var groups = selected
                .GroupBy(v => (Shift: v.Item.SuggestedShiftId, Line: v.Item.SuggestedLineId, Cust: v.Item.CustomerId))
                .ToList();

            var created = new List<DayRunOrderDto>();
            var failures = new List<string>();
            foreach (var g in groups)
            {
                var orderItems = g.Select(v => new OrderItemDto
                {
                    PlanItemId = v.Item.Id,
                    LotId = v.Item.LotId,
                    ShipmentId = v.Item.ShipmentId,
                    CustomerId = v.Item.CustomerId,
                    ProductId = v.Item.ProductId,
                    PackagingTypeId = v.Item.PackagingTypeId,
                    PlannedCartons = v.Cartons,
                    PlannedQtyKg = v.Cartons * UnitsPolicy.CartonWeight(Db, v.Item.ProductId, v.Item.PackagingTypeId)
                }).ToList();

                var r = _orders.SaveOrder("FromPlan", planId, g.Key.Cust, day.ToString("yyyy-MM-dd"), g.Key.Shift, g.Key.Line, orderItems);
                if (!r.Ok) { failures.Add(r.Message); continue; }
                var ap = _orders.ApproveOrder(r.Id); // صرف المواد + «مجدول» — جاهز للبدء
                if (!ap.Ok) { failures.Add($"{r.DocumentNumber}: {ap.Message}"); }

                created.Add(new DayRunOrderDto
                {
                    OrderId = r.Id,
                    OrderNumber = r.DocumentNumber,
                    CustomerName = g.Key.Cust != null && customers.TryGetValue(g.Key.Cust.Value, out var cn) ? cn : "—",
                    ShiftName = g.Key.Shift != null && shifts.TryGetValue(g.Key.Shift.Value, out var sn) ? sn : "—",
                    LineName = g.Key.Line != null && linesD.TryGetValue(g.Key.Line.Value, out var ln) ? ln : "—",
                    TotalCartons = orderItems.Sum(i => i.PlannedCartons),
                    TotalKg = orderItems.Sum(i => i.PlannedQtyKg)
                });
            }

            var nums = string.Join("، ", created.Select(c => c.OrderNumber));
            if (created.Count == 0)
                return OpResult.Fail(failures.Count > 0
                    ? "لم يُنشأ أي أمر:\n- " + string.Join("\n- ", failures)
                    : "لم يُنشأ أي أمر.");
            string okMsg = $"✅ تم إنشاء {(created.Count == 1 ? "أمر تشغيل" : created.Count + " أوامر تشغيل")} لليوم {day:dd/MM}:\n{nums}\nالأوامر «مجدولة» — ابدأ التشغيل من مهمة كل أمر.";
            if (failures.Count > 0)
                return OpResult.Fail(okMsg + "\n\n⚠ لكن فشلت مجموعات أخرى:\n- " + string.Join("\n- ", failures));
            return OpResult.Success(okMsg, 0, nums);
        }
        catch (DomainException ex) { return OpResult.Fail(ex.Message); }
    }
}
