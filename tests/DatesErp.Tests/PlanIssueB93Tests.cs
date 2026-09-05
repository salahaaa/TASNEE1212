using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B93 — ترحيل الخطة المعتمدة إلى أوامر: تجميع (تاريخ×وردية×خط)، المتبقي فقط،
/// رفض غير المعتمدة، فلترة الفترة، ومجموعات متعددة العملاء.
/// </summary>
public class PlanIssueB93Tests
{
    private static (TestHost host, int custA, int custB) Seed2()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        db.Customers.Add(new Customer { CustomerCode = "PI-A", CustomerName = "عميل ترحيل أ", IsActive = true });
        db.Customers.Add(new Customer { CustomerCode = "PI-B", CustomerName = "عميل ترحيل ب", IsActive = true });
        db.SaveChanges();
        return (host, db.Customers.Single(c => c.CustomerCode == "PI-A").Id,
            db.Customers.Single(c => c.CustomerCode == "PI-B").Id);
    }

    private static int SaveApprovedPlan(TestHost host, List<PlanItemDto> items)
    {
        using var scope = host.Services.CreateScope();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var r = plan.SavePlan("خطة ترحيل", "Period", "2026-09-01", "2026-09-06", 1, 1, items);
        Assert.True(r.Ok);
        Assert.True(plan.ApprovePlan(r.Id).Ok);
        return r.Id;
    }

    private static IProductionOrderService Orders(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IProductionOrderService>();

    [Fact]
    public void Issue_Groups_By_Date_Shift_And_Links_Items()
    {
        using var (host, a, b) = Seed2();
        int planId = SaveApprovedPlan(host, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            new() { CustomerId = a, ProductId = 4, PlannedCartons = 300, PlannedQtyKg = 600, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            new() { CustomerId = b, ProductId = 3, PlannedCartons = 100, PlannedQtyKg = 750, ScheduledDate = "2026-09-03", SuggestedShiftId = 2 },
        });

        var r = Orders(host).IssueOrdersFromPlan(planId);
        Assert.True(r.Ok);
        Assert.Equal(2, r.Created.Count);
        Assert.Equal(3, r.Created.Sum(o => o.ItemsCount));
        Assert.Equal(2850, r.Created.Sum(o => o.TotalKg));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var orderItems = db.ProductionOrderItems.Where(i => i.PlanItemId != null).ToList();
        Assert.Equal(3, orderItems.Count(i => db.ProductionOrders.Any(o => o.Id == i.OrderId && o.SourcePlanId == planId)));

        // الترحيل الثاني: لا متبقي — يُتخطى الكل
        var r2 = Orders(host).IssueOrdersFromPlan(planId);
        Assert.False(r2.Ok);
        Assert.Empty(r2.Created);
        Assert.Contains(r2.Skipped, s => s.Contains("بلا متبقي"));
    }

    [Fact]
    public void Unapproved_Plan_Rejected()
    {
        using var (host, a, b) = Seed2();
        int planId;
        using (var scope = host.Services.CreateScope())
        {
            var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var r = plan.SavePlan("خطة مسودة", "Period", "2026-09-01", "2026-09-06", 1, 1, new List<PlanItemDto>
            {
                new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            });
            Assert.True(r.Ok);
            planId = r.Id;
        }
        var res = Orders(host).IssueOrdersFromPlan(planId);
        Assert.False(res.Ok);
        Assert.Contains("غير معتمدة", res.Message);
    }

    [Fact]
    public void Date_Filter_Limits_Groups()
    {
        using var (host, a, b) = Seed2();
        int planId = SaveApprovedPlan(host, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            new() { CustomerId = b, ProductId = 3, PlannedCartons = 100, PlannedQtyKg = 750, ScheduledDate = "2026-09-03", SuggestedShiftId = 1 },
        });

        var r = Orders(host).IssueOrdersFromPlan(planId, "2026-09-01", "2026-09-01");
        Assert.True(r.Ok);
        Assert.Single(r.Created);
        Assert.Equal("01/09/2026", r.Created[0].ProductionDate);
        Assert.Contains(r.Skipped, s => s.Contains("خارج الفترة"));
    }

    [Fact]
    public void MixedCustomer_Group_Header_Null_Items_Keep_Customers()
    {
        using var (host, a, b) = Seed2();
        int planId = SaveApprovedPlan(host, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-02", SuggestedShiftId = 1 },
            new() { CustomerId = b, ProductId = 4, PlannedCartons = 300, PlannedQtyKg = 600, ScheduledDate = "2026-09-02", SuggestedShiftId = 1 },
        });

        var r = Orders(host).IssueOrdersFromPlan(planId);
        Assert.True(r.Ok);
        Assert.Single(r.Created);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var order = db.ProductionOrders.Single(o => o.Id == r.Created[0].OrderId);
        Assert.Null(order.CustomerId);
        var custs = db.ProductionOrderItems.Where(i => i.OrderId == order.Id).Select(i => i.CustomerId).ToList();
        Assert.Contains(a, custs);
        Assert.Contains(b, custs);
    }
}
