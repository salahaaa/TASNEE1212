using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §B79 — «إقفال خطة الإنتاج»: المستوى الإجمالي فوق أوامر الإنتاج.
/// الخطة تُقفل فقط عندما تكون جميع أوامرها المطلوبة مقفلة؛ والإقفال معاملة ذرية واحدة
/// تُسجَّل في التدقيق بالمستخدم والوقت والحالتين، ومع الاستثناء تُوثَّق الأسباب والأوامر المفتوحة.
/// </summary>
public class PlanClosureService : ServiceBase, IPlanClosureService
{
    private readonly IAuditService _audit;

    public PlanClosureService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IAuditService audit)
        : base(db, session, numbering)
    {
        _audit = audit;
    }

    private static string OrderStateOf(ProductionOrder o) =>
        o.Status == DocStatuses.Cancelled ? "ملغى" :
        (o.IsClosed || o.Status == DocStatuses.Closed) ? "مقفل" :
        o.Status is DocStatuses.InProgress or DocStatuses.Stopped ? "قيد الإنتاج" :
        o.Status == DocStatuses.Completed ? "مكتمل" : "مفتوح";

    /// <summary>الأمر يمنع الإقفال إن كان مفتوحاً/قيد الإنتاج/مكتملاً بلا إقفال رسمي.</summary>
    private static bool IsBlocking(string state) => state is "مفتوح" or "قيد الإنتاج" or "مكتمل";

    public PlanClosureInfo GetInfo(int planId)
    {
        var info = new PlanClosureInfo { PlanId = planId };
        var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId);
        if (plan == null) { info.Blockers.Add("الخطة غير موجودة."); return info; }

        info.PlanNumber = plan.DocumentNumber;
        info.PlanTypeAr = plan.PlanType switch { "Daily" => "يومية", "Weekly" => "أسبوعية", "Monthly" => "شهرية", _ => "فترية" };
        info.StartDate = plan.StartDate?.ToString("dd/MM/yyyy") ?? "—";
        info.EndDate = plan.EndDate?.ToString("dd/MM/yyyy") ?? "—";
        info.IsClosed = plan.IsClosed;
        info.ClosedAt = plan.ClosedDate?.ToString("dd/MM/yyyy HH:mm") ?? "—";
        info.ClosedByName = plan.ClosedBy == null ? "—"
            : Db.Users.AsNoTracking().Where(u => u.Id == plan.ClosedBy).Select(u => u.FullName).FirstOrDefault() ?? "—";

        var orders = Db.ProductionOrders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.SourcePlanId == planId).OrderBy(o => o.Id).ToList();

        foreach (var o in orders)
        {
            var state = OrderStateOf(o);
            double planned = o.Items.Sum(i => i.PlannedQtyKg);
            double produced = o.Items.Sum(i => i.ProducedQtyKg);
            double closed = o.IsClosed ? produced : o.Items.Where(i => i.IsClosed).Sum(i => i.ProducedQtyKg);
            info.Orders.Add(new OrderClosureRow
            {
                OrderId = o.Id,
                OrderNumber = o.DocumentNumber,
                CustomerName = o.CustomerId == null ? "—"
                    : Db.Customers.AsNoTracking().Where(c => c.Id == o.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                ProductNames = string.Join("، ", o.Items.Select(i => i.ProductId).Distinct().Select(pid =>
                    Db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => p.ProductNameAr).FirstOrDefault() ?? "—")),
                Date = o.ProductionDate?.ToString("dd/MM/yyyy") ?? "—",
                ShiftName = o.ShiftId == null ? "—"
                    : Db.Shifts.AsNoTracking().Where(s => s.Id == o.ShiftId).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "—",
                Planned = planned,
                Produced = produced,
                Closed = closed,
                StateAr = state,
                IsCancelled = state == "ملغى"
            });

            switch (state)
            {
                case "مفتوح": info.OpenOrders++; break;
                case "قيد الإنتاج": info.InProgressOrders++; break;
                case "مكتمل": info.CompletedOrders++; break;
                case "مقفل": info.ClosedOrders++; break;
                case "ملغى": info.CancelledOrders++; break;
            }
            if (state != "ملغى")
            {
                info.PlannedTotal += planned;
                info.ProducedTotal += produced;
                info.ClosedTotal += closed;
                // §B83: فرق الأمر المقفل = فرق «معالج» حكماً — ما كان لِيُقفل الأمر لولا تسويته
                // (إعادة المتبقي للمخزن بحركة مرتجع موثقة أو إتمام الإنتاج) حسب قواعد النظام.
                if (state == "مقفل") info.SettledVariance += Math.Max(0, planned - produced);
                if (IsBlocking(state))
                    info.Blockers.Add(state == "مقفل" ? "" :
                        $"أمر {o.DocumentNumber} — {state}" + (produced < planned - 0.001 ? $" (المنفذ {produced:N0} من {planned:N0})" : ""));
                if (produced > planned + 0.001)
                    info.Blockers.Add($"أمر {o.DocumentNumber} — الكمية الفعلية أكبر من المخطط ({produced:N0} > {planned:N0}).");
            }
        }
        info.Blockers.RemoveAll(string.IsNullOrEmpty);
        info.TotalOrders = orders.Count;
        info.Remaining = Math.Max(0, info.PlannedTotal - info.ProducedTotal);
        // §B83: الأوامر غير المعالجة — المكتمل وحده لا يكفي حتى يُقفل رسمياً (§4 من المواصفة)
        info.UnprocessedOrders = info.OpenOrders + info.InProgressOrders + info.CompletedOrders;

        // ملخصات العملاء والأصناف — التتبع لا يُدمج
        // §B102 — ملخص العملاء من بنود الخطة نفسها (لا من ترويسات الأوامر): هوية العميل على البند،
        // فالأمر الواحد قد يكون متعدد العملاء وترويسته فارغة — وكانت الصفوف تخرج باسم «—».
        // وتشمل المقبول/المسلَّم (قيم مُزامَنة على البنود) — دمج إصلاح B100 مع تعدد العملاء.
        var custPlanItems = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.PlanId == planId && i.CustomerId != null).ToList();
        var custNameById = Db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
        foreach (var g in custPlanItems.GroupBy(i => i.CustomerId.Value).OrderBy(g => g.Key))
        {
            double planned = g.Sum(i => i.PlannedQtyKg);
            double produced = g.Sum(i => i.ProducedQtyKg);
            info.Customers.Add(new ClosureSummaryRow
            {
                Name = custNameById.TryGetValue(g.Key, out var cnn) ? cnn : $"#{g.Key}",
                Planned = planned,
                Produced = produced,
                Accepted = g.Sum(i => i.AcceptedQtyKg),
                Delivered = g.Sum(i => i.DeliveredQtyKg),
                Closed = g.Where(i => i.IsClosed).Sum(i => i.ProducedQtyKg),
                Remaining = Math.Max(0, planned - produced),
                StateAr = g.All(i => i.IsClosed) ? "مقفل ✔" : "غير مكتمل"
            });
        }
        foreach (var g in info.Orders.Where(o => !o.IsCancelled)
                   .SelectMany(o => o.ProductNames.Split("، ").Select(p => new { p, o })))
        {
            var e = info.Products.FirstOrDefault(x => x.Name == g.p);
            if (e == null) info.Products.Add(e = new ClosureSummaryRow { Name = g.p });
            e.Planned += g.o.Planned; e.Produced += g.o.Produced; e.Closed += g.o.Closed;
            e.Remaining = Math.Max(0, e.Planned - e.Produced);
            e.StateAr = g.o.StateAr == "مقفل" ? "مقفل ✔" : "غير مكتمل";
        }

        // حالة الخطة المشتقة (مسودة→معتمدة→قيد التنفيذ→مكتملة جزئياً→مكتملة→مقفلة / ملغاة)
        info.StatusAr =
            plan.Status == DocStatuses.Cancelled ? "ملغاة" :
            plan.IsClosed ? "مقفلة" :
            info.TotalOrders > 0 && info.ClosedOrders + info.CancelledOrders == info.TotalOrders && info.ClosedOrders > 0 ? "مكتملة" :
            info.ClosedOrders > 0 || info.InProgressOrders > 0 ? (info.ProducedTotal > 0 && info.ClosedOrders == 0 ? "قيد التنفيذ" : "مكتملة جزئيًا") :
            plan.IsApproved ? "معتمدة" : "مسودة";

        if (!plan.IsApproved) info.Blockers.Add("الخطة غير معتمدة.");
        if (plan.Status == DocStatuses.Cancelled) info.Blockers.Add("الخطة ملغاة.");
        if (plan.IsClosed) info.Blockers.Add("الخطة مقفلة مسبقاً.");
        info.CanClose = plan.IsApproved && !plan.IsClosed && plan.Status != DocStatuses.Cancelled && info.Blockers.Count == 0;
        return info;
    }

    public OpResult ClosePlanFinal(int planId, string reason = null, bool force = false)
    {
        Require("planning", "Approve");
        return RunOp(() =>
        {
            var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId)
                       ?? throw new DomainException("الخطة غير موجودة.");
            var info = GetInfo(planId);
            if (plan.IsClosed) throw new DomainException("الخطة مقفلة مسبقاً — لا يمكن إقفالها مرتين.");
            if (plan.Status == DocStatuses.Cancelled) throw new DomainException("الخطة ملغاة — لا يمكن إقفالها.");
            if (!plan.IsApproved) throw new DomainException("الخطة غير معتمدة — اعتمدها أولاً.");
            if (info.Blockers.Count > 0)
            {
                if (!force)
                    throw new DomainException(
                        $"لا يمكن إقفال الخطة لوجود {info.Blockers.Count} مانع:\n- " + string.Join("\n- ", info.Blockers));
                if (string.IsNullOrWhiteSpace(reason))
                    throw new DomainException("الإقفال الاستثنائي يتطلب سبباً مكتوباً يُسجَّل في التدقيق.");
            }

            var oldStatus = info.StatusAr;
            plan.IsClosed = true;
            plan.ClosedDate = DateTime.Now;
            plan.ClosedBy = Session?.UserId;
            plan.Status = DocStatuses.Closed;
            // §B79: تحرير حجوزات دفعات الخطة المقفلة (المستهلك فقط يبقى محجوزاً حتى الصرف)
            foreach (var lid in plan.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value).Distinct().ToList())
            {
                var lot = Db.Lots.FirstOrDefault(l => l.Id == lid);
                if (lot == null) continue;
                double reserved = Db.ProductionPlanItems
                    .Where(i => i.LotId == lid && i.PlanId != plan.Id)
                    .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                    .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed && !x.i.IsClosed)
                    .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg);
                lot.ReservedQtyKg = Math.Max(0, reserved);
            }
            Db.SaveChanges();

            _audit.Log("إقفال خطة الإنتاج", force ? "إقفال استثنائي" : "إقفال", "Plan", plan.DocumentNumber, plan.Id,
                new { الحالة_السابقة = oldStatus },
                new
                {
                    الحالة_الجديدة = "مقفلة",
                    استثناء = force,
                    السبب = reason,
                    الأوامر_غير_المكتملة = info.Blockers,
                    إجمالي_الكمية = info.PlannedTotal,
                    المنتَج = info.ProducedTotal,
                    المتبقي = info.Remaining,
                    عدد_الأوامر = info.TotalOrders
                });
            return OpResult.Success(
                force ? $"تم إقفال الخطة {plan.DocumentNumber} إقفالاً استثنائياً موثقاً بالسبب." :
                        $"تم إقفال الخطة {plan.DocumentNumber} رسمياً — جميع الأوامر المطلوبة مقفلة.");
        });
    }

    public OpResult ReopenPlan(int planId, string reason)
    {
        // §B84/S2: تفعيل صلاحية Reopen الميتة في الكتالوج — المنح التلقائي لحاملي الاعتماد
        // يتم في PermissionService.GrantReopenToApprovers عند كل إقلاع (فلا إقفال للأدوار القائمة).
        Require("planning", "Reopen");
        if (string.IsNullOrWhiteSpace(reason))
            return OpResult.Fail("إعادة الفتح تتطلب سبباً مكتوباً يُسجَّل في التدقيق.");
        return RunOp(() =>
        {
            var plan = Db.ProductionPlans.FirstOrDefault(p => p.Id == planId)
                       ?? throw new DomainException("الخطة غير موجودة.");
            if (!plan.IsClosed) return OpResult.Fail("الخطة غير مقفلة — لا داعي لإعادة الفتح.");
            plan.IsClosed = false;
            plan.ClosedDate = null;
            plan.ClosedBy = null;
            plan.Status = DocStatuses.Approved;
            Db.SaveChanges();
            var recalculated = GetInfo(planId).StatusAr;
            _audit.Log("إقفال خطة الإنتاج", "إعادة فتح", "Plan", plan.DocumentNumber, plan.Id,
                new { الحالة_السابقة = "مقفلة" }, new { الحالة_الجديدة = recalculated, السبب = reason });
            return OpResult.Success($"أُعيد فتح الخطة {plan.DocumentNumber} — الحالة المحتسبة الآن: {recalculated}.");
        });
    }
}
