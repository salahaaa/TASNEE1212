using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClosedXML.Excel;
using DatesErp.Core.Interfaces.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §25 + §التطوير الشامل — محرك الإخراج الاحترافي للتقارير:
/// طباعة بمعاينة (ترويسة شركة + مستخدم + إجماليات + تبريد صفوف + اتجاه تلقائي)،
/// PDF متعدد الصفوف برأس متكرر وترقيم صفحات وأعمدة متناسبة،
/// Excel منسّق (حدود/تبريد/تنسيق أرقام/صف إجماليات/تثبيت الرأس/اتجاه RTL).
/// </summary>
public class ExportPrintService
{
    private readonly DialogService _dialogs;

    public ExportPrintService(DialogService dialogs)
    {
        _dialogs = dialogs;
    }

    private static string CurrentUser()
    {
        try { return AppContainer.Get<ICurrentSession>().UserName ?? "-"; }
        catch { return "-"; }
    }

    /// <summary>§الإجماليات الاحترافية: مجموع كل عمود رقمي (null للأعمدة النصية وغير القابلة للجمع).</summary>
    public static List<double?> ComputeTotals(List<string> columns, List<object[]> rows)
    {
        var totals = new List<double?>();
        for (int c = 0; c < columns.Count; c++)
        {
            // §B84/P1: الأعمدة غير القابلة للجمع (نسب/أسعار/أرقام تعريفية) كان مجموعها مضللاً — تُستبعد.
            if (IsNonSummable(columns[c])) { totals.Add(null); continue; }
            // §B84/P1: حارس السنوات — عمود سنوي (2024، 2025...) لا يُجمع حتى لو بدا رقمياً.
            bool yearHeader = IsYearHeader(columns[c]);
            double sum = 0; bool numeric = false; bool allYearLike = true;
            foreach (var row in rows)
            {
                if (c >= row.Length) continue;
                var v = row[c];
                if (v == null || string.IsNullOrWhiteSpace(v.ToString())) continue;
                if (v is double d) { sum += d; numeric = true; if (d < 1900 || d > 2100 || d != Math.Floor(d)) allYearLike = false; }
                else if (v is int i) { sum += i; numeric = true; if (i < 1900 || i > 2100) allYearLike = false; }
                else if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) { sum += p; numeric = true; if (p < 1900 || p > 2100 || p != Math.Floor(p)) allYearLike = false; }
                else { numeric = false; break; }
            }
            totals.Add(numeric && rows.Count > 0 && !(yearHeader && allYearLike) ? sum : null);
        }
        return totals;
    }

    /// <summary>§B84/P1: ترويسات لا معنى لجمعها (نسب، متوسطات، أسعار مفردة، أرقام تعريفية).</summary>
    private static bool IsNonSummable(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return true;
        string[] keys = { "نسبة", "%", "٪", "متوسط", "معدل", "سعر", "السعر", "Price", "price",
            "رقم", "كود", "رمز", "تسلسل", "No.", "Code", "code" };
        foreach (var k in keys)
            if (header.Contains(k)) return true;
        return false;
    }

    /// <summary>§B84/P1: ترويسة عمود سنوي (يُطبَّق عليها حارس السنوات مع فحص القيم).</summary>
    private static bool IsYearHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        return header.Contains("سنة") || header.Contains("السنة") || header.Contains("العام")
            || header.Contains("Year") || header.Contains("year");
    }

    /// <summary>§شريط إجماليات نصي مختصر للعرض داخل الشاشة.</summary>
    public static string TotalsLine(List<string> columns, List<object[]> rows)
    {
        var totals = ComputeTotals(columns, rows);
        var parts = new List<string>();
        for (int c = 0; c < columns.Count; c++)
            // §B84/P2: توحيد الإجماليات على منزلتين عشريتين (كانت N1 هنا وN2 في الخلايا).
            if (totals[c] is double t) parts.Add($"{columns[c]}: {t:N2}");
        return parts.Count > 0 ? "الإجماليات ← " + string.Join(" | ", parts) : "";
    }

    // ═══════════════════════════ الطباعة بمعاينة ═══════════════════════════

    public void Print(ReportResult report)
    {
        var doc = BuildFlowDocument(report);
        var preview = new Views.PrintPreviewWindow(doc, report.TitleAr)
        { Owner = System.Windows.Application.Current.MainWindow };
        preview.ShowDialog();
    }

    // ═══════════════════════════ PDF احترافي ═══════════════════════════

    public void ExportPdf(ReportResult report)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ملف PDF|*.pdf",
            FileName = SafeFileName(report.TitleAr) + ".pdf"
        };
        if (dlg.ShowDialog() != true) return;
        WritePdf(report, dlg.FileName);
        _dialogs.Info($"تم تصدير التقرير إلى:\n{dlg.FileName}");
    }

    /// <summary>§B84/P6: كتابة PDF لمسار مباشر بلا حوار حفظ — كان التصدير مرتبطاً بالحوار
    /// فيستحيل إعادة استخدامه (معاينة/أرشفة/إرفاق). ExportPdf أعلاه أصبح غلافاً رفيعاً حولها.</summary>
    public void WritePdf(ReportResult report, string path)
    {
        try
        {
            var doc = new PdfDocument();
            doc.Info.Title = report.TitleAr;
            var totals = ComputeTotals(report.Columns, report.Rows);

            // §أعراض أعمدة متناسبة حسب أطول محتوى (بدل التقسيم المتساوي)
            int cols = Math.Max(1, report.Columns.Count);
            var weights = new double[cols];
            for (int c = 0; c < cols; c++)
            {
                double w = (report.Columns[c] ?? "").Length + 2;
                foreach (var row in report.Rows)
                {
                    double len = c < row.Length ? (FormatCell(row[c]).Length) : 0;
                    if (len > w) w = len;
                }
                weights[c] = Math.Min(w, 42);
            }
            double wSum = weights.Sum(x => x == 0 ? 1 : x);

            double margin = 18;
            double rowH = 16;
            var titleFont = new XFont("Segoe UI", 13, XFontStyleEx.Bold);
            var metaFont = new XFont("Segoe UI", 8, XFontStyleEx.Regular);
            var headFont = new XFont("Segoe UI", 8.5, XFontStyleEx.Bold);
            var cellFont = new XFont("Segoe UI", 8, XFontStyleEx.Regular);

            PdfPage page = null;
            XGraphics gfx = null;
            double y = 0;

            // §B84/P4: الاتجاه العرضي كان مفروضاً على كل التقارير — الآن يتبع قاعدة المعاينة نفسها (>8 أعمدة).
            bool landscape = report.Columns.Count > 8;
            void NewPage()
            {
                gfx?.Dispose();
                page = doc.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                page.Orientation = landscape ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;
                gfx = XGraphics.FromPdfPage(page);
                double w = page.Width.Point - margin * 2;

                // §ترويسة احترافية: شعار + شركة + عنوان + مستخدم/توقيت
                var logoBytes = CompanyIdentity.LogoBytes;
                if (logoBytes is { Length: > 0 })
                {
                    try { using var ms = new MemoryStream(logoBytes); using var img = XImage.FromStream(ms); gfx.DrawImage(img, page.Width.Point - margin - 24, margin, 24, 24); }
                    catch { }
                }
                gfx.DrawString(CompanyIdentity.NameAr, new XFont("Segoe UI", 10.5, XFontStyleEx.Bold), XBrushes.Black,
                    new XPoint(page.Width.Point - margin - 30, margin + 6), XStringFormats.TopRight);
                gfx.DrawString(CompanyIdentity.Address ?? "", metaFont, XBrushes.Gray,
                    new XPoint(page.Width.Point - margin - 30, margin + 20), XStringFormats.TopRight);
                gfx.DrawString(report.TitleAr, titleFont, XBrushes.DarkSlateBlue,
                    new XPoint(page.Width.Point / 2, margin + 4), XStringFormats.TopCenter);
                gfx.DrawString($"المستخدم: {CurrentUser()}   |   تاريخ الإصدار: {DateTime.Now:dd/MM/yyyy HH:mm}", metaFont, XBrushes.Gray,
                    new XPoint(margin, margin + 6), XStringFormats.TopLeft);
                gfx.DrawLine(new XPen(XBrushes.DarkSlateBlue.Color, 1.2), margin, margin + 34, page.Width.Point - margin, margin + 34);

                // §رأس الجدول
                y = margin + 40;
                double x = margin;
                for (int c = 0; c < cols; c++)
                {
                    double cw = (weights[c] == 0 ? 1 : weights[c]) / wSum * w;
                    var rect = new XRect(x, y, cw, rowH);
                    gfx.DrawRectangle(XBrushes.DarkSlateBlue, rect);
                    gfx.DrawString(report.Columns[c] ?? "", headFont, XBrushes.White,
                        new XRect(rect.X + 2, rect.Y, rect.Width - 4, rect.Height), XStringFormats.CenterRight);
                    x += cw;
                }
                y += rowH;
            }

            NewPage();
            double pageW = page.Width.Point - margin * 2;
            bool zebra = false;

            foreach (var row in report.Rows)
            {
                if (y + rowH > page.Height.Point - margin - 14) NewPage();
                double x = margin;
                zebra = !zebra;
                for (int c = 0; c < cols; c++)
                {
                    double cw = (weights[c] == 0 ? 1 : weights[c]) / wSum * pageW;
                    var rect = new XRect(x, y, cw, rowH);
                    if (zebra) gfx.DrawRectangle(XBrushes.WhiteSmoke, rect);
                    gfx.DrawRectangle(new XPen(XColors.LightGray, 0.4), rect);
                    string val = c < row.Length ? FormatCell(row[c]) : "";
                    if (val.Length > 46) val = val[..46] + "…";
                    gfx.DrawString(val, cellFont, XBrushes.Black,
                        new XRect(rect.X + 2, rect.Y, rect.Width - 4, rect.Height), XStringFormats.CenterRight);
                    x += cw;
                }
                y += rowH;
            }

            // §صف الإجماليات الغامق
            if (y + rowH > page.Height.Point - margin - 14) NewPage();
            double x2 = margin;
            for (int c = 0; c < cols; c++)
            {
                double cw = (weights[c] == 0 ? 1 : weights[c]) / wSum * pageW;
                var rect = new XRect(x2, y, cw, rowH);
                gfx.DrawRectangle(XBrushes.LightSteelBlue, rect);
                string val = c == 0 ? "الإجمالي" : totals[c] is double t ? t.ToString("N2") : ""; // §B84/P2
                gfx.DrawString(val, headFont, XBrushes.Black,
                    new XRect(rect.X + 2, rect.Y, rect.Width - 4, rect.Height), XStringFormats.CenterRight);
                x2 += cw;
            }
            y += rowH + 6;

            // §المؤشرات أسفل الجدول
            foreach (var kv in report.Summary)
            {
                if (y + rowH > page.Height.Point - margin - 14) NewPage();
                gfx.DrawString($"{kv.Key}: {kv.Value}", headFont, XBrushes.DarkGreen,
                    new XPoint(page.Width.Point - margin, y + 10), XStringFormats.TopRight);
                y += rowH;
            }

            // §ترقيم الصفحات + تذييل الهوية في كل صفحة
            int total = doc.PageCount;
            for (int i = 0; i < total; i++)
            {
                using var g = XGraphics.FromPdfPage(doc.Pages[i]);
                g.DrawString($"صفحة {i + 1} من {total}", metaFont, XBrushes.Gray,
                    new XPoint(pageW / 2 + margin, doc.Pages[i].Height.Point - 10), XStringFormats.TopCenter);
                if (!string.IsNullOrWhiteSpace(CompanyIdentity.ReportFooter))
                    g.DrawString(CompanyIdentity.ReportFooter, metaFont, XBrushes.Gray,
                        new XPoint(doc.Pages[i].Width.Point - margin, doc.Pages[i].Height.Point - 10), XStringFormats.TopRight);
            }

            doc.Save(path);
        }
        catch (Exception ex)
        {
            _dialogs.HandleException(ex, "WritePdf");
        }
    }

    // ═══════════════════════════ Excel احترافي ═══════════════════════════

    public void ExportExcel(ReportResult report)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ملف Excel|*.xlsx",
            FileName = SafeFileName(report.TitleAr) + ".xlsx"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet(SafeSheetName(report.TitleAr));
            ws.RightToLeft = true;

            // §ترويسة: شركة + تقرير + مستخدم/توقيت
            var coCell = ws.Cell(1, 1);
            coCell.Value = CompanyIdentity.NameAr;
            coCell.Style.Font.Bold = true;
            coCell.Style.Font.FontSize = 16;
            coCell.Style.Font.FontColor = XLColor.FromHtml("#0A246A");
            ws.Cell(2, 1).Value = report.TitleAr;
            ws.Cell(2, 1).Style.Font.Bold = true;
            var meta = ws.Cell(3, 1);
            meta.Value = $"المستخدم: {CurrentUser()} | تاريخ الإصدار: {DateTime.Now:dd/MM/yyyy HH:mm}";
            meta.Style.Font.FontColor = XLColor.Gray;
            meta.Style.Font.FontSize = 9;

            int headRow = 4;
            for (int c = 0; c < report.Columns.Count; c++)
            {
                var cell = ws.Cell(headRow, c + 1);
                cell.Value = report.Columns[c];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0A246A");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            var totals = ComputeTotals(report.Columns, report.Rows);
            for (int r = 0; r < report.Rows.Count; r++)
            {
                for (int c = 0; c < report.Columns.Count && c < report.Rows[r].Length; c++)
                {
                    var cell = ws.Cell(headRow + 1 + r, c + 1);
                    SetCell(cell, report.Rows[r][c]);
                    if (r % 2 == 1) cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    if (totals[c] != null && report.Rows[r][c] is double or int)
                        cell.Style.NumberFormat.Format = "#,##0.###";
                }
            }

            // §صف الإجماليات
            int tr = headRow + 1 + report.Rows.Count;
            ws.Cell(tr, 1).Value = "الإجمالي";
            ws.Cell(tr, 1).Style.Font.Bold = true;
            for (int c = 0; c < report.Columns.Count; c++)
            {
                var cell = ws.Cell(tr, c + 1);
                if (totals[c] is double t) { cell.Value = t; cell.Style.NumberFormat.Format = "#,##0.##"; }
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#C9CBA3");
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            int sr = tr + 2;
            foreach (var kv in report.Summary)
            {
                ws.Cell(sr, 1).Value = kv.Key;
                ws.Cell(sr, 2).Value = kv.Value;
                ws.Cell(sr, 1).Style.Font.Bold = true;
                ws.Cell(sr, 2).Style.Font.FontColor = XLColor.FromHtml("#1B4D3E");
                sr++;
            }
            if (!string.IsNullOrWhiteSpace(CompanyIdentity.ReportFooter))
            {
                sr++;
                ws.Cell(sr, 1).Value = CompanyIdentity.ReportFooter;
                ws.Cell(sr, 1).Style.Font.FontColor = XLColor.Gray;
            }
            ws.SheetView.FreezeRows(headRow);
            ws.Columns().AdjustToContents();
            wb.SaveAs(dlg.FileName);
            _dialogs.Info($"تم تصدير التقرير إلى:\n{dlg.FileName}");
        }
        catch (Exception ex)
        {
            _dialogs.HandleException(ex, "ExportExcel");
        }
    }

    private static void SetCell(IXLCell cell, object v)
    {
        switch (v)
        {
            case null: cell.Value = ""; break;
            case double d: cell.Value = d; break;
            case int i: cell.Value = i; break;
            case DateTime dt: cell.Value = dt.ToString("dd/MM/yyyy"); break;
            default: cell.Value = v.ToString(); break;
        }
    }

    // ═══════════════════════════ معاينة الطباعة ═══════════════════════════

    private FlowDocument BuildFlowDocument(ReportResult report)
    {
        bool landscape = report.Columns.Count > 8;
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,   // §خط أكبر: التقرير يُقرأ مطبوعاً
            TextAlignment = TextAlignment.Right,
            FlowDirection = FlowDirection.RightToLeft, // §B84/P5: الجداول العربية كانت تُرسم LTR
            PagePadding = new Thickness(40),
            // §اتجاه تلقائي: أعمدة كثيرة = عرضي
            PageWidth = landscape ? 1122 : 794,
            PageHeight = landscape ? 794 : 1122
        };

        // §ترويسة دفترية احترافية: الشركة يميناً + الشعار وسطاً + بيانات الاتصال يساراً (نموذج الإدارة المعتمد)
        var head = new Table { CellSpacing = 0 };
        head.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        head.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        head.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var cRight = new TableCell();
        cRight.Blocks.Add(new Paragraph(new Run(CompanyIdentity.NameAr)) { FontSize = 13, FontWeight = FontWeights.Bold });
        if (!string.IsNullOrWhiteSpace(CompanyIdentity.Address)) cRight.Blocks.Add(new Paragraph(new Run(CompanyIdentity.Address)) { FontSize = 8.5, Foreground = Brushes.Gray });
        if (!string.IsNullOrWhiteSpace(CompanyIdentity.Phone)) cRight.Blocks.Add(new Paragraph(new Run("هاتف: " + CompanyIdentity.Phone)) { FontSize = 8.5, Foreground = Brushes.Gray });
        var cMid = new TableCell();
        var logo = CompanyIdentity.GetLogo(64);
        if (logo != null) cMid.Blocks.Add(new BlockUIContainer(new System.Windows.Controls.Image { Source = logo, Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center }));
        else cMid.Blocks.Add(new Paragraph(new Run("")));
        var cLeft = new TableCell();
        // §B84/P3: سطر الهاتف يظهر فقط عند توفر الرقم، وسطرا الفاكس والصندوق المحذوفان ("-")
        // كانا يطبعان شرطة صريحة في كل تقرير — حُذفا نهائياً (لا حقل لهما في CompanyIdentity).
        if (!string.IsNullOrWhiteSpace(CompanyIdentity.Phone))
            cLeft.Blocks.Add(new Paragraph(new Run("Tele No.: " + CompanyIdentity.Phone)) { FontSize = 8.5, Foreground = Brushes.Gray });
        var hrow = new TableRow();
        hrow.Cells.Add(cRight); hrow.Cells.Add(cMid); hrow.Cells.Add(cLeft);
        var hgroup = new TableRowGroup(); hgroup.Rows.Add(hrow);
        head.RowGroups.Add(hgroup);
        doc.Blocks.Add(head);
        doc.Blocks.Add(new Paragraph(new Run(report.TitleAr))
        { FontSize = 19, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
        doc.Blocks.Add(new Paragraph(new Run($"طبع بواسطة: {CurrentUser()}   |   تاريخ التقرير: {DateTime.Now:dd/MM/yyyy HH:mm}" + (string.IsNullOrWhiteSpace(report.PeriodLabel) ? "" : $"   |   الفترة: {report.PeriodLabel}")))
        { FontSize = 11, Foreground = Brushes.Gray, TextAlignment = TextAlignment.Center });

        // §الجدول مع تبريد صفوف وصف إجماليات
        var table = new Table { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), CellSpacing = 0 };
        var header = new TableRowGroup();
        var hRow = new TableRow { Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) };
        foreach (var col in report.Columns)
        {
            hRow.Cells.Add(new TableCell(new Paragraph(new Run(col ?? "")) { Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12.5 }));
            table.Columns.Add(new TableColumn());
        }
        header.Rows.Add(hRow);
        table.RowGroups.Add(header);

        var body = new TableRowGroup();
        bool zebra = false;
        foreach (var row in report.Rows)
        {
            zebra = !zebra;
            var tr = new TableRow { Background = zebra ? new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)) : Brushes.White };
            for (int c = 0; c < report.Columns.Count; c++)
                tr.Cells.Add(new TableCell(new Paragraph(new Run(c < row.Length ? FormatCell(row[c]) : "")) { FontSize = 12 }));
            body.Rows.Add(tr);
        }
        table.RowGroups.Add(body);

        // §صف الإجماليات
        var totals = ComputeTotals(report.Columns, report.Rows);
        if (totals.Any(t => t != null))
        {
            var tGroup = new TableRowGroup();
            var tRow = new TableRow { Background = new SolidColorBrush(Color.FromRgb(0xC9, 0xCB, 0xA3)), FontWeight = FontWeights.Bold };
            for (int c = 0; c < report.Columns.Count; c++)
                tRow.Cells.Add(new TableCell(new Paragraph(new Run(c == 0 ? "الإجمالي" : totals[c] is double t ? t.ToString("N2") : "")) { FontSize = 12, FontWeight = FontWeights.Bold })); // §B84/P2
            tGroup.Rows.Add(tRow);
            table.RowGroups.Add(tGroup);
        }
        doc.Blocks.Add(table);

        // §المؤشرات
        foreach (var kv in report.Summary)
            doc.Blocks.Add(new Paragraph(new Run($"{kv.Key}: {kv.Value}")) { FontWeight = FontWeights.Bold, Foreground = Brushes.DarkGreen, FontSize = 10.5 });

        if (!string.IsNullOrWhiteSpace(CompanyIdentity.ReportFooter))
            doc.Blocks.Add(new Paragraph(new Run(CompanyIdentity.ReportFooter))
            { FontSize = 9, Foreground = Brushes.Gray, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0) });

        return doc;
    }

    private static string FormatCell(object v) => v switch
    {
        null => "",
        double d => d.ToString("N2", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("dd/MM/yyyy HH:mm"),
        _ => v.ToString()
    };

    private static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "report" : s;
    }

    private static string SafeSheetName(string s)
    {
        foreach (var c in new[] { '\\', '/', '*', '?', ':', '[', ']' }) s = s.Replace(c, ' ');
        return s.Length > 28 ? s[..28] : (string.IsNullOrWhiteSpace(s) ? "تقرير" : s);
    }
}
