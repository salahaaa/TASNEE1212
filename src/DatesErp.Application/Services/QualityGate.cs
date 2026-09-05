using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §إصلاح حرج — بوابة جودة مركزية واحدة.
///
/// الوضع في B18: قرار الجودة كان مُهمَلاً في أربع حلقات متتالية، وأُثبت بالتشغيل أن
/// دفعة قرارها «مرفوض تماماً» اعتُمدت ودخلت مخزن التام وسُلّمت للعميل:
///   1) ExecutionService.ApproveCheck        — لا يفحص Decision إطلاقاً
///   2) FinishedGoodsService.SaveReceipt     — تشترط وجود فحص فقط
///   3) DeliveryView.Save                    — تمرر orderId = null
///   4) CustomerDeliveryService.Approve      — البوابة داخل if (OrderId is int) فلا تُنفَّذ أبداً
///
/// الإصلاح: بوابة واحدة تفحص القرار (لا مجرد الاعتماد)، وتُشتق الأوامر من الدفعة/الصنف
/// إن غاب معرّف الأمر — فلا يمكن تجاوزها بتمرير null.
/// </summary>
public static class QualityGate
{
    public const string Passed = "Passed";
    public const string Quarantine = "Quarantine";
    public const string Rejected = "Rejected";

    /// <summary>
    /// هل يُسمح بتسليم هذه البضاعة للعميل؟
    /// يفحص: وجود فحص ← اعتماده ← قراره (مرفوض/محجوز يمنع التسليم).
    /// </summary>
    public static (bool ok, string reason) CustomerDeliveryAllowed(
        DatesErpDbContext db, int? orderId, int? lotId, int? productId)
    {
        var orderIds = new List<int>();
        if (orderId != null) orderIds.Add(orderId.Value);

        // §إصلاح: الواجهة كانت تمرر orderId = null فتتخطى البوابة كلياً.
        // نشتق الأوامر من الدفعة/الصنف إن غاب المعرّف.
        if (orderIds.Count == 0 && lotId != null)
        {
            orderIds = db.ProductionOrderItems.AsNoTracking()
                .Where(i => i.LotId == lotId && (productId == null || i.ProductId == productId))
                .Select(i => i.OrderId).Distinct().ToList();
        }

        if (orderIds.Count == 0)
            return (false,
                "⛔ لا يمكن التسليم للعميل: لا يوجد أمر إنتاج مرتبط بهذه الدفعة للتحقق من نتيجة فحص الجودة.\n" +
                "اربط السند بأمر الإنتاج أو حدّد الدفعة.");

        foreach (var oid in orderIds)
        {
            var checks = db.QualityChecks.AsNoTracking().Where(c => c.OrderId == oid).ToList();
            if (checks.Count == 0)
                return (false,
                    "⛔ لا يمكن التسليم للعميل: لا يوجد فحص جودة لأمر الإنتاج المرتبط.\n" +
                    "في إنتاج التمور لا يظهر العيب إلا بعد أن يبرد المنتج — نفّذ الفحص أولاً.");

            if (!checks.Any(c => c.IsApproved))
                return (false,
                    "⛔ لا يمكن التسليم للعميل: فحص الجودة لم يُعتمد بعد.\n" +
                    "في إنتاج التمور لا يظهر العيب إلا بعد أن يبرد المنتج (فترة تبريد يومان بعد التصنيع).\n" +
                    "التسليم لمخزن التام كان مسموحاً عند الإقفال — أما تسليم العميل فينتظر اعتماد نتيجة الفحص.");

            var rejected = checks.FirstOrDefault(c => c.IsApproved && c.Decision == Rejected);
            if (rejected != null)
                return (false,
                    $"⛔ لا يمكن التسليم للعميل: قرار فحص الجودة «مرفوض تماماً / عوادم».\n" +
                    $"الفحص: {rejected.DocumentNumber} — اعتمد قرار الإتلاف أو إعادة التصنيع قبل أي تسليم.");

            var quarantined = checks.FirstOrDefault(c => c.IsApproved && c.Decision == Quarantine);
            if (quarantined != null)
                return (false,
                    $"⛔ لا يمكن التسليم للعميل: البضاعة تحت «حجز وتحريز مؤقت».\n" +
                    $"الفحص: {quarantined.DocumentNumber} — الإفراج يتطلب قرار جودة «مطابق ومقبول».");
        }

        return (true, null);
    }

    /// <summary>
    /// §B95 — سقف الكمية المسلَّمة (تكملة بوابة القرار): لا يُسلَّم للعميل أكثر من «المطابق المعتمد» —
    /// مجموع المقبول في الفحوصات المعتمدة لأوامر البضاعة، ناقصاً ما سُلِّم معتمداً سابقاً لنفس النطاق.
    /// يُفحص بعد رصيد المخزن (لا قبله) لتبقى رسائل الرصيد القائمة على حالها،
    /// ويُتجاوز بصمت عند غياب بنود فحص (فحوصات بلا تفصيل) — فبوابة القرار هي صاحبة الرفض هناك.
    /// </summary>
    public static (bool ok, string reason) CustomerDeliveryQtyAllowed(
        DatesErpDbContext db, CustomerDelivery dlv, CustomerDeliveryItem item)
    {
        // اشتقاق الأوامر كما في بوابة القرار (معرّف الأمر أو الدفعة/الصنف — لا تجاوز بالـnull)
        var orderIds = new List<int>();
        if (dlv.OrderId != null) orderIds.Add(dlv.OrderId.Value);
        if (orderIds.Count == 0 && item.LotId != null)
            orderIds = db.ProductionOrderItems.AsNoTracking()
                .Where(i => i.LotId == item.LotId && i.ProductId == item.ProductId)
                .Select(i => i.OrderId).Distinct().ToList();
        if (orderIds.Count == 0) return (true, null); // بوابة القرار رفضت أصلاً — لا رسالة مكررة

        var checkIds = db.QualityChecks.AsNoTracking()
            .Where(c => c.OrderId != null && orderIds.Contains(c.OrderId.Value) && c.IsApproved)
            .Select(c => c.Id).ToList();
        double approved = 0;
        if (checkIds.Count > 0)
            approved = db.QualityCheckItems.AsNoTracking()
                .Where(q => checkIds.Contains(q.CheckId) && q.ProductId == item.ProductId)
                .Sum(q => q.AcceptedQtyKg);
        if (approved <= 0) return (true, null); // بلا تفصيل مقبول — بوابة القرار هي صاحبة الرفض

        var orderLots = db.ProductionOrderItems.AsNoTracking()
            .Where(i => orderIds.Contains(i.OrderId) && i.LotId != null)
            .Select(i => i.LotId!.Value).Distinct().ToList();
        double delivered = (from di in db.CustomerDeliveryItems.AsNoTracking()
                            join dd in db.CustomerDeliveries.AsNoTracking() on di.DeliveryId equals dd.Id
                            where dd.Id != dlv.Id && dd.IsApproved
                                && dd.CustomerId == dlv.CustomerId && di.ProductId == item.ProductId
                                && ((dd.OrderId != null && orderIds.Contains(dd.OrderId.Value))
                                    || (di.LotId != null && orderLots.Contains(di.LotId.Value)))
                            select di.QtyKg).Sum();
        // بنود السند الحالي لنفس الصنف تُحسب معاً (سند متعدد البنود لصنف واحد)
        double inCurrent = dlv.Items.Where(x => x.ProductId == item.ProductId).Sum(x => x.QtyKg);
        if (delivered + inCurrent > approved + 0.01)
        {
            string pname = db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"صنف #{item.ProductId}";
            return (false,
                $"⛔ لا يمكن تسليم {inCurrent:N1} كجم من «{pname}»: المطابق المعتمد {approved:N1} كجم" +
                (delivered > 0.001 ? $" وسُلِّم منه {delivered:N1} كجم سابقاً" : "") + ".\n" +
                "لا يُسلَّم للعميل إلا الكمية المطابقة المعتمدة من فحص الجودة — راجع المحضر المعتمد.");
        }
        return (true, null);
    }

    /// <summary>هل سُمح بالإفراج لمخزن التام؟ (يكفي إرسال الإنتاج للفحص — قرارهم #19/#20).</summary>
    public static (bool ok, string reason) FinishedGoodsIssueAllowed(DatesErpDbContext db, int orderId)
    {
        bool anyCheck = db.QualityChecks.AsNoTracking().Any(c => c.OrderId == orderId);
        if (!anyCheck)
            return (false,
                "لا يمكن تسليم الإنتاج قبل إقفال يوم الإنتاج وإرساله إلى الجودة — نفّذ الإقفال اليومي أولاً.");
        return (true, null);
    }
}
