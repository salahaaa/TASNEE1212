using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§7 — خطط الإنتاج: حجز كميات الدفعات لكل الأصناف + فحص طاقة الورديات + رفض التجاوز.</summary>
public class PlanningService : ServiceBase, IPlanningService
{
    public PlanningService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    /// <summary>§B75: نهاية الفترة حسب النوع — يومية=البداية، أسبوعية=+6 أيام.</summary>
    public static DateTime PeriodEndDate(string planType, DateTime start)
        => planType switch
        {
            "Weekly" => start.AddDays(6),
            "Monthly" => start.AddMonths(1).AddDays(-1),   // §B77 شهرية: حتى نهاية الشهر
            _ => start
        };

    public OpResult SavePlan(string title, string planType, string startDate, string endDate, int? shiftId, int? lineId, List<PlanItemDto> items, string notes = null, string scopeMode = null, int? singleCustomerId = null)
    {
        Require("planning", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل في الخطة.");

        return RunOp(() =>
        {
            var plan = new ProductionPlan
            {
                DocumentNumber = Numbering.Next("PLAN"),
                PlanTitle = title,
                PlanType = planType ?? "Daily",
                ScopeMode = scopeMode ?? "Multi",
                SingleCustomerId = singleCustomerId,
                StartDate = UiFormat.TryParseDate(startDate, out var sd) ? sd : null,
                EndDate = UiFormat.TryParseDate(endDate, out var ed) ? ed : null,
                ShiftId = shiftId,
                LineId = lineId,
                Status = DocStatuses.Draft,
                Notes = notes
            };
            Db.ProductionPlans.Add(plan);
            Db.SaveChanges();

            // §الخطة الطويلة: لكل بند تاريخ إنتاج إلزامي داخل فترة الخطة.
            // بدونه كان البند بلا تاريخ يتخطى فحص الطاقة كلياً (الحارس يشترط ScheduledDate != null).
            // §B80: + رفض أي تاريخ بند خارج فترة الخطة.
            ApplyDefaultScheduledDates(items, sd, ed);
            // §B76: خطة العميل الواحد لا تقبل تسريب عملاء آخرين — فرض من الخلفية لا من الواجهة فقط
            if ((scopeMode ?? "Multi") == "Single" && singleCustomerId != null)
                foreach (var dtoC in items)
                    if (dtoC.CustomerId != singleCustomerId)
                        throw new DomainException("خطة العميل الواحد لا تقبل بنوداً لعميل آخر — كل البنود يجب أن تكون للعميل المحدد في الرأس.");

            // §حرج: حارس الطاقة يشترط وردية على البند، فكان أي بند بلا SuggestedShiftId
            // يتخطى الفحص كلياً وبصمت. البند يرث وردية الخطة وخطها إن لم يُحددا له.
            foreach (var dtoInh in items)
            {
                dtoInh.SuggestedShiftId ??= shiftId;
                dtoInh.SuggestedLineId ??= lineId;
            }
            // §إصلاح حرج: تراكم استهلاك بنود «هذه الخطة نفسها» على نفس اليوم/الوردية/الخط.
            var localUsed = new Dictionary<(DateTime day, int shift, int line), double>();
            var capWarnings = new List<string>(); // §B85/H4: تنبيهات الطاقة غير المعرَّفة

            foreach (var dto in items)
            {
                // §نظام الوحدات: بنود الخطة منتجات تامة فقط (002) — واتساق الكرتون/الكيلو إلزامي
                UnitsPolicy.RequireItemType(Db, dto.ProductId, "Finished", "خطة الإنتاج");
                dto.PlannedQtyKg = UnitsPolicy.EnsureCartonKgConsistency(Db, dto.ProductId, dto.PackagingTypeId,
                    dto.PlannedQtyKg, dto.PlannedCartons, "خطة الإنتاج");

                // §الإنتاج التام unitه الكرتون: إن أُدخل الوزن بلا كراتين تُشتق الكراتين من
                // وزن كرتون الصنف/العبوة المعرَّف — فتُفحص الطاقة دائماً ولا تُتخطى.
                if (dto.PlannedCartons <= 0 && dto.PlannedQtyKg > 0)
                {
                    double w = UnitsPolicy.CartonWeight(Db, dto.ProductId, dto.PackagingTypeId);
                    if (w > 0) dto.PlannedCartons = (int)Math.Round(dto.PlannedQtyKg / w);
                }

                // §قاعدة توازن الإنتاج: المخطط كمية منتج تام مستهدفة، لا حجزاً للخام.
                // وفي تصنيع التمور يزيد وزن الخارج عن الداخل لإضافة الماء أثناء التشغيل،
                // فالتخطيط بكمية أكبر من رصيد الخام مشروع — ولا معادلة ثابتة تربطهما.
                // الحجز الفعلي للخام يتم عند اعتماد الأمر، وهناك يُمنع تجاوز الرصيد فعلاً.
                if (dto.SourceType == "FromReceiving" && dto.LotId is int lotId)
                {
                    if (!Db.Lots.AsNoTracking().Any(l => l.Id == lotId))
                        throw new DomainException("الدفعة المحددة غير موجودة.");
                }

                // §تتبع الصنف: التحويل الرسمي — لا يخطط خلاص من دفعة سكري ولا سكري من دفعة خلاص
                ProductIdentityGuard.EnsureConversionAllowed(Db, dto.ProductId, dto.LotId);

                // §3/§8 — فحص الطاقة الإنتاجية للوردية في اليوم المحدد (لكل بند)
                // مع تراكم بنود هذه الخطة نفسها — لا يكفي استثناء الخطة من خطط الآخرين.
                if (EnsureSlotCapacity(plan, dto, localUsed) is string capW && !capWarnings.Contains(capW)) capWarnings.Add(capW);

                // §الملكية: دفعة العميل لا تُخطط إلا باسم صاحبها — لا دمج ولا تداخل
                if (dto.LotId is int lotOwn && dto.CustomerId is int custOwn)
                {
                    var lotOwner = Db.Lots.AsNoTracking().Where(l => l.Id == lotOwn).Select(l => l.CustomerId).FirstOrDefault();
                    if (lotOwner != null && lotOwner != custOwn)
                        throw new DomainException("الدفعة المحددة مملوكة لعميل آخر — لا يمكن تخطيطها لعميل مختلف.", "OWNERSHIP");
                }

                plan.Items.Add(new ProductionPlanItem
                {
                    PlanId = plan.Id,
                    SourceType = dto.SourceType,
                    LotId = dto.LotId,
                    ShipmentId = dto.ShipmentId ?? (dto.LotId != null ? Db.Lots.Where(l => l.Id == dto.LotId).Select(l => l.ShipmentId).FirstOrDefault() : null),
                    CustomerId = dto.CustomerId,
                    ProductId = dto.ProductId,
                    PackagingTypeId = dto.PackagingTypeId,
                    PlannedQtyKg = dto.PlannedQtyKg,
                    PlannedCartons = dto.PlannedCartons,
                    ScheduledDate = UiFormat.TryParseDate(dto.ScheduledDate, out var d) ? d : null,
                    SuggestedShiftId = dto.SuggestedShiftId,
                    SuggestedLineId = dto.SuggestedLineId,
                    PriorityNo = dto.PriorityNo,
                    Status = DocStatuses.Draft
                });
            }

            // حجز كميات الدفعات (لكل الأصناف) بمجرد إنشاء الخطة
            ApplyLotReservations(plan);
            Db.SaveChanges();
            string capMsg = capWarnings.Count > 0 ? "\n" + string.Join("\n", capWarnings) : "";
            // §B86/#16: تنبيه الخطة الطويلة — مراجعة الإشغال الأسبوعي قبل التوسع
            string longMsg = plan.StartDate != null && plan.EndDate != null
                && (plan.EndDate.Value.Date - plan.StartDate.Value.Date).TotalDays > 7
                ? "\n📅 تنبيه خطة طويلة (أكثر من 7 أيام): راجع إشغال الورديات أسبوعياً وتأكد من توزيع البنود على الأيام بدل تكديسها."
                : "";
            return OpResult.Success("تم حفظ خطة الإنتاج بنجاح." + capMsg + longMsg, plan.Id, plan.DocumentNumber);
        });
    }

    /// <summary>
    /// §تعديل خطة قائمة (مسودة غير معتمدة): يستبدل البنود ويعيد فحص الطاقة والأرصدة والحجوزات.
    /// يعالج حجز الخطة القديم صحيحاً (يُستثنى من فحص المتاح حتى لا يُحتسب مرتين).
    /// </summary>
    public OpResult UpdatePlan(int planId, string title, string planType, string startDate, string endDate, int? shiftId, int? lineId, List<PlanItemDto> items, string notes = null, string scopeMode = null, int? singleCustomerId = null)
    {
        Require("planning", "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("أضف بنداً واحداً على الأقل في الخطة.");

        return RunOp(() =>
        {
            var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId)
                       ?? throw new DomainException("الخطة غير موجودة.");
            if (plan.IsApproved) throw new DomainException("الخطة معتمدة — ألغِ الاعتماد أولاً للتعديل.");
            if (plan.IsClosed) throw new DomainException("الخطة مقفلة — لا يمكن التعديل.");

            plan.PlanTitle = title;
            plan.PlanType = planType ?? "Daily";
            plan.ScopeMode = scopeMode ?? plan.ScopeMode ?? "Multi";
            plan.SingleCustomerId = singleCustomerId;
            plan.StartDate = UiFormat.TryParseDate(startDate, out var sd) ? sd : null;
            plan.EndDate = UiFormat.TryParseDate(endDate, out var ed) ? ed : null;
            plan.ShiftId = shiftId;
            plan.LineId = lineId;
            plan.Notes = notes;

            // حذف البنود القديمة (ستُستبدل) — مع تحرير حجوزاتها ضمنياً بالاستثناء من الفحص
            Db.ProductionPlanItems.RemoveRange(plan.Items);
            plan.Items.Clear();

            // §الخطة الطويلة: لكل بند تاريخ إنتاج إلزامي داخل فترة الخطة.
            // §B80: + رفض أي تاريخ بند خارج فترة الخطة.
            ApplyDefaultScheduledDates(items, sd, ed);
            // §B76: خطة العميل الواحد لا تقبل تسريب عملاء آخرين — فرض من الخلفية لا من الواجهة فقط
            // §إصلاح ثغرة: كان الفحص على scopeMode **الوارد** لا على المحفوظ في الخطة، فاستدعاء
            // UpdatePlan بـ scopeMode=null على خطة Single قائمة يتخطى الحارس كلياً — مع أن
            // السطر أعلاه يُبقي plan.ScopeMode = "Single". النتيجة: بنود عميل آخر تدخل خطة
            // عميل واحد. نفحص الآن القيمة الفعلية بعد الدمج.
            if (plan.ScopeMode == "Single" && plan.SingleCustomerId != null)
                foreach (var dtoC in items)
                    if (dtoC.CustomerId != plan.SingleCustomerId)
                        throw new DomainException("خطة العميل الواحد لا تقبل بنوداً لعميل آخر — كل البنود يجب أن تكون للعميل المحدد في الرأس.");

            // §حرج: حارس الطاقة يشترط وردية على البند، فكان أي بند بلا SuggestedShiftId
            // يتخطى الفحص كلياً وبصمت. البند يرث وردية الخطة وخطها إن لم يُحددا له.
            foreach (var dtoInh in items)
            {
                dtoInh.SuggestedShiftId ??= shiftId;
                dtoInh.SuggestedLineId ??= lineId;
            }
            // §إصلاح حرج: تراكم استهلاك بنود «هذه الخطة نفسها» على نفس اليوم/الوردية/الخط.
            var localUsed = new Dictionary<(DateTime day, int shift, int line), double>();
            var capWarnings = new List<string>(); // §B85/H4: تنبيهات الطاقة غير المعرَّفة

            foreach (var dto in items)
            {
                // §نظام الوحدات: بنود الخطة منتجات تامة فقط (002) — واتساق الكرتون/الكيلو إلزامي
                UnitsPolicy.RequireItemType(Db, dto.ProductId, "Finished", "خطة الإنتاج");
                dto.PlannedQtyKg = UnitsPolicy.EnsureCartonKgConsistency(Db, dto.ProductId, dto.PackagingTypeId,
                    dto.PlannedQtyKg, dto.PlannedCartons, "خطة الإنتاج");

                // §الإنتاج التام unitه الكرتون: إن أُدخل الوزن بلا كراتين تُشتق الكراتين من
                // وزن كرتون الصنف/العبوة المعرَّف — فتُفحص الطاقة دائماً ولا تُتخطى.
                if (dto.PlannedCartons <= 0 && dto.PlannedQtyKg > 0)
                {
                    double w = UnitsPolicy.CartonWeight(Db, dto.ProductId, dto.PackagingTypeId);
                    if (w > 0) dto.PlannedCartons = (int)Math.Round(dto.PlannedQtyKg / w);
                }

                // §كما في SavePlan: المخطط مستهدف إنتاجي لا حجز خام — لا معادلة ثابتة
                if (dto.SourceType == "FromReceiving" && dto.LotId is int lotId)
                {
                    if (!Db.Lots.AsNoTracking().Any(l => l.Id == lotId))
                        throw new DomainException("الدفعة المحددة غير موجودة.");
                }

                // §تتبع الصنف: التحويل الرسمي — لا يخطط خلاص من دفعة سكري ولا سكري من دفعة خلاص
                ProductIdentityGuard.EnsureConversionAllowed(Db, dto.ProductId, dto.LotId);

                // §3/§8 — فحص الطاقة الإنتاجية للوردية في اليوم المحدد (باستثناء هذه الخطة)
                // مع تراكم بنود هذه الخطة نفسها — لا يكفي استثناء الخطة من خطط الآخرين.
                if (EnsureSlotCapacity(plan, dto, localUsed) is string capW && !capWarnings.Contains(capW)) capWarnings.Add(capW);

                // §الملكية: دفعة العميل لا تُخطط إلا باسم صاحبها
                if (dto.LotId is int lotOwn && dto.CustomerId is int custOwn)
                {
                    var lotOwner = Db.Lots.AsNoTracking().Where(l => l.Id == lotOwn).Select(l => l.CustomerId).FirstOrDefault();
                    if (lotOwner != null && lotOwner != custOwn)
                        throw new DomainException("الدفعة المحددة مملوكة لعميل آخر — لا يمكن تخطيطها لعميل مختلف.", "OWNERSHIP");
                }

                plan.Items.Add(new ProductionPlanItem
                {
                    PlanId = plan.Id,
                    SourceType = dto.SourceType,
                    LotId = dto.LotId,
                    ShipmentId = dto.ShipmentId ?? (dto.LotId != null ? Db.Lots.Where(l => l.Id == dto.LotId).Select(l => l.ShipmentId).FirstOrDefault() : null),
                    CustomerId = dto.CustomerId,
                    ProductId = dto.ProductId,
                    PackagingTypeId = dto.PackagingTypeId,
                    PlannedQtyKg = dto.PlannedQtyKg,
                    PlannedCartons = dto.PlannedCartons,
                    ScheduledDate = UiFormat.TryParseDate(dto.ScheduledDate, out var d) ? d : null,
                    SuggestedShiftId = dto.SuggestedShiftId,
                    SuggestedLineId = dto.SuggestedLineId,
                    PriorityNo = dto.PriorityNo,
                    Status = DocStatuses.Draft
                });
            }

            // إعادة احتساب الحجوزات بالبنود الجديدة
            ApplyLotReservations(plan);
            Db.SaveChanges();
            string capMsgU = capWarnings.Count > 0 ? "\n" + string.Join("\n", capWarnings) : "";
            string longMsgU = plan.StartDate != null && plan.EndDate != null
                && (plan.EndDate.Value.Date - plan.StartDate.Value.Date).TotalDays > 7
                ? "\n📅 تنبيه خطة طويلة (أكثر من 7 أيام): راجع إشغال الورديات أسبوعياً وتأكد من توزيع البنود على الأيام بدل تكديسها."
                : "";
            return OpResult.Success("تم تحديث خطة الإنتاج بنجاح." + capMsgU + longMsgU, plan.Id, plan.DocumentNumber);
        });
    }

    /// <summary>المتاح الفعلي من دفعة بعد خصم حجوزات الخطط النشطة والأوامر، مع استثناء خطة محددة.</summary>
    private double LotAvailableExcluding(int lotId, int? excludePlanId)
    {
        var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
        if (lot == null) return 0;
        double planCommitted = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.LotId == lotId && (excludePlanId == null || i.PlanId != excludePlanId))
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Where(x => !x.i.IsClosed)
            .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);
        double orderCommitted = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId == lotId)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
            .Where(x => !x.i.IsClosed)
            .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);
        // §المعالجة والتعقيم (الموضعان 8 و9): ما هو داخل دورة معالجة جارية ليس متاحاً
        return Math.Max(0, Math.Round(lot.InStockQtyKg - lot.UnderTreatmentQtyKg - planCommitted - orderCommitted, 3));
    }

    /// <summary>
    /// §المعالجة والتعقيم — يتحقق أن خام كل بند سيكون **جاهزاً في تاريخ إنتاج ذلك البند**.
    ///
    /// يُرجع رسالة الرفض أو <c>null</c> إن كان كل شيء سليماً. الرسالة تذكر
    /// **الرقم والسبب وأقرب موعد ممكن** — لأن المخطِّط يحتاج أن يعرف متى يستطيع،
    /// لا أن يُمنع وحسب.
    ///
    /// البنود بلا دفعة أو بلا تاريخ لا تُفحص: لا مرجع خام لها.
    /// </summary>
    private string CheckTreatmentReadiness(ProductionPlan plan)
    {
        // نجمع الطلب لكل (دفعة، تاريخ) حتى لا يمر بندان صغيران يتجاوزان معاً المتاح
        var demand = plan.Items
            .Where(i => i.LotId != null && i.ScheduledDate != null && !i.IsClosed)
            .GroupBy(i => (LotId: i.LotId.Value, Day: i.ScheduledDate.Value.Date))
            .Select(g => new { g.Key.LotId, g.Key.Day, Kg = g.Sum(x => x.PlannedQtyKg) })
            .ToList();
        if (demand.Count == 0) return null;

        var lotIds = demand.Select(d => d.LotId).Distinct().ToList();
        var lots = Db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToList();
        var gated = Db.Products.AsNoTracking()
            .Where(p => p.RequiresTreatment).Select(p => p.Id).ToHashSet();

        foreach (var d in demand.OrderBy(x => x.Day))
        {
            var lot = lots.FirstOrDefault(l => l.Id == d.LotId);
            if (lot == null || !gated.Contains(lot.ProductId)) continue; // صنف لا يشترط معالجة

            var end = d.Day.AddDays(1).AddTicks(-1);
            var live = Db.RawTreatments.AsNoTracking()
                .Where(t => t.LotId == lot.Id && t.Status == TreatmentStatuses.InProgress)
                .Select(t => new { t.ExpectedReadyAt, Kg = t.QtyKg - t.ReleasedQtyKg - t.RejectedQtyKg })
                .ToList();

            double maturing = live.Where(t => t.ExpectedReadyAt <= end).Sum(t => Math.Max(0, t.Kg));
            // §الحجوزات الأخرى تُطرح: خطة معتمدة أخرى التزام قائم لا يُنقض ضمناً
            double reservedOthers = Db.ProductionPlanItems.AsNoTracking()
                .Where(i => i.LotId == lot.Id && i.PlanId != plan.Id && !i.IsClosed)
                .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                .Sum(x => (double?)(x.i.PlannedQtyKg - x.i.ProducedQtyKg)) ?? 0;

            double availableForDate = Math.Max(0,
                lot.TreatmentReadyQtyKg + maturing - Math.Max(0, reservedOthers) - lot.ProducedQtyKg);
            if (d.Kg <= availableForDate + 0.001) continue;

            // أقرب تاريخ تكتمل فيه الكمية المطلوبة — البديل العملي بدل «غير كافٍ»
            string soonest = "لا توجد معالجة جارية تكفي — ابدأ معالجة الكمية الناقصة.";
            double cum = Math.Max(0, lot.TreatmentReadyQtyKg - Math.Max(0, reservedOthers) - lot.ProducedQtyKg);
            foreach (var t in live.OrderBy(t => t.ExpectedReadyAt))
            {
                cum += Math.Max(0, t.Kg);
                if (cum >= d.Kg - 0.001)
                {
                    soonest = $"أقرب موعد لاكتمال الكمية: {t.ExpectedReadyAt:dd/MM/yyyy}.";
                    break;
                }
            }

            return $"⛔ لا يمكن اعتماد الخطة: خام الدفعة {lot.LotCode} لن يكون جاهزاً يوم {d.Day:dd/MM/yyyy}.\n"
                 + $"المطلوب: {d.Kg:N1} كجم | الجاهز حالياً: {lot.TreatmentReadyQtyKg:N1} كجم"
                 + $" | المتوقع جاهزيته حتى ذلك التاريخ: {maturing:N1} كجم"
                 + (reservedOthers > 0.001 ? $" | المحجوز لخطط أخرى: {reservedOthers:N1} كجم" : "")
                 + $"\nالمتاح للتخطيط في ذلك التاريخ: {availableForDate:N1} كجم.\n{soonest}";
        }
        return null;
    }

    private void ApplyLotReservations(ProductionPlan plan)
    {
        // تصفير حجوزات هذه الخطة السابقة ثم إعادة الاحتساب — يدعم التعديل متعدد الأصناف
        var lotIds = plan.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value).Distinct().ToList();
        foreach (var lid in lotIds)
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lid);
            if (lot == null) continue;
            // الحجز = المتبقي غير المنتَج من مخطط الخطط النشطة (المنتَج استُهلك خامه أو أُنتج)
            double reserved = Db.ProductionPlanItems
                .Where(i => i.LotId == lid && i.PlanId != plan.Id)
                .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                .Where(x => !x.i.IsClosed)
                .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg);
            lot.ReservedQtyKg = Math.Max(0, reserved);
        }
        // §الخطة المقفلة/الملغاة تُحرر حجوزاتها — لا تُعاد إضافتها
        bool planStillActive = plan.Status != DocStatuses.Closed && plan.Status != DocStatuses.Cancelled && !plan.IsClosed;
        if (planStillActive)
        {
            foreach (var lid in plan.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value).Distinct())
            {
                var lot = Db.Lots.First(l => l.Id == lid);
                lot.ReservedQtyKg += Math.Max(0, plan.Items.Where(i => i.LotId == lid && !i.IsClosed).Sum(i => i.PlannedQtyKg - i.ProducedQtyKg));
            }
        }
    }

    /// <summary>
    /// §إعادة احتساب حجز دفعات بعينها **من الواقع** — بلا افتراض وجود خطة بذاتها.
    /// تلزم بعد حذف خطة: <see cref="ApplyLotReservations"/> تبني الحجز حول خطة قائمة،
    /// وهنا لم تعد قائمة، فيُجمع المتبقي من الخطط النشطة وحدها.
    /// </summary>
    private void RecomputeLotReservations(List<int> lotIds)
    {
        foreach (var lid in lotIds ?? new List<int>())
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lid);
            if (lot == null) continue;
            double reserved = Db.ProductionPlanItems
                .Where(i => i.LotId == lid)
                .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                .Where(x => !x.i.IsClosed)
                .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg);
            lot.ReservedQtyKg = Math.Max(0, reserved);
        }
    }

    /// <summary>§الطاقة اللحظية: المتاح/المستخدم/المتبقي للوردية والخط في تاريخ محدد.</summary>
    public ShiftCapacityInfo GetShiftCapacityInfo(int shiftId, int lineId, string date, int? productId = null, int? excludePlanId = null)
    {
        var (used, eff, rate) = ShiftUsageHours(shiftId, lineId, date, productId ?? 0, excludePlanId);
        var shift = Db.Shifts.FirstOrDefault(s => s.Id == shiftId);
        return new ShiftCapacityInfo
        {
            ShiftName = shift?.ShiftNameAr ?? "-",
            TotalHours = eff,
            UsedHours = used,
            RemainingHours = Math.Max(0, Math.Round(eff - used, 2)),
            HourlyRate = rate,
            MaxCartons = (int)(eff * rate),
            RemainingCartons = (int)(Math.Max(0, eff - used) * rate)
        };
    }

    /// <summary>
    /// §المتاح لصنف محدد من دفعة (مطابق لـ v1.60 product_lot_remaining): يخصم فقط حجوزات
    /// هذا الصنف من الخطط النشطة لا حجوزات الأصناف الأخرى — فلا تداخل بين أصناف الدفعة الواحدة.
    /// سكري: رصيد الدفعة − حجوز سكري فقط | برمي: رصيد الدفعة − حجوز برمي فقط.
    /// </summary>
    public double GetProductLotRemaining(int lotId, int productId, int? excludePlanId = null)
    {
        var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
        if (lot == null) return 0;

        // حجوزات هذا الصنف فقط من بنود الخطط النشطة (غير المقفلة/الملغاة)
        var planCommitted = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.LotId == lotId && i.ProductId == productId
                        && (excludePlanId == null || i.PlanId != excludePlanId))
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Where(x => !x.i.IsClosed)
            .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);

        // حجوزات هذا الصنف من أوامر الإنتاج النشطة (غير الملغاة/المكتملة)
        var orderCommitted = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId == lotId && i.ProductId == productId)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
            .Where(x => !x.i.IsClosed)
            .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);

        // §المعالجة والتعقيم (الموضعان 8 و9): ما هو داخل دورة معالجة جارية ليس متاحاً
        return Math.Max(0, Math.Round(lot.InStockQtyKg - lot.UnderTreatmentQtyKg - planCommitted - orderCommitted, 3));
    }

    /// <summary>§7 — إرسال الخطة للمدير العام للاعتماد الرسمي.</summary>
    public OpResult SubmitPlan(int planId)
    {
        Require("planning", "Edit");
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (plan.IsApproved) return OpResult.Fail("الخطة معتمدة مسبقاً.");
        if (plan.Items.Count == 0) return OpResult.Fail("لا يمكن إرسال خطة بدون بنود.");
        return RunOp(() =>
        {
            plan.Status = "UnderApproval";
            Db.SaveChanges();
            return OpResult.Success("تم إرسال الخطة للاعتماد الرسمي.", plan.Id, plan.DocumentNumber);
        });
    }

    /// <summary>إرجاع الخطة للمخطط للتعديل مع ملاحظات.</summary>
    public OpResult ReturnPlan(int planId, string notes)
    {
        Require("planning", "Approve");
        var plan = Db.ProductionPlans.FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (plan.IsApproved) return OpResult.Fail("الخطة معتمدة — استخدم إلغاء الاعتماد.");
        return RunOp(() =>
        {
            plan.Status = DocStatuses.Draft;
            plan.Notes = string.IsNullOrWhiteSpace(notes) ? plan.Notes : ("ملاحظات الإرجاع: " + notes);
            Db.SaveChanges();
            return OpResult.Success("تم إرجاع الخطة للتعديل.", plan.Id, plan.DocumentNumber);
        });
    }

    public OpResult UnapprovePlan(int planId)
    {
        Require("planning", "Cancel");
        var plan = Db.ProductionPlans.FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (!plan.IsApproved) return OpResult.Fail("الخطة غير معتمدة.");
        if (Db.ProductionOrders.Any(o => o.SourcePlanId == planId))
            return OpResult.Fail("لا يمكن إلغاء الاعتماد: صدرت أوامر إنتاج من هذه الخطة.");
        return RunOp(() =>
        {
            plan.IsApproved = false;
            plan.Status = DocStatuses.Draft;
            ApplyLotReservations(plan); // إعادة احتساب الحجوزات بدون هذه الخطة
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء اعتماد الخطة وإعادة فتحها للتعديل.", plan.Id, plan.DocumentNumber);
        });
    }

    public OpResult DeletePlan(int planId)
    {
        Require("planning", "Delete");
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (plan.IsApproved) return OpResult.Fail("لا يمكن حذف خطة معتمدة — ألغِ الاعتماد أولاً.");
        if (Db.ProductionOrders.Any(o => o.SourcePlanId == planId))
            return OpResult.Fail("لا يمكن حذف خطة صدرت منها أوامر إنتاج.");
        return RunOp(() =>
        {
            // §إصلاح تسرّب حجز: الحذف كان يزيل الخطة وبنودها (Cascade) بلا إعادة احتساب
            // الحجوزات، فتبقى ReservedQtyKg على الدفعة محجوزة لخطة لم تعد موجودة —
            // كمية مجمّدة إلى الأبد لا يحرّرها شيء، ولا شاشة تفسّر سببها.
            // نصفّر بنود الخطة أولاً ثم نعيد الاحتساب، فتُطرح حصتها كما في الإقفال.
            var affectedLots = plan.Items.Where(i => i.LotId != null)
                .Select(i => i.LotId.Value).Distinct().ToList();
            plan.Status = DocStatuses.Cancelled;
            plan.IsClosed = true;
            ApplyLotReservations(plan);

            Db.ProductionPlans.Remove(plan);
            Db.SaveChanges();

            // §بعد الحذف الفعلي نعيد الاحتساب من الواقع: أي حجز متبقٍ يعود لخطط أخرى فقط.
            RecomputeLotReservations(affectedLots);
            Db.SaveChanges();
            return OpResult.Success("تم حذف الخطة (المسودة) وتحرير حجوزات دفعاتها.");
        });
    }

    /// <summary>§7 — اعتماد الخطة تصبح معه جاهزة لأوامر الإنتاج.</summary>
    public OpResult ApprovePlan(int planId)
    {
        Require("planning", "Approve");
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (plan.IsApproved) return OpResult.Fail("الخطة معتمدة مسبقاً.");
        if (plan.Items.Count == 0) return OpResult.Fail("لا يمكن اعتماد خطة بدون بنود.");

        // §لا خطة فوق خطة (فحص طاقة لا منع أعمى): يُسمح بخطة ثانية في نفس اليوم والوردية
        // ما دامت الطاقة الفعلية المتبقية تكفي، ويُرفض فقط التجاوز الذي يتعدى الطاقة.
        // §إصلاح حرج: تراكم بنود الخطة نفسها على نفس اليوم/الوردية/الخط عند الاعتماد أيضاً.
        var localUsed = new Dictionary<(DateTime day, int shift, int line), double>();
        foreach (var item in plan.Items)
        {
            if (item.PlannedCartons > 0 && item.ScheduledDate != null && item.SuggestedShiftId is int shId)
            {
                int lineId = item.SuggestedLineId ?? 1;
                var (usedOther, cap, rate) = ShiftUsageHours(shId, lineId,
                    item.ScheduledDate.Value.ToString("dd/MM/yyyy"), item.ProductId,
                    excludePlanId: plan.Id, item.PackagingTypeId);
                var key = (item.ScheduledDate.Value.Date, shId, lineId);
                double usedHere = localUsed.TryGetValue(key, out var lu) ? lu : 0;
                double requiredHours = rate > 0 ? item.PlannedCartons / rate : 0;
                double used = usedOther + usedHere;
                if (used + requiredHours > cap + 0.0001)
                {
                    double remainingHours = Math.Max(0, cap - used);
                    int availableCartons = (int)Math.Floor(remainingHours * rate);
                    return OpResult.Fail(
                        $"⛔ لا يمكن اعتماد الخطة: الطاقة الإنتاجية للوردية يوم {item.ScheduledDate:dd/MM/yyyy} لا تكفي.\n" +
                        $"الطاقة المتاحة: {availableCartons:N0} كرتون | المطلوب لهذا البند: {item.PlannedCartons:N0} كرتون\n" +
                        $"(مستهلكة في خطط أخرى {usedOther:N1} س + في بنود هذه الخطة {usedHere:N1} س من أصل {cap:N1} س)\n" +
                        $"قلّل الكمية أو انقل البند ليوم/وردية أخرى بها طاقة متبقية.");
                }
                localUsed[key] = usedHere + requiredHours;
            }
        }

        // §المعالجة والتعقيم — حارس الاعتماد: لا تُعتمد خطة على خام لن يكون جاهزاً
        // في تاريخ إنتاجها. الفحص **حسب تاريخ كل بند** لا حسب إجمالي المستلم.
        var treatMsg = CheckTreatmentReadiness(plan);
        if (treatMsg != null) return OpResult.Fail(treatMsg);

        return RunOp(() =>
        {
            plan.IsApproved = true;
            plan.Status = DocStatuses.Approved;
            plan.ApprovedBy = Session?.UserId;
            plan.ApprovedDate = DateTime.Now;
            foreach (var it in plan.Items) it.Status = DocStatuses.Approved;
            Db.SaveChanges();
            return OpResult.Success("تم اعتماد الخطة — أصبحت جاهزة لإنشاء أوامر الإنتاج.", plan.Id, plan.DocumentNumber);
        });
    }

    public OpResult ClosePlan(int planId, string notes)
    {
        Require("planning", "Cancel");
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");

        return RunOp(() =>
        {
            plan.IsClosed = true;
            plan.ClosedDate = DateTime.Now;
            plan.Status = DocStatuses.Closed;
            plan.Notes = string.IsNullOrWhiteSpace(notes) ? plan.Notes : notes;
            ApplyLotReservations(plan); // تحرير الحجوزات غير المستهلكة
            Db.SaveChanges();
            return OpResult.Success("تم إقفال الخطة نهائياً.");
        });
    }

    /// <summary>
    /// §12/§13 — الدفعات المتاحة مع خصم كل الخطط النشطة لكل الأصناف.
    ///
    /// §المعالجة والتعقيم (الموضع 7): يُطرح <c>UnderTreatmentQtyKg</c> أيضاً، ويُحسب
    /// <c>AvailableForDateKg</c> حين يُمرَّر <paramref name="forDate"/>.
    /// **المعامل اختياري بقيمة null = السلوك القديم حرفياً**، فلا تتأثر أي شاشة
    /// قائمة لا تمرّره — التزاماً بمنع حذف أي وظيفة قائمة.
    /// </summary>
    public List<AvailableLotDto> GetAvailableLots(int? customerId = null, DateTime? forDate = null)
    {
        var q = Db.Lots.AsQueryable();
        // §B67: فلترة العميل تشمل الدفعات التي ورثت عميلها من سند الاستلام (بيانات قديمة
        // بلا CustomerId على الدفعة) — فلا «تختفي أصناف العميل» أبداً.
        if (customerId != null)
            q = q.Where(l => l.CustomerId == customerId
                || (l.CustomerId == null && Db.Shipments.Any(x => x.Id == l.ShipmentId && x.CustomerId == customerId)));
        var rows = q.Where(l => l.Status == DocStatuses.Approved && l.InStockQtyKg > 0)
            .Select(l => new AvailableLotDto
            {
                LotId = l.Id,
                LotCode = l.LotCode,
                ProductId = l.ProductId,
                ProductName = Db.Products.Where(p => p.Id == l.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                CustomerId = l.CustomerId,
                CustomerName = Db.Customers.Where(c => c.Id == l.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                ShipmentId = l.ShipmentId,
                ShipmentNo = Db.Shipments.Where(x => x.Id == l.ShipmentId).Select(x => x.DocumentNumber).FirstOrDefault(),
                ArrivalDate = Db.Shipments.Where(x => x.Id == l.ShipmentId).Select(x => x.ArrivalDate).FirstOrDefault(),
                InitialQtyKg = l.InitialQtyKg,
                ReservedQtyKg = l.ReservedQtyKg,
                RequiresTreatment = Db.Products.Where(p => p.Id == l.ProductId)
                                      .Select(p => p.RequiresTreatment).FirstOrDefault(),
                ReadyNowKg = l.TreatmentReadyQtyKg,
                UnderTreatmentKg = l.UnderTreatmentQtyKg,
                // §B64: AvailableQtyKg خاصية محسوبة غير مخزّنة — لا تُترجم في استعلام خادمي؛
                // يُستبدل بالتعبير القابل للترجمة (يطرح الأعمدة المخزّنة).
                RemainingKg = l.InStockQtyKg - l.ReservedQtyKg - l.UnderTreatmentQtyKg
            })
            .Where(l => l.RemainingKg > 0)
            .ToList();

        // §المعالجة والتعقيم: «المتوقع جاهزيته حتى تاريخ الخطة» يُحسب في الذاكرة —
        // تجميع المعالجات لكل دفعة لا يُترجم داخل الاستعلام أعلاه.
        var ids = rows.Where(r => r.RequiresTreatment).Select(r => r.LotId).ToList();
        if (ids.Count > 0)
        {
            var end = forDate?.Date.AddDays(1).AddTicks(-1);
            var maturing = Db.RawTreatments.AsNoTracking()
                .Where(t => ids.Contains(t.LotId) && t.Status == TreatmentStatuses.InProgress)
                .Where(t => end == null || t.ExpectedReadyAt <= end)
                .GroupBy(t => t.LotId)
                .Select(g => new { LotId = g.Key, Kg = g.Sum(x => x.QtyKg - x.ReleasedQtyKg - x.RejectedQtyKg) })
                .ToDictionary(x => x.LotId, x => x.Kg);

            foreach (var r in rows)
            {
                if (!r.RequiresTreatment)
                {
                    // الصنف الذي لا يشترط معالجة: المتاح كما كان تماماً (قرار س3)
                    r.AvailableForDateKg = r.RemainingKg;
                    continue;
                }
                r.ExpectedReadyByDateKg = maturing.TryGetValue(r.LotId, out var m) ? Math.Max(0, m) : 0;
                r.AvailableForDateKg = Math.Max(0,
                    r.ReadyNowKg + r.ExpectedReadyByDateKg - r.ReservedQtyKg);
            }
        }
        else
        {
            foreach (var r in rows) r.AvailableForDateKg = r.RemainingKg;
        }
        return rows;
    }

    /// <summary>§B68: الأصناف القابلة للتخطيط من دفعة محددة — يُستبعد كل صنف نفذ رصيده منها.
    /// بلا دفعة (إضافة يدوي/إدارة) تُرجع كل الأصناف التامة.</summary>
    public List<Product> GetPlannableProducts(int? lotId = null)
    {
        var fins = GetFinishedProducts();
        if (lotId == null) return fins;
        return fins.Where(p => GetProductLotRemaining(lotId.Value, p.Id) > 0).ToList();
    }

    /// <summary>
    /// §نافذة الاختيار: الأصناف التامة التي تظهر في قائمة الصنف التام — مطابقة لفلتر v1.59:
    /// (المجموعة 002 أو صنف بلا مجموعة) ونشط. هذا يضمن ظهور أصناف المصنع حتى لو لم تُملأ المجموعة.
    /// </summary>
    /// <summary>§B56: الأصناف التامة فقط — النوع «Finished» هو انتماء مجموعة التام،
    /// فلا تظهر أصناف الخام/الثانوي في قوائم اختيار الصنف التام، وتظهر التامة بمجموعات مخصصة.</summary>
    public List<Product> GetFinishedProducts()
        => Db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.ItemType == "Finished")
            .OrderBy(p => p.ProductNameAr)
            .ToList();

    /// <summary>
    /// ⚖ محرك التوزيع العادل الآلي v2 (B87/H5/M8):
    /// • فلتر التحويل الرسمي: الدفعة لا تُنتَج إلا بالأصناف المسموحة من خامها (بطاقة المنتج) —
    ///   والدوّار يدور بين المسموحة فقط، والدفعة بلا صنف مسموح تُتجاوَز بملاحظة صاخبة لا صفوف وهمية.
    /// • حصة كل (صنف×عبوة) بمعدلها الخاص في كل وردية — لا معدل مرجعي واحد يُفرَض على الجميع.
    /// • الأرصدة واعية بالأوامر: الرصيد − بنود الخطط الحية − بنود الأوامر المستقلة الحية (بلا ازدواج).
    /// • تخطي الجمعة افتراضياً (الأسبوع: السبت–الخميس) — اختياري من المعالج.
    /// • تعبئة كل الورديات النشطة: تبدأ بالأساسية ثم تفيض للبقية — كل بند موسوم بورديته.
    /// • لا بنود صفرية: الفتات دون العبوة الكاملة يُترَك برصيده ويُبلَّغ عنه بصدق (لا تجاوز أبداً).
    /// • DaysUsed = أيام الإنتاج الفعلية فقط (L3).
    /// النتيجة: لا يُغرق السوق بصنف عميل واحد، ولا ينتظر صاحب الحاوية الواحدة خلف أصحاب الحاويات.
    /// </summary>
    public FairDistributionProposal SuggestFairDistribution(string startDate, string endDate, int shiftId, int lineId,
        int? targetProductId = null, double? dailyKgOverride = null, bool excludeFriday = true)
    {
        Require("planning", "View");
        var result = new FairDistributionProposal();

        if (!UiFormat.TryParseDate(startDate, out var d0) || !UiFormat.TryParseDate(endDate, out var d1) || d1 < d0)
        { result.Message = "تواريخ الخطة غير صحيحة — حدد فترة سليمة (من ≤ إلى)."; return result; }
        var days = new List<DateTime>();
        for (var d = d0.Date; d <= d1.Date; d = d.AddDays(1))
            if (!excludeFriday || d.DayOfWeek != DayOfWeek.Friday) days.Add(d);
        if (days.Count == 0)
        { result.Message = "الفترة لا تحتوي أي يوم عمل — كلها أيام جمعة مستثناة (ألغِ «تخطي الجمعة» من المعالج إن كان المصنع يعمل فيها)."; return result; }

        // §الوردية اختيار إلزامي — وهي الأساسية التي يبدأ منها الملء ثم يفيض لبقية الورديات النشطة
        var shift = Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == shiftId);
        if (shift == null) { result.Message = "اختر الوردية أولاً — اختيار إلزامي لمعدل الخطة."; return result; }
        var activeShifts = Db.Shifts.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id).ToList();
        if (activeShifts.Count == 0) activeShifts.Add(shift);
        activeShifts = activeShifts.OrderBy(x => x.Id == shiftId ? 0 : 1).ThenBy(x => x.Id).ToList();
        double Eff(Shift s2)
        {
            double e = CapacityPolicy.EffectiveHours(s2.EffectiveProductiveHours, s2.TotalHours);
            return e > 0 ? e : 8;
        }

        // الأصناف التامة: صنف مستهدف واحد أو دوّار أصناف تلقائي (يُصفَّى بالمسموح لكل دفعة لاحقاً)
        var fins = targetProductId != null
            ? Db.Products.AsNoTracking().Where(p => p.Id == targetProductId.Value && p.IsActive).ToList()
            : GetFinishedProducts();
        if (fins.Count == 0) { result.Message = "لا توجد أصناف تامة معرفّة — أضفها من شاشة الأصناف أولاً."; return result; }

        var packs = Db.PackagingTypes.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).ToList();
        if (packs.Count == 0) { result.Message = "لا توجد عبوات معرفّة — أضف العبوات أولاً."; return result; }

        int DefaultPackId(Product p) => packs.OrderBy(x => Math.Abs(x.UnitWeightKg - p.CartonWeightKg)).First().Id;
        double PackWeight(int packId) => packs.First(p => p.Id == packId).UnitWeightKg;

        // §معدلات محلولة ومحفوظة: كل (صنف×عبوة×وردية) بمعدلها وطاقتها ومصدرها
        var rateCache = new Dictionary<(int fin, int pack, int sh), (double rate, int cap, string src)>();
        (double rate, int cap, string src) RateOf(int finId, int packId, int shId)
        {
            var key = (finId, packId, shId);
            if (!rateCache.TryGetValue(key, out var v))
            { v = CapacityPolicy.Resolve(Db, finId, shId, packId); rateCache[key] = v; }
            return v;
        }

        var skipped = new List<string>();
        // الحصة اليومية (كجم): إدخال يدوي يوزَّع على كل الورديات، أو تلقائية بمعدلات الأصناف
        if (dailyKgOverride > 0)
        {
            result.DailyQuotaKg = dailyKgOverride.Value;
            result.CapacityNote = $"حصة يومية يدوية {dailyKgOverride.Value:N0} كجم تُوزَّع على كل الورديات النشطة ({activeShifts.Count}) — بمعدل كل صنف في ورديته.";
        }
        else
        {
            // §لا معدل افتراضي في الكود: الغياب التام للمعدلات = لا اقتراح مع بيان السبب (لا صفوف وهمية)
            bool anyRate = fins.Any(f => activeShifts.Any(sh => RateOf(f.Id, DefaultPackId(f), sh.Id).rate > 0));
            if (!anyRate)
            {
                result.CapacityNote = "لا طاقة معرَّفة لهذه الأصناف في الورديات النشطة — عرّفها من شاشة الأصناف";
                result.Message = result.CapacityNote;
                return result;
            }
            var refProd = fins.FirstOrDefault(f => activeShifts.Any(sh => RateOf(f.Id, DefaultPackId(f), sh.Id).rate > 0)) ?? fins[0];
            int refPack = DefaultPackId(refProd);
            double refUw = PackWeight(refPack);
            var parts = new List<string>();
            double sum = 0;
            foreach (var sh in activeShifts)
            {
                var (rate, cap, _) = RateOf(refProd.Id, refPack, sh.Id);
                int cartons = cap > 0 ? cap : (rate > 0 ? (int)Math.Round(rate * Eff(sh)) : 0);
                double kg = Math.Round(cartons * refUw, 0);
                sum += kg;
                parts.Add($"{sh.ShiftNameAr} ≈ {kg:N0}");
            }
            result.DailyQuotaKg = sum;
            result.CapacityNote = $"الحصة اليومية ≈ {sum:N0} كجم = {string.Join(" + ", parts)} (بمعدل كل صنف×عبوة في ورديته — {refProd.ProductNameAr} مرجع العرض فقط)";
        }

        // §M8: الالتزامات الحية لكل دفعة — بنود الخطط الحية + بنود الأوامر المستقلة الحية (بلا ازدواج)
        // (تجميع ذاكري بعد ToList — آمن لكل المزودات بما فيها InMemory في الاختبارات)
        var planLiveByLot = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.LotId != null && !i.IsClosed)
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .ToList()
            .GroupBy(x => x.i.LotId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => Math.Max(0, x.i.PlannedQtyKg - x.i.ProducedQtyKg)));
        var standaloneLiveByLot = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId != null && i.PlanItemId == null && !i.IsClosed)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
            .ToList()
            .GroupBy(x => x.i.LotId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => Math.Max(0, x.i.PlannedQtyKg - x.i.ProducedQtyKg)));

        // الأرصدة المتاحة واعية بالأوامر، مرتبة بأقدمية وصول الحاويات (FIFO حسب تاريخ الوصول)
        var lotsRaw = (from l in Db.Lots.AsNoTracking()
                       where l.InStockQtyKg > 0.01
                       join s in Db.Shipments.AsNoTracking() on l.ShipmentId equals s.Id into sj
                       from s in sj.DefaultIfEmpty()
                       orderby s.ArrivalDate ascending, l.Id ascending
                       select new { l, s }).ToList();
        // §إصلاح: ToDictionary عملية ذاكرية — AsEnumerable قبلها
        var rawNames = Db.Products.AsNoTracking().AsEnumerable().ToDictionary(p => p.Id, p => p.ProductNameAr);
        var custNames = Db.Customers.AsNoTracking().AsEnumerable().ToDictionary(c => c.Id, c => c.CustomerName);
        // §B77: أولوية العميل تُقدَّم على قاعدة الأقل إنجازاً عند التناوب
        var custPrio = Db.Customers.AsNoTracking().AsEnumerable().ToDictionary(c => c.Id, c => c.PriorityNo);
        var today = DateTime.Today;

        // §H5: فلتر التحويل الرسمي — الأصناف المسموح إنتاجها من كل دفعة حسب بطاقة المنتج (محفوظ)
        var allowedCache = new Dictionary<int, List<Product>>();
        List<Product> AllowedFor(int lotId)
        {
            if (!allowedCache.TryGetValue(lotId, out var list))
            {
                list = fins.Where(f => ProductIdentityGuard.CheckConversion(Db, f.Id, lotId) == null).ToList();
                allowedCache[lotId] = list;
            }
            return list;
        }

        var buckets = new Dictionary<int?, CustBucket>();
        foreach (var row in lotsRaw)
        {
            var l = row.l;
            var s = row.s;
            double committed = (planLiveByLot.TryGetValue(l.Id, out var pl) ? pl : 0)
                             + (standaloneLiveByLot.TryGetValue(l.Id, out var so) ? so : 0);
            // §المعالجة والتعقيم (الموضع 10): الدوّار لا يقترح خاماً تحت المعالجة
            double remaining = Math.Max(0, l.InStockQtyKg - l.UnderTreatmentQtyKg - committed);
            if (remaining <= 0.01) continue; // مستنفَدة بالخطط/الأوامر الحية — لا تدخل الدوّار
            var allowed = AllowedFor(l.Id);
            if (allowed.Count == 0)
            {
                skipped.Add(targetProductId != null
                    ? $"⛔ الدفعة {l.LotCode}: الصنف المطلوب «{fins[0].ProductNameAr}» غير مسموح إنتاجه من خامها — راجع بطاقة المنتج (تعريف التحويل الرسمي)."
                    : $"⛔ الدفعة {l.LotCode} (خام: {(rawNames.TryGetValue(l.ProductId, out var rn0) ? rn0 : "—")}): لا يوجد أي صنف تام مسموح إنتاجه منها — عرّف التحويل الرسمي في بطاقة المنتج أولاً.");
                continue;
            }
            if (dailyKgOverride <= 0)
            {
                // الوضع التلقائي يتطلب معدلاً فعلياً في وردية ما — وإلا تجاوُز صاخب لا صفوف وهمية
                bool hasRate = allowed.Any(f => activeShifts.Any(sh => RateOf(f.Id, DefaultPackId(f), sh.Id).rate > 0));
                if (!hasRate)
                {
                    skipped.Add($"⛔ الدفعة {l.LotCode}: أصنافها المسموحة ({string.Join("، ", allowed.Select(f => f.ProductNameAr))}) بلا معدل في أي وردية نشطة — عرّف الطاقة من شاشة الأصناف.");
                    continue;
                }
            }
            int? cid = l.CustomerId;
            if (!buckets.TryGetValue(cid, out var b))
            {
                b = new CustBucket
                {
                    CustomerId = cid,
                    Name = cid != null && custNames.TryGetValue(cid.Value, out var nm) ? nm : "بدون عميل",
                    OldestArrival = s?.ArrivalDate ?? DateTime.MaxValue
                };
                b.PriorityNo = cid != null && custPrio.TryGetValue(cid.Value, out var pr) ? pr : 0;
                buckets[cid] = b;
            }
            var seed = new LotSeed
            {
                Id = l.Id, LotCode = l.LotCode, CustomerId = cid, ShipmentId = l.ShipmentId,
                RawProductId = l.ProductId, Remaining = remaining,
                ArrivalDate = s?.ArrivalDate, ContainerNumber = s?.ContainerNumber, ShipmentNo = s?.DocumentNumber
            };
            seed.DaysInStock = seed.ArrivalDate != null ? Math.Max(0, (today - seed.ArrivalDate.Value.Date).Days) : 0;
            b.Lots.Add(seed);
            b.TotalAvailable += remaining;
            b.ShipmentIds.Add(l.ShipmentId ?? 0);
            if (seed.ArrivalDate != null && seed.ArrivalDate < b.OldestArrival) b.OldestArrival = seed.ArrivalDate.Value;
        }
        if (buckets.Count == 0)
        {
            result.SkippedNotes = skipped;
            result.Message = skipped.Count > 0
                ? "لا يمكن بناء اقتراح — كل الدفعات المتاحة مرفوضة:\n" + string.Join("\n", skipped.Take(5)) + (skipped.Count > 5 ? $"\n…و {skipped.Count - 5} ملاحظات أخرى." : "")
                : "لا توجد أرصدة خام متاحة للتوزيع — استلم شحنات واعتمدها أولاً.";
            return result;
        }

        foreach (var b in buckets.Values) b.Remaining = b.TotalAvailable;
        double totalRemaining = buckets.Values.Sum(b => b.TotalAvailable);
        var rows = new List<FairPlanRowDto>();
        var producedDays = new HashSet<DateTime>();
        var usedSources = new HashSet<string>();
        int prio = 1;

        // §الدوّار العادل v2: يوم ← وردياته النشطة (الأساسية أولاً) ← العملاء بنسبة الإنجاز ← أقدم الدفعات
        foreach (var day in days)
        {
            if (totalRemaining <= 0.01) break;
            double dayKgLeft = dailyKgOverride > 0 ? dailyKgOverride.Value : double.MaxValue;
            string dayStr = day.ToString("dd/MM/yyyy");
            foreach (var sh in activeShifts)
            {
                if (totalRemaining <= 0.01) break;
                if (dayKgLeft <= 0.01) break;
                double slotHoursLeft = Eff(sh);
                var usedCap = new Dictionary<int, int>(); // كراتين مستهلكة من سقف (هذه الوردية×الصنف)
                var tried = new HashSet<int?>();
                while (slotHoursLeft > 0.0001 && totalRemaining > 0.01 && dayKgLeft > 0.01)
                {
                    var next = buckets.Values.Where(c => c.Remaining > 0.01 && !tried.Contains(c.CustomerId))
                        .OrderBy(c => c.PriorityNo == 0 ? int.MaxValue : c.PriorityNo).ThenBy(c => c.Ratio).ThenBy(c => c.OldestArrival).FirstOrDefault();
                    if (next == null) break; // الباقي فتات لا يملأ عبوة أو عملاء فارغون — الوردية التالية
                    double need = Math.Min(dayKgLeft, next.Remaining);
                    bool emitted = false;
                    foreach (var lot in next.Lots.Where(x => x.Remaining > 0.01).ToList())
                    {
                        var allowed = AllowedFor(lot.Id);
                        if (allowed.Count == 0) continue;
                        // §الدوّار بين المسموحة فقط — عدّاد كل دفعة يتقدم عند الإنتاج الفعلي فقط
                        var fin = allowed[lot.PickNo % allowed.Count];
                        int packId = DefaultPackId(fin);
                        double uw = PackWeight(packId);
                        if (uw <= 0) continue;
                        var (rate, cap, rateSrc) = RateOf(fin.Id, packId, sh.Id);
                        int already = usedCap.TryGetValue(fin.Id, out var u) ? u : 0;
                        int maxByCap = cap > 0 ? Math.Max(0, cap - already) : int.MaxValue;
                        int maxByHours = rate > 0 ? (int)Math.Floor(slotHoursLeft * rate + 1e-6)
                            : (dailyKgOverride > 0 ? int.MaxValue : 0);
                        double take = Math.Min(need, lot.Remaining);
                        int cartons = (int)Math.Min(Math.Floor(take / uw + 1e-6),
                            Math.Min((double)maxByHours, (double)maxByCap));
                        if (cartons < 1) continue; // فتات أو سقف ممتلئ — الدفعة التالية (لا صفوف صفرية ولا تجاوز)
                        double kg = Math.Round(cartons * uw, 1);

                        rows.Add(new FairPlanRowDto
                        {
                            PriorityNo = prio++,
                            Date = dayStr,
                            ShiftId = sh.Id,
                            ShiftName = sh.ShiftNameAr,
                            CustomerId = next.CustomerId,
                            CustomerName = next.Name,
                            ShipmentId = lot.ShipmentId,
                            ShipmentNo = lot.ShipmentNo ?? "—",
                            ContainerNumber = lot.ContainerNumber ?? "—",
                            ArrivalDate = lot.ArrivalDate?.ToString("dd/MM/yyyy") ?? "—",
                            DaysInStock = lot.DaysInStock,
                            LotId = lot.Id,
                            LotCode = lot.LotCode,
                            RawName = rawNames.TryGetValue(lot.RawProductId, out var rn) ? rn : "—",
                            AvailableKg = Math.Round(lot.Remaining, 1),
                            ProductId = fin.Id,
                            ProductName = fin.ProductNameAr,
                            PackagingTypeId = packId,
                            PackName = packs.First(p => p.Id == packId).PackageNameAr,
                            PlannedCartons = cartons,
                            PlannedQtyKg = kg
                        });
                        if (!next.Days.Contains(day)) next.Days.Add(day);
                        producedDays.Add(day);

                        next.AllocatedCartons += cartons;
                        lot.PickNo++;
                        lot.Remaining -= kg; next.Remaining -= kg; totalRemaining -= kg; dayKgLeft -= kg;
                        if (rate > 0) slotHoursLeft -= cartons / rate;
                        if (cap > 0) usedCap[fin.Id] = already + cartons;
                        next.Allocated += kg;
                        double denom = next.Allocated + next.Remaining;
                        next.Ratio = denom > 0 ? next.Allocated / denom : 1.0;
                        if (!string.IsNullOrWhiteSpace(rateSrc))
                            usedSources.Add($"{fin.ProductNameAr} × {sh.ShiftNameAr}: {rate:0.#} كرتون/ساعة ← {rateSrc}");
                        if (lot.Remaining <= 0.01) next.Lots.Remove(lot);
                        emitted = true;
                        break;
                    }
                    if (!emitted) tried.Add(next.CustomerId);
                }
            }
        }

        result.Rows = rows;
        result.Customers = buckets.Values.Select(b => new FairCustomerSummaryDto
        {
            CustomerId = b.CustomerId,
            CustomerName = b.Name,
            ContainersCount = b.ShipmentIds.Where(x => x > 0).Distinct().Count(),
            TotalAvailableKg = Math.Round(b.TotalAvailable, 1),
            AllocatedKg = Math.Round(b.Allocated, 1),
            AllocatedCartons = b.AllocatedCartons,
            ProgressRatio = Math.Round(b.Ratio * 100, 1),
            ProductionDays = b.Days.OrderBy(x => x).Select(x => x.ToString("MM-dd")).ToList()
        }).OrderByDescending(c => c.TotalAvailableKg).ToList();
        result.TotalRemainingKg = Math.Round(totalRemaining, 1);
        result.DaysUsed = producedDays.Count; // §L3: أيام الإنتاج الفعلية فقط
        result.SkippedNotes = skipped;

        result.Ok = rows.Count > 0;
        result.Message = $"⚖ اقتراح توزيع عادل: {rows.Count} بنداً على {result.DaysUsed} يوماً لـ{buckets.Count} عملاء — حصة يومية ≈ {result.DailyQuotaKg:N0} كجم.";
        if (totalRemaining > 0.01)
            result.Message += $"\n⚠ متبقٍ {totalRemaining:N0} كجم لا تسعها مدة الخطة — وسّع الفترة أو زد الحصة اليومية.";
        if (skipped.Count > 0)
            result.Message += "\n" + string.Join("\n", skipped.Take(3)) + (skipped.Count > 3 ? $"\n…و {skipped.Count - 3} ملاحظات تجاوُز أخرى." : "");
        if (usedSources.Count > 0)
            result.CapacityNote = (result.CapacityNote ?? "") + "\nمعدلات مستخدمة: " + string.Join("؛ ", usedSources.Take(4)) + (usedSources.Count > 4 ? "…" : "");
        return result;
    }

    private class LotSeed
    {
        public int Id; public string LotCode; public int? CustomerId; public int? ShipmentId;
        public int RawProductId; public double Remaining; public DateTime? ArrivalDate;
        public string ContainerNumber; public string ShipmentNo; public int DaysInStock;
        /// <summary>§B87: عدّاد الدوّار بين الأصناف المسموحة لهذه الدفعة.</summary>
        public int PickNo;
    }

    private class CustBucket
    {
        public int? CustomerId; public string Name; public List<LotSeed> Lots = new();
        public double TotalAvailable; public double Remaining;
        public double Allocated; public int AllocatedCartons;
        public double Ratio; public DateTime OldestArrival = DateTime.MaxValue;
        public int PriorityNo;
        public HashSet<int> ShipmentIds = new(); public List<DateTime> Days = new();
    }

    /// <summary>
    /// §B91 — فحص الخطة متعددة العملاء بنفس عمل محرك التوزيع:
    /// أيام العمل (تخطي الجمعة)، الورديات النشطة (وردية الخطة أولاً)، أولوية العميل ثم الأقل إنجازاً،
    /// معدلات (صنف×عبوة×وردية) بالكرتون/ساعة، سقوف الكراتين، التحويل الرسمي، والالتزامات الحية (M8) بلا ازدواج.
    /// يحاكي التوزيع يوماً بيوم: الحصة اليومية + المرحّل، والفائض يرحّل لليوم التالي،
    /// والباقي بعد آخر يوم = عجز صريح. لا يتجاوز الطاقة أبداً ولا ينتج صفوفاً صفرية.
    /// </summary>
    public PlanCheckResult CheckPlan(int planId, bool excludeFriday = true)
    {
        Require("planning", "View");
        var result = new PlanCheckResult();

        var plan = Db.ProductionPlans.Include(p => p.Items).AsNoTracking()
            .FirstOrDefault(p => p.Id == planId);
        if (plan == null)
        { result.Verdict = "الخطة غير موجودة."; return result; }
        result.PlanNumber = plan.DocumentNumber;
        result.PlanTitle = plan.PlanTitle;
        var items = plan.Items.Where(i => !i.IsClosed && i.PlannedQtyKg > 0.01).ToList();
        result.ItemsCount = items.Count;
        if (items.Count == 0)
        { result.Verdict = "الخطة بلا بنود قابلة للفحص — أضف بنوداً بكميات أولاً."; return result; }

        DateTime d0 = plan.StartDate?.Date ?? DateTime.Today;
        DateTime d1 = (plan.EndDate ?? plan.StartDate)?.Date ?? DateTime.Today;
        if (d1 < d0)
        { result.Verdict = "تواريخ الخطة غير صحيحة — تاريخ النهاية قبل البداية."; return result; }
        var days = new List<DateTime>();
        for (var d = d0; d <= d1; d = d.AddDays(1))
            if (!excludeFriday || d.DayOfWeek != DayOfWeek.Friday) days.Add(d);
        result.WorkDays = days.Count;
        if (days.Count == 0)
        { result.Verdict = "الفترة (من–إلى) لا تحتوي أي يوم عمل — كلها أيام جمعة مستثناة."; return result; }

        var activeShifts = Db.Shifts.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id).ToList();
        if (plan.ShiftId != null)
            activeShifts = activeShifts.OrderBy(x => x.Id == plan.ShiftId.Value ? 0 : 1).ThenBy(x => x.Id).ToList();
        if (activeShifts.Count == 0)
        { result.Verdict = "لا توجد ورديات نشطة — فعّل وردية من شاشة الورديات أولاً."; return result; }
        double Eff(Shift s2)
        {
            double e = CapacityPolicy.EffectiveHours(s2.EffectiveProductiveHours, s2.TotalHours);
            return e > 0 ? e : 8;
        }

        var packs = Db.PackagingTypes.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).ToList();
        if (packs.Count == 0)
        { result.Verdict = "لا توجد عبوات معرفّة — أضف العبوات أولاً."; return result; }
        var products = Db.Products.AsNoTracking().ToDictionary(p => p.Id);
        int DefaultPackId(Product p) => packs.OrderBy(x => Math.Abs(x.UnitWeightKg - p.CartonWeightKg)).First().Id;
        double PackWeight(int packId) => packs.First(p => p.Id == packId).UnitWeightKg;

        var rateCache = new Dictionary<(int fin, int pack, int sh), (double rate, int cap, string src)>();
        (double rate, int cap, string src) RateOf(int finId, int packId, int shId)
        {
            var key = (finId, packId, shId);
            if (!rateCache.TryGetValue(key, out var v))
            { v = CapacityPolicy.Resolve(Db, finId, shId, packId); rateCache[key] = v; }
            return v;
        }

        var custNames = Db.Customers.AsNoTracking().AsEnumerable().ToDictionary(c => c.Id, c => c.CustomerName);
        var custPrio = Db.Customers.AsNoTracking().AsEnumerable().ToDictionary(c => c.Id, c => c.PriorityNo);
        var lots = Db.Lots.AsNoTracking().ToDictionary(l => l.Id);
        string CustName(int? cid) => cid != null && custNames.TryGetValue(cid.Value, out var nm) ? nm : "بدون عميل";

        // §M8 بلا ازدواج وبلا احتساب ذاتي: التزامات الخطط الأخرى + الأوامر المستقلة الحية فقط
        var planLiveByLot = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.LotId != null && !i.IsClosed && i.PlanId != planId)
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .ToList()
            .GroupBy(x => x.i.LotId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => Math.Max(0, x.i.PlannedQtyKg - x.i.ProducedQtyKg)));
        var standaloneLiveByLot = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId != null && i.PlanItemId == null && !i.IsClosed)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
            .ToList()
            .GroupBy(x => x.i.LotId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => Math.Max(0, x.i.PlannedQtyKg - x.i.ProducedQtyKg)));
        var lotRemaining = new Dictionary<int, double>();
        double LotAvail(int lotId)
        {
            if (!lotRemaining.TryGetValue(lotId, out var v))
            {
                double stock = lots.TryGetValue(lotId, out var l) ? l.InStockQtyKg : 0;
                double committed = (planLiveByLot.TryGetValue(lotId, out var pl) ? pl : 0)
                                 + (standaloneLiveByLot.TryGetValue(lotId, out var so) ? so : 0);
                v = Math.Max(0, stock - committed);
                lotRemaining[lotId] = v;
            }
            return v;
        }

        // ── تحويل البنود إلى طلبات فحص + فحوصات صاخبة (معدل/تحويل/دفعة) ──
        // §البنود تُوزَّع بحرية على أيام الفترة (تواريخ البنود المحفوظة لا تقيّد الفحص — كالتوزيع تماماً).
        var demands = new List<CheckDemand>();
        var usedSources = new HashSet<string>();
        foreach (var it in items)
        {
            if (!products.TryGetValue(it.ProductId, out var prod))
            { result.Warnings.Add($"⛔ بند بلا صنف معروف (معرف {it.ProductId}) — راجع بيانات الخطة."); continue; }
            int packId = it.PackagingTypeId ?? DefaultPackId(prod);
            double uw = packs.Any(p => p.Id == packId) ? PackWeight(packId) : 0;
            if (uw <= 0)
            { result.Warnings.Add($"⛔ «{prod.ProductNameAr}»: العبوة بلا وزن — عرّف وزن العبوة أولاً."); continue; }
            bool hasRate = activeShifts.Any(sh => RateOf(prod.Id, packId, sh.Id).rate > 0
                || RateOf(prod.Id, packId, sh.Id).cap > 0);
            if (!hasRate)
            {
                result.Warnings.Add($"⛔ «{prod.ProductNameAr}» ({CustName(it.CustomerId)}): بلا معدل ولا سقف في أي وردية نشطة — عرّف الطاقة من شاشة الأصناف.");
                demands.Add(new CheckDemand { Item = it, PackId = packId, UnitWeight = uw, Blocked = true });
                continue;
            }
            if (it.LotId != null)
            {
                string conv = ProductIdentityGuard.CheckConversion(Db, prod.Id, it.LotId);
                if (conv != null)
                {
                    result.Warnings.Add($"⛔ «{prod.ProductNameAr}» من الدفعة {(lots.TryGetValue(it.LotId.Value, out var lx) ? lx.LotCode : "?")}: {conv}");
                    demands.Add(new CheckDemand { Item = it, PackId = packId, UnitWeight = uw, Blocked = true });
                    continue;
                }
            }
            var dm = new CheckDemand { Item = it, PackId = packId, UnitWeight = uw, Remaining = it.PlannedQtyKg };
            // §الطاقة اليومية لهذا البند وحده — نفس رياضيات حصة التوزيع (سقف أو معدل×ساعات × وزن العبوة)
            double dayCap = 0;
            foreach (var sh in activeShifts)
            {
                var (rate, cap, src) = RateOf(prod.Id, packId, sh.Id);
                int cartons = cap > 0 ? cap : (rate > 0 ? (int)Math.Round(rate * Eff(sh)) : 0);
                dayCap += Math.Round(cartons * uw, 1);
                if (!string.IsNullOrWhiteSpace(src))
                    usedSources.Add($"{prod.ProductNameAr} × {sh.ShiftNameAr}: {rate:0.#} كرتون/ساعة ← {src}");
            }
            dm.DailyCapKg = dayCap;
            demands.Add(dm);
        }

        // ── المحاكاة: يوم ← ورديات ← عملاء (أولوية ثم أقل إنجازاً) ← بنود ──
        var buckets = demands.Where(d => !d.Blocked).GroupBy(d => d.Item.CustomerId)
            .Select(g => new CheckBucket
            {
                CustomerId = g.Key,
                Name = CustName(g.Key),
                PriorityNo = g.Key != null && custPrio.TryGetValue(g.Key.Value, out var pr) ? pr : 0,
                Demands = g.ToList()
            }).ToList();
        foreach (var b in buckets) { b.Required = b.Demands.Sum(d => d.Item.PlannedQtyKg); b.Remaining = b.Required; }
        double totalRequired = demands.Sum(d => d.Item.PlannedQtyKg);
        result.RequiredKg = Math.Round(totalRequired, 1);
        double dailyTarget = days.Count > 0 ? totalRequired / days.Count : totalRequired;

        double backlog = 0;
        var lotUsedGlobal = new Dictionary<int, double>(); // §استهلاك الدفعات مشترك بين كل بنود الخطة
        foreach (var day in days)
        {
            double dayDemand = dailyTarget + backlog;
            double dayAlloc = 0, hoursUsed = 0;
            double hoursTotal = activeShifts.Sum(Eff);
            if (buckets.Any(b => b.Remaining > 0.01))
            {
                foreach (var sh in activeShifts)
                {
                    double slotHoursLeft = Eff(sh);
                    var usedCap = new Dictionary<int, int>();
                    var tried = new HashSet<int?>();
                    while (slotHoursLeft > 0.0001 && buckets.Any(b => b.Remaining > 0.01))
                    {
                        var next = buckets.Where(b => b.Remaining > 0.01 && !tried.Contains(b.CustomerId))
                            .OrderBy(b => b.PriorityNo == 0 ? int.MaxValue : b.PriorityNo)
                            .ThenBy(b => b.Ratio).ThenBy(b => b.CustomerId ?? int.MaxValue).FirstOrDefault();
                        if (next == null) break;
                        bool emitted = false;
                        foreach (var dm in next.Demands.Where(d => d.Remaining > 0.01).ToList())
                        {
                            // §عجز الدفعة لحظياً: لا يستهلك بندٌ خاماً فوق المتاح للدفعة (مشترك مع بنود الخطة)
                            double lotCap = double.MaxValue;
                            if (dm.Item.LotId != null)
                                lotCap = LotAvail(dm.Item.LotId.Value) - lotUsedGlobal.GetValueOrDefault(dm.Item.LotId.Value);
                            var (rate, cap, _) = RateOf(dm.Item.ProductId, dm.PackId, sh.Id);
                            int already = usedCap.TryGetValue(dm.Item.ProductId, out var u) ? u : 0;
                            int maxByCap = cap > 0 ? Math.Max(0, cap - already) : int.MaxValue;
                            int maxByHours = rate > 0 ? (int)Math.Floor(slotHoursLeft * rate + 1e-6) : 0;
                            double take = Math.Min(dm.Remaining, lotCap);
                            int cartons = (int)Math.Min(Math.Floor(take / dm.UnitWeight + 1e-6),
                                Math.Min((double)maxByHours, (double)maxByCap));
                            if (cartons < 1) continue; // فتات أو سقف/ساعات ممتلئة — البند التالي
                            double kg = Math.Round(cartons * dm.UnitWeight, 1);
                            dm.Remaining -= kg; dm.Covered += kg;
                            next.Remaining -= kg; next.Covered += kg;
                            double denom = next.Covered + next.Remaining;
                            next.Ratio = denom > 0 ? next.Covered / denom : 1.0;
                            if (dm.Item.LotId != null)
                                lotUsedGlobal[dm.Item.LotId.Value] = lotUsedGlobal.GetValueOrDefault(dm.Item.LotId.Value) + kg;
                            if (rate > 0) { slotHoursLeft -= cartons / rate; hoursUsed += cartons / rate; }
                            if (cap > 0) usedCap[dm.Item.ProductId] = already + cartons;
                            dayAlloc += kg;
                            emitted = true;
                            break;
                        }
                        if (!emitted) tried.Add(next.CustomerId);
                    }
                }
            }
            backlog = Math.Max(0, dayDemand - dayAlloc);
            bool tight = hoursTotal > 0 && hoursUsed / hoursTotal >= 0.99;
            string status = backlog > 0.01 ? "Short" : (dayDemand <= 0.01 ? "Idle" : (tight ? "Full" : "Easy"));
            result.Days.Add(new PlanCheckDayDto
            {
                Date = day.ToString("dd/MM/yyyy"),
                DemandKg = Math.Round(dayDemand, 1),
                AllocatedKg = Math.Round(dayAlloc, 1),
                HoursUsed = Math.Round(hoursUsed, 1),
                HoursTotal = Math.Round(hoursTotal, 1),
                LoadPct = hoursTotal > 0 ? (int)Math.Round(hoursUsed / hoursTotal * 100) : 0,
                Status = status,
                StatusAr = status switch { "Short" => "🔴 عجز مرحّل", "Full" => "🟡 ممتلئ", "Idle" => "⚪ بلا حمل", _ => "🟢 مريح" }
            });
        }

        // ── التغطيات والحكم ──
        foreach (var b in buckets)
        {
            double sh = Math.Round(Math.Max(0, b.Required - b.Covered), 1);
            result.Customers.Add(new PlanCheckCustomerDto
            {
                CustomerId = b.CustomerId, CustomerName = b.Name,
                RequiredKg = Math.Round(b.Required, 1), CoveredKg = Math.Round(b.Covered, 1), ShortageKg = sh,
                StatusAr = sh > 0.01 ? "🔴 عجز" : "🟢 مغطى"
            });
        }
        foreach (var dm in demands)
        {
            double sh = Math.Round(Math.Max(0, dm.Item.PlannedQtyKg - dm.Covered), 1);
            var prod = products[dm.Item.ProductId];
            result.Items.Add(new PlanCheckItemDto
            {
                CustomerName = CustName(dm.Item.CustomerId),
                ProductName = prod.ProductNameAr,
                LotCode = dm.Item.LotId != null && lots.TryGetValue(dm.Item.LotId.Value, out var l) ? l.LotCode : "يدوي",
                RequiredKg = Math.Round(dm.Item.PlannedQtyKg, 1),
                CoveredKg = Math.Round(dm.Covered, 1),
                DailyCapKg = Math.Round(dm.DailyCapKg, 1),
                DaysNeeded = dm.DailyCapKg > 0 ? Math.Round(dm.Item.PlannedQtyKg / dm.DailyCapKg, 1) : double.PositiveInfinity,
                StatusAr = dm.Blocked ? "⛔ محجوب" : (sh > 0.01 ? "🔴 عجز" : "🟢 مغطى")
            });
        }
        // §عجز الدفعات: بنود مربوطة بدفعة استهلكت فوق المتاح لها (بعد خصم التزامات الغير)
        foreach (var g in demands.Where(d => !d.Blocked && d.Item.LotId != null).GroupBy(d => d.Item.LotId!.Value))
        {
            double avail = LotAvail(g.Key);
            double need = g.Sum(d => d.Item.PlannedQtyKg);
            if (need - avail > 0.01)
                result.Warnings.Add($"⚠ الدفعة {(lots.TryGetValue(g.Key, out var l) ? l.LotCode : "?")}: بنود الخطة تحتاج {need:N1} كجم والمتاح لها {avail:N1} كجم (بعد التزامات الخطط/الأوامر الأخرى) — عجز خام {need - avail:N1} كجم.");
        }

        double covered = demands.Sum(d => d.Covered);
        result.CoveredKg = Math.Round(covered, 1);
        result.ShortageKg = Math.Round(Math.Max(0, totalRequired - covered), 1);
        result.CustomersCount = buckets.Count;
        if (usedSources.Count > 0)
            result.CapacityNote = "معدلات مستخدمة: " + string.Join("؛ ", usedSources.Take(4)) + (usedSources.Count > 4 ? "…" : "");

        bool blocked = demands.Any(d => d.Blocked);
        int shortDays = result.Days.Count(d => d.Status == "Short");
        if (!blocked && result.ShortageKg <= 0.01)
            result.Verdict = $"✅ الخطة قابلة للتنفيذ — {result.CoveredKg:N1} كجم موزعة على {result.WorkDays} يوم عمل لـ{result.CustomersCount} عملاء ({result.ItemsCount} بنود) بنفس قواعد التوزيع.";
        else
        {
            result.Verdict = $"🔴 الخطة غير قابلة للتنفيذ كما هي — المغطى {result.CoveredKg:N1} من {result.RequiredKg:N1} كجم (عجز {result.ShortageKg:N1} كجم)";
            if (blocked) result.Verdict += " — بنود محجوبة (بلا معدل/تحويل)";
            if (shortDays > 0) result.Verdict += $" — {shortDays} أيام بعجز";
            result.Verdict += ".";
        }
        result.Ok = !blocked && result.ShortageKg <= 0.01;
        return result;
    }

    /// <summary>§B91 — طلب فحص: بند خطة + عبوة الفحص + المتبقي والمغطى والطاقة اليومية.</summary>
    private class CheckDemand
    {
        public ProductionPlanItem Item;
        public int PackId;
        public double UnitWeight;
        public double Remaining;
        public double Covered;
        public double DailyCapKg;
        public bool Blocked;
    }

    /// <summary>§B91 — سلة عميل للفحص: بنوده + نسبته للإنجاز (الأقل إنجازاً يتقدم).</summary>
    private class CheckBucket
    {
        public int? CustomerId; public string Name; public int PriorityNo;
        public List<CheckDemand> Demands = new();
        public double Required; public double Remaining; public double Covered; public double Ratio;
    }

    /// <summary>
    /// §الإقفال اليومي: إن اكتمل إنتاج كل بنود الخطة تُقفل تلقائياً — يحرر الحجوزات
    /// غير المستهلكة ويعيد الأرصدة متاحة، استعداداً لإصدار خطة اليوم التالي.
    /// </summary>
    public OpResult TryAutoCloseIfComplete(int planId)
    {
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null || plan.IsClosed) return OpResult.Fail("");
        if (plan.Items.Count == 0 || !plan.Items.All(i => i.IsClosed || i.ProducedQtyKg + 0.001 >= i.PlannedQtyKg))
            return OpResult.Fail(""); // لم تكتمل بعد — تبقى مفتوحة (إجراء صامت)

        // §B79: مكتملة ≠ مقفلة — لا إقفال تلقائي؛ الإقفال قرار صريح من شاشة «إقفال خطة الإنتاج».
        return OpResult.Success($"✅ الخطة {plan.DocumentNumber} مكتملة — جاهزة للإقفال الرسمي من شاشة «إقفال خطة الإنتاج».");
    }

    /// <summary>
    /// §إصلاح حرج: كل بند يجب أن يحمل تاريخ إنتاج — وإلا تخطّى فحص الطاقة كلياً
    /// لأن الحارس يشترط ScheduledDate != null. البند بلا تاريخ يُجدول على بداية الخطة
    /// (لا يُرفض — جدولة رحومة تُخضعه للفحص بدل أن تُعطّل العمل).
    /// </summary>
    private static void ApplyDefaultScheduledDates(List<PlanItemDto> items, DateTime? planStart, DateTime? planEnd = null)
    {
        // §إصلاح: فترة مقلوبة (النهاية قبل البداية) كانت تمرّ بصمت — الحارس أدناه يشترط
        // planEnd >= planStart فيُلغي نفسه، فتُقبل كل التواريخ بلا فحص وتُحفظ خطة
        // بفترة مستحيلة. الرفض هنا صراحةً بدل تعطيل الحارس ضمناً.
        if (planStart != null && planEnd != null && planEnd.Value.Date < planStart.Value.Date)
            throw new DomainException(
                $"فترة الخطة غير صحيحة: تاريخ النهاية ({planEnd.Value.Date.ToString(UiFormat.DatePattern)}) "
                + $"قبل تاريخ البداية ({planStart.Value.Date.ToString(UiFormat.DatePattern)}).",
                "INVALID_PERIOD");

        string fallback = (planStart ?? DateTime.Today).Date.ToString(UiFormat.DatePattern);
        foreach (var i in items)
            if (!UiFormat.TryParseDate(i.ScheduledDate, out _))
                i.ScheduledDate = fallback;

        // §B80: فرض تاريخ كل إنتاج — تاريخ أي بند خارج فترة الخطة مرفوض صراحةً.
        // (الواجهة تعرض تاريخاً لكل بند في نافذة الاختيار وجدول البنود وتتحقق قبل الحفظ،
        // وهذا الحارس يسدّ أي مسار آخر — الخطة الطويلة لا تقبل بنداً في يوم خارج فترتها.)
        if (planStart != null && planEnd != null && planEnd.Value.Date >= planStart.Value.Date)
        {
            var outside = items
                .Where(i => UiFormat.TryParseDate(i.ScheduledDate, out var d)
                            && (d.Date < planStart.Value.Date || d.Date > planEnd.Value.Date))
                .ToList();
            if (outside.Count > 0)
            {
                string first = outside[0].ScheduledDate;
                throw new DomainException(
                    $"تاريخ الإنتاج ({first}) خارج فترة الخطة " +
                    $"({planStart.Value.Date.ToString(UiFormat.DatePattern)} ← {planEnd.Value.Date.ToString(UiFormat.DatePattern)}) — " +
                    $"{outside.Count} بند بتواريخ خارج الفترة.\nحدّد لكل بند تاريخ إنتاج داخل فترة الخطة.",
                    "DATE_OUT_OF_RANGE");
            }
        }
    }

    /// <summary>
    /// §إصلاح حرج — فحص الطاقة لبند مع تراكم بنود «الخطة نفسها».
    /// الخلل السابق: كان يُمرَّر excludePlanId وحده، فتُستثنى الخطة كلها من الحساب
    /// ولا تُحتسب بنودها على بعضها ← أمكن تحميل 16 ساعة في وردية 8 ساعات بوضع البنود في خطة واحدة.
    /// </summary>
    private string EnsureSlotCapacity(ProductionPlan plan, PlanItemDto dto,
        Dictionary<(DateTime day, int shift, int line), double> localUsed)
    {
        if (dto.PlannedCartons <= 0 || dto.SuggestedShiftId is not int shId) return null;
        if (!UiFormat.TryParseDate(dto.ScheduledDate, out var day)) return null;
        int lineId = dto.SuggestedLineId ?? 1;

        var (usedOther, cap, rate) = ShiftUsageHours(shId, lineId, dto.ScheduledDate, dto.ProductId,
            excludePlanId: plan.Id, dto.PackagingTypeId);
        var key = (day.Date, shId, lineId);
        double usedHere = localUsed.TryGetValue(key, out var lu) ? lu : 0;
        double requiredHours = rate > 0 ? dto.PlannedCartons / rate : 0;
        double used = usedOther + usedHere;

        if (used + requiredHours > cap + 0.0001)
        {
            double remainingHours = Math.Max(0, cap - used);
            int availableCartons = (int)Math.Floor(remainingHours * rate);
            int over = Math.Max(0, dto.PlannedCartons - availableCartons);
            // §B77: اقتراح أقرب يوم بديل تتسع طاقته للكمية — بدل الرفض الأعمى
            string suggestion = "";
            if (plan.StartDate != null && plan.EndDate != null && rate > 0)
            {
                // §B77: ابحث داخل فترة الخطة أولاً ثم حتى 7 أيام بعدها (مع تنبيه تمديد الفترة)
                var last = plan.EndDate.Value.Date.AddDays(7);
                for (var d = plan.StartDate.Value.Date; d <= last; d = d.AddDays(1))
                {
                    if (d == day.Date) continue;
                    var (uOther, _, _) = ShiftUsageHours(shId, lineId, d.ToString("dd/MM/yyyy"), dto.ProductId, plan.Id, dto.PackagingTypeId);
                    double freeHere = localUsed.TryGetValue((d, shId, lineId), out var lh) ? lh : 0;
                    if (uOther + freeHere + requiredHours <= cap + 0.0001)
                    {
                        int altAvail = (int)Math.Floor(Math.Max(0, cap - uOther - freeHere) * rate);
                        string beyond = d > plan.EndDate.Value.Date ? " (خارج فترة الخطة الحالية — مدّد الخطة إليه)" : "";
                        suggestion = $"\n💡 أقرب يوم بديل تتسع طاقته: {d:dd/MM/yyyy}{beyond} (متاح فيه ≈ {altAvail:N0} كرتون) — انقل البند إليه أو وزّع عبر «اقتراح توزيع عادل».";
                        break;
                    }
                }
            }
            throw new DomainException(
                $"الكمية المطلوبة أكبر من الطاقة الإنتاجية المتاحة للصنف في هذه الوردية يوم {day:dd/MM/yyyy}.\n" +
                $"الطاقة المتاحة: {availableCartons:N0} كرتون | المطلوب: {dto.PlannedCartons:N0} كرتون | الزيادة: {over:N0} كرتون\n" +
                $"(الساعات: الإنتاجية {cap:N1} | في خطط أخرى {usedOther:N1} | في بنود هذه الخطة {usedHere:N1} | المطلوبة {requiredHours:N1})" + suggestion,
                "CAPACITY_EXCEEDED");
        }
        localUsed[key] = usedHere + requiredHours;
        // §B85/H4: معدل صفر = طاقة غير معرَّفة — كانت تُمرَّر بصمت (500 سابقاً). تُقبل مع تنبيه صريح.
        if (rate <= 0)
        {
            string pname = Db.Products.AsNoTracking().Where(p => p.Id == dto.ProductId)
                .Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{dto.ProductId}";
            return $"⚠ تنبيه طاقة: الصنف «{pname}» بلا معدل/طاقة معرَّفة في الوردية {shId} يوم {day:dd/MM/yyyy} — لم تُفحص طاقته. حدّدها من «الأصناف ← طاقات الأصناف».";
        }
        return null;
    }

    /// <summary>(الوردية، الخط، اليوم) ← ساعات مستخدمة/طاقة كلية/معدل الصنف — يراعي العبوة/المواصفة.</summary>
    internal (double usedHours, double effHours, double rate) ShiftUsageHours(int shiftId, int lineId, string schedDate, int productId, int? excludePlanId, int? packagingTypeId = null)
    {
        var shift = Db.Shifts.FirstOrDefault(s => s.Id == shiftId);
        double effHours = CapacityPolicy.EffectiveHours(shift?.EffectiveProductiveHours ?? 0, shift?.TotalHours ?? 0);
        double rate = RateFor(productId, shiftId, packagingTypeId);

        DateTime? day = UiFormat.TryParseDate(schedDate, out var dv) ? dv.Date : (DateTime?)null;
        DateTime? dayEnd = day?.AddDays(1);
        var items = Db.ProductionPlanItems
            .Where(i => i.SuggestedLineId == lineId && (i.SuggestedShiftId ?? 1) == shiftId
                        && i.ScheduledDate != null && i.ScheduledDate >= day && i.ScheduledDate < dayEnd
                        && !i.IsClosed) // §B85/H6: البند المقفل لا يشغل طاقة
            .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            // §B85/H6: الخطة المقفلة/الملغاة لا تشغل طاقة — كانت تحجز الشفت أبدياً
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Select(x => new { x.i.PlanId, x.i.ProductId, x.i.PackagingTypeId, x.i.PlannedCartons }).ToList()
            .Where(x => excludePlanId == null || x.PlanId != excludePlanId);

        double used = 0;
        foreach (var it in items)
        {
            // §كل بند يُحسب بمعدل صنفه وعبوته وورديته (سكري 7.5 ≠ سكري 4)
            var r = RateFor(it.ProductId, shiftId, it.PackagingTypeId);
            if (r > 0) used += it.PlannedCartons / r;   // §بلا معدل معرَّف لا تُحتسب ساعات
        }
        return (Math.Round(used, 2), effHours, rate);
    }

    /// <summary>معدل صنف + عبوة + وردية: خاصة بالعبوة أولاً، ثم العامة، ثم معدل الصنف العام.</summary>
    private double RateFor(int productId, int shiftId, int? packagingTypeId)
    {
        var (r, _, _) = CapacityPolicy.Resolve(Db, productId, shiftId, packagingTypeId);
        return r;
    }
}
