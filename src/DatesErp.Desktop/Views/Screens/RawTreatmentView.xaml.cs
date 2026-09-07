using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §المعالجة والتعقيم — شاشة تشغيل الدورة (المرحلة 4).
///
/// ### التوجيه بالقدرة لا بالمسمى
/// كل زر يظهر أو يختفي بحسب قدرة المستخدم: `Create` بدء · `Approve` إفراج ·
/// `Cancel` رفض وإلغاء. **لا زر ميت**: من لا يملك القدرة لا يراه أصلاً.
///
/// ### لا شاشة جديدة تحل محل شاشة قائمة
/// الأصناف والدفعات تُقرأ من مصادرها القائمة — **لا قائمة أصناف مكررة هنا**.
/// </summary>
public partial class RawTreatmentView : UserControl
{
    private ErpChrome _chrome;
    private List<TreatmentRowDto> _rows = new();

    public RawTreatmentView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void AttachChrome(ErpChrome chrome)
    {
        _chrome = chrome;
        chrome.SetModule("معالجة وتعقيم الخام");
        chrome.SetScreenCode("MRPTRT1000");
        chrome.SetToolbar(new ErpToolbar()
            .WithRefresh((_, _) => Reload())
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard")));
        chrome.SetBody(this);
        chrome.SetPermissionModule(PermissionModules.Treatment);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (StatusFilter.Items.Count == 0)
        {
            StatusFilter.Items.Add(new ComboBoxItem { Content = "الكل", Tag = "" });
            StatusFilter.Items.Add(new ComboBoxItem { Content = "تحت المعالجة", Tag = TreatmentStatuses.InProgress });
            StatusFilter.Items.Add(new ComboBoxItem { Content = "جاهزة للإنتاج", Tag = TreatmentStatuses.Released });
            StatusFilter.Items.Add(new ComboBoxItem { Content = "مرفوضة", Tag = TreatmentStatuses.Rejected });
            StatusFilter.Items.Add(new ComboBoxItem { Content = "ملغاة", Tag = TreatmentStatuses.Cancelled });
            StatusFilter.SelectedIndex = 0;
        }

        // الأزرار بالقدرة — من لا يملكها لا يراها
        StartBtn.Visibility = Vis(PermissionGate.Can(PermissionModules.Treatment, "Create"));
        ReleaseBtn.Visibility = Vis(PermissionGate.Can(PermissionModules.Treatment, "Approve"));
        var canCancel = PermissionGate.Can(PermissionModules.Treatment, "Cancel");
        RejectBtn.Visibility = Vis(canCancel);
        CancelBtn.Visibility = Vis(canCancel);

        Reload();
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ══════════════════ التحميل ══════════════════

    private void Reload()
    {
        try
        {
            string status = (StatusFilter.SelectedItem as ComboBoxItem)?.Tag as string;
            bool overdue = OverdueOnly.IsChecked == true;

            using var scope = AppContainer.NewScope();
            _rows = scope.ServiceProvider.GetRequiredService<IRawTreatmentService>()
                .Search(string.IsNullOrEmpty(status) ? null : status, overdue);

            Grid.ItemsSource = _rows.Select(r => new
            {
                r.Id,
                r.TreatmentNo,
                r.LotCode,
                r.ProductName,
                r.TreatmentTypeName,
                r.QtyKg,
                r.PackageCount,
                r.StartedAt,
                DurationText = FormatDuration(r.DurationHours),
                r.ExpectedReadyAt,
                r.ReleasedQtyKg,
                r.RejectedQtyKg,
                r.RemainingQtyKg,
                // §الحالة تحمل إشارة الوقت: «متأخرة» و«جاهزة زمنياً» ليستا حالتين
                // مخزَّنتين بل اشتقاق لحظي — المستخدم يحتاج رؤيته في نفس العمود.
                StatusText = r.IsOverdue ? $"⚠️ متأخرة — {r.StatusAr}"
                    : r.Status == TreatmentStatuses.InProgress && r.IsReadyByTime ? "🟢 اكتملت المدة — جاهزة للإفراج"
                    : r.Status == TreatmentStatuses.InProgress ? "🟠 " + r.StatusAr
                    : r.Status == TreatmentStatuses.Released ? "✅ " + r.StatusAr
                    : r.StatusAr,
                r.ResponsibleName,
                r.Notes
            }).ToList();

            EmptyText.Text = overdue
                ? "لا توجد معالجات متأخرة — كل شيء في موعده."
                : "لا توجد عمليات معالجة مطابقة.";
            EmptyText.Visibility = Vis(_rows.Count == 0);

            UpdateCounters();
            _chrome?.SetCount(_rows.Count);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Treatment.Load"); }
    }

    private void UpdateCounters()
    {
        CountersPanel.Children.Clear();
        var live = _rows.Where(r => r.Status == TreatmentStatuses.InProgress).ToList();

        Add("🟠 تحت المعالجة", live.Sum(r => r.RemainingQtyKg), "#EA580C");
        Add("🟢 اكتملت مدتها", live.Where(r => r.IsReadyByTime && !r.IsOverdue).Sum(r => r.RemainingQtyKg), "#16A34A");
        Add("⚠️ متأخرة", live.Where(r => r.IsOverdue).Sum(r => r.RemainingQtyKg), "#DC2626");
        Add("✅ أُفرج عنه", _rows.Sum(r => r.ReleasedQtyKg), "#0A246A");

        void Add(string label, double kg, string color)
        {
            var b = new System.Windows.Controls.Border
            {
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(4),
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                    .ConvertFromString(color + "1A"),
                Child = new TextBlock
                {
                    Text = $"{label}: {kg:N0} كجم",
                    FontSize = 11.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                        .ConvertFromString(color)
                }
            };
            CountersPanel.Children.Add(b);
        }
    }

    // اسمان منفصلان: SelectionChanged يتطلب SelectionChangedEventHandler حرفياً
    // ولا يرتبط بمعالج RoutedEventArgs. IsLoaded يمنع الاستدعاء أثناء بناء الشاشة.
    private void Status_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Reload(); }
    private void Overdue_Toggled(object sender, RoutedEventArgs e) { if (IsLoaded) Reload(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    // ══════════════════ الإجراءات ══════════════════

    private TreatmentRowDto Selected()
    {
        if (Grid.SelectedItem == null)
        {
            AppContainer.Get<DialogService>().Info("اختر عملية معالجة من الجدول أولاً.");
            return null;
        }
        int id = (int)Grid.SelectedItem.GetType().GetProperty("Id")!.GetValue(Grid.SelectedItem)!;
        return _rows.FirstOrDefault(r => r.Id == id);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TreatmentStartDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Reload();
    }

    private void Release_Click(object sender, RoutedEventArgs e)
    {
        var r = Selected();
        if (r == null) return;
        if (r.Status != TreatmentStatuses.InProgress)
        {
            AppContainer.Get<DialogService>().Info($"العملية «{r.StatusAr}» — لا إفراج منها.");
            return;
        }

        // §الكمية الافتراضية هي المتبقي كاملاً: الحالة الغالبة إفراج كامل،
        // والجزئي يعدّلها. عرض حقل فارغ كان سيفرض إعادة إدخال في كل مرة.
        var dlg = new InputDialog("إفراج من المعالجة",
            $"الكمية المراد الإفراج عنها (المتبقي {r.RemainingQtyKg:N1} كجم):",
            r.RemainingQtyKg.ToString("0.##"))
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (!double.TryParse(dlg.Value, out var qty) || qty <= 0)
        {
            AppContainer.Get<DialogService>().Error("أدخل كمية صحيحة أكبر من صفر.");
            return;
        }
        Run(s => s.Release(r.Id, qty));
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        var r = Selected();
        if (r == null) return;
        var qDlg = new InputDialog("رفض كمية",
            $"الكمية المرفوضة (المتبقي {r.RemainingQtyKg:N1} كجم):", r.RemainingQtyKg.ToString("0.##"))
        { Owner = Window.GetWindow(this) };
        if (qDlg.ShowDialog() != true) return;
        if (!double.TryParse(qDlg.Value, out var qty) || qty <= 0)
        {
            AppContainer.Get<DialogService>().Error("أدخل كمية صحيحة أكبر من صفر.");
            return;
        }

        var rDlg = new InputDialog("سبب الرفض", "السبب (إلزامي — يُسجَّل في التتبع):")
        { Owner = Window.GetWindow(this) };
        if (rDlg.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(rDlg.Value))
        {
            AppContainer.Get<DialogService>().Error("سبب الرفض إلزامي.");
            return;
        }
        if (!AppContainer.Get<DialogService>().Confirm(
                $"سترفض {qty:N1} كجم وتُسجَّل هدراً — تنقص من مخزون الدفعة نهائياً.\nمتابعة؟"))
            return;
        Run(s => s.Reject(r.Id, qty, rDlg.Value.Trim()));
    }

    private void CancelStart_Click(object sender, RoutedEventArgs e)
    {
        var r = Selected();
        if (r == null) return;
        var dlg = new InputDialog("إلغاء بدء المعالجة", "السبب (تصحيح خطأ إدخال):")
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        Run(s => s.Cancel(r.Id, dlg.Value?.Trim()));
    }

    private void Run(Func<IRawTreatmentService, OpResult> work)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var res = work(scope.ServiceProvider.GetRequiredService<IRawTreatmentService>());
            if (!res.Ok) { AppContainer.Get<DialogService>().Error(res.Message); return; }
            AppContainer.Get<DialogService>().Info(res.Message);
            Reload();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Treatment.Action"); }
    }

    internal static string FormatDuration(double hours)
    {
        if (hours <= 0) return "-";
        int days = (int)(hours / 24);
        double rem = hours - days * 24;
        if (days > 0 && rem < 0.01) return $"{days} يوم";
        if (days > 0) return $"{days}ي {rem:N0}س";
        return $"{hours:N0} ساعة";
    }
}
