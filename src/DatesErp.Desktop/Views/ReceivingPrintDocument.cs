using System.IO;
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

/// <summary>§نموذج سند الاستلام الرسمي — يُبنى كـ FlowDocument بمقاس A4 للمعاينة قبل الطباعة.</summary>
public class ReceivingPrintModel
{
    public string CompanyNameAr { get; set; } = "شركة التمور";
    public string CompanyNameEn { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public byte[] LogoBytes { get; set; }

    public string DocumentNumber { get; set; } = "";
    public bool IsApproved { get; set; }
    public string CustomerName { get; set; } = "-";
    public string ContainerNumber { get; set; } = "-";
    public string VesselName { get; set; } = "";
    public DateTime? ArrivalDate { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public string EmployeeName { get; set; } = "-";
    public string WarehouseName { get; set; } = "-";
    public string Notes { get; set; } = "";

    public class ItemRow
    {
        public int RowNo { get; set; }
        public string ProductCode { get; set; } = "-";
        public string ProductName { get; set; } = "-";
        public string ReceiptUnit { get; set; } = "-";
        public string PackName { get; set; } = "-";
        public int PackageCount { get; set; }
        public double UnitWeightKg { get; set; }
        public double QtyKg { get; set; }
    }

    public List<ItemRow> Items { get; set; } = new();

    /// <summary>§تحميل بيانات السند كاملة من قاعدة البيانات (بما فيها أسماء العرض) جاهزة للطباعة.</summary>
    public static ReceivingPrintModel Load(DatesErpDbContext db, int shipmentId)
    {
        var ship = db.Shipments.Include(s => s.Items).FirstOrDefault(s => s.Id == shipmentId);
        if (ship == null) return null;

        var co = db.CompanyInfos.OrderBy(x => x.Id).FirstOrDefault();
        var model = new ReceivingPrintModel
        {
            DocumentNumber = ship.DocumentNumber,
            IsApproved = ship.IsApproved,
            CustomerName = db.Customers.Where(c => c.Id == ship.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-",
            ContainerNumber = string.IsNullOrWhiteSpace(ship.ContainerNumber) ? "-" : ship.ContainerNumber,
            VesselName = ship.VesselName ?? "",
            ArrivalDate = ship.ArrivalDate,
            ReceivedDate = ship.ReceivedDate,
            WarehouseName = db.Warehouses.Where(w => w.Id == (ship.ReceivingWarehouseId ?? 0)).Select(w => w.WarehouseNameAr).FirstOrDefault()
                             ?? db.Warehouses.Where(w => w.WarehouseCode == "WRM").Select(w => w.WarehouseNameAr).FirstOrDefault() ?? "مخزن الخام",
            Notes = ship.Notes ?? ""
        };
        if (co != null)
        {
            model.CompanyNameAr = co.CompanyNameAr ?? model.CompanyNameAr;
            model.CompanyNameEn = co.CompanyNameEn ?? "";
            model.Address = co.Address ?? "";
            model.Phone = co.Phone ?? "";
            model.LogoBytes = co.LogoBytes;
        }
        var emp = ship.ReceivedBy != null
            ? db.Employees.Where(e => e.Id == ship.ReceivedBy).Select(e => e.FullName).FirstOrDefault()
            : null;
        model.EmployeeName = emp ?? "-";

        int n = 1;
        foreach (var it in ship.Items)
        {
            model.Items.Add(new ItemRow
            {
                RowNo = n++,
                ProductCode = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductCode).FirstOrDefault() ?? "-",
                ProductName = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                ReceiptUnit = it.ReceiptUnit ?? "-",
                PackName = it.PackagingTypeId != null
                    ? db.PackagingTypes.Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-"
                    : "-",
                PackageCount = it.PackageCount,
                UnitWeightKg = it.UnitWeightKg,
                QtyKg = it.TotalWeightKg
            });
        }
        return model;
    }
}

/// <summary>
/// §بِناء سند الاستلام الرسمي (A4 عمودي) كـ FlowDocument للمعاينة قبل الطباعة:
/// ترويسة الشركة (شعار + اسم + عنوان) ← بيانات السند ← جدول البنود ← الإجماليات ← التوقيعات.
/// </summary>
public static class ReceivingPrintDocument
{
    // §B84/P8: عرض A4 عمودي الدقيق (210مم = 793.7 ← 794، كان 820 يفيض عن الصفحة).
    private const double PageWidthA4 = 794;
    private const double Margin = 28;

    public static FlowDocument Build(ReceivingPrintModel m)
    {
        var doc = new FlowDocument
        {
            PageWidth = PageWidthA4,
            PageHeight = 1122, // §B84/P8: ارتفاع A4 الدقيق (297مم، كان 1160)
            PagePadding = new Thickness(Margin),
            ColumnWidth = double.MaxValue,
            FlowDirection = FlowDirection.RightToLeft,
            FontFamily = new FontFamily("Segoe UI, Tahoma"),
            FontSize = 11
        };

        doc.Blocks.Add(BuildHeader(m));
        doc.Blocks.Add(BuildTitle(m));
        doc.Blocks.Add(BuildInfoGrid(m));
        doc.Blocks.Add(BuildItemsTable(m));
        doc.Blocks.Add(BuildTotals(m));
        if (!string.IsNullOrWhiteSpace(m.Notes)) doc.Blocks.Add(BuildNotes(m));
        doc.Blocks.Add(BuildSignatures());
        return doc;
    }

    // ── الترويسة: شعار + اسم الشركة + عنوان وهاتف ──
    private static Block BuildHeader(ReceivingPrintModel m)
    {
        var t = new Table { CellSpacing = 0 };
        t.Columns.Add(new TableColumn { Width = new GridLength(90) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var logoCell = new TableCell(BuildLogo(m.LogoBytes))
        {
            Padding = new Thickness(0, 0, 0, 4)
        };
        var nameCell = new TableCell(new Paragraph(new Run(m.CompanyNameAr)
        {
            FontSize = 19, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D))
        }) { TextAlignment = TextAlignment.Center });
        if (!string.IsNullOrWhiteSpace(m.CompanyNameEn) || !string.IsNullOrWhiteSpace(m.Address) || !string.IsNullOrWhiteSpace(m.Phone))
        {
            var sub = new Paragraph(new Run(string.Join("  |  ", new[]
            {
                m.CompanyNameEn, m.Address, string.IsNullOrWhiteSpace(m.Phone) ? "" : "هاتف: " + m.Phone
            }.Where(x => !string.IsNullOrWhiteSpace(x)))))
            { FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), TextAlignment = TextAlignment.Center };
            nameCell.Blocks.Add(sub);
        }
        var headerGroup = new TableRowGroup();
        headerGroup.Rows.Add(new TableRow { Cells = { logoCell, nameCell } });
        t.RowGroups.Add(headerGroup);

        var border = new BlockUIContainer
        {
            Child = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27)), BorderThickness = new Thickness(0, 0, 0, 2.2),
                Margin = new Thickness(0, 4, 0, 8), Height = 2
            },
            Margin = new Thickness(0, 6, 0, 6)
        };
        var sp = new Section();
        sp.Blocks.Add(t);
        sp.Blocks.Add(border);
        return sp;
    }

    private static Block BuildLogo(byte[] logo)
    {
        if (logo is { Length: > 0 })
        {
            try
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = LoadImage(logo),
                    Width = 78, Height = 62, Stretch = Stretch.Uniform
                };
                return new BlockUIContainer { Child = img };
            }
            catch { /* شعار تالف — نصير للاسم النصي */ }
        }
        // §B84/P8: بديل الشعار كان إيموجي نخلة 🌴 يُطبع في المستند الرسمي — الآن اسم الشركة بخط مميز.
        return new Paragraph(new Run(Services.CompanyIdentity.NameAr) { FontSize = 20, FontWeight = FontWeights.ExtraBold })
            { TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) };
    }

    private static ImageSource LoadImage(byte[] bytes)
    {
        var bi = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    // ── العنوان + رقم السند + ختم الحالة ──
    private static Block BuildTitle(ReceivingPrintModel m)
    {
        var p = new Paragraph
        {
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.DarkBlue
        };
        p.Inlines.Add(new Run("أمر وسند استلام شحنة تمور خام"));
        p.Inlines.Add(new Run("   ( Receiving Voucher )") { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), FontWeight = FontWeights.Normal });

        var meta = new Paragraph { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        meta.Inlines.Add(new Run($"رقم السند: {m.DocumentNumber}   ") { FontWeight = FontWeights.Bold });
        meta.Inlines.Add(new Run(m.IsApproved ? "معتمد ✓" : "مسودة — غير معتمد")
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = m.IsApproved ? new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)) : new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E)),
            Background = m.IsApproved ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)) : new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7)),
            TextDecorations = null
        });
        if (m.IsApproved)
            meta.Inlines.Add(new Run($"   تاريخ الاعتماد مطابق لتاريخ الاستلام: {UiFormat.D(m.ReceivedDate)}") { FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)) });

        var sec = new Section();
        sec.Blocks.Add(p);
        sec.Blocks.Add(meta);
        return sec;
    }

    // ── شبكة بيانات السند (2×4) ──
    private static Block BuildInfoGrid(ReceivingPrintModel m)
    {
        var t = NewTable(4, new double[] { 96, 190, 96, 190 });
        void AddRow(string l1, string v1, string l2, string v2)
        {
            var r = new TableRow();
            r.Cells.Add(LabelCell(l1));
            r.Cells.Add(ValueCell(v1));
            r.Cells.Add(LabelCell(l2));
            r.Cells.Add(ValueCell(v2));
            t.RowGroups[0].Rows.Add(r);
        }
        AddRow("العميل المورد:", m.CustomerName, "رقم الحاوية/الشاحنة:", m.ContainerNumber);
        AddRow("تاريخ الوصول:", UiFormat.D(m.ArrivalDate), "تاريخ الاستلام:", UiFormat.D(m.ReceivedDate));
        AddRow("موظف الاستلام:", m.EmployeeName, "مخزن الاستلام:", m.WarehouseName);
        if (!string.IsNullOrWhiteSpace(m.VesselName))
            AddRow("الناقل/الباخرة:", m.VesselName, "", "");
        return WrapCard(t);
    }

    // ── جدول البنود ──
    private static Block BuildItemsTable(ReceivingPrintModel m)
    {
        var t = NewTable(7, new[] { 30, 90, Double.NaN /*اسم الصنف يأخذ الباقي*/, 92, 62, 84, 96 });
        // رأس الجدول
        var head = new TableRow { Background = Brushes.DarkBlue };
        foreach (var h in new[] { "م", "رقم الصنف", "اسم الصنف الخام", "وحدة الاستلام", "العدد", "وزن العبوة", "الإجمالي (كجم)" })
            head.Cells.Add(HeadCell(h));
        t.RowGroups[0].Rows.Add(head);

        int alt = 0;
        foreach (var it in m.Items)
        {
            var r = new TableRow { Background = alt++ % 2 == 1 ? Brushes.WhiteSmoke : Brushes.White };
            r.Cells.Add(CenterCell(it.RowNo.ToString()));
            r.Cells.Add(CenterCell(it.ProductCode));
            r.Cells.Add(Cell(it.ProductName));
            r.Cells.Add(CenterCell(it.ReceiptUnit));
            r.Cells.Add(CenterCell(it.PackageCount.ToString()));
            r.Cells.Add(CenterCell(UiFormat.N(it.UnitWeightKg)));
            r.Cells.Add(CenterCell(UiFormat.N(it.QtyKg)));
            t.RowGroups[0].Rows.Add(r);
        }

        // صف الإجمالي
        var total = new TableRow { Background = Brushes.Khaki, FontWeight = FontWeights.Bold };
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(Cell("الإجمالي"));
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(CenterCell(m.Items.Sum(i => i.PackageCount).ToString()));
        total.Cells.Add(new TableCell(new Paragraph(new Run(""))));
        total.Cells.Add(CenterCell(UiFormat.N(m.Items.Sum(i => i.QtyKg))));
        t.RowGroups[0].Rows.Add(total);

        return new Section { Margin = new Thickness(0, 8, 0, 0), Blocks = { t } };
    }

    // ── صندوق الإجماليات الموMid ──
    private static Block BuildTotals(ReceivingPrintModel m)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)),
            TextAlignment = TextAlignment.Right
        };
        p.Inlines.Add(new Run("إجمالي وزن الشحنة: "));
        p.Inlines.Add(new Run($"{UiFormat.N(m.Items.Sum(i => i.QtyKg))} كجم") { FontSize = 14 });
        p.Inlines.Add(new Run($"     |     إجمالي عدد العبوات: {m.Items.Sum(i => i.PackageCount):N0}"));
        p.Inlines.Add(new Run($"     |     عدد البنود: {m.Items.Count:N0}"));
        return p;
    }

    private static Block BuildNotes(ReceivingPrintModel m) => new Paragraph(new Run($"البيان: {m.Notes}"))
    {
        Margin = new Thickness(0, 8, 0, 0),
        FontSize = 10.5,
        Foreground = Brushes.DimGray,
        TextAlignment = TextAlignment.Right
    };

    // ── التوقيعات ──
    private static Block BuildSignatures()
    {
        var t = NewTable(3, new[] { Double.NaN, Double.NaN, Double.NaN });
        var r = new TableRow();
        foreach (var title in new[] { "موظف الاستلام", "مسؤول فحص الجودة", "أمين مخزن المواد الخام", "مدير المصنع / الاعتماد" })
        {
            var cell = new TableCell(new Paragraph(new Run(title) { FontWeight = FontWeights.Bold, FontSize = 10.5 })
            { TextAlignment = TextAlignment.Center })
            {
                Padding = new Thickness(6, 26, 6, 6),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                BorderThickness = new Thickness(0.6)
            };
            r.Cells.Add(cell);
        }
        t.RowGroups[0].Rows.Add(r);
        return new Section { Margin = new Thickness(0, 18, 0, 0), Blocks = { t } };
    }

    // ════ أدوات بناء الجداول ════
    private static Table NewTable(int cols, double[] widths)
    {
        var t = new Table { CellSpacing = 0, BorderBrush = Brushes.DarkBlue, BorderThickness = new Thickness(1) };
        for (int i = 0; i < cols; i++)
            t.Columns.Add(new TableColumn { Width = double.IsNaN(widths[i]) ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i]) });
        t.RowGroups.Add(new TableRowGroup());
        return t;
    }

    private static TableCell HeadCell(string text) => new(new Paragraph(new Run(text)
    { FontWeight = FontWeights.Bold, FontSize = 10.5, Foreground = Brushes.White })
    { TextAlignment = TextAlignment.Center })
    { Padding = new Thickness(4, 5, 4, 5), TextAlignment = TextAlignment.Center };

    private static TableCell Cell(string text) => new(new Paragraph(new Run(text)) { TextAlignment = TextAlignment.Right })
    { Padding = new Thickness(5, 3, 5, 3) };

    private static TableCell CenterCell(string text) => new(new Paragraph(new Run(text)) { TextAlignment = TextAlignment.Center })
    { Padding = new Thickness(4, 3, 4, 3) };

    private static TableCell LabelCell(string text) => new(new Paragraph(new Run(text)
    { FontWeight = FontWeights.Bold, FontSize = 10.5, Foreground = Brushes.DarkSlateGray }))
    { Padding = new Thickness(5, 3, 5, 3), Background = Brushes.Beige };

    private static TableCell ValueCell(string text) => new(new Paragraph(new Run(text)) { TextAlignment = TextAlignment.Right })
    { Padding = new Thickness(5, 3, 5, 3) };

    private static Block WrapCard(Table t) => new Section
    {
        Blocks = { t },
        Margin = new Thickness(0, 2, 0, 0)
    };
}
