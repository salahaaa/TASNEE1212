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
                // §B107 — تُحذف أجزاء درجات الإصابة صراحةً مع بنودها. الحذف المتتالي
                // معرَّف في النموذج، لكن التصريح به هنا يحمي قواعد قديمة أُنشئ فيها
                // الجدول بالترحيل الآمن بلا قيد أجنبي.
                var oldItemIds = ship.Items.Select(i => i.Id).ToList();
                Db.ShipmentItemTreatmentParts.RemoveRange(
                    Db.ShipmentItemTreatmentParts.Where(p => oldItemIds.Contains(p.ShipmentItemId)));
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

                // §B107 — الوجهة: مخزن الخام أم مستودع المعالجة. تُوحَّد هنا فلا يصل
                // كود حر إلى حركة مخزون، والفارغ = مخزن الخام (سلوك كل السندات القائمة).
                var dest = ReceiptDestinations.Normalize(it.Destination);
                if (dest == ReceiptDestinations.Treatment && !prod.RequiresTreatment)
                    throw new DomainException(
                        $"الصنف «{prod.ProductNameAr}» غير معلَّم بأنه «يحتاج معالجة» في بطاقة الأصناف،\n"
                        + "فلا يجوز توجيهه إلى مستودع المعالجة. فعّل العلم في بطاقة الصنف أو وجّه البند لمخزن الخام.",
                        "NOT_TREATABLE");

                var newItem = new ShipmentItem
                {
                    ProductId = it.ProductId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    UnitWeightKg = it.UnitWeightKg,
                    TotalWeightKg = qty,
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
                    Status = string.IsNullOrWhiteSpace(it.ItemStatus) ? "Received" : it.ItemStatus,
                    Destination = dest
                };
                ship.Items.Add(newItem);

                // §B107 — تقسيم كمية البند الواحد لأجزاء بدرجات إصابة مختلفة (5/7/10 أيام)
                // **بلا صنف جديد**: كل جزء يصير صف RawTreatment مستقلاً على نفس الدفعة عند الاعتماد.
                BuildTreatmentParts(newItem, it, dest, qty);
            }
            ship.TotalWeightKg = ship.Items.Sum(i => i.TotalWeightKg);
            ship.TotalCartons = ship.Items.Sum(i => i.PackageCount);
            ship.ItemCount = ship.Items.Count;
            Db.SaveChanges();
            return OpResult.Success(existingId != null ? "تم حفظ التعديلات على سند الاستلام." : "تم حفظ أمر الاستلام بنجاح.", ship.Id, ship.DocumentNumber);
        });
    }

    /// <summary>
    /// §B107 — يبني أجزاء درجات الإصابة لبند واحد ويتحقق من اتساقها مع كمية البند.
    ///
    /// • وجهة مخزن الخام ← لا أجزاء إطلاقاً (وأي أجزاء واردة تُهمل، فهي بلا معنى هناك).
    /// • وجهة المعالجة بلا أجزاء ← جزء واحد ضمني بكامل الكمية بدرجة متوسطة (7 أيام)،
    ///   كي لا يُجبر أمين المخزن على تقسيم بضاعة متجانسة.
    /// • مجموع الأجزاء يجب أن يساوي كمية البند بالضبط — وإلا اختل ثابت التوازن بين
    ///   مخزن الخام ومستودع المعالجة لحظة الاعتماد.
    /// </summary>
    private void BuildTreatmentParts(ShipmentItem newItem, ShipmentItemDto it, string dest, double qty)
    {
        if (dest != ReceiptDestinations.Treatment) return;

        var src = (it.TreatmentParts ?? new List<TreatmentPartDto>())
            .Where(p => p != null && p.QtyKg > 0).ToList();

        if (src.Count == 0)
            src.Add(new TreatmentPartDto
            {
                InfestationLevel = InfestationLevels.Medium,
                QtyKg = qty,
                PackageCount = it.PackageCount
            });

        double sum = src.Sum(p => p.QtyKg);
        if (Math.Abs(sum - qty) > 0.001)
            throw new DomainException(
                $"مجموع أجزاء درجات الإصابة ({sum:N1} كجم) لا يساوي كمية البند ({qty:N1} كجم).\n"
                + "صحّح الأجزاء — الفرق يعني كميةً لا يعرف النظام أين تذهب.", "PARTS_MISMATCH");

        // §أنواع المعالجة المبذورة في B106 تُقرأ مرة واحدة لكل بند (لا استعلام داخل حلقة)
        var typeByCode = Db.TreatmentTypes.AsNoTracking()
            .Where(t => t.IsActive)
            .ToDictionary(t => t.TypeCode, t => new { t.Id, t.DefaultDurationHours });

        int packagesLeft = it.PackageCount;
        for (int i = 0; i < src.Count; i++)
        {
            var p = src[i];
            var level = InfestationLevels.Normalize(p.InfestationLevel);
            var code = InfestationLevels.TypeCode(level);
            typeByCode.TryGetValue(code, out var type);

            // §الطرود بنسبة الكمية حين لا يحددها المستخدم — والآخر يأخذ الباقي فلا يضيع طرد بالتقريب
            int packages = p.PackageCount > 0
                ? p.PackageCount
                : (i == src.Count - 1
                    ? Math.Max(0, packagesLeft)
                    : (qty <= 0 ? 0 : (int)Math.Round(it.PackageCount * (p.QtyKg / qty), MidpointRounding.AwayFromZero)));
            packagesLeft -= packages;

            newItem.TreatmentParts.Add(new ShipmentItemTreatmentPart
            {
                InfestationLevel = level,
                TreatmentTypeId = type?.Id,
                QtyKg = p.QtyKg,
                PackageCount = Math.Max(0, packages),
                // §المدة تُثبَّت على درجة الإصابة لا على تقنية المعالجة (فجوة B106 §9①)
                DurationHours = p.DurationHours ?? type?.DefaultDurationHours ?? InfestationLevels.DefaultHours(level),
                Notes = p.Notes
            });
        }
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
            // §المخازن المتعددة: مخزن الاستلام المختار في السند — أو الافتراضي WRM للسندات القديمة
            var whRaw = ship.ReceivingWarehouseId ?? WarehouseId("WRM");
            // §B107 — عدّاد المعالجات التي بدأت تلقائياً، ليعرف الموظف ما حدث في رسالة واحدة
            int startedCount = 0; double startedQty = 0;
            foreach (var item in receivedItems)
            {
                var lot = new Lot
                {
                    LotCode = Numbering.Next("LOT"),
                    ShipmentId = ship.Id,
                    ShipmentItemId = item.Id,
                    ProductId = item.ProductId,
                    CustomerId = ship.CustomerId,
                    PackagingTypeId = item.PackagingTypeId,
                    LotDate = ship.ReceivedDate ?? DateTime.Now,
                    InitialQtyKg = item.TotalWeightKg,
                    InStockQtyKg = item.TotalWeightKg,
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

                // §B107 — **بدء المعالجة تلقائياً عند اعتماد السند**.
                // الترتيب مقصود: تُقيَّد الكمية أولاً وارداً في مخزن الخام ثم تُنقل منه إلى
                // مستودع المعالجة. فالتتبع يبقى كاملاً (استلام ← معالجة) ويبقى ثابت التوازن
                // رصيد(الخام) + رصيد(WTRT) = InStockQtyKg صحيحاً في كل لحظة.
                if (ReceiptDestinations.IsTreatment(item.Destination))
                {
                    var parts = Db.ShipmentItemTreatmentParts.Where(p => p.ShipmentItemId == item.Id).ToList();
                    if (parts.Count == 0)
                        parts.Add(new ShipmentItemTreatmentPart
                        {
                            InfestationLevel = InfestationLevels.Medium,
                            QtyKg = item.TotalWeightKg,
                            PackageCount = item.PackageCount,
                            DurationHours = InfestationLevels.DefaultHours(InfestationLevels.Medium)
                        });

                    // §وقت البدء = لحظة الاعتماد، لا تاريخ الاستلام المُدخل. لو أُخذ الأخير
                    // لأمكن اعتماد سند بتاريخ قديم فتخرج البضاعة «جاهزة» فوراً بلا معالجة فعلية.
                    var startAt = DateTime.Now;
                    foreach (var part in parts)
                    {
                        var typeId = part.TreatmentTypeId ?? Db.TreatmentTypes
                            .Where(t => t.TypeCode == InfestationLevels.TypeCode(part.InfestationLevel))
                            .Select(t => (int?)t.Id).FirstOrDefault();
                        double hours = part.DurationHours
                            ?? Db.TreatmentTypes.Where(t => t.Id == typeId)
                                 .Select(t => (double?)t.DefaultDurationHours).FirstOrDefault()
                            ?? InfestationLevels.DefaultHours(part.InfestationLevel);

                        // §لا فحص أهلية: الدفعة أُنشئت للتو من كمية هذا البند بعينه،
                        // فالكمية مضمونة بحكم مصدرها ولا مخزون سابقاً يُقارن به.
                        StartTreatmentCore(lot, typeId, part.QtyKg, part.PackageCount,
                            startAt, hours, ship.ReceivedBy ?? Session?.UserId,
                            $"بدء تلقائي من اعتماد سند الاستلام {ship.DocumentNumber} — "
                            + $"{InfestationLevels.ToArabic(part.InfestationLevel)}"
                            + (string.IsNullOrWhiteSpace(part.Notes) ? "" : $" · {part.Notes}"),
                            checkEligibility: false, sourceWarehouseId: whRaw);

                        startedCount++;
                        startedQty += part.QtyKg;
                    }
                }
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
                + (rejCount > 0 ? $" رُفض {rejCount} بنداً." : "")
                + (startedCount > 0
                    ? $"\n🧪 بدأت {startedCount} عملية معالجة تلقائياً على {startedQty:N1} كجم — تابعها من شاشة «معالجة وتعقيم الخام»."
                    : ""), ship.Id, ship.DocumentNumber);
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
                // §B107 — الاعتماد قد يكون بدأ معالجةً تلقائياً، فالكمية غادرت مخزن الخام
                // إلى WTRT. عكسُ الاستلام هنا كان سيُنقص رصيداً لم يعد موجوداً ويكسر
                // ثابت التوازن. الإلغاء يمر بإلغاء المعالجة أولاً — فعل بشري موثّق.
                if (lot.UnderTreatmentQtyKg > 0.001 || Db.RawTreatments.Any(t => t.LotId == lot.Id))
                    throw new DomainException(
                        $"لا يمكن إلغاء الاستلام: الدفعة {lot.LotCode} دخلت دورة المعالجة والتعقيم.\n"
                        + "ألغِ عمليات المعالجة المرتبطة بها من شاشة «معالجة وتعقيم الخام» أولاً.",
                        "IN_TREATMENT");
                // §تعكس الأرصدة من المخزن نفسه الذي قُيّدت فيه عند الاعتماد
                var whRaw = ship.ReceivingWarehouseId ?? WarehouseId("WRM");
                var balance = Db.StockBalances.FirstOrDefault(b => b.WarehouseId == whRaw && b.LotId == lot.Id);
                if (balance != null)
                {
                    balance.QtyKg -= lot.InitialQtyKg;
                    if (balance.QtyKg < -0.001) throw new DomainException("لا يمكن الإلغاء: الرصيد الحالي لا يغطي كمية الدفعة.");
                }
                Db.InventoryTransactions.RemoveRange(Db.InventoryTransactions.Where(t =>
                    t.ReferenceDocType == ReferenceDocType.ShipmentReceipt && t.ReferenceDocNumber == ship.DocumentNumber && t.LotId == lot.Id));
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
                var moved = new ShipmentItem
                {
                    ProductId = it.ProductId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    UnitWeightKg = it.UnitWeightKg,
                    TotalWeightKg = it.TotalWeightKg,
                    ReceiptUnit = it.ReceiptUnit,
                    Status = "Received",
                    // §B107 — الوجهة تنتقل مع البند: البند المعلّق الموجَّه للمعالجة يظل كذلك
                    Destination = ReceiptDestinations.Normalize(it.Destination)
                };
                // §B107 — وكذلك تقسيم درجات الإصابة، فلا يُعيد الموظف إدخاله في السند اللاحق
                foreach (var part in Db.ShipmentItemTreatmentParts.AsNoTracking().Where(p => p.ShipmentItemId == it.Id))
                    moved.TreatmentParts.Add(new ShipmentItemTreatmentPart
                    {
                        InfestationLevel = part.InfestationLevel,
                        TreatmentTypeId = part.TreatmentTypeId,
                        QtyKg = part.QtyKg,
                        PackageCount = part.PackageCount,
                        DurationHours = part.DurationHours,
                        Notes = part.Notes
                    });
                ship.Items.Add(moved);
            }
            Db.Shipments.Add(ship);
            Db.SaveChanges();
            return OpResult.Success($"أُنشئ سند الاستلام اللاحق {ship.DocumentNumber} بـ {ship.Items.Count} بنداً معلّقاً — اعتمده ليدخل المخزون.", ship.Id, ship.DocumentNumber);
        });
    }
}
