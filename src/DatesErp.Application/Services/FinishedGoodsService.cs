using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §7 — الدورة القانونية لاستلام الإنتاج التام:
/// أمر التسليم (متعدد الأصناف) ← الإصدار للمخزن (بلا أثر على الأرصدة)
/// ← سند الاستلام المخزني هو وحده ما يحرّك أرصدة مخزن التام (كلي/جزئي لكل صنف).
/// </summary>
public class FinishedGoodsService : ServiceBase, IFinishedGoodsService
{
    public FinishedGoodsService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public OpResult SaveReceipt(int orderId, int? qualityCheckId, string deliveryDate, List<FinishedGoodsItemDto> items, int? deliveryId = null)
    {
        Require("finishedgoods", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        // §B96 — الربط بأمر تسليم: يُحرَّر من الإنتاج أولاً، والأمر المحدد من أوامره
        ProductionDelivery delivery = null;
        if (deliveryId != null)
        {
            delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId.Value);
            if (delivery == null) return OpResult.Fail("أمر تسليم الإنتاج غير موجود — تحقق من الرقم.");
            if (delivery.Status == DocStatuses.Draft) return OpResult.Fail("أمر التسليم مسودة — يجب تحريره من مدير الإنتاج أولاً.");
            if (delivery.Status == DocStatuses.Cancelled) return OpResult.Fail("أمر التسليم ملغى.");
            if (delivery.Status == DocStatuses.Completed) return OpResult.Fail("أمر التسليم مستلم بالكامل مسبقاً.");
            var delOrders = delivery.Items.Where(i => i.OrderId != null).Select(i => i.OrderId.Value).Distinct().ToList();
            if (!delOrders.Contains(orderId)) return OpResult.Fail("الأمر المحدد ليس من أوامر أمر التسليم المحدد.");
        }
        // §جودة التمور (فترة تبريد يومان): يُسمح بالتسليم لمخزن التام بمجرد الإقفال اليومي
        // وإرسال الإنتاج للجودة — النتيجة النهائية تُستكمل خلال يومين قبل تسليم العميل.
        // §B96 — المربوط بأمر تسليم يحكمه أمر التسليم (ومنه التجاوز الموثق) لا بوابة الفحص هنا
        QualityCheck qualityCheck = null;
        bool coolingPending = false;
        if (delivery == null)
        {
            qualityCheck = Db.QualityChecks.AsNoTracking()
                .Where(c => c.OrderId == orderId).OrderByDescending(c => c.Id).FirstOrDefault();
            if (qualityCheck == null)
                return OpResult.Fail("لا يمكن تسليم الإنتاج قبل إقفال يوم الإنتاج وإرساله إلى الجودة — نفّذ الإقفال اليومي مع «إرسال للفحص» أولاً، أو أنشئ فحص جودة مستقلاً من شاشة الجودة.");
            coolingPending = !qualityCheck.IsApproved;
        }

        return RunOp(() =>
        {
            var rcpt = new FinishedGoodsReceipt
            {
                DocumentNumber = Numbering.Next("FGR"),
                OrderId = orderId,
                QualityCheckId = qualityCheckId ?? (delivery?.SourceType == DeliverySources.FromCheck ? delivery.SourceId : null),
                DeliveryId = deliveryId,
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : DateTime.Now,
                WarehouseId = WarehouseId("WFG"),
                Status = DocStatuses.Draft,
                ReceiptStatus = "None"
            };
            var boxWarnings = new List<string>(); // §B86/H7: تنبيهات مطابقة الكراتين
            // §B96 — حارس التكرار يمنع بندين بنفس (الصنف + الدفعة) في سند واحد: رفض مبكر برسالة واضحة
            // (لعملاء مختلفين على نفس الدفعة: استلم كل بند تسليم في سند مستقل — فالترقيم مختلف ولا تعارض)
            var dupLine = items.GroupBy(i => new { i.ProductId, i.LotId }).FirstOrDefault(g => g.Count() > 1);
            if (dupLine != null)
                throw new DomainException(
                    "⛔ بندَان مكرران لنفس الصنف والدفعة في سند واحد — وحّدهما في بند واحد.\n" +
                    "لعملاء مختلفين على نفس الدفعة: استلم كل بند تسليم في سند مستقل.",
                    "DUP_LINE");
            foreach (var it in items)
            {
                // §نظام الوحدات: استلام التام للمنتجات التامة فقط (002) واتساق الكرتون/الكيلو إلزامي
                UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "استلام الإنتاج التام");
                it.NetWeightKg = UnitsPolicy.EnsureCartonKgConsistency(Db, it.ProductId, it.PackagingTypeId,
                    it.NetWeightKg, it.PackageCount, "استلام الإنتاج التام");

                // §B96 — المربوط: بند التسليم هو الحاكم (المتبقي + الهوية) — المباشر: بنود الأمر كما كان
                int? effCust = null;
                int? effLine = null;
                if (delivery != null)
                {
                    if (it.DeliveryItemId == null)
                        throw new DomainException("حدد بند أمر التسليم لكل صنف في السند المربوط.", "NO_DELIVERY_LINE");
                    var line = delivery.Items.FirstOrDefault(l => l.Id == it.DeliveryItemId.Value)
                        ?? throw new DomainException("بند التسليم غير تابع لأمر التسليم المحدد.", "NO_DELIVERY_LINE");
                    if (line.ProductId != it.ProductId)
                        throw new DomainException("الصنف لا يطابق بند أمر التسليم المحدد.", "LINE_MISMATCH");
                    if (it.LotId != null && it.LotId != line.LotId)
                        throw new DomainException("الدفعة لا تطابق بند أمر التسليم المحدد.", "LOT_MISMATCH");
                    if (it.LotId == null) it.LotId = line.LotId;
                    // §B86/H8 بالمثل: المسودات لا تحجب بعضها — السقف على المستلَم ويُعاد فحصه عند الاستلام
                    double lineRemaining = line.QtyKg - line.ReceivedQtyKg;
                    if (it.NetWeightKg > lineRemaining + 0.001)
                        throw new DomainException(
                            $"⛔ كمية البند ({it.NetWeightKg:N1} كجم) تتجاوز المتبقي في بند أمر التسليم ({lineRemaining:N1} كجم).",
                            "OVER_DELIVERY");
                    effCust = line.CustomerId;
                    effLine = line.Id;
                }
                else
                {
                // §8 — لا يتجاوز بند التسليم كمية الأمر
                var orderItem = order.Items.FirstOrDefault(i => i.ProductId == it.ProductId);
                if (orderItem == null) throw new DomainException("الصنف غير موجود في أمر الإنتاج.");

                // §تتبع الصنف: الدفعة المرتبطة بالتسليم يجب أن تتطابق مع دفعة بند الأمر (لا استبدال هوية)
                if (it.LotId is int fgLotId)
                {
                    ProductIdentityGuard.EnsureConversionAllowed(Db, it.ProductId, fgLotId);
                    if (orderItem.LotId != null && orderItem.LotId != fgLotId)
                    {
                        string wantLot = Db.Lots.AsNoTracking().Where(l => l.Id == fgLotId).Select(l => l.LotCode).FirstOrDefault() ?? $"#{fgLotId}";
                        string realLot = Db.Lots.AsNoTracking().Where(l => l.Id == orderItem.LotId).Select(l => l.LotCode).FirstOrDefault() ?? $"#{orderItem.LotId}";
                        throw new DomainException(
                            $"⛔ الدفعة {wantLot} ليست دفعة هذا البند — بند الأمر مرتبط بالدفعة {realLot}.\n" +
                            "هوية الصنف والدفعة تنتقلان من أمر الإنتاج كما هما.",
                            "LOT_MISMATCH");
                    }
                }
                // §B86/H8: سقف المنتَج = مجموع بنود الأمر لذات الصنف (الأمر متعدد الدفعات لصنف واحد شائع)
                double producedForProduct = order.Items.Where(i => i.ProductId == it.ProductId).Sum(i => i.ProducedQtyKg);
                int producedBoxesForProduct = order.Items.Where(i => i.ProductId == it.ProductId).Sum(i => i.ProducedCartons);
                // §B86/H8: الحصة المحجوزة = المستلَم فعلاً (لا المأمور) — المسودات لا تحجب بعضها، والملغاة لا تحجز؛ السقف يُعاد فحصه عند الاستلام
                double alreadyDelivering = Db.FinishedGoodsReceiptItems
                    .Join(Db.FinishedGoodsReceipts, i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                    .Where(x => x.r.OrderId == orderId && x.i.ProductId == it.ProductId
                        && x.r.Status != DocStatuses.Cancelled)
                    .Sum(x => x.i.ReceivedQtyKg);
                if (alreadyDelivering + it.NetWeightKg > producedForProduct + 0.001)
                    throw new DomainException(
                        $"تسليم يتجاوز كمية أمر الإنتاج للصنف.\nالمنتَج: {producedForProduct:N1} كجم | المطلوب تسليمه تراكمياً: {alreadyDelivering + it.NetWeightKg:N1}",
                        "EXCEED_ORDER_QTY");
                // §B86/H7: مطابقة الكراتين — تنبيه عند تجاوز المتبقي المنتَج (لا رفض: إنتاج ما قبل B86 بلا كراتين مسجلة)
                if (producedBoxesForProduct > 0 && it.PackageCount > 0)
                {
                    int receivedBoxes = Db.FinishedGoodsReceiptItems
                        .Join(Db.FinishedGoodsReceipts, i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                        .Where(x => x.r.OrderId == orderId && x.i.ProductId == it.ProductId
                            && x.r.Status != DocStatuses.Cancelled)
                        .Sum(x => x.i.PackageCount);
                    int boxRemaining = producedBoxesForProduct - receivedBoxes;
                    if (it.PackageCount > boxRemaining)
                    {
                        string pname = Db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{it.ProductId}";
                        boxWarnings.Add($"⚠ كراتين السند ({it.PackageCount:N0}) تتجاوز المتبقي المنتَج ({boxRemaining:N0}) للصنف «{pname}» — راجع العد قبل الاعتماد.");
                    }
                }
                } // §B96 — نهاية المسار المباشر (غير مربوط)

                rcpt.Items.Add(new FinishedGoodsReceiptItem
                {
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    CustomerId = effCust,
                    DeliveryItemId = effLine,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    NetWeightKg = it.NetWeightKg,
                    ReceivedQtyKg = 0,
                    // §القاعدة 7: وزن الكرتون وقت الاستلام — لا يتغير بتعريف العبوة لاحقاً
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId)
                });
            }
            Db.FinishedGoodsReceipts.Add(rcpt);
            Db.SaveChanges();
            string coolingNote = (coolingPending && qualityCheck != null)
                ? $"\n🔬 الفحص النهائي متوقع {qualityCheck.ExpectedCheckDate:dd/MM/yyyy} (فترة تبريد) — تسليم العميل متوقف على اعتماد الفحص."
                : "";
            string boxMsg = boxWarnings.Count > 0 ? "\n" + string.Join("\n", boxWarnings) : "";
            if (delivery != null)
                return OpResult.Success($"تم إنشاء سند الاستلام {rcpt.DocumentNumber} من أمر التسليم {delivery.DocumentNumber} — أصدره ثم نفّذ الاستلام.", rcpt.Id, rcpt.DocumentNumber);
            return OpResult.Success($"تم إنشاء أمر تسليم الإنتاج {rcpt.DocumentNumber} — الجودة سمحت بالتسليم للتام." + coolingNote + boxMsg, rcpt.Id, rcpt.DocumentNumber);
        });
    }

    /// <summary>الإصدار إلى المخزن — لا يمس أي رصيد (§7).</summary>
    public OpResult Issue(int receiptId)
    {
        var rcpt = Db.FinishedGoodsReceipts.FirstOrDefault(r => r.Id == receiptId);
        if (rcpt == null) return OpResult.Fail("أمر التسليم غير موجود.");
        if (rcpt.Status == DocStatuses.Issued) return OpResult.Fail("أمر التسليم مُصدر مسبقاً.");
        // بوابة الصلاحيات: الإصدار للإنتاج/الإدارة — الجودة والمخازن لا يُصدران
        if (Session != null && !Session.Can("finishedgoods", "Create") && !Session.Can("production", "Edit"))
            return OpResult.Fail("لا تملك صلاحية إصدار أمر التسليم.");

        return RunOp(() =>
        {
            rcpt.Status = DocStatuses.Issued;
            Db.SaveChanges();
            return OpResult.Success("تم إصدار أمر التسليم إلى المخزن — بانتظار سند الاستلام.");
        });
    }

    /// <summary>§7/§8 — سند الاستلام المخزني: وحده يؤثر على الأرصدة، كلياً أو جزئياً لكل صنف.</summary>
    public OpResult Receive(int receiptId, Dictionary<int, double> receivedByItemId)
    {
        Require("finishedgoods", "Approve");
        var rcpt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId);
        if (rcpt == null) return OpResult.Fail("أمر التسليم غير موجود.");
        if (rcpt.ReceiptStatus == "Full") return OpResult.Fail("السند منفذ بالكامل مسبقاً.");
        if (rcpt.Status != DocStatuses.Issued && rcpt.Status != DocStatuses.Completed)
            return OpResult.Fail("لا يمكن الاستلام قبل إصدار أمر التسليم.");

        return RunOp(() =>
        {
            var whFg = rcpt.WarehouseId;
            var orderCust = Db.ProductionOrders.Where(o => o.Id == rcpt.OrderId).Select(o => o.CustomerId).FirstOrDefault();
            double totalReceived = 0;
            rcpt.ReceiveCount++;
            rcpt.ReceiptNumber ??= Numbering.Next("RCV");
            var voucher = $"{rcpt.ReceiptNumber}#{rcpt.ReceiveCount}"; // لكل سند استلام (متابعة) ترقيم متسلسل
            var recvAcc = new Dictionary<int, double>(); // §B86/H8: مستلَم هذه الدفعة لكل صنف — بندَان لصنف واحد لا يتجاوزا السقف معاً
            // §B96 — المربوط: بنود التسليم للتحديث + مجمّع لكل بند (سندان لبند واحد لا يتجاوزاه معاً)
            var delLines = rcpt.DeliveryId != null
                ? Db.ProductionDeliveryItems.Where(i => i.DeliveryId == rcpt.DeliveryId.Value).ToList()
                : new List<ProductionDeliveryItem>();
            var recvAccLine = new Dictionary<int, double>();
            foreach (var item in rcpt.Items)
            {
                double remaining = item.NetWeightKg - item.ReceivedQtyKg;
                if (remaining <= 0.001) continue;
                double recv = receivedByItemId != null && receivedByItemId.TryGetValue(item.Id, out var v) ? v : remaining;
                if (recv <= 0) continue;
                if (recv > remaining + 0.001)
                    throw new DomainException($"الكمية المستلمة أكبر من المتبقي للبند ({remaining:N1} كجم).", "OVER_RECEIPT");
                // §B96 — المربوط: سقف بند التسليم أولاً (رسالة دقيقة) ثم السقف الفيزيائي الموحد (شبكة أمان ضد المباشر)
                ProductionDeliveryItem delLine = null;
                if (item.DeliveryItemId != null)
                {
                    delLine = delLines.FirstOrDefault(l => l.Id == item.DeliveryItemId.Value)
                        ?? throw new DomainException("بند أمر التسليم المربوط غير موجود.", "NO_DELIVERY_LINE");
                    recvAccLine.TryGetValue(delLine.Id, out var recvLineCall);
                    if (delLine.ReceivedQtyKg + recvLineCall + recv > delLine.QtyKg + 0.001)
                        throw new DomainException(
                            $"⛔ الاستلام يتجاوز بند أمر التسليم.\nالبند: {delLine.QtyKg:N1} كجم | المستلَم منه: {delLine.ReceivedQtyKg + recvLineCall:N1} | المطلوب: {recv:N1}",
                            "OVER_DELIVERY");
                    recvAccLine[delLine.Id] = recvLineCall + recv;
                    if (delLine.OrderId is int capOrder)
                    {
                        double producedCap = Db.ProductionOrderItems.AsNoTracking()
                            .Where(o => o.OrderId == capOrder && o.ProductId == item.ProductId)
                            .Sum(o => o.ProducedQtyKg);
                        // §بنود التسليم لنفس الأمر (مجموعة محلية — Contains تُترجم إلى IN)
                        var capLineIds = delLines.Where(l => l.OrderId == capOrder).Select(l => l.Id).ToHashSet();
                        double receivedAll = Db.FinishedGoodsReceiptItems.AsNoTracking()
                            .Join(Db.FinishedGoodsReceipts.AsNoTracking(), i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                            .Where(x => x.r.Status != DocStatuses.Cancelled && x.i.Id != item.Id
                                && ((x.i.DeliveryItemId != null && capLineIds.Contains(x.i.DeliveryItemId.Value))
                                    || (x.i.DeliveryItemId == null && x.r.OrderId == capOrder)))
                            .Where(x => x.i.ProductId == item.ProductId)
                            .Sum(x => x.i.ReceivedQtyKg);
                        recvAcc.TryGetValue(item.ProductId, out var recvThisCall2);
                        if (receivedAll + item.ReceivedQtyKg + recvThisCall2 + recv > producedCap + 0.001)
                            throw new DomainException(
                                $"الاستلام يتجاوز المنتَج الفعلي للصنف (مباشر + مربوط معاً).\nالمنتَج: {producedCap:N1} كجم | المستلَم: {receivedAll + item.ReceivedQtyKg + recvThisCall2:N1} | المطلوب: {recv:N1}",
                                "EXCEED_ORDER_QTY");
                        recvAcc[item.ProductId] = recvThisCall2 + recv;
                    }
                    delLine.ReceivedQtyKg += recv;
                }
                else
                {
                // §B86/H8: سقف المنتَج يُفحص عند الاستلام أيضاً — مسودتان معاً قد تتجاوزا المنتَج الفعلي
                double receivedOthers = Db.FinishedGoodsReceiptItems
                    .Join(Db.FinishedGoodsReceipts, i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                    .Where(x => x.r.OrderId == rcpt.OrderId && x.i.ProductId == item.ProductId
                        && x.r.Status != DocStatuses.Cancelled && x.i.Id != item.Id)
                    .Sum(x => x.i.ReceivedQtyKg);
                double producedCap = Db.ProductionOrderItems.AsNoTracking()
                    .Where(o => o.OrderId == rcpt.OrderId && o.ProductId == item.ProductId)
                    .Sum(o => o.ProducedQtyKg);
                recvAcc.TryGetValue(item.ProductId, out var recvThisCall);
                if (receivedOthers + item.ReceivedQtyKg + recvThisCall + recv > producedCap + 0.001)
                    throw new DomainException(
                        $"الاستلام يتجاوز المنتَج الفعلي للصنف.\nالمنتَج: {producedCap:N1} كجم | المستلَم في سندات أخرى: {receivedOthers:N1} | هذا السند بعد الاستلام: {item.ReceivedQtyKg + recvThisCall + recv:N1}",
                        "EXCEED_ORDER_QTY");

                recvAcc[item.ProductId] = recvThisCall + recv;
                }
                item.ReceivedQtyKg += recv;
                totalReceived += recv;
                // التام يُورد بالكرتون: قيد العبوات المستلمة تناسبياً مع الوزن
                int pkgRecv = item.PackageCount > 0 && item.NetWeightKg > 0
                    ? (int)Math.Round(recv / item.NetWeightKg * item.PackageCount) : 0;
                PostStockMovement(whFg, MovementType.Inbound, recv, pkgRecv,
                    ReferenceDocType.FinishedGoodsReceipt, voucher,
                    productId: item.ProductId, lotId: item.LotId, orderId: rcpt.OrderId,
                    customerId: item.CustomerId ?? orderCust, packagingTypeId: item.PackagingTypeId,
                    notes: $"استلام إنتاج تام — سند {rcpt.ReceiptNumber}");
            }

            if (totalReceived <= 0.001) return OpResult.Fail("لم تُدخل أي كمية مستلمة.");

            bool full = rcpt.Items.All(i => i.ReceivedQtyKg + 0.001 >= i.NetWeightKg);
            rcpt.ReceiptStatus = full ? "Full" : "Partial";
            rcpt.IsApproved = true;
            rcpt.Status = DocStatuses.Completed;
            // §B96 — عكس التقدم على أمر التسليم المربوط
            if (rcpt.DeliveryId != null)
            {
                var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == rcpt.DeliveryId.Value);
                if (delivery != null)
                {
                    bool dFull = delivery.Items.Count > 0 && delivery.Items.All(i => i.ReceivedQtyKg + 0.001 >= i.QtyKg);
                    bool dAny = delivery.Items.Any(i => i.ReceivedQtyKg > 0.001);
                    delivery.ReceiptStatus = dFull ? "Full" : (dAny ? "Partial" : "None");
                    if (dFull) delivery.Status = DocStatuses.Completed;
                }
            }
            rcpt.ApprovedBy = Session?.UserId;
            rcpt.ApprovedDate = DateTime.Now;

            // إغلاق أمر الإنتاج تلقائياً عند الاكتمال الكامل
            if (full)
            {
                var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == rcpt.OrderId);
                if (order != null && order.Items.All(i => i.IsClosed || i.ProducedQtyKg + 0.001 >= i.PlannedQtyKg))
                {
                    order.Status = DocStatuses.Completed;
                    order.IsClosed = true;
                    order.ClosedDate = DateTime.Now;
                }
                else if (order != null) order.Status = DocStatuses.PendingDelivery; // §B85/M9: ثابت معتمد بدل القيمة الحرة
            }

            Db.SaveChanges();
            return OpResult.Success(full
                ? "تم الاستلام الكامل وتقييد كامل الكمية في مخزن الإنتاج التام."
                : $"تم الاستلام الجزئي ({totalReceived:N1} كجم) وتقييدها في مخزن التام.", rcpt.Id, rcpt.ReceiptNumber);
        });
    }

    /// <summary>إلغاء السند يعكس الأرصدة بدقة ويحذف حركاته (§6).</summary>
    public OpResult Unapprove(int receiptId)
    {
        Require("finishedgoods", "Cancel");
        var rcpt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId);
        if (rcpt == null) return OpResult.Fail("السند غير موجود.");
        if (!rcpt.IsApproved) return OpResult.Fail("السند غير معتمد.");

        return RunOp(() =>
        {
            var whFg = rcpt.WarehouseId;
            var orderCust = Db.ProductionOrders.Where(o => o.Id == rcpt.OrderId).Select(o => o.CustomerId).FirstOrDefault();
            var prefix = rcpt.ReceiptNumber ?? rcpt.DocumentNumber;
            // §إصلاح حرج — الإلغاء بقيد عكسي لا بحذف دفتر الأستاذ.
            // كان يحذف الحركات بـ StartsWith(prefix):
            //  • يدمّر سجلّاً إلحاقياً (قرارهم #48: «إلحاقي غير قابل للتعديل»)
            //  • وتصادم بادئات: عند السند رقم 10000 يصبح RCV-...-1000 بادئة له فيحذف حركاته
            //  • وكان يبحث الرصيد بلا CustomerId بينما Receive يكتب به ← قد يطرح من صف آخر
            // §B96 — أمر التسليم المربوط (يُحمَّل قبل التصفير ليُعكس عنه المستلَم بدقة)
            var delivery = rcpt.DeliveryId != null
                ? Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == rcpt.DeliveryId.Value)
                : null;
            int seq = 0;
            foreach (var item in rcpt.Items.Where(i => i.ReceivedQtyKg > 0))
            {
                int pkgBack = item.PackageCount > 0 && item.NetWeightKg > 0
                    ? (int)Math.Round(item.ReceivedQtyKg / item.NetWeightKg * item.PackageCount) : 0;
                seq++;
                PostStockMovement(whFg, MovementType.Outbound, item.ReceivedQtyKg, pkgBack,
                    ReferenceDocType.FinishedGoodsReceipt, $"{prefix}#REV{seq}",
                    productId: item.ProductId, lotId: item.LotId, orderId: rcpt.OrderId,
                    customerId: item.CustomerId ?? orderCust, packagingTypeId: item.PackagingTypeId,
                    notes: $"إلغاء سند الاستلام {rcpt.ReceiptNumber} — قيد عكسي");
                if (delivery != null && item.DeliveryItemId != null)
                {
                    var back = delivery.Items.FirstOrDefault(l => l.Id == item.DeliveryItemId.Value);
                    if (back != null) back.ReceivedQtyKg = Math.Max(0, back.ReceivedQtyKg - item.ReceivedQtyKg);
                }
                item.ReceivedQtyKg = 0;
            }
            rcpt.IsApproved = false;
            rcpt.ReceiptStatus = "None";
            rcpt.Status = DocStatuses.Issued;
            // §B96 — إعادة احتساب حالة أمر التسليم المربوط وإعادة فتحه إن اكتمل سابقاً
            string delReopenMsg = "";
            if (delivery != null)
            {
                bool dAny = delivery.Items.Any(i => i.ReceivedQtyKg > 0.001);
                delivery.ReceiptStatus = dAny ? "Partial" : "None";
                if (delivery.Status == DocStatuses.Completed)
                {
                    delivery.Status = DocStatuses.Issued;
                    delReopenMsg = " وأُعيد فتح أمر التسليم (استلامه لم يعد مكتملاً).";
                }
            }
            // §B86/H8: إلغاء آخر سند كامل يعيد فتح الأمر المغلق تلقائياً (التلقائي = Completed+مقفل؛ اليدوي = Closed فلا يُمس)
            string reopenMsg = "";
            bool otherFull = Db.FinishedGoodsReceipts.AsNoTracking()
                .Any(r => r.OrderId == rcpt.OrderId && r.Id != rcpt.Id && r.ReceiptStatus == "Full");
            if (!otherFull)
            {
                var ord = Db.ProductionOrders.FirstOrDefault(o => o.Id == rcpt.OrderId);
                if (ord != null && ord.IsClosed && ord.Status == DocStatuses.Completed)
                {
                    ord.IsClosed = false;
                    ord.ClosedDate = null;
                    reopenMsg = " وأُعيد فتح أمر الإنتاج (تسليمه لم يعد مكتملاً).";
                }
            }
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء السند وعكس أرصدة مخزن التام بالكامل." + reopenMsg + delReopenMsg);
        });
    }
}
