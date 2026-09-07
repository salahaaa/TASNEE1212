using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §B96 — أوامر تسليم الإنتاج (إدارة الإنتاج — يحررها مدير الإنتاج):
/// مسودة ← مُصدَرة ← مستلمة (عبر سندات الاستلام المخزنية).
/// المصدر: محضر فحص معتمد (طبيعي) أو خطة/إقفال خطة (تجاوز للفحص بصلاحية وسبب مكتوب).
/// البند = أمر + صنف + دفعة + عميل + كمية — عميل أو عدة عملاء، صنف أو أكثر.
/// سقفان: سقف المصدر (المقبول/المنتَج) + سقف فيزيائي موحد (كل المصادر ≤ المنتَج).
/// </summary>
public class ProductionDeliveryService : ServiceBase, IProductionDeliveryService
{
    private readonly IAuditService _audit;

    public ProductionDeliveryService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IAuditService audit)
        : base(db, session, numbering)
    {
        _audit = audit;
    }

    public OpResult SaveDelivery(string sourceType, int sourceId, string deliveryDate, List<ProductionDeliveryItemDto> items,
        string bypassReason = null, string notes = null)
    {
        Require("production", "Create");
        if (sourceType != DeliverySources.FromCheck && sourceType != DeliverySources.FromPlan && sourceType != DeliverySources.FromClosing)
            return OpResult.Fail("مصدر التسليم غير معروف — اختر: محضر فحص / خطة إنتاج / إقفال خطة.");
        if (items == null || items.Count == 0)
            return OpResult.Fail("أدخل بنداً واحداً على الأقل في أمر التسليم.");

        // §B96 — التجاوز بصلاحية وسبب مكتوب (لا تجاوز صامت إطلاقاً)
        bool bypass = DeliverySources.IsBypass(sourceType);
        if (bypass)
        {
            Require("production", "BypassInspection");
            if (string.IsNullOrWhiteSpace(bypassReason))
                return OpResult.Fail("التسليم من الخطة/الإقفال يتجاوز الفحص — أدخل سبب التجاوز مكتوباً ليُحفظ موثقاً في الأمر.");
        }

        // §التحقق من المصدر وحالته
        QualityCheck check = null;
        ProductionPlan plan = null;
        ProductionOrder checkOrder = null;
        if (sourceType == DeliverySources.FromCheck)
        {
            check = Db.QualityChecks.Include(c => c.Items).FirstOrDefault(c => c.Id == sourceId);
            if (check == null) return OpResult.Fail("محضر الفحص غير موجود — تحقق من الرقم.");
            if (!check.IsApproved) return OpResult.Fail($"محضر الفحص {check.DocumentNumber} غير معتمد — لا يُسلَّم إلا من محضر معتمد.");
            if (check.OrderId == null) return OpResult.Fail("الفحص اليدوي بلا أمر لا يُسلَّم منه — التسليم من إنتاج أمر تشغيل فقط.");
            checkOrder = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == check.OrderId.Value);
            if (checkOrder == null) return OpResult.Fail("أمر المحضر غير موجود.");
        }
        else
        {
            plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == sourceId);
            if (plan == null) return OpResult.Fail("الخطة غير موجودة — تحقق من الرقم.");
            if (!plan.IsApproved) return OpResult.Fail("الخطة غير معتمدة — اعتمدها أولاً.");
            if (plan.Status == DocStatuses.Cancelled) return OpResult.Fail("الخطة ملغاة — لا يُسلَّم منها.");
            if (sourceType == DeliverySources.FromClosing && !plan.IsClosed)
                return OpResult.Fail("الخطة غير مقفلة — التسليم من الإقفال يتطلب خطة مقفلة (أو سلِّم من الخطة مباشرة).");
        }

        var srcLines = BuildSourceLines(sourceType, sourceId, check, checkOrder, plan);

        return RunOp(() =>
        {
            var delivery = new ProductionDelivery
            {
                DocumentNumber = Numbering.Next("PDL"),
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : DateTime.Now,
                SourceType = sourceType,
                SourceId = sourceId,
                BypassReason = bypass ? bypassReason.Trim() : null,
                Status = DocStatuses.Draft,
                ReceiptStatus = "None",
                Notes = notes
            };

            var takenPerLine = new Dictionary<DeliverySourceLine, double>();
            foreach (var it in items)
            {
                // §نظام الوحدات: التسليم للمنتجات التامة فقط (002)
                UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "بند أمر تسليم الإنتاج");
                if (it.QtyKg <= 0)
                    throw new DomainException($"كمية البند للصنف «{ProdName(it.ProductId)}» يجب أن تكون أكبر من صفر.");

                // §مطابقة سطر المصدر (صنف + دفعة + عميل)
                // §تعدد العملاء: حين يُترك عميل البند فارغاً كان الشرط l.CustomerId == (it.CustomerId ?? l.CustomerId)
                // يتحقق لكل السطور، فيُلتقط أول سطر بالصدفة ويُقيَّد التام لعميل غير صاحبه.
                // فإن تعددت السطور المطابقة صنفاً ودفعةً، العميل إلزامي صريح.
                var candidates = srcLines.Where(l => l.ProductId == it.ProductId && l.LotId == it.LotId).ToList();
                DeliverySourceLine line;
                if (it.CustomerId != null)
                    line = candidates.FirstOrDefault(l => l.CustomerId == it.CustomerId);
                else if (candidates.Count > 1)
                    throw new DomainException(
                        $"⛔ الصنف «{ProdName(it.ProductId)}»" +
                        (it.LotId != null ? $" من الدفعة «{LotCode(it.LotId)}»" : "") +
                        $" مشترك بين {candidates.Count} عملاء في {DeliverySources.ToArabic(sourceType)} — حدد عميل البند صراحةً.\n" +
                        "العملاء: " + string.Join(" · ", candidates.Select(c => c.CustomerName ?? "بلا عميل")),
                        "AMBIGUOUS_CUSTOMER");
                else
                    line = candidates.FirstOrDefault();
                if (line == null)
                    throw new DomainException(
                        $"لا يوجد سطر مطابق في {DeliverySources.ToArabic(sourceType)} للصنف «{ProdName(it.ProductId)}»" +
                        (it.LotId != null ? $" والدفعة «{LotCode(it.LotId)}»" : "") + " — راجع الملء الآلي.",
                        "NO_SOURCE_LINE");

                // §سقف المصدر: لا تسليم فوق المتبقي (المقبول/المنتَج ناقص ما سُلِّم سابقاً)
                // RemainingQtyKg محسوب مرة واحدة قبل الحلقة من المحفوظ، فبندان في نفس المستند
                // على سطر واحد كانا يُقاسان كلاهما على المتبقي الكامل ويتجاوزانه معاً — يُجمَّع محلياً.
                takenPerLine.TryGetValue(line, out double takenBefore);
                if (takenBefore + it.QtyKg > line.RemainingQtyKg + 0.01)
                    throw new DomainException(
                        $"⛔ كمية البند ({it.QtyKg:N1} كجم) تتجاوز المتبقي القابل للتسليم ({line.RemainingQtyKg:N1} كجم) " +
                        $"للصنف «{line.ProductName}» في {DeliverySources.ToArabic(sourceType)}." +
                        (takenBefore > 0 ? $"\nمأخوذ في بنود أخرى من هذا المستند: {takenBefore:N1} كجم." : ""),
                        "OVER_SOURCE");
                takenPerLine[line] = takenBefore + it.QtyKg;

                // §السقف الفيزيائي الموحد: كل المصادر معاً لا تتجاوز إنتاج الأمر (منع ازدواج محضر+خطة)
                if (line.OrderId is int lineOrderId)
                {
                    double produced = Db.ProductionOrderItems.AsNoTracking()
                        .Where(o => o.OrderId == lineOrderId && o.ProductId == it.ProductId).Sum(o => o.ProducedQtyKg);
                    double deliveredAll = Db.ProductionDeliveryItems.AsNoTracking()
                        .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                        .Where(x => x.d.Status != DocStatuses.Cancelled && x.i.OrderId == lineOrderId && x.i.ProductId == it.ProductId)
                        .Sum(x => x.i.QtyKg);
                    // §المباشر القديم يُرى أيضاً (المربوط داخل deliveredAll أصلاً فلا ازدواج)
                    double directReceived = Db.FinishedGoodsReceiptItems.AsNoTracking()
                        .Join(Db.FinishedGoodsReceipts.AsNoTracking(), i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                        .Where(x => x.r.Status != DocStatuses.Cancelled && x.r.DeliveryId == null
                            && x.r.OrderId == lineOrderId && x.i.ProductId == it.ProductId)
                        .Sum(x => x.i.ReceivedQtyKg);
                    double thisDoc = delivery.Items.Where(i => i.OrderId == lineOrderId && i.ProductId == it.ProductId).Sum(i => i.QtyKg);
                    if (deliveredAll + directReceived + thisDoc + it.QtyKg > produced + 0.01)
                        throw new DomainException(
                            $"⛔ التسليم يتجاوز الإنتاج الفعلي للصنف «{line.ProductName}».\n" +
                            $"المنتَج: {produced:N1} كجم | المُسلَّم بأوامر أخرى: {deliveredAll:N1} | المستلَم مباشرةً: {directReceived:N1} | هذا الأمر بعد الإضافة: {thisDoc + it.QtyKg:N1}",
                            "OVER_PRODUCED");
                }

                // §تتبع الهوية: الدفعة المسلَّمة يجب أن تطابق دفعة بند الأمر (لا استبدال هوية)
                if (it.LotId is int lineLot && line.OrderId is int lineOrd)
                {
                    ProductIdentityGuard.EnsureConversionAllowed(Db, it.ProductId, lineLot);
                    bool lotOk = Db.ProductionOrderItems.AsNoTracking()
                        .Any(o => o.OrderId == lineOrd && o.ProductId == it.ProductId && o.LotId == lineLot);
                    if (!lotOk)
                        throw new DomainException($"⛔ الدفعة «{LotCode(lineLot)}» ليست دفعة هذا الصنف في الأمر المصدر.", "LOT_MISMATCH");
                }

                delivery.Items.Add(new ProductionDeliveryItem
                {
                    OrderId = line.OrderId,
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    CustomerId = line.CustomerId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    QtyKg = it.QtyKg
                });
            }

            Db.ProductionDeliveries.Add(delivery);
            Db.SaveChanges();
            if (bypass)
                _audit.Log("الإنتاج", "تسليم بتجاوز الفحص", "ProductionDelivery", delivery.DocumentNumber, delivery.Id,
                    new { المصدر = DeliverySources.ToArabic(sourceType) }, new { السبب = delivery.BypassReason });
            double total = delivery.Items.Sum(i => i.QtyKg);
            return OpResult.Success(
                $"تم إنشاء أمر تسليم الإنتاج {delivery.DocumentNumber} من {DeliverySources.ToArabic(sourceType)} — {delivery.Items.Count} بنود ({total:N1} كجم)." +
                (bypass ? " (بتجاوز موثق للفحص)" : "") + " — بانتظار تحرير مدير الإنتاج.",
                delivery.Id, delivery.DocumentNumber);
        });
    }

    public OpResult IssueDelivery(int deliveryId)
    {
        Require("production", "Approve");
        var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (delivery == null) return OpResult.Fail("أمر التسليم غير موجود.");
        if (delivery.Status == DocStatuses.Issued) return OpResult.Fail("أمر التسليم مُحرَّر مسبقاً.");
        if (delivery.Status == DocStatuses.Cancelled) return OpResult.Fail("أمر التسليم ملغى.");
        if (delivery.Status == DocStatuses.Completed) return OpResult.Fail("أمر التسليم مستلم بالكامل.");

        return RunOp(() =>
        {
            if (delivery.Items.Count == 0)
                throw new DomainException("لا يمكن تحرير أمر بلا بنود.");
            delivery.Status = DocStatuses.Issued;
            delivery.IsApproved = true;
            delivery.ApprovedBy = Session?.UserId;
            delivery.ApprovedDate = DateTime.Now;
            Db.SaveChanges();
            return OpResult.Success($"تم تحرير أمر التسليم {delivery.DocumentNumber} إلى المخازن — بانتظار سند الاستلام.");
        });
    }

    public OpResult CancelDelivery(int deliveryId)
    {
        Require("production", "Cancel");
        var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (delivery == null) return OpResult.Fail("أمر التسليم غير موجود.");
        if (delivery.Status == DocStatuses.Cancelled) return OpResult.Fail("أمر التسليم ملغى مسبقاً.");
        if (delivery.Status == DocStatuses.Completed) return OpResult.Fail("الأمر مستلم بالكامل — ألغِ سندات الاستلام أولاً.");

        return RunOp(() =>
        {
            if (delivery.Items.Any(i => i.ReceivedQtyKg > 0.001))
                throw new DomainException("بدأ استلام هذا الأمر — ألغِ سندات الاستلام أولاً ثم ألغِ الأمر.", "HAS_RECEIPTS");
            delivery.Status = DocStatuses.Cancelled;
            Db.SaveChanges();
            return OpResult.Success($"تم إلغاء أمر التسليم {delivery.DocumentNumber}.");
        });
    }

    public DeliverySourceContext GetSourceContext(string sourceType, int sourceId)
    {
        var ctx = new DeliverySourceContext { SourceType = sourceType, SourceId = sourceId };
        if (sourceType == DeliverySources.FromCheck)
        {
            var check = Db.QualityChecks.Include(c => c.Items).AsNoTracking().FirstOrDefault(c => c.Id == sourceId);
            if (check == null) return ctx;
            ctx.SourceNumber = check.DocumentNumber;
            ctx.SourceDate = UiFormat.D(check.CheckDate);
            var order = check.OrderId != null
                ? Db.ProductionOrders.Include(o => o.Items).AsNoTracking().FirstOrDefault(o => o.Id == check.OrderId.Value)
                : null;
            ctx.Lines = BuildSourceLines(sourceType, sourceId, check, order, null);
        }
        else if (sourceType == DeliverySources.FromPlan || sourceType == DeliverySources.FromClosing)
        {
            var plan = Db.ProductionPlans.Include(p => p.Items).AsNoTracking().FirstOrDefault(p => p.Id == sourceId);
            if (plan == null) return ctx;
            ctx.SourceNumber = plan.DocumentNumber;
            ctx.SourceDate = UiFormat.D(plan.StartDate);
            ctx.Lines = BuildSourceLines(sourceType, sourceId, null, null, plan);
        }
        return ctx;
    }

    public List<(int Id, string Label)> GetSourceDocs(string sourceType)
    {
        if (sourceType == DeliverySources.FromCheck)
            return Db.QualityChecks.AsNoTracking().Include(c => c.Items)
                .Where(c => c.IsApproved && c.OrderId != null)
                .OrderByDescending(c => c.Id).Take(200).ToList()
                .Select(c => (c.Id, $"{c.DocumentNumber} — {UiFormat.D(c.CheckDate)} — مقبول {c.Items.Sum(i => i.AcceptedQtyKg):N1} كجم"))
                .ToList();
        if (sourceType == DeliverySources.FromPlan)
            return Db.ProductionPlans.AsNoTracking()
                .Where(p => p.IsApproved && p.Status != DocStatuses.Cancelled && !p.IsClosed)
                .OrderByDescending(p => p.Id).Take(200).ToList()
                .Select(p => (p.Id, $"{p.DocumentNumber} — {UiFormat.D(p.StartDate)}"))
                .ToList();
        if (sourceType == DeliverySources.FromClosing)
            return Db.ProductionPlans.AsNoTracking()
                .Where(p => p.IsApproved && p.IsClosed)
                .OrderByDescending(p => p.Id).Take(200).ToList()
                .Select(p => (p.Id, $"{p.DocumentNumber} — {UiFormat.D(p.StartDate)} (مقفلة)"))
                .ToList();
        return new List<(int, string)>();
    }

    public ProductionDeliveryCard GetDelivery(int deliveryId)
    {
        var d = Db.ProductionDeliveries.Include(x => x.Items).AsNoTracking().FirstOrDefault(x => x.Id == deliveryId);
        if (d == null) return null;
        var card = new ProductionDeliveryCard
        {
            Id = d.Id,
            DocumentNumber = d.DocumentNumber,
            DeliveryDate = UiFormat.D(d.DeliveryDate),
            SourceType = d.SourceType,
            SourceTypeAr = DeliverySources.ToArabic(d.SourceType),
            SourceId = d.SourceId,
            SourceNumber = SourceNumber(d.SourceType, d.SourceId),
            BypassReason = d.BypassReason,
            Status = d.Status,
            StatusAr = DocStatuses.ToArabic(d.Status),
            ReceiptStatus = d.ReceiptStatus
        };
        foreach (var i in d.Items)
            card.Lines.Add(new ProductionDeliveryLineRow
            {
                Id = i.Id,
                OrderId = i.OrderId,
                OrderNumber = i.OrderId != null ? Db.ProductionOrders.AsNoTracking().Where(o => o.Id == i.OrderId.Value).Select(o => o.DocumentNumber).FirstOrDefault() : null,
                ProductId = i.ProductId,
                ProductName = ProdName(i.ProductId),
                LotId = i.LotId,
                LotCode = i.LotId != null ? LotCode(i.LotId) : null,
                CustomerId = i.CustomerId,
                CustomerName = i.CustomerId != null ? CustName(i.CustomerId) : null,
                QtyKg = i.QtyKg,
                ReceivedQtyKg = i.ReceivedQtyKg,
                RemainingQtyKg = Math.Max(0, i.QtyKg - i.ReceivedQtyKg)
            });
        return card;
    }

    public List<ProductionDeliveryCard> GetDeliveries(string statusFilter = null)
    {
        var ids = Db.ProductionDeliveries.AsNoTracking()
            .Where(d => statusFilter == null || d.Status == statusFilter)
            .OrderByDescending(d => d.Id).Take(300).Select(d => d.Id).ToList();
        return ids.Select(GetDelivery).Where(c => c != null).ToList();
    }

    // ── سطور المصدر: المتاح (مقبول/منتَج) ناقص ما سُلِّم بأوامر غير ملغاة ──
    private List<DeliverySourceLine> BuildSourceLines(string sourceType, int sourceId,
        QualityCheck check, ProductionOrder order, ProductionPlan plan)
    {
        var lines = new List<DeliverySourceLine>();
        if (sourceType == DeliverySources.FromCheck)
        {
            if (check == null || order == null) return lines;
            foreach (var g in check.Items.GroupBy(i => new { i.ProductId, i.LotId }))
            {
                double accepted = g.Sum(i => i.AcceptedQtyKg);
                if (accepted <= 0.001) continue;
                // §عميل السطر: بند الأمر المطابق (صنف + دفعة) ثم أول بند للصنف ثم عميل الأمر
                int? cust = order.Items.FirstOrDefault(i => i.ProductId == g.Key.ProductId && i.LotId == g.Key.LotId)?.CustomerId
                    ?? order.Items.FirstOrDefault(i => i.ProductId == g.Key.ProductId)?.CustomerId
                    ?? order.CustomerId;
                double delivered = Db.ProductionDeliveryItems.AsNoTracking()
                    .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                    .Where(x => x.d.Status != DocStatuses.Cancelled && x.d.SourceType == DeliverySources.FromCheck
                        && x.d.SourceId == sourceId && x.i.ProductId == g.Key.ProductId && x.i.LotId == g.Key.LotId)
                    .Sum(x => x.i.QtyKg);
                lines.Add(new DeliverySourceLine
                {
                    OrderId = order.Id,
                    OrderNumber = order.DocumentNumber,
                    ProductId = g.Key.ProductId,
                    ProductName = ProdName(g.Key.ProductId),
                    LotId = g.Key.LotId,
                    LotCode = g.Key.LotId != null ? LotCode(g.Key.LotId) : null,
                    CustomerId = cust,
                    CustomerName = cust != null ? CustName(cust) : null,
                    AvailableQtyKg = accepted,
                    DeliveredQtyKg = delivered,
                    RemainingQtyKg = Math.Max(0, accepted - delivered)
                });
            }
        }
        else
        {
            if (plan == null) return lines;
            // §التجاوز يسلِّم المنتَج فقط (لا المخطط النظري): المنتَج المزامَن من الأوامر
            var planTypes = new[] { DeliverySources.FromPlan, DeliverySources.FromClosing };
            foreach (var pi in plan.Items)
            {
                if (pi.ProducedQtyKg <= 0.001) continue;
                int? ordId = Db.ProductionOrderItems.AsNoTracking()
                    .Where(o => o.PlanItemId == pi.Id).OrderBy(o => o.Id)
                    .Select(o => (int?)o.OrderId).FirstOrDefault();
                double delivered = Db.ProductionDeliveryItems.AsNoTracking()
                    .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                    .Where(x => x.d.Status != DocStatuses.Cancelled && planTypes.Contains(x.d.SourceType)
                        && x.d.SourceId == sourceId && x.i.ProductId == pi.ProductId
                        && x.i.LotId == pi.LotId && x.i.CustomerId == pi.CustomerId)
                    .Sum(x => x.i.QtyKg);
                lines.Add(new DeliverySourceLine
                {
                    OrderId = ordId,
                    OrderNumber = ordId != null ? Db.ProductionOrders.AsNoTracking().Where(o => o.Id == ordId.Value).Select(o => o.DocumentNumber).FirstOrDefault() : null,
                    ProductId = pi.ProductId,
                    ProductName = ProdName(pi.ProductId),
                    LotId = pi.LotId,
                    LotCode = pi.LotId != null ? LotCode(pi.LotId) : null,
                    CustomerId = pi.CustomerId,
                    CustomerName = pi.CustomerId != null ? CustName(pi.CustomerId) : null,
                    AvailableQtyKg = pi.ProducedQtyKg,
                    DeliveredQtyKg = delivered,
                    RemainingQtyKg = Math.Max(0, pi.ProducedQtyKg - delivered)
                });
            }
        }
        return lines;
    }

    private string SourceNumber(string sourceType, int sourceId)
    {
        if (sourceType == DeliverySources.FromCheck)
            return Db.QualityChecks.AsNoTracking().Where(c => c.Id == sourceId).Select(c => c.DocumentNumber).FirstOrDefault() ?? $"#{sourceId}";
        return Db.ProductionPlans.AsNoTracking().Where(p => p.Id == sourceId).Select(p => p.DocumentNumber).FirstOrDefault() ?? $"#{sourceId}";
    }

    private string ProdName(int id)
        => Db.Products.AsNoTracking().Where(p => p.Id == id).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"صنف #{id}";

    private string LotCode(int? id)
        => id == null ? null : Db.Lots.AsNoTracking().Where(l => l.Id == id.Value).Select(l => l.LotCode).FirstOrDefault() ?? $"دفعة #{id}";

    private string CustName(int? id)
        => id == null ? null : Db.Customers.AsNoTracking().Where(c => c.Id == id.Value).Select(c => c.CustomerName).FirstOrDefault() ?? $"عميل #{id}";
}
