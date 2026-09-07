using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>§لون حالة أمر الإنتاج — ألوان مميزة لكل حالة في الجدول.</summary>
public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        DocStatuses.Draft => new SolidColorBrush(Color.FromRgb(0x9e, 0x9e, 0x9e)),
        DocStatuses.Approved => new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xc0)),
        DocStatuses.Scheduled => new SolidColorBrush(Color.FromRgb(0x00, 0x83, 0x8f)),
        DocStatuses.InProgress => new SolidColorBrush(Color.FromRgb(0xef, 0x6c, 0x00)),
        DocStatuses.Stopped => new SolidColorBrush(Color.FromRgb(0xc6, 0x28, 0x28)),
        DocStatuses.Completed => new SolidColorBrush(Color.FromRgb(0x2e, 0x7d, 0x32)),
        DocStatuses.Closed => new SolidColorBrush(Color.FromRgb(0x1b, 0x5e, 0x20)),
        DocStatuses.Cancelled => new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75)),
        _ => new SolidColorBrush(Color.FromRgb(0x9e, 0x9e, 0x9e))
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class ProgressPctConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? $"{d:N0}%" : "0%";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>صف في جدول الأوامر — بالهوية الكاملة: الصنف المستلم والمنتج النهائي لكل أمر.</summary>
public class OrderRowUi
{
    public int Id { get; set; }
    public string OrderNumber { get; set; }
    public string PlanNumber { get; set; }
    public string Date { get; set; }
    public string Customer { get; set; }
    public string RawName { get; set; }       // الصنف المستلم — لا أسماء عامة أبداً
    public string ProductNames { get; set; }  // المنتج النهائي
    public double PlannedKg { get; set; }
    public double ProducedKg { get; set; }
    public double RemainingKg { get; set; }
    public string ShiftName { get; set; }
    public string LineName { get; set; }
    public string StartTime { get; set; }
    public string ExpectedEnd { get; set; }
    public string Status { get; set; }
    public string StatusAr { get; set; }
    public string StatusKey { get; set; }
    public double ProgressPct { get; set; }
    public string SearchBlob { get; set; }
}

/// <summary>
/// §أوامر الإنتاج: تحويل بنود الخطة المعتمدة إلى أوامر تنفيذية — بلا إعادة إدخال،
/// مع بطاقة ملخص وشريط تقدم وسجل عمليات وطاقة محسوبة وحالة ملونة لكل أمر.
/// </summary>
public partial class OrdersView : UserControl
{
    private readonly ObservableCollection<OrderRowUi> _rows = new();
    private List<(int id, string name)> _customers = new();
    private List<(int id, string name)> _products = new();
    private List<(int id, string name)> _shifts = new();

    public OrdersView()
    {
        InitializeComponent();
        OrdersGrid.ItemsSource = _rows;
        Loaded += (_, _) => Init();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("أوامر الإنتاج — من الخطة المعتمدة إلى التنفيذ والفحص");
        chrome.SetScreenCode("MRPMPS1007");
        var tb = new Views.ErpToolbar()
            .WithNew((_, _) => NewOrder_Click(null, null), "أمر إنتاج جديد من خطة معتمدة")
            .WithRefresh((_, _) => RefreshList())
            .WithPrint((_, _) => Print_Click(null, null))
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        chrome.SetToolbar(tb);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private bool Can(string module, string action)
    {
        try { return AppContainer.Get<SessionContext>().Can(module, action); }
        catch { return false; }
    }

    private void Init()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _customers = db.Customers.AsNoTracking().OrderBy(c => c.CustomerName)
                .Select(c => new { c.Id, c.CustomerName }).ToList().Select(x => (x.Id, x.CustomerName)).ToList();
            _products = db.Products.AsNoTracking().OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.ProductNameAr }).ToList().Select(x => (x.Id, x.ProductNameAr)).ToList();
            _shifts = db.Shifts.AsNoTracking().OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.ShiftNameAr }).ToList().Select(x => (x.Id, x.ShiftNameAr)).ToList();

            FCustomer.Items.Clear(); FCustomer.Items.Add("— الكل —");
            foreach (var c in _customers) FCustomer.Items.Add(c.name);
            FCustomer.SelectedIndex = 0;

            FProduct.Items.Clear(); FProduct.Items.Add("— الكل —");
            foreach (var p in _products) FProduct.Items.Add(p.name);
            FProduct.SelectedIndex = 0;

            FShift.Items.Clear(); FShift.Items.Add("— الكل —");
            foreach (var s in _shifts) FShift.Items.Add(s.name);
            FShift.SelectedIndex = 0;

            FStatus.Items.Clear();
            FStatus.Items.Add("— الكل —");
            foreach (var st in new[] { DocStatuses.Draft, DocStatuses.Approved, DocStatuses.Scheduled, DocStatuses.InProgress,
                                       DocStatuses.Stopped, DocStatuses.Completed, DocStatuses.Closed, DocStatuses.Cancelled })
                FStatus.Items.Add(DocStatuses.ToArabic(st));
            FStatus.SelectedIndex = 0;

            // §2/§27 — الشاشة تفتح فارغة نظيفة في وضع «جديد» — لا عرض تلقائي للبيانات
            _rows.Clear();
            ShowListArea();

            // §التنقل من التقارير: فتح أمر محدد فور تحميل الشاشة
            if (MainWindow.PendingOrderIdToOpen is int pendingOrder)
            {
                MainWindow.PendingOrderIdToOpen = null;
                ShowDocument(pendingOrder);
            }
            // §B94: لا قفز بين الشاشات — الترحيل من داخل شاشة الأوامر فقط (زرها الخاص).
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Orders.Init"); }
    }

    // ═══════════════════════════ المستند داخل الواجهة الرئيسية (§5/§9) ═══════════════════════════

    /// <summary>فتح مستند أمر الإنتاج كاملاً في الواجهة الرئيسية نفسها — لا نافذة منفصلة.</summary>
    private void ShowDocument(int orderId)
    {
        DocHost.Children.Clear();
        DocHost.Children.Add(new OrderDocumentPanel(orderId));
        ListArea.Visibility = Visibility.Collapsed;
        DocArea.Visibility = Visibility.Visible;
    }

    /// <summary>§6 — الخروج من المستند: يبقى محفوظاً في النظام وتعود الواجهة جاهزة.</summary>
    private void BackToList_Click(object sender, RoutedEventArgs e)
    {
        ShowListArea();
        RefreshList();
    }

    private void ShowListArea()
    {
        DocArea.Visibility = Visibility.Collapsed;
        ListArea.Visibility = Visibility.Visible;
    }

    // ═══════════════════════════ الجدول والفلاتر ═══════════════════════════

    private void RefreshList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

            var all = new List<OrderRowUi>();
            var orders = db.ProductionOrders.AsNoTracking().OrderByDescending(o => o.Id).ToList();
            foreach (var o in orders)
            {
                var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == o.Id).ToList();
                var productNames = string.Join(" + ", items.Select(i => i.ProductId).Distinct()
                    .Select(pid => _products.FirstOrDefault(p => p.id == pid).name ?? $"#{pid}"));
                var lotIds = items.Select(i => i.LotId).Where(l => l != null).Distinct().ToList();
                // §إصلاح: إغلاق استعلام القاعدة بـ ToList قبل العمليات الذاكرية على _products
                var rawNames = string.Join(" + ", db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).Select(l => l.ProductId).Distinct().ToList()
                    .Select(pid => _products.FirstOrDefault(p => p.id == pid).name ?? "-"));
                var planNo = o.SourcePlanId != null
                    ? db.ProductionPlans.AsNoTracking().Where(p => p.Id == o.SourcePlanId).Select(p => p.DocumentNumber).FirstOrDefault() ?? "-"
                    : "يدوي";
                double planned = items.Sum(i => i.PlannedQtyKg);
                double produced = items.Sum(i => i.ProducedQtyKg);
                string status = o.IsClosed ? DocStatuses.Closed : o.Status;

                // وقت البداية الفعلي + النهاية المتوقع (التاريخ + بداية الوردية + ساعات الأمر)
                string start = db.ProductionExecutions.AsNoTracking()
                    .Where(e => e.OrderId == o.Id && e.StartDateTime != null)
                    .OrderBy(e => e.StartDateTime).Select(e => e.StartDateTime).FirstOrDefault() is System.DateTime stv ? Core.Common.UiFormat.DT(stv) : "-";
                string expected = "-";
                var shift = o.ShiftId != null ? db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == o.ShiftId) : null;
                if (o.ProductionDate != null && shift != null && TimeSpan.TryParse(shift.StartTime, out var st))
                {
                    double hours = 0;
                    foreach (var it in items.Where(i => i.PlannedCartons > 0))
                    {
                        double rate = RateFor(db, it.ProductId, o.ShiftId.Value, it.PackagingTypeId);
                        hours += it.PlannedCartons / (rate > 0 ? rate : 500);
                    }
                    expected = Core.Common.UiFormat.DT(o.ProductionDate.Value.Date.Add(st).AddHours(hours));
                }

                all.Add(new OrderRowUi
                {
                    Id = o.Id,
                    OrderNumber = o.DocumentNumber,
                    PlanNumber = planNo,
                    Date = Core.Common.UiFormat.D(o.ProductionDate),
                    Customer = o.CustomerId != null ? _customers.FirstOrDefault(c => c.id == o.CustomerId).name ?? "-" : "-",
                    RawName = string.IsNullOrEmpty(rawNames) ? "-" : rawNames,
                    ProductNames = string.IsNullOrEmpty(productNames) ? "-" : productNames,
                    PlannedKg = Math.Round(planned, 1),
                    ProducedKg = Math.Round(produced, 1),
                    RemainingKg = Math.Round(Math.Max(0, planned - produced), 1),
                    ShiftName = shift?.ShiftNameAr ?? "-",
                    LineName = o.LineId != null ? db.ProductionLines.AsNoTracking().Where(l => l.Id == o.LineId).Select(l => l.LineNameAr).FirstOrDefault() ?? "-" : "-",
                    StartTime = start,
                    ExpectedEnd = expected,
                    Status = status,
                    StatusAr = DocStatuses.ToArabic(status),
                    StatusKey = status,
                    ProgressPct = planned > 0 ? Math.Round(Math.Min(100, produced / planned * 100), 1) : 0,
                    SearchBlob = $"{o.DocumentNumber} {planNo} {o.CustomerId} {productNames} {rawNames} {status}"
                });
            }

            ApplyFilters(all);
            BuildChips(all);
            SetButtonStates();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Orders.Refresh"); }
    }

    private static double RateFor(DatesErpDbContext db, int productId, int shiftId, int? packId)
    {
        var cap = packId != null
            ? db.ProductShiftCapacities.AsNoTracking().FirstOrDefault(c => c.ProductId == productId && c.ShiftId == shiftId && c.PackagingTypeId == packId && c.IsActive)
            : null;
        if (cap == null || cap.HourlyProductionRate <= 0)
            cap = db.ProductShiftCapacities.AsNoTracking().FirstOrDefault(c => c.ProductId == productId && c.ShiftId == shiftId && c.PackagingTypeId == null && c.IsActive);
        if (cap != null && cap.HourlyProductionRate > 0) return cap.HourlyProductionRate;
        return db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.HourlyProductionRate).FirstOrDefault();
    }

    private void ApplyFilters(List<OrderRowUi> all)
    {
        IEnumerable<OrderRowUi> q = all;
        if (!string.IsNullOrWhiteSpace(FOrderNo.Text)) q = q.Where(r => r.OrderNumber.Contains(FOrderNo.Text.Trim()));
        if (!string.IsNullOrWhiteSpace(FPlanNo.Text)) q = q.Where(r => r.PlanNumber.Contains(FPlanNo.Text.Trim()));
        if (FCustomer.SelectedIndex > 0)
        {
            var cid = _customers[FCustomer.SelectedIndex - 1].id;
            q = q.Where(r => r.SearchBlob.Contains(cid.ToString()));
        }
        if (FProduct.SelectedIndex > 0) q = q.Where(r => r.ProductNames.Contains(_products[FProduct.SelectedIndex - 1].name));
        if (FDateFrom.SelectedDate != null) q = q.Where(r => string.CompareOrdinal(r.Date, FDateFrom.SelectedDate.Value.ToString(Core.Common.UiFormat.DatePattern)) >= 0);
        if (FDateTo.SelectedDate != null) q = q.Where(r => r.Date != "-" && string.CompareOrdinal(r.Date, FDateTo.SelectedDate.Value.ToString(Core.Common.UiFormat.DatePattern)) <= 0);
        if (FShift.SelectedIndex > 0) q = q.Where(r => r.ShiftName == _shifts[FShift.SelectedIndex - 1].name);
        if (FStatus.SelectedIndex > 0)
        {
            var statusKey = new[] { DocStatuses.Draft, DocStatuses.Approved, DocStatuses.Scheduled, DocStatuses.InProgress,
                                    DocStatuses.Stopped, DocStatuses.Completed, DocStatuses.Closed, DocStatuses.Cancelled }[FStatus.SelectedIndex - 1];
            q = q.Where(r => r.Status == statusKey);
        }
        _rows.Clear();
        foreach (var r in q) _rows.Add(r);
    }

    private void BuildChips(List<OrderRowUi> all)
    {
        ChipsPanel.Children.Clear();
        foreach (var st in new[] { DocStatuses.Draft, DocStatuses.Scheduled, DocStatuses.InProgress, DocStatuses.Stopped, DocStatuses.Completed, DocStatuses.Cancelled })
        {
            int count = all.Count(r => r.Status == st);
            var brush = (Brush)new StatusToBrushConverter().Convert(st, null, null, null);
            var chip = new Border
            {
                Background = brush, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 4), Cursor = System.Windows.Input.Cursors.Hand
            };
            chip.Child = new TextBlock { Text = $"{DocStatuses.ToArabic(st)}: {count}", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11.5 };
            string key = st;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                var arr = new[] { DocStatuses.Draft, DocStatuses.Approved, DocStatuses.Scheduled, DocStatuses.InProgress,
                                  DocStatuses.Stopped, DocStatuses.Completed, DocStatuses.Closed, DocStatuses.Cancelled };
                FStatus.SelectedIndex = Array.IndexOf(arr, key) + 1;
                ApplyFilters(all);
            };
            ChipsPanel.Children.Add(chip);
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        FOrderNo.Text = ""; FPlanNo.Text = "";
        FCustomer.SelectedIndex = 0; FProduct.SelectedIndex = 0; FShift.SelectedIndex = 0; FStatus.SelectedIndex = 0;
        FDateFrom.SelectedDate = null; FDateTo.SelectedDate = null;
        RefreshList();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    // ═══════════════════════════ العمليات على الأمر المحدد ═══════════════════════════

    private OrderRowUi SelectedRow() => OrdersGrid.SelectedItem as OrderRowUi;

    private void SetButtonStates()
    {
        var r = SelectedRow();
        NewBtn.IsEnabled = Can("production", "Create");
        // §B80: التعديل والحذف لمسودة لم يبدأ تنفيذها — والحذف بصلاحية Delete
        bool draftNoExec = r != null && r.Status == DocStatuses.Draft;
        EditOrderBtn.IsEnabled = draftNoExec && Can("production", "Edit");
        DeleteOrderBtn.IsEnabled = draftNoExec && Can("production", "Delete");
        ApproveBtn.IsEnabled = r != null && Can("production", "Approve");
        UnapproveBtn.IsEnabled = r != null && Can("production", "Cancel");
        StartBtn.IsEnabled = r != null && Can("execution", "Create");
        StopBtn.IsEnabled = r != null && r.Status == DocStatuses.InProgress && Can("execution", "Edit");
        ResumeBtn.IsEnabled = r != null && r.Status == DocStatuses.Stopped && Can("execution", "Edit");
        CloseDayBtn.IsEnabled = r != null && r.Status is DocStatuses.InProgress or DocStatuses.Scheduled or DocStatuses.Approved && Can("execution", "Edit");
        CancelOrderBtn.IsEnabled = r != null && Can("production", "Cancel");
    }

    private void OrdersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => SetButtonStates();

    private void NewOrder_Click(object sender, RoutedEventArgs e)
    {
        if (!Can("production", "Create")) { AppContainer.Get<DialogService>().Error("لا تملك صلاحية إنشاء أوامر إنتاج."); return; }
        // §الواجهة الرئيسية: لوحة إنشاء الأمر تُفتح داخل المستند لا في نافذة منبثقة
        var panel = new NewOrderPanel();
        panel.OrderCreated += id => ShowDocument(id);
        DocHost.Children.Clear();
        DocHost.Children.Add(panel);
        ListArea.Visibility = Visibility.Collapsed;
        DocArea.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// §B93 — 📋 ترحيل خطة إلى أوامر: نافذة اختيار الخطة المعتمدة والفترة والوردية، ثم الترحيل المجمع.
    /// §B94: الترحيل من داخل شاشة الأوامر فقط — لا قفز من شاشة التخطيط (نظام صلاحيات).
    /// </summary>
    private void IssuePlan_Click(object sender, RoutedEventArgs e)
    {
        if (!Can("production", "Create")) { AppContainer.Get<DialogService>().Error("لا تملك صلاحية إنشاء أوامر إنتاج."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var plans = db.ProductionPlans.AsNoTracking()
                .Where(p => p.IsApproved && !p.IsClosed && p.Status != DocStatuses.Cancelled)
                .OrderByDescending(p => p.Id)
                .Select(p => new { p.Id, p.DocumentNumber, p.PlanTitle }).ToList()
                .Select(x => (x.Id, $"{x.DocumentNumber} — {x.PlanTitle}")).ToList();
            if (plans.Count == 0) { AppContainer.Get<DialogService>().Error("لا توجد خطط معتمدة قابلة للترحيل — اعتمد خطة أولاً."); return; }
            var shifts = db.Shifts.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.ShiftNameAr }).ToList().Select(x => (x.Id, x.ShiftNameAr)).ToList();
            var win = new IssuePlanWindow(plans, shifts) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true) RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Orders.IssuePlan"); }
    }

    /// <summary>
    /// §B80 — تعديل الأمر: يفتح وثيقة الأمر في الواجهة الرئيسية حيث تُعدَّل بيانات التنفيذ
    /// (التاريخ/الوردية/الخط/الملاحظات) وكميات البنود (كراتين) ما دام الأمر مسودة لم يبدأ تنفيذها.
    /// </summary>
    private void EditOrder_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRow();
        if (r == null) { AppContainer.Get<DialogService>().Error("اختر أمر إنتاج من الجدول أولاً."); return; }
        if (r.Status != DocStatuses.Draft)
        { AppContainer.Get<DialogService>().Info("تعديل بنود الأمر متاح قبل بدء الإنتاج (مسودة).\nللأوامر المعتمدة: ألغِ الاعتماد أولاً حسب صلاحيتك، أو ألغِ الأمر وأنشئ بديلاً."); return; }
        ShowDocument(r.Id);
    }

    /// <summary>§B80 — حذف الأمر (مسودة بلا تنفيذ) — الحراس كاملة في الـ Backend.</summary>
    private void DeleteOrder_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRow();
        if (r == null) { AppContainer.Get<DialogService>().Error("اختر أمر إنتاج من الجدول أولاً."); return; }
        if (!AppContainer.Get<DialogService>().Confirm($"حذف أمر الإنتاج {r.OrderNumber}؟\n(المسودة التي لم يبدأ تنفيذها فقط — يبقى السجل في التدقيق)")) return;
        WithSelected((s, id) => s.DeleteOrder(id), "حذف الأمر");
    }

    /// <summary>§9 — نقرتان متتاليتان على المستند في نتائج البحث ← يعود كاملاً إلى الواجهة الرئيسية.</summary>
    private void OrdersGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var r = SelectedRow();
        if (r == null) return;
        ShowDocument(r.Id);
    }

    private void WithSelected(Func<IProductionOrderService, int, OpResult> op, string what)
    {
        var r = SelectedRow();
        if (r == null) { AppContainer.Get<DialogService>().Error("اختر أمر إنتاج من الجدول أولاً."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var res = op(svc, r.Id);
            if (!res.Ok) AppContainer.Get<DialogService>().Error(res.Message, what);
            else AppContainer.Get<DialogService>().Info(res.Message, what);
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, what); }
    }

    private void Approve_Click(object sender, RoutedEventArgs e) => WithSelected((s, id) => s.ApproveOrder(id), "اعتماد الأمر");
    private void Unapprove_Click(object sender, RoutedEventArgs e) => WithSelected((s, id) => s.UnapproveOrder(id), "إلغاء الاعتماد");
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRow();
        if (r == null) { AppContainer.Get<DialogService>().Error("اختر أمر إنتاج من الجدول أولاً."); return; }
        if (!AppContainer.Get<DialogService>().Confirm($"بدء الإنتاج للأمر {r.OrderNumber}؟\nسيُسجل وقت البداية واسم المستخدم وتتحول الحالة إلى «قيد التنفيذ».")) return;
        WithSelected((s, id) => s.StartOrder(id), "بدء الإنتاج");
    }
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRow();
        if (r == null) return;
        var dlg = new InputDialog("إيقاف الأمر مؤقتاً", "سبب الإيقاف (إجباري — 10 أحرف على الأقل):");
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;   // §B102: السبب إجباري
        WithSelected((s, id) => s.StopOrder(id, dlg.Value.Trim()), "إيقاف الأمر");
    }
    private void Resume_Click(object sender, RoutedEventArgs e) => WithSelected((s, id) => s.ResumeOrder(id), "استئناف الأمر");

    private void CancelOrder_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRow();
        if (r == null) { AppContainer.Get<DialogService>().Error("اختر أمر إنتاج من الجدول أولاً."); return; }
        if (!AppContainer.Get<DialogService>().Confirm($"إلغاء الأمر {r.OrderNumber}؟\nإن كان معتمداً سيُعكس ما صُرف (المواد المساعدة، والخام إن كان يومه مقفلاً)، ويعود المتبقي للخطة.\nيبقى الأمر في السجل للتدقيق.")) return;
        var dlg = new InputDialog("إلغاء أمر الإنتاج", "سبب الإلغاء (اختياري):");
        string reason = dlg.ShowDialog() == true ? dlg.Value : "";
        WithSelected((s, id) => s.CancelOrder(id, reason), "إلغاء الأمر");
    }

    /// <summary>§16 — إقفال يوم الإنتاج من داخل الأمر: المنتَج والمخرجات والإرسال للفحص.</summary>
    private void CloseDay_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRow();
        if (r == null) { AppContainer.Get<DialogService>().Error("اختر أمر إنتاج من الجدول أولاً."); return; }
        var dlg = new CloseDayDialog(r.Id, r.OrderNumber, r.RemainingKg) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IExecutionService>();
            var res = svc.CloseProductionDay(r.Id, dlg.ProducedKg, dlg.ProducedCartons, dlg.HashfKg, dlg.NawaKg, dlg.WastageKg,
                dlg.CarryToNextDay, dlg.Downtimes, dlg.SendToQuality, dlg.Notes, dlg.ByProducts, consumedRawKg: dlg.ConsumedKg, itemQtys: dlg.ItemQtys,
                actualAux: dlg.ActualAux, emptyCartonsActual: dlg.EmptyCartonsActual);
            if (!res.Ok) AppContainer.Get<DialogService>().Error(res.Message, "إقفال يوم الإنتاج");
            else AppContainer.Get<DialogService>().Info(res.Message, "إقفال يوم الإنتاج");
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Orders.CloseDay"); }
    }

    // ═══════════════════════════ الطباعة الرسمية ═══════════════════════════

    private ReportResult BuildPrintReport(OrderRowUi r)
    {
        using var scope = AppContainer.NewScope();
        var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var card = svc.GetOrderCard(r.Id);
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == r.Id).ToList();

        var report = new ReportResult
        {
            TitleAr = $"أمر إنتاج رقم: {r.OrderNumber}",
            Columns = new List<string> { "الدفعة", "العميل", "الصنف المستلم", "المنتج النهائي", "العبوة", "الكمية (كجم)", "الكراتين", "المنتَج (كجم)" }
        };
        foreach (var it in items)
        {
            report.Rows.Add(new object[]
            {
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "-",
                _customers.FirstOrDefault(c => c.id == (it.CustomerId ?? 0)).name ?? "-",
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => p.ProductNameAr).FirstOrDefault() ?? "-",
                _products.FirstOrDefault(p => p.id == it.ProductId).name ?? "-",
                it.PackagingTypeId != null ? db.PackagingTypes.AsNoTracking().Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() : "-",
                it.PlannedQtyKg, it.PlannedCartons, it.ProducedQtyKg
            });
        }
        report.Summary["خطة الإنتاج"] = card.PlanNumber;
        report.Summary["العميل"] = card.CustomerName;
        report.Summary["الصنف المستلم"] = card.RawName;
        report.Summary["المنتج النهائي"] = card.ProductName;
        report.Summary["تاريخ الإنتاج"] = card.ProductionDate;
        report.Summary["الوردية"] = card.ShiftName;
        report.Summary["خط الإنتاج"] = card.LineName;
        report.Summary["وقت البداية"] = card.StartTime;
        report.Summary["وقت النهاية المتوقع"] = card.ExpectedEndTime;
        report.Summary["معدل الإنتاج"] = $"{card.RatePerHour:N0} كرتون/ساعة — الساعات المتوقعة {card.ExpectedHours:N1}";
        report.Summary["الحالة"] = card.StatusAr;
        report.Summary["توقيع مدير الإنتاج"] = "____________________";
        report.Summary["توقيع مشرف الوردية"] = "____________________";
        report.Summary["توقيع مسؤول الجودة"] = "____________________";
        return report;
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRow();
        if (r == null) { AppContainer.Get<DialogService>().Error("اختر أمر إنتاج للطباعة."); return; }
        var m = BuildOrderModel(r);
        new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}", p => PhasePrint.ExportPdf(m, p))
        { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void Pdf_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRow();
        if (r == null) { AppContainer.Get<DialogService>().Error("اختر أمر إنتاج للتصدير."); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "ملف PDF|*.pdf", FileName = $"أمر_إنتاج_{r.OrderNumber}.pdf" };
        if (dlg.ShowDialog() != true) return;
        try { PhasePrint.ExportPdf(BuildOrderModel(r), dlg.FileName); AppContainer.Get<DialogService>().Info($"تم التصدير إلى:\n{dlg.FileName}"); }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Orders.Pdf"); }
    }

    /// <summary>§نمط خطط الإنتاج: مستند أمر إنتاج رسمي (بيانات + بنود + مواد + توقيعات).</summary>
    private PhaseDocModel BuildOrderModel(OrderRowUi r)
    {
        using var scope = AppContainer.NewScope();
        var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var card = svc.GetOrderCard(r.Id);
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == r.Id).ToList();
        var materials = db.ProductionOrderMaterials.AsNoTracking().Where(x => x.OrderId == r.Id).ToList();

        var m = new PhaseDocModel
        {
            // §القالب المرجعي print_order.html
            DocTitle = "أمر وتشغيل إنتاج التمور (Work Order)",
            DocNo = r.OrderNumber,
            StatusAr = r.StatusAr,
            MainTitle = "📦 بنود التشغيل والمنتجات التامة المستهدفة",
            Columns = new[] { "#", "الدفعة", "العميل", "الخام المستلم", "المنتج النهائي", "العبوة", "الكمية (كجم)", "الكراتين", "المنفذ (كجم)" },
            SecondTitle = "🧪 جدول المواد المساعدة والتغليف المحتسبة آلياً (BOM)",
            SecondColumns = new[] { "المادة", "الوحدة", "المحتسب", "المصروف", "المستهلك" },
            Signatures = { "مشرف صالة الإنتاج", "أمين مخزن المواد المساعدة", "مدير إدارة الإنتاج / الاعتماد" }
        };
        int n = 1;
        foreach (var it in items)
        {
            m.Rows.Add(new object[]
            {
                n++,
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "-",
                _customers.FirstOrDefault(c => c.id == (it.CustomerId ?? 0)).name ?? "-",
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Select(l => l.ProductId).FirstOrDefault() is int rp ? (_products.FirstOrDefault(p => p.id == rp).name ?? "-") : "-",
                _products.FirstOrDefault(p => p.id == it.ProductId).name ?? "-",
                it.PackagingTypeId != null ? db.PackagingTypes.AsNoTracking().Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() : "-",
                it.PlannedQtyKg, it.PlannedCartons, it.ProducedQtyKg
            });
        }
        foreach (var x in materials)
            m.SecondRows.Add(new object[]
            {
                db.AuxiliaryMaterials.AsNoTracking().Where(a => a.Id == x.MaterialId).Select(a => a.MaterialNameAr).FirstOrDefault() ?? "-",
                x.UnitOfMeasure ?? "-", x.CalculatedQty, x.ActualIssuedQty, x.ConsumedQty
            });
        m.Info.Add(("خطة الإنتاج", card.PlanNumber));
        m.Info.Add(("تاريخ الإنتاج", card.ProductionDate));
        m.Info.Add(("الوردية", card.ShiftName));
        m.Info.Add(("خط الإنتاج", card.LineName));
        m.Info.Add(("معدل الإنتاج", $"{card.RatePerHour:N0} كرتون/ساعة"));
        m.Info.Add(("البداية الفعلية", card.StartTime));
        m.Totals.Add(("إجمالي الكمية (كجم)", items.Sum(i => i.PlannedQtyKg).ToString("N1")));
        m.Totals.Add(("إجمالي الكراتين", items.Sum(i => i.PlannedCartons).ToString("N0")));
        m.Totals.Add(("إجمالي المنفذ (كجم)", items.Sum(i => i.ProducedQtyKg).ToString("N1")));
        return m;
    }
}
