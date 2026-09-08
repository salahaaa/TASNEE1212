using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B107 — **الوصلة الناقصة**: ربط شاشة الاستلام بدورة المعالجة (فجوتا FIXLOG B106 §9③ و§10①).
///
/// أربعة مطالب:
/// ① عمود «يحتاج معالجة» من <c>Product.RequiresTreatment</c>
/// ② عمود الوجهة: مخزن الخام أم مستودع المعالجة
/// ③ تقسيم كمية البند الواحد لأجزاء بدرجات إصابة مختلفة (5/7/10 أيام) **بلا صنف جديد**
/// ④ بدء المعالجة تلقائياً عند اعتماد السند
///
/// الاختبارات تمر بالخدمات الحقيقية كاملةً (استلام ← اعتماد ← معالجة) لا بحقن صفوف يدوياً.
/// </summary>
public class B107ReceivingTreatmentLinkTests
{
    private const double BasketKg = 20;

    private static (TestHost host, DatesErpDbContext db, IReceivingService rec, Product raw) Setup(bool requiresTreatment = true)
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var raw = db.Products.First(p => p.ProductCode == "001-001");
        raw.RequiresTreatment = requiresTreatment;
        db.SaveChanges();
        return (host, db, host.Get<IReceivingService>(), raw);
    }

    private static ShipmentItemDto Item(Product raw, int baskets, string dest, params TreatmentPartDto[] parts)
        => new()
        {
            ProductId = raw.Id,
            PackagingTypeId = 3,
            PackageCount = baskets,
            UnitWeightKg = BasketKg,
            QtyKg = baskets * BasketKg,
            ReceiptUnit = "سلة",
            Destination = dest,
            TreatmentParts = parts.ToList()
        };

    private static TreatmentPartDto Part(string level, double qty, int packages = 0)
        => new() { InfestationLevel = level, QtyKg = qty, PackageCount = packages };

    // ═══════════ ① عمود «يحتاج معالجة» ═══════════

    /// <summary>
    /// العلم مصدره بطاقة الأصناف وحدها — لا يُدخله الموظف في السند. والدليل التشغيلي:
    /// توجيه صنف غير معلَّم إلى مستودع المعالجة **يُرفض** برسالة تحيل إلى بطاقة الصنف.
    /// </summary>
    [Fact]
    public void P1_Destination_Treatment_Rejected_When_Product_Not_Flagged()
    {
        var (host, db, rec, raw) = Setup(requiresTreatment: false);
        using (host)
        {
            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01",
                new List<ShipmentItemDto> { Item(raw, 100, ReceiptDestinations.Treatment) });

            Assert.False(r.Ok);
            Assert.Contains("يحتاج معالجة", r.Message);
            Assert.Empty(db.Shipments.ToList());
        }
    }

    /// <summary>والصنف المعلَّم يُقبل توجيهه — العلم شرط تمكين لا حجب.</summary>
    [Fact]
    public void P1b_Flagged_Product_Accepts_Treatment_Destination()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01",
                new List<ShipmentItemDto> { Item(raw, 100, ReceiptDestinations.Treatment) });

            Assert.True(r.Ok, r.Message);
            Assert.True(db.Products.Single(p => p.Id == raw.Id).RequiresTreatment);
        }
    }

    // ═══════════ ② عمود الوجهة ═══════════

    /// <summary>
    /// الوجهة تُحفظ **لكل بند** لا لكل سند: الحاوية الواحدة تحمل صنفاً سليماً وآخر مصاباً.
    /// وفارغُها = مخزن الخام، فكل السندات القائمة تبقى على سلوكها حرفياً.
    /// </summary>
    [Fact]
    public void P2_Destination_Is_Per_Item_And_Defaults_To_Raw_Store()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            var other = db.Products.First(p => p.ProductCode != "001-001" && p.ItemType == "Raw" && p.IsActive);
            other.RequiresTreatment = false;
            db.SaveChanges();

            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
            {
                Item(raw, 100, ReceiptDestinations.Treatment),
                new() { ProductId = other.Id, PackagingTypeId = 3, PackageCount = 50,
                        UnitWeightKg = BasketKg, QtyKg = 50 * BasketKg, ReceiptUnit = "سلة" } // بلا وجهة إطلاقاً
            });
            Assert.True(r.Ok, r.Message);

            var items = db.ShipmentItems.AsNoTracking().Where(i => i.ShipmentId == r.Id).OrderBy(i => i.Id).ToList();
            Assert.Equal(ReceiptDestinations.Treatment, items[0].Destination);
            Assert.Equal(ReceiptDestinations.RawStore, items[1].Destination);   // §الافتراضي بلا تدخل
        }
    }

    // ═══════════ ③ التقسيم بدرجات إصابة — بلا صنف جديد ═══════════

    /// <summary>
    /// سيناريو المستخدم حرفياً: بند واحد 5,000 سلة ← 4,000 خفيفة + 500 متوسطة + 500 شديدة.
    /// **الادعاء المركزي:** ثلاث عمليات معالجة على **دفعة واحدة** و**صنف واحد**،
    /// بمُدد 120/168/240 ساعة — ولا صنف جديد يُنشأ في بطاقة الأصناف.
    /// </summary>
    [Fact]
    public void P3_One_Item_Splits_Into_Three_Infestation_Levels_Without_New_Product()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            int productsBefore = db.Products.Count();

            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
            {
                Item(raw, 5000, ReceiptDestinations.Treatment,
                    Part(InfestationLevels.Light, 4000 * BasketKg, 4000),
                    Part(InfestationLevels.Medium, 500 * BasketKg, 500),
                    Part(InfestationLevels.High, 500 * BasketKg, 500))
            });
            Assert.True(r.Ok, r.Message);
            Assert.True(rec.ApproveShipment(r.Id).Ok);

            // لا صنف جديد — الصنف يُنسخ من الدفعة كما هو
            Assert.Equal(productsBefore, db.Products.Count());

            var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == r.Id);
            var trts = db.RawTreatments.AsNoTracking().Where(t => t.LotId == lot.Id)
                .OrderBy(t => t.DurationHours).ToList();

            Assert.Equal(3, trts.Count);
            Assert.All(trts, t => Assert.Equal(raw.Id, t.ProductId));   // صنف واحد
            Assert.All(trts, t => Assert.Equal(lot.Id, t.LotId));       // دفعة واحدة
            Assert.Equal(new[] { 120d, 168d, 240d }, trts.Select(t => t.DurationHours));
            Assert.Equal(new[] { 80000d, 10000d, 10000d }, trts.Select(t => t.QtyKg));
            Assert.Equal(new[] { 4000, 500, 500 }, trts.Select(t => t.PackageCount));

            // موعد الجاهزية محسوب ومخزَّن = البدء + المدة
            foreach (var t in trts)
                Assert.Equal(t.StartedAt.AddHours(t.DurationHours), t.ExpectedReadyAt);
        }
    }

    /// <summary>مجموع الأجزاء يجب أن يساوي كمية البند — الفرق كميةٌ لا يعرف النظام أين تذهب.</summary>
    [Fact]
    public void P3b_Parts_Must_Sum_To_Item_Quantity()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
            {
                Item(raw, 1000, ReceiptDestinations.Treatment,
                    Part(InfestationLevels.Light, 10000),
                    Part(InfestationLevels.High, 5000))   // المجموع 15,000 والبند 20,000
            });

            Assert.False(r.Ok);
            Assert.Contains("لا يساوي كمية البند", r.Message);
        }
    }

    /// <summary>وجهة المعالجة بلا تقسيم = جزء واحد ضمني بكامل الكمية (7 أيام) — لا إجبار على التقسيم.</summary>
    [Fact]
    public void P3c_Treatment_Without_Parts_Defaults_To_Single_Medium_Part()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01",
                new List<ShipmentItemDto> { Item(raw, 200, ReceiptDestinations.Treatment) });
            Assert.True(r.Ok, r.Message);
            Assert.True(rec.ApproveShipment(r.Id).Ok);

            var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == r.Id);
            var t = db.RawTreatments.AsNoTracking().Single(x => x.LotId == lot.Id);
            Assert.Equal(168d, t.DurationHours);          // متوسطة — 7 أيام
            Assert.Equal(200 * BasketKg, t.QtyKg, 1);
        }
    }

    // ═══════════ ④ البدء التلقائي عند الاعتماد ═══════════

    /// <summary>
    /// الاعتماد وحده يكفي: تُنشأ الدفعة، ويُقيَّد الوارد في الخام، ثم ينتقل إلى WTRT.
    /// **وثابت التوازن يبقى صحيحاً:** رصيد(WRM) + رصيد(WTRT) = InStockQtyKg.
    /// </summary>
    [Fact]
    public void P4_Approval_Starts_Treatment_Automatically_And_Balances_Hold()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
            {
                Item(raw, 1000, ReceiptDestinations.Treatment,
                    Part(InfestationLevels.Medium, 600 * BasketKg, 600),
                    Part(InfestationLevels.High, 400 * BasketKg, 400))
            });
            Assert.True(r.Ok, r.Message);

            // قبل الاعتماد: لا معالجة إطلاقاً — الاعتماد هو المُطلِق
            Assert.Empty(db.RawTreatments.AsNoTracking().ToList());

            var ap = rec.ApproveShipment(r.Id);
            Assert.True(ap.Ok, ap.Message);
            Assert.Contains("بدأت", ap.Message);

            var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == r.Id);
            Assert.Equal(20000, lot.InStockQtyKg, 1);            // لم تغادر المنشأة
            Assert.Equal(20000, lot.UnderTreatmentQtyKg, 1);     // كلها تحت المعالجة
            Assert.Equal(0, lot.AvailableQtyKg, 1);              // ⇦ غير متاحة للإنتاج

            int whRaw = db.Warehouses.Single(w => w.WarehouseCode == "WRM").Id;
            int whTrt = db.Warehouses.Single(w => w.WarehouseCode == "WTRT").Id;
            double bRaw = db.StockBalances.AsNoTracking()
                .Where(b => b.WarehouseId == whRaw && b.LotId == lot.Id).Sum(b => b.QtyKg);
            double bTrt = db.StockBalances.AsNoTracking()
                .Where(b => b.WarehouseId == whTrt && b.LotId == lot.Id).Sum(b => b.QtyKg);

            Assert.Equal(0, bRaw, 1);
            Assert.Equal(20000, bTrt, 1);
            Assert.Equal(lot.InStockQtyKg, bRaw + bTrt, 1);       // ثابت التوازن
        }
    }

    /// <summary>الوجهة «مخزن الخام» لا تُنشئ معالجةً إطلاقاً — السلوك القائم لا يتغير بحرف.</summary>
    [Fact]
    public void P4b_Raw_Store_Destination_Starts_No_Treatment()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01",
                new List<ShipmentItemDto> { Item(raw, 300, ReceiptDestinations.RawStore) });
            Assert.True(r.Ok, r.Message);
            Assert.True(rec.ApproveShipment(r.Id).Ok);

            var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == r.Id);
            Assert.Empty(db.RawTreatments.AsNoTracking().Where(t => t.LotId == lot.Id).ToList());
            Assert.Equal(0, lot.UnderTreatmentQtyKg, 1);
            Assert.Equal(6000, lot.AvailableQtyKg, 1);

            int whRaw = db.Warehouses.Single(w => w.WarehouseCode == "WRM").Id;
            Assert.Equal(6000, db.StockBalances.AsNoTracking()
                .Where(b => b.WarehouseId == whRaw && b.LotId == lot.Id).Sum(b => b.QtyKg), 1);
        }
    }

    /// <summary>
    /// الإفراج بعد اكتمال المدة يعمل على المعالجة التي بدأها الاستلام تماماً كما لو بدأتها
    /// الشاشة المستقلة — فالمسار الجديد يُنتج بيانات يفهمها المحرك القائم بلا استثناء.
    /// </summary>
    [Fact]
    public void P5_Auto_Started_Treatment_Releases_Through_Existing_Engine()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01",
                new List<ShipmentItemDto> { Item(raw, 500, ReceiptDestinations.Treatment) });
            Assert.True(rec.ApproveShipment(r.Id).Ok);

            var lot = db.Lots.Single(l => l.ShipmentId == r.Id);
            var t = db.RawTreatments.Single(x => x.LotId == lot.Id);
            var trt = host.Get<IRawTreatmentService>();

            // الحارس الزمني قائم: لا إفراج قبل اكتمال المدة
            Assert.False(trt.Release(t.Id, 1000).Ok);

            // نُقدّم الزمن (لا مؤقّت خلفي — الإفراج فعل بشري)
            t.StartedAt = t.StartedAt.AddDays(-8);
            t.ExpectedReadyAt = t.ExpectedReadyAt.AddDays(-8);
            db.SaveChanges();

            // نوع «إصابة متوسطة» يشترط فحص جودة معتمداً — نسجّله كما تفعل دورة الجودة
            var qc = new QualityCheck { DocumentNumber = "QC-B107", Decision = "Passed", Status = "Approved", CheckDate = DateTime.Now };
            db.QualityChecks.Add(qc);
            db.SaveChanges();
            db.QualityCheckItems.Add(new QualityCheckItem { CheckId = qc.Id, LotId = lot.Id });
            db.SaveChanges();

            var rel = trt.Release(t.Id, 4000);   // إفراج جزئي
            Assert.True(rel.Ok, rel.Message);

            db.ChangeTracker.Clear();
            var after = db.Lots.AsNoTracking().Single(l => l.Id == lot.Id);
            Assert.Equal(4000, after.TreatmentReadyQtyKg, 1);
            Assert.Equal(6000, after.UnderTreatmentQtyKg, 1);
            Assert.Equal(4000, after.AvailableQtyKg, 1);
        }
    }

    /// <summary>
    /// إلغاء اعتماد سند دخلت دفعته المعالجة **يُرفض**: الكمية غادرت مخزن الخام إلى WTRT،
    /// وعكسُها هنا كان سيخصم رصيداً غير موجود ويكسر ثابت التوازن صامتاً.
    /// </summary>
    [Fact]
    public void P6_Unapprove_Blocked_While_Lot_Is_In_Treatment()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01",
                new List<ShipmentItemDto> { Item(raw, 300, ReceiptDestinations.Treatment) });
            Assert.True(rec.ApproveShipment(r.Id).Ok);

            var un = rec.UnapproveShipment(r.Id);
            Assert.False(un.Ok);
            Assert.Contains("المعالجة", un.Message);

            // ولا شيء تغيّر: الدفعة والمعالجة باقيتان كما هما
            Assert.Single(db.Lots.AsNoTracking().Where(l => l.ShipmentId == r.Id).ToList());
        }
    }

    /// <summary>تعديل سند مسودة يستبدل الأجزاء ولا يُراكمها — لا صفوف يتيمة بعد إعادة الحفظ.</summary>
    [Fact]
    public void P7_Editing_Draft_Replaces_Parts_Without_Orphans()
    {
        var (host, db, rec, raw) = Setup();
        using (host)
        {
            var r1 = rec.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
            {
                Item(raw, 1000, ReceiptDestinations.Treatment,
                    Part(InfestationLevels.Light, 10000), Part(InfestationLevels.High, 10000))
            });
            Assert.True(r1.Ok, r1.Message);
            Assert.Equal(2, db.ShipmentItemTreatmentParts.AsNoTracking().Count());

            var r2 = rec.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
            {
                Item(raw, 1000, ReceiptDestinations.Treatment, Part(InfestationLevels.Medium, 20000))
            }, existingId: r1.Id);
            Assert.True(r2.Ok, r2.Message);

            db.ChangeTracker.Clear();
            var parts = db.ShipmentItemTreatmentParts.AsNoTracking().ToList();
            Assert.Single(parts);
            Assert.Equal(InfestationLevels.Medium, parts[0].InfestationLevel);
            var itemIds = db.ShipmentItems.AsNoTracking().Select(i => i.Id).ToList();
            Assert.All(parts, p => Assert.Contains(p.ShipmentItemId, itemIds));
        }
    }

    /// <summary>الترحيل الآمن ينشئ جدول الأجزاء وعمود الوجهة في القواعد القائمة — بلا حذف بيانات.</summary>
    [Fact]
    public void P8_Schema_Migrator_Provides_New_Table_And_Column()
    {
        var (host, db, _, _) = Setup();
        using (host)
        {
            var report = SchemaMigrator.Migrate(db);
            Assert.DoesNotContain(report, m => m.StartsWith("خطأ"));

            // الجدول والعمود متاحان فعلياً للاستعلام
            Assert.Empty(db.ShipmentItemTreatmentParts.AsNoTracking().ToList());
            Assert.Empty(db.ShipmentItems.AsNoTracking().Where(i => i.Destination == "WTRT").ToList());
        }
    }
}
