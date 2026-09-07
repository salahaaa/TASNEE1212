using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B100 — سجل التدقيق بالفلاتر: مستخدم/إجراء/شاشة/مستند/فترة.
/// قراءة فقط — السجل إلزامي غير قابل للتعديل أو الحذف (§48).
/// </summary>
public partial class AuditFilterView : UserControl
{
    private List<AuditRowUi> _all = new();

    private class AuditRowUi
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string Time { get; set; }
        public string User { get; set; }
        public string Machine { get; set; }
        public string Action { get; set; }
        public string Screen { get; set; }
        public string Document { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public string Detail { get; set; }
    }

    public AuditFilterView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadAll();
    }

    private static string Trunc(string s, int max = 220)
        => string.IsNullOrWhiteSpace(s) ? "—" : (s.Length > max ? s[..max] + "…" : s);

    private void LoadAll()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _all = db.AuditLogs.AsNoTracking()
                .OrderByDescending(a => a.ActionDate).Take(5000)
                .ToList()   // §سحب: دوال بمعاملات اختيارية غير قابلة للترجمة في شجرة EF
                .Select(a => new AuditRowUi
                {
                    Id = a.Id,
                    Date = a.ActionDate,
                    Time = a.ActionDate.ToString("dd/MM/yyyy HH:mm:ss"),
                    User = a.UserName ?? "—",
                    Machine = a.MachineName ?? a.ComputerName ?? "—",
                    Action = a.ActionType ?? "—",
                    Screen = a.ScreenName ?? "—",
                    Document = a.DocumentNumber ?? "—",
                    OldValue = a.OldValue,
                    NewValue = a.NewValue,
                    Detail = $"{Trunc(a.OldValue)} ← {Trunc(a.NewValue)}"
                }).ToList();

            // فلاتر ديناميكية من البيانات الفعلية (الأفعال تشمل المخصصة كـ «تصحيح معتمد»)
            FUser.Items.Clear();
            FUser.Items.Add(new ComboBoxItem { Content = "— الكل —" });
            foreach (var u in _all.Select(r => r.User).Distinct().OrderBy(x => x))
                FUser.Items.Add(new ComboBoxItem { Content = u });
            FAction.Items.Clear();
            FAction.Items.Add(new ComboBoxItem { Content = "— الكل —" });
            foreach (var a in _all.Select(r => r.Action).Distinct().OrderBy(x => x))
                FAction.Items.Add(new ComboBoxItem { Content = a });
            FUser.SelectedIndex = 0;
            FAction.SelectedIndex = 0;

            Apply();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Audit.Load"); }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        FUser.SelectedIndex = 0;
        FAction.SelectedIndex = 0;
        FScreen.Text = "";
        FDoc.Text = "";
        FFrom.Text = "";
        FTo.Text = "";
        Apply();
    }

    private void Apply()
    {
        try
        {
            string user = (FUser.SelectedItem as ComboBoxItem)?.Content?.ToString();
            string action = (FAction.SelectedItem as ComboBoxItem)?.Content?.ToString();
            string screen = FScreen.Text?.Trim();
            string doc = FDoc.Text?.Trim();
            bool hasFrom = UiDate.TryParse(FFrom.Text?.Trim(), out var from);
            bool hasTo = UiDate.TryParse(FTo.Text?.Trim(), out var to);

            IEnumerable<AuditRowUi> q = _all;
            if (!string.IsNullOrEmpty(user)) q = q.Where(r => r.User == user);
            if (!string.IsNullOrEmpty(action)) q = q.Where(r => r.Action == action);
            if (!string.IsNullOrEmpty(screen)) q = q.Where(r => r.Screen.Contains(screen, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(doc)) q = q.Where(r => r.Document.Contains(doc, StringComparison.OrdinalIgnoreCase));
            if (hasFrom) q = q.Where(r => r.Date.Date >= from.Date);
            if (hasTo) q = q.Where(r => r.Date.Date <= to.Date);

            var rows = q.Take(500).ToList();
            Grid.ItemsSource = rows;
            CountLabel.Text = $"{rows.Count} سجل ({_all.Count} في نطاق التحميل)";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Audit.Apply"); }
    }

    private static class UiDate
    {
        public static bool TryParse(string s, out DateTime d)
        {
            d = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return DateTime.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }
    }

    /// <summary>كل الحقل (ما قبل/ما بعد كاملاً) — نمط §B89.</summary>
    private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is not AuditRowUi r) return;
        var dlg = new RecordDetailsDialog("سجل التدقيق", new List<(string, string)>
        {
            ("التاريخ والوقت", r.Date.ToString("dd/MM/yyyy HH:mm:ss")),
            ("المستخدم", r.User),
            ("الجهاز", r.Machine),
            ("الإجراء", r.Action),
            ("الشاشة", r.Screen),
            ("المستند", r.Document),
            ("القيمة قبل", r.OldValue ?? "—"),
            ("القيمة بعد", r.NewValue ?? "—"),
            ("معرف السجل", r.Id.ToString())
        }) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }
}
