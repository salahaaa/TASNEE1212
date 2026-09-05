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

    public OpResult Save(int customerId, string deliveryDate, int? orderId, List<CustomerDeliveryItemDto> items)
    {
        Require("delivery", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
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
                // §8 — رصيد العميل المتاح في مخزن التام يجب أن يغطي الكمية
                var balance = Db.StockBalances.FirstOrDefault(b =>
                    b.WarehouseId == whFg && b.ProductId == item.ProductId
                    && (item.LotId == null || b.LotId == item.LotId) && b.CustomerId == dlv.CustomerId);
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
