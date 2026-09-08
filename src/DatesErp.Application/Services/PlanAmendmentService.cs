using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §تعديلات العملاء أثناء التنفيذ — تنفيذ مسار تعديل الخطة المعتمدة (Change Request → Revision).
///
/// ### لماذا مسار مستقل بدل التعديل في مكانه؟
/// تعديل صنف خطة بدأ تنفيذها يجب أن يكون حدثاً موثّقاً لا تعديلاً صامتاً: الخطة الأصلية
/// تبقى محفوظة، والكمية المنفذة محمية، والمتبقي يُوقَف، ويُنشأ إصدار جديد (Revision)
/// بالصنف الجديد — ويُفحص أثر التعديل على الطاقة والمخزون قبل الاعتماد.
/// </summary>
public class PlanAmendmentService : ServiceBase, IPlanAmendmentService
{
    private readonly IAuditService _audit;
    private readonly PlanningService _planning;

    public PlanAmendmentService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IAuditService audit, PlanningService planning)
        : base(db, session, numbering)
    {
        _audit = audit;
        _planning = planning;
    }

    // ═══════════════════ الطلب ═══════════════════

    /// <summary>
    /// يطلب تعديل خطة معتمدة: يوثّق كل التفاصيل (قديم/جديد/منفذ/متبقي/السبب/من طلب ومتى)
    /// في مسودة تعديل تنتظر الاعتماد. لا يمسّ الخطة الأصلية ولا البنود.
    /// </summary>
    public OpResult RequestAmendment(PlanAmendmentRequestDto dto)
    {
        Require("planning", "Edit");
        if (dto == null) return OpResult.Fail("لا توجد بيانات طلب تعديل.");
        if (string.IsNullOrWhiteSpace(dto.Reason))
            return OpResult.Fail("سبب التعديل إلزامي — يُسجَّل في سجل التعديل للتدقيق.");

        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == dto.OriginalPlanId);
        if (plan == null) return OpResult.Fail("الخطة الأصلية غير موجودة.");
        if (!plan.IsApproved) return OpResult.Fail("الخطة الأصلية غير معتمدة — لا يمكن طلب تعديل عليها.");
        if (plan.IsClosed) return OpResult.Fail("الخطة الأصلية مقفلة — لا يمكن تعديلها.");

        var item = plan.Items.FirstOrDefault(i => i.Id == dto.PlanItemId);
        if (item == null) return OpResult.Fail("بند الخطة المستهدف غير موجود في الخطة الأصلية.");
        if (item.IsClosed) return OpResult.Fail("بند الخطة موقوف/مقفل سابقاً — لا يمكن تعديله.");

        // §منع ازدواج الطلبات: لا طلب جديد على بند له طلب معلّق (اعتُمد أو ارفض أولاً)
        if (Db.PlanAmendments.Any(a => a.PlanItemId == item.Id && a.Status == DocStatuses.Draft))
            return OpResult.Fail("يوجد طلب تعديل معلّق على هذا البند — اعتمده أو ارفضه أولاً.");

        var newProd = Db.Products.AsNoTracking().FirstOrDefault(p => p.Id == dto.NewProductId && p.IsActive);
        if (newProd == null) return OpResult.Fail("الصنف الجديد غير موجود أو موقوف.");
        if (dto.NewProductId == item.ProductId)
            return OpResult.Fail("الصنف الجديد مطابق للقديم — لا حاجة لتعديل.");

        // §التتبع: الصنف الجديد يجب أن يلتزم بالتحويل الرسمي لدفعة البند إن وُجدت
        ProductIdentityGuard.EnsureConversionAllowed(Db, dto.NewProductId, item.LotId);

        // §الملكية: دفعة البند ملك عميلها — لا تعديل لعميل آخر
        if (item.LotId != null && item.CustomerId != null)
        {
            var owner = Db.Lots.AsNoTracking().Where(l => l.Id == item.LotId).Select(l => l.CustomerId).FirstOrDefault();
            if (owner != null && owner != item.CustomerId)
                return OpResult.Fail("دفعة البند مملوكة لعميل آخر — لا يمكن تعديلها لعميل مختلف.");
        }

        double executed = item.ProducedQtyKg;
        double remaining = Math.Max(0, item.PlannedQtyKg - executed);
        string execState = executed <= 0.001 ? "NotStarted" : (remaining <= 0.001 ? "Completed" : "Partial");
        double newQty = dto.NewQtyKg is double nq && nq > 0 ? nq : remaining;
        if (newQty <= 0) return OpResult.Fail("لا توجد كمية متبقية لتحويلها — البند أُنتج بالكامل.");

        return RunOp(() =>
        {
            var am = new PlanAmendment
            {
                DocumentNumber = Numbering.Next("AMD"),
                OriginalPlanId = plan.Id,
                PlanItemId = item.Id,
                SourceOrderId = dto.SourceOrderId,
                CustomerId = item.CustomerId ?? plan.SingleCustomerId,
                LotId = item.LotId,
                ShipmentId = item.ShipmentId,
                OldProductId = item.ProductId,
                NewProductId = dto.NewProductId,
                NewPackagingTypeId = dto.NewPackagingTypeId ?? item.PackagingTypeId,
                OldQtyKg = item.PlannedQtyKg,
                ExecutedQtyKg = executed,
                RemainingQtyKg = remaining,
                NewQtyKg = newQty,
                Reason = dto.Reason.Trim(),
                ExecutionState = execState,
                RequestedAt = DateTime.Now,
                RequestedBy = Session?.UserId,
                Status = DocStatuses.Draft
            };
            Db.PlanAmendments.Add(am);
            Db.SaveChanges();

            _audit.Log("تعديلات خطط الإنتاج", "طلب تعديل", "PlanAmendment", am.DocumentNumber, am.Id,
                new { item.ProductId, item.PlannedQtyKg },
                new { am.NewProductId, am.NewQtyKg, am.Reason });

            return OpResult.Success(
                $"سُجّل طلب تعديل الخطة {plan.DocumentNumber} — بانتظار الاعتماد.\n" +
                $"المنفذ: {executed:N1} كجم | المتبقي: {remaining:N1} كجم | الكمية الجديدة للصنف الجديد: {newQty:N1} كجم.",
                am.Id, am.DocumentNumber);
        });
    }

    // ═══════════════════ الاعتماد ═══════════════════

    /// <summary>
    /// يعتمد التعديل ذرّياً:
    /// 1) يجمد المتبقي من البند الأصلي (IsClosed + ReleasedQtyKg) مع حماية المنفذ.
    /// 2) يفحص الطاقة للكمية الجديدة (البند الأصلي المقفل لا يشغل طاقة بعد الآن).
    /// 3) يُنشئ إصداراً جديداً من الخطة (Revision) بالصنف الجديد والكمية المعتمدة.
    /// 4) يعيد احتساب حجوزات الدفعة ويسجّل الاعتماد في سجل التدقيق.
    /// </summary>
    public OpResult ApproveAmendment(int amendmentId)
    {
        Require("planning", "Approve");
        var am = Db.PlanAmendments.FirstOrDefault(a => a.Id == amendmentId);
        if (am == null) return OpResult.Fail("طلب التعديل غير موجود.");
        if (am.Status == DocStatuses.Cancelled) return OpResult.Fail("طلب التعديل مرفوض — لا يمكن اعتماده.");
        if (am.Status == DocStatuses.Approved) return OpResult.Fail("طلب التعديل معتمد مسبقاً.");
        if (am.PlanItemId == null) return OpResult.Fail("طلب التعديل بلا بند محدد — لا يمكن اعتماده.");

        // §كل القراءة والتحقق داخل المعاملة الذرّية (لا فجوة بين فحص البند وتجميده)
        return RunOp(() =>
        {
            var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == am.OriginalPlanId)
                       ?? throw new DomainException("الخطة الأصلية غير موجودة.");
            var item = Db.ProductionPlanItems.AsNoTracking().FirstOrDefault(i => i.Id == am.PlanItemId)
                       ?? throw new DomainException("بند الخطة المستهدف غير موجود.");

            // §حماية المنفذ: يُقرأ من البند (مصدر الحقيقة) لا من حقول التعديل المخزّنة سابقاً
            double executed = item.ProducedQtyKg;
            double remaining = Math.Max(0, item.PlannedQtyKg - executed);
            double newQty = am.NewQtyKg > 0 ? am.NewQtyKg : remaining;
            if (newQty <= 0) throw new DomainException("لا توجد كمية متبقية لتحويلها — البند أُنتج بالكامل.");

            // 1) مطالبة ذرّية بالبند: تجميد المتبقي الأصلي بشرط «ما زال غير مقفل».
            //    إن اعتمد مستخدم آخر تعديلاً على نفس البند لحظة التنفيذ تُرجع 0 صف وتُرفض العملية
            //    كاملة (لا تعديل مزدوج متزامن). لا يُمَسّ المنفذ ولا تُعاد الكمية المنتجة للخطة.
            int claimed = Db.ProductionPlanItems
                .Where(i => i.Id == am.PlanItemId && !i.IsClosed)
                .ExecuteUpdate(s => s
                    .SetProperty(i => i.IsClosed, true)
                    .SetProperty(i => i.ReleasedQtyKg, remaining));
            if (claimed == 0)
                throw new DomainException("بند الخطة أُوقف/عُدّل أثناء المراجعة — أعِد فتح الطلب والمحاولة.");

            // 2) فحص الطاقة للكمية الجديدة (البند الأصلي أُغلق فحرّر طاقته تلقائياً)
            int cartons = DeriveCartons(am.NewProductId, am.NewPackagingTypeId, newQty);
            string capErr = CheckReplacementCapacity(am.NewProductId, am.NewPackagingTypeId, cartons,
                item.SuggestedShiftId ?? plan.ShiftId ?? 1, item.SuggestedLineId ?? plan.LineId ?? 1,
                item.ScheduledDate ?? plan.StartDate ?? DateTime.Today);
            if (capErr != null) throw new DomainException(capErr); // rollback يعيد البند غير مقفل

            // 3) إنشاء الإصدار الجديد (Revision) بالصنف الجديد
            var rev = new ProductionPlan
            {
                DocumentNumber = Numbering.Next("PLAN"),
                PlanTitle = plan.PlanTitle + $" — تعديل {am.DocumentNumber}",
                PlanType = plan.PlanType,
                ScopeMode = plan.ScopeMode,
                SingleCustomerId = plan.SingleCustomerId,
                StartDate = plan.StartDate,
                EndDate = plan.EndDate,
                ShiftId = plan.ShiftId,
                LineId = plan.LineId,
                RevisionNo = plan.RevisionNo + 1,
                ParentPlanId = plan.Id,
                Status = DocStatuses.Approved,
                IsApproved = true,
                ApprovedBy = Session?.UserId,
                ApprovedDate = DateTime.Now,
                Notes = $"إصدار تعديل {am.DocumentNumber} عن الخطة {plan.DocumentNumber} — {am.Reason}"
            };
            Db.ProductionPlans.Add(rev);
            Db.SaveChanges();

            var revItem = new ProductionPlanItem
            {
                PlanId = rev.Id,
                SourceType = item.SourceType,
                LotId = item.LotId,
                ShipmentId = item.ShipmentId,
                ReceiptUnit = item.ReceiptUnit,
                CustomerId = item.CustomerId,
                ProductId = am.NewProductId,
                PackagingTypeId = am.NewPackagingTypeId ?? item.PackagingTypeId,
                PlannedQtyKg = newQty,
                PlannedCartons = cartons,
                ScheduledDate = item.ScheduledDate,
                SuggestedShiftId = item.SuggestedShiftId,
                SuggestedLineId = item.SuggestedLineId,
                PriorityNo = item.PriorityNo,
                Status = DocStatuses.Approved
            };
            rev.Items.Add(revItem);
            Db.SaveChanges();

            // 4) إعادة احتساب حجوزات الدفعة (تحرير المتبقي الأصلي + حجز الجديد)
            if (item.LotId is int lid) _planning.RecomputeLotReservations(new List<int> { lid });

            // 5) تحديث التعديل: معتمد ومطبّق
            am.Status = DocStatuses.Approved;
            am.IsApproved = true;
            am.ApprovedBy = Session?.UserId;
            am.ApprovedDate = DateTime.Now;
            am.AppliedAt = DateTime.Now;
            am.NewRevisionPlanId = rev.Id;
            am.ExecutedQtyKg = executed;
            am.RemainingQtyKg = remaining;
            am.NewQtyKg = newQty;
            Db.SaveChanges();

            _audit.Log("تعديلات خطط الإنتاج", "اعتماد تعديل", "PlanAmendment", am.DocumentNumber, am.Id,
                new { item.ProductId, item.PlannedQtyKg, executed },
                new { am.NewProductId, newQty, rev.DocumentNumber });

            return OpResult.Success(
                $"اعتُمد التعديل {am.DocumentNumber}.\n" +
                $"✅ المخطط الأصلي {item.PlannedQtyKg:N1} كجم محفوظ، والمنفذ {executed:N1} كجم محمي.\n" +
                $"⏹ أُوقف المتبقي {remaining:N1} كجم من الصنف السابق.\n" +
                $"📄 أُنشئ الإصدار الجديد {rev.DocumentNumber} (الإصدار {rev.RevisionNo}) بالصنف الجديد {newQty:N1} كجم.",
                am.Id, am.DocumentNumber);
        });
    }

    // ═══════════════════ الرفض ═══════════════════

    public OpResult RejectAmendment(int amendmentId, string reason)
    {
        Require("planning", "Approve");
        var am = Db.PlanAmendments.FirstOrDefault(a => a.Id == amendmentId);
        if (am == null) return OpResult.Fail("طلب التعديل غير موجود.");
        if (am.Status == DocStatuses.Approved) return OpResult.Fail("طلب التعديل معتمد — لا يمكن رفضه.");
        if (string.IsNullOrWhiteSpace(reason)) return OpResult.Fail("سبب الرفض إلزامي.");

        return RunOp(() =>
        {
            am.Status = DocStatuses.Cancelled;
            am.StatusReason = reason.Trim();
            Db.SaveChanges();
            _audit.Log("تعديلات خطط الإنتاج", "رفض تعديل", "PlanAmendment", am.DocumentNumber, am.Id, null, reason);
            return OpResult.Success("رُفض طلب التعديل وبقي مسجلاً للتدقيق.", am.Id, am.DocumentNumber);
        });
    }

    // ═══════════════════ السجل ═══════════════════

    /// <summary>
    /// سلسلة تعديلات خطة — التاريخ الكامل عبر شجرة الإصدارات:
    /// يجمع الخطة المطلوبة + والدها (لأعلى) + كل أبنائها (لأسفل) ثم كل التعديلات التي تمسّها.
    /// الفائدة: فتح إصدار v3 يعرض تعديلاته وتعديلات أسلافه (v1←v2←v3) دفعة واحدة.
    /// </summary>
    public List<PlanAmendmentDto> GetPlanHistory(int planId)
    {
        // جمع كل معرّفات الخطط في الشجرة (سعة صغيرة — توقف آمن ضد الحلقات بفضل HashSet)
        var planIds = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(planId);
        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            if (!planIds.Add(pid)) continue;
            var parent = Db.ProductionPlans.AsNoTracking().Where(p => p.Id == pid).Select(p => p.ParentPlanId).FirstOrDefault();
            if (parent != null) queue.Enqueue(parent.Value);
            foreach (var child in Db.ProductionPlans.AsNoTracking().Where(p => p.ParentPlanId == pid).Select(p => p.Id))
                queue.Enqueue(child);
        }

        // كل التعديلات التي تمسّ أي خطة في الشجرة (طلب عليها أو أنتجها)
        var ams = Db.PlanAmendments.AsNoTracking()
            .Where(a => planIds.Contains(a.OriginalPlanId) || planIds.Contains(a.NewRevisionPlanId ?? 0))
            .OrderBy(a => a.Id).ToList();
        return ToDtos(ams);
    }

    /// <summary>
    /// تحويل دفعة تعديلات إلى DTO بستة استعلامات فقط مهما بلغ عدد التعديلات —
    /// بدل استعلام لكل حقل لكل تعديل (كان سيتحول إلى عشرات الاستعلامات N+1).
    /// </summary>
    private List<PlanAmendmentDto> ToDtos(List<PlanAmendment> ams)
    {
        if (ams.Count == 0) return new List<PlanAmendmentDto>();

        var productIds = ams.SelectMany(a => new[] { a.OldProductId, a.NewProductId }).Distinct().ToList();
        var customerIds = ams.Where(a => a.CustomerId != null).Select(a => a.CustomerId.Value).Distinct().ToList();
        var lotIds = ams.Where(a => a.LotId != null).Select(a => a.LotId.Value).Distinct().ToList();
        var userIds = ams.SelectMany(a => new int?[] { a.RequestedBy, a.ApprovedBy })
                         .Where(id => id != null).Select(id => id.Value).Distinct().ToList();
        var planIds = ams.SelectMany(a => new int?[] { a.OriginalPlanId, a.NewRevisionPlanId })
                         .Where(id => id != null).Select(id => id.Value).Distinct().ToList();

        var products = Db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.ProductNameAr);
        var customers = Db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionary(c => c.Id, c => c.CustomerName);
        var lots = Db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionary(l => l.Id, l => l.LotCode);
        var users = Db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionary(u => u.Id, u => u.FullName);
        var plans = Db.ProductionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p);

        return ams.Select(a =>
        {
            plans.TryGetValue(a.OriginalPlanId, out var oldPlan);
            plans.TryGetValue(a.NewRevisionPlanId ?? 0, out var revPlan);
            return new PlanAmendmentDto
            {
                Id = a.Id,
                DocumentNumber = a.DocumentNumber,
                OriginalPlanId = a.OriginalPlanId,
                OriginalPlanNumber = oldPlan?.DocumentNumber ?? "-",
                PlanItemId = a.PlanItemId,
                SourceOrderId = a.SourceOrderId,
                CustomerId = a.CustomerId,
                CustomerName = a.CustomerId != null && customers.TryGetValue(a.CustomerId.Value, out var cn) ? cn : "-",
                LotId = a.LotId,
                LotCode = a.LotId != null && lots.TryGetValue(a.LotId.Value, out var lc) ? lc : null,
                ShipmentId = a.ShipmentId,
                OldProductId = a.OldProductId,
                OldProductName = products.TryGetValue(a.OldProductId, out var op) ? op : "-",
                NewProductId = a.NewProductId,
                NewProductName = products.TryGetValue(a.NewProductId, out var np) ? np : "-",
                OldQtyKg = a.OldQtyKg,
                ExecutedQtyKg = a.ExecutedQtyKg,
                RemainingQtyKg = a.RemainingQtyKg,
                NewQtyKg = a.NewQtyKg,
                Reason = a.Reason,
                ExecutionState = a.ExecutionState,
                Status = a.Status,
                StatusAr = a.Status == DocStatuses.Approved ? "معتمد" : a.Status == DocStatuses.Cancelled ? "مرفوض" : "مسودة",
                RequestedAt = UiFormat.DT(a.RequestedAt),
                RequestedBy = a.RequestedBy != null && users.TryGetValue(a.RequestedBy.Value, out var rb) ? rb : "-",
                ApprovedBy = a.ApprovedBy != null && users.TryGetValue(a.ApprovedBy.Value, out var ab) ? ab : "-",
                ApprovedAt = UiFormat.DT(a.ApprovedDate),
                AppliedAt = UiFormat.DT(a.AppliedAt),
                NewRevisionPlanId = a.NewRevisionPlanId,
                NewRevisionPlanNumber = revPlan?.DocumentNumber ?? "-",
                RevisionNo = revPlan?.RevisionNo ?? 0
            };
        }).ToList();
    }

    // ═══════════════════ أدوات ═══════════════════

    /// <summary>اشتقاق الكراتين من الوزن الجديد ووزن الكرتون المعرَّف — رفض صريح إن غاب التعريف.</summary>
    private int DeriveCartons(int productId, int? packagingTypeId, double qtyKg)
    {
        double weight = UnitsPolicy.CartonWeight(Db, productId, packagingTypeId);
        if (weight <= 0)
        {
            string name = Db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{productId}";
            throw new DomainException(
                $"⛔ وزن الكرتون غير معرَّف للصنف الجديد «{name}» — لا يمكن اشتقاق عدد الكراتين.\n" +
                "عرّف وزن الكرتون (أو القوالب × وزن القالب) في بطاقة الصنف أو العبوة أولاً.");
        }
        return Math.Max(1, (int)Math.Ceiling(qtyKg / weight));
    }

    /// <summary>
    /// فحص الطاقة للكمية الجديدة على نفس يوم/وردية/خط البند الأصلي — يعيد رسالة رفض أو null للقبول.
    /// يعتمد نفس محرك <see cref="PlanningService.ShiftUsageHours"/> (يراعي العبوة، ويستثني البنود
    /// المقفلة والخطط المغلقة) — والبند الأصلي أُغلق قبل هذا الفحص فحرّر طاقته تلقائياً.
    /// </summary>
    private string CheckReplacementCapacity(int productId, int? packagingTypeId, int cartons, int shiftId, int lineId, DateTime date)
    {
        if (cartons <= 0) return null;
        var (usedHours, effHours, rate) = _planning.ShiftUsageHours(shiftId, lineId, date.ToString("dd/MM/yyyy"),
            productId, excludePlanId: null, packagingTypeId);

        if (rate <= 0) return null; // طاقة غير معرَّفة للصنف الجديد — تُقبل مع تنبيه (نفس سلوك النظام)
        double required = cartons / rate;
        if (usedHours + required <= effHours + 0.0001) return null;

        int avail = (int)Math.Floor(Math.Max(0, effHours - usedHours) * rate);
        int over = Math.Max(0, cartons - avail);
        return $"⛔ لا يمكن اعتماد التعديل: الطاقة الإنتاجية للوردية يوم {date:dd/MM/yyyy} لا تكفي.\n" +
               $"الطاقة المتاحة: {avail:N0} كرتون | المطلوب للصنف الجديد: {cartons:N0} كرتون | الزيادة: {over:N0} كرتون\n" +
               $"(الساعات: الإنتاجية {effHours:N1} | المستخدمة {usedHours:N1} | المطلوبة {required:N1})\n" +
               "أعد جدولة البند ليوم/وردية أخرى بها طاقة متبقية أو قلّل الكمية.";
    }
}
