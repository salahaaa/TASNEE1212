using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

public class FgItemUi : INotifyPropertyChanged
{
    public int ItemId { get; set; }        // معرف بند السند بعد الحفظ
    public int OrderItemId { get; set; }
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    public string ProductName { get; set; }
    public string LotCode { get; set; }
    public int Packages { get; set; }
    public double Weight { get; set; }     // الكمية المتاحة للتسليم
    private bool _included = true;
    public bool Included { get => _included; set { _included = value; OnChanged(nameof(Included)); } }
    private double _deliverQty;
    public double DeliverQty { get => _deliverQty; set { _deliverQty = value; OnChanged(nameof(DeliverQty)); } }
    private double _receivedQty;
    public double ReceivedQty { get => _receivedQty; set { _receivedQty = value; OnChanged(nameof(ReceivedQty)); } }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// تسليم الإنتاج التام — الدورة القانونية الكاملة:
/// مدير الإنتاج: أمر تسليم متعدد الأصناف (كامل/نصف/استبعاد) → إصدار للمخزن (بلا أثر على الأرصدة)
/// أمين المخزن: سند الاستلام وحده يؤثر على أرصدة مخزن التام (كلي/جزئي/متابعة) ← إلغاء السند يعكس الأرصدة.
/// </summary>
public partial class FinishedGoodsView : UserControl
{
    private List<object> _receipts_all = new();
    private readonly ObservableCollection<FgItemUi> _items = new();
    private List<(int orderId, int qcId)> _eligible = new();
    private List<int> _receiptIds = new();
    private int _currentReceiptId, _currentOrderId, _currentQcId;
    private Views.ErpToolbar _toolbar;

    public FinishedGoodsView()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _items;
        Loaded += (_, _) => Load();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("أمر تسليم الإنتاج التام");
        chrome.SetScreenCode("MRPMPS1015");
        // §1 — الترتيب القياسي الموحد للأزرار الأساسية
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => NewForm(), "سند تسليم جديد (F2)")
            .WithSave((_, _) => SaveAndIssue(), "حفظ وتوريد السند للمخزن — يبقى أمامك كما هو (F10)")
            .WithSearch((_, _) => { RefreshList(); RecSearchBox.Focus(); }, "بحث في سندات التسليم المحفوظة (F9)")
            .WithUndo((_, _) => UndoSmart(), "تراجع: يلغي الإدخالات غير المحفوظة ويعيد آخر نسخة محفوظة — لا يحذف أي سند")
            .WithPrint((_, _) => Print(), "طباعة السند (Ctrl+P)")
            .WithExcel((_, _) => Export())
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithList((_, _) => RefreshList(), "عرض كل سندات التسليم")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        chrome.SetToolbar(_toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

            // الأوامر المؤهلة: لها فحص جودة معتمد
            var orders = db.ProductionOrders
                .Where(o => o.IsApproved && db.QualityChecks.Any(c => c.OrderId == o.Id && c.IsApproved))
                .OrderByDescending(o => o.Id).ToList();
            _eligible = orders.Select(o => (o.Id,
                db.QualityChecks.Where(c => c.OrderId == o.Id && c.IsApproved).OrderByDescending(c => c.Id).Select(c => c.Id).First())).ToList();
            OrderBox.ItemsSource = orders.Select(o =>
                $"{o.DocumentNumber} — {db.Customers.Where(c => c.Id == o.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—"}").ToList();

            // §2 — الشاشة تفتح فارغة في وضع «سند جديد» — نتائج البحث تظهر عند الضغط على «بحث»
            NewForm();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FG.Load"); }

        // §التنقل من التقارير: فتح سند تسليم تام محدد فور تحميل الشاشة
        if (MainWindow.PendingFGIdToOpen is int pendingFG)
        {
            MainWindow.PendingFGIdToOpen = null;
            OpenReceipt(pendingFG);
        }
    }

    /// <summary>§7/§8 — نتائج البحث: نقرتان متتاليتان تعيدان السند كاملاً إلى هذه الواجهة.</summary>
    private void RefreshList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var receipts = db.FinishedGoodsReceipts.OrderByDescending(r => r.Id).ToList();
            _receiptIds = receipts.Select(r => r.Id).ToList();
            _receipts_all = receipts.Select(r => new
            {
                Id = r.Id,
                DocNo = r.DocumentNumber,
                ReceiptNo = r.ReceiptNumber ?? "—",
                OrderNo = db.ProductionOrders.Where(o => o.Id == r.OrderId).Select(o => o.DocumentNumber).FirstOrDefault(),
                Date = Core.Common.UiFormat.D(r.DeliveryDate),
                Total = db.FinishedGoodsReceiptItems.Where(i => i.ReceiptId == r.Id).Sum(i => i.NetWeightKg),
                StatusAr = Core.Common.DocStatuses.ToArabic(r.Status),
                ReceiptStatusAr = r.ReceiptStatus == "Full" ? "مستلم بالكامل ✅" : r.ReceiptStatus == "Partial" ? "استلام جزئي 🟠" : "بانتظار الاستلام ⏳"
            }).ToList().Cast<object>().ToList();
            ScreenSearch.Apply(RecSearchBox, ReceiptsGrid, _receipts_all);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FG.List"); }
    }

    /// <summary>§13 — التراجع: سند جديد ← إفراغ؛ سند محفوظ ← إعادة آخر نسخة محفوظة دون حذف.</summary>
    private void UndoSmart()
    {
        if (_currentReceiptId > 0) OpenReceipt(_currentReceiptId);
        else NewForm();
    }

    /// <summary>تعبئة البنود: الكمية المتاحة = المنتَج − المسلَّم سابقاً عبر كل السندات.</summary>
    private void Order_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (OrderBox.SelectedIndex < 0 || OrderBox.SelectedIndex >= _eligible.Count) return;
        var (orderId, qcId) = _eligible[OrderBox.SelectedIndex];
        _currentOrderId = orderId; _currentQcId = qcId;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var orderItems = db.ProductionOrderItems.Where(i => i.OrderId == orderId).ToList();
            _items.Clear();
            foreach (var oi in orderItems)
            {
                double delivered = db.FinishedGoodsReceiptItems
                    .Join(db.FinishedGoodsReceipts, i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                    .Where(x => x.r.OrderId == orderId && x.i.ProductId == oi.ProductId)
                    .Sum(x => x.i.NetWeightKg);
                double available = oi.ProducedQtyKg - delivered;
                if (available <= 0.001 && oi.ProducedQtyKg <= 0) continue;
                _items.Add(new FgItemUi
                {
                    OrderItemId = oi.Id,
                    ProductId = oi.ProductId,
                    LotId = oi.LotId,
                    ProductName = db.Products.Where(p => p.Id == oi.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    LotCode = db.Lots.Where(l => l.Id == oi.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                    Packages = oi.ProducedCartons,
                    Weight = Math.Max(0, available),
                    DeliverQty = Math.Max(0, available),
                    Included = available > 0.001
                });
            }
            ShowImpact();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FG.OrderChanged"); }
    }

    private void AllFull_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) { i.Included = true; i.DeliverQty = i.Weight; }
        ItemsGrid.Items.Refresh();
    }

    private void AllHalf_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) { i.Included = true; i.DeliverQty = Math.Round(i.Weight / 2, 1); }
        ItemsGrid.Items.Refresh();
    }

    /// <summary>§10 — حفظ السند ثم توريده للمخزن: الإصدار لا يمس الأرصدة.</summary>
    private void SaveAndIssue()
    {
        try
        {
            if (_currentOrderId == 0) { AppContainer.Get<DialogService>().Error("اختر أمر إنتاج له فحص جودة معتمد."); return; }
            var selected = _items.Where(i => i.Included && i.DeliverQty > 0.001).ToList();
            if (selected.Count == 0) { AppContainer.Get<DialogService>().Error("ضمّن بنداً واحداً على الأقل بكمية أكبر من صفر (أو أزل الاستبعاد)."); return; }

            using var scope = AppContainer.NewScope();
            var svc = (IFinishedGoodsService)scope.ServiceProvider.GetService(typeof(IFinishedGoodsService));
            var r = svc.SaveReceipt(_currentOrderId, _currentQcId,
                (DateBox.SelectedDate ?? DateTime.Now).ToString("dd/MM/yyyy"),
                selected.Select(i => new FinishedGoodsItemDto
                {
                    ProductId = i.ProductId,
                    LotId = i.LotId,
                    PackageCount = i.Packages,
                    NetWeightKg = i.DeliverQty
                }).ToList());
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            _currentReceiptId = r.Id;
            DocNoBox.Text = r.DocumentNumber;

            var r2 = svc.Issue(_currentReceiptId);
            if (!r2.Ok) { AppContainer.Get<DialogService>().Error(r2.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message + "\n" + r2.Message + "\n(الإصدار لم يؤثر على الأرصدة — بانتظار سند الاستلام المخزني)");
            RefreshList();
            OpenReceipt(_currentReceiptId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FG.SaveIssue"); }
    }

    /// <summary>§7/§8 — سند الاستلام المخزني: وحده يؤثر على الأرصدة، كلياً أو جزئياً، مع سندات متابعة.</summary>
    private void Receive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentReceiptId == 0) { AppContainer.Get<DialogService>().Error("افتح سند تسليم مُصدراً."); return; }
            Dictionary<int, double> received = null;
            if (!string.IsNullOrWhiteSpace(ReceiveQtyBox.Text))
            {
                if (!double.TryParse(ReceiveQtyBox.Text, out var cartons) || cartons <= 0)
                { AppContainer.Get<DialogService>().Error("كمية غير صالحة — اترك الحقل فارغاً للاستلام الكامل."); return; }
                var sel = ItemsGrid.SelectedItem as FgItemUi;
                if (sel == null || sel.ItemId == 0)
                { AppContainer.Get<DialogService>().Error("اختر بنداً من الجدول لاستلام كمية جزئية منه."); return; }
                // §قاعدة الكرتون: الإدخال بالكرتون ويُشتق الكجم المكافئ من وزن كرتون البند
                double unitW = sel.Packages > 0 ? sel.Weight / sel.Packages : 7.5;
                received = new Dictionary<int, double> { [sel.ItemId] = Math.Round(cartons * (unitW > 0 ? unitW : 7.5), 1) };
            }

            using var scope = AppContainer.NewScope();
            var svc = (IFinishedGoodsService)scope.ServiceProvider.GetService(typeof(IFinishedGoodsService));
            var r = svc.Receive(_currentReceiptId, received);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            ReceiveQtyBox.Text = "";
            OpenReceipt(_currentReceiptId);
            ShowImpact();
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FG.Receive"); }
    }

    /// <summary>إلغاء السند يعكس أرصدة مخزن التام بدقة (§6).</summary>
    private void Unapprove_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentReceiptId == 0) return;
            if (!AppContainer.Get<DialogService>().Confirm("إلغاء السند سيعكس كل أرصدة الاستلام المرتبطة به من مخزن التام. متابعة؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (IFinishedGoodsService)scope.ServiceProvider.GetService(typeof(IFinishedGoodsService));
            var r = svc.Unapprove(_currentReceiptId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            OpenReceipt(_currentReceiptId);
            ShowImpact();
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FG.Unapprove"); }
    }

    /// <summary>عرض الأثر الفعلي على مخزن التام حسب عميل الأمر.</summary>
    private void ShowImpact()
    {
        try
        {
            if (_currentOrderId == 0) { ImpactBox.Visibility = Visibility.Collapsed; return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var order = db.ProductionOrders.FirstOrDefault(o => o.Id == _currentOrderId);
            if (order == null) return;
            var whFg = db.Warehouses.FirstOrDefault(w => w.WarehouseCode == "WFG")?.Id ?? 0;
            var balances = db.StockBalances
                .Where(b => b.WarehouseId == whFg && b.CustomerId == order.CustomerId)
                .ToList();
            var custName = db.Customers.Where(c => c.Id == order.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—";
            ImpactBox.Visibility = Visibility.Visible;
            if (balances.Count == 0)
            {
                ImpactText.Text = $"أثر الاستلام على مخزن التام — رصيد العميل «{custName}»: لا يوجد رصيد بعد.";
                return;
            }
            var lines = balances.Select(b =>
                $"{db.Products.Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()}: {b.QtyKg:N1} كجم");
            ImpactText.Text = $"أثر الاستلام على مخزن التام — رصيد العميل «{custName}»:\n" + string.Join(" | ", lines);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FG.Impact"); }
    }

    private void NewForm()
    {
        _currentReceiptId = 0;
        _currentOrderId = 0;
        _items.Clear();
        DocNoBox.Text = "(تلقائي عند الحفظ)";
        ImpactBox.Visibility = Visibility.Collapsed;
    }

    private void Nav(int dir)
    {
        if (_receiptIds.Count == 0) return;
        int idx = _receiptIds.IndexOf(_currentReceiptId);
        idx = dir switch { 0 => 0, int.MaxValue => _receiptIds.Count - 1, _ => Math.Clamp(idx + dir, 0, _receiptIds.Count - 1) };
        OpenReceipt(_receiptIds[idx]);
    }

    private void OpenReceipt(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var rcpt = db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == id);
            if (rcpt == null) return;
            _currentReceiptId = rcpt.Id;
            _currentOrderId = rcpt.OrderId;
            DocNoBox.Text = rcpt.DocumentNumber;
            DateBox.SelectedDate = rcpt.DeliveryDate;
            _items.Clear();
            foreach (var it in rcpt.Items)
            {
                _items.Add(new FgItemUi
                {
                    ItemId = it.Id,
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    ProductName = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    LotCode = db.Lots.Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                    Packages = it.PackageCount,
                    Weight = it.NetWeightKg,
                    DeliverQty = it.NetWeightKg,
                    ReceivedQty = it.ReceivedQtyKg,
                    Included = true
                });
            }
            // §8 — السند المستلم بالكامل يُقفل: لا يمكن تكرار الاستلام
            bool full = rcpt.ReceiptStatus == "Full";
            ReceiveBtn.IsEnabled = !full;
            ReceiveQtyBox.IsEnabled = !full;
            ShowImpact();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "FG.Open"); }
    }

    private void Print()
    {
        // §نمط خطط الإنتاج: سند استلام تام رسمي مع معاينة وPDF
        var m = new PhaseDocModel
        {
            // §القالب المرجعي print_delivery.html
            DocTitle = "سند تسليم واستلام إنتاج تام (WFG)",
            MainTitle = "📦 بنود الإنتاج التام والمخرجات الثانوية المسلمة للمستودعات",
            DocNo = DocNoBox.Text,
            StatusAr = _currentReceiptId > 0 ? "محفوظ" : "مسودة",
            Columns = new[] { "الصنف", "الدفعة", "العبوات (كرتون)", "الوزن المكافئ (كجم)", "المستلم فعلياً (كجم مكافئ)" },
            Signatures = { "مسؤول التعبئة والتغليف", "ضابط فحص الجودة", "أمين مستودع الإنتاج التام WFG", "مدير إدارة الإنتاج / الاعتماد" }
        };
        foreach (var i in _items)
            m.Rows.Add(new object[] { i.ProductName, i.LotCode, i.Packages, i.Weight, i.ReceivedQty });
        m.Totals.Add(("إجمالي العبوات (كرتون)", _items.Sum(i => i.Packages).ToString("N0")));
        m.Totals.Add(("إجمالي الوزن (كجم)", _items.Sum(i => i.Weight).ToString("N1")));
        new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}", p => PhasePrint.ExportPdf(m, p))
        { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void Export()
    {
        var report = new ReportResult
        {
            TitleAr = $"سند تسليم الإنتاج {DocNoBox.Text}",
            Columns = new List<string> { "الصنف", "الدفعة", "العبوات", "الوزن (كجم)", "المستلم (كجم)" },
            Rows = _items.Select(i => new object[] { i.ProductName, i.LotCode, i.Packages, i.Weight, i.ReceivedQty }).ToList()
        };
        AppContainer.Get<ExportPrintService>().ExportExcel(report);
    }

    private void ReceiptsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReceiptsGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(ReceiptsGrid.SelectedItem) is int id)
            OpenReceipt(id);
    }

    /// <summary>§بحث وفلترة لحظية على كل الأعمدة.</summary>
    private void RecSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ScreenSearch.Apply(RecSearchBox, ReceiptsGrid, _receipts_all);
}

