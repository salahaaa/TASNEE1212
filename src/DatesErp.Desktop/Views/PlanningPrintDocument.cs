using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;

namespace DatesErp.Desktop.Views;

/// <summary>§نموذج خطة الإنتاج للمعاينة/الطباعة — بيانات كاملة تُحمَّل من قاعدة البيانات.</summary>
public class PlanningPrintModel
{
    public string CompanyNameAr { get; set; } = "شركة التمور";
    public string CompanyNameEn { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public byte[] LogoBytes { get; set; }

    public string PlanNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string PlanTypeAr { get; set; } = "";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsApproved { get; set; }
    public string ShiftName { get; set; } = "-";
    public string LineName { get; set; } = "-";
    public string Notes { get; set; } = "";
    public string CreatedByName { get; set; } = "-";

    public class ItemRow
    {
        public int RowNo { get; set; }
        public string CustomerName { get; set; } = "-";
        public string ShipmentNo { get; set; } = "-";
        public string LotCode { get; set; } = "-";
        public string RawName { get; set; } = "-";
        public string ProductName { get; set; } = "-";
        public string PackName { get; set; } = "-";
        public int Cartons { get; set; }
        public double QtyKg { get; set; }
        public string Date { get; set; } = "-";
        public string ShiftName { get; set; } = "-";
    }

    public List<ItemRow> Items { get; set; } = new();

    public static PlanningPrintModel Load(DatesErpDbContext db, int planId)
    {
        var plan = db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return null;
        var co = db.CompanyInfos.OrderBy(x => x.Id).FirstOrDefault();

        var shiftId = plan.Items.Select(i => i.SuggestedShiftId).FirstOrDefault();
        var lineId = plan.Items.Select(i => i.SuggestedLineId).FirstOrDefault();

        var m = new PlanningPrintModel
        {
            PlanNumber = plan.DocumentNumber,
            Title = plan.PlanTitle ?? "",
            StartDate = plan.StartDate,
            EndDate = plan.EndDate,
            IsApproved = plan.IsApproved,
            ShiftName = db.Shifts.Where(s => s.Id == (shiftId ?? 0)).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-",
            LineName = db.ProductionLines.Where(l => l.Id == (lineId ?? 0)).Select(l => l.LineNameAr).FirstOrDefault() ?? "-",
            Notes = plan.Notes ?? "",
            PlanTypeAr = plan.PlanType switch { "Daily" => "يومية", "Weekly" => "أسبوعية", _ => "فترة محددة" }
        };
        if (co != null)
        {
            m.CompanyNameAr = co.CompanyNameAr ?? m.CompanyNameAr;
            m.CompanyNameEn = co.CompanyNameEn ?? "";
            m.Address = co.Address ?? "";
            m.Phone = co.Phone ?? "";
            m.LogoBytes = co.LogoBytes;
        }

        int n = 1;
        foreach (var it in plan.Items.OrderBy(i => i.ScheduledDate ?? DateTime.MinValue).ThenBy(i => i.PriorityNo))
        {
            string Cust(int? cid) => db.Customers.Where(c => c.Id == (cid ?? 0)).Select(c => c.CustomerName).FirstOrDefault();
            string Prod(int pid) => db.Products.Where(p => p.Id == pid).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
            string LotCode(int? lid) => db.Lots.Where(l => l.Id == (lid ?? 0)).Select(l => l.LotCode).FirstOrDefault() ?? "-";
            int? lotShip = db.Lots.Where(l => l.Id == (it.LotId ?? 0)).Select(l => l.ShipmentId).FirstOrDefault();

            m.Items.Add(new ItemRow
            {
                RowNo = n++,
                CustomerName = Cust(it.CustomerId) ?? "-",
                ShipmentNo = db.Shipments.Where(s => s.Id == (lotShip ?? 0)).Select(s => s.DocumentNumber).FirstOrDefault() ?? "-",
                LotCode = LotCode(it.LotId),
                RawName = db.Lots.Where(l => l.Id == (it.LotId ?? 0)).Select(l => l.ProductId).FirstOrDefault() is int rp
                            ? db.Products.Where(p => p.Id == rp).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-" : "-",
                ProductName = Prod(it.ProductId),
                PackName = it.PackagingTypeId != null
                    ? db.PackagingTypes.Where(p_ => p_.Id == it.PackagingTypeId).Select(p_ => p_.PackageNameAr).FirstOrDefault() ?? "-"
                    : "-",
                Cartons = it.PlannedCartons,
                QtyKg = it.PlannedQtyKg,
                Date = UiFormat.D(it.ScheduledDate),
                ShiftName = db.Shifts.Where(s => s.Id == (it.SuggestedShiftId ?? 0)).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-"
            });
        }
        return m;
    }
}

/// <summary>
/// §تخطيط أفقي (A4 Landscape) بتصميم نظامنا: ترويسة الشركة ← بطاقة بيانات الخطة ←
/// ملخص العملاء ← جدول البنود مجمعة بالتاريخ ← الإجماليات ← تذييل الاعتماد.
/// </summary>
public static class PlanningPrintDocument
{
    // A4 أفقي: 1169×826 DIU تقريباً
    // §B84/P8: أبعاد A4 أفقي الدقيقة (كانت 1160×820 تقريبية تُنتج هوامش غير متساوية).
    private const double PageW = 1122, PageH = 794, Margin = 26;

    public static FlowDocument Build(PlanningPrintModel m)
    {
        var doc = new FlowDocument
        {
            PageWidth = PageW, PageHeight = PageH,
            PagePadding = new Thickness(Margin),
            ColumnWidth = double.MaxValue,
            FlowDirection = FlowDirection.RightToLeft,
            FontFamily = new FontFamily("Segoe UI, Tahoma"),
            FontSize = 10.5
        };

        doc.Blocks.Add(Header(m));
        doc.Blocks.Add(PlanCard(m));
        var cust = CustomerSummary(m);
        if (cust != null) doc.Blocks.Add(cust);
        doc.Blocks.Add(ItemsTable(m));
        doc.Blocks.Add(Totals(m));
        if (!string.IsNullOrWhiteSpace(m.Notes)) doc.Blocks.Add(Notes(m));
        doc.Blocks.Add(ApprovalFooter(m));
        return doc;
    }

    private static Block Header(PlanningPrintModel m)
    {
        var t = new Table { CellSpacing = 0 };
        t.Columns.Add(new TableColumn { Width = new GridLength(96) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var logo = new TableCell(LogoBlock(m.LogoBytes));
        var name = new TableCell(new Paragraph(new Run(m.CompanyNameAr)
        { FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)) })
        { TextAlignment = TextAlignment.Center });
        var sub = string.Join("  |  ", new[] { m.CompanyNameEn, m.Address, string.IsNullOrWhiteSpace(m.Phone) ? "" : "هاتف: " + m.Phone }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (sub.Length > 0)
            name.Blocks.Add(new Paragraph(new Run(sub)) { FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), TextAlignment = TextAlignment.Center });

        var g = new TableRowGroup();
        g.Rows.Add(new TableRow { Cells = { logo, name } });
        t.RowGroups.Add(g);

        var rule = new BlockUIContainer
        {
            Child = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27)), BorderThickness = new Thickness(0, 0, 0, 2.4), Height = 2, Margin = new Thickness(0, 4, 0, 6) }
        };

        // §القالب المرجعي print_plan.html
        var title = new Paragraph(new Run($"خطة وجدولة تشغيل وإنتاج التمور المعتمدة — {m.PlanTypeAr}")
        { FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.DarkBlue })
        { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 2) };
        var meta = new Paragraph { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        meta.Inlines.Add(new Run($"رقم الخطة: {m.PlanNumber}   ") { FontWeight = FontWeights.Bold });
        meta.Inlines.Add(new Run(m.IsApproved ? "معتمدة ✓" : "مسودة — بانتظار الاعتماد")
        {
            FontWeight = FontWeights.Bold,
            Foreground = m.IsApproved ? new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)) : new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E)),
            Background = m.IsApproved ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)) : new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7))
        });

        var sec = new Section();
        sec.Blocks.Add(t);
        sec.Blocks.Add(rule);
        sec.Blocks.Add(title);
        sec.Blocks.Add(meta);
        return sec;
    }

    private static Block LogoBlock(byte[] logo)
    {
        if (logo is { Length: > 0 })
        {
            try
            {
                var bi = new BitmapImage();
                using var ms = new System.IO.MemoryStream(logo);
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit(); bi.Freeze();
                return new BlockUIContainer { Child = new System.Windows.Controls.Image { Source = bi, Width = 84, Height = 66, Stretch = Stretch.Uniform } };
            }
            catch { }
        }
        // §B84/P8: بديل الشعار كان إيموجي نخلة 🌴 يُطبع في المستند الرسمي — الآن اسم الشركة بخط مميز.
        return new Paragraph(new Run(Services.CompanyIdentity.NameAr) { FontSize = 20, FontWeight = FontWeights.ExtraBold })
            { TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) };
    }

    private static Block PlanCard(PlanningPrintModel m)
    {
        var t = NewTable(6, new[] { 100, 190, 100, 120, 100, Double.NaN });
        void Row(string l1, string v1, string l2, string v2, string l3, string v3)
        {
            var r = new TableRow();
            r.Cells.Add(Lbl(l1)); r.Cells.Add(Val(v1));
            r.Cells.Add(Lbl(l2)); r.Cells.Add(Val(v2));
            r.Cells.Add(Lbl(l3)); r.Cells.Add(Val(v3));
            t.RowGroups[0].Rows.Add(r);
        }
        Row("عنوان الخطة:", m.Title, "الوردية:", m.ShiftName, "خط الإنتاج:", m.LineName);
        Row("من تاريخ:", UiFormat.D(m.StartDate), "إلى تاريخ:", UiFormat.D(m.EndDate), "عدد الأيام:", Days(m).ToString());
        return new Section { Blocks = { t } };
    }

    private static Block? CustomerSummary(PlanningPrintModel m)
    {
        var groups = m.Items.GroupBy(i => i.CustomerName).OrderByDescending(g => g.Sum(x => x.QtyKg)).ToList();
        if (groups.Count <= 1) return null; // عميل واحد — الجدول يكفي

        var t = NewTable(5, new[] { Double.NaN, 96, 96, 96, 96 });
        var head = new TableRow { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)) };
        foreach (var h in new[] { "العميل", "عدد البنود", "الكراتين", "الوزن (كجم)", "نصيبه %" })
            head.Cells.Add(new TableCell(new Paragraph(new Run(h) { FontWeight = FontWeights.Bold, Foreground = Brushes.White, FontSize = 10 })
            { TextAlignment = TextAlignment.Center }) { Padding = new Thickness(4, 4, 4, 4) });
        t.RowGroups[0].Rows.Add(head);

        var totKg = m.Items.Sum(i => i.QtyKg); if (totKg <= 0) totKg = 1;
        foreach (var g in groups)
        {
            var kg = g.Sum(x => x.QtyKg);
            var r = new TableRow();
            r.Cells.Add(Cell(g.Key));
            r.Cells.Add(C(g.Count().ToString()));
            r.Cells.Add(C(g.Sum(x => x.Cartons).ToString("N0")));
            r.Cells.Add(C(UiFormat.N(kg)));
            r.Cells.Add(C($"{kg / totKg * 100:0.#}%"));
            t.RowGroups[0].Rows.Add(r);
        }
        var cap = new Paragraph(new Run("جدول تشغيل حصص العملاء والمنتجات المستهدفة") { FontWeight = FontWeights.Bold, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)) })
        { Margin = new Thickness(0, 8, 0, 3) };
        var sec = new Section();
        sec.Blocks.Add(cap);
        sec.Blocks.Add(t);
        return sec;
    }

    private static Block ItemsTable(PlanningPrintModel m)
    {
        var t = NewTable(10, new double[] { 28, 108, 92, 88, 108, 108, 86, 62, 84, 78 });
        var head = new TableRow { Background = Brushes.DarkBlue };
        // §B84/P8: رؤوس الجدول المطبوع بلا إيموجي (قد يظهر مربعات □ على طابعات الإدارة).
        foreach (var h in new[] { "م", "العميل", "الشحنة", "الدفعة", "الخام", "الصنف التام", "العبوة", "الكراتين", "الوزن (كجم)", "التاريخ" })
            head.Cells.Add(Head(h));
        t.RowGroups[0].Rows.Add(head);

        string? lastDate = null;
        int alt = 0;
        foreach (var it in m.Items) // مرتبة بالتاريخ من النموذج
        {
            var isNewDay = it.Date != lastDate;
            if (isNewDay) alt = 0;
            lastDate = it.Date;
            var bg = isNewDay ? Brushes.MintCream : (alt++ % 2 == 1 ? Brushes.WhiteSmoke : Brushes.White);
            var r = new TableRow { Background = bg };
            // §B84/P8: فاصل اليوم الجديد كان يعرض إيموجي 📅 وحيداً — الآن يعرض التاريخ الفعلي.
            if (isNewDay)
                r.Cells.Add(new TableCell(new Paragraph(new Run("تاريخ التشغيل: " + it.Date.ToString("dd/MM/yyyy"))) { TextAlignment = TextAlignment.Center }) { Padding = new Thickness(2, 3, 2, 3), ColumnSpan = 10, FontSize = 9 });
            t.RowGroups[0].Rows.Add(r);

            var row = new TableRow { Background = bg };
            row.Cells.Add(C(it.RowNo.ToString()));
            row.Cells.Add(CellB(it.CustomerName));
            row.Cells.Add(C(it.ShipmentNo));
            row.Cells.Add(C(it.LotCode));
            row.Cells.Add(Cell(it.RawName));
            row.Cells.Add(CellB(it.ProductName));
            row.Cells.Add(C(it.PackName));
            row.Cells.Add(C(it.Cartons.ToString("N0")));
            row.Cells.Add(C(UiFormat.N(it.QtyKg)));
            row.Cells.Add(C(it.Date));
            t.RowGroups[0].Rows.Add(row);
        }

        var total = new TableRow { Background = Brushes.Khaki, FontWeight = FontWeights.Bold };
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(CellB("الإجمالي"));
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(C(m.Items.Sum(i => i.Cartons).ToString("N0")));
        total.Cells.Add(C(UiFormat.N(m.Items.Sum(i => i.QtyKg))));
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        t.RowGroups[0].Rows.Add(total);

        var cap = new Paragraph(new Run("بنود الخطة مرتبة بالتاريخ — مع تفاصيل الكراتين والقوالب المحسوبة من الإعدادات")
        { FontWeight = FontWeights.Bold, FontSize = 11, Foreground = Brushes.DarkBlue })
        { Margin = new Thickness(0, 8, 0, 3) };
        var sec = new Section();
        sec.Blocks.Add(cap);
        sec.Blocks.Add(t);
        return sec;
    }

    private static Block Totals(PlanningPrintModel m)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)),
            TextAlignment = TextAlignment.Right
        };
        p.Inlines.Add($"الإجمالي: {m.Items.Count:N0} بند | {m.Items.Select(i => i.CustomerName).Distinct().Count()} عميل | ");
        p.Inlines.Add($"{m.Items.Sum(i => i.Cartons):N0} كرتون | {UiFormat.N(m.Items.Sum(i => i.QtyKg))} كجم");
        p.Inlines.Add($"   عبر {Days(m):N0} يوم إنتاج");
        return p;
    }

    private static Block Notes(PlanningPrintModel m) => new Paragraph(new Run($"ملاحظات الخطة: {m.Notes}"))
    { Margin = new Thickness(0, 6, 0, 0), FontSize = 10, Foreground = Brushes.DimGray, TextAlignment = TextAlignment.Right };

    private static Block ApprovalFooter(PlanningPrintModel m)
    {
        var t = NewTable(3, new[] { Double.NaN, Double.NaN, Double.NaN });
        var r = new TableRow();
        foreach (var title in new[] { "مسؤول التخطيط والجدولة", "مدير إدارة الإنتاج", "المدير العام / اعتماد الخطة" })
            r.Cells.Add(new TableCell(new Paragraph(new Run(title) { FontWeight = FontWeights.Bold, FontSize = 10.5 })
            { TextAlignment = TextAlignment.Center })
            {
                Padding = new Thickness(6, 24, 6, 6),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), BorderThickness = new Thickness(0.6)
            });
        t.RowGroups[0].Rows.Add(r);

        var foot = new Paragraph(new Run(m.IsApproved ? "✓ هذه الخطة معتمدة رسمياً وقابلة لإصدار أوامر الإنتاج"
                                                      : "هذه الخطة مسودة — لا تصدر أوامر إنتاج قبل الاعتماد") // §B84/P8: بلا إيموجي
        { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = m.IsApproved ? new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)) : new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E)) })
        { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };

        var sec = new Section { Margin = new Thickness(0, 16, 0, 0) };
        sec.Blocks.Add(t);
        sec.Blocks.Add(foot);
        return sec;
    }

    private static int Days(PlanningPrintModel m) =>
        m.StartDate.HasValue && m.EndDate.HasValue ? Math.Max(1, (m.EndDate.Value.Date - m.StartDate.Value.Date).Days + 1) : 1;

    // ═══ أدوات ═══
    private static Table NewTable(int cols, double[] widths)
    {
        var t = new Table { CellSpacing = 0, BorderBrush = Brushes.DarkBlue, BorderThickness = new Thickness(1) };
        for (int i = 0; i < cols; i++)
            t.Columns.Add(new TableColumn { Width = double.IsNaN(widths[i]) ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i]) });
        t.RowGroups.Add(new TableRowGroup());
        return t;
    }
    private static TableCell Head(string s) => new(new Paragraph(new Run(s) { FontWeight = FontWeights.Bold, FontSize = 10, Foreground = Brushes.White })
    { TextAlignment = TextAlignment.Center }) { Padding = new Thickness(4, 4, 4, 4) };
    private static TableCell Cell(string s) => new(new Paragraph(new Run(s)) { TextAlignment = TextAlignment.Right }) { Padding = new Thickness(5, 2, 5, 2) };
    private static TableCell CellB(string s) => new(new Paragraph(new Run(s) { FontWeight = FontWeights.SemiBold }) { TextAlignment = TextAlignment.Right }) { Padding = new Thickness(5, 2, 5, 2) };
    private static TableCell C(string s) => new(new Paragraph(new Run(s)) { TextAlignment = TextAlignment.Center }) { Padding = new Thickness(3, 2, 3, 2) };
    private static TableCell Lbl(string s) => new(new Paragraph(new Run(s) { FontWeight = FontWeights.Bold, FontSize = 10, Foreground = Brushes.DarkSlateGray })
    { TextAlignment = TextAlignment.Right }) { Padding = new Thickness(5, 2, 5, 2), Background = Brushes.Beige };
    private static TableCell Val(string s) => new(new Paragraph(new Run(s)) { TextAlignment = TextAlignment.Right }) { Padding = new Thickness(5, 2, 5, 2) };
}
