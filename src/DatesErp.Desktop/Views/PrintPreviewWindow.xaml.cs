using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §معاينة النماذج قبل الطباعة: تعرض النموذج كاملاً (هوية الشركة + الجدول + الإجماليات + التوقيعات)
/// في صفحة بيضاء، ولا يُرسل للطابعة إلا بعد مراجعة المستخدم وضغط «طباعة الآن».
/// §التحسين: تكبير/تصغير حي (40%–300%) + ملاءمة العرض + زر تصدير PDF اختياري (يظهر عند توفير مُصدِّر).
/// </summary>
public partial class PrintPreviewWindow : Window
{
    private readonly FlowDocument _doc;

    /// <summary>مُصدِّر PDF اختياري: يظهر زر «تصدير PDF» فقط إذا مرّرت دالة تصدير (مثل ReceivingPrintPdf).</summary>
    private readonly Action<string>? _pdfExporter;

    public PrintPreviewWindow(FlowDocument doc, string title, Action<string>? pdfExporter = null)
    {
        InitializeComponent();
        _doc = doc;
        _pdfExporter = pdfExporter;
        PreviewTitle.Text = title;
        Title = $"معاينة قبل الطباعة — {title}";
        if (_pdfExporter != null) PdfBtn.Visibility = Visibility.Visible;
        Loaded += (_, _) =>
        {
            // §إصلاح جذري (الصفحات الفارغة): كان هنا ColumnWidth = 1000 يدهس ضبطَ بُناة
            // النماذج (double.MaxValue = عمود واحد يملأ الصفحة) ولا يُعاد أبداً قبل الطباعة.
            // النموذج الرأسي عرضه 794 وهوامشه 80 ← 714 متاح فقط، فالعمود 1000 أعرض من
            // الصفحة ويعجز المُرقِّم عن رصفه ← صفحات بيضاء. عمودٌ واحد هو الصواب دائماً.
            // §B106.1 — تصحيح: كان double.MaxValue فانهار الرصف إلى عمود بعرض حرف واحد
            // (النص العربي يتكسّر رأسياً وتتضاعف الصفحات). double.MaxValue يصلح لعارض
            // التمرير الذي يقصّه إلى عرض النافذة، أما FlowDocumentPageViewer فيحترم
            // المقاس حرفياً فيأخذه على أنه عرض لا نهائي.
            // الصواب: عمود واحد بعرض منطقة النص الفعلية = عرض الصفحة ناقص الهوامش.
            _doc.ColumnWidth = UsableWidth(_doc);
            Viewer.Document = _doc;
            ApplyZoom(ZoomSlider.Value);
        };
    }

    // ════ الطباعة ════
    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        // §إصلاح: اتجاه الورق يتبع أبعاد النموذج نفسه — كان النموذج الأفقي (1122×794)
        // يُرسل إلى ورقة رأسية فيُقصّ أو يُخرج صفحات فارغة.
        try
        {
            bool landscape = _doc.PageWidth > _doc.PageHeight;
            var ticket = dlg.PrintTicket ?? new System.Printing.PrintTicket();
            ticket.PageOrientation = landscape
                ? System.Printing.PageOrientation.Landscape
                : System.Printing.PageOrientation.Portrait;
            dlg.PrintTicket = ticket;
        }
        catch { /* بعض برامج التشغيل ترفض ضبط التذكرة — تُترك على الافتراضي */ }

        // §إصلاح: تثبيت مقاس الصفحة على أبعاد المستند قبل الترقيم (لا مقاس العارض)
        var paginator = ((IDocumentPaginatorSource)_doc).DocumentPaginator;
        try
        {
            if (_doc.PageWidth > 0 && _doc.PageHeight > 0)
                paginator.PageSize = new Size(_doc.PageWidth, _doc.PageHeight);
        }
        catch { }

        dlg.PrintDocument(paginator, PreviewTitle.Text);
    }

    // ════ تصدير PDF عبر المُصدِّر المخصص ════
    private void Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfExporter == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ملف PDF|*.pdf",
            FileName = MakeSafe(PreviewTitle.Text) + ".pdf"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _pdfExporter(dlg.FileName);
            MessageBox.Show(this, $"تم تصدير النموذج بنجاح إلى:\n{dlg.FileName}", "تصدير PDF",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"تعذر تصدير PDF:\n{ex.Message}", "تصدير PDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>عرض منطقة النص داخل الصفحة = PageWidth ناقص الهوامش (بحدٍّ أدنى آمن).</summary>
    private static double UsableWidth(FlowDocument d)
    {
        double w = d.PageWidth;
        if (double.IsNaN(w) || double.IsInfinity(w) || w <= 0) w = 794; // A4 عمودي
        var pad = d.PagePadding;
        double l = double.IsNaN(pad.Left) ? 0 : pad.Left;
        double r = double.IsNaN(pad.Right) ? 0 : pad.Right;
        double usable = w - l - r;
        return usable > 100 ? usable : w;
    }

    // ════ التكبير/التصغير ════
    private void ApplyZoom(double percent)
    {
        if (Viewer == null) return;
        Viewer.Zoom = percent;
        if (ZoomLabel != null) ZoomLabel.Text = $"{percent:0}%";
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        => ApplyZoom(ZoomSlider.Value);

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
        => ZoomSlider.Value = Math.Min(300, ZoomSlider.Value + 20);

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => ZoomSlider.Value = Math.Max(40, ZoomSlider.Value - 20);

    private void Reset_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = 100;

    /// <summary>§ملاءمة العرض: يوسّع المستند حتى يملأ عرض النافذة (بحد 300%).</summary>
    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        const double pagePadding = 120; // حدود الصفحة البيضاء + هوامش القارئ
        var target = Math.Min(300, Math.Max(40, (Viewer.ActualWidth - pagePadding) / (_doc.PageWidth > 0 ? _doc.PageWidth : 820) * 100));
        ZoomSlider.Value = Math.Round(target);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string MakeSafe(string s) =>
        string.Concat((s ?? "document").Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
}
