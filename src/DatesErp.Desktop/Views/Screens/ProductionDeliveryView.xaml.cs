using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

public class PdLineUi : INotifyPropertyChanged
{
    public int? OrderId { get; set; }
    public string OrderNumber { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    public int? LotId { get; set; }
    public string LotCode { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public double Available { get; set; }
    public double Delivered { get; set; }
    public double Remaining { get; set; }
    private bool _included = true;
    public bool Included { get => _included; set { _included = value; OnChanged(nameof(Included)); } }
    private double _qty;
    public double Qty { get => _qty; set { _qty = value; OnChanged(nameof(Qty)); } }
    private int _packages;
    public int Packages { get => _packages; set { _packages = value; OnChanged(nameof(Packages)); } }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// §B96 — أوامر تسليم الإنتاج (إدارة الإنتاج — يحررها مدير الإنتاج):
/// إنشاء من مصدر (محضر معتمد/خطة/إقفال) ← تحرير للمخازن (بلا أثر على الأرصدة) ← الاستلام من شاشة المخازن.
/// </summary>
public partial class ProductionDeliveryView : UserControl
{
    private readonly ObservableCollection<PdLineUi> _lines = new();
    private List<object> _records_all = new();
    private List<int> _recordIds = new();
    private List<(int Id, string Label)> _docs = new();
    private readonly List<(string Code, string Title)> _sources = new()
    {
        (DeliverySources.FromCheck, "محضر فحص معتمد"),
        (DeliverySources.FromPlan, "خطة إنتاج (تجاوز بصلاحية)"),
        (DeliverySources.FromClosing, "إقفال خطة (تجاوز بصلاحية)")
    };
    private int _currentId;

    public ProductionDeliveryView()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _lines;
        Loaded += (_, _) => Load();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("أوامر تسليم الإنتاج");
        chrome.SetScreenCode("MRPMPS1021");
        var tb = new Views.ErpToolbar()
            .WithNew((_, _) => NewForm(), "أمر تسليم جديد (F2)")
            .WithSave((_, _) => Save(), "حفظ الأمر كمسودة (F10)")
            .WithCustom("📤 تحرير للمخازن", "ErpApproveButton", (_, _) => Issue(), "تحرير الأمر — مدير الإنتاج")
            .WithCustom("✖ إلغاء الأمر", "ErpDangerButton", (_, _) => Cancel(), "إلغاء أمر لم يبدأ استلامه")
            .WithSearch((_, _) => { RefreshList(); RecSearchBox.Focus(); }, "بحث في أوامر التسليم (F9)")
            .WithUndo((_, _) => UndoSmart(), "تراجع: يعيد آخر نسخة محفوظة — لا يحذف أي أمر")
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithList((_, _) => RefreshList(), "عرض كل أوامر التسليم")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        chrome.SetToolbar(tb);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void Load()
    {
        try
        {
            SourceTypeBox.ItemsSource = _sources.Select(s => s.Title).ToList();
            SourceTypeBox.SelectedIndex = 0;
            DateBox.SelectedDate = DateTime.Now;
            NewForm();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PD.Load"); }
    }

    private string SourceCode() => SourceTypeBox.SelectedIndex >= 0 ? _sources[SourceTypeBox.SelectedIndex].Code : DeliverySources.FromCheck;

    private void SourceType_Changed(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>();
            _docs = svc.GetSourceDocs(SourceCode());
            SourceDocBox.ItemsSource = _docs.Select(d => d.Label).ToList();
            SourceDocBox.SelectedIndex = _docs.Count > 0 ? 0 : -1;
            BypassBox.IsEnabled = DeliverySources.IsBypass(SourceCode());
            if (!BypassBox.IsEnabled) BypassBox.Text = "";
            _lines.Clear();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PD.SourceType"); }
    }

    private void SourceDoc_Changed(object sender, SelectionChangedEventArgs e) => Download();

    private void Download_Click(object sender, RoutedEventArgs e) => Download();

    private void Download()
    {
        try
        {
            if (_currentId > 0) return; // أمر مفتوح — بنوده من البطاقة لا المصدر
            if (SourceDocBox.SelectedIndex < 0 || SourceDocBox.SelectedIndex >= _docs.Count) return;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>();
            var ctx = svc.GetSourceContext(SourceCode(), _docs[SourceDocBox.SelectedIndex].Id);
            _lines.Clear();
            foreach (var l in ctx.Lines)
                _lines.Add(new PdLineUi
                {
                    OrderId = l.OrderId,
                    OrderNumber = l.OrderNumber ?? "—",
                    ProductId = l.ProductId,
                    ProductName = l.ProductName,
                    LotId = l.LotId,
                    LotCode = l.LotCode ?? "—",
                    CustomerId = l.CustomerId,
                    CustomerName = l.CustomerName ?? "—",
                    Available = l.AvailableQtyKg,
                    Delivered = l.DeliveredQtyKg,
                    Remaining = l.RemainingQtyKg,
                    Included = l.RemainingQtyKg > 0.001,
                    Qty = Math.Max(0, Math.Round(l.RemainingQtyKg, 1))
                });
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PD.Download"); }
    }

    private void AllFull_Click(object sender, RoutedEventArgs e)
    {
        foreach (var l in _lines) { l.Included = l.Remaining > 0.001; l.Qty = Math.Max(0, Math.Round(l.Remaining, 1)); }
        ItemsGrid.Items.Refresh();
    }

    private void Save()
    {
        try
        {
            if (_currentId > 0) { AppContainer.Get<DialogService>().Error("الأمر محفوظ — الأوامر المحفوظة تُحرَّر أو تُلغى (لا تعديل بعد الحفظ)."); return; }
            var selected = _lines.Where(l => l.Included && l.Qty > 0.001).ToList();
            if (selected.Count == 0) { AppContainer.Get<DialogService>().Error("ضمّن بنداً واحداً على الأقل بكمية أكبر من صفر."); return; }
            if (SourceDocBox.SelectedIndex < 0) { AppContainer.Get<DialogService>().Error("اختر مستند المصدر."); return; }

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>();
            var r = svc.SaveDelivery(SourceCode(), _docs[SourceDocBox.SelectedIndex].Id,
                (DateBox.SelectedDate ?? DateTime.Now).ToString("dd/MM/yyyy"),
                selected.Select(l => new ProductionDeliveryItemDto
                {
                    OrderId = l.OrderId,
                    ProductId = l.ProductId,
                    LotId = l.LotId,
                    CustomerId = l.CustomerId,
                    PackageCount = l.Packages,
                    QtyKg = l.Qty
                }).ToList(),
                BypassBox.Text, NotesBox.Text);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshList();
            OpenDelivery(r.Id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PD.Save"); }
    }

    private void Issue()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("احفظ الأمر أولاً ثم حرره."); return; }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>();
            var r = svc.IssueDelivery(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message + "\n(التحرير لم يؤثر على الأرصدة — بانتظار سند الاستلام من المخازن)");
            RefreshList();
            OpenDelivery(_currentId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PD.Issue"); }
    }

    private void Cancel()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("افتح أمراً أولاً."); return; }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>();
            var r = svc.CancelDelivery(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshList();
            OpenDelivery(_currentId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PD.Cancel"); }
    }

    private void RefreshList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>();
            var list = svc.GetDeliveries();
            _recordIds = list.Select(c => c.Id).ToList();
            _records_all = list.Select(c => new
            {
                Id = c.Id,
                DocNo = c.DocumentNumber,
                Date = c.DeliveryDate,
                SourceAr = c.SourceTypeAr,
                SourceNo = c.SourceNumber,
                Lines = c.Lines.Count,
                Total = c.Lines.Sum(l => l.QtyKg).ToString("N1"),
                StatusAr = c.StatusAr,
                ReceiptAr = c.ReceiptStatus == "Full" ? "مستلم بالكامل ✅" : c.ReceiptStatus == "Partial" ? "استلام جزئي 🟠" : "بانتظار الاستلام ⏳"
            }).ToList().Cast<object>().ToList();
            ScreenSearch.Apply(RecSearchBox, RecordsGrid, _records_all);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PD.List"); }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void Records_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecordsGrid.SelectedItem == null) return;
        int id = (int)RecordsGrid.SelectedItem.GetType().GetProperty("Id").GetValue(RecordsGrid.SelectedItem);
        OpenDelivery(id);
    }

    private void OpenDelivery(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>();
            var c = svc.GetDelivery(id);
            if (c == null) return;
            _currentId = c.Id;
            DocNoBox.Text = c.DocumentNumber;
            BypassBox.Text = c.BypassReason ?? "";
            var idx = _sources.FindIndex(s => s.Code == c.SourceType);
            SourceTypeBox.SelectedIndex = idx;
            SourceDocBox.ItemsSource = new List<string> { c.SourceNumber };
            SourceDocBox.SelectedIndex = 0;
            _lines.Clear();
            foreach (var l in c.Lines)
                _lines.Add(new PdLineUi
                {
                    OrderId = l.OrderId,
                    OrderNumber = l.OrderNumber ?? "—",
                    ProductId = l.ProductId,
                    ProductName = l.ProductName,
                    LotId = l.LotId,
                    LotCode = l.LotCode ?? "—",
                    CustomerId = l.CustomerId,
                    CustomerName = l.CustomerName ?? "—",
                    Available = l.QtyKg,
                    Delivered = l.ReceivedQtyKg,
                    Remaining = l.RemainingQtyKg,
                    Included = false,
                    Qty = l.QtyKg
                });
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PD.Open"); }
    }

    private void NewForm()
    {
        _currentId = 0;
        DocNoBox.Text = "";
        BypassBox.Text = "";
        NotesBox.Text = "";
        DateBox.SelectedDate = DateTime.Now;
        if (SourceTypeBox.SelectedIndex < 0) SourceTypeBox.SelectedIndex = 0;
        else SourceType_Changed(null, null);
    }

    private void UndoSmart()
    {
        if (_currentId > 0) OpenDelivery(_currentId);
        else NewForm();
    }

    private void Nav(int move)
    {
        if (_recordIds.Count == 0) RefreshList();
        if (_recordIds.Count == 0) return;
        int i = _recordIds.IndexOf(_currentId);
        int n = move == 0 ? 0 : move == int.MaxValue ? _recordIds.Count - 1 : Math.Min(_recordIds.Count - 1, Math.Max(0, i + move));
        OpenDelivery(_recordIds[n]);
    }
}
