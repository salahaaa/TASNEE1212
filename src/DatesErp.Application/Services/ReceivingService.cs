using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§7 — استلام التمور واعتماد الشحنات وتوليد الدفعات (Lots) داخل معاملات ذرية.</summary>
public class ReceivingService : ServiceBase, IReceivingService
{
    public ReceivingService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public OpResult SaveShipment(int customerId, string arrivalDate, string receivedDate, List<ShipmentItemDto> items, string notes = null, string containerNumber = null, int? receivedBy = null, int? existingId = null, int? warehouseId = null)
    {
        Require("receiving", existingId == null ? "Create" : "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        var customer = Db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer == null) return OpResult.Fail("العميل غير موجود.");

        return RunOp(() =>
        {
            Shipment ship;
            if (existingId != null)
            {
                ship = Db.Shipments.Include(x => x.Items).FirstOrDefault(x => x.Id == existingId)
                       ?? throw new DomainException("أمر الاستلام غير موجود.");
                if (ship.IsApproved) throw new DomainException("أمر الاستلام معتمد — ألغِ الاعتماد أولاً للتعديل.");
                Db.ShipmentItems.RemoveRange(ship.Items);
                ship.Items.Clear();
            }
            else
            {
                ship = new Shipment { DocumentNumber = Numbering.Next("SHIP") };
                Db.Shipments.Add(ship);
            }
            ship.CustomerId = customerId;
            ship.ArrivalDate = UiFormat.TryParseDate(arrivalDate, out var a) ? a : null;
            ship.ReceivedDate = UiFormat.TryParseDate(receivedDate, out var r) ? r : DateTime.Now;
            ship.ReceivedBy = receivedBy ?? Session?.UserId;
            ship.ContainerNumber = containerNumber;
            ship.Notes = notes;
            // §المخازن المتعددة: يحفظ مخزن الاستلام الفعلي — الاعتماد يقيّد الوارد فيه تحديداً
            if (warehouseId != null && Db.Warehouses.Any(w => w.Id == warehouseId && w.IsActive))
                ship.ReceivingWarehouseId = warehouseId;
            ship.Status = DocStatuses.Draft;
            var recvDate = ship.ReceivedDate ?? DateTime.Now;
            foreach (var it in items)
            {
                // §تتبع الصنف: كل بند استلام يجب أن يسجل صنفاً صريحاً — لا استلام باسم عام («تمور» فئة وليست صنفاً)
                if (it.ProductId <= 0)
                    throw new DomainException("يجب تحديد الصنف صراحةً لكل بند استلام — لا يُقبل استلام بدون اسم صنف حقيقي (سكري، خلاص...).", "NO_PRODUCT");
                var prod = Db.Products.FirstOrDefault(p => p.Id == it.ProductId)
                           ?? throw new DomainException("الصنف المحدد في بند الاستلام غير موجود في بطاقة الأصناف.");
                if (!prod.IsActive)
                    throw new DomainException($"الصنف «{prod.ProductNameAr}» موقوف — لا يمكن الاستلام به.");

                // §نظام الوحدات: الاستلام للمواد الخام فقط (001) — الكمية القياسية كجم
                UnitsPolicy.RequireItemType(Db, it.ProductId, "Raw", "الاستلام");

                var qty = it.QtyKg > 0 ? it.QtyKg : it.PackageCount * it.UnitWeightKg;
                if (qty <= 0) throw new DomainException("كمية غير صالحة في أحد البنود.");

                // §المعالجة ضمن أمر الاستلام — قرار السطر (نعم/لا) وتاريخ انتهاء المعالجة.
                // «نعم» ⟵ حتى تاريخ إلزامي و≥ تاريخ الاستلام. «لا»/غير محدد (توافق قديم)
                // ⟵ لا معالجة ويُمسح أي تاريخ معلّق، ويُحفظ القرار كما ورد (null يبقى null
                // كي لا يُفترض «لا» بصمت على بيانات قديمة).
                bool treat = it.RequiresTreatment == true;
                DateTime? until = null;
                if (treat)
                {
                    if (!UiFormat.TryParseDate(it.TreatmentUntil, out var tu))
                        throw new DomainException(
                            $"البند «{prod.ProductNameAr}» يتطلب معالجة (نعم) — أدخل تاريخ انتهاء المعالجة «حتى تاريخ» بصيغة يوم/شهر/سنة.");
                    until = tu.Date;
                    if (until.Value.Date < recvDate.Date)
                        throw new DomainException(
                            $"تاريخ انتهاء المعالجة للبند «{prod.ProductNameAr}» ({tu:dd/MM/yyyy}) " +
                            $"يجب أن يكون أكبر من أو يساوي تاريخ الاستلام ({recvDate:dd/MM/yyyy}).");
                }

                // §المخازن المتعددة — مخزن الخام الوجهة لهذا البند (خام/ثلاجة/خام 2...).
                // فارغ = مخزن السند. يُرفض أي معرّف لمخزن غير نشط أو غير خام.
                int? destWh = it.WarehouseId;
                if (destWh != null)
                {
                    var wh = Db.Warehouses.FirstOrDefault(w => w.Id == destWh && w.IsActive && w.WarehouseType == "Raw")
                        ?? throw new DomainException(
                            $"مخزن الوجهة للبند «{prod.ProductNameAr}» غير موجود أو غير نشط أو ليس مخزن خام.");
                }

                ship.Items.Add(new ShipmentItem
                {
                    ProductId = it.ProductId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    UnitWeightKg = it.UnitWeightKg,
                    TotalWeightKg = qty,
                    RequiresTreatment = it.RequiresTreatment, // null يبقى null (توافق قديم) لا «لا» بصمت
                    TreatmentUntil = until,
                    DestinationWarehouseId = destWh,
                    // §قاعدة الاستلام: الخام قد يصل بأي عبوة — سلة/كيس/كرتون/غيرها.
                    // فتُسجَّل وحدة الاستلام الأصلية كما وردت، ولا تُفرض في الكود،
                    // والكيلو يبقى الوزن المرجعي في TotalWeightKg. ومجموعة الصنف (001) هي
                    // التي تحدد أنه خام — لا اسم الوحدة، فـ«كرتون» قد تكون عبوة خام.
                    ReceiptUnit = !string.IsNullOrWhiteSpace(it.ReceiptUnit)
                        ? it.ReceiptUnit.Trim()
                        : (Db.PackagingTypes.AsNoTracking().Where(k => k.Id == it.PackagingTypeId)
                               .Select(k => k.PackageNameAr).FirstOrDefault()
                           ?? Db.Products.AsNoTracking().Where(k => k.Id == it.ProductId)
                               .Select(k => k.UnitOfMeasure).FirstOrDefault()
                           ?? UnitsPolicy.UnitKg),
                    Status = string.IsNullOrWhiteSpace(it.ItemStatus) ? "Received" : it.ItemStatus
                });
            }
            ship.TotalWeightKg = ship.Items.Sum(i => i.TotalWeightKg);
            ship.TotalCartons = ship.Items.Sum(i => i.PackageCount);
            ship.ItemCount = ship.Items.Count;
            Db.SaveChanges();
            return OpResult.Success(existingId != null ? "تم حفظ التعديلات على سند الاستلام." : "تم حفظ أمر الاستلام بنجاح.", ship.Id, ship.DocumentNumber);
        });
    }

    /// <summary>§كشف تكرار رقم الحاوية: سندات سابقة بنفس الرقم (تحذير قبل الحفظ).</summary>
    public List<DuplicateContainerMatch> FindDuplicateContainers(string containerNumber, int? excludeShipmentId = null)
    {
        var cn = containerNumber?.Trim();
        if (string.IsNullOrWhiteSpace(cn)) return new List<DuplicateContainerMatch>();
        return Db.Shipments.AsNoTracking()
            .Where(s => s.ContainerNumber != null && s.ContainerNumber.Trim() == cn && (excludeShipmentId == null || s.Id != excludeShipmentId))
            .OrderByDescending(s => s.Id)
            .Select(s => new DuplicateContainerMatch
            {
                ShipmentId = s.Id,
                DocumentNumber = s.DocumentNumber,
                CustomerName = Db.Customers.Where(c => c.Id == s.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                ReceivedDate = s.ReceivedDate,
                TotalWeightKg = s.TotalWeightKg,
                IsApproved = s.IsApproved
            }).ToList();
    }

    /// <summary>§23 — حذف سند لم يُعتمد بعد (المسودات فقط).</summary>
    public OpResult DeleteShipment(int shipmentId)
    {
        Require("receiving", "Delete");
        return RunOp(() =>
        {
            var ship = Db.Shipments.FirstOrDefault(x => x.Id == shipmentId);
            if (ship == null) throw new DomainException("أمر الاستلام غير موجود.");
            if (ship.IsApproved) throw new DomainException("لا يمكن حذف استلام معتمد — ألغِ الاعتماد أولاً.");
            Db.Shipments.Remove(ship);
            Db.SaveChanges();
            return OpResult.Success("تم حذف سند الاستلام (المسودة).");
        });
    }

    /// <summary>الاعتماد ينشئ الدفعات ويقيد الوارد في مخزن الخام باسم العميل — معاملة واحدة.</summary>
    public OpResult ApproveShipment(int shipmentId)
    {
        Require("receiving", "Approve");
        var ship = Db.Shipments.Include(s => s.Items).FirstOrDefault(s => s.Id == shipmentId);
        if (ship == null) return OpResult.Fail("أمر الاستلام غير موجود.");
        if (ship.IsApproved) return OpResult.Fail("أمر الاستلام معتمد مسبقاً.");
        if (ship.Items.Count == 0) return OpResult.Fail("لا يمكن اعتماد استلام بدون بنود.");
        var receivedItems = ship.Items.Where(i => i.Status != "Rejected" && i.Status != "Pending").ToList();
        if (receivedItems.Count == 0) return OpResult.Fail("لا توجد بنود مستلمة للاعتماد — علّم البنود المستلمة أو أكمل الاستلام الجزئي.");

        return RunOp(() =>
        {
            foreach (var item in receivedItems)
            {
                // §المخازن المتعددة — مخزن الخام الوجهة لهذا البند (خام/ثلاجة/خام 2...).
                // قرار السطر يعلو على مخزن السند، ثم WRM للسندات القديمة بلا مخزن.
                var whRaw = item.DestinationWarehouseId
                    ?? ship.ReceivingWarehouseId
                    ?? WarehouseId("WRM");
                var lot = new Lot
                {
                    LotCode = Numbering.Next("LOT"),
                    ShipmentId = ship.Id,
                    ShipmentItemId = item.Id,
                    ProductId = item.ProductId,
                    CustomerId = ship.CustomerId,
                    PackagingTypeId = item.PackagingTypeId,
                    // §وحدات الإدخال — تُنقل وحدة الاستلام الأصلية (سلة/كرتون/كجم) للدفعة
                    // حتى يبقى التخطيط والتتبع بنفس وحدة الاستلام دون فقدها.
                    ReceiptUnit = item.ReceiptUnit,
                    LotDate = ship.ReceivedDate ?? DateTime.Now,
                    InitialQtyKg = item.TotalWeightKg,
                    InStockQtyKg = item.TotalWeightKg,
                    WarehouseId = whRaw, // §وجهة الدفعة — تعود إليها الكمية عند الإفراج من المعالجة
                    // §المعالجة ضمن أمر الاستلام — قرار السطر هو مصدر الحقيقة الموروث للدفعة:
                    // true = تحتاج معالجة · false = جاهزة · null = قديمة بلا قرار (تُحلّ في الترحيل).
                    RequiresTreatment = item.RequiresTreatment,
                    Status = DocStatuses.Approved
                };
                Db.Lots.Add(lot);
                Db.SaveChanges(); // للحصول على معرف الدفعة قبل قيد الحركة
                PostStockMovement(whRaw, MovementType.Inbound, item.TotalWeightKg, item.PackageCount,
                    ReferenceDocType.ShipmentReceipt, ship.DocumentNumber,
                    productId: item.ProductId, lotId: lot.Id, customerId: ship.CustomerId,
                    packagingTypeId: item.PackagingTypeId,
                    notes: $"استلام شحنة {ship.DocumentNumber}");
                item.Status = DocStatuses.Approved;

                // §المعالجة ضمن أمر الاستلام — بند «المعالجة = نعم» يدخل مخزن المعالجة فور الاعتماد:
                // تُنشأ عملية معالجة تلقائية (مصدر الحقيقة للحالة) ويُحوَّل الرصيد من مخزن الدفعة إلى WTRT
                // ويُعلَّم رصيد الدفعة «تحت المعالجة» فلا يُتاح للتخطيط/الإنتاج قبل انتهاء المدة.
                // الإفراج بعد انتهاء المدة يبقى في دورة المعالجة المعتمدة (شاشة «معالجة وتعقيم الخام»).
                if (item.RequiresTreatment == true && item.TreatmentUntil != null)
                {
                    // إبقاء قيد الاستلام الوارد ظاهراً في المخزن قبل التحويل حتى يجده قيد التحويل
                    Db.SaveChanges();
                    var startedAt = ship.ReceivedDate ?? DateTime.Now;
                    var until = item.TreatmentUntil.Value.Date;
                    var trt = new RawTreatment
                    {
                        TreatmentNo = Numbering.Next("TRT"),
                        LotId = lot.Id,
                        ProductId = item.ProductId,          // §لا صنف جديد: يُنسخ من الدفعة كما هو
                        QtyKg = item.TotalWeightKg,
                        PackageCount = item.PackageCount,
                        StartedAt = startedAt,
                        DurationHours = Math.Max(0, (until.AddDays(1) - startedAt).TotalHours),
                        ExpectedReadyAt = until.AddDays(1).AddTicks(-1), // نهاية يوم «حتى تاريخ»
                        ResponsibleUserId = ship.ReceivedBy ?? Session?.UserId,
                        Notes = $"معالجة تلقائية من سند الاستلام {ship.DocumentNumber} — حتى تاريخ {until:dd/MM/yyyy}",
                        Status = TreatmentStatuses.InProgress
                    };
                    Db.RawTreatments.Add(trt);
                    Db.SaveChanges(); // للحصول على معرف العملية قبل قيد الحركة

                    PostStockMovement(WarehouseId("WTRT"), MovementType.Inbound, item.TotalWeightKg, item.PackageCount,
                        ReferenceDocType.TreatmentStart, trt.TreatmentNo,
                        productId: item.ProductId, lotId: lot.Id, customerId: ship.CustomerId,
                        packagingTypeId: item.PackagingTypeId,
                        notes: $"بدء معالجة {trt.TreatmentNo} — من سند الاستلام {ship.DocumentNumber}");
                    PostStockMovement(whRaw, MovementType.Outbound, item.TotalWeightKg, item.PackageCount,
                        ReferenceDocType.TreatmentStart, trt.TreatmentNo,
                        productId: item.ProductId, lotId: lot.Id, customerId: ship.CustomerId,
                        packagingTypeId: item.PackagingTypeId,
                        notes: $"بدء معالجة {trt.TreatmentNo} — من سند الاستلام {ship.DocumentNumber}");

                    lot.UnderTreatmentQtyKg = item.TotalWeightKg;
                }
                // «لا» أو null ← لا معالجة تلقائية: يبقى UnderTreatmentQtyKg = 0 فالكمية
                // متاحة فوراً للتخطيط/الإنتاج (AvailableQtyKg = InStock − Reserved − 0).
                // TreatmentReadyQtyKg لا يُلمَس هنا: يُراكم فقط عند الإفراج من المعالجة
                // (RawTreatmentService.Release) وللدفعات القائمة عبر BackfillTreatmentReadiness —
                // ولو مُلئ عند الاستلام لخرق حارس «لم تكتمل معالجتها» على الأصناف التي تشترط معالجة.
            }
            ship.IsApproved = true;
            ship.Status = DocStatuses.Approved;
            ship.ApprovedBy = Session?.UserId;
            ship.ApprovedDate = DateTime.Now;
            Db.SaveChanges();
            var pendingCount = ship.Items.Count(i => i.Status == "Pending");
            var rejCount = ship.Items.Count(i => i.Status == "Rejected");
            return OpResult.Success($"تم اعتماد الاستلام وإنشاء {receivedItems.Count} دفعة تلقائياً."
                + (pendingCount > 0 ? $" تبقّى {pendingCount} بنداً معلّقاً لاستلام لاحق." : "")
                + (rejCount > 0 ? $" رُفض {rejCount} بنداً." : ""), ship.Id, ship.DocumentNumber);
        });
    }

    public OpResult UnapproveShipment(int shipmentId)
    {
        Require("receiving", "Cancel");
        var ship = Db.Shipments.Include(s => s.Lots).FirstOrDefault(s => s.Id == shipmentId);
        if (ship == null) return OpResult.Fail("أمر الاستلام غير موجود.");
        if (!ship.IsApproved) return OpResult.Fail("أمر الاستلام غير معتمد.");

        return RunOp(() =>
        {
            // منع الإلغاء إذا استُهلك من الدفعات أي كمية (§8)
            foreach (var lot in ship.Lots)
            {
                if (lot.ProducedQtyKg > 0 || lot.DeliveredQtyKg > 0)
                    throw new DomainException($"لا يمكن إلغاء الاستلام: الدفعة {lot.LotCode} استُهلك منها بالفعل.");

                // §المعالجة ضمن أمر الاستلام — إلغاء الاعتماد يعكس أيضاً دورة المعالجة التلقائية:
                // عمليات المعالجة + حركاتها (TreatmentStart/Release) + رصيد مستودع المعالجة.
                var treatments = Db.RawTreatments.Where(t => t.LotId == lot.Id).ToList();
                foreach (var trt in treatments)
                {
                    Db.InventoryTransactions.RemoveRange(Db.InventoryTransactions.Where(t =>
                        t.LotId == lot.Id &&
                        (t.ReferenceDocType == ReferenceDocType.TreatmentStart || t.ReferenceDocType == ReferenceDocType.TreatmentRelease) &&
                        t.ReferenceDocNumber.StartsWith(trt.TreatmentNo)));
                }
                Db.RawTreatments.RemoveRange(treatments);

                // §تعكس حركة استلام الشحنة نفسها
                Db.InventoryTransactions.RemoveRange(Db.InventoryTransactions.Where(t =>
                    t.ReferenceDocType == ReferenceDocType.ShipmentReceipt && t.ReferenceDocNumber == ship.DocumentNumber && t.LotId == lot.Id));

                // §إزالة أرصدة الدفعة من كل المخازن (خام/معالجة) — لا بقايا صفرية تُبقي قيوداً وهمية
                Db.StockBalances.RemoveRange(Db.StockBalances.Where(b => b.LotId == lot.Id));
                Db.Lots.Remove(lot);
            }
            ship.IsApproved = false;
            ship.Status = DocStatuses.Draft;
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء اعتماد الاستلام وعكس أرصدته.");
        });
    }

    /// <summary>§استلام جزئي: سند لاحق يكمل البنود المعلّقة في سند معتمد (سلسلة ParentShipmentId).</summary>
    public OpResult ReceiveRemaining(int shipmentId)
    {
        Require("receiving", "Create");
        var src = Db.Shipments.Include(x => x.Items).FirstOrDefault(x => x.Id == shipmentId);
        if (src == null) return OpResult.Fail("السند الأصلي غير موجود.");
        if (!src.IsApproved) return OpResult.Fail("أكمل اعتماد السند الأصلي أولاً.");
        var pend = src.Items.Where(i => i.Status == "Pending").ToList();
        if (pend.Count == 0) return OpResult.Fail("لا توجد بنود معلّقة متبقية في هذا السند.");
        return RunOp(() =>
        {
            var ship = new Shipment
            {
                DocumentNumber = Numbering.Next("SHIP"),
                CustomerId = src.CustomerId,
                ContainerNumber = src.ContainerNumber,
                ArrivalDate = src.ArrivalDate,
                ReceivedDate = DateTime.Now,
                ParentShipmentId = src.Id,
                Status = DocStatuses.Draft
            };
            foreach (var it in pend)
            {
                it.Status = "Moved"; // انتقلت للسند اللاحق — تبقى للأثر التدقيقي
                ship.Items.Add(new ShipmentItem
                {
                    ProductId = it.ProductId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    UnitWeightKg = it.UnitWeightKg,
                    TotalWeightKg = it.TotalWeightKg,
                    ReceiptUnit = it.ReceiptUnit,
                    RequiresTreatment = it.RequiresTreatment,
                    TreatmentUntil = it.TreatmentUntil,
                    DestinationWarehouseId = it.DestinationWarehouseId, // §يبقى البند المعلّق على وجهته (خام/ثلاجة...)
                    Status = "Received"
                });
            }
            Db.Shipments.Add(ship);
            Db.SaveChanges();
            return OpResult.Success($"أُنشئ سند الاستلام اللاحق {ship.DocumentNumber} بـ {ship.Items.Count} بنداً معلّقاً — اعتمده ليدخل المخزون.", ship.Id, ship.DocumentNumber);
        });
    }
}
