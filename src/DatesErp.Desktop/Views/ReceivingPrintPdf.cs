using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using DatesErp.Core.Common;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §تصدير سند الاستلام الرسمي إلى PDF (A4 عمودي) — نفس تخطيط معاينة الطباعة:
/// ترويسة الشركة ← بيانات السند ← جدول البنود ← الإجماليات ← التوقيعات.
/// يعمل بالكامل Offline عبر PdfSharp (مضاف أصلًا في المشروع).
/// </summary>
public static class ReceivingPrintPdf
{
    // أبعاد A4 بالنقاط (72 نقطة/بوصة)
    private const double W = 595, H = 842, M = 36;

    public static void Export(ReceivingPrintModel m, string path)
    {
        var doc = new PdfDocument();
        doc.Info.Title = $"سند استلام {m.DocumentNumber}";
        var page = doc.AddPage();
        page.Size = PageSize.A4;
        page.Orientation = PdfSharp.PageOrientation.Portrait;
        using var g = XGraphics.FromPdfPage(page);
        g.SmoothingMode = XSmoothingMode.AntiAlias;

        // §RTL: نرسم من اليمين لليسار — الدوال المساعدة تحسب من الحافة اليمنى
        double y = M;
        var fTitle = new XFont("Segoe UI", 17, XFontStyleEx.Bold);
        var fSub = new XFont("Segoe UI", 8.5, XFontStyleEx.Regular);
        var fHead = new XFont("Segoe UI", 11, XFontStyleEx.Bold);
        var fBody = new XFont("Segoe UI", 9.5, XFontStyleEx.Regular);
        var fBold = new XFont("Segoe UI", 9.5, XFontStyleEx.Bold);

        // ── الترويسة ──
        g.DrawString(m.CompanyNameAr, fTitle, XBrushes.DarkGreen,
            new XRect(M, y, W - 2 * M, 24), XStringFormats.TopCenter);
        y += 24;
        var subLine = string.Join("   |   ", new[] { m.CompanyNameEn, m.Address, string.IsNullOrWhiteSpace(m.Phone) ? "" : "هاتف: " + m.Phone }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (subLine.Length > 0)
        {
            g.DrawString(subLine, fSub, XBrushes.Gray, new XRect(M, y, W - 2 * M, 12), XStringFormats.TopCenter);
            y += 13;
        }
        var pen = new XPen(XColor.FromArgb(218, 165, 32), 2.2);
        g.DrawLine(pen, M, y, W - M, y);
        y += 14;

        // ── العنوان + الحالة ──
        g.DrawString("أمر وسند استلام شحنة تمور خام", fHead, XBrushes.DarkBlue,
            new XRect(M, y, W - 2 * M, 18), XStringFormats.TopCenter);
        y += 19;
        var status = m.IsApproved ? "معتمد ✓" : "مسودة — غير معتمد";
        var statusColor = m.IsApproved ? XColor.FromArgb(20, 83, 45) : XColor.FromArgb(146, 64, 14);
        var meta = $"رقم السند: {m.DocumentNumber}          {status}";
        g.DrawString(meta, fBold, new XSolidBrush(statusColor),
            new XRect(M, y, W - 2 * M, 14), XStringFormats.TopCenter);
        y += 20;

        // ── بيانات السند (صندوق 2×2) ──
        double boxH = 52;
        g.DrawRectangle(XPens.DarkBlue, new XRect(M, y, W - 2 * M, boxH));
        g.DrawLine(XPens.DarkBlue, M, y + boxH / 2, W - M, y + boxH / 2);
        g.DrawLine(XPens.DarkBlue, M + (W - 2 * M) / 2, y, M + (W - 2 * M) / 2, y + boxH);
        void Info(double cx, double cy, string label, string value)
        {
            g.DrawString(label, fBody, XBrushes.DarkSlateGray, new XPoint(cx, cy));
            g.DrawString(value, fBold, XBrushes.Black, new XPoint(cx, cy + 12));
        }
        var midX = M + (W - 2 * M) / 2;
        Info(W - M - 8, y + 8, "العميل المورد:", m.CustomerName);
        Info(midX - 8, y + 8, "رقم الحاوية/الشاحنة:", m.ContainerNumber);
        Info(W - M - 8, y + boxH / 2 + 8, "تاريخ الاستلام:", UiFormat.D(m.ReceivedDate));
        Info(midX - 8, y + boxH / 2 + 8, "مخزن الاستلام:", m.WarehouseName);
        y += boxH + 10;

        // ── جدول البنود ──
        double[] colW = { 22, 68, 150, 72, 45, 62, 72 };
        var totalW = colW.Sum();
        var tableX = W - M - totalW; // يمين الصفحة
        string[] heads = { "م", "رقم الصنف", "اسم الصنف الخام", "وحدة الاستلام", "العدد", "وزن العبوة", "الإجمالي (كجم)" };

        void DrawRow(string[] cells, XBrush bg, XFont font, bool isHead)
        {
            double rowH = isHead ? 22 : 18;
            var x = W - M;
            g.DrawRectangle(bg, new XRect(x - totalW, y, totalW, rowH));
            for (int i = 0; i < cells.Length; i++)
            {
                x -= colW[i];
                g.DrawRectangle(XPens.DarkBlue, new XRect(x, y, colW[i], rowH));
                g.DrawString(cells[i], font, isHead ? XBrushes.White : XBrushes.Black,
                    new XRect(x + 2, y, colW[i] - 4, rowH),
                    i == 2 ? XStringFormats.Center : XStringFormats.Center);
            }
            y += rowH;
        }

        DrawRow(heads, XBrushes.DarkBlue, fBody, true);
        int alt = 0;
        foreach (var it in m.Items)
        {
            DrawRow(new[]
            {
                it.RowNo.ToString(), it.ProductCode, it.ProductName, it.ReceiptUnit,
                it.PackageCount.ToString(), UiFormat.N(it.UnitWeightKg), UiFormat.N(it.QtyKg)
            }, alt++ % 2 == 1 ? XBrushes.WhiteSmoke : XBrushes.White, fBody, false);
        }
        DrawRow(new[] { "", "", "الإجمالي", "", m.Items.Sum(i => i.PackageCount).ToString(), "", UiFormat.N(m.Items.Sum(i => i.QtyKg)) },
            XBrushes.Khaki, fBold, false);
        y += 12;

        // ── البيان ──
        if (!string.IsNullOrWhiteSpace(m.Notes))
        {
            g.DrawString($"البيان: {m.Notes}", fBody, XBrushes.DimGray, new XRect(M, y, W - 2 * M, 14), XStringFormats.TopRight);
            y += 18;
        }

        // ── التوقيعات ──
        double sigY = Math.Max(y + 24, H - M - 70);
        double sigW = (W - 2 * M - 24) / 3;
        var titles = new[] { "موظف الاستلام", "مسؤول فحص الجودة", "أمين مخزن المواد الخام", "مدير المصنع / الاعتماد" };
        double sx = W - M;
        foreach (var t in titles)
        {
            sx -= sigW;
            g.DrawRectangle(XPens.Gray, new XRect(sx, sigY, sigW, 58));
            g.DrawString(t, fBold, XBrushes.Black, new XRect(sx, sigY + 6, sigW, 14), XStringFormats.TopCenter);
            g.DrawString("......................", fBody, XBrushes.Gray, new XRect(sx, sigY + 34, sigW, 12), XStringFormats.TopCenter);
            sx -= 12;
        }

        // ── تذييل ──
        g.DrawString($"طُبع بتاريخ {UiFormat.D(DateTime.Now)} — نظام DateERP",
            fSub, XBrushes.Gray, new XRect(M, H - M + 6, W - 2 * M, 10), XStringFormats.TopCenter);

        doc.Save(path);
    }
}
