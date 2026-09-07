using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// 📈 مركز التقارير الموحد — تقارير العمليات والمخزون والشاملة:
/// فلاتر ديناميكية حسب التقرير + بطاقات مؤشرات + بحث سريع لحظي في النتائج
/// + زر «+» أمام كل صف يفتح المستند الأصلي في شاشته (تنقل احترافي).
/// </summary>
public partial class ReportsView : UserControl
{
    private ReportResult _current;
    private readonly List<(string key, Func<string> get)> _paramGetters = new();
    private List<string> _columns = new();
    private List<object[]> _rows = new();
    private List<DocLinkDto> _links; // null = تقرير بلا تنقل
    private System.ComponentModel.ICollectionView _reportsView; // §B90: لفلترة المعرض بالاسم

    public ReportsView()
    {
        InitializeComponent();
        RowsCount.TextWrapping = TextWrapping.Wrap; // §شريط الإجماليات قد يطول
        Loaded += (_, _) => Load();
    }

    /// <summary>
    /// §التقرير يملأ الواجهة: تختفي قائمة التقارير ويتسع عمود النتيجة لكل العرض.
    /// </summary>
    private void ShowReportFullWidth()
    {
        ListCol.Width = new GridLength(0);
        CatalogPanel.Visibility = Visibility.Collapsed;
        ReportPanel.SetValue(Grid.ColumnProperty, 0);
        Grid.SetColumnSpan(ReportPanel, 2);
        BackBtn.Visibility = Visibility.Visible;
    }

    /// <summary>العودة إلى المعرض مع الاحتفاظ بالتقرير المعروض خلفه.</summary>
    private void ShowCatalog()
    {
        ListCol.Width = new GridLength(360);
        CatalogPanel.Visibility = Visibility.Visible;
        ReportPanel.SetValue(Grid.ColumnProperty, 1);
        Grid.SetColumnSpan(ReportPanel, 1);
        BackBtn.Visibility = Visibility.Collapsed;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowCatalog();

    /// <summary>§نقرتان على تقرير = تشغيله مباشرة بفلاتره الافتراضية.</summary>
    private void Report_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReportsList.SelectedItem is ReportDefinition) Run_Click(sender, e);
    }

    private void Load()
    {
        ShowCatalog();
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IReportService>();
            var reports = svc.GetReports();
            var view = CollectionViewSource.GetDefaultView(reports);
            view.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
            _reportsView = view;
            ReportsList.ItemsSource = view;
            int cats = reports.Select(r => r.Category).Distinct().Count();
            ReportsCount.Text = $"{reports.Count} تقريراً · {cats} تصنيفات";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Reports.Load"); }
    }

    /// <summary>§B90 — فلترة لحظية لمعرض التقارير بالاسم (تبقى المجموعات الفارغة مخفية تلقائياً).</summary>
    private void CatalogSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_reportsView == null) return;
        string term = CatalogSearchBox.Text?.Trim() ?? "";
        if (term.Length == 0) _reportsView.Filter = null;
        else _reportsView.Filter = o => o is ReportDefinition d
            && (d.TitleAr ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════ المعاملات الديناميكية ═══════════════════════════

    private void Report_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ReportsList.SelectedItem is not ReportDefinition def) return;
        ReportTitle.Text = def.TitleAr;
        ReportSub.Text = $"{def.Category} · " + (def.Parameters.Count == 0 ? "بدون فلاتر — اضغط تشغيل" : $"{def.Parameters.Count} فلتر");
        ParamsPanel.Children.Clear();
        _paramGetters.Clear();

        if (def.Parameters.Count == 0)
            ParamsPanel.Children.Add(new TextBlock { Text = "هذا التقرير بدون فلاتر — اضغط تشغيل.", FontSize = 13.5, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });

        foreach (var prm in def.Parameters)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 6) };
            sp.Children.Add(new TextBlock { Text = prm.LabelAr + ":", FontWeight = FontWeights.Bold, FontSize = 13.5, Margin = new Thickness(0, 0, 0, 2) });

            switch (prm.Kind)
            {
                case "date":
                {
                    var dp = new DatePicker { Width = 150, FontSize = 13.5 };
                    sp.Children.Add(dp);
                    _paramGetters.Add((prm.Key, () => dp.SelectedDate?.ToString(UiFormat.DatePattern) ?? ""));
                    break;
                }
                case "list":
                {
                    var cb = new ComboBox { Width = 200, FontSize = 13.5 };
                    cb.Items.Add(new ComboOpt { Value = "", Label = "— الكل —" });
                    if (prm.Options != null)
                        foreach (var (value, label) in prm.Options)
                            cb.Items.Add(new ComboOpt { Value = value, Label = label });
                    cb.SelectedIndex = 0;
                    sp.Children.Add(cb);
                    _paramGetters.Add((prm.Key, () => (cb.SelectedItem as ComboOpt)?.Value ?? ""));
                    break;
                }
                default:
                {
                    var tb = new TextBox { Width = 150, FontSize = 13.5 };
                    sp.Children.Add(tb);
                    _paramGetters.Add((prm.Key, () => tb.Text?.Trim() ?? ""));
                    break;
                }
            }
            ParamsPanel.Children.Add(sp);
        }
    }

    private sealed class ComboOpt
    {
        public string Value { get; set; }
        public string Label { get; set; }
        public override string ToString() => Label;
    }

    // ═══════════════════════════ التشغيل والعرض ═══════════════════════════

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ReportsList.SelectedItem is not ReportDefinition def)
            { AppContainer.Get<DialogService>().Error("اختر تقريراً من القائمة."); return; }

            var parameters = new Dictionary<string, string>();
            foreach (var (key, get) in _paramGetters)
            {
                var v = get();
                if (!string.IsNullOrEmpty(v)) parameters[key] = v;
            }

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IReportService>();
            _current = svc.Run(def.Code, parameters);
            if (_current == null) { AppContainer.Get<DialogService>().Error("تعذر تشغيل التقرير."); return; }

            ReportTitle.Text = _current.TitleAr;
            _columns = _current.Columns;
            _rows = _current.Rows;
            _links = _current.RowLinks;
            QuickSearchBox.Text = "";

            // بطاقات المؤشرات (الإجماليات) أعلى الجدول
            KpiPanel.Children.Clear();
            foreach (var kv in _current.Summary)
                KpiPanel.Children.Add(KpiCard(kv.Key, kv.Value));

            RenderRows(_rows, _links);
            ShowReportFullWidth();   // §تختفي القائمة ويملأ التقرير الواجهة
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Reports.Run"); }
    }

    /// <summary>بناء الجدول: زر «+» أولاً إن وُجدت روابط، ثم أعمدة البيانات.</summary>
    private void RenderRows(List<object[]> rows, List<DocLinkDto> links)
    {
        var dt = new DataTable();
        foreach (var c in _columns) dt.Columns.Add(c);
        foreach (var row in rows)
        {
            var arr = new object[_columns.Count];
            for (int i = 0; i < _columns.Count; i++) arr[i] = i < row.Length ? row[i] : null;
            dt.Rows.Add(arr);
        }

        ReportGrid.AutoGenerateColumns = false;
        ReportGrid.Columns.Clear();

        // §العمود يظهر فقط إن وُجد مستند مرتبط فعلاً — وإلا ظهر زر لا يفعل شيئاً
        if (links != null && links.Any(l => l != null))
        {
            var drill = new DataGridTemplateColumn
            {
                Header = "+",
                Width = 44,
                CellTemplate = BuildDrillTemplate()
            };
            ReportGrid.Columns.Add(drill);
        }
        // §عرض نجمي: الأعمدة تتقاسم عرض الواجهة كاملاً بدل أن تتزاحم على اليسار
        foreach (var c in _columns)
            ReportGrid.Columns.Add(new DataGridTextColumn
            {
                Header = c,
                Binding = new Binding($"[{c}]"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                HeaderStyle = (Style)System.Windows.Application.Current.FindResource("ReportHeaderStyle")
            });

        ReportGrid.ItemsSource = dt.DefaultView;
        // §التطوير الشامل: شريط إجماليات الأعمدة الرقمية أسفل العدّاد
        var totalsLine = ExportPrintService.TotalsLine(_columns, rows);
        RowsCount.Text = $"عدد الصفوف: {UiFormat.N0(rows.Count)}" + (links != null ? " — اضغط «+» أمام أي صف لفتح مستنده" : "")
            + (string.IsNullOrEmpty(totalsLine) ? "" : "\n" + totalsLine);
    }

    private DataTemplate BuildDrillTemplate()
    {
        var factory = new System.Windows.FrameworkElementFactory(typeof(Button));
        factory.SetValue(Button.ContentProperty, "+");
        factory.SetValue(Button.FontWeightProperty, FontWeights.Bold);
        factory.SetValue(Button.ToolTipProperty, "فتح المستند الأصلي في شاشته");
        factory.SetValue(Button.MarginProperty, new Thickness(2));
        factory.SetValue(Button.PaddingProperty, new Thickness(6, 0, 6, 0));
        factory.SetValue(Button.FontSizeProperty, 13.0);
        factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        // §رقم الصف مثبَّت في Tag — لا اعتماد على وراثة DataContext وحدها،
        // فهي تسقط إن تغيّرت شجرة العناصر أو لم يُحدَّد الصف بالنقر على الزر.
        factory.SetBinding(FrameworkElement.TagProperty, new Binding());
        factory.AddHandler(Button.ClickEvent, new RoutedEventHandler(Drill_Click));
        return new DataTemplate { VisualTree = factory };
    }

    /// <summary>§زر + : فتح المستند المرتبط بالصف في شاشته.</summary>
    /// <summary>
    /// §زر «+» يفتح المستند الأصلي في شاشته.
    /// كان يرجع بصمت في أربع حالات فيبدو الزر معطلاً — الآن كل حالة تقول سببها.
    /// </summary>
    private void Drill_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button btn) return;

            if (_links == null || _links.All(l => l == null))
            { AppContainer.Get<DialogService>().Error("هذا التقرير لا يرتبط بمستندات أصلية — لا مستند يُفتح من هنا."); return; }

            // §الصف من Tag (مثبَّت صراحةً) وإلا من DataContext — ولا اعتماد على التحديد،
            // فهو قد لا يتغير بالنقر على الزر فيبدو الزر معطلاً.
            int idx = -1;
            if (btn.Tag is DataRowView tagged) idx = tagged.Row.Table.Rows.IndexOf(tagged.Row);
            else if (btn.DataContext is DataRowView rowView) idx = rowView.Row.Table.Rows.IndexOf(rowView.Row);

            if (idx < 0)
            { AppContainer.Get<DialogService>().Error("تعذّر تحديد الصف — انقر على الصف نفسه ثم على «+» مرة أخرى."); return; }
            if (idx >= _links.Count)
            { AppContainer.Get<DialogService>().Error($"لا رابط للصف رقم {idx + 1} — عدد الروابط {_links.Count} فقط."); return; }

            var link = _links[idx];
            if (link == null)
            { AppContainer.Get<DialogService>().Error("لا يوجد مستند مرتبط بهذا الصف بالذات (الصف تلخيص أو بلا مصدر)."); return; }

            var win = Window.GetWindow(this) as MainWindow;
            if (win == null)
            { AppContainer.Get<DialogService>().Error("تعذّر فتح الشاشة الرئيسية."); return; }
            win.OpenDocument(link.DocType, link.Id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Reports.Drill"); }
    }

    /// <summary>§بحث سريع لحظي في كل أعمدة النتيجة — يعمل مع جميع التقارير.</summary>
    private void QuickSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_rows.Count == 0) return;
        string term = QuickSearchBox.Text?.Trim().ToLower() ?? "";
        if (term.Length == 0) { RenderRows(_rows, _links); return; }
        var filteredIdx = new List<int>();
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool hit = row.Any(cell => cell?.ToString()?.ToLower().Contains(term) == true);
            if (hit) filteredIdx.Add(i);
        }
        RenderRows(filteredIdx.Select(i => _rows[i]).ToList(),
            _links == null ? null : filteredIdx.Select(i => _links[i]).ToList());
    }

    private static Border KpiCard(string label, string value)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xE8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 6)
        };
        b.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label, FontSize = 13, Foreground = Brushes.Gray },
                new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) }
            }
        };
        return b;
    }

    private bool EnsureReport()
    {
        if (_current == null) { AppContainer.Get<DialogService>().Error("شغّل التقرير أولاً."); return false; }
        return true;
    }

    private void Print_Click(object sender, RoutedEventArgs e) { if (EnsureReport()) AppContainer.Get<ExportPrintService>().Print(_current); }
    private void Pdf_Click(object sender, RoutedEventArgs e) { if (EnsureReport()) AppContainer.Get<ExportPrintService>().ExportPdf(_current); }
    private void Excel_Click(object sender, RoutedEventArgs e) { if (EnsureReport()) AppContainer.Get<ExportPrintService>().ExportExcel(_current); }
}
