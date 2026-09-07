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
            _doc.ColumnWidth = 1000;
            Viewer.Document = _doc;
            ApplyZoom(ZoomSlider.Value);
        };
    }

    // ════ الطباعة ════
    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(((IDocumentPaginatorSource)_doc).DocumentPaginator, PreviewTitle.Text);
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
