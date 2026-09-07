using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §شرط القبول النهائي لشاشة أوامر الإنتاج (الاختبارات الخمسة الإلزامية):
/// 1) المتبقي في الخطة بعد أمر جزئي — 2) لا خلط بين الخلاص والسكري —
/// 3) عدة عملاء في خطة واحدة وأوامرهم — 4) منع تجاوز الطاقة —
/// 5) التتبع الكامل من الأمر حتى الفحص والمخزون والتسليم.
/// </summary>
public class ProductionOrderScreenTests
{
    private sealed record Setup(int Customer, int RawSukkari, int RawKhalas, int FinSukkari, int FinKhalas);

    private static Setup SeedCompany(TestHost host, string code, string name)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var c = master.SaveCustomer(null, code, name, "جملة", "777", "-", true);
        Assert.True(c.Ok, c.Message);
        var rs = master.SaveProductFull(null, $"{code}-R1", "سكري", "001", "Raw", "كجم", 20, 0, 0, null);
        var rk = master.SaveProductFull(null, $"{code}-R2", "خلاص", "001", "Raw", "كجم", 20, 0, 0, null);
        Assert.True(rs.Ok, rs.Message); Assert.True(rk.Ok, rk.Message);
        var fs = master.SaveProductFull(null, $"{code}-F1", "سكري تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rs.Id);
        var fk = master.SaveProductFull(null, $"{code}-F2", "خلاص تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rk.Id);
        Assert.True(fs.Ok, fs.Message); Assert.True(fk.Ok, fk.Message);
        return new Setup(c.Id, rs.Id, rk.Id, fs.Id, fk.Id);
    }

    private static int Receive(TestHost host, int cust, int productId, double kg)
    {
        using var scope = host.Services.CreateScope();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var s = receiving.SaveShipment(cust, "2026-08-20", "2026-08-20", new List<ShipmentItemDto>
        { new() { ProductId = productId, QtyKg = kg, PackageCount = (int)(kg / 20), UnitWeightKg = 20 } });
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        return db.Lots.Where(l => l.ShipmentId == s.Id && l.ProductId == productId).Single().Id;
    }

    // ══════════ الاختبار 1: المتبقي في الخطة بعد أمر جزئي ══════════
    [Fact]
    public void Partial_Order_Leaves_Plan_Remaining_And_OverRemaining_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = SeedCompany(host, "T1", "شركة الاختبار الأول");
        int lot = Receive(host, s.Customer, s.RawSukkari, 10000);

        int planId;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var plan = planning.SavePlan("خطة سكري 10000", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari, PlannedQtyKg = 10000, PriorityNo = 1 } });
            Assert.True(plan.Ok, plan.Message);
            planId = plan.Id;
            Assert.True(planning.ApprovePlan(planId).Ok);
        }

        using (var scope = host.Services.CreateScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var items = orders.GetOrderableItems(planId);
            Assert.Single(items);
            Assert.Equal(10000, items[0].RemainingKg, 1);
            Assert.Equal("سكري", items[0].RawName);
            Assert.Equal("سكري تام", items[0].ProductName);

            // أمر 4,000
            var o1 = orders.SaveOrder("FromPlan", planId, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
            { new() { PlanItemId = items[0].PlanItemId, LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari, PlannedQtyKg = 4000 } });
            Assert.True(o1.Ok, o1.Message);

            // المتبقي أصبح 6,000
            items = orders.GetOrderableItems(planId);
            Assert.Equal(6000, items[0].RemainingKg, 1);
            Assert.Equal(4000, items[0].OrderedKg, 1);

            // محاولة أمر 7,000 ← «الكمية المطلوبة تتجاوز الكمية المتبقية في خطة الإنتاج»
            var bad = orders.SaveOrder("FromPlan", planId, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
            { new() { PlanItemId = items[0].PlanItemId, LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari, PlannedQtyKg = 7000 } });
            Assert.False(bad.Ok);
            Assert.Contains("تتجاوز الكمية المتبقية", bad.Message);

            // أمر بالمتبقي كاملاً 6,000 ← ينجح (التوزيع على أوامر متعددة من نفس البند)
            var o2 = orders.SaveOrder("FromPlan", planId, s.Customer, "2026-09-02", 1, 1, new List<OrderItemDto>
            { new() { PlanItemId = items[0].PlanItemId, LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari, PlannedQtyKg = 6000 } });
            Assert.True(o2.Ok, o2.Message);
            Assert.Equal(0, orders.GetOrderableItems(planId)[0].RemainingKg, 1);
        }
    }

    // ══════════ الاختبار 2: الخلاص لا يختلط مع السكري ══════════
    [Fact]
    public void Khalas_Is_Never_Mixed_With_Sukkari_In_Orders()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = SeedCompany(host, "T2", "شركة الاختبار الثاني");
        int lotSuk = Receive(host, s.Customer, s.RawSukkari, 8000);
        int lotKha = Receive(host, s.Customer, s.RawKhalas, 8000);

        int planId;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var plan = planning.SavePlan("خطة الصنفين", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotSuk, CustomerId = s.Customer, ProductId = s.FinSukkari, PlannedQtyKg = 5000, PriorityNo = 1 },
                new() { SourceType = "FromReceiving", LotId = lotKha, CustomerId = s.Customer, ProductId = s.FinKhalas, PlannedQtyKg = 4000, PriorityNo = 2 }
            });
            Assert.True(plan.Ok, plan.Message);
            planId = plan.Id;
            Assert.True(planning.ApprovePlan(planId).Ok);
        }

        using var scope2 = host.Services.CreateScope();
        var orders = scope2.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var db = scope2.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = orders.GetOrderableItems(planId);
        var khaItem = items.Single(i => i.ProductName == "خلاص تام");
        var sukItem = items.Single(i => i.ProductName == "سكري تام");
        Assert.Equal("خلاص", khaItem.RawName);
        Assert.Equal("سكري", sukItem.RawName);

        // محاولة إنتاج خلاص تام من دفعة السكري ← مرفوض في الـ Backend
        var wrong = orders.SaveOrder("FromPlan", planId, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = khaItem.PlanItemId, LotId = lotSuk, CustomerId = s.Customer, ProductId = s.FinKhalas, PlannedQtyKg = 1000 } });
        Assert.False(wrong.Ok);
        Assert.Contains("تحويل", wrong.Message);

        // الأمر الصحيح: خلاص تام من دفعة الخلاص — الهوية محفوظة في البطاقة
        var ok = orders.SaveOrder("FromPlan", planId, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = khaItem.PlanItemId, LotId = lotKha, CustomerId = s.Customer, ProductId = s.FinKhalas, PlannedQtyKg = 4000 } });
        Assert.True(ok.Ok, ok.Message);
        var card = orders.GetOrderCard(ok.Id);
        Assert.Equal("خلاص", card.RawName);
        Assert.Equal("خلاص تام", card.ProductName);
        Assert.DoesNotContain("سكري", card.ProductName);
        Assert.DoesNotContain("تمور", card.ProductName);
    }

    // ══════════ الاختبار 3: عدة عملاء — كل أمر يحتفظ بعميله وصنفه ══════════
    [Fact]
    public void Multi_Customer_Orders_Keep_Customer_And_Product_Identity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var a = SeedCompany(host, "TA3", "العميل A");
        var b = SeedCompany(host, "TB3", "العميل B");

        // كل عميل يستلم خامه الخاص (سلالات مستقلة)
        var masterA_finSuk = a.FinSukkari; var masterB_finSuk = b.FinSukkari;
        int lotASuk = Receive(host, a.Customer, a.RawSukkari, 5000);
        int lotAKha = Receive(host, a.Customer, a.RawKhalas, 4000);
        int lotBSuk = Receive(host, b.Customer, b.RawSukkari, 6000);

        int planId;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var plan = planning.SavePlan("خطة عميلين", "Period", "2026-09-01", "2026-09-03", 1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotASuk, CustomerId = a.Customer, ProductId = a.FinSukkari, PlannedQtyKg = 5000, PriorityNo = 1 },
                new() { SourceType = "FromReceiving", LotId = lotAKha, CustomerId = a.Customer, ProductId = a.FinKhalas, PlannedQtyKg = 4000, PriorityNo = 2 },
                new() { SourceType = "FromReceiving", LotId = lotBSuk, CustomerId = b.Customer, ProductId = b.FinSukkari, PlannedQtyKg = 6000, PriorityNo = 3 }
            });
            Assert.True(plan.Ok, plan.Message);
            planId = plan.Id;
            Assert.True(planning.ApprovePlan(planId).Ok);
        }

        using var scope2 = host.Services.CreateScope();
        var orders = scope2.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var db = scope2.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = orders.GetOrderableItems(planId);
        Assert.Equal(3, items.Count);

        // أمر لكل بند — كما تفعل الشاشة (أمر لكل عميل)
        var oA = orders.SaveOrder("FromPlan", planId, a.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
        {
            new() { PlanItemId = items[0].PlanItemId, LotId = lotASuk, CustomerId = a.Customer, ProductId = a.FinSukkari, PlannedQtyKg = 5000 },
            new() { PlanItemId = items[1].PlanItemId, LotId = lotAKha, CustomerId = a.Customer, ProductId = a.FinKhalas, PlannedQtyKg = 4000 }
        });
        Assert.True(oA.Ok, oA.Message);
        var oB = orders.SaveOrder("FromPlan", planId, b.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = items[2].PlanItemId, LotId = lotBSuk, CustomerId = b.Customer, ProductId = b.FinSukkari, PlannedQtyKg = 6000 } });
        Assert.True(oB.Ok, oB.Message);

        // كل بند أمر احتفظ بعميله وصنفه ودفعة عميله — لا خلط
        var itemsA = db.ProductionOrderItems.Where(i => i.OrderId == oA.Id).ToList();
        var itemsB = db.ProductionOrderItems.Where(i => i.OrderId == oB.Id).ToList();
        Assert.Equal(2, itemsA.Count);
        Assert.All(itemsA, i => Assert.Equal(a.Customer, i.CustomerId));
        Assert.Contains(itemsA, i => i.ProductId == a.FinSukkari && i.LotId == lotASuk);
        Assert.Contains(itemsA, i => i.ProductId == a.FinKhalas && i.LotId == lotAKha);
        Assert.Single(itemsB);
        Assert.Equal(b.Customer, itemsB[0].CustomerId);
        Assert.Equal(b.FinSukkari, itemsB[0].ProductId);
        Assert.Equal(lotBSuk, itemsB[0].LotId);

        // المتبقيات مستقلة لكل بند
        var rem = orders.GetOrderableItems(planId);
        Assert.Equal(0, rem[0].RemainingKg, 1);
        Assert.Equal(0, rem[1].RemainingKg, 1);
        Assert.Equal(0, rem[2].RemainingKg, 1);

        // بطاقة كل أمر تعرض عميلها وصنفها فقط
        Assert.Equal("العميل A", orders.GetOrderCard(oA.Id).CustomerName);
        Assert.Equal("العميل B", orders.GetOrderCard(oB.Id).CustomerName);
        Assert.DoesNotContain("العميل B", orders.GetOrderCard(oA.Id).CustomerName);
    }

    // ══════════ الاختبار 4: منع تجاوز الطاقة الإنتاجية ══════════
    [Fact]
    public void Order_Cannot_Exceed_Plan_Remaining_And_Capacity_Is_Checked()
    {
        // §قيدان مستقلان: المتبقي من الخطة، وطاقة الوردية.
        // عندما يكون الأمر من خطة فالمتبقي من الخطة هو القيد الفعّال عادةً،
        // وقيد الطاقة يُفحص مستقلاً في MultiCustomerPlanTests وProductionBalanceTests.
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = SeedCompany(host, "T4", "شركة اختبار الطاقة");
        int lot = Receive(host, s.Customer, s.RawSukkari, 100000);

        using (var scope = host.Services.CreateScope())
        {
            var capacity = scope.ServiceProvider.GetRequiredService<ICapacityService>();
            Assert.True(capacity.SetCapacity(s.FinSukkari, 1, 4000).Ok);
            Assert.True(capacity.SetCapacity(s.FinSukkari, 2, 3000).Ok);
        }

        int planId;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            // 15,000 كجم ÷ 7.5 = 2,000 كرتون = 4 ساعات من 8
            var plan = planning.SavePlan("خطة الطاقة", "Period", "2026-09-01", "2026-09-05", 1, 1, new List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari,
                      PlannedQtyKg = 15000, ScheduledDate = "2026-09-01", PriorityNo = 1 } });
            Assert.True(plan.Ok, plan.Message);
            planId = plan.Id;
            Assert.True(planning.ApprovePlan(planId).Ok);
        }

        using var scope2 = host.Services.CreateScope();
        var orders = scope2.ServiceProvider.GetRequiredService<IProductionOrderService>();
        int planItemId = orders.GetOrderableItems(planId)[0].PlanItemId;

        // طاقة الوردية 4,000 كرتون والخطة استهلكت 2,000 → المتبقي 2,000 كرتون
        var slot1 = orders.GetOrderSlot(s.FinSukkari, null, 1, 1, "2026-09-01");
        Assert.Equal(4000, slot1.CapacityCartons);
        Assert.Equal(2000, slot1.RemainingCartons);
        Assert.Equal(500, slot1.RatePerHour, 1);

        // أمر 1,000 كرتون (7,500 كجم) ← ينجح ضمن المتبقي
        var ok = orders.SaveOrder("FromPlan", planId, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItemId, LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari,
                  PlannedQtyKg = 7500, PlannedCartons = 1000 } });
        Assert.True(ok.Ok, ok.Message);

        // أمر 3,000 كرتون (22,500 كجم) ← مرفوض: يتجاوز المتبقي من الخطة والطاقة معاً
        var over = orders.SaveOrder("FromPlan", planId, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItemId, LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari,
                  PlannedQtyKg = 22500, PlannedCartons = 3000 } });
        Assert.False(over.Ok);

        // والخطة بلا طاقة معرَّفة تُرفض عند تجاوز الطاقة (لا معدل افتراضي في الكود)
        using (var scope3 = host.Services.CreateScope())
        {
            var planning = scope3.ServiceProvider.GetRequiredService<IPlanningService>();
            var noCap = planning.SavePlan("خطة بلا طاقة", "Daily", "2026-09-09", "2026-09-09", 2, 1, new List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari,
                      PlannedQtyKg = 99999, ScheduledDate = "2026-09-09", PriorityNo = 1 } });
            Assert.False(noCap.Ok);
            Assert.Contains("الطاقة", noCap.Message);
        }
    }

    // ══════════ الاختبار 5: التتبع الكامل من الأمر حتى التسليم + آلة الحالات ══════════
    [Fact]
    public void Order_Traceability_FullChain_And_StateMachine()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = SeedCompany(host, "T5", "شركة التتبع");
        int lot = Receive(host, s.Customer, s.RawSukkari, 10000);

        using (var scope = host.Services.CreateScope())
        {
            var capacity = scope.ServiceProvider.GetRequiredService<ICapacityService>();
            Assert.True(capacity.SetCapacity(s.FinSukkari, 1, 20000).Ok);
        }

        int planId, orderId;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            string day = DateTime.Today.AddDays(2).ToString("dd/MM/yyyy");
            var plan = planning.SavePlan("خطة التتبع", "Daily", day, day, 1, 1, new List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari,
                      PlannedQtyKg = 9750, PlannedCartons = 1300, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
            Assert.True(plan.Ok, plan.Message);
            planId = plan.Id;
            Assert.True(planning.ApprovePlan(planId).Ok);

            var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            int planItemId = orders.GetOrderableItems(planId)[0].PlanItemId;

            var order = orders.SaveOrder("FromPlan", planId, s.Customer, day, 1, 1, new List<OrderItemDto>
            { new() { PlanItemId = planItemId, LotId = lot, CustomerId = s.Customer, ProductId = s.FinSukkari, PlannedQtyKg = 9750, PlannedCartons = 1300 } });
            Assert.True(order.Ok, order.Message);
            orderId = order.Id;

            // §آلة الحالات: مسودة لا تُبدأ مباشرة
            var noStart = orders.StartOrder(orderId);
            Assert.False(noStart.Ok);

            // التعديل مسموح قبل الاعتماد/البدء
            Assert.True(orders.UpdateOrderHeader(orderId, notes: "أمر التتبع التجريبي").Ok);

            // الاعتماد → مجدول (له تاريخ ووردية) — وصرف المواد المساعدة (الخام عند الإقفال)
            var ap = orders.ApproveOrder(orderId);
            Assert.True(ap.Ok, ap.Message);
            Assert.Equal(DocStatuses.Scheduled, orders.GetOrderCard(orderId).Status);

            // بدء الإنتاج → قيد التنفيذ، ويسجل الوقت والمستخدم في السجل
            var st = orders.StartOrder(orderId);
            Assert.True(st.Ok, st.Message);
            Assert.Equal(DocStatuses.InProgress, orders.GetOrderCard(orderId).Status);

            // بعد البدء: التعديل ممنوع (قفل الهوية والتتبع) والإلغاء ممنوع
            Assert.False(orders.UpdateOrderHeader(orderId, notes: "محاولة").Ok);
            Assert.False(orders.CancelOrder(orderId).Ok);

            // إيقاف ثم استئناف
            Assert.True(orders.StopOrder(orderId, "عطل كهربائي").Ok);
            Assert.Equal(DocStatuses.Stopped, orders.GetOrderCard(orderId).Status);
            Assert.True(orders.ResumeOrder(orderId).Ok);
            Assert.Equal(DocStatuses.InProgress, orders.GetOrderCard(orderId).Status);
        }

        // الإنتاج الفعلي ← الفحص ← المخزون ← التسليم
        using (var scope = host.Services.CreateScope())
        {
            var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
            // المنتَج = كمية الأمر كاملة (مجموع المخرجات لا يتجاوز الخام المستهلك)
            var close = exec.CloseProductionDay(orderId, producedKg: 9750, producedCartons: 1300,
                hashfKg: 0, nawaKg: 0, wastageKg: 0, carryToNextDay: false,
                downtimes: new List<DowntimeDto> { new() { Hours = 1, ReasonAr = "عطل كهربائي" } }, sendToQuality: true);
            Assert.True(close.Ok, close.Message);

            var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();
            var qc = quality.SaveCheck(orderId, close.Id, DateTime.Today.ToString("dd/MM/yyyy"), "نهائي", new List<QualityItemDto>
            { new() { ProductId = s.FinSukkari, CheckedQtyKg = 9750, AcceptedQtyKg = 9700, RejectedQtyKg = 50 } });
            Assert.True(qc.Ok, qc.Message);
            Assert.True(quality.ApproveCheck(qc.Id).Ok);

            var fg = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var rcpt = fg.SaveReceipt(orderId, qc.Id, DateTime.Today.ToString("dd/MM/yyyy"), new List<FinishedGoodsItemDto>
            { new() { ProductId = s.FinSukkari, LotId = lot, NetWeightKg = 9700, PackageCount = 1293 } });
            Assert.True(rcpt.Ok, rcpt.Message);
            Assert.True(fg.Issue(rcpt.Id).Ok);
            Assert.True(fg.Receive(rcpt.Id, null).Ok);

            var dlvSvc = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>();
            var dlv = dlvSvc.Save(s.Customer, DateTime.Today.ToString("dd/MM/yyyy"), orderId, new List<CustomerDeliveryItemDto>
            { new() { ProductId = s.FinSukkari, LotId = lot, QtyKg = 1000 } });
            Assert.True(dlv.Ok, dlv.Message);
            Assert.True(dlvSvc.Approve(dlv.Id).Ok);

            // الأمر اكتمل إنتاجه ← «مكتمل»، وبعد الاستلام الكامل في التام يصبح «مغلقاً»
            var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var card = orders.GetOrderCard(orderId);
            Assert.True(card.Status is DocStatuses.Completed or DocStatuses.Closed,
                $"الحالة المتوقعة مكتمل/مغلق والفعلية: {card.StatusAr}");

            // §بطاقة الملخص: السلسلة كاملة بدون فقد أي رابط
            Assert.Equal("شركة التتبع", card.CustomerName);
            Assert.Equal("سكري", card.RawName);              // الصنف المستلم بالكيلو
            Assert.Equal("سكري تام", card.ProductName);       // المنتج النهائي بالكرتون
            Assert.True(card.PlannedInPlanKg > 0);
            Assert.Equal(9750, card.OrderedKg, 1);
            Assert.Equal(9750, card.ProducedKg, 1);
            Assert.Equal(9700, card.AcceptedKg, 1);
            Assert.Equal(0, card.RemainingKg, 1);
            Assert.Equal(100, card.ProgressPct, 0);
            Assert.NotEqual("-", card.PlanNumber);
            Assert.NotEqual("-", card.LotCode);
            Assert.NotEqual("-", card.ShipmentNumber);
            Assert.NotEqual("-", card.ShiftName);
            Assert.NotEqual("-", card.StartTime);

            // سجل العمليات: إنشاء/اعتماد/بدء/توقف/استئناف/إقفال/فحص
            var events = orders.GetOrderEvents(orderId);
            Assert.Contains(events, e => e.Action.Contains("إنشاء"));
            Assert.Contains(events, e => e.Action.Contains("اعتماد الأمر"));
            Assert.Contains(events, e => e.Action.Contains("بدء"));
            Assert.Contains(events, e => e.Action.Contains("إقفال يوم الإنتاج"));
            Assert.Contains(events, e => e.Action.Contains("اعتماد الفحص"));
            Assert.Contains(events, e => e.Detail.Contains("عطل كهربائي"));

            // §التتبع من الأمر إلى رحلة الصنف: الخطة ← العميل ← الخام ← المنتج ← الفحص ← المخزون ← التسليم
            var trace = scope.ServiceProvider.GetRequiredService<ITraceabilityService>();
            var journey = trace.GetJourneys(s.Customer, s.RawSukkari).Single();
            Assert.Equal(10000, journey.ReceivedKg, 1);
            Assert.Equal(9750, journey.PlannedKg, 1);
            Assert.Equal(9750, journey.ProducedKg, 1);
            Assert.Equal(9700, journey.AcceptedKg, 1);
            Assert.Equal(8700, journey.InStockKg, 1); // 9700 − 1000 مسلَّم
            Assert.Equal(1000, journey.DeliveredKg, 1);
        }
    }
}
