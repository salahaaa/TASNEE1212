using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §7/§8 — أوامر الإنتاج: تحويل بنود الخطة المعتمدة إلى أوامر تنفيذية بمرجعها الكامل،
/// بلا إعادة إدخال: العميل والصنف والمنتج والكمية المخططة تُجلب من الخطة.
/// حراس: لا أمر أكبر من متبقي الخطة، لا أمر فوق طاقة الوردية (في الـ Backend لا الواجهة فقط)،
/// هوية الأمر مقفولة بعد بدء الإنتاج، وآلة حالات منضبطة:
/// مسودة → معتمد/مجدول → قيد التنفيذ → (متوقف ⇄ استئناف) → مكتمل، أو ملغي قبل الإنتاج.
/// </summary>
public class ProductionOrderService : ServiceBase, IProductionOrderService
{
    public ProductionOrderService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    // ═══════════════════════════ الإنشاء من الخطة ═══════════════════════════

    public OpResult SaveOrder(string sourceType, int? sourcePlanId, int? customerId, string productionDate, int? shiftId, int? lineId, List<OrderItemDto> items)
    {
        Require("production", "Create");
        // §B88/L1: الأمر اليدوي (بلا خطة) استثناء إجرائي — يتطلب صلاحية صريحة تُمنح من مصفوفة الأدوار
        if (sourcePlanId == null) Require("manualorder", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        // §B80: لا أمر إنتاج بصفر — أي بند بلا كمية (لا كيلو ولا كراتين) يرفض الأمر كاملاً
        // قبل فتح المعاملة، فلا يُنشأ أمر فارغ ولا يُسقط البند بصمت.
        foreach (var dtoZ in items)
            if (dtoZ.PlannedQtyKg <= 0 && dtoZ.PlannedCartons <= 0)
                return OpResult.Fail("كمية الأمر يجب أن تكون أكبر من صفر — يوجد بند بلا كمية (لا كجم ولا كراتين). أدخل الكمية لكل بند معلم.");

        return RunOp(() =>
        {
            // §الخطة المعتمدة هي مصدر الأمر — لا أوامر من خطة غير معتمدة أو مقفلة
            if (sourcePlanId is int planId)
            {
                var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId)
                           ?? throw new DomainException("خطة الإنتاج غير موجودة.");
                if (!plan.IsApproved) throw new DomainException("لا يمكن إنشاء أمر إنتاج من خطة غير معتمدة — اعتمد الخطة أولاً.");
                if (plan.IsClosed) throw new DomainException("الخطة مقفلة — لا يمكن إصدار أوامر جديدة منها.");
            }

            var order = new ProductionOrder
            {
                DocumentNumber = Numbering.Next("ORD"),
                SourceType = sourceType ?? "Manual",
                SourcePlanId = sourcePlanId,
                CustomerId = customerId,
                ProductionDate = UiFormat.TryParseDate(productionDate, out var d) ? d : null,
                ShiftId = shiftId,
                LineId = lineId,
                Status = DocStatuses.Draft
            };
            Db.ProductionOrders.Add(order);
            Db.SaveChanges();

            foreach (var dto in items)
            {
                if (dto.LotId is int lotId)
                {
                    var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId);
                    if (lot == null) throw new DomainException("الدفعة غير موجودة.");
                    // §8 — لا يسمح بتجاوز رصيد الدفعة، ولا بتسليم دفعة عميل إلى أمر عميل آخر
                    if (order.CustomerId != null && lot.CustomerId != null && lot.CustomerId != order.CustomerId)
                        throw new DomainException($"الدفعة {lot.LotCode} تخص عميلاً آخر — لا يمكن استخدامها لهذا الأمر.", "CROSS_CUSTOMER");
                }

                // §تتبع الصنف: التحويل الرسمي — لا يُنتج خلاص من دفعة سكري
                ProductIdentityGuard.EnsureConversionAllowed(Db, dto.ProductId, dto.LotId);

                // §نظام الوحدات: الأمر للمنتجات التامة فقط (002) — الوحدة الأساسية كرتونة
                // والوزن المكافئ بالكيلو يُحسب من وزن الكرتون ويجب أن يتطابق
                UnitsPolicy.RequireItemType(Db, dto.ProductId, "Finished", "أمر الإنتاج");
                dto.PlannedQtyKg = UnitsPolicy.EnsureCartonKgConsistency(Db, dto.ProductId, dto.PackagingTypeId,
                    dto.PlannedQtyKg, dto.PlannedCartons, "أمر الإنتاج");
                // §الكراتين بلا كيلو ← الكيلو المكافئ يُشتق من وزن الكرتون (القاعدة 5)
                if (dto.PlannedQtyKg <= 0) throw new DomainException("كمية الأمر يجب أن تكون أكبر من صفر — أدخل الكمية بالكيلو أو عدد الكراتين.");

                // §تتبع الصنف: هوية البند — صنف الأمر يجب أن يطابق صنف بند الخطة المصدر حرفياً
                if (dto.PlanItemId is int planItemId)
                {
                    var planItem = Db.ProductionPlanItems.AsNoTracking().FirstOrDefault(i => i.Id == planItemId);
                    if (planItem != null && planItem.ProductId != dto.ProductId)
                    {
                        string planName = Db.Products.AsNoTracking().Where(p => p.Id == planItem.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                        string orderName = Db.Products.AsNoTracking().Where(p => p.Id == dto.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                        throw new DomainException(
                            $"⛔ صنف أمر الإنتاج «{orderName}» يختلف عن صنف بند الخطة «{planName}».\n" +
                            "هوية الصنف تنتقل من الخطة إلى الأمر كما هي — لا يمكن تغييرها عند الإصدار.",
                            "IDENTITY_MISMATCH");
                    }
                }

                order.Items.Add(new ProductionOrderItem
                {
                    OrderId = order.Id,
                    PlanItemId = dto.PlanItemId,
                    LotId = dto.LotId,
                    ShipmentId = dto.ShipmentId ?? (dto.LotId != null ? Db.Lots.Where(l => l.Id == dto.LotId).Select(l => l.ShipmentId).FirstOrDefault() : null),
                    CustomerId = dto.CustomerId ?? order.CustomerId,
                    ProductId = dto.ProductId,
                    PackagingTypeId = dto.PackagingTypeId,
                    PlannedQtyKg = dto.PlannedQtyKg,
                    PlannedCartons = dto.PlannedCartons,
                    Status = DocStatuses.Draft,
                    // §القاعدة 7: وزن الكرتون وقت العملية — لا يتغير بتعريف العبوة لاحقاً
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, dto.ProductId, dto.PackagingTypeId),
                    // §حفظ تعريف التعبئة كاملاً وقت الأمر — قوالب × وزن قالب
                    MoldsCount = UnitsPolicy.PackagingDefinition(Db, dto.ProductId, dto.PackagingTypeId).MoldsCount,
                    MoldWeightKg = (decimal)UnitsPolicy.PackagingDefinition(Db, dto.ProductId, dto.PackagingTypeId).MoldWeightKg
                });
            }

            // §8 — حراس الأمر في الـ Backend: متبقي الخطة ثم طاقة الوردية
            CheckPlanRemaining(order);
            var capWarnSave = CheckOrderCapacity(order);

            CalculateMaterials(order); // §7 حساب المواد تلقائياً من معادلات الاستهلاك
            Db.SaveChanges();
            string capMsgSave = capWarnSave.Count > 0 ? "\n" + string.Join("\n", capWarnSave) : "";
            return OpResult.Success("تم حفظ أمر الإنتاج بنجاح." + capMsgSave, order.Id, order.DocumentNumber);
        });
    }

    /// <summary>§8 — لا أمر أكبر من المتبقي في خطة الإنتاج (المخطط − أوامر سابقة غير ملغاة).</summary>
    private void CheckPlanRemaining(ProductionOrder order)
    {
        foreach (var item in order.Items.Where(i => i.PlanItemId != null))
        {
            var pi = Db.ProductionPlanItems.AsNoTracking().FirstOrDefault(i => i.Id == item.PlanItemId)
                     ?? throw new DomainException("بند الخطة المرتبط لم يعد موجوداً.", "BAD_PLAN_ITEM");
            double orderedBefore = Db.ProductionOrderItems.AsNoTracking()
                .Where(x => x.PlanItemId == pi.Id && x.OrderId != order.Id)
                .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x, o })
                .Where(z => z.o.Status != DocStatuses.Cancelled)
                .Sum(z => z.x.PlannedQtyKg);
            double remaining = pi.PlannedQtyKg - orderedBefore;
            if (item.PlannedQtyKg > remaining + 0.001)
            {
                string name = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                throw new DomainException(
                    $"⛔ الكمية المطلوبة تتجاوز الكمية المتبقية في خطة الإنتاج.\n" +
                    $"الصنف: {name} | المخطط في الخطة: {pi.PlannedQtyKg:N1} كجم | أوامر سابقة: {orderedBefore:N1} كجم | المتبقي: {remaining:N1} كجم\n" +
                    $"المطلوب في هذا الأمر: {item.PlannedQtyKg:N1} كجم — قلّل الكمية أو أكمل المتبقي بأمر لاحق.",
                    "OVER_PLAN_REMAINING");
            }
            // §B86/M11: متبقي الكراتين أيضاً — كان الكيلو فقط فيمر تجاوز الكراتين.
            // يُفحص فقط عند تطابق العبوة (أوزان مختلفة = أعداد غير قابلة للمقارنة) مع سماح كرتونين لخطأ التقريب عبر التقسيمات
            if (pi.PlannedCartons > 0 && item.PlannedCartons > 0 && item.PackagingTypeId == pi.PackagingTypeId)
            {
                int orderedBoxesBefore = Db.ProductionOrderItems.AsNoTracking()
                    .Where(x => x.PlanItemId == pi.Id && x.OrderId != order.Id)
                    .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x, o })
                    .Where(z => z.o.Status != DocStatuses.Cancelled)
                    .Sum(z => z.x.PlannedCartons);
                int boxRemaining = pi.PlannedCartons - orderedBoxesBefore;
                if (item.PlannedCartons > boxRemaining + 2)
                {
                    string name = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                    throw new DomainException(
                        $"⛔ عدد الكراتين يتجاوز المتبقي في خطة الإنتاج.\n" +
                        $"الصنف: {name} | المخطط: {pi.PlannedCartons:N0} كرتون | أوامر سابقة: {orderedBoxesBefore:N0} | المتبقي: {boxRemaining:N0}\n" +
                        $"المطلوب في هذا الأمر: {item.PlannedCartons:N0} كرتون — قلّل الكمية أو أكمل المتبقي بأمر لاحق.",
                        "OVER_PLAN_REMAINING");
                }
            }
        }
    }

    /// <summary>
    /// §11/§13 — لا أمر فوق طاقة الوردية: يحسب التحميل القائم في نفس اليوم/الوردية/الخط
    /// (بنود الخطط + الأوامر الأخرى غير الملغاة) ويمنع التجاوز — في الـ Backend لا الواجهة فقط.
    /// </summary>
    private List<string> CheckOrderCapacity(ProductionOrder order)
    {
        // §B85/H4: تُرجع تنبيهات الطاقة غير المعرَّفة (تُلحق برسالة الحفظ/الاعتماد) — كانت 500 صامتاً
        var capWarnings = new List<string>();
        if (order.ProductionDate == null || order.ShiftId == null) return capWarnings;
        int shiftId = order.ShiftId.Value;
        int lineId = order.LineId ?? 1;
        var day = order.ProductionDate.Value.Date;
        var dayEnd = day.AddDays(1);
        var shift = Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == shiftId);
        double effHours = CapacityPolicy.EffectiveHours(shift?.EffectiveProductiveHours ?? 0, shift?.TotalHours ?? 0);
        string shiftName = shift?.ShiftNameAr ?? $"وردية {shiftId}";
        void WarnNoRate(int productId)
        {
            string nm = Db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{productId}";
            string w = $"⚠ تنبيه طاقة: الصنف «{nm}» بلا معدل/طاقة معرَّفة في {shiftName} يوم {day:dd/MM/yyyy} — لم تُفحص طاقته. حدّدها من «الأصناف ← طاقات الأصناف».";
            if (!capWarnings.Contains(w)) capWarnings.Add(w);
        }

        // 1) الأوامر الأخرى في نفس الفتحة (غير الملغاة) — تُحسب أولاً لأنها تستهلك طاقة بنودها من الخطط
        var otherOrderIds = Db.ProductionOrders.AsNoTracking()
            .Where(o => o.ProductionDate != null && o.ProductionDate >= day && o.ProductionDate < dayEnd
                        && o.ShiftId == shiftId && (o.LineId ?? 1) == lineId
                        && o.Status != DocStatuses.Cancelled && o.Id != order.Id)
            .Select(o => o.Id).ToList();
        var otherItems = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => otherOrderIds.Contains(i.OrderId))
            .Select(i => new { i.PlanItemId, i.ProductId, i.PackagingTypeId, i.PlannedCartons }).ToList();
        double usedHours = 0;
        foreach (var oi in otherItems)
        {
            if (oi.PlannedCartons <= 0) continue;
            var r = OrderRateFor(oi.ProductId, shiftId, oi.PackagingTypeId);
            if (r > 0) usedHours += oi.PlannedCartons / r; // §B85/H4: بلا 500 صامتة — وبلا معدل لا تُحتسب ساعات
            else WarnNoRate(oi.ProductId);
        }

        // 2) بنود الخطط المجدولة في نفس الفتحة — يُحتسب فقط الجزء غير المغطى بأوامر
        // (المغطى محسوب ضمن الأوامر أعلاه) وباستثناء بنود هذا الأمر نفسه
        var linkedPlanItemIds = order.Items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId.Value).ToHashSet();
        // §إصلاح: التجميع في الذاكرة (AsEnumerable) — GroupBy/ToDictionary غير قابلة للترجمة لبعض مزودات SQL
        var orderedKgByPlanItem = Db.ProductionOrderItems.AsNoTracking()
            .Where(x => x.PlanItemId != null)
            .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x.PlanItemId, o.Status, x.PlannedQtyKg })
            .Where(z => z.Status != DocStatuses.Cancelled)
            .AsEnumerable()
            .GroupBy(z => z.PlanItemId.Value)
            .ToDictionary(g => g.Key, g => g.Sum(z => z.PlannedQtyKg));
        var planItems = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.ScheduledDate != null && i.ScheduledDate >= day && i.ScheduledDate < dayEnd
                        && (i.SuggestedShiftId ?? 1) == shiftId && (i.SuggestedLineId ?? 1) == lineId
                        && !i.IsClosed) // §B85/H6: البند المقفل لا يشغل طاقة
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            // §B85/H6: الخطة المقفلة/الملغاة لا تشغل طاقة
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Select(x => new { x.i.Id, x.i.ProductId, x.i.PackagingTypeId, x.i.PlannedCartons, x.i.PlannedQtyKg }).ToList()
            .Where(x => !linkedPlanItemIds.Contains(x.Id));
        foreach (var pi in planItems)
        {
            if (pi.PlannedCartons <= 0) continue;
            orderedKgByPlanItem.TryGetValue(pi.Id, out var orderedKg);
            double uncoveredKg = Math.Max(0, pi.PlannedQtyKg - orderedKg);
            if (uncoveredKg <= 0.001) continue; // مغطى بالكامل بأوامر محسوبة أعلاه
            double frac = pi.PlannedQtyKg > 0 ? uncoveredKg / pi.PlannedQtyKg : 1;
            var r = OrderRateFor(pi.ProductId, shiftId, pi.PackagingTypeId);
            if (r > 0) usedHours += pi.PlannedCartons * frac / r; // §B85/H4: بلا 500 صامتة
            else WarnNoRate(pi.ProductId);
        }

        // 3) بنود هذا الأمر — إن تجاوزت المتبقي تُرفض برسالة واضحة
        foreach (var item in order.Items)
        {
            if (item.PlannedCartons <= 0) continue;
            double rate = OrderRateFor(item.ProductId, shiftId, item.PackagingTypeId);
            // §B85/H4: معدل صفر = طاقة غير معرَّفة: تُقبل مع تنبيه (موحّد مع مسار الخطة) بدل رفض ∞
            if (rate <= 0) WarnNoRate(item.ProductId);
            double reqHours = rate > 0 ? item.PlannedCartons / rate : 0;
            if (usedHours + reqHours > effHours + 0.0001)
            {
                string name = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                int availableCartons = (int)Math.Floor(Math.Max(0, effHours - usedHours) * rate);
                throw new DomainException(
                    $"⛔ لا يمكن إنشاء أمر الإنتاج: الكمية المطلوبة تتجاوز الطاقة الإنتاجية المتاحة للوردية.\n" +
                    $"الصنف: {name} | {shiftName} يوم {day:dd/MM/yyyy} | الخط {lineId}\n" +
                    $"الطاقة المتاحة: {availableCartons:N0} كرتون ({Math.Max(0, effHours - usedHours):N1} ساعة متبقية) | المطلوب: {item.PlannedCartons:N0} كرتون ({reqHours:N1} ساعة)\n" +
                    $"الحل: وزّع الكمية على أكثر من وردية (أمر لكل وردية) أو اختر يوماً آخر.",
                    "CAPACITY_EXCEEDED");
            }
            usedHours += reqHours; // بنود الأمر نفسه تتراكم على نفس الفتحة
        }
        return capWarnings;
    }

    /// <summary>معدل الصنف في وردية/عبوة — §عبر CapacityPolicy (مصدر واحد للترتيب).</summary>
    private double OrderRateFor(int productId, int shiftId, int? packagingTypeId)
        => CapacityPolicy.RateFor(Db, productId, shiftId, packagingTypeId);

    /// <summary>§7 — حساب المواد المساعدة من معادلات الاستهلاك لكل بند.</summary>
    private void CalculateMaterials(ProductionOrder order)
    {
        Db.ProductionOrderMaterials.RemoveRange(Db.ProductionOrderMaterials.Where(m => m.OrderId == order.Id));
        var agg = new Dictionary<int, double>();
        foreach (var item in order.Items)
        {
            double cartons = item.PlannedCartons > 0
                ? item.PlannedCartons
                : (item.PlannedQtyKg / Math.Max(0.001, Db.Products.Where(p => p.Id == item.ProductId).Select(p => p.CartonWeightKg).FirstOrDefault()));
            var formulas = Db.ConsumptionFormulas.Where(f => f.ProductId == item.ProductId && f.IsActive
                && (f.CustomerId == null || f.CustomerId == order.CustomerId)).ToList();
            foreach (var f in formulas)
            {
                // §الفعلي/الساعي/المعطل لا يُصرف عند الاعتماد — يُستهلك عند الإقفال بالإدخال الفعلي
                if (f.Mode == "Actual" || f.Mode == "PerHour" || f.Mode == "Unused") continue;
                var matId = ResolveAuxMaterial(f, order.CustomerId);
                agg.TryGetValue(matId, out var cur);
                agg[matId] = cur + f.QtyPerUnit * cartons;
            }
        }
        foreach (var kv in agg)
        {
            order.Materials.Add(new ProductionOrderMaterial
            {
                OrderId = order.Id,
                MaterialId = kv.Key,
                CalculatedQty = Math.Round(kv.Value, 2),
                IsAutoCalculated = true,
                UnitOfMeasure = Db.AuxiliaryMaterials.Where(m => m.Id == kv.Key).Select(m => m.UnitOfMeasure).FirstOrDefault()
            });
        }
    }

    // ═══════════════════════════ بنود الخطة القابلة للأمر ═══════════════════════════

    public List<OrderableItemDto> GetOrderableItems(int planId)
    {
        var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId);
        if (plan == null) return new List<OrderableItemDto>();
        var items = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == planId).OrderBy(i => i.PriorityNo).ToList();
        var result = new List<OrderableItemDto>();
        foreach (var pi in items)
        {
            double orderedKg = 0; int orderedCartons = 0;
            var prev = Db.ProductionOrderItems.AsNoTracking()
                .Where(x => x.PlanItemId == pi.Id)
                .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x, o })
                .Where(z => z.o.Status != DocStatuses.Cancelled)
                .Select(z => z.x).ToList();
            orderedKg = prev.Sum(x => x.PlannedQtyKg);
            orderedCartons = prev.Sum(x => x.PlannedCartons);

            var lot = pi.LotId != null ? Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == pi.LotId) : null;
            // §B86/L2: متبقي الدفعة الحقيقي = الرصيد − حجوزات الخطط − الأوامر المستقلة (بلا ازدواج: أمر الخطة داخل حصة خطته)
            double lotAvail = lot != null ? Math.Max(0, lot.InStockQtyKg - lot.UnderTreatmentQtyKg) : 0;
            if (lot != null)
            {
                double planLive = Db.ProductionPlanItems.AsNoTracking()
                    .Where(i => i.LotId == lot.Id)
                    .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                    .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                    .Where(x => !x.i.IsClosed)
                    .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);
                double standaloneLive = Db.ProductionOrderItems.AsNoTracking()
                    .Where(i => i.LotId == lot.Id && i.PlanItemId == null)
                    .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
                    .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
                    .Where(x => !x.i.IsClosed)
                    .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);
                // §المعالجة والتعقيم (الموضع 11): بوابة أمر الإنتاج تستبعد ما تحت المعالجة
                lotAvail = Math.Max(0, lot.InStockQtyKg - lot.UnderTreatmentQtyKg - planLive - standaloneLive);
            }
            result.Add(new OrderableItemDto
            {
                PlanItemId = pi.Id,
                PlanId = planId,
                PlanNumber = plan.DocumentNumber,
                PlanTitle = plan.PlanTitle,
                PlanDate = plan.StartDate?.ToString("dd/MM/yyyy"),
                CustomerId = pi.CustomerId,
                CustomerName = pi.CustomerId != null
                    ? Db.Customers.AsNoTracking().Where(c => c.Id == pi.CustomerId).Select(c => c.CustomerName).FirstOrDefault()
                    : "-",
                LotId = pi.LotId,
                LotCode = lot?.LotCode ?? "-",
                RawName = lot != null
                    ? Db.Products.AsNoTracking().Where(p => p.Id == lot.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                    : "-",
                LotRemainingKg = lotAvail,
                ProductId = pi.ProductId,
                ProductName = Db.Products.AsNoTracking().Where(p => p.Id == pi.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                PackagingTypeId = pi.PackagingTypeId,
                PackName = pi.PackagingTypeId != null
                    ? Db.PackagingTypes.AsNoTracking().Where(p => p.Id == pi.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault()
                    : "-",
                PlannedKg = pi.PlannedQtyKg,
                PlannedCartons = pi.PlannedCartons,
                OrderedKg = orderedKg,
                OrderedCartons = orderedCartons,
                RemainingKg = Math.Max(0, pi.PlannedQtyKg - orderedKg),
                RemainingCartons = Math.Max(0, pi.PlannedCartons - orderedCartons),
                ProducedKg = pi.ProducedQtyKg,
                ScheduledDate = pi.ScheduledDate?.ToString("dd/MM/yyyy"),
                SuggestedShiftId = pi.SuggestedShiftId,
                SuggestedLineId = pi.SuggestedLineId
            });
        }
        return result;
    }

    /// <summary>
    /// §B93 — ترحيل الخطة إلى أوامر (المرحلة التالية بعد الاعتماد):
    /// بنود الخطة المعتمدة ذات المتبقي تُجمَّع (تاريخ مجدول × وردية × خط) — أمر واحد لكل مجموعة
    /// بكامل المتبقي، عبر SaveOrder نفسه (كل حراسه: المتبقي/الطاقة/الهوية/التحويل).
    /// كل مجموعة معاملة مستقلة: فشل مجموعة لا يوقف البقية، والملخص يذكر كل شيء بصدق.
    /// </summary>
    public PlanIssueResult IssueOrdersFromPlan(int planId, string fromDate = null, string toDate = null, int? shiftId = null)
    {
        Require("production", "Create");
        var result = new PlanIssueResult { PlanId = planId };
        var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId);
        if (plan == null) { result.Message = "خطة الإنتاج غير موجودة."; return result; }
        result.PlanNumber = plan.DocumentNumber;
        if (!plan.IsApproved) { result.Message = "لا يمكن الترحيل من خطة غير معتمدة — اعتمد الخطة أولاً."; return result; }
        if (plan.IsClosed || plan.Status == DocStatuses.Closed || plan.Status == DocStatuses.Cancelled)
        { result.Message = "الخطة مقفلة أو ملغاة — لا يمكن إصدار أوامر جديدة منها."; return result; }

        DateTime? from = UiFormat.TryParseDate(fromDate, out var f) ? f.Date : null;
        DateTime? to = UiFormat.TryParseDate(toDate, out var t) ? t.Date : null;
        var orderables = GetOrderableItems(planId);
        if (orderables.Count == 0) { result.Message = "الخطة بلا بنود قابلة للترحيل."; return result; }

        DateTime planStart = plan.StartDate?.Date ?? DateTime.Today;
        int fallbackShift = plan.ShiftId
            ?? Db.Shifts.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Id).Select(s => s.Id).FirstOrDefault();
        if (fallbackShift == 0)
        { result.Message = "لا توجد ورديات نشطة — فعّل وردية أولاً."; return result; }
        int? fallbackLine = plan.LineId;

        var cands = new List<(OrderableItemDto o, DateTime date, int shift, int? line)>();
        int noRemaining = 0, outOfRange = 0, shiftMismatch = 0;
        bool undatedNoted = false, unshiftedNoted = false;
        foreach (var o in orderables)
        {
            if (o.RemainingKg <= 0.01 && o.RemainingCartons <= 0) { noRemaining++; continue; }
            DateTime d = UiFormat.TryParseDate(o.ScheduledDate, out var sd) ? sd.Date : planStart;
            if (o.ScheduledDate == null && !undatedNoted)
            { result.Skipped.Add("بنود بلا تاريخ مجدول أُلحقت ببداية الفترة (" + planStart.ToString("dd/MM/yyyy") + ")."); undatedNoted = true; }
            if (from != null && d < from || to != null && d > to) { outOfRange++; continue; }
            int sh = o.SuggestedShiftId ?? fallbackShift;
            if (o.SuggestedShiftId == null && !unshiftedNoted)
            { result.Skipped.Add("بنود بلا وردية مقترحة أُلحقت بوردية الخطة الافتراضية."); unshiftedNoted = true; }
            if (shiftId != null && sh != shiftId.Value) { shiftMismatch++; continue; }
            cands.Add((o, d, sh, o.SuggestedLineId ?? fallbackLine));
        }
        if (noRemaining > 0) result.Skipped.Add($"{noRemaining} بنود بلا متبقي (صدرت أوامرها كاملة سابقاً).");
        if (outOfRange > 0) result.Skipped.Add($"{outOfRange} بنود خارج الفترة المحددة.");
        if (shiftMismatch > 0) result.Skipped.Add($"{shiftMismatch} بنود لورديات أخرى.");
        if (cands.Count == 0)
        {
            result.Message = "لا توجد بنود قابلة للترحيل ضمن المحددات — راجع المتخطاة.";
            return result;
        }

        var shiftNames = Db.Shifts.AsNoTracking().ToDictionary(s => s.Id, s => s.ShiftNameAr);
        var lineNames = Db.ProductionLines.AsNoTracking().ToDictionary(l => l.Id, l => l.LineNameAr);
        int totalItems = 0;
        double totalKg = 0;
        foreach (var g in cands.GroupBy(x => new { x.date, x.shift, x.line }).OrderBy(g => g.Key.date).ThenBy(g => g.Key.shift))
        {
            var list = g.ToList();
            var custs = list.Select(x => x.o.CustomerId).Distinct().ToList();
            int? headerCust = custs.Count == 1 ? custs[0] : null; // §متعدد العملاء: الرأس فارغ والعميل على كل بند
            var dtos = list.Select(x => new OrderItemDto
            {
                PlanItemId = x.o.PlanItemId,
                LotId = x.o.LotId,
                CustomerId = x.o.CustomerId,
                ProductId = x.o.ProductId,
                PackagingTypeId = x.o.PackagingTypeId,
                PlannedQtyKg = Math.Round(x.o.RemainingKg, 1),
                PlannedCartons = x.o.RemainingCartons
            }).ToList();
            try
            {
                var r = SaveOrder("FromPlan", planId, headerCust, g.Key.date.ToString("dd/MM/yyyy"), g.Key.shift, g.Key.line, dtos);
                if (!r.Ok) { result.Failed.Add($"{g.Key.date:dd/MM/yyyy} — وردية {(shiftNames.TryGetValue(g.Key.shift, out var sn) ? sn : "?")}: {r.Message}"); continue; }
                result.Created.Add(new IssuedOrderDto
                {
                    OrderId = r.Id,
                    OrderNumber = r.DocumentNumber,
                    ProductionDate = g.Key.date.ToString("dd/MM/yyyy"),
                    ShiftName = shiftNames.TryGetValue(g.Key.shift, out var sn2) ? sn2 : "—",
                    LineName = g.Key.line != null && lineNames.TryGetValue(g.Key.line.Value, out var ln) ? ln : "—",
                    ItemsCount = list.Count,
                    TotalKg = Math.Round(dtos.Sum(d => d.PlannedQtyKg), 1)
                });
                totalItems += list.Count;
                totalKg += dtos.Sum(d => d.PlannedQtyKg);
            }
            catch (Exception ex)
            {
                result.Failed.Add($"{g.Key.date:dd/MM/yyyy} — وردية {(shiftNames.TryGetValue(g.Key.shift, out var sn3) ? sn3 : "?")}: {ex.Message}");
            }
        }

        result.Ok = result.Created.Count > 0;
        result.Message = result.Ok
            ? $"📋 رُحّلت الخطة {plan.DocumentNumber}: {result.Created.Count} أوامر ({totalItems} بنود، {totalKg:N1} كجم)."
              + (result.Failed.Count > 0 ? $" — تعذر {result.Failed.Count} مجموعة (راجع الفاشلة)." : "")
            : "تعذر إنشاء أي أمر — راجع الفاشلة والمتخطاة.";
        return result;
    }

    /// <summary>§10/§12 — طاقة فتحة يوم/وردية/خط لصنف محدد — للعرض والتوزيع على الورديات.</summary>
    public OrderSlotInfo GetOrderSlot(int productId, int? packagingTypeId, int shiftId, int lineId, string date)
    {
        var shift = Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == shiftId);
        double effHours = CapacityPolicy.EffectiveHours(shift?.EffectiveProductiveHours ?? 0, shift?.TotalHours ?? 0);
        double rate = OrderRateFor(productId, shiftId, packagingTypeId);
        UiFormat.TryParseDate(date, out var day);
        var dayEnd = day.Date.AddDays(1);

        double usedHours = 0;
        var orderIds = Db.ProductionOrders.AsNoTracking()
            .Where(o => o.ProductionDate != null && o.ProductionDate >= day.Date && o.ProductionDate < dayEnd
                        && o.ShiftId == shiftId && (o.LineId ?? 1) == lineId && o.Status != DocStatuses.Cancelled)
            .Select(o => o.Id).ToList();
        var orderItems = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => orderIds.Contains(i.OrderId))
            .Select(i => new { i.ProductId, i.PackagingTypeId, i.PlannedCartons }).ToList();
        foreach (var oi in orderItems.Where(x => x.PlannedCartons > 0))
        {
            var r = OrderRateFor(oi.ProductId, shiftId, oi.PackagingTypeId);
            if (r > 0) usedHours += oi.PlannedCartons / r; // §B85/H4: بلا 500 صامتة
        }
        // بنود الخطط: الجزء غير المغطى بأوامر فقط (المغطى محسوب ضمن الأوامر أعلاه)
        // §إصلاح: التجميع في الذاكرة (AsEnumerable) — GroupBy/ToDictionary غير قابلة للترجمة لبعض مزودات SQL
        var orderedKgByPlanItem = Db.ProductionOrderItems.AsNoTracking()
            .Where(x => x.PlanItemId != null)
            .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x.PlanItemId, o.Status, x.PlannedQtyKg })
            .Where(z => z.Status != DocStatuses.Cancelled)
            .AsEnumerable()
            .GroupBy(z => z.PlanItemId.Value)
            .ToDictionary(g => g.Key, g => g.Sum(z => z.PlannedQtyKg));
        var planItems = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.ScheduledDate != null && i.ScheduledDate >= day.Date && i.ScheduledDate < dayEnd
                        && (i.SuggestedShiftId ?? 1) == shiftId && (i.SuggestedLineId ?? 1) == lineId
                        && !i.IsClosed) // §B85/H6: البند المقفل لا يشغل طاقة
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            // §B85/H6: الخطة المقفلة/الملغاة لا تشغل طاقة
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Select(x => new { x.i.Id, x.i.ProductId, x.i.PackagingTypeId, x.i.PlannedCartons, x.i.PlannedQtyKg }).ToList();
        foreach (var pi in planItems.Where(x => x.PlannedCartons > 0))
        {
            orderedKgByPlanItem.TryGetValue(pi.Id, out var orderedKg);
            double uncoveredKg = Math.Max(0, pi.PlannedQtyKg - orderedKg);
            if (uncoveredKg <= 0.001) continue;
            double frac = pi.PlannedQtyKg > 0 ? uncoveredKg / pi.PlannedQtyKg : 1;
            var r = OrderRateFor(pi.ProductId, shiftId, pi.PackagingTypeId);
            if (r > 0) usedHours += pi.PlannedCartons * frac / r; // §B85/H4: بلا 500 صامتة
        }

        int capacity = (int)(effHours * rate);
        int used = (int)(usedHours * rate);
        // §B85/H4: معدل صفر = طاقة غير معرَّفة — التوزيع التلقائي سيجد صفراً متاحاً فيوجَّه المستخدم لتعريف الطاقة
        string capNote = rate > 0 ? null
            : "⚠ تنبيه طاقة: هذا الصنف بلا معدل/طاقة معرَّفة في هذه الوردية — حدّدها من «الأصناف ← طاقات الأصناف» قبل التوزيع.";
        return new OrderSlotInfo
        {
            ShiftId = shiftId,
            ShiftName = shift?.ShiftNameAr ?? "-",
            ShiftStart = shift?.StartTime ?? "-",
            ShiftEnd = shift?.EndTime ?? "-",
            ProductionHours = effHours,
            RatePerHour = rate,
            CapacityCartons = capacity,
            UsedCartons = Math.Min(capacity, used),
            RemainingCartons = Math.Max(0, capacity - used),
            CapacityNote = capNote
        };
    }

    // ═══════════════════════════ آلة الحالات ═══════════════════════════

    /// <summary>§8 — الاعتماد: صرف المواد المحتسبة من مخزن المواد المساعدة وخصم أرصدة الخام من الدفعات — ذرياً.</summary>
    public OpResult ApproveOrder(int orderId)
    {
        Require("production", "Approve");
        var order = Db.ProductionOrders.Include(o => o.Items).Include(o => o.Materials)
            .FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.IsApproved) return OpResult.Fail("الأمر معتمد مسبقاً.");
        if (order.Status == DocStatuses.Cancelled) return OpResult.Fail("الأمر ملغي — لا يمكن اعتماده. أنشئ أمراً بديلاً.");
        if (order.Items.Count == 0) return OpResult.Fail("لا يمكن اعتماد أمر بدون بنود.");

        return RunOp(() =>
        {
            // §متبقي الخطة والطاقة يُعاد فحصهما عند الاعتماد أيضاً (قد تكونت أوامر بعد الحفظ)
            CheckPlanRemaining(order);
            var capWarnAppr = CheckOrderCapacity(order);

            order.IsApproved = true;
            order.Status = order.ProductionDate != null && order.ShiftId != null ? DocStatuses.Scheduled : DocStatuses.Approved;
            order.ApprovedBy = Session?.UserId;
            order.ApprovedDate = DateTime.Now;
            foreach (var it in order.Items) it.Status = DocStatuses.Approved;

            // §قاعدة توازن الإنتاج: لا يُخصم الخام عند الاعتماد.
            // كان يُخصم هنا بوزن المنتج التام المخطط — أي بافتراض أن الخام = المنتج،
            // وهذه معادلة ثابتة ترفضها القاعدة لأن وزن الخارج يزيد عن الداخل لإضافة
            // الماء أثناء التشغيل. فالخام يُصرف عند الإقفال بالكمية المستهلكة فعلياً.
            var whRaw = WarehouseId("WRM");

            // صرف المواد المساعدة من مخزنها
            var whAux = WarehouseId("WAUX");
            foreach (var mat in order.Materials.Where(m => m.CalculatedQty > 0))
            {
                var available = Db.StockBalances.Where(b => b.WarehouseId == whAux && b.MaterialId == mat.MaterialId)
                    .Select(b => b.QtyKg).FirstOrDefault();
                if (available < mat.CalculatedQty - 0.001)
                {
                    // §التجارب لا تتعرقَل: الصرامة اختيارية من الإعدادات، وإلا فصرف جزئي بالمتاح فقط
                    if (StrictAuxEnabled())
                        throw new DomainException(
                            $"رصيد المادة المساعدة غير كافٍ للاعتماد.\nالمتاح: {available:N1} — المطلوب: {mat.CalculatedQty:N1}",
                            "INSUFFICIENT_MATERIAL");
                }
                var issueQty = Math.Min(mat.CalculatedQty, Math.Max(0, available));
                if (issueQty > 0.001)
                    PostStockMovement(whAux, MovementType.Outbound, issueQty, 0,
                        ReferenceDocType.MaterialIssue, order.DocumentNumber,
                        materialId: mat.MaterialId, orderId: order.Id,
                        notes: $"صرف مواد عند اعتماد أمر {order.DocumentNumber}");
                mat.ActualIssuedQty = issueQty;
                mat.Status = DocStatuses.Issued;
            }

            Db.SaveChanges();
            string stateAr = order.Status == DocStatuses.Scheduled ? "معتمد ومجدول 📅" : "معتمد";
            // §B85/M7: تصحيح الرسالة — الخام يُصرف عند الإقفال لا الاعتماد؛ §B85/H4: تنبيه الطاقة غير المعرَّفة
            string capMsgAppr = capWarnAppr.Count > 0 ? "\n" + string.Join("\n", capWarnAppr) : "";
            return OpResult.Success($"تم اعتماد أمر الإنتاج {order.DocumentNumber} ({stateAr}) — صُرفت المواد المساعدة من المخازن، والخام يُصرف فعلياً عند إقفال يوم الإنتاج." + capMsgAppr, order.Id, order.DocumentNumber);
        });
    }

    public OpResult UnapproveOrder(int orderId)
    {
        Require("production", "Cancel");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("الأمر غير موجود.");
        if (!order.IsApproved) return OpResult.Fail("الأمر غير معتمد.");
        if (Db.ProductionExecutions.Any(e => e.OrderId == orderId))
            return OpResult.Fail("لا يمكن إلغاء الاعتماد: يوجد تنفيذ مسجل على الأمر.");

        return RunOp(() =>
        {
            ReverseConsumption(order);
            order.IsApproved = false;
            order.Status = DocStatuses.Draft;
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء الاعتماد وعكس كل حركات الصرف.");
        });
    }

    /// <summary>§15 — بدء الإنتاج: وقت البداية الفعلي + المستخدم + الحالة «قيد التنفيذ».</summary>
    public OpResult StartOrder(int orderId)
    {
        Require("execution", "Create");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (!order.IsApproved) return OpResult.Fail("لا يمكن بدء الإنتاج لأمر غير معتمد — اعتمد الأمر أولاً.");
        if (order.IsClosed) return OpResult.Fail("الأمر مغلق مسبقاً.");
        if (order.Status == DocStatuses.InProgress) return OpResult.Fail("الأمر قيد التنفيذ بالفعل.");
        if (order.Status == DocStatuses.Completed || order.Status == DocStatuses.Closed)
            return OpResult.Fail("الأمر مكتمل — لا يمكن بدء الإنتاج من جديد.");
        if (order.Status == DocStatuses.Cancelled) return OpResult.Fail("الأمر ملغي — لا يمكن بدء الإنتاج.");
        if (Db.ProductionExecutions.Any(e => e.OrderId == orderId && e.Status == DocStatuses.InProgress))
            return OpResult.Fail("توجد جلسة تنفيذ فعالة بالفعل على هذا الأمر.");

        return RunOp(() =>
        {
            var exe = new ProductionExecution
            {
                DocumentNumber = Numbering.Next("EXE"),
                OrderId = orderId,
                LineId = order.LineId,
                ShiftId = order.ShiftId,
                StartDateTime = DateTime.Now,
                Status = DocStatuses.InProgress
            };
            Db.ProductionExecutions.Add(exe);
            order.Status = DocStatuses.InProgress;
            Db.SaveChanges();
            return OpResult.Success(
                $"🏭 بدأ الإنتاج للأمر {order.DocumentNumber} — الجلسة {exe.DocumentNumber}.\n" +
                $"وقت البداية: {DateTime.Now:dd/MM/yyyy HH:mm} | المستخدم: {Session?.UserName ?? "-"}", exe.Id, exe.DocumentNumber);
        });
    }

    /// <summary>§إيقاف مؤقت أثناء التنفيذ — لا يفقد أي بيانات ويُستأنف.</summary>
    public OpResult StopOrder(int orderId, string reason = null)
    {
        Require("execution", "Edit");
        var order = Db.ProductionOrders.FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.Status != DocStatuses.InProgress)
            return OpResult.Fail($"لا يمكن إيقاف أمر حالته «{DocStatuses.ToArabic(order.Status)}» — الإيقاف يكون لأمر قيد التنفيذ فقط.");
        return RunOp(() =>
        {
            order.Status = DocStatuses.Stopped;
            if (!string.IsNullOrWhiteSpace(reason)) order.Notes = (order.Notes + $"\n[توقف {DateTime.Now:dd/MM/yyyy HH:mm}] {reason}").Trim();
            Db.SaveChanges();
            return OpResult.Success($"⏸ تم إيقاف الأمر {order.DocumentNumber} مؤقتاً — يمكن استئنافه في أي وقت.");
        });
    }

    /// <summary>§استئناف أمر متوقف.</summary>
    public OpResult ResumeOrder(int orderId)
    {
        Require("execution", "Edit");
        var order = Db.ProductionOrders.FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.Status != DocStatuses.Stopped)
            return OpResult.Fail($"لا يمكن استئناف أمر حالته «{DocStatuses.ToArabic(order.Status)}» — الاستئناف يكون لأمر متوقف فقط.");
        return RunOp(() =>
        {
            order.Status = DocStatuses.InProgress;
            Db.SaveChanges();
            return OpResult.Success($"▶ تم استئناف الأمر {order.DocumentNumber} — عاد إلى قيد التنفيذ.");
        });
    }

    /// <summary>§إلغاء الأمر قبل الإنتاج — مع عكس الصرف إن كان معتمداً، ويبقى في السجل للتدقيق.</summary>
    public OpResult CancelOrder(int orderId, string reason = null)
    {
        Require("production", "Cancel");
        var order = Db.ProductionOrders.Include(o => o.Items).Include(o => o.Materials).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.Status == DocStatuses.Cancelled) return OpResult.Fail("الأمر ملغي مسبقاً.");
        if (order.Status is DocStatuses.InProgress or DocStatuses.Stopped or DocStatuses.Completed or DocStatuses.Closed)
            return OpResult.Fail(
                $"لا يمكن إلغاء أمر حالته «{DocStatuses.ToArabic(order.Status)}».\n" +
                "بعد بدء الإنتاج لا يُلغى الأمر — استخدم الإقفال أو الإرجاع حسب الصلاحيات، مع بقاء السجل كاملاً للتدقيق.");

        return RunOp(() =>
        {
            if (order.IsApproved) ReverseConsumption(order);
            order.IsApproved = false;
            order.Status = DocStatuses.Cancelled;
            if (!string.IsNullOrWhiteSpace(reason)) order.Notes = (order.Notes + $"\n[إلغاء {DateTime.Now:dd/MM/yyyy HH:mm}] {reason}").Trim();
            Db.SaveChanges();
            return OpResult.Success(
                $"تم إلغاء الأمر {order.DocumentNumber} وعكس صرفه إن وُجد.\n" +
                "المتبقي في الخطة أصبح متاحاً لأمر جديد.");
        });
    }

    /// <summary>عكس صرف الخام والمواد (لإلغاء الاعتماد/الإلغاء).</summary>
    private void ReverseConsumption(ProductionOrder order)
    {
        var whRaw = WarehouseId("WRM");
        var whAux = WarehouseId("WAUX");
        foreach (var item in order.Items.Where(i => i.LotId != null))
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == item.LotId);
            if (lot != null) { lot.InStockQtyKg += item.PlannedQtyKg; lot.ProducedQtyKg -= item.PlannedQtyKg; }
            Db.InventoryTransactions.RemoveRange(Db.InventoryTransactions.Where(t =>
                t.ReferenceDocType == ReferenceDocType.ProductionExecution && t.ReferenceDocNumber == order.DocumentNumber && t.LotId == item.LotId));
            var bal = Db.StockBalances.FirstOrDefault(b => b.WarehouseId == whRaw && b.LotId == item.LotId);
            if (bal != null) bal.QtyKg += item.PlannedQtyKg;
        }
        foreach (var mat in Db.ProductionOrderMaterials.Where(m => m.OrderId == order.Id && m.ActualIssuedQty > 0))
        {
            Db.InventoryTransactions.RemoveRange(Db.InventoryTransactions.Where(t =>
                t.ReferenceDocType == ReferenceDocType.MaterialIssue && t.ReferenceDocNumber.StartsWith(order.DocumentNumber) && t.MaterialId == mat.MaterialId));
            var bal = Db.StockBalances.FirstOrDefault(b => b.WarehouseId == whAux && b.MaterialId == mat.MaterialId);
            if (bal != null) bal.QtyKg += mat.ActualIssuedQty;
            mat.ActualIssuedQty = 0;
            mat.Status = DocStatuses.Draft;
        }
    }

    /// <summary>
    /// §23 — تعديل بيانات التنفيذ (التاريخ/الوردية/الخط/الملاحظات) فقط.
    /// الهوية (العميل/الصنف/المنتج/الخطة) مقفولة نهائياً، وبعد بدء الإنتاج لا تعديل إطلاقاً.
    /// </summary>
    public OpResult UpdateOrderHeader(int orderId, string productionDate = null, int? shiftId = null, int? lineId = null, string notes = null)
    {
        Require("production", "Edit");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.Status is DocStatuses.InProgress or DocStatuses.Stopped or DocStatuses.Completed or DocStatuses.Closed)
            return OpResult.Fail(
                "⛔ بعد بدء الإنتاج لا يمكن تعديل بيانات الأمر (قفل الهوية والتتبع).\n" +
                "إن حدث خطأ: أوقف/أقفل الأمر حسب الصلاحيات وأنشئ أمراً بديلاً — يبقى السجل السابق محفوظاً.");
        if (order.Status == DocStatuses.Cancelled) return OpResult.Fail("الأمر ملغي — لا يمكن تعديله.");

        return RunOp(() =>
        {
            if (productionDate != null)
            {
                if (!UiFormat.TryParseDate(productionDate, out var nd)) throw new DomainException("تاريخ غير صالح.");
                order.ProductionDate = nd;
            }
            if (shiftId != null)
            {
                if (!Db.Shifts.Any(s => s.Id == shiftId)) throw new DomainException("الوردية غير موجودة.");
                order.ShiftId = shiftId;
            }
            if (lineId != null)
            {
                if (!Db.ProductionLines.Any(l => l.Id == lineId)) throw new DomainException("خط الإنتاج غير موجود.");
                order.LineId = lineId;
            }
            if (notes != null) order.Notes = notes;

            // §الطاقة: الفتحة الجديدة يجب أن تتسع للأمر (مع استثنائه هو من الحساب)
            if (order.IsApproved) CheckOrderCapacity(order);

            Db.SaveChanges();
            return OpResult.Success($"تم تعديل بيانات التنفيذ للأمر {order.DocumentNumber} وأُعيد فحص الطاقة للفتحة الجديدة.");
        });
    }

    /// <summary>
    /// §B80 — تعديل بنود أمر إنتاج (مسودة لم يبدأ تنفيذها): كميات البنود القائمة فقط
    /// (كراتين/كجم) — الهوية مقفلة كما في تعديل الرأس. الحراس: كمية موجبة لكل بند،
    /// اتساق الكرتون/الكيلو، متبقي الخطة، طاقة الوردية، ثم إعادة احتساب المواد.
    /// </summary>
    public OpResult UpdateOrderItems(int orderId, List<OrderItemDto> items)
    {
        Require("production", "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("لا توجد بنود للتحديث.");
        var order0 = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == orderId);
        if (order0 == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order0.Status != DocStatuses.Draft)
            return OpResult.Fail("تعديل البنود متاح لأمر بحالة مسودة فقط — ألغِ الاعتماد أولاً إن كان معتمداً.");
        if (Db.ProductionExecutions.Any(e => e.OrderId == orderId))
            return OpResult.Fail("الأمر له جلسات تنفيذ مسجلة — لا يمكن تعديل بنوده.");
        foreach (var dz in items)
            if (dz.PlannedQtyKg <= 0 && dz.PlannedCartons <= 0)
                return OpResult.Fail("كمية البند يجب أن تكون أكبر من صفر — لا يُقبل بند بلا كمية.");

        return RunOp(() =>
        {
            var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId)
                        ?? throw new DomainException("أمر الإنتاج غير موجود.");
            if (order.Status != DocStatuses.Draft) throw new DomainException("تعديل البنود متاح لأمر بحالة مسودة فقط.");

            foreach (var dto in items)
            {
                if (dto.Id is not int itemId) continue;
                var it = order.Items.FirstOrDefault(x => x.Id == itemId)
                         ?? throw new DomainException($"البند رقم {itemId} غير موجود في هذا الأمر.");
                // الهوية (الصنف/الدفعة/العميل/الخطة) مقفولة — الكميات فقط
                double kg = UnitsPolicy.EnsureCartonKgConsistency(Db, it.ProductId, it.PackagingTypeId,
                    dto.PlannedQtyKg, dto.PlannedCartons, "تعديل أمر الإنتاج");
                if (dto.PlannedCartons > 0 && kg <= 0)
                {
                    double w = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId);
                    kg = w > 0 ? Math.Round(dto.PlannedCartons * w, 1) : kg;
                }
                if (kg <= 0) throw new DomainException("كمية البند يجب أن تكون أكبر من صفر — أدخل الكمية بالكيلو أو عدد الكراتين.");
                it.PlannedQtyKg = kg;
                if (dto.PlannedCartons > 0) it.PlannedCartons = dto.PlannedCartons;
            }

            Db.SaveChanges();
            CheckPlanRemaining(order);
            CheckOrderCapacity(order);
            CalculateMaterials(order);
            Db.SaveChanges();
            return OpResult.Success($"تم تعديل بنود الأمر {order.DocumentNumber} — أُعيد فحص متبقي الخطة والطاقة واحتساب المواد.");
        });
    }

    // ═══════════════════════════ بطاقة الملخص والسجل ═══════════════════════════

    public OrderCardDto GetOrderCard(int orderId)
    {
        var order = Db.ProductionOrders.AsNoTracking().Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return null;

        var firstLot = order.Items.Select(i => i.LotId).FirstOrDefault(l => l != null);
        var lot = firstLot != null ? Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == firstLot) : null;
        var shift = order.ShiftId != null ? Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == order.ShiftId) : null;

        // المخطط في الخطة للبنود المرتبطة (كجم + كرتون)
        var planItemIds = order.Items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId.Value).Distinct().ToList();
        var planItems = Db.ProductionPlanItems.AsNoTracking().Where(i => planItemIds.Contains(i.Id)).ToList();

        double acceptedKg = 0;
        var qcItems = Db.QualityCheckItems.AsNoTracking()
            .Join(Db.QualityChecks.AsNoTracking(), q => q.CheckId, c => c.Id, (q, c) => new { q, c })
            .Where(x => x.c.OrderId == orderId && x.c.IsApproved).ToList();
        acceptedKg = qcItems.Sum(x => x.q.AcceptedQtyKg);
        double rejectedKg = qcItems.Sum(x => x.q.RejectedQtyKg);

        double orderedKg = order.Items.Sum(i => i.PlannedQtyKg);
        double producedKg = order.Items.Sum(i => i.ProducedQtyKg);
        int orderedCartons = order.Items.Sum(i => i.PlannedCartons);
        int producedCartons = order.Items.Sum(i => i.ProducedCartons);

        // وقت النهاية المتوقع: تاريخ الإنتاج + بداية الوردية + ساعات الأمر بمعدل الصنف
        string expectedEnd = "-";
        double expectedHours = 0;
        if (order.ProductionDate != null && order.ShiftId != null)
        {
            foreach (var it in order.Items)
            {
                double rate = OrderRateFor(it.ProductId, order.ShiftId.Value, it.PackagingTypeId);
                    if (it.PlannedCartons > 0 && rate > 0) expectedHours += it.PlannedCartons / rate; // §B85/H4: بلا معدل لا يُقدَّر زمن
            }
            if (TimeSpan.TryParse(shift?.StartTime, out var startTs))
                expectedEnd = order.ProductionDate.Value.Date.Add(startTs).AddHours(expectedHours).ToString("dd/MM/yyyy HH:mm");
        }

        var card = new OrderCardDto
        {
            OrderId = order.Id,
            OrderNumber = order.DocumentNumber,
            Status = order.IsClosed ? DocStatuses.Closed : order.Status,
            StatusAr = DocStatuses.ToArabic(order.IsClosed ? DocStatuses.Closed : order.Status),
            CustomerName = order.CustomerId != null
                ? Db.Customers.AsNoTracking().Where(c => c.Id == order.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-"
                : "-",
            RawName = lot != null
                ? Db.Products.AsNoTracking().Where(p => p.Id == lot.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-"
                : "-",
            ProductName = string.Join(" + ", order.Items.Select(i => i.ProductId).Distinct()
                .Select(pid => Db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-")),
            PackName = string.Join(" + ", order.Items.Select(i => i.PackagingTypeId).Distinct()
                .Select(pid => pid == null ? "-" : Db.PackagingTypes.AsNoTracking().Where(p => p.Id == pid).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-")),
            PlanNumber = order.SourcePlanId != null
                ? Db.ProductionPlans.AsNoTracking().Where(p => p.Id == order.SourcePlanId).Select(p => p.DocumentNumber).FirstOrDefault() ?? "-"
                : "-",
            LotCode = lot?.LotCode ?? "-",
            ShipmentNumber = lot?.ShipmentId != null
                ? Db.Shipments.AsNoTracking().Where(s => s.Id == lot.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() ?? "-"
                : "-",
            ProductionDate = order.ProductionDate?.ToString("dd/MM/yyyy") ?? "-",
            ShiftName = shift != null ? $"{shift.ShiftNameAr} ({shift.StartTime}–{shift.EndTime})" : "-",
            LineName = order.LineId != null
                ? Db.ProductionLines.AsNoTracking().Where(l => l.Id == order.LineId).Select(l => l.LineNameAr).FirstOrDefault() ?? "-"
                : "-",
            StartTime = Db.ProductionExecutions.AsNoTracking().Where(e => e.OrderId == orderId && e.StartDateTime != null)
                .OrderBy(e => e.StartDateTime).Select(e => e.StartDateTime).FirstOrDefault()?.ToString("dd/MM/yyyy HH:mm") ?? "-",
            ExpectedEndTime = expectedEnd,
            PlannedInPlanKg = planItems.Sum(i => i.PlannedQtyKg),
            PlannedInPlanCartons = planItems.Sum(i => i.PlannedCartons),
            OrderedKg = orderedKg,
            OrderedCartons = orderedCartons,
            ProducedKg = producedKg,
            ProducedCartons = producedCartons,
            AcceptedKg = acceptedKg,
            RejectedKg = rejectedKg,
            RemainingKg = Math.Max(0, orderedKg - producedKg),
            ProgressPct = orderedKg > 0 ? Math.Round(Math.Min(100, producedKg / orderedKg * 100), 1) : 0,
            RatePerHour = order.Items.Count > 0 && order.ShiftId != null
                ? OrderRateFor(order.Items[0].ProductId, order.ShiftId.Value, order.Items[0].PackagingTypeId) : 0,
            ExpectedHours = Math.Round(expectedHours, 2),
            CreatedBy = Db.Users.AsNoTracking().Where(u => u.Id == order.CreatedBy).Select(u => u.FullName).FirstOrDefault() ?? "-",
            CreatedDate = order.CreatedDate.ToString("dd/MM/yyyy HH:mm")
        };
        return card;
    }

    public List<OrderEventDto> GetOrderEvents(int orderId)
    {
        var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == orderId);
        if (order == null) return new List<OrderEventDto>();
        var events = new List<(DateTime time, string user, string action, string detail)>();

        string UserName(int? id) => id == null ? "-" : Db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefault() ?? "-";
        string ActionAr(string a) => a switch
        {
            "Create" => "إنشاء الأمر",
            "Approve" => "اعتماد الأمر",
            "Cancel" => "إلغاء الأمر",
            "Edit" => "تعديل الأمر",
            "Issue" => "إصدار الأمر",
            _ => a
        };

        // الربط برقم المستند: حدث الإنشاء يُدقق قبل توليد المعرف (RecordId=0) فالرقم هو المرجع الثابت
        foreach (var a in Db.AuditLogs.AsNoTracking()
                     .Where(x => x.DocumentType == "ProductionOrder" && x.DocumentNumber == order.DocumentNumber)
                     .OrderBy(x => x.ActionDate).ToList())
            events.Add((a.ActionDate, a.UserName ?? "-", ActionAr(a.ActionType), ""));

        foreach (var e in Db.ProductionExecutions.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.Id).ToList())
        {
            if (e.StartDateTime != null)
                events.Add((e.StartDateTime.Value, UserName(e.CreatedBy), "بدء جلسة الإنتاج", $"الجلسة {e.DocumentNumber}"));
            if (e.IsDayClosed && e.EndDateTime != null)
                events.Add((e.EndDateTime.Value, UserName(e.ModifiedBy ?? e.CreatedBy), "إقفال يوم الإنتاج",
                    $"المنتَج {e.ActualQtyKg:N1} كجم ({e.ActualCartons:N0} كرتون) | حشف {e.HashfKg:N1} | نوى {e.NawaKg:N1} | هالك {e.WastageQtyKg:N1}"));
            else if (e.Status == DocStatuses.Completed && e.EndDateTime != null)
                events.Add((e.EndDateTime.Value, UserName(e.ModifiedBy ?? e.CreatedBy), "تسجيل إنتاج فعلي", $"{e.ActualQtyKg:N1} كجم"));
            foreach (var dt in Db.ExecutionDowntimes.AsNoTracking().Where(d => d.ExecutionId == e.Id))
                if (e.StartDateTime != null)
                    events.Add((e.StartDateTime.Value, UserName(e.CreatedBy), "تسجيل توقف", $"{dt.Hours:N1} ساعة — {dt.ReasonAr}"));
        }

        foreach (var c in Db.QualityChecks.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.Id).ToList())
        {
            if (c.CheckDate != null)
                events.Add((c.CheckDate.Value, UserName(c.ApprovedBy ?? c.CreatedBy),
                    c.IsApproved ? $"اعتماد الفحص — مقبول {c.AcceptedKg:N1} كجم" : "إرسال للفحص (فترة تبريد)",
                    c.DocumentNumber));
        }

        foreach (var r in Db.FinishedGoodsReceipts.AsNoTracking().Where(x => x.OrderId == orderId).ToList())
            if (r.DeliveryDate != null)
                events.Add((r.DeliveryDate.Value, UserName(r.ApprovedBy ?? r.CreatedBy),
                    r.ReceiptStatus == "Full" ? "استلام كامل في مخزن التام" : $"استلام في مخزن التام ({r.ReceiptStatus})", r.DocumentNumber));

        return events.OrderBy(e => e.time)
            .Select(e => new OrderEventDto { Time = e.time.ToString("dd/MM/yyyy HH:mm"), User = e.user, Action = e.action, Detail = e.detail })
            .ToList();
    }

    // ═══════════════════════════ المواد والإغلاق ═══════════════════════════

    public OpResult IssueMaterials(int orderId, Dictionary<int, double> qtys = null)
    {
        Require("materials", "Post");
        var order = Db.ProductionOrders.Include(o => o.Materials).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("الأمر غير موجود.");
        if (!order.IsApproved) return OpResult.Fail("لا يمكن صرف مواد لأمر غير معتمد.");

        return RunOp(() =>
        {
            var whAux = WarehouseId("WAUX");
            int count = 0;
            int seq = Db.InventoryTransactions.Count(t => t.ReferenceDocType == ReferenceDocType.MaterialIssue
                        && t.ReferenceDocNumber.StartsWith(order.DocumentNumber));
            foreach (var mat in order.Materials)
            {
                double req = qtys != null && qtys.TryGetValue(mat.MaterialId, out var qv) ? qv
                             : (mat.ActualIssuedQty == 0 ? mat.CalculatedQty : 0);
                if (req <= 0) continue;
                seq++;
                PostStockMovement(whAux, MovementType.Outbound, req, 0,
                    ReferenceDocType.MaterialIssue, $"{order.DocumentNumber}#ISS{seq}",
                    materialId: mat.MaterialId, orderId: order.Id,
                    notes: $"صرف مواد إضافي لأمر {order.DocumentNumber}");
                mat.ActualIssuedQty += req;
                count++;
            }
            Db.SaveChanges();
            return count == 0
                ? OpResult.Fail("لا توجد كميات جديدة للصرف.")
                : OpResult.Success($"تم صرف المواد لـ {count} بند.");
        });
    }

    public OpResult ConsumeMaterials(int orderId, int materialId, double consumed, double wasted, string reason = null)
    {
        Require("materials", "Edit");
        var pom = Db.ProductionOrderMaterials.FirstOrDefault(m => m.OrderId == orderId && m.MaterialId == materialId);
        if (pom == null) return OpResult.Fail("بند المادة غير موجود في الأمر.");
        if (consumed + wasted > pom.ActualIssuedQty + 0.001)
            return OpResult.Fail($"إجمالي المستهلك والهالك ({consumed + wasted:N1}) يتجاوز المصروف ({pom.ActualIssuedQty:N1}).");

        return RunOp(() =>
        {
            pom.ConsumedQty = consumed;
            pom.WastedQty = wasted;
            pom.Status = DocStatuses.Completed;
            Db.SaveChanges();
            return OpResult.Success("تم تسجيل الاستهلاك والهالك.");
        });
    }

    public OpResult ReturnUnusedMaterials(int orderId)
    {
        Require("materials", "Post");
        var order = Db.ProductionOrders.Include(o => o.Materials).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("الأمر غير موجود.");

        return RunOp(() =>
        {
            var whAux = WarehouseId("WAUX");
            int n = 0;
            foreach (var mat in order.Materials)
            {
                double unused = mat.ActualIssuedQty - mat.ConsumedQty - mat.WastedQty - mat.ReturnedQty;
                if (unused <= 0.001) continue;
                PostStockMovement(whAux, MovementType.Inbound, unused, 0,
                    ReferenceDocType.Return, order.DocumentNumber,
                    materialId: mat.MaterialId, orderId: order.Id,
                    notes: $"إرجاع فائض مواد من أمر {order.DocumentNumber}");
                mat.ReturnedQty += unused;
                n++;
            }
            Db.SaveChanges();
            return OpResult.Success(n == 0 ? "لا توجد مواد فائضة للإرجاع." : "تم إرجاع المواد الفائضة للمخزن.");
        });
    }

    /// <summary>§23 — حذف أمر مسودة لم يُعتمد.</summary>
    public OpResult DeleteOrder(int orderId)
    {
        Require("production", "Delete");
        var order = Db.ProductionOrders.FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.IsApproved) return OpResult.Fail("لا يمكن حذف أمر معتمد — ألغِ الاعتماد أو ألغِ الأمر.");
        if (Db.ProductionExecutions.Any(e => e.OrderId == orderId))
            return OpResult.Fail("لا يمكن حذف أمر له جلسات تنفيذ.");
        return RunOp(() =>
        {
            Db.ProductionOrders.Remove(order);
            Db.SaveChanges();
            return OpResult.Success("تم حذف أمر الإنتاج (المسودة).");
        });
    }

    /// <summary>§8 — لا يُغلق أمر لم يكتمل إنتاجه أو لم يُسلَّم إنتاجه.</summary>
    public OpResult CloseOrder(int orderId, string reason = null)
    {
        Require("production", "Cancel");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.IsClosed) return OpResult.Fail("الأمر مغلق مسبقاً.");

        return RunOp(() =>
        {
            // §B79: المنتَج يُقرأ من المصدرين معاً بلا ازدواج: بنود الأمر وجلسات التنفيذ — ويُعتمد الأكبر.
            double planned = order.Items.Sum(i => i.PlannedQtyKg);
            double produced = Math.Max(
                order.Items.Sum(i => i.ProducedQtyKg),
                Db.ProductionExecutions.AsNoTracking().Where(e => e.OrderId == order.Id).Sum(e => e.ActualQtyKg));
            bool complete = produced + 0.001 >= planned;
            // §B95 — المسار الواحد: لا إغلاق بعجز إلا بتسوية موثقة (سبب يُحفظ في الأمر ويظهر في فروقات الخطة).
            // بنود IsClosed الموروثة من المسار المحذوف تُحترم للبيانات القديمة فقط.
            bool legacySettled = order.Items.Count > 0 && order.Items.All(i => i.IsClosed);
            if (!complete && !legacySettled && string.IsNullOrWhiteSpace(reason))
                throw new DomainException(
                    $"لا يمكن إغلاق أمر إنتاج ناقص بلا تسوية.\nالأمر: المنتَج {produced:N1} كجم من أصل {planned:N1} كجم — العجز {planned - produced:N1} كجم.\n" +
                    "أدخل سبب التسوية (عطل معتمد/نقص خام/...) ليُحفظ موثقاً في الأمر.",
                    "INCOMPLETE_ORDER");
            order.IsClosed = true;
            order.ClosedDate = DateTime.Now;
            order.Status = DocStatuses.Closed;
            if (!complete && !legacySettled)
            {
                order.CloseReason = reason.Trim();
                Db.SaveChanges();
                return OpResult.Success($"تم إغلاق الأمر {order.DocumentNumber} بتسوية موثقة — العجز {planned - produced:N1} كجم: {reason.Trim()}.");
            }
            Db.SaveChanges();
            return OpResult.Success("تم إغلاق أمر الإنتاج.");
        });
    }

    /// <summary>§مواصفات العملاء: كرتون ماركة مستقلة لكل عميل — يمنع الخلط مع بقاء الحراس تحذيرية.</summary>
    public int ResolveAuxMaterial(DatesErp.Core.Domain.Entities.ConsumptionFormula f, int? customerId)
    {
        if (customerId != null)
        {
            var matGroup = Db.AuxiliaryMaterials.AsNoTracking().Where(m => m.Id == f.MaterialId).Select(m => m.GroupCode).FirstOrDefault();
            var spec = Db.AuxCustomerSpecs.AsNoTracking().Where(x => x.IsActive && x.CustomerId == customerId
                    && (x.ProductId == null || x.ProductId == f.ProductId)
                    && (x.PackagingTypeId == null || x.PackagingTypeId == f.PackagingTypeId))
                .OrderByDescending(x => x.Priority).FirstOrDefault();
            if (spec != null && (matGroup == "AG-CART" || spec.MaterialId == f.MaterialId))
                return spec.MaterialId;
        }
        return f.MaterialId;
    }

    private bool StrictAuxEnabled()
        => Db.SystemSettings.AsNoTracking().Any(x => x.SettingKey == "StrictAux" && x.SettingValue == "1");
}
