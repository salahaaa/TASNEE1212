using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات إصلاح الترقيم — كانت أرقام المستندات تتكرر (خطأ UNIQUE) لأن خدمة الترقيم
/// كانت Singleton فلا تُحفظ زيادة التسلسل أبداً. الآن Scoped + حلقة ضمان عدم التكرار.
/// </summary>
public class NumberingTests
{
    [Fact]
    public void Next_ProducesUniqueSequentialNumbers_AndPersists()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var numbering = scope.ServiceProvider.GetRequiredService<INumberingService>();

        var n1 = numbering.Next("SHIP"); db.SaveChanges();
        var n2 = numbering.Next("SHIP"); db.SaveChanges();
        var n3 = numbering.Next("SHIP"); db.SaveChanges();

        Assert.NotEqual(n1, n2);
        Assert.NotEqual(n2, n3);
        Assert.NotEqual(n1, n3);

        // التسلسل محفوظ — خدمة جديدة في نفس النطاق تقرأ القيمة المحدثة
        var seq = db.NumberingSchemes.First(s => s.SchemeCode == "SHIP").LastSequence;
        Assert.True(seq >= 3, $"التسلسل يجب أن يُحفظ (كان {seq})");
    }

    [Fact]
    public void Next_SelfCorrects_When_Sequence_Is_Stale_Behind_Existing_Data()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var numbering = scope.ServiceProvider.GetRequiredService<INumberingService>();

        // محاكاة قاعدة غير متزامنة: مستندات موجودة حتى التسلسل 7 بينما المخطط ما زال عند 0
        var cust = db.Customers.First();
        for (int i = 1; i <= 7; i++)
        {
            db.Shipments.Add(new Shipment
            {
                DocumentNumber = $"REC-{DateTime.Now:yyyy}-{i:D4}",
                CustomerId = cust.Id,
                TotalWeightKg = 100
            });
        }
        db.SaveChanges();
        var scheme = db.NumberingSchemes.First(s => s.SchemeCode == "SHIP");
        scheme.LastSequence = 0; // تسلسل متأخر (الخلل القديم)
        db.SaveChanges();

        // الرقم التالي يجب ألا يكرر أي رقم موجود — يقفز فوقها تلقائياً
        var next = numbering.Next("SHIP");
        var seq = int.Parse(next.Split('-')[^1]);
        Assert.True(seq >= 8, $"الرقم يجب أن يتجاوز الموجود (حصل على {next})");

        // ولا يصطدم عند الحفظ الفعلي
        db.Shipments.Add(new Shipment { DocumentNumber = next, CustomerId = cust.Id, TotalWeightKg = 50 });
        db.SaveChanges(); // يجب ألا يرمي UNIQUE
    }

    [Fact]
    public void Next_CreatesMissingScheme_SelfHealing()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var numbering = scope.ServiceProvider.GetRequiredService<INumberingService>();

        // مخطط غير موجود إطلاقاً — يُنشأ ذاتياً بدل الانهيار
        Assert.Null(db.NumberingSchemes.FirstOrDefault(s => s.SchemeCode == "NEWSCHEME"));
        var n1 = numbering.Next("NEWSCHEME"); db.SaveChanges();
        var n2 = numbering.Next("NEWSCHEME"); db.SaveChanges();
        Assert.NotEqual(n1, n2);
        Assert.NotNull(db.NumberingSchemes.FirstOrDefault(s => s.SchemeCode == "NEWSCHEME"));
    }

    [Fact]
    public void ApproveShipment_CreatesUniqueLotsAndTransactions_NoDuplicateErrors()
    {
        // سيناريو المستخدم حرفياً: حفظ سندات واعتمادها بلا أخطاء UNIQUE
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var cust = db.Customers.First();

        for (int s = 0; s < 3; s++)
        {
            var prod = db.Products.First(p => p.GroupCode == "001");
            var save = receiving.SaveShipment(cust.Id, "2026-08-25", "2026-08-25",
                new List<ShipmentItemDto>
                {
                    new() { ProductId = prod.Id, PackageCount = 10, UnitWeightKg = 20, QtyKg = 200 }
                });
            Assert.True(save.Ok, save.Message);
            var approve = receiving.ApproveShipment(save.Id);
            Assert.True(approve.Ok, approve.Message);
        }

        // كل أرقام المستندات والدفعات والحركات فريدة
        var docNos = db.Shipments.Select(x => x.DocumentNumber).ToList();
        Assert.Equal(docNos.Count, docNos.Distinct().Count());
        var lotCodes = db.Lots.Select(x => x.LotCode).ToList();
        Assert.Equal(lotCodes.Count, lotCodes.Distinct().Count());
        var txnNos = db.InventoryTransactions.Select(x => x.TxnNumber).ToList();
        Assert.Equal(txnNos.Count, txnNos.Distinct().Count());
    }
}
