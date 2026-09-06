using DatesErp.Core.Domain.Entities;

namespace DatesErp.Core.Interfaces.Services;

/// <summary>نتيجة موحدة لعمليات الحفظ/الاعتماد.</summary>
public class OpResult
{
    public bool Ok { get; set; }
    public string Message { get; set; }
    public int Id { get; set; }
    public string DocumentNumber { get; set; }
    public static OpResult Success(string msg = null, int id = 0, string docNo = null)
    {
        // §رقم المستند يظهر دائماً عند الإنشاء/الحفظ مباشرة
        if (!string.IsNullOrWhiteSpace(docNo) && (msg == null || !msg.Contains(docNo)))
            msg = (msg ?? "") + $"\n📄 رقم المستند: {docNo}";
        return new OpResult { Ok = true, Message = msg, Id = id, DocumentNumber = docNo };
    }
    public static OpResult Fail(string msg) => new() { Ok = false, Message = msg };
}

/// <summary>بند استلام تمور.</summary>
public class ShipmentItemDto
{
    public int ProductId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int PackageCount { get; set; }
    public double UnitWeightKg { get; set; }
    public double QtyKg { get; set; }
    /// <summary>§نظام الوحدات: وحدة الاستلام الأصلية كما وصلت فعلياً (كرتون/سلة/كجم...).</summary>
    public string ReceiptUnit { get; set; }
    /// <summary>§استلام جزئي: Received مستلم | Rejected مرفوض/تالف | Pending معلّق لاحقاً.</summary>
    public string ItemStatus { get; set; } = "Received";
}

/// <summary>بند خطة إنتاج.</summary>
public class PlanItemDto
{
    public string SourceType { get; set; } = "Manual";
    public int? LotId { get; set; }
    public int? ShipmentId { get; set; }
    public int? CustomerId { get; set; }
    public int ProductId { get; set; }
    public int? PackagingTypeId { get; set; }
    public double PlannedQtyKg { get; set; }
    public int PlannedCartons { get; set; }
    public string ScheduledDate { get; set; }
    public int? SuggestedShiftId { get; set; }
    public int? SuggestedLineId { get; set; }
    public int PriorityNo { get; set; }
}

/// <summary>بند أمر إنتاج.</summary>
public class OrderItemDto
{
    /// <summary>§B80: معرف بند الأمر القائم — يُمرر في تعديل بنود أمر مسودة (UpdateOrderItems).</summary>
    public int? Id { get; set; }
    public int? PlanItemId { get; set; }
    public int? LotId { get; set; }
    public int? ShipmentId { get; set; }
    public int? CustomerId { get; set; }
    public int ProductId { get; set; }
    public int? PackagingTypeId { get; set; }
    public double PlannedQtyKg { get; set; }
    public int PlannedCartons { get; set; }
}

/// <summary>§أوامر الإنتاج: بند خطة قابل للتحويل إلى أمر — بكل مرجعه ومتبقيه (لا إعادة إدخال).</summary>
public class OrderableItemDto
{
    public int PlanItemId { get; set; }
    public int PlanId { get; set; }
    public string PlanNumber { get; set; }
    public string PlanTitle { get; set; }
    public string PlanDate { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public int? LotId { get; set; }
    public string LotCode { get; set; }
    /// <summary>الصنف المستلم (الخام) — بالكيلو.</summary>
    public string RawName { get; set; }
    public double LotRemainingKg { get; set; }
    /// <summary>المنتج النهائي — بالكرتون.</summary>
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    public int? PackagingTypeId { get; set; }
    public string PackName { get; set; }
    public double PlannedKg { get; set; }
    public int PlannedCartons { get; set; }
    /// <summary>ما أُنشئ له أوامر سابقة (غير ملغاة).</summary>
    public double OrderedKg { get; set; }
    public int OrderedCartons { get; set; }
    /// <summary>المتبقي القابل للأمر = المخطط − أوامر سابقة.</summary>
    public double RemainingKg { get; set; }
    public int RemainingCartons { get; set; }
    /// <summary>المنتَج فعلياً حتى الآن.</summary>
    public double ProducedKg { get; set; }
    public string ScheduledDate { get; set; }
    public int? SuggestedShiftId { get; set; }
    public int? SuggestedLineId { get; set; }
}

/// <summary>§B93 — نتيجة ترحيل خطة إلى أوامر: المنشأة + المتخطاة + الفاشلة بأسبابها.</summary>
public class PlanIssueResult
{
    public bool Ok { get; set; }
    public string Message { get; set; }
    public int PlanId { get; set; }
    public string PlanNumber { get; set; }
    public List<IssuedOrderDto> Created { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public List<string> Failed { get; set; } = new();
}

/// <summary>§B93 — أمر منشأ بالترحيل: مرجعه ومجموعته (تاريخ/وردية/خط).</summary>
public class IssuedOrderDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; }
    public string ProductionDate { get; set; }
    public string ShiftName { get; set; }
    public string LineName { get; set; }
    public int ItemsCount { get; set; }
    public double TotalKg { get; set; }
}

/// <summary>§بطاقة ملخص أمر الإنتاج — بيانات حية للعرض أعلى الأمر وشريط التقدم.</summary>
public class OrderCardDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; }
    public string Status { get; set; }
    public string StatusAr { get; set; }
    public string CustomerName { get; set; }
    public string RawName { get; set; }          // الصنف المستلم
    public string ProductName { get; set; }      // المنتج النهائي
    public string PackName { get; set; }
    public string PlanNumber { get; set; }
    public string LotCode { get; set; }
    public string ShipmentNumber { get; set; }
    public string ProductionDate { get; set; }
    public string ShiftName { get; set; }
    public string LineName { get; set; }
    public string StartTime { get; set; }
    public string ExpectedEndTime { get; set; }
    public double PlannedInPlanKg { get; set; }      // المخطط في الخطة (كجم)
    public double PlannedInPlanCartons { get; set; } // المخطط في الخطة (كرتون)
    public double OrderedKg { get; set; }            // كمية هذا الأمر
    public int OrderedCartons { get; set; }
    public double ProducedKg { get; set; }
    public int ProducedCartons { get; set; }
    public double AcceptedKg { get; set; }
    public double RejectedKg { get; set; }
    public double RemainingKg { get; set; }
    public double ProgressPct { get; set; }
    public double RatePerHour { get; set; }
    public double ExpectedHours { get; set; }
    public string CreatedBy { get; set; }
    public string CreatedDate { get; set; }
}

/// <summary>§سجل عمليات أمر الإنتاج: من فعل ماذا ومتى.</summary>
public class OrderEventDto
{
    public string Time { get; set; }
    public string User { get; set; }
    public string Action { get; set; }
    public string Detail { get; set; }
}

/// <summary>§فتحة طاقة يوم/وردية/خط لأوامر الإنتاج — تُحسب لحظياً من الأوامر وبنود الخطة.</summary>
public class OrderSlotInfo
{
    public int ShiftId { get; set; }
    public string ShiftName { get; set; }
    public string ShiftStart { get; set; }
    public string ShiftEnd { get; set; }
    public double ProductionHours { get; set; }    // ساعات الإنتاج الفعلية
    public double RatePerHour { get; set; }
    public int CapacityCartons { get; set; }       // الطاقة الكاملة للوردية
    public int UsedCartons { get; set; }           // المحجوز (أوامر + خطط) في نفس اليوم/الوردية/الخط
    public int RemainingCartons { get; set; }      // المتاح لأمر جديد
    public string CapacityNote { get; set; }       // §B85/H4: تنبيه الطاقة غير المعرَّفة (إن وجد)
}

/// <summary>بند استلام إنتاج تام.</summary>
public class FinishedGoodsItemDto
{
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int PackageCount { get; set; }
    public double NetWeightKg { get; set; }
    public double? ReceivedQtyKg { get; set; }
    /// <summary>§B96 — عميل البند (للمربوط بأمر تسليم: يُفرض من بند التسليم — وإلا عميل الأمر).</summary>
    public int? CustomerId { get; set; }
    /// <summary>§B96 — بند أمر التسليم المربوط (إجباري عند الربط بأمر تسليم).</summary>
    public int? DeliveryItemId { get; set; }
}

/// <summary>§B96 — بند أمر تسليم إنتاج: أمر + صنف + دفعة + عميل + كمية.</summary>
public class ProductionDeliveryItemDto
{
    public int? OrderId { get; set; }
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    public int? CustomerId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int PackageCount { get; set; }
    public double QtyKg { get; set; }
}

/// <summary>§B96 — سطر متاح في مصدر التسليم (للملء الآلي): الكمية والسقف والمتبقي.</summary>
public class DeliverySourceLine
{
    public int? OrderId { get; set; }
    public string OrderNumber { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    public int? LotId { get; set; }
    public string LotCode { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public double AvailableQtyKg { get; set; }
    public double DeliveredQtyKg { get; set; }
    public double RemainingQtyKg { get; set; }
}

/// <summary>§B96 — سياق مصدر التسليم (رأس + سطور قابلة للتسليم).</summary>
public class DeliverySourceContext
{
    public string SourceType { get; set; }
    public int SourceId { get; set; }
    public string SourceNumber { get; set; }
    public string SourceDate { get; set; }
    public List<DeliverySourceLine> Lines { get; set; } = new();
}

/// <summary>§B96 — بطاقة أمر تسليم (رأس + بنود + متبقيات).</summary>
public class ProductionDeliveryCard
{
    public int Id { get; set; }
    public string DocumentNumber { get; set; }
    public string DeliveryDate { get; set; }
    public string SourceType { get; set; }
    public string SourceTypeAr { get; set; }
    public int SourceId { get; set; }
    public string SourceNumber { get; set; }
    public string BypassReason { get; set; }
    public string Status { get; set; }
    public string StatusAr { get; set; }
    public string ReceiptStatus { get; set; }
    public List<ProductionDeliveryLineRow> Lines { get; set; } = new();
}

/// <summary>§B96 — سطر بطاقة أمر التسليم مع المتبقي للاستلام.</summary>
public class ProductionDeliveryLineRow
{
    public int Id { get; set; }
    public int? OrderId { get; set; }
    public string OrderNumber { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    public int? LotId { get; set; }
    public string LotCode { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public double QtyKg { get; set; }
    public double ReceivedQtyKg { get; set; }
    public double RemainingQtyKg { get; set; }
}

/// <summary>بند تسليم عميل.</summary>
public class CustomerDeliveryItemDto
{
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int PackageCount { get; set; }
    public double QtyKg { get; set; }
}

/// <summary>بند فحص جودة.</summary>
public class QualityItemDto
{
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    public double CheckedQtyKg { get; set; }
    public double AcceptedQtyKg { get; set; }
    public double RejectedQtyKg { get; set; }
    /// <summary>§B95 — كراتين البند (وحدة الإنتاج التام الأساسية): صفر = غير مُدخلة فيُعتمد الكيلو وحده.</summary>
    public double CheckedCartons { get; set; }
    public double AcceptedCartons { get; set; }
    public double RejectedCartons { get; set; }
    /// <summary>ملاحظات البند (حسب تصميم استمارة الفحص).</summary>
    public string Notes { get; set; }
}

/// <summary>§قرار الجودة ومعايير الفحص المخبري والحسي — المواصفة القياسية المعتمدة للتمور.</summary>
public class QualityLabDto
{
    /// <summary>Passed مطابق ومقبول للإفراج | Quarantine حجز وتحريز مؤقت | Rejected مرفوض/عوادم.</summary>
    public string Decision { get; set; } = "Passed";
    public double MoisturePct { get; set; } = 16.5;      // نسبة الرطوبة % — القياسي 14–18
    public double BrixDeg { get; set; } = 68.5;          // تركيز السكريات Brix° — القياسي ≥ 65
    public double SkinSeparationPct { get; set; } = 2.0; // نسبة انفصال القشرة % — الحد الأقصى 5
    public double ImpuritiesPct { get; set; } = 0.3;     // نسبة الشوائب والأتربة % — الحد الأقصى 1
    public int SampleCartons { get; set; } = 10;         // عينة الفحص المخبري (كرتون)
    public string InspectorNotes { get; set; }
}

/// <summary>§7 — عقد خدمات سير العمل الكامل: استلام ← دفعة ← خطة ← أمر ← صرف ← تنفيذ ← جودة ← تام ← تسليم.</summary>
/// <summary>§كشف تكرار رقم الحاوية قبل الحفظ.</summary>
public class DuplicateContainerMatch
{
    public int ShipmentId { get; set; }
    public string DocumentNumber { get; set; }
    public string CustomerName { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public double TotalWeightKg { get; set; }
    public bool IsApproved { get; set; }
}

public interface IReceivingService
{
    /// <summary>§حفظ سند الاستلام — warehouseId: مخزن الاستلام الفعلي، فارغ = الافتراضي WRM.</summary>
    OpResult SaveShipment(int customerId, string arrivalDate, string receivedDate, List<ShipmentItemDto> items, string notes = null, string containerNumber = null, int? receivedBy = null, int? existingId = null, int? warehouseId = null);
    /// <summary>§سندات سابقة بنفس رقم الحاوية (تحذير التكرار قبل الحفظ).</summary>
    List<DuplicateContainerMatch> FindDuplicateContainers(string containerNumber, int? excludeShipmentId = null);
    OpResult ApproveShipment(int shipmentId);
    /// <summary>§استلام جزئي: سند لاحق للبنود المعلّقة.</summary>
    OpResult ReceiveRemaining(int shipmentId);
    OpResult UnapproveShipment(int shipmentId);
    OpResult DeleteShipment(int shipmentId);
}

public interface IPlanningService
{
    OpResult SavePlan(string title, string planType, string startDate, string endDate, int? shiftId, int? lineId, List<PlanItemDto> items, string notes = null, string scopeMode = null, int? singleCustomerId = null);
    /// <summary>§تعديل خطة قائمة (مسودة غير معتمدة): يستبدل البنود ويعيد فحص الطاقة والأرصدة والحجوزات.</summary>
    OpResult UpdatePlan(int planId, string title, string planType, string startDate, string endDate, int? shiftId, int? lineId, List<PlanItemDto> items, string notes = null, string scopeMode = null, int? singleCustomerId = null);
    ShiftCapacityInfo GetShiftCapacityInfo(int shiftId, int lineId, string date, int? productId = null, int? excludePlanId = null);
    /// <summary>
    /// §المتاح لصنف محدد من دفعة (مطابق لـ v1.60 product_lot_remaining): يخصم فقط حجوزات
    /// هذا الصنف لا الأصناف الأخرى — سكري: 10000 − حجوز سكري فقط، برمي: 10000 − حجوز برمي فقط.
    /// </summary>
    double GetProductLotRemaining(int lotId, int productId, int? excludePlanId = null);
    OpResult SubmitPlan(int planId);
    OpResult ApprovePlan(int planId);
    OpResult ReturnPlan(int planId, string notes);
    OpResult UnapprovePlan(int planId);
    OpResult DeletePlan(int planId);
    OpResult ClosePlan(int planId, string notes);
    /// <summary>الدفعات المتاحة للتخطيط مع المتبقي بعد خصم الخطط النشطة — لكل الأصناف.</summary>
    List<AvailableLotDto> GetAvailableLots(int? customerId = null, DateTime? forDate = null);
    List<DatesErp.Core.Domain.Entities.Product> GetPlannableProducts(int? lotId = null);
    /// <summary>الأصناف التامة الصالحة للتخطيط في نافذة الاختيار: المجموعة 002 أو بدون مجموعة (مطابق لفلتر v1.59).</summary>
    List<Product> GetFinishedProducts();
    /// <summary>
    /// ⚖ معالج التوزيع العادل الآلي v2 (B87) — لفترة حرة (أسبوع، 20 يوماً، شهر...):
    /// فلتر تحويل رسمي لكل دفعة، حصة كل (صنف×عبوة) بمعدلها في ورديتها، أرصدة واعية
    /// بالأوامر الحية، تخطي الجمعة افتراضياً، وتعبئة كل الورديات النشطة (الأساسية أولاً).
    /// </summary>
    FairDistributionProposal SuggestFairDistribution(string startDate, string endDate, int shiftId, int lineId,
        int? targetProductId = null, double? dailyKgOverride = null, bool excludeFriday = true);
    /// <summary>§الإقفال اليومي: إقفال تلقائي للخطة إن اكتمل إنتاج كل بنودها — يحرر الحجوزات غير المستهلكة.</summary>
    OpResult TryAutoCloseIfComplete(int planId);
    /// <summary>
    /// §B91 — فحص الخطة متعددة العملاء: يوزّع بنود الخطة (عملاء/أصناف) على أيام الفترة (من–إلى)
    /// بنفس عمل محرك التوزيع (تخطي الجمعة، تعبئة الورديات بالترتيب، أولوية العميل، معدلات الطاقة،
    /// التحويل الرسمي، والالتزامات الحية) — ويحكم: قابلة للتنفيذ أم فيها عجز/تجاوز.
    /// </summary>
    PlanCheckResult CheckPlan(int planId, bool excludeFriday = true);
}

/// <summary>§B91 — نتيجة فحص الخطة: حكم + توزيع الأيام + تغطية العملاء والأصناف + تحذيرات صاخبة.</summary>
public class PlanCheckResult
{
    public bool Ok { get; set; }
    /// <summary>سطر الحكم النهائي (يُعرض في الشريط العلوي).</summary>
    public string Verdict { get; set; }
    public string PlanNumber { get; set; }
    public string PlanTitle { get; set; }
    public int WorkDays { get; set; }
    public int CustomersCount { get; set; }
    public int ItemsCount { get; set; }
    public double RequiredKg { get; set; }
    public double CoveredKg { get; set; }
    public double ShortageKg { get; set; }
    public List<PlanCheckDayDto> Days { get; set; } = new();
    public List<PlanCheckCustomerDto> Customers { get; set; } = new();
    public List<PlanCheckItemDto> Items { get; set; } = new();
    /// <summary>تحذيرات صاخبة — كل تجاوز يقول سببه (بلا معدل/بلا تحويل/عجز دفعة/عجز طاقة).</summary>
    public List<string> Warnings { get; set; } = new();
    public string CapacityNote { get; set; }
}

/// <summary>§B91 — يوم فحص: الحصة المطلوبة + المرحّل مقابل ما وُزع فعلاً والساعات المستخدمة.</summary>
public class PlanCheckDayDto
{
    public string Date { get; set; }
    /// <summary>مطلوب اليوم = الحصة اليومية + المرحّل من أمس.</summary>
    public double DemandKg { get; set; }
    /// <summary>ما وُزع فعلاً هذا اليوم (لا يتجاوز الطاقة أبداً).</summary>
    public double AllocatedKg { get; set; }
    public double HoursUsed { get; set; }
    public double HoursTotal { get; set; }
    public int LoadPct { get; set; }
    /// <summary>Easy مريح | Full ممتلئ | Short عجز مرحّل | Idle بلا حمل.</summary>
    public string Status { get; set; }
    public string StatusAr { get; set; }
}

/// <summary>§B91 — تغطية عميل في الخطة.</summary>
public class PlanCheckCustomerDto
{
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public double RequiredKg { get; set; }
    public double CoveredKg { get; set; }
    public double ShortageKg { get; set; }
    public string StatusAr { get; set; }
}

/// <summary>§B91 — تغطية بند (صنف) في الخطة.</summary>
public class PlanCheckItemDto
{
    public string CustomerName { get; set; }
    public string ProductName { get; set; }
    public string LotCode { get; set; }
    public double RequiredKg { get; set; }
    public double CoveredKg { get; set; }
    public double DailyCapKg { get; set; }
    /// <summary>أيام الإنتاج اللازمة لهذا البند وحده.</summary>
    public double DaysNeeded { get; set; }
    public string StatusAr { get; set; }
}

/// <summary>⚖ نتيجة معالج التوزيع العادل: بنود مقترحة + ملخص لكل عميل (في أي يوم يُنتج له).</summary>
public class FairDistributionProposal
{
    public bool Ok { get; set; }
    public string Message { get; set; }
    public List<FairPlanRowDto> Rows { get; set; } = new();
    public List<FairCustomerSummaryDto> Customers { get; set; } = new();
    public double TotalRemainingKg { get; set; }
    /// <summary>§B87/L3: أيام الإنتاج الفعلية فقط — الأيام الصفرية والجُمَع المستثناة لا تُحتسب.</summary>
    public int DaysUsed { get; set; }
    public double DailyQuotaKg { get; set; }
    /// <summary>§B87/H5: ملاحظات التجاوُز الصاخبة — دفعات بلا صنف مسموح أو بلا طاقة (تُعرَض للمشغّل).</summary>
    public List<string> SkippedNotes { get; set; } = new();
    /// <summary>
    /// §من أين جاء المعدل — حتى لا يظهر رقم بلا أساس.
    /// «لا طاقة معرَّفة» تعني أن الصنف لم يُعرَّف له طاقة في هذه الوردية.
    /// </summary>
    public string CapacityNote { get; set; }
}

/// <summary>بند مقترح من التوزيع العادل — بمرجعه الكامل (عميل/شحنة/دفعة/صنف/عبوة/يوم).</summary>
public class FairPlanRowDto
{
    public int PriorityNo { get; set; }
    public string Date { get; set; }
    /// <summary>§B87: الوردية المجدول فيها هذا البند (المحرك يملأ كل الورديات النشطة).</summary>
    public int ShiftId { get; set; }
    public string ShiftName { get; set; }
    /// <summary>§B87/M6: null = دفعة بلا عميل («بدون عميل») — لا صفر بعد اليوم.</summary>
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public int? ShipmentId { get; set; }
    public string ShipmentNo { get; set; }
    public string ContainerNumber { get; set; }
    public string ArrivalDate { get; set; }
    /// <summary>عدد أيام الشحنة في المخازن حتى اليوم (الأقدم أولوية الإنتاج).</summary>
    public int DaysInStock { get; set; }
    public int LotId { get; set; }
    public string LotCode { get; set; }
    public string RawName { get; set; }
    public double AvailableKg { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    public int PackagingTypeId { get; set; }
    public string PackName { get; set; }
    public int PlannedCartons { get; set; }
    public double PlannedQtyKg { get; set; }
}

/// <summary>ملخص نصيب كل عميل من خطة التوزيع العادل — يشمل أيام إنتاجه.</summary>
public class FairCustomerSummaryDto
{
    /// <summary>§B87/M6: null = تجميع دفعات «بدون عميل».</summary>
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public int ContainersCount { get; set; }
    public double TotalAvailableKg { get; set; }
    public double AllocatedKg { get; set; }
    public int AllocatedCartons { get; set; }
    public double ProgressRatio { get; set; }
    /// <summary>الأيام التي خُصص فيها إنتاج لهذا العميل (الإجابة المباشرة: في أي يوم ننتج له).</summary>
    public List<string> ProductionDays { get; set; } = new();
}

/// <summary>§مصدر الطاقة: صف في شبكة «الطاقة الإنتاجية حسب الوردية» داخل شاشة الصنف.</summary>
public class ProductCapacityRow
{
    public int ShiftId { get; set; }
    public string ShiftName { get; set; }
    public double ProductionHours { get; set; }   // ساعات الإنتاج الفعلية للوردية
    /// <summary>العبوة/المواصفة (فارغ = طاقة عامة للصنف بأي عبوة).</summary>
    public int? PackagingTypeId { get; set; }
    public string PackagingName { get; set; }
    public int MaxCapacity { get; set; }          // الطاقة القصوى (كرتون) — المُدخل من المسؤول
    public double RatePerHour { get; set; }       // معدل الإنتاج/ساعة — قيمة محسوبة
}

/// <summary>
/// §أمر التطوير: الطاقة الإنتاجية تُعرَّف لكل صنف لكل وردية من شاشة الأصناف فقط.
/// الوردية تحدد الوقت المتاح، والصنف يحدد طاقته، والمعدل يُحسب تلقائياً.
/// </summary>
public interface ICapacityService
{
    List<ProductCapacityRow> GetProductCapacities(int productId);
    /// <summary>طاقة عامة للصنف بأي عبوة.</summary>
    OpResult SetCapacity(int productId, int shiftId, int maxCartons);
    /// <summary>§طاقة حسب العبوة/المواصفة: عبوة محددة أو فارغة (عامة).</summary>
    OpResult SetCapacity(int productId, int shiftId, int? packagingTypeId, int maxCartons);
    (double rate, int capacity) GetCapacity(int productId, int shiftId);
    /// <summary>§الطاقة لصنف + عبوة + وردية — مع الرجوع للطاقة العامة إن لم تُعرَّف طاقة للعبوة.</summary>
    (double rate, int capacity) GetCapacity(int productId, int shiftId, int? packagingTypeId);
    /// <summary>إعادة حساب طاقات كل الأصناف بعد تغيير ساعات وردية.</summary>
    int RecomputeForShift(int shiftId);
    /// <summary>§B73: الإنتاج بالساعة للصنف — المصدر المعتمد للطاقات.</summary>
    OpResult SaveHourlyRate(int productId, double ratePerHour);
    OpResult ClearHourlyRate(int productId);
    double GetDayCapacity(int productId);
}

/// <summary>إدارة الورديات — الوقت فقط (لا طاقة للأصناف هنا).</summary>
public interface IShiftService
{
    OpResult SaveShift(int? id, string name, string start, string end, double totalHours, double downtimeHours, double effectiveHours);
    OpResult DeleteShift(int id);
}

/// <summary>معلومات الطاقة اللحظية للوردية/الخط في يوم.</summary>
public class ShiftCapacityInfo
{
    public string ShiftName { get; set; }
    public double TotalHours { get; set; }
    public double UsedHours { get; set; }
    public double RemainingHours { get; set; }
    public double HourlyRate { get; set; }
    public int MaxCartons { get; set; }
    public int RemainingCartons { get; set; }
}

public class AvailableLotDto
{
    public int LotId { get; set; }
    public string LotCode { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public int? ShipmentId { get; set; }
    public string ShipmentNo { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public double InitialQtyKg { get; set; }
    public double ReservedQtyKg { get; set; }
    public double RemainingKg { get; set; }

    // §المعالجة والتعقيم — أعمدة العرض التي تفسّر الرقم بدل أن يبدو نقصاً غامضاً
    /// <summary>هل الصنف يشترط معالجة؟ إن كان false فبقية الحقول لا تُقيّد شيئاً.</summary>
    public bool RequiresTreatment { get; set; }
    /// <summary>🟢 الجاهز للإنتاج الآن.</summary>
    public double ReadyNowKg { get; set; }
    /// <summary>🟠 تحت المعالجة — غير متاح.</summary>
    public double UnderTreatmentKg { get; set; }
    /// <summary>المتوقع اكتمال معالجته حتى تاريخ الخطة.</summary>
    public double ExpectedReadyByDateKg { get; set; }
    /// <summary>المتاح فعلياً للتخطيط في ذلك التاريخ بعد الحجز.</summary>
    public double AvailableForDateKg { get; set; }
}

public interface IProductionOrderService
{
    OpResult SaveOrder(string sourceType, int? sourcePlanId, int? customerId, string productionDate, int? shiftId, int? lineId, List<OrderItemDto> items);
    /// <summary>§بنود الخطة القابلة للتحويل إلى أوامر — بالمرجع الكامل والمتبقي بعد الأوامر السابقة.</summary>
    List<OrderableItemDto> GetOrderableItems(int planId);
    /// <summary>
    /// §B93 — ترحيل الخطة إلى أوامر: يحوّل بنود الخطة المعتمدة (ذات المتبقي) إلى أوامر إنتاج،
    /// أمر واحد لكل (تاريخ مجدول × وردية × خط) — مع فلاتر اختيارية للفترة والوردية.
    /// </summary>
    PlanIssueResult IssueOrdersFromPlan(int planId, string fromDate = null, string toDate = null, int? shiftId = null);
    /// <summary>§طاقة يوم/وردية/خط لهذا الصنف — لحساب التوزيع على الورديات ومنع التجاوز.</summary>
    OrderSlotInfo GetOrderSlot(int productId, int? packagingTypeId, int shiftId, int lineId, string date);
    /// <summary>§8 — الاعتماد يصرف المواد المحتسبة من المخازن ويخفض الأرصدة داخل معاملة ذرية.</summary>
    OpResult ApproveOrder(int orderId);
    OpResult UnapproveOrder(int orderId);
    /// <summary>§بدء الإنتاج: يسجل وقت البداية والمستخدم وينقل الأمر إلى «قيد التنفيذ».</summary>
    OpResult StartOrder(int orderId);
    /// <summary>§إيقاف مؤقت للأمر أثناء التنفيذ — يُستأنف لاحقاً.</summary>
    OpResult StopOrder(int orderId, string reason = null);
    /// <summary>§استئناف أمر متوقف.</summary>
    OpResult ResumeOrder(int orderId);
    /// <summary>§إلغاء الأمر (قبل الإنتاج) مع عكس الصرف إن كان معتمداً — يبقى في السجل للتدقيق.</summary>
    OpResult CancelOrder(int orderId, string reason = null);
    /// <summary>§تعديل بيانات التنفيذ (التاريخ/الوردية/الخط/الكمية) — ممنوع بعد بدء الإنتاج (قفل الهوية).</summary>
    OpResult UpdateOrderHeader(int orderId, string productionDate = null, int? shiftId = null, int? lineId = null, string notes = null);
    /// <summary>
    /// §B80: تعديل بنود أمر إنتاج مسودة (قبل الاعتماد وبدء التنفيذ): تحديث كميات البنود
    /// القائمة (كراتين/كجم) بمعرفات البنود — مع حراس الكمية الموجبة واتساق الكرتون/الكيلو
    /// ومتبقي الخطة وطاقة الوردية، وإعادة احتساب المواد المساعدة.
    /// </summary>
    OpResult UpdateOrderItems(int orderId, List<OrderItemDto> items);
    /// <summary>§بطاقة ملخص الأمر الحية (للشاشة وشريط التقدم والطباعة).</summary>
    OrderCardDto GetOrderCard(int orderId);
    /// <summary>§سجل العمليات: إنشاء/اعتماد/بدء/توقف/استئناف/إقفال — من فعل ماذا ومتى.</summary>
    List<OrderEventDto> GetOrderEvents(int orderId);
    OpResult IssueMaterials(int orderId, Dictionary<int, double> qtys = null);
    OpResult ConsumeMaterials(int orderId, int materialId, double consumed, double wasted, string reason = null);
    OpResult ReturnUnusedMaterials(int orderId);
    /// <summary>§B95 — إغلاق الأمر: مكتمل الإنتاج يُغلق مباشرة، والناقص يتطلب سبب تسوية موثقاً يُحفظ في الأمر.</summary>
    OpResult CloseOrder(int orderId, string reason = null);
    OpResult DeleteOrder(int orderId);
}

public interface IExecutionService
{
    /// <summary>
    /// §نموذج إقفال الخطة اليومي (بديل جلسة التنفيذ): إنزال من أمر التشغيل —
    /// المفترض إنتاجه مقابل المنتَج والمخرجات (كراتين/حشف/نوى/هالك) والمتبقي في الصالة
    /// (يُرحَّل اختيارياً لليوم التالي) والتوقفات بسببها، ثم الإرسال للجودة
    /// (فحص متوقع بعد يومَي تبريد) وإقفال اليوم استعداداً لأمر جديد.
    /// </summary>
    /// <param name="byProducts">
    /// §المخرجات الثانوية بأصنافها من جدول ByProducts — لا «حشف/نوى» مفروضة في الكود.
    /// المعاملان hashfKg/nawaKg بقيا للبيانات والتوافق القديم فقط.
    /// </param>
    /// <param name="consumedRawKg">
    /// §الخام المستهلك فعلياً في العملية. لا يُشتق من وزن المنتج التام — فلا معادلة
    /// ثابتة تربطهما، لأن وزن الخارج يزيد عن الداخل لإضافة الماء أثناء التشغيل.
    /// </param>
    OpResult CloseProductionDay(int orderId, double producedKg, int producedCartons,
        double hashfKg, double nawaKg, double wastageKg, bool carryToNextDay,
        List<DowntimeDto> downtimes, bool sendToQuality, string notes = null,
        List<ByProductQtyDto> byProducts = null, double consumedRawKg = 0,
        List<CloseItemQtyDto> itemQtys = null,
        // §B95 — استوعب المسار المحذوف: تسوية المواد المساعدة + توريد الكرتون الفارغ (اختيارية كلها)
        List<AuxActualDto> actualAux = null, double? emptyCartonsActual = null, int? cartonWarehouseId = null);
    // §B95 — حُذف ClosePlanItems نهائياً: كان مسار إقفال موازياً ميتاً (لا تستدعيه أي شاشة)
    // يكرر منطق الإقفال اليومي برياضيات مختلفة. المسار الرسمي الوحيد: CloseProductionDay
    // عبر أمر الإنتاج (استوعب تسوية المواد المساعدة وتوريد الكرتون الفارغ).
    // جداول PlanClosing* باقية للقراءة التاريخية (التقارير/التقدم) ولا تُكتب فيها صفوف جديدة.
}

/// <summary>§B88/M13: إنتاج بند أمر في إقفال اليوم — الإقفال متعدد الأصناف: كل بند بكميته (كجم + كراتين) للفحص والتوزيع الدقيق.</summary>
public class CloseItemQtyDto
{
    public int OrderItemId { get; set; }
    public double ProducedKg { get; set; }
    public int ProducedCartons { get; set; }
}

/// <summary>بند توقف في إقفال اليوم: «توقفنا كذا ساعة بسبب كذا».</summary>
public class DowntimeDto
{
    public double Hours { get; set; }
    public string ReasonAr { get; set; }
    /// <summary>§السجل الزمني للتوقف (HH:mm) — يُطبع في بطاقة جلسة التشغيل.</summary>
    public string StartTime { get; set; }
    public string EndTime { get; set; }
}

/// <summary>بند إقفال على مستوى بند الخطة: الخطة كاملة أو صنف واحد أو جزء من صنف.</summary>
public class PlanClosingItemDto
{
    public int PlanItemId { get; set; }
    /// <summary>§الخام المستهلك فعلياً لهذا البند — لا يُشتق من وزن المنتج التام.</summary>
    public double ConsumedRawKg { get; set; }
    /// <summary>الكمية المنتجة في هذا الإقفال — إن تركت صفراً تُعبأ بالمتبقي كاملاً (إقفال الصنف كله).</summary>
    public double ProducedKg { get; set; }
    public int ProducedCartons { get; set; }
    public double HashfKg { get; set; }
    public double NawaKg { get; set; }
    public double WastageKg { get; set; }
    /// <summary>§لا ثوابت: المخرجات الثانوية الفعلية بأصنافها من جدول ByProducts.</summary>
    public List<ByProductQtyDto> ByProducts { get; set; } = new();
}

public class AuxActualDto
{
    public int OrderId { get; set; }
    public int MaterialId { get; set; }
    public double Qty { get; set; }
}

public class ByProductQtyDto
{
    public int ByProductId { get; set; }
    public double QtyKg { get; set; }
}

/// <summary>نتيجة فحص مُدخلة: نوع النتيجة + الكمية + وحدتها.</summary>
public class InspectionResultDto
{
    public int ResultTypeId { get; set; }
    public double Qty { get; set; }
    /// <summary>فارغ = استخدم الوحدة المعتمدة للنوع/الصنف.</summary>
    public int? UnitId { get; set; }
    public int? ProductId { get; set; }
    public int? LotId { get; set; }
    public string Notes { get; set; }
}

/// <summary>نوع نتيجة كما يُعرض في الشاشة — الوحدة مضمّنة فلا يحتاج الـUI استعلاماً ثانياً.</summary>
public class AllowedResultType
{
    public int ResultTypeId { get; set; }
    public string Code { get; set; }
    public string NameAr { get; set; }
    public string ResultKind { get; set; }
    public string ResultKindAr { get; set; }
    public int? UnitId { get; set; }
    public string UnitLabel { get; set; }
    public bool IsFinishedGood { get; set; }
    public bool IsByProduct { get; set; }
    public bool EntersInventory { get; set; }
    public bool CountsAsLoss { get; set; }
    /// <summary>§B95 — للنوع المرفوض: false = غير مطابق، true = مرفوض نهائي.</summary>
    public bool IsFinalScrap { get; set; }
    /// <summary>§B95 — درجة النوع في معادلة الإنتاج التام (مطابق/غير مطابق/مرفوض) — فارغة للمخرج الثانوي والفاقد.</summary>
    public string QualityGrade { get; set; }
    public string QualityGradeAr { get; set; }
    public double DefaultQty { get; set; }
    public bool IsMandatory { get; set; }
    public int SortNo { get; set; }
}

/// <summary>إجمالي مجموعة كمية بوحدتها — لا يُدمج مع وحدة أخرى إلا بتحويل معرَّف.</summary>
public class UnitTotal
{
    public int? UnitId { get; set; }
    public string UnitLabel { get; set; }
    public double Checked { get; set; }
    public double Accepted { get; set; }
    public double Rejected { get; set; }
    /// <summary>§B95 — تفصيل المرفوض: غير مطابق (قابل للمعالجة) + مرفوض نهائي — مجموعهما = Rejected.</summary>
    public double NonConforming { get; set; }
    public double Scrap { get; set; }
    public double ByProduct { get; set; }
    public double Loss { get; set; }
}

/// <summary>نتيجة حساب الفحص — الإجماليات لكل وحدة على حدة + النسب.</summary>
public class InspectionTotals
{
    public List<UnitTotal> ByUnit { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>كمية كل نوع نتيجة (مجموع عبر الوحدات — للتقرير التفصيلي فقط).</summary>
    public List<(int ResultTypeId, string NameAr, double Qty, string UnitLabel)> ByResultType { get; set; } = new();

    public double TotalChecked { get; set; }
    public double TotalAccepted { get; set; }
    public double TotalRejected { get; set; }
    /// <summary>§B95 — تفصيل المرفوض عبر الوحدات: مجموعهما = TotalRejected.</summary>
    public double TotalNonConforming { get; set; }
    public double TotalScrap { get; set; }
    public double TotalByProduct { get; set; }
    public double TotalLoss { get; set; }

    /// <summary>نسبة القبول ٪ — تُحسب داخل الوحدة الواحدة فقط (لا خلط وحدات).</summary>
    public double? AcceptancePct { get; set; }
    public double? ByProductPct { get; set; }
    public double? LossPct { get; set; }

    /// <summary>صحيح فقط إن كانت كل الكميات بوحدة واحدة — وإلا فالنسب تُحسب لكل وحدة.</summary>
    public bool SingleUnit { get; set; }
    public string PrimaryUnitLabel { get; set; }
}

/// <summary>§B95 — صف في جدول «نتيجة فحص الإنتاج التام»: الدرجة + الكمية + النسبة التلقائية + الملاحظات.</summary>
public class GradeSummaryRow
{
    /// <summary>Conforming | NonConforming | Scrap.</summary>
    public string Grade { get; set; }
    public string GradeAr { get; set; }
    public double Qty { get; set; }
    /// <summary>النسبة ٪ من الكمية المنتجة (لا شيء إن تعذّر التعبير عن المنتَج بهذه الوحدة).</summary>
    public double? PctOfProduced { get; set; }
    public string Notes { get; set; }
}

/// <summary>§B95 — ملخص نتيجة فحص الإنتاج التام بوحدة الصنف (الكرتون): 3 درجات + إجمالي + مطابقة مع المنتَج.</summary>
public class GradeSummary
{
    public List<GradeSummaryRow> Rows { get; set; } = new();
    public string UnitLabel { get; set; }
    public int? UnitId { get; set; }
    /// <summary>الكمية المنتجة بوحدة الملخص — فارغة إن تعذّر التعبير عنها (بلا كراتين مسجلة ولا وزن معرَّف).</summary>
    public double? ProducedQty { get; set; }
    public double TotalQty { get; set; }
    public double? TotalPct { get; set; }
    /// <summary>متوازن: الإجمالي = المنتَج (ضمن التفاوت) — شرط الاكتمال والاعتماد.</summary>
    public bool Balanced { get; set; }
    public List<string> Warnings { get; set; } = new();
}

/// <summary>سياق أمر الإنتاج كما يظهر في رأس شاشة الفحص — يُجلب آلياً ولا يُعاد إدخاله.</summary>
public class InspectionOrderContext
{
    public int OrderId { get; set; }
    public string OrderNo { get; set; }
    public string PlanNo { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public string RawItemName { get; set; }
    public string RawItemCode { get; set; }
    public int? FinishedProductId { get; set; }
    public string FinishedProductName { get; set; }
    public string FinishedProductCode { get; set; }
    public double ProducedQty { get; set; }
    public string ProducedUnitLabel { get; set; }
    public double ProducedQtyKg { get; set; }
    /// <summary>§B95 — الكراتين المنتجة (وحدة الإنتاج التام الأساسية) — صفر إن سُجل الإنتاج وزناً فقط.</summary>
    public int ProducedCartons { get; set; }
    /// <summary>§B95 — تاريخ الإنتاج من أمر التشغيل (يُعرض في رأس المحضر — لا يُعاد إدخاله).</summary>
    public string ProductionDate { get; set; }
    public string Date { get; set; }
    public string ShiftName { get; set; }
    public string LineName { get; set; }
    public string LotCode { get; set; }
    public int? LotId { get; set; }
    /// <summary>بنود الأمر: (الصنف، الدفعة، العميل، الكمية المنتجة).</summary>
    public List<(int ProductId, string ProductName, int? LotId, string LotCode, int? CustomerId, double QtyKg)> Items { get; set; } = new();
}


public interface IQualityService
{
    /// <summary>حفظ فحص جودة — الأمر اختياري (الفحص اليدوي بلا أمر) مع قرار الجودة ومعايير الفحص المخبري.</summary>
    OpResult SaveCheck(int? orderId, int? executionId, string checkDate, string checkType, List<QualityItemDto> items,
        List<(int byProductId, double qtyKg)> byProducts = null, QualityLabDto lab = null);
    OpResult ApproveCheck(int checkId);
    /// <summary>§B95 — تصحيح معتمد على فحص معتمد: يتطلب صلاحية (الجودة/تعديل بعد الاعتماد) وسبباً مسجلاً.</summary>
    OpResult RequestCorrection(int checkId, string reason);
}

/// <summary>
/// §الفحص الديناميكي — أنواع نتائج قابلة للتعريف من الإعدادات، وحدات من القاموس،
/// ربط كامل بأمر الإنتاج، وحسابات لا تجمع وحدات مختلفة إلا بتحويل معرَّف.
/// </summary>
public interface IInspectionService
{
    /// <summary>§3 — تعريف/تعديل نوع نتيجة فحص (اسم + تصنيف + وحدة + أعلام المخزون والفاقد).</summary>
    OpResult SaveResultType(int? id, string code, string nameAr, string resultKind,
        int? unitId, bool isFinishedGood, bool isByProduct, bool entersInventory,
        bool countsAsLoss, int sortNo, bool isActive, string notes = null, bool isFinalScrap = false);

    List<AllowedResultType> GetResultTypes(bool includeInactive = false);

    /// <summary>§7 — نتائج الفحص المسموحة لصنف بوحدتها المعتمدة (ملف الصنف ← المجموعة ← العام).</summary>
    List<AllowedResultType> GetAllowedResultTypesForItem(int? productId, string groupCode = null);

    OpResult SetProfile(int? id, int? productId, string groupCode, int resultTypeId,
        int? unitId, decimal defaultQty, bool isMandatory, int sortNo, bool isActive);

    /// <summary>§6 — تعريف تحويل وحدات؛ بدونه لا يُجمع مقداران بوحدتين مختلفتين.</summary>
    OpResult SaveConversion(int? id, int fromUnitId, int toUnitId, decimal factor, bool isActive);

    /// <summary>§1 — بيانات الفحص تُجلب آلياً من أمر الإنتاج (لا إعادة إدخال).</summary>
    InspectionOrderContext GetOrderContext(int orderId);

    /// <summary>§8 — التحقق: النوع معرَّف، الوحدة معرفة ومسموحة، لا صنف خارج الأمر، الإجباري مُدخل.</summary>
    void ValidateResults(List<InspectionResultDto> results, int? orderId = null, int? productId = null);

    /// <summary>§B95 — التحقق الإجباري ضد الإنتاج: الأمر موجود، إنتاج مسجل، أصناف تامة (002) فقط، لا تجاوز للمنتَج.</summary>
    void ValidateAgainstProduction(List<InspectionResultDto> results, int orderId, int? productId = null);

    /// <summary>§B95 — ملخص نتيجة فحص الإنتاج التام: 3 درجات بوحدة الصنف + نسب تلقائية + مطابقة مع المنتَج.</summary>
    GradeSummary ComputeGradeSummary(List<InspectionResultDto> results, int? orderId, int? productId);

    /// <summary>§6 — الإجماليات والنسب لكل وحدة على حدة.</summary>
    InspectionTotals Compute(List<InspectionResultDto> results);

    /// <summary>§6 — إجمالي موحّد عبر التحويلات المعرَّفة فقط (null + سبب إن وُجدت وحدة بلا تحويل).</summary>
    double? ComputeConvertedTotal(List<InspectionResultDto> results, int toUnitId, out string failureReason);

    string UnitName(int unitId);
}

public interface IFinishedGoodsService
{
    OpResult SaveReceipt(int orderId, int? qualityCheckId, string deliveryDate, List<FinishedGoodsItemDto> items, int? deliveryId = null);
    /// <summary>الإصدار للمخزن — لا يمس الأرصدة.</summary>
    OpResult Issue(int receiptId);
    /// <summary>§7 — سند الاستلام المخزني هو وحده ما يؤثر على الأرصدة (كلي/جزئي لكل صنف).</summary>
    OpResult Receive(int receiptId, Dictionary<int, double> receivedByItemId);
    OpResult Unapprove(int receiptId);
}

/// <summary>§B96 — أوامر تسليم الإنتاج (إدارة الإنتاج — يحررها مدير الإنتاج).</summary>
public interface IProductionDeliveryService
{
    /// <summary>إنشاء أمر تسليم من مصدر (محضر معتمد/خطة/إقفال خطة — الأخيران تجاوز بصلاحية وسبب).</summary>
    OpResult SaveDelivery(string sourceType, int sourceId, string deliveryDate, List<ProductionDeliveryItemDto> items, string bypassReason = null, string notes = null);
    /// <summary>تحرير الأمر للمخزن (مدير الإنتاج) — لا يمس الأرصدة.</summary>
    OpResult IssueDelivery(int deliveryId);
    /// <summary>إلغاء الأمر (مسودة دائماً — مُصدَر فقط إن لم يبدأ استلامه).</summary>
    OpResult CancelDelivery(int deliveryId);
    /// <summary>سياق المصدر للملء الآلي (سطور + متاح/مُسلَّم/متبقي).</summary>
    DeliverySourceContext GetSourceContext(string sourceType, int sourceId);
    /// <summary>مستندات مصدر صالحة للاختيار (محاضر معتمدة/خطط معتمدة/خطط مقفلة).</summary>
    List<(int Id, string Label)> GetSourceDocs(string sourceType);
    /// <summary>بطاقة أمر تسليم (رأس + بنود + متبقيات).</summary>
    ProductionDeliveryCard GetDelivery(int deliveryId);
    /// <summary>قائمة الأوامر (الأحدث أولاً — اختيارياً بحالة).</summary>
    List<ProductionDeliveryCard> GetDeliveries(string statusFilter = null);
}

public interface ICustomerDeliveryService
{
    OpResult Save(int customerId, string deliveryDate, int? orderId, List<CustomerDeliveryItemDto> items);
    OpResult Approve(int deliveryId);
    OpResult Unapprove(int deliveryId);
}

// ═══════════════════════════════════════════════════════════════
// §الخطة الطويلة: خطة واحدة ← أيام ← وردية ← عميل ← صنف ← كمية
// ═══════════════════════════════════════════════════════════════

public class PlanRowDto
{
    public int ItemId { get; set; }
    public int PlanId { get; set; }
    public string PlanNumber { get; set; }
    public string Date { get; set; }
    public int? ShiftId { get; set; }
    public string ShiftName { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public string LotCode { get; set; }
    public string ShipmentNo { get; set; }
    public string ProductName { get; set; }
    public string PackName { get; set; }
    public double PlannedKg { get; set; }
    public int PlannedCartons { get; set; }
    public double ProducedKg { get; set; }
    public double AcceptedKg { get; set; }
    public double DeliveredKg { get; set; }
    public double RemainingKg { get; set; }
    public string ExecStatusAr { get; set; }
    // الطاقة: الصنف + الوردية + ساعات الإنتاج الفعلية
    public double RatePerHour { get; set; }
    public int MaxCapacity { get; set; }
    public double RequiredHours { get; set; }
    public double HoursUsedOnDay { get; set; }
    public double HoursRemainingOnDay { get; set; }
}

public class CustomerProgressDto
{
    public int CustomerId { get; set; }
    public string CustomerName { get; set; }
    public double Planned { get; set; }
    public double Produced { get; set; }
    public double Accepted { get; set; }
    public double Delivered { get; set; }
    public double Remaining { get; set; }
    public string StatusAr { get; set; } // مكتمل | جزئي | لم يبدأ
}

public class DayStatusDto
{
    public string Date { get; set; }
    public int RowsCount { get; set; }
    public double PlannedKg { get; set; }
    public double ProducedKg { get; set; }
    public string StatusAr { get; set; } // مكتمل | جزئي | غير مكتمل
}

public class BillableDto
{
    public int DeliveryId { get; set; }
    public string DeliveryNumber { get; set; }
    public string Date { get; set; }
    public double TotalQtyKg { get; set; }
    public double InvoicedQtyKg { get; set; }
    public double BillableQtyKg { get; set; }
}

public interface IPlanProgressService
{
    /// <summary>§5 — خطة يوم محدد (لمدير الإنتاج: يرى بنود اليوم فقط، لكل العملاء والورديات).</summary>
    List<PlanRowDto> GetDailyPlan(string date, int? planId = null);
    /// <summary>§6 — حالة كل عميل مستقلة داخل الخطة: مخطط/منتج/مقبول/مسلّم/متبقي.</summary>
    List<CustomerProgressDto> GetPlanProgressByCustomer(int planId);
    /// <summary>حالة كل يوم في الخطة: مكتمل / جزئي / غير مكتمل.</summary>
    List<DayStatusDto> GetPlanDayStatuses(int planId);
    /// <summary>§3 — تعديل بند مستقبلي (تاريخ/كمية/وردية/عميل/صنف/عبوة) مع إعادة فحص الطاقة تلقائياً. الأيام المنفذة لا تُعدل إلا بصلاحية.</summary>
    OpResult UpdatePlanItem(int itemId, string newDate = null, double? newQtyKg = null, int? newShiftId = null, int? newCustomerId = null,
        int? newProductId = null, int? newPackagingTypeId = null);
    /// <summary>§9 — الكميات القابلة للفوترة = المسلَّم فعلياً لكل عميل.</summary>
    List<BillableDto> GetBillableDeliveries(int customerId);
    /// <summary>§9 — تسجيل فوترة كمية من سند تسليم — مع منع تكرار الفوترة لنفس الكمية.</summary>
    OpResult MarkInvoiced(int deliveryId, double qty);
}

// ═══════════════════════════════════════════════════════════════
// §تتبع الصنف: الصنف المستلم هو هوية المادة حتى نهاية الدورة
// استلام → خطة → أمر → إنتاج → فحص → مخزون → تسليم → فاتورة
// ═══════════════════════════════════════════════════════════════

/// <summary>مرحلة واحدة في رحلة الصنف (استلام/خطة/أمر/إنتاج/فحص/مخزون/تسليم/فاتورة).</summary>
public class TraceStageDto
{
    public string StageAr { get; set; }       // المرحلة
    public string DocNumber { get; set; }     // رقم المستند
    public string Date { get; set; }
    public string CustomerName { get; set; }
    public string ProductName { get; set; }   // الاسم الفعلي للصنف — لا أسماء عامة أبداً
    public string LotCode { get; set; }
    public double QtyKg { get; set; }
    public int Cartons { get; set; }
    public string StatusAr { get; set; }
    public string Detail { get; set; }
}

/// <summary>رحلة صنف كاملة من الاستلام حتى الفاتورة — مع الإجماليات والمتبقي.</summary>
public class ProductJourneyDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    public string ItemTypeAr { get; set; }    // خام | تام
    /// <summary>للمنتج التام: الصنف الخام المصدر حسب بطاقة المنتج.</summary>
    public int? SourceProductId { get; set; }
    public string SourceProductName { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public List<TraceStageDto> Stages { get; set; } = new();
    // الإجماليات عبر الرحلة
    public double ReceivedKg { get; set; }    // المستلم من الخام
    public double PlannedKg { get; set; }
    public double ProducedKg { get; set; }
    public double AcceptedKg { get; set; }    // المقبول من الفحص
    public double InStockKg { get; set; }     // في مخزن التام
    public double DeliveredKg { get; set; }
    public double InvoicedKg { get; set; }
    public double RemainingKg { get; set; }   // المتبقي في المخزون التام
}

/// <summary>
/// §تتبع الصنف: فتح أي صنف يعرض رحلته كاملة — الاستلام، الخطة، الأمر، الإنتاج الفعلي،
/// الفحص، المخزون، التسليم، الفاتورة، والمتبقي. الهوية لا تضيع في أي مرحلة.
/// </summary>
public interface ITraceabilityService
{
    /// <summary>رحلات الأصناف — تصفية اختيارية بالعميل و/أو الصنف (خاماً كان أو تاماً).</summary>
    List<ProductJourneyDto> GetJourneys(int? customerId = null, int? productId = null);
    /// <summary>رحلة الدفعة: تتبع كامل بدءاً من دفعة محددة (بعميلها وصنفها).</summary>
    ProductJourneyDto GetLotJourney(int lotId);
}
