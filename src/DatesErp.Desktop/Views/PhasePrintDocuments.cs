using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §نمط خطط الإنتاج/الاستلام للمراحل التالية (أوامر/تنفيذ/جودة/تام/تسليم):
/// نموذج مستند رسمي موحّد — ترويسة شركة + شبكة بيانات + جداول + إجماليات + توقيعات،
/// يُخرج FlowDocument للمعاينة/الطباعة وPDF احترافي بصفحات مرقمة ورأس متكرر.
/// </summary>
public class PhaseDocModel
{
    public string DocTitle { get; set; } = "";
    public string DocNo { get; set; } = "";
    public string StatusAr { get; set; } = "";
    public List<(string Label, string Value)> Info { get; set; } = new();
    public string[] Columns { get; set; } = Array.Empty<string>();
    public List<object[]> Rows { get; set; } = new();
    public List<(string Label, string Value)> Totals { get; set; } = new();
    /// <summary>§عنوان قسم الجدول الرئيسي — لكل مستند عنوانه في القالب المرجعي.</summary>
    public string MainTitle { get; set; } = "بنود المستند";
    public string SecondTitle { get; set; } = "";
    public string[] SecondColumns { get; set; } = Array.Empty<string>();
    public List<object[]> SecondRows { get; set; } = new();
    public List<string> Signatures { get; set; } = new();
    public string Notes { get; set; } = "";
    /// <summary>§المستندات الرسمية تُطبع A4 عمودياً؛ تُجعل أفقية فقط للجداول العريضة جداً.</summary>
    public bool Landscape { get; set; }
    /// <summary>§بيان يُطبع أسفل التوقيعات (رقم الأمر/الخطة + وقت الإصدار).</summary>
    public string FooterNote { get; set; } = "";
}

public static class PhasePrint
{
    private static readonly Brush Navy = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A));
    private static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27));

    // ═══════════════════════════ FlowDocument للمعاينة والطباعة ═══════════════════════════

    // ═══ ألوان الهوية (مطابقة للنموذج المرجعي) ═══
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D));
    private static readonly Brush GreenDark = new SolidColorBrush(Color.FromRgb(0x0B, 0x3D, 0x1F));
    private static readonly Brush BadgeBg = new SolidColorBrush(Color.FromRgb(0xF0, 0xFD, 0xF4));
    private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly Brush RuleLine = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
    private static readonly Brush AmberBg = new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7));
    private static readonly Brush AmberFg = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));

    /// <summary>
    /// §إعادة تصميم كاملة على النموذج المرجعي:
    /// A4 عمودي · ترويسة بثلاثة أعمدة (شركة | شارة عنوان المستند | بيانات) · بطاقة بيانات
    /// بشبكة أربعة أعمدة · جدول برأس متدرّج وصفوف مخططة وصف إجماليات كهرماني ·
    /// صناديق توقيع بدور وخط منقّط · تذييل بالبيان ووقت الإصدار.
    /// </summary>
    public static FlowDocument Build(PhaseDocModel m)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            FlowDirection = FlowDirection.RightToLeft, // §B84/P5: الجداول العربية كانت تُرسم LTR
            PagePadding = new Thickness(m.Landscape ? 30 : 38),
            // A4 عمودي = 794×1122 نقطة؛ أفقي للعريضة فقط
            PageWidth = m.Landscape ? 1122 : 794,
            PageHeight = m.Landscape ? 794 : 1122
        };

        // ── 1) الترويسة: ثلاثة أعمدة ──
        var header = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 4) };
        header.Columns.Add(new TableColumn { Width = new GridLength(1.4, GridUnitType.Star) });
        header.Columns.Add(new TableColumn { Width = new GridLength(1.8, GridUnitType.Star) });
        header.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });

        // العمود الأيمن: الشركة
        var co = new StackPanel();
        var logo = Services.CompanyIdentity.GetLogo(64);
        if (logo != null)
            co.Children.Add(new Image { Source = logo, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 3) });
        co.Children.Add(Tb(Services.CompanyIdentity.NameAr, 14, FontWeights.ExtraBold, Green, TextAlignment.Right));
        if (!string.IsNullOrWhiteSpace(Services.CompanyIdentity.Address))
            co.Children.Add(Tb(Services.CompanyIdentity.Address, 8.5, FontWeights.Normal, Muted, TextAlignment.Right));

        // العمود الأوسط: شارة عنوان المستند
        var mid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        mid.Children.Add(new Border
        {
            Background = BadgeBg,
            BorderBrush = Green,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 5, 18, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = Tb(m.DocTitle, 17, FontWeights.ExtraBold, Green, TextAlignment.Center)
        });
        if (!string.IsNullOrEmpty(m.DocNo))
            mid.Children.Add(Tb(m.DocNo, 12.5, FontWeights.Bold, Ink, TextAlignment.Center, new Thickness(0, 5, 0, 0)));

        // العمود الأيسر: بيانات الإصدار والحالة
        var right = new StackPanel();
        right.Children.Add(Tb($"الحالة: {m.StatusAr}", 10, FontWeights.Bold, Ink, TextAlignment.Left));
        right.Children.Add(Tb($"تاريخ الإصدار: {DateTime.Now:dd/MM/yyyy}", 9, FontWeights.Normal, Muted, TextAlignment.Left));
        right.Children.Add(Tb($"{DateTime.Now:HH:mm}", 9, FontWeights.Normal, Muted, TextAlignment.Left));

        var hr = new TableRow();
        hr.Cells.Add(WrapCell(co)); hr.Cells.Add(WrapCell(mid)); hr.Cells.Add(WrapCell(right));
        var hg = new TableRowGroup(); hg.Rows.Add(hr); header.RowGroups.Add(hg);
        doc.Blocks.Add(header);

        // خط فاصل تحت الترويسة
        doc.Blocks.Add(new Paragraph(new Run(" "))
        {
            BorderBrush = Green,
            BorderThickness = new Thickness(0, 0, 0, 2.5),
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 2
        });

        // ── 2) بطاقة البيانات: شبكة أربعة أعمدة ──
        if (m.Info.Count > 0)
        {
            var card = new Table
            {
                CellSpacing = 0,
                Background = CardBg,
                BorderBrush = RuleLine,
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 0, 14),
                Padding = new Thickness(10)
            };
            for (int i = 0; i < 4; i++) card.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var cg = new TableRowGroup();
            for (int i = 0; i < m.Info.Count; i += 2)
            {
                var tr = new TableRow();
                tr.Cells.Add(MetaCell(m.Info[i].Label, m.Info[i].Value));
                if (i + 1 < m.Info.Count) tr.Cells.Add(MetaCell(m.Info[i + 1].Label, m.Info[i + 1].Value));
                else tr.Cells.Add(MetaCell("", ""));
                cg.Rows.Add(tr);
            }
            card.RowGroups.Add(cg);
            doc.Blocks.Add(card);
        }

        // ── 3) الجدول الرئيسي ──
        if (m.Columns.Length > 0)
        {
            doc.Blocks.Add(SectionTitle(string.IsNullOrWhiteSpace(m.MainTitle) ? "بنود المستند" : m.MainTitle));
            doc.Blocks.Add(Table(m.Columns, m.Rows, m.Totals));
        }

        // ── 4) جدول ثانٍ ──
        if (!string.IsNullOrWhiteSpace(m.SecondTitle) && m.SecondColumns.Length > 0)
        {
            doc.Blocks.Add(SectionTitle(m.SecondTitle));
            doc.Blocks.Add(Table(m.SecondColumns, m.SecondRows, null));
        }

        // ── 5) الإجماليات كصفوف مميزة (لا فقرات نصية) ──
        if (m.Totals is { Count: > 0 })
        {
            var tt = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 0) };
            tt.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            tt.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var tg = new TableRowGroup();
            for (int i = 0; i < m.Totals.Count; i += 2)
            {
                var tr = new TableRow { Background = AmberBg };
                tr.Cells.Add(TotalCell(m.Totals[i].Label, m.Totals[i].Value));
                tr.Cells.Add(i + 1 < m.Totals.Count ? TotalCell(m.Totals[i + 1].Label, m.Totals[i + 1].Value) : TotalCell("", ""));
                tg.Rows.Add(tr);
            }
            tt.RowGroups.Add(tg);
            doc.Blocks.Add(tt);
        }

        // ── 6) الملاحظات ──
        if (!string.IsNullOrWhiteSpace(m.Notes))
            doc.Blocks.Add(new Paragraph(new Run("ملاحظات: " + m.Notes))
            { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)), Margin = new Thickness(0, 10, 0, 0) });

        // ── 7) التوقيعات: صناديق بدور وخط منقّط ──
        if (m.Signatures.Count > 0)
        {
            var sig = new Table { CellSpacing = 0, Margin = new Thickness(0, 30, 0, 0) };
            for (int i = 0; i < m.Signatures.Count; i++) sig.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var sg = new TableRowGroup();
            var tr = new TableRow();
            foreach (var role in m.Signatures)
            {
                var box = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
                box.Children.Add(Tb(role, 10.5, FontWeights.ExtraBold, Green, TextAlignment.Center));
                box.Children.Add(new Border
                {
                    BorderBrush = Muted,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 30, 0, 0),
                    Child = Tb("التوقيع", 8.5, FontWeights.Normal, Muted, TextAlignment.Center, new Thickness(0, 3, 0, 0))
                });
                tr.Cells.Add(WrapCell(box));
            }
            sg.Rows.Add(tr);
            sig.RowGroups.Add(sg);
            doc.Blocks.Add(sig);
        }

        // ── 8) التذييل ──
        var footText = string.IsNullOrWhiteSpace(m.FooterNote)
            ? $"{m.DocTitle} {m.DocNo} — طُبع {DateTime.Now:dd/MM/yyyy HH:mm}"
            : m.FooterNote;
        doc.Blocks.Add(new Paragraph(new Run(footText))
        {
            FontSize = 8.5,
            Foreground = Muted,
            TextAlignment = TextAlignment.Center,
            BorderBrush = RuleLine,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(0, 5, 0, 0)
        });
        if (!string.IsNullOrWhiteSpace(Services.CompanyIdentity.ReportFooter))
            doc.Blocks.Add(new Paragraph(new Run(Services.CompanyIdentity.ReportFooter))
            { FontSize = 8.5, Foreground = Muted, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });

        return doc;
    }

    // ═══ عناصر التصميم ═══
    private static Paragraph Txt(string text, double size, FontWeight weight, Brush color,
        TextAlignment align, Thickness? margin = null)
        => new(new Run(text ?? ""))
        {
            FontSize = size, FontWeight = weight, Foreground = color, TextAlignment = align,
            Margin = margin ?? new Thickness(0)
        };

    /// <summary>نسخة TextBlock — تُستخدم داخل StackPanel (Paragraph ليس UIElement).</summary>
    private static TextBlock Tb(string text, double size, FontWeight weight, Brush color,
        TextAlignment align, Thickness? margin = null)
        => new()
        {
            Text = text ?? "",
            FontSize = size, FontWeight = weight, Foreground = color, TextAlignment = align,
            Margin = margin ?? new Thickness(0)
        };

    private static TableCell WrapCell(UIElement child)
        => new(new BlockUIContainer(child)) { Padding = new Thickness(4, 2, 4, 2) };

    private static TableCell MetaCell(string label, string value)
    {
        var sp = new StackPanel();
        if (!string.IsNullOrWhiteSpace(label))
            sp.Children.Add(Tb(label, 8.5, FontWeights.SemiBold, Muted, TextAlignment.Right));
        sp.Children.Add(Tb(value ?? "—", 10.5, FontWeights.Bold, Ink, TextAlignment.Right));
        return new TableCell(new BlockUIContainer(sp)) { Padding = new Thickness(4, 3, 4, 3) };
    }

    private static TableCell TotalCell(string label, string value)
    {
        var sp = new StackPanel();
        if (!string.IsNullOrWhiteSpace(label))
            sp.Children.Add(Tb(label, 9, FontWeights.SemiBold, AmberFg, TextAlignment.Right));
        sp.Children.Add(Tb(value ?? "", 11.5, FontWeights.ExtraBold, AmberFg, TextAlignment.Right));
        return new TableCell(new BlockUIContainer(sp))
        { Padding = new Thickness(8, 5, 8, 5), BorderBrush = AmberFg, BorderThickness = new Thickness(0.5) };
    }

    private static Paragraph SectionTitle(string text)
        => new(new Run(text))
        {
            FontSize = 12.5, FontWeight = FontWeights.ExtraBold, Foreground = Green,
            Margin = new Thickness(0, 4, 0, 6)
        };

    private static TableCell Cell(string text, bool label)
    {
        return new TableCell(new Paragraph(new Run(text ?? ""))
        {
            FontWeight = label ? FontWeights.Bold : FontWeights.Normal,
            Background = label ? new SolidColorBrush(Color.FromRgb(0xEC, 0xE9, 0xD8)) : Brushes.White,
            FontSize = 10.5
        });
    }

    private static Table Table(string[] columns, List<object[]> rows, List<(string, string)> totals)
    {
        var t = new Table { BorderBrush = Green, BorderThickness = new Thickness(1.5), CellSpacing = 0 };
        var head = new TableRow { Background = Green };
        foreach (var c in columns)
        {
            head.Cells.Add(new TableCell(new Paragraph(new Run(c ?? ""))
            {
                Foreground = Brushes.White, FontWeight = FontWeights.ExtraBold, FontSize = 10,
                TextAlignment = TextAlignment.Center
            })
            { BorderBrush = Green, BorderThickness = new Thickness(0.75), Padding = new Thickness(4, 6, 4, 6) });
            t.Columns.Add(new TableColumn());
        }
        var hg = new TableRowGroup();
        hg.Rows.Add(head);
        t.RowGroups.Add(hg);

        var bg = new TableRowGroup();
        bool zebra = false;
        foreach (var r in rows)
        {
            zebra = !zebra;
            var tr = new TableRow { Background = zebra ? CardBg : Brushes.White };
            for (int c = 0; c < columns.Length; c++)
                tr.Cells.Add(new TableCell(new Paragraph(new Run(c < r.Length ? Format(r[c]) : ""))
                { FontSize = 9.5, Foreground = Ink, TextAlignment = TextAlignment.Center })
                { BorderBrush = RuleLine, BorderThickness = new Thickness(0.5), Padding = new Thickness(4, 4, 4, 4) });
            bg.Rows.Add(tr);
        }
        t.RowGroups.Add(bg);

        if (totals is { Count: > 0 })
        {
            var tg = new TableRowGroup();
            var tr = new TableRow { Background = AmberBg };
            tr.Cells.Add(new TableCell(new Paragraph(new Run("الإجمالي"))
            { FontWeight = FontWeights.ExtraBold, FontSize = 10, Foreground = AmberFg, TextAlignment = TextAlignment.Center })
            { BorderBrush = AmberFg, BorderThickness = new Thickness(0.75), Padding = new Thickness(4, 5, 4, 5) });
            for (int c = 1; c < columns.Length; c++)
            {
                var sum = SumColumn(rows, c);
                tr.Cells.Add(new TableCell(new Paragraph(new Run(sum is double s ? s.ToString("N1") : ""))
                { FontWeight = FontWeights.ExtraBold, FontSize = 10, Foreground = AmberFg, TextAlignment = TextAlignment.Center })
                { BorderBrush = AmberFg, BorderThickness = new Thickness(0.75), Padding = new Thickness(4, 5, 4, 5) });
            }
            tg.Rows.Add(tr);
            t.RowGroups.Add(tg);
        }
        return t;
    }

    private static double? SumColumn(List<object[]> rows, int c)
    {
        double sum = 0; bool any = false;
        foreach (var r in rows)
        {
            if (c >= r.Length) continue;
            var v = r[c];
            if (v is double d) { sum += d; any = true; }
            else if (v is int i) { sum += i; any = true; }
        }
        return any ? sum : null;
    }

    private static string Format(object v) => v switch
    {
        null => "",
        double d => d.ToString("N2", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("dd/MM/yyyy"),
        _ => v.ToString() ?? ""
    };

    // ═══════════════════════════ PDF احترافي ═══════════════════════════

    public static void ExportPdf(PhaseDocModel m, string path)
    {
        var doc = new PdfDocument { Info = { Title = $"{m.DocTitle} {m.DocNo}" } };
        double margin = 18, rowH = 16;
        var titleFont = new XFont("Segoe UI", 13, XFontStyleEx.Bold);
        var metaFont = new XFont("Segoe UI", 8, XFontStyleEx.Regular);
        var headFont = new XFont("Segoe UI", 8.5, XFontStyleEx.Bold);
        var cellFont = new XFont("Segoe UI", 8, XFontStyleEx.Regular);

        int cols = Math.Max(1, m.Columns.Length);
        var weights = new double[cols];
        for (int c = 0; c < cols; c++)
        {
            double w = (m.Columns[c] ?? "").Length + 2;
            foreach (var r in m.Rows)
            {
                double len = c < r.Length ? Format(r[c]).Length : 0;
                if (len > w) w = len;
            }
            weights[c] = Math.Min(w, 42);
        }
        double wSum = Math.Max(1, weights.Sum(x => x == 0 ? 1 : x));

        PdfPage page = null; XGraphics gfx = null; double y = 0;

        void NewPage()
        {
            gfx?.Dispose();
            page = doc.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            page.Orientation = PdfSharp.PageOrientation.Landscape;
            gfx = XGraphics.FromPdfPage(page);
            double w = page.Width.Point - margin * 2;
            gfx.DrawString(Services.CompanyIdentity.NameAr, new XFont("Segoe UI", 10.5, XFontStyleEx.Bold), XBrushes.Black,
                new XPoint(page.Width.Point - margin, margin + 6), XStringFormats.TopRight);
            gfx.DrawString($"{m.DocTitle} — {m.DocNo}", titleFont, XBrushes.DarkSlateBlue,
                new XPoint(page.Width.Point / 2, margin + 4), XStringFormats.TopCenter);
            gfx.DrawString($"الحالة: {m.StatusAr} | الإصدار: {DateTime.Now:dd/MM/yyyy HH:mm}", metaFont, XBrushes.Gray,
                new XPoint(margin, margin + 6), XStringFormats.TopLeft);
            gfx.DrawLine(new XPen(XColors.DarkSlateBlue, 1.1), margin, margin + 30, page.Width.Point - margin, margin + 30);
            y = margin + 36;

            // شبكة البيانات
            int half = (m.Info.Count + 1) / 2;
            for (int i = 0; i < m.Info.Count; i += 2)
            {
                string line = $"{m.Info[i].Label}: {m.Info[i].Value}" + (i + 1 < m.Info.Count ? $"     ||     {m.Info[i + 1].Label}: {m.Info[i + 1].Value}" : "");
                gfx.DrawString(line, cellFont, XBrushes.Black, new XPoint(page.Width.Point - margin, y + 10), XStringFormats.TopRight);
                y += 13;
            }
            y += 4;

            // §عنوان قسم الجدول الرئيسي — كان يُرسم في المعاينة فقط ويغيب عن PDF
            if (!string.IsNullOrWhiteSpace(m.MainTitle))
            {
                gfx.DrawString(m.MainTitle, new XFont("Segoe UI", 10.5, XFontStyleEx.Bold), XBrushes.DarkGreen,
                    new XPoint(page.Width.Point - margin, y + 11), XStringFormats.TopRight);
                y += 16;
            }

            double x = margin;
            for (int c = 0; c < cols; c++)
            {
                double cw = (weights[c] == 0 ? 1 : weights[c]) / wSum * w;
                var rect = new XRect(x, y, cw, rowH);
                gfx.DrawRectangle(XBrushes.DarkSlateBlue, rect);
                gfx.DrawString(m.Columns[c] ?? "", headFont, XBrushes.White, new XRect(rect.X + 2, rect.Y, rect.Width - 4, rect.Height), XStringFormats.CenterRight);
                x += cw;
            }
            y += rowH;
        }

        NewPage();
        double pageW = page.Width.Point - margin * 2;
        bool zebra = false;
        foreach (var r in m.Rows)
        {
            if (y + rowH > page.Height.Point - margin - 14) NewPage();
            zebra = !zebra;
            double x = margin;
            for (int c = 0; c < cols; c++)
            {
                double cw = (weights[c] == 0 ? 1 : weights[c]) / wSum * pageW;
                var rect = new XRect(x, y, cw, rowH);
                if (zebra) gfx.DrawRectangle(XBrushes.WhiteSmoke, rect);
                gfx.DrawRectangle(new XPen(XColors.LightGray, 0.4), rect);
                string val = c < r.Length ? Format(r[c]) : "";
                if (val.Length > 46) val = val[..46] + "…";
                gfx.DrawString(val, cellFont, XBrushes.Black, new XRect(rect.X + 2, rect.Y, rect.Width - 4, rect.Height), XStringFormats.CenterRight);
                x += cw;
            }
            y += rowH;
        }

        // صف الإجماليات
        if (y + rowH > page.Height.Point - margin - 14) NewPage();
        double x2 = margin;
        for (int c = 0; c < cols; c++)
        {
            double cw = (weights[c] == 0 ? 1 : weights[c]) / wSum * pageW;
            var rect = new XRect(x2, y, cw, rowH);
            gfx.DrawRectangle(XBrushes.LightSteelBlue, rect);
            string val = c == 0 ? "الإجمالي" : SumColumn(m.Rows, c) is double s ? s.ToString("N1") : "";
            gfx.DrawString(val, headFont, XBrushes.Black, new XRect(rect.X + 2, rect.Y, rect.Width - 4, rect.Height), XStringFormats.CenterRight);
            x2 += cw;
        }
        y += rowH + 6;

        foreach (var t in m.Totals)
        {
            if (y + 13 > page.Height.Point - margin - 14) NewPage();
            gfx.DrawString($"{t.Label}: {t.Value}", headFont, XBrushes.DarkGreen, new XPoint(page.Width.Point - margin, y + 10), XStringFormats.TopRight);
            y += 13;
        }

        // §الجدول الثاني (المواد المساعدة / التوقفات / المعايير) — كان يُرسم في المعاينة
        // ويغيب تماماً عن PDF، فينقص المستند المطبوع قسماً كاملاً.
        if (!string.IsNullOrWhiteSpace(m.SecondTitle) && m.SecondColumns.Length > 0 && m.SecondRows.Count > 0)
        {
            if (y + 60 > page.Height.Point - margin - 14) NewPage(); else y += 12;
            gfx.DrawString(m.SecondTitle, new XFont("Segoe UI", 10.5, XFontStyleEx.Bold), XBrushes.DarkGreen,
                new XPoint(page.Width.Point - margin, y + 11), XStringFormats.TopRight);
            y += 16;

            int cols2 = m.SecondColumns.Length;
            double w2 = page.Width.Point - margin * 2;
            double xh = margin;
            for (int c = 0; c < cols2; c++)
            {
                double cw = w2 / cols2;
                var rect = new XRect(xh, y, cw, rowH);
                gfx.DrawRectangle(XBrushes.DarkSlateBlue, rect);
                gfx.DrawString(m.SecondColumns[c] ?? "", headFont, XBrushes.White,
                    new XRect(rect.X + 2, rect.Y, rect.Width - 4, rect.Height), XStringFormats.CenterRight);
                xh += cw;
            }
            y += rowH;

            bool zebra2 = false;
            foreach (var r in m.SecondRows)
            {
                if (y + rowH > page.Height.Point - margin - 14) NewPage();
                zebra2 = !zebra2;
                double xc = margin;
                for (int c = 0; c < cols2; c++)
                {
                    double cw = w2 / cols2;
                    var rect = new XRect(xc, y, cw, rowH);
                    if (zebra2) gfx.DrawRectangle(XBrushes.WhiteSmoke, rect);
                    gfx.DrawRectangle(new XPen(XColors.LightGray, 0.4), rect);
                    string val = c < r.Length ? Format(r[c]) : "";
                    if (val.Length > 46) val = val[..46] + "…";
                    gfx.DrawString(val, cellFont, XBrushes.Black,
                        new XRect(rect.X + 2, rect.Y, rect.Width - 4, rect.Height), XStringFormats.CenterRight);
                    xc += cw;
                }
                y += rowH;
            }
            y += 6;
        }

        // التوقيعات
        // §B84/P7: كانت تُحذف بصمت عند امتلاء الصفحة (مستند بلا توقيع!) — الآن تنتقل
        // لصفحة جديدة عند الحاجة، فلا تُفقد أبداً.
        if (m.Signatures.Count > 0)
        {
            if (y + 40 > page.Height.Point - margin - 14) NewPage();
            y += 26;
            double sw = pageW / m.Signatures.Count;
            for (int i = 0; i < m.Signatures.Count; i++)
            {
                var cx = margin + i * sw + sw / 2;
                gfx.DrawString(m.Signatures[i], headFont, XBrushes.Black, new XPoint(cx, y + 20), XStringFormats.TopCenter);
                gfx.DrawLine(new XPen(XColors.Black, 0.8), cx - 60, y + 18, cx + 60, y + 18);
            }
        }

        int total = doc.PageCount;
        for (int i = 0; i < total; i++)
        {
            using var g = XGraphics.FromPdfPage(doc.Pages[i]);
            g.DrawString($"صفحة {i + 1} من {total}", metaFont, XBrushes.Gray,
                new XPoint(pageW / 2 + margin, doc.Pages[i].Height.Point - 10), XStringFormats.TopCenter);
        }
        doc.Save(path);
    }
}
