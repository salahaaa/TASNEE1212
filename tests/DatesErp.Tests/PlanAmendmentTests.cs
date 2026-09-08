using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §تعديلات العملاء أثناء التنفيذ — اختبارات مسار (طلب تعديل → اعتماد → إصدار جديد):
/// الخطة الأصلية لا تُحذف، المنفذ محمي، المتبقي يُوقَف، إصدار جديد بالصنف الجديد،
/// وفحص الطاقة قبل الاعتماد (رفض إن تجاوز).
/// </summary>
public class PlanAmendmentTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    private static int SeedLot(TestHost host, double qtyKg)
    {
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto>
        { new() { ProductId = 1, PackageCount = (int)(qtyKg / 20), UnitWeightKg = 20, QtyKg = qtyKg } });
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        using var db = FreshDb(host);
        return db.Lots.OrderByDescending(l => l.Id).First().Id;
    }

    /// <summary>صنف تام بديل من نفس خام الخلاص (source=1) — حتى يُقبل التحويل الرسمي.</summary>
    private static int SeedProductY(TestHost host, double cartonWeightKg = 7.5)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.MasterDataService>();
        var created = master.SaveProductFull(null, "002-099", "خلاص بديل 500جم", "002", "Finished", "كرتون",
            cartonWeightKg, 1, 0.5, new List<(int, int?, int)>(), sourceProductId: 1);
        Assert.True(created.Ok, created.Message);
        return created.Id;
    }

    /// <summary>خطة معتمدة ببند واحد (الصنف 3) — تُعيد (planId, itemId).</summary>
    private static (int planId, int itemId) SeedApprovedPlan(TestHost host, int lotId, double qtyKg, int cartons)
    {
        var planning = Svc<IPlanningService>(host);
        var plan = planning.SavePlan("خطة للاختبار", "Daily", "2026-11-01", "2026-11-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                        PlannedQtyKg = qtyKg, PlannedCartons = cartons,
                        ScheduledDate = "2026-11-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        int itemId;
        using (var db = FreshDb(host))
            itemId = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).Id;
        return (plan.Id, itemId);
    }

    // ═══════════════ 1) الطلب يوثّق قديم/منفذ/متبقي/جديد ═══════════════

    [Fact]
    public void RequestAmendment_Records_Old_Executed_Remaining_And_New_Qty()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 100000);
        int productY = SeedProductY(host);
        var (planId, itemId) = SeedApprovedPlan(host, lotId, 1500, 200); // المخطط 1500 كجم

        // §حماية المنفذ: 600 كجم أُنتجت فعلياً قبل التعديل
        using (var db = FreshDb(host))
        {
            var item = db.ProductionPlanItems.Single(i => i.Id == itemId);
            item.ProducedQtyKg = 600;
            db.SaveChanges();
        }

        var svc = Svc<IPlanAmendmentService>(host);
        var r = svc.RequestAmendment(new PlanAmendmentRequestDto
        {
            OriginalPlanId = planId, PlanItemId = itemId, CustomerId = 1,
            NewProductId = productY, Reason = "توقف الصنف X بأمر العميل والتحويل إلى Y"
        });
        Assert.True(r.Ok, r.Message);

        using var db = FreshDb(host);
        var am = db.PlanAmendments.Single(a => a.Id == r.Id);
        Assert.Equal(1500, am.OldQtyKg);
        Assert.Equal(600, am.ExecutedQtyKg);
        Assert.Equal(900, am.RemainingQtyKg);
        Assert.Equal(900, am.NewQtyKg);            // الافتراضي = المتبقي
        Assert.Equal(3, am.OldProductId);
        Assert.Equal(productY, am.NewProductId);
        Assert.Equal("Partial", am.ExecutionState);
        Assert.Equal(planId, am.OriginalPlanId);
        Assert.Equal(DocStatuses.Draft, am.Status);

        // الخطة الأصلية لم تُمَسّ: البند ما زال غير مقفل ومنتَجه 600
        var item = db.ProductionPlanItems.Single(i => i.Id == itemId);
        Assert.False(item.IsClosed);
        Assert.Equal(600, item.ProducedQtyKg);
        Assert.Equal(1500, item.PlannedQtyKg);
    }

    // ═══════════════ 2) الاعتماد: إصدار جديد + حفظ الأصل + حماية المنفذ ═══════════════

    [Fact]
    public void ApproveAmendment_Creates_Revision_Preserves_Original_Protects_Produced()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 100000);
        int productY = SeedProductY(host);
        var (planId, itemId) = SeedApprovedPlan(host, lotId, 1500, 200);

        using (var db = FreshDb(host))
        {
            var item = db.ProductionPlanItems.Single(i => i.Id == itemId);
            item.ProducedQtyKg = 600;
            db.SaveChanges();
        }

        var svc = Svc<IPlanAmendmentService>(host);
        var req = svc.RequestAmendment(new PlanAmendmentRequestDto
        {
            OriginalPlanId = planId, PlanItemId = itemId, CustomerId = 1,
            NewProductId = productY, Reason = "توقف الصنف X بأمر العميل والتحويل إلى Y"
        });
        Assert.True(req.Ok, req.Message);

        var appr = svc.ApproveAmendment(req.Id);
        Assert.True(appr.Ok, appr.Message);

        using var db = FreshDb(host);
        // 1) الخطة الأصلية باقية وغير محذوفة
        var original = db.ProductionPlans.Single(p => p.Id == planId);
        Assert.NotNull(original);
        Assert.Equal(1, original.RevisionNo);

        // 2) البند الأصلي أُغلق، والمنفذ محمي، والمتبقي مُحرَّر (لا يُعاد للخطة)
        var origItem = db.ProductionPlanItems.Single(i => i.Id == itemId);
        Assert.True(origItem.IsClosed);
        Assert.Equal(600, origItem.ProducedQtyKg);      // المنفذ لم يُمَس
        Assert.Equal(1500, origItem.PlannedQtyKg);      // المخطط الأصلي محفوظ
        Assert.Equal(900, origItem.ReleasedQtyKg);      // المتبقي المُوقَف

        // 3) سجل التعديل معتمد ويشير للإصدار الجديد
        var am = db.PlanAmendments.Single(a => a.Id == req.Id);
        Assert.Equal(DocStatuses.Approved, am.Status);
        Assert.True(am.IsApproved);
        Assert.NotNull(am.NewRevisionPlanId);
        Assert.Equal(600, am.ExecutedQtyKg);
        Assert.Equal(900, am.RemainingQtyKg);

        // 4) الإصدار الجديد (Revision 2) بالصنف الجديد والكمية المعتمدة
        var rev = db.ProductionPlans.Single(p => p.Id == am.NewRevisionPlanId);
        Assert.Equal(2, rev.RevisionNo);
        Assert.Equal(planId, rev.ParentPlanId);
        Assert.True(rev.IsApproved);
        var revItem = db.ProductionPlanItems.Single(i => i.PlanId == rev.Id);
        Assert.Equal(productY, revItem.ProductId);
        Assert.Equal(900, revItem.PlannedQtyKg);
    }

    // ═══════════════ 3) رفض التعديل إن تجاوز الطاقة ═══════════════

    [Fact]
    public void ApproveAmendment_Rejects_When_Capacity_Exceeded()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 100000);
        int productY = SeedProductY(host);
        var (planId, itemId) = SeedApprovedPlan(host, lotId, 1500, 200);

        // طاقة ضيقة للصنف الجديد في الوردية 1 (10 كرتون) — المتبقي 900 كجم = 120 كرتون لن تتسع
        var cap = Svc<ICapacityService>(host);
        Assert.True(cap.SetCapacity(productY, 1, 10).Ok);

        var svc = Svc<IPlanAmendmentService>(host);
        var req = svc.RequestAmendment(new PlanAmendmentRequestDto
        {
            OriginalPlanId = planId, PlanItemId = itemId, CustomerId = 1,
            NewProductId = productY, Reason = "توقف الصنف X بأمر العميل والتحويل إلى Y"
        });
        Assert.True(req.Ok, req.Message);

        var appr = svc.ApproveAmendment(req.Id);
        Assert.False(appr.Ok);                        // يجب رفض الاعتماد
        Assert.Contains("الطاقة", appr.Message);

        // لا إصدار جديد، والطلب ما زال مسودة، والبند الأصلي لم يُقفل
        using var db = FreshDb(host);
        Assert.Equal(1, db.ProductionPlans.Count(p => p.Id == planId || p.ParentPlanId == planId));
        var am = db.PlanAmendments.Single(a => a.Id == req.Id);
        Assert.Equal(DocStatuses.Draft, am.Status);
        var item = db.ProductionPlanItems.Single(i => i.Id == itemId);
        Assert.False(item.IsClosed);
    }

    // ═══════════════ 4) سجل التعديلات الكامل ═══════════════

    [Fact]
    public void GetPlanHistory_Returns_Amendment_Chain()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 100000);
        int productY = SeedProductY(host);
        var (planId, itemId) = SeedApprovedPlan(host, lotId, 1500, 200);

        var svc = Svc<IPlanAmendmentService>(host);
        var req = svc.RequestAmendment(new PlanAmendmentRequestDto
        {
            OriginalPlanId = planId, PlanItemId = itemId, CustomerId = 1,
            NewProductId = productY, Reason = "توقف الصنف X بأمر العميل والتحويل إلى Y"
        });
        Assert.True(req.Ok, req.Message);

        var history = svc.GetPlanHistory(planId);
        var dto = Assert.Single(history);
        Assert.Equal(req.DocumentNumber, dto.DocumentNumber);
        Assert.Equal("مسودة", dto.StatusAr);
        Assert.Equal(1500, dto.OldQtyKg);      // المخطط الأصلي
        Assert.Equal(0, dto.ExecutedQtyKg);    // لم يُنتج شيء بعد
        Assert.Equal(1500, dto.RemainingQtyKg); // المتبقي = المخطط كاملاً
        Assert.Equal("خلاص ممتاز 500جم", dto.OldProductName);
    }
}
