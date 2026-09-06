using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§25 — محرك التقارير المركزي: بيانات حية من القاعدة المركزية (مستلم/مصروف/متبقي).</summary>
public partial class ReportService : ServiceBase, IReportService
{
    public ReportService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public List<ReportDefinition> GetReports()
    {
        var customerOpts = Db.Customers.AsNoTracking().OrderBy(c => c.CustomerName)
            .Select(c => new { c.Id, c.CustomerName }).ToList().Select(x => (x.Id.ToString(), x.CustomerName)).ToList();
        var productOpts = Db.Products.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.ProductNameAr }).ToList().Select(x => (x.Id.ToString(), x.ProductNameAr)).ToList();
        var list = new List<ReportDefinition>
    {
        new ReportDefinition { Code = "receiving", TitleAr = "تقارير الاستلام", Category = "الاستلام", Parameters = DateRange() },
        new ReportDefinition { Code = "inventory", TitleAr = "تقارير المخزون (الأرصدة)", Category = "المخزون" },
        new ReportDefinition { Code = "customers", TitleAr = "تقارير العملاء وأرصدتهم", Category = "العملاء" },
        new ReportDefinition { Code = "lots", TitleAr = "تقارير الدفعات Lots", Category = "الدفعات" },
        new ReportDefinition { Code = "plans", TitleAr = "خطط الإنتاج", Category = "الإنتاج", Parameters = DateRange() },
        new ReportDefinition { Code = "orders", TitleAr = "أوامر الإنتاج", Category = "الإنتاج", Parameters = DateRange() },
        new ReportDefinition { Code = "material_consumption", TitleAr = "تقارير استهلاك المواد", Category = "المواد" },
        new ReportDefinition { Code = "production", TitleAr = "الإنتاج المنفذ (جلسات التشغيل)", Category = "الإنتاج", Parameters = DateRange() },
        new ReportDefinition { Code = "quality", TitleAr = "فحوصات الجودة", Category = "الجودة", Parameters = DateRange() },
        new ReportDefinition { Code = "wastage", TitleAr = "الهالك والأصناف الثانوية", Category = "الجودة" },
        new ReportDefinition { Code = "finished_goods", TitleAr = "تقارير الإنتاج التام", Category = "المخزون" },
        new ReportDefinition { Code = "delivery", TitleAr = "تسليم العملاء", Category = "التسليم", Parameters = DateRange() },
        new ReportDefinition { Code = "movements", TitleAr = "تقارير حركة المخزون", Category = "المخزون", Parameters = DateRange() },
        new ReportDefinition { Code = "audit", TitleAr = "تقارير التدقيق", Category = "الإدارة", Parameters = DateRange() },
        new ReportDefinition { Code = "management", TitleAr = "تقارير الإدارة (مؤشرات)", Category = "الإدارة" },
        new ReportDefinition
        {
            Code = "item_journey",
            TitleAr = "تتبع الصنف — الرحلة الكاملة (استلام ← إنتاج ← فحص ← تسليم)",
            Category = "التتبع",
            Parameters = new List<ReportParameter>
            {
                new ReportParameter { Key = "customer", LabelAr = "العميل", Kind = "list", Options = customerOpts },
                new ReportParameter { Key = "product", LabelAr = "الصنف", Kind = "list", Options = productOpts }
            }
        }
    };
        // §مرحلة التقارير: تقارير العمليات والتقارير الشاملة (المحرك الجديد)
        list.AddRange(GetNewReportDefinitions());
        // §خيارات الشحنات الفعلية لفلتر تتبع الشحنة (أحدث 100)
        foreach (var d in list.Where(x => x.Code == "shipment_tracking"))
        {
            var prm = d.Parameters.First(x => x.Key == "shipment");
            prm.Options = Db.Shipments.AsNoTracking().OrderByDescending(x => x.Id).Take(100).ToList()
                .Select(x => ((string)x.Id.ToString(), $"{x.DocumentNumber} ({x.TotalWeightKg:N0} كجم)")).ToList();
        }
        // §العملية الشاملة لتطوير التقارير: الحزمة الاحترافية (7 تقارير)
        list.AddRange(GetProfessionalDefinitions());
        // §المعالجة والتعقيم: حزمة تقارير الدورة (سجل + متأخرات + أداء المدد).
        list.AddRange(GetTreatmentDefinitions());
        return list;
    }

    /// <summary>§نص الفترة للترويسة — يظهر دائماً حتى بلا فلتر.</summary>
    private static string PeriodText(DateTime? from, DateTime? to)
    {
        if (from == null && to == null) return $"من البداية حتى {DateTime.Today:dd/MM/yyyy} (بلا تحديد فترة)";
        return $"{(from != null ? from.Value.ToString("dd/MM/yyyy") : "البداية")} ← {(to != null ? to.Value.ToString("dd/MM/yyyy") : "اليوم")}";
    }

    private static List<ReportParameter> DateRange() => new()
    {
        new ReportParameter { Key = "from", LabelAr = "من تاريخ", Kind = "date" },
        new ReportParameter { Key = "to", LabelAr = "إلى تاريخ", Kind = "date" }
    };

    public ReportResult Run(string reportCode, Dictionary<string, string> parameters)
    {
        Require("reports", "View");
        parameters ??= new Dictionary<string, string>();
        DateTime? from = parameters.TryGetValue("from", out var f) && UiFormat.TryParseDate(f, out var fd) ? fd : null;
        DateTime? to = parameters.TryGetValue("to", out var t) && UiFormat.TryParseDate(t, out var td) ? td : null;
        // §تتبع الصنف: تصفية التقارير بالعميل و/أو الصنف
        int? custId = parameters.TryGetValue("customer", out var cp) && int.TryParse(cp, out var cVal) ? cVal : null;
        int? prodId = parameters.TryGetValue("product", out var pp) && int.TryParse(pp, out var pVal) ? pVal : null;

        var r = new ReportResult();
            r.RowLinks = new List<DocLinkDto>();   // §زر «+» للتنقل إلى المستند المصدر
        switch (reportCode)
        {
            case "receiving":
            {
                r.TitleAr = "تقرير الاستلام";
                r.Columns.AddRange(new[] { "رقم الاستلام", "التاريخ", "العميل", "عدد البنود", "إجمالي الوزن (كجم)", "الحالة" });
                var q = Db.Shipments.Include(s => s.Items).AsQueryable();
                if (from != null) q = q.Where(s => s.ReceivedDate >= from);
                if (to != null) q = q.Where(s => s.ReceivedDate <= to.Value.AddDays(1));
                if (custId != null) q = q.Where(s => s.CustomerId == custId);
                if (prodId != null) q = q.Where(s => s.Items.Any(i => i.ProductId == prodId));
                foreach (var s in q.OrderByDescending(s => s.Id))
                {
                    var cust = Db.Customers.Where(c => c.Id == s.CustomerId).Select(c => c.CustomerName).FirstOrDefault();
                    r.Rows.Add(new object[] { s.DocumentNumber, s.ReceivedDate?.ToString("dd/MM/yyyy"), cust, s.Items.Count, s.TotalWeightKg, Core.Common.DocStatuses.ToArabic(s.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "receiving", Id = s.Id });
                }
                r.Summary["إجمالي الاستلام (كجم)"] = r.Rows.Sum(x => Convert.ToDouble(x[4])).ToString("N1");
                break;
            }
            case "inventory":
            {
                r.TitleAr = "تقرير أرصدة المخزون";
                r.Columns.AddRange(new[] { "المخزن", "الصنف/المادة", "الدفعة", "العميل", "الرصيد (كجم)", "عدد العبوات" });
                foreach (var b in Db.StockBalances.Where(b => b.QtyKg != 0 || b.PackageCount != 0))
                {
                    r.Rows.Add(new object[]
                    {
                        Db.Warehouses.Where(w => w.Id == b.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                        b.ProductId != null ? Db.Products.Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                                            : Db.AuxiliaryMaterials.Where(m => m.Id == b.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
                        Db.Lots.Where(l => l.Id == b.LotId).Select(l => l.LotCode).FirstOrDefault(),
                        Db.Customers.Where(c => c.Id == b.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                        b.QtyKg, b.PackageCount
                    });
                }
                break;
            }
            case "customers":
            {
                r.TitleAr = "تقرير العملاء وأرصدة الإنتاج التام";
                r.Columns.AddRange(new[] { "العميل", "الهاتف", "الرصيد في مخزن التام (كجم)", "إجمالي المسلَّم (كجم)" });
                foreach (var c in Db.Customers.Where(c => c.IsActive))
                {
                    double fg = Db.StockBalances.Where(b => b.CustomerId == c.Id && b.QtyKg > 0).Sum(b => b.QtyKg);
                    double delivered = Db.CustomerDeliveries.Where(d => d.CustomerId == c.Id && d.IsApproved).Sum(d => d.TotalQtyKg);
                    r.Rows.Add(new object[] { c.CustomerName, c.Phone, fg, delivered });
                }
                break;
            }
            case "lots":
            {
                r.TitleAr = "تقرير الدفعات (المستلم / المصروف للإنتاج / المتبقي)";
                r.Columns.AddRange(new[] { "الدفعة", "الصنف", "العميل", "المستلم (كجم)", "المصروف للإنتاج (كجم)", "المتبقي (كجم)", "المسلَّم (كجم)" });
                var lotsQ = Db.Lots.AsQueryable();
                if (custId != null) lotsQ = lotsQ.Where(l => l.CustomerId == custId);
                if (prodId != null) lotsQ = lotsQ.Where(l => l.ProductId == prodId);
                foreach (var l in lotsQ)
                {
                    r.Rows.Add(new object[]
                    {
                        l.LotCode,
                        Db.Products.Where(p => p.Id == l.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                        Db.Customers.Where(c => c.Id == l.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                        l.InitialQtyKg, l.ProducedQtyKg, l.InStockQtyKg, l.DeliveredQtyKg
                    });
                    r.RowLinks.Add(new DocLinkDto { DocType = "receiving", Id = l.ShipmentId ?? 0 });
                }
                r.Summary["إجمالي المتبقي (كجم)"] = Db.Lots.Sum(l => l.InStockQtyKg).ToString("N1");
                break;
            }
            case "plans":
            {
                r.TitleAr = "تقرير خطط الإنتاج";
                r.Columns.AddRange(new[] { "الخطة", "العنوان", "من", "إلى", "عدد البنود", "إجمالي الكمية (كجم)", "الحالة" });
                var plansQ = Db.ProductionPlans.Include(p => p.Items).AsQueryable();
                if (from != null) plansQ = plansQ.Where(p => p.StartDate >= from);
                if (to != null) plansQ = plansQ.Where(p => p.StartDate < to.Value.AddDays(1));
                if (custId != null) plansQ = plansQ.Where(p => p.Items.Any(i => i.CustomerId == custId));
                if (prodId != null) plansQ = plansQ.Where(p => p.Items.Any(i => i.ProductId == prodId));
                foreach (var p in plansQ)
                {
                    r.Rows.Add(new object[] { p.DocumentNumber, p.PlanTitle, p.StartDate?.ToString("dd/MM/yyyy"), p.EndDate?.ToString("dd/MM/yyyy"),
                        p.Items.Count, p.Items.Sum(i => i.PlannedQtyKg), Core.Common.DocStatuses.ToArabic(p.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "planning", Id = p.Id });
                }
                break;
            }
            case "orders":
            {
                r.TitleAr = "تقرير أوامر الإنتاج";
                r.Columns.AddRange(new[] { "الأمر", "التاريخ", "عدد البنود", "المخطط (كجم)", "المنتَج (كجم)", "الحالة" });
                var ordersQ = Db.ProductionOrders.Include(o => o.Items).AsQueryable();
                if (from != null) ordersQ = ordersQ.Where(o => o.ProductionDate >= from);
                if (to != null) ordersQ = ordersQ.Where(o => o.ProductionDate < to.Value.AddDays(1));
                if (custId != null) ordersQ = ordersQ.Where(o => o.CustomerId == custId || o.Items.Any(i => i.CustomerId == custId));
                if (prodId != null) ordersQ = ordersQ.Where(o => o.Items.Any(i => i.ProductId == prodId));
                foreach (var o in ordersQ)
                {
                    r.Rows.Add(new object[] { o.DocumentNumber, o.ProductionDate?.ToString("dd/MM/yyyy"), o.Items.Count,
                        o.Items.Sum(i => i.PlannedQtyKg), o.Items.Sum(i => i.ProducedQtyKg), Core.Common.DocStatuses.ToArabic(o.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = o.Id });
                }
                break;
            }
            case "material_consumption":
            {
                r.TitleAr = "تقرير استهلاك المواد المساعدة";
                r.Columns.AddRange(new[] { "الأمر", "المادة", "المحتسبة", "المصروفة", "المستهلكة", "الهالك", "المتبقي غير المستخدم" });
                foreach (var m in Db.ProductionOrderMaterials)
                {
                    double unused = m.ActualIssuedQty - m.ConsumedQty - m.WastedQty - m.ReturnedQty;
                    r.Rows.Add(new object[]
                    {
                        Db.ProductionOrders.Where(o => o.Id == m.OrderId).Select(o => o.DocumentNumber).FirstOrDefault(),
                        Db.AuxiliaryMaterials.Where(x => x.Id == m.MaterialId).Select(x => x.MaterialNameAr).FirstOrDefault(),
                        m.CalculatedQty, m.ActualIssuedQty, m.ConsumedQty, m.WastedQty, Math.Round(unused, 2)
                    });
                }
                break;
            }
            case "production":
            {
                r.TitleAr = "تقرير الإنتاج المنفذ";
                r.Columns.AddRange(new[] { "الجلسة", "الأمر", "البداية", "النهاية", "الكمية (كجم)", "كراتين", "الحالة" });
                var exeQ = Db.ProductionExecutions.AsQueryable();
                if (from != null) exeQ = exeQ.Where(e => e.StartDateTime >= from);
                if (to != null) exeQ = exeQ.Where(e => e.StartDateTime < to.Value.AddDays(1));
                if (custId != null) exeQ = exeQ.Where(e => Db.ProductionOrders.Any(o => o.Id == e.OrderId && o.CustomerId == custId));
                if (prodId != null) exeQ = exeQ.Where(e => Db.ProductionOrderItems.Any(i => i.OrderId == e.OrderId && i.ProductId == prodId));
                foreach (var e in exeQ)
                {
                    r.Rows.Add(new object[] { e.DocumentNumber,
                        Db.ProductionOrders.Where(o => o.Id == e.OrderId).Select(o => o.DocumentNumber).FirstOrDefault(),
                        e.StartDateTime?.ToString("dd/MM/yyyy HH:mm"), e.EndDateTime?.ToString("dd/MM/yyyy HH:mm"),
                        e.ActualQtyKg, e.ActualCartons, Core.Common.DocStatuses.ToArabic(e.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = e.OrderId });
                }
                break;
            }
            case "quality":
            {
                r.TitleAr = "تقرير فحوصات الجودة";
                r.Columns.AddRange(new[] { "الفحص", "الأمر", "التاريخ", "المفحوص (كجم)", "المقبول (كجم)", "المرفوض (كجم)", "الحالة" });
                var qcQ = Db.QualityChecks.AsQueryable();
                if (from != null) qcQ = qcQ.Where(c => c.CheckDate >= from);
                if (to != null) qcQ = qcQ.Where(c => c.CheckDate < to.Value.AddDays(1));
                if (custId != null) qcQ = qcQ.Where(c => Db.ProductionOrders.Any(o => o.Id == c.OrderId && o.CustomerId == custId));
                if (prodId != null) qcQ = qcQ.Where(c => c.Items.Any(i => i.ProductId == prodId));
                foreach (var c in qcQ)
                {
                    r.Rows.Add(new object[] { c.DocumentNumber,
                        Db.ProductionOrders.Where(o => o.Id == c.OrderId).Select(o => o.DocumentNumber).FirstOrDefault(),
                        c.CheckDate?.ToString("dd/MM/yyyy"), c.TotalCheckedKg, c.AcceptedKg, c.RejectedKg, Core.Common.DocStatuses.ToArabic(c.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "quality", Id = c.Id });
                }
                break;
            }
            case "wastage":
            {
                r.TitleAr = "تقرير الهالك والأصناف الثانوية (بالكيلو)";
                r.Columns.AddRange(new[] { "الفحص", "الصنف الثانوي", "الكمية (كجم)" });
                foreach (var b in Db.QualityByProductRecords)
                {
                    r.Rows.Add(new object[]
                    {
                        Db.QualityChecks.Where(c => c.Id == b.CheckId).Select(c => c.DocumentNumber).FirstOrDefault(),
                        Db.ByProducts.Where(x => x.Id == b.ByProductId).Select(x => x.ByProductNameAr).FirstOrDefault(),
                        b.QtyKg
                    });
                }
                break;
            }
            case "finished_goods":
            {
                r.TitleAr = "تقرير أرصدة الإنتاج التام حسب العميل";
                r.Columns.AddRange(new[] { "العميل", "الصنف", "الدفعة", "الرصيد (كجم)", "العبوات" });
                var whFg = Db.Warehouses.FirstOrDefault(w => w.WarehouseCode == "WFG")?.Id ?? 0;
                var fgQ = Db.StockBalances.Where(b => b.WarehouseId == whFg && (b.QtyKg != 0 || b.PackageCount != 0));
                if (custId != null) fgQ = fgQ.Where(b => b.CustomerId == custId);
                if (prodId != null) fgQ = fgQ.Where(b => b.ProductId == prodId);
                foreach (var b in fgQ)
                {
                    r.Rows.Add(new object[]
                    {
                        Db.Customers.Where(c => c.Id == b.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                        Db.Products.Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                        Db.Lots.Where(l => l.Id == b.LotId).Select(l => l.LotCode).FirstOrDefault(),
                        b.QtyKg, b.PackageCount
                    });
                }
                break;
            }
            case "delivery":
            {
                r.TitleAr = "تقرير تسليم العملاء";
                r.Columns.AddRange(new[] { "السند", "العميل", "التاريخ", "الكمية (كجم)", "الكراتين", "الحالة" });
                var dlvQ = Db.CustomerDeliveries.AsQueryable();
                if (from != null) dlvQ = dlvQ.Where(d => d.DeliveryDate >= from);
                if (to != null) dlvQ = dlvQ.Where(d => d.DeliveryDate < to.Value.AddDays(1));
                if (custId != null) dlvQ = dlvQ.Where(d => d.CustomerId == custId);
                if (prodId != null) dlvQ = dlvQ.Where(d => d.Items.Any(i => i.ProductId == prodId));
                foreach (var d in dlvQ)
                {
                    r.Rows.Add(new object[] { d.DocumentNumber,
                        Db.Customers.Where(c => c.Id == d.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                        d.DeliveryDate?.ToString("dd/MM/yyyy"), d.TotalQtyKg, d.TotalCartons, Core.Common.DocStatuses.ToArabic(d.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "delivery", Id = d.Id });
                }
                break;
            }
            case "movements":
            {
                r.TitleAr = "تقرير حركة المخزون (تتبع كامل)";
                r.Columns.AddRange(new[] { "الحركة", "التاريخ", "المخزن", "الصنف", "الدفعة", "النوع", "الكمية (كجم)", "المستند", "المستخدم", "الجهاز" });
                var q = Db.InventoryTransactions.AsQueryable();
                if (from != null) q = q.Where(x => x.TxnDate >= from);
                if (to != null) q = q.Where(x => x.TxnDate <= to.Value.AddDays(1));
                foreach (var x in q.OrderByDescending(x => x.TxnDate).Take(3000))
                {
                    r.Rows.Add(new object[] { x.TxnNumber, x.TxnDate.ToString("dd/MM/yyyy HH:mm"),
                        Db.Warehouses.Where(w => w.Id == x.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                        x.ProductId != null ? Db.Products.Where(p => p.Id == x.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                                            : Db.AuxiliaryMaterials.Where(m => m.Id == x.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
                        Db.Lots.Where(l => l.Id == x.LotId).Select(l => l.LotCode).FirstOrDefault(),
                        x.MovementType == Core.Domain.Enums.MovementType.Inbound ? "وارد" : "صادر",
                        x.QtyKg, $"{x.ReferenceDocType}: {x.ReferenceDocNumber}",
                        Db.Users.Where(u => u.Id == x.CreatedBy).Select(u => u.FullName).FirstOrDefault(), x.MachineName });
                }
                break;
            }
            case "audit":
            {
                r.TitleAr = "تقرير سجل التدقيق";
                r.Columns.AddRange(new[] { "التاريخ", "المستخدم", "الجهاز", "الإجراء", "الشاشة", "المستند", "السجل" });
                var q = Db.AuditLogs.AsQueryable();
                if (from != null) q = q.Where(a => a.ActionDate >= from);
                if (to != null) q = q.Where(a => a.ActionDate <= to.Value.AddDays(1));
                foreach (var a in q.OrderByDescending(a => a.ActionDate).Take(3000))
                    r.Rows.Add(new object[] { a.ActionDate.ToString("dd/MM/yyyy HH:mm:ss"), a.UserName, a.MachineName, a.ActionType, a.ScreenName, a.DocumentNumber, a.RecordId });
                break;
            }
            case "management":
            {
                r.TitleAr = "مؤشرات الإدارة العامة";
                r.Columns.AddRange(new[] { "المؤشر", "القيمة" });
                r.Rows.Add(new object[] { "إجمالي المستلم من التمور (كجم)", Db.Shipments.Where(s => s.IsApproved).Sum(s => s.TotalWeightKg) });
                r.Rows.Add(new object[] { "رصيد الخام المتبقي (كجم)", Db.Lots.Sum(l => l.InStockQtyKg) });
                r.Rows.Add(new object[] { "إجمالي الإنتاج المنفذ (كجم)", Db.ProductionExecutions.Where(e => e.Status == "Completed").Sum(e => e.ActualQtyKg) });
                r.Rows.Add(new object[] { "إجمالي المسلَّم للعملاء (كجم)", Db.CustomerDeliveries.Where(d => d.IsApproved).Sum(d => d.TotalQtyKg) });
                r.Rows.Add(new object[] { "أوامر إنتاج مفتوحة", Db.ProductionOrders.Count(o => !o.IsClosed && o.IsApproved) });
                r.Rows.Add(new object[] { "خطط نشطة", Db.ProductionPlans.Count(p => p.IsApproved && !p.IsClosed) });
                r.Rows.Add(new object[] { "أجهزة متصلة", Db.ClientMachines.Count(m => m.IsActive) });
                break;
            }
            case "item_journey":
            {
                // §تتبع الصنف: الرحلة الكاملة لكل صنف — استلام ← خطة ← أمر ← إنتاج ← فحص ← مخزون ← تسليم ← فاتورة
                r.TitleAr = "تقرير تتبع الصنف — الرحلة الكاملة من الاستلام حتى الفاتورة";
                var svc = new TraceabilityService(Db, Session, Numbering);
                var journeys = svc.GetJourneys(custId, prodId);
                r.Columns.AddRange(new[] { "الصنف", "النوع", "العميل", "المرحلة", "المستند", "التاريخ", "الدفعة", "الكمية (كجم)", "الكراتين", "الحالة", "التفاصيل" });
                foreach (var j in journeys)
                {
                    r.Rows.Add(new object[]
                    {
                        j.ProductName, j.ItemTypeAr, j.CustomerName, "═══ ملخص الرحلة ═══", "-", "-", "-",
                        j.ReceivedKg, 0,
                        $"استُلم {j.ReceivedKg:N1} | خُطط {j.PlannedKg:N1} | أُنتج {j.ProducedKg:N1} | قُبل {j.AcceptedKg:N1}",
                        $"مخزون {j.InStockKg:N1} | سُلّم {j.DeliveredKg:N1} | فُوتر {j.InvoicedKg:N1} | متبقي {j.RemainingKg:N1}"
                    });
                    foreach (var s in j.Stages)
                        r.Rows.Add(new object[] { j.ProductName, j.ItemTypeAr, s.CustomerName, s.StageAr, s.DocNumber, s.Date ?? "-", s.LotCode ?? "-", s.QtyKg, s.Cartons, s.StatusAr ?? "-", s.Detail ?? "-" });
                }
                break;
            }
            default:
            {
                // §مرحلة التقارير: التقارير الجديدة (العمليات + الشاملة + الاحترافية)
                var nr = RunNewReports(reportCode, parameters, from, to, custId, prodId)
                    ?? RunProfessional(reportCode, parameters, from, to, custId, prodId)
                    ?? RunTreatmentReports(reportCode, parameters, from, to, custId, prodId);
                if (nr != null)
                {
                    if (string.IsNullOrWhiteSpace(nr.PeriodLabel)) nr.PeriodLabel = PeriodText(from, to);
                    nr.RowLinks ??= new List<DocLinkDto>();
                }
                return nr;
            }
        }
        // §إصلاح: الفترة تُعرض دائماً في الترويسة — حتى بلا فلتر، ليعرف القارئ مدى تغطية التقرير
        if (string.IsNullOrWhiteSpace(r.PeriodLabel)) r.PeriodLabel = PeriodText(from, to);
        // §زر «+»: القائمة لا تكون null أبداً حتى لا ينكسر العرض في تقرير بلا مستند مصدر
        r.RowLinks ??= new List<DocLinkDto>();
        // §إصلاح: كل تقرير يحمل إجمالي عدد الصفوف على الأقل
        if (r.Summary.Count == 0) r.Summary["عدد الصفوف"] = r.Rows.Count.ToString("N0");
        return r;
    }
}
