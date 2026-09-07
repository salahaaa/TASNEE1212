using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §تتبع الصنف: نافذة الرحلة الكاملة — استلام → خطة → أمر → إنتاج → فحص → مخزون → تسليم → فاتورة.
/// تُفتح من شاشة الأصناف (لصنف محدد) أو مستقلة مع تصفية بالعميل/الصنف.
/// </summary>
public class TraceabilityWindow : Window
{
    private readonly ComboBox _customerBox = new() { Width = 220, MinHeight = 28 };
    private readonly ComboBox _productBox = new() { Width = 260, MinHeight = 28 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 6) };
    private readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column };

    private List<(int id, string name)> _customers = new();
    private List<(int id, string name)> _products = new();

    public TraceabilityWindow(int? productId = null, int? customerId = null)
    {
        Title = "🔍 تتبع الصنف — الرحلة الكاملة من الاستلام حتى الفاتورة";
        Width = 1150; Height = 680; MinWidth = 900; MinHeight = 520;
        FlowDirection = FlowDirection.RightToLeft;
        // §B84/B8: كانت CenterScreen (تضيع خلف النوافذ) وبلا زر إغلاق ولا Escape — الآن فوق الأب وتُغلق بـ Escape.
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
        };

        _grid.Columns.Add(new DataGridTextColumn { Header = "المرحلة", Binding = new System.Windows.Data.Binding("Stage"), Width = 150 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "المستند", Binding = new System.Windows.Data.Binding("DocNumber"), Width = 120 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "التاريخ", Binding = new System.Windows.Data.Binding("Date"), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "العميل", Binding = new System.Windows.Data.Binding("Customer"), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الصنف", Binding = new System.Windows.Data.Binding("Product"), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الدفعة", Binding = new System.Windows.Data.Binding("Lot"), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الكمية (كجم)", Binding = new System.Windows.Data.Binding("Qty"), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الكراتين", Binding = new System.Windows.Data.Binding("Cartons"), Width = 70 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("Status"), Width = 150 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "التفاصيل", Binding = new System.Windows.Data.Binding("Detail"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var refreshBtn = new Button { Content = "🔄 تحديث الرحلة", Padding = new Thickness(14, 6, 14, 6) };
        refreshBtn.Click += (_, _) => Refresh();

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        filters.Children.Add(Label("العميل:")); filters.Children.Add(_customerBox);
        filters.Children.Add(Label("الصنف:")); filters.Children.Add(_productBox);
        filters.Children.Add(refreshBtn);
        filters.Children.Add(new TextBlock
        {
            Text = "— الهوية محفوظة في كل مرحلة: الصنف المستلم يظهر باسمه الفعلي حتى التسليم والفاتورة",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), FontSize = 11, Opacity = 0.7
        });

        var body = new DockPanel { Margin = new Thickness(10) };
        body.Children.Add(filters); DockPanel.SetDock(filters, Dock.Top);
        body.Children.Add(_summary); DockPanel.SetDock(_summary, Dock.Top);
        body.Children.Add(_grid);

        Content = body;
        Loaded += (_, _) =>
        {
            LoadFilters(productId, customerId);
            Refresh();
        };
    }

    private static TextBlock Label(string t) => new()
    {
        Text = t, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0), FontWeight = FontWeights.Bold
    };

    private void LoadFilters(int? productId, int? customerId)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _customers = db.Customers.OrderBy(c => c.CustomerName)
                .Select(c => new { c.Id, c.CustomerName }).ToList()
                .Select(x => (x.Id, x.CustomerName)).ToList();
            _products = db.Products.OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.ProductNameAr, p.ItemType }).ToList()
                .Select(x => (x.Id, x.ProductNameAr + (x.ItemType == "Raw" ? " (خام)" : x.ItemType == "Finished" ? " (تام)" : ""))).ToList();

            _customerBox.Items.Clear();
            _customerBox.Items.Add("— كل العملاء —");
            foreach (var c in _customers) _customerBox.Items.Add(c.name);
            _customerBox.SelectedIndex = customerId != null && _customers.Any(c => c.id == customerId)
                ? _customers.FindIndex(c => c.id == customerId) + 1 : 0;

            _productBox.Items.Clear();
            _productBox.Items.Add("— كل الأصناف —");
            foreach (var p in _products) _productBox.Items.Add(p.name);
            _productBox.SelectedIndex = productId != null && _products.Any(p => p.id == productId)
                ? _products.FindIndex(p => p.id == productId) + 1 : 0;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Trace.Filters"); }
    }

    private void Refresh()
    {
        try
        {
            int? custId = _customerBox.SelectedIndex > 0 ? _customers[_customerBox.SelectedIndex - 1].id : null;
            int? prodId = _productBox.SelectedIndex > 0 ? _products[_productBox.SelectedIndex - 1].id : null;

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<ITraceabilityService>();
            var journeys = svc.GetJourneys(custId, prodId);

            var rows = new List<object>();
            foreach (var j in journeys)
            {
                rows.Add(new
                {
                    Stage = $"══ {j.ProductName} ({j.ItemTypeAr}) — ملخص الرحلة ══",
                    DocNumber = "-", Date = "-", Customer = j.CustomerName, Product = j.ProductName, Lot = "-",
                    Qty = 0, Cartons = 0,
                    Status = $"أُنتج {j.ProducedKg:N1} | قُبل {j.AcceptedKg:N1} | سُلّم {j.DeliveredKg:N1}",
                    Detail = $"استُلم {j.ReceivedKg:N1} | خُطط {j.PlannedKg:N1} | مخزون {j.InStockKg:N1} | فُوتر {j.InvoicedKg:N1} | المتبقي {j.RemainingKg:N1}"
                });
                foreach (var s in j.Stages)
                    rows.Add(new
                    {
                        Stage = s.StageAr, DocNumber = s.DocNumber, Date = s.Date ?? "-",
                        Customer = s.CustomerName, Product = s.ProductName, Lot = s.LotCode ?? "-",
                        Qty = s.QtyKg, Cartons = s.Cartons, Status = s.StatusAr ?? "-", Detail = s.Detail ?? "-"
                    });
            }

            _grid.ItemsSource = rows;
            _summary.Text = journeys.Count == 0
                ? "لا توجد رحلات مطابقة — استلم أصنافاً أولاً من شاشة الاستلام."
                : $"عدد الرحلات: {journeys.Count} — كل مرحلة تعرض الصنف باسمه الفعلي (لا أسماء عامة)، والمتبقي = رصيد مخزن التام.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Trace.Refresh"); }
    }
}
