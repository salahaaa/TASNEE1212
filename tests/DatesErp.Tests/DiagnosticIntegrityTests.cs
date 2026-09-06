using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §الفحص الذاتي — الطبقتان الجديدتان: الإعداد التشغيلي واتساق البيانات.
///
/// كل فحص هنا مُختبَر **في الاتجاهين**: يمرّ على قاعدة سليمة، **ويسقط فعلاً**
/// حين يُفسَد الشيء الذي يزعم حراسته. الاختبار الإيجابي وحده لا يُثبت أن الفحص
/// يعمل — قد يكون يُرجع «ناجح» دائماً، وهو أسوأ من عدم وجوده لأنه يمنح طمأنينة كاذبة.
/// </summary>
public class DiagnosticIntegrityTests
{
    private static DatesErpDbContext Db(TestHost host) =>
        host.Services.CreateScope().ServiceProvider.GetRequiredService<DatesErpDbContext>();

    /// <summary>
    /// §DbSeeder لا يبذر كتالوج الصلاحيات — يفعل ذلك EnsureCatalog() عند إقلاع سطح المكتب
    /// (Bootstrapper.cs:159). فتهيئة الاختبار تستدعيه كي تحاكي قاعدة حيّة فعلاً،
    /// وإلا فُحصت حالة لا توجد عند أي مستخدم.
    /// </summary>
    private static DatesErpDbContext Ready(TestHost host)
    {
        var db = Db(host);
        new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>()).EnsureCatalog();
        return db;
    }

    private static DiagnosticCore.Finding Find(List<DiagnosticCore.Finding> f, string part) =>
        f.FirstOrDefault(x => x.Name.Contains(part));

    // ═══════════════════ الإعداد التشغيلي ═══════════════════

    [Fact]
    public void Operational_All_Pass_On_Seeded_Db()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var findings = DiagnosticCore.CheckOperational(Ready(host));

        var failed = findings.Where(f => !f.Ok).Select(f => $"{f.Name}: {f.Detail}").ToList();
        Assert.True(failed.Count == 0, "فحوصات تشغيلية فشلت على قاعدة مبذورة سليمة:\n  - "
            + string.Join("\n  - ", failed));
    }

    /// <summary>الأربعة المطلوبة بالاسم في الكود — WTRT أُضيف لدورة المعالجة.</summary>
    [Fact]
    public void Operational_Checks_All_Four_Warehouses()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var f = DiagnosticCore.CheckOperational(Ready(host));
        foreach (var code in new[] { "WRM", "WFG", "WAUX", "WTRT" })
            Assert.True(Find(f, code)?.Ok == true, $"المخزن {code} غير مفحوص أو فاشل");
    }

    /// <summary>
    /// اختبار سلبي: حذف مستودع المعالجة يجب أن يُسقط الفحص.
    /// هذا بالضبط سيناريو قاعدة مُرقّاة من إصدار سابق لدورة المعالجة.
    /// </summary>
    [Fact]
    public void Operational_Detects_Missing_Treatment_Warehouse()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Ready(host);

        var wtrt = db.Warehouses.First(w => w.WarehouseCode == "WTRT");
        db.Warehouses.Remove(wtrt);
        db.SaveChanges();

        var f = DiagnosticCore.CheckOperational(db);
        var finding = Find(f, "WTRT");
        Assert.NotNull(finding);
        Assert.False(finding.Ok);
        Assert.Contains("مفقود", finding.Detail);
    }

    /// <summary>اختبار سلبي: مخطط ترقيم ناقص = مستندات بلا أرقام.</summary>
    [Fact]
    public void Operational_Detects_Missing_Numbering_Scheme()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Ready(host);

        db.NumberingSchemes.Remove(db.NumberingSchemes.First(x => x.SchemeCode == "TRT"));
        db.SaveChanges();

        var finding = Find(DiagnosticCore.CheckOperational(db), "مخططات ترقيم");
        Assert.False(finding.Ok);
        Assert.Contains("TRT", finding.Detail);
    }

    /// <summary>اختبار سلبي: مورد صلاحية ناقص = أزرار تختفي بلا سبب ظاهر.</summary>
    [Fact]
    public void Operational_Detects_Missing_Permission_Resource()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Ready(host);

        var res = db.PermissionResources.FirstOrDefault(x => x.Code == "treatment");
        Assert.NotNull(res); // البذر يجب أن يكون قد أنشأه أصلاً
        db.PermissionResources.Remove(res);
        db.SaveChanges();

        var finding = Find(DiagnosticCore.CheckOperational(db), "موارد الصلاحيات");
        Assert.False(finding.Ok);
        Assert.Contains("treatment", finding.Detail);
    }

    // ═══════════════════ اتساق البيانات ═══════════════════

    [Fact]
    public void Integrity_All_Pass_On_Clean_Db()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var failed = DiagnosticCore.CheckDataIntegrity(Db(host))
            .Where(f => !f.Ok).Select(f => $"{f.Name}: {f.Detail}").ToList();
        Assert.True(failed.Count == 0, "فحوصات اتساق فشلت على قاعدة نظيفة:\n  - "
            + string.Join("\n  - ", failed));
    }

    /// <summary>دورة معالجة حقيقية كاملة يجب ألا تُنتج أي عدم اتساق.</summary>
    [Fact]
    public void Integrity_Holds_Through_Real_Treatment_Cycle()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Db(host);

        var raw = db.Products.First(p => p.ProductCode == "001-001");
        raw.RequiresTreatment = true;
        db.SaveChanges();

        var receiving = host.Get<IReceivingService>();
        var r = receiving.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
        {
            new() { ProductId = raw.Id, PackagingTypeId = 3, PackageCount = 5000,
                    UnitWeightKg = 20, QtyKg = 100000, ReceiptUnit = "سلة" }
        });
        Assert.True(r.Ok, r.Message);
        Assert.True(receiving.ApproveShipment(r.Id).Ok);

        var trt = host.Get<IRawTreatmentService>();
        var lot = db.Lots.OrderBy(l => l.Id).Last();

        // بدء معالجتين، ثم إفراج جزئي ورفض جزئي — أكثر المسارات عرضة للانحراف.
        var t1 = trt.Start(new TreatmentStartDto
        {
            LotId = lot.Id, QtyKg = 10000, PackageCount = 500,
            DurationHours = 7 * 24, StartedAt = DateTime.Now.AddDays(-8)
        });
        Assert.True(t1.Ok, t1.Message);

        var t2 = trt.Start(new TreatmentStartDto
        {
            LotId = lot.Id, QtyKg = 10000, PackageCount = 500,
            DurationHours = 10 * 24, StartedAt = DateTime.Now.AddDays(-11)
        });
        Assert.True(t2.Ok, t2.Message);

        Assert.True(trt.Release(t1.Id, 4000).Ok);
        Assert.True(trt.Reject(t2.Id, 2000, "تلف").Ok);

        var failed = DiagnosticCore.CheckDataIntegrity(Db(host))
            .Where(f => !f.Ok).Select(f => $"{f.Name}: {f.Detail}").ToList();
        Assert.True(failed.Count == 0,
            "دورة معالجة حقيقية أنتجت عدم اتساق:\n  - " + string.Join("\n  - ", failed));
    }

    /// <summary>اختبار سلبي: رصيد دفعة سالب — يعني حركة صرف تجاوزت حارس المنع.</summary>
    [Fact]
    public void Integrity_Detects_Negative_Lot_Balance()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Db(host);

        db.Lots.Add(new Lot { LotCode = "BAD-NEG", ProductId = 1, InStockQtyKg = -50 });
        db.SaveChanges();

        var finding = Find(DiagnosticCore.CheckDataIntegrity(db), "سالبة");
        Assert.False(finding.Ok);
        Assert.Contains("BAD-NEG", finding.Detail);
    }

    /// <summary>
    /// اختبار سلبي: المحجوز + تحت المعالجة يتجاوز المخزون.
    /// أثره أن AvailableQtyKg يصير صفراً فتبدو الدفعة «غير متاحة» وهي مليئة —
    /// عطل صامت يوجّه الاتهام إلى منطق التخطيط لا إلى البيانات.
    /// </summary>
    [Fact]
    public void Integrity_Detects_Commitments_Exceeding_Stock()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Db(host);

        db.Lots.Add(new Lot
        {
            LotCode = "BAD-OVER", ProductId = 1,
            InStockQtyKg = 1000, ReservedQtyKg = 800, UnderTreatmentQtyKg = 500
        });
        db.SaveChanges();

        var finding = Find(DiagnosticCore.CheckDataIntegrity(db), "ضمن المخزون");
        Assert.False(finding.Ok);
        Assert.Contains("BAD-OVER", finding.Detail);
    }

    /// <summary>
    /// اختبار سلبي: «تحت المعالجة» على الدفعة لا تفسّره عمليات معالجة جارية.
    /// يعني كمية محجوبة عن الإنتاج بلا سبب مسجَّل.
    /// </summary>
    [Fact]
    public void Integrity_Detects_UnderTreatment_Without_Matching_Treatments()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Db(host);

        db.Lots.Add(new Lot
        {
            LotCode = "BAD-GHOST", ProductId = 1,
            InStockQtyKg = 5000, UnderTreatmentQtyKg = 3000 // بلا أي RawTreatment
        });
        db.SaveChanges();

        var finding = Find(DiagnosticCore.CheckDataIntegrity(db), "يطابق عمليات المعالجة");
        Assert.False(finding.Ok);
        Assert.Contains("BAD-GHOST", finding.Detail);
    }

    /// <summary>اختبار سلبي: دفعة تشير إلى شحنة غير موجودة — التتبع مقطوع.</summary>
    [Fact]
    public void Integrity_Detects_Orphan_Lot()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Db(host);

        db.Lots.Add(new Lot { LotCode = "BAD-ORPHAN", ProductId = 1, ShipmentId = 999999 });
        db.SaveChanges();

        var finding = Find(DiagnosticCore.CheckDataIntegrity(db), "مرتبطة بشحنة قائمة");
        Assert.False(finding.Ok);
        Assert.Contains("BAD-ORPHAN", finding.Detail);
    }

    /// <summary>الفحوصات كلها للقراءة فقط: لا تُصلح ولا تحذف ولا تعدّل صفاً واحداً.</summary>
    [Fact]
    public void Diagnostics_Never_Mutate_Data()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Ready(host);

        int lots = db.Lots.Count(), users = db.Users.Count(),
            wh = db.Warehouses.Count(), res = db.PermissionResources.Count();

        DiagnosticCore.CheckOperational(db);
        DiagnosticCore.CheckDataIntegrity(db);
        DiagnosticCore.CheckSeedData(db);

        Assert.Equal(lots, db.Lots.Count());
        Assert.Equal(users, db.Users.Count());
        Assert.Equal(wh, db.Warehouses.Count());
        Assert.Equal(res, db.PermissionResources.Count());
    }

    /// <summary>كل فحص يحمل اسماً وتفصيلاً — تقرير بسطور فارغة لا يُرسل للمطوّر.</summary>
    [Fact]
    public void Every_Finding_Has_Name_And_Detail()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = Ready(host);
        var all = DiagnosticCore.CheckOperational(db)
            .Concat(DiagnosticCore.CheckDataIntegrity(db))
            .Concat(DiagnosticCore.CheckSeedData(db)).ToList();

        Assert.NotEmpty(all);
        foreach (var f in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Name));
            Assert.False(string.IsNullOrWhiteSpace(f.Detail));
        }
    }
}
