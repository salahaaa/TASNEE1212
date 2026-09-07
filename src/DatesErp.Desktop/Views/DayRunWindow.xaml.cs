using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §B98 — «تشغيل اليوم»: الإدخال اليدوي الموجّه.
/// سطور يوم الخطة (من الخطة — بلا إعادة إدخال) + خانات اختيار + «كراتين الأمر» بيد المدير.
/// «إنشاء أمر التشغيل» يصدر فقط المختار وبالكميات المدخلة — أمر لكل (وردية×خط×عميل).
/// </summary>
public partial class DayRunWindow : Window
{
    private readonly int _planId;
    private readonly string _date;
    private string _planNumber = "—";

    public DayRunWindow(int planId, string date)
    {
        InitializeComponent();
        _planId = planId;
        _date = date;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IDayRunService>();
            var ctx = svc.GetDayRun(_planId, _date);
            _planNumber = ctx.PlanNumber;

            Title = $"تشغيل اليوم {ctx.Date} — خطة {_planNumber}";
            HeadTitle.Text = $"الخطة: {ctx.PlanTitle} ({_planNumber}) — تشغيل يوم {ctx.Date}";
            HeadState.Text = ctx.AllIssued ? "✅ اليوم مُشغَّل بالكامل" : (ctx.IsOverdue ? "⏰ يوم متعثر — لم يكتمل تشغيله" : "بانتظار الإدخال اليدوي");

            if (ctx.IsOverdue && !ctx.AllIssued)
            {
                OverdueBanner.Visibility = Visibility.Visible;
                OverdueText.Text = $"⚠️ تاريخ هذا اليوم {ctx.Date} قد مضى ولم يُشغَّل كاملاً — شغّله الآن أو أعد جدولة البنود من شاشة الخطط.";
            }
            else OverdueBanner.Visibility = Visibility.Collapsed;

            RowsGrid.ItemsSource = ctx.Rows;
            BtnCreate.IsEnabled = !ctx.AllIssued && ctx.Rows.Any(r => r.RemainingCartons > 0);
            UpdateTotals();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "DayRun.Load"); }
    }

    /// <summary>تحديث الوزن المكافئ والإجماليات عند أي تعديل (كراتين/تحديد).</summary>
    private void RowsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row?.Item is DayRunRowDto row)
        {
            if (e.Column.Header?.ToString() == "كراتين الأمر ✍️")
            {
                // حارس العرض: لا يتجاوز المتبقي (الحارس الحقيقي في الخدمة)
                int v = 0;
                if (int.TryParse((e.EditingElement as TextBox)?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    v = Math.Max(0, Math.Min(parsed, row.RemainingCartons));
                row.OrderCartons = v;
                row.OrderKg = Math.Round(v * row.PackWeight, 1);
                if (v <= 0) row.IsChecked = false;
            }
            e.Row.InvalidateVisual();
            UpdateTotals();
        }
    }

    private void UpdateTotals()
    {
        var rows = (RowsGrid.ItemsSource as System.Collections.IEnumerable)?.Cast<DayRunRowDto>().ToList() ?? new List<DayRunRowDto>();
        var sel = rows.Where(r => r.IsChecked && r.OrderCartons > 0).ToList();
        int ctn = sel.Sum(r => r.OrderCartons);
        double kg = sel.Sum(r => r.OrderKg);
        TotalsText.Text = sel.Count > 0
            ? $"المحدد: {sel.Count} من {rows.Count} سطر — {ctn:N0} كرتون ({kg:N1} كجم)"
            : "لا سطر محدد بعد — علّم الصفوف وضبط الكمية";
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows = (RowsGrid.ItemsSource as System.Collections.IEnumerable)?.Cast<DayRunRowDto>().ToList() ?? new List<DayRunRowDto>();
            var lines = rows
                .Where(r => r.IsChecked && r.OrderCartons > 0)
                .Select(r => new DayRunIssueLineDto { ItemId = r.ItemId, Cartons = r.OrderCartons })
                .ToList();
            if (lines.Count == 0)
            {
                AppContainer.Get<DialogService>().Error("حدد سطراً واحداً على الأقل — علّم «تشغيل» وأدخل كراتين الأمر.");
                return;
            }
            var confirm = AppContainer.Get<DialogService>().Confirm(
                $"سيُنشأ أمر تشغيل (أو أوامر — حسب الوردية/العميل) بالكميات التي حددتها:\n" +
                $"{string.Join("\n", lines.Select(l => $"• سطر #{l.ItemId}: {l.Cartons:N0} كرتون"))}\n\n" +
                "مع الاعتماد التلقائي (صرف المواد) — هل تريد المتابعة؟");
            if (!confirm) return;

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IDayRunService>();
            var r = svc.IssueSelected(_planId, _date, lines);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Close(); // اللوحة تعيد رسم نفسها: اليوم يترك «المطلوب اليوم» وتظهر الأوامر «جاهزة للبدء»
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "DayRun.Create"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
