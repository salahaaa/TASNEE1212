using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §7/§8 — تسليم الإنتاج للعميل مع الحراس:
/// لا تسليم أكبر من رصيد العميل في مخزن التام، لا تسليم دفعة عميل لعميل آخر، لا تكرار التسليم.
/// </summary>
public class CustomerDeliveryService : ServiceBase, ICustomerDeliveryService
{
    public CustomerDeliveryService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    /// <summary>§B105/P1 — حارس الكمية: صفر وسالب ممنوعان (كان السالب يخصم الرصيد ويعكس المسلَّم سالباً).</summary>
    private static string QtyGuard(CustomerDeliveryItemDto it)
    {
        if (it.QtyKg <= 0) return "كمية البند يجب أن تكون أكبر من صفر — لا تُقبل بنود صفرية أو سالبة.";
        if (it.PackageCount < 0) return "عدد العبوات لا يمكن أن يكون سالباً.";
        return null;
    }

    /// <summary>حراس البند المشتركة (الحفظ والتعديل): كمية + ملكية دفعة + تحويل + نوع تام.</summary>
    private void ItemGuards(int customerId, CustomerDeliveryItemDto it)
    {
        string q = QtyGuard(it);
        if (q != null) throw new DomainException(q, "INVALID_QTY");

        // §8 — الدفعة يجب أن تخص نفس العميل
        if (it.LotId is int lotId)
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId);
            if (lot == null) throw new DomainException("الدفعة غير موجودة.");
            if (lot.CustomerId != null && lot.CustomerId != customerId)
                throw new DomainException($"لا يمكن تسليم كمية العميل إلى عميل آخر — الدفعة {lot.LotCode} تخص عميلاً مختلفاً.", "CROSS_CUSTOMER");
        }

        // §تتبع الصنف: لا تسليم خلاص من دفعة سكري — التحويل الرسمي فقط حسب بطاقة المنتج
        ProductIdentityGuard.EnsureConversionAllowed(Db, it.ProductId, it.LotId);

        // §نظام الوحدات: التسليم للعميل منتجات تامة فقط (002)
        UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "تسليم العميل");
    }

    public OpResult Save(int customerId, string deliveryDate, int? orderId, List<CustomerDeliveryItemDto> items)
    {
        Require("delivery", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        foreach (var itq in items)
        {
            string q = QtyGuard(itq);
            if (q != null) return OpResult.Fail(q);
        }
        var customer = Db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer == null) return OpResult.Fail("العميل غير موجود.");

        return RunOp(() =>
        {
            var dlv = new CustomerDelivery
            {
                DocumentNumber = Numbering.Next("CD"),
                CustomerId = customerId,
                OrderId = orderId,
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : DateTime.Now,
                Status = DocStatuses.Draft
            };
            foreach (var it in items)
            {
                ItemGuards(customerId, it);
                // §مؤجَّل بقرار: توحيد الكرتون/الكيلو عند التسليم يغيّر كميات تاريخية ويحتاج
                // قرار منتج (أي المدخلين مرجعي). موثق في أمر العمل — لا يُطبَّق ضمن هذه الحزمة.

                dlv.Items.Add(new CustomerDeliveryItem
                {
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    QtyKg = it.QtyKg,
                    // §القاعدة 7: وزن الكرتون وقت التسليم — لا يتغير بتعريف العبوة لاحقاً
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId)
                });
            }
            dlv.TotalQtyKg = dlv.Items.Sum(i => i.QtyKg);
            dlv.TotalCartons = dlv.Items.Sum(i => i.PackageCount);
            Db.CustomerDeliveries.Add(dlv);
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ سند تسليم العميل {dlv.DocumentNumber}.", dlv.Id, dlv.DocumentNumber);
        });
    }

    /// <summary>§B105/P2 — تعديل سند مسودة: يستبدل البنود بنفس حراس الحفظ. المعتمد يُرفض.</summary>
    public OpResult Update(int deliveryId, int customerId, string deliveryDate, int? orderId, List<CustomerDeliveryItemDto> items)
    {
        Require("delivery", "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        foreach (var itq in items)
        {
            string q = QtyGuard(itq);
            if (q != null) return OpResult.Fail(q);
        }
        var customer = Db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer == null) return OpResult.Fail("العميل غير موجود.");

        return RunOp(() =>
        {
            var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId)
                      ?? throw new DomainException("سند التسليم غير موجود.");
            if (dlv.IsApproved || dlv.Status == DocStatuses.Completed)
                throw new DomainException("السند معتمد — لا يُعدَّل. ألغِ الاعتماد أولاً ثم عدّل.", "APPROVED_LOCK");

            dlv.CustomerId = customerId;
            dlv.OrderId = orderId ?? dlv.OrderId;
            dlv.DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : dlv.DeliveryDate;

            Db.CustomerDeliveryItems.RemoveRange(dlv.Items);
            dlv.Items.Clear();
            foreach (var it in items)
            {
                ItemGuards(customerId, it);
                dlv.Items.Add(new CustomerDeliveryItem
                {
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    QtyKg = it.QtyKg,
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId)
                });
            }
            dlv.TotalQtyKg = dlv.Items.Sum(i => i.QtyKg);
            dlv.TotalCartons = dlv.Items.Sum(i => i.PackageCount);
            Db.SaveChanges();
            return OpResult.Success($"تم تحديث سند التسليم {dlv.DocumentNumber}.", dlv.Id, dlv.DocumentNumber);
        });
    }

    /// <summary>§B105/P2 — حذف مسودة فقط: لم تخصم شيئاً فلا أثر مخزني — المعتمد لا يُحذف أبداً.</summary>
    public OpResult DeleteDraft(int deliveryId)
    {
        Require("delivery", "Delete");
        var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (dlv == null) return OpResult.Fail("سند التسليم غير موجود.");
        if (dlv.IsApproved || dlv.Status == DocStatuses.Completed)
            return OpResult.Fail("السند معتمد — لا يُحذف. إن لزم التصحيح: ألغِ الاعتماد (قيد عكسي) ثم عدّل.");
        return RunOp(() =>
        {
            string docNo = dlv.DocumentNumber;
            Db.CustomerDeliveryItems.RemoveRange(dlv.Items);
            Db.CustomerDeliveries.Remove(dlv);
            Db.SaveChanges();
            return OpResult.Success($"تم حذف سند التسليم المسودة {docNo}.");
        });
    }

    public OpResult Approve(int deliveryId)
    {
        Require("delivery", "Approve");
        var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (dlv == null) return OpResult.Fail("سند التسليم غير موجود.");
        if (dlv.IsApproved) return OpResult.Fail("سند التسليم معتمد مسبقاً — لا يسمح بتكرار التسليم.");

        // §إصلاح حرج — بوابة الجودة المركزية.
        // كانت البوابة داخل if (dlv.OrderId is int) والواجهة تمرر null، فلم تُنفَّذ أبداً؛
        // وكانت تفحص IsApproved فقط لا Decision. أُثبت بالتشغيل أن دفعة «مرفوضة تماماً» سُلّمت للعميل.
        foreach (var item in dlv.Items)
        {
            var (ok, reason) = QualityGate.CustomerDeliveryAllowed(Db, dlv.OrderId, item.LotId, item.ProductId);
            if (!ok) return OpResult.Fail(reason);
        }

        return RunOp(() =>
        {
            var whFg = WarehouseId("WFG");
            foreach (var item in dlv.Items)
            {
                // §B105/P1 — دفاع ثانٍ: الاعتماد لا يمرر صفراً ولا سالباً ولو حُفظ قبل الإصلاح
                if (item.QtyKg <= 0)
                    throw new DomainException("كمية البند يجب أن تكون أكبر من صفر — لا تُقبل بنود صفرية أو سالبة.", "INVALID_QTY");

                // §B105/P6 — بند بلا دفعة: لا خصم اعتباطي؛ إن تعددت أرصدة الصنف للعميل فالدفعة إلزامية
                int? effectiveLot = item.LotId;
                if (effectiveLot == null)
                {
                    var lotsWith = Db.StockBalances.AsNoTracking()
                        .Where(b => b.WarehouseId == whFg && b.ProductId == item.ProductId
                            && b.CustomerId == dlv.CustomerId && b.QtyKg > 0.001 && b.LotId != null)
                        .Select(b => b.LotId!.Value).Distinct().ToList();
                    if (lotsWith.Count > 1)
                    {
                        string pname0 = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"صنف #{item.ProductId}";
                        throw new DomainException(
                            $"الصنف «{pname0}» له رصيد في {lotsWith.Count} دفعات مختلفة — حدّد الدفعة في البند حتى لا يُخصم اعتباطياً.",
                            "LOT_REQUIRED");
                    }
                    if (lotsWith.Count == 1) { effectiveLot = lotsWith[0]; item.LotId = effectiveLot; }
                }

                // §8 — رصيد العميل المتاح في مخزن التام يجب أن يغطي الكمية
                var balance = Db.StockBalances.FirstOrDefault(b =>
                    b.WarehouseId == whFg && b.ProductId == item.ProductId
                    && (effectiveLot == null || b.LotId == effectiveLot) && b.CustomerId == dlv.CustomerId);
                var available = balance?.QtyKg ?? 0;
                if (available < item.QtyKg - 0.001)
                    throw new DomainException(
                        $"الكمية أكبر من رصيد العميل في مخزن التام.\nالمتاح: {available:N1} كجم — المطلوب: {item.QtyKg:N1} كجم",
                        "INSUFFICIENT_CUSTOMER_BALANCE");

                // §B95 — سقف المطابق المعتمد: بعد الرصيد (لتبقى رسائله) وقبل الخصم
                var (qok, qreason) = QualityGate.CustomerDeliveryQtyAllowed(Db, dlv, item);
                if (!qok) throw new DomainException(qreason);

                PostStockMovement(whFg, MovementType.Outbound, item.QtyKg, item.PackageCount,
                    ReferenceDocType.CustomerDelivery, dlv.DocumentNumber,
                    productId: item.ProductId, lotId: item.LotId, customerId: dlv.CustomerId,
                    orderId: dlv.OrderId, packagingTypeId: item.PackagingTypeId,
                    notes: $"تسليم عميل — سند {dlv.DocumentNumber}");

                if (item.LotId is int lotId)
                {
                    var lot = Db.Lots.First(l => l.Id == lotId);
                    lot.DeliveredQtyKg += item.QtyKg;
                }
            }
            dlv.IsApproved = true;
            dlv.IsPosted = true;
            dlv.Status = DocStatuses.Completed;
            dlv.PostedDate = dlv.ApprovedDate = DateTime.Now;
            dlv.ApprovedBy = Session?.UserId;
            // §الخطة الطويلة: توزيع المسلَّم على بنود الخطة الخاصة بالعميل (§9 الفوترة على المسلَّم فقط)
            // §B86/M5: التوزيع لكل بند تسليم (صنفه وعبوته) — لا كيلو إجمالي أعمى
            foreach (var sItem in dlv.Items)
                PlanSync.SyncDeliveredForCustomer(Db, dlv.CustomerId, sItem.ProductId, sItem.PackagingTypeId, sItem.QtyKg);
            Db.SaveChanges();
            return OpResult.Success($"تم اعتماد التسليم وخصم {dlv.TotalQtyKg:N1} كجم من رصيد العميل.", dlv.Id, dlv.DocumentNumber);
        });
    }

    public OpResult Unapprove(int deliveryId)
    {
        Require("delivery", "Cancel");
        var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (dlv == null) return OpResult.Fail("السند غير موجود.");
        if (!dlv.IsApproved) return OpResult.Fail("السند غير معتمد.");

        return RunOp(() =>
        {
            var whFg = WarehouseId("WFG");
            // §إصلاح حرج — الإلغاء بقيد عكسي (وارد) لا بحذف حركات دفتر الأستاذ.
            int seq = 0;
            foreach (var item in dlv.Items)
            {
                seq++;
                PostStockMovement(whFg, MovementType.Inbound, item.QtyKg, item.PackageCount,
                    ReferenceDocType.CustomerDelivery, $"{dlv.DocumentNumber}#REV{seq}",
                    productId: item.ProductId, lotId: item.LotId, customerId: dlv.CustomerId,
                    orderId: dlv.OrderId, packagingTypeId: item.PackagingTypeId,
                    notes: $"إلغاء سند التسليم {dlv.DocumentNumber} — قيد عكسي");
                if (item.LotId is int lotId)
                {
                    var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId);
                    if (lot != null) lot.DeliveredQtyKg -= item.QtyKg;
                }
            }
            dlv.IsApproved = false;
            dlv.IsPosted = false;
            dlv.Status = DocStatuses.Draft;
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء التسليم وإعادة الكميات إلى رصيد العميل.");
        });
    }
}
