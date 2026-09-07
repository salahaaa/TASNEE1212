using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §B52 — نافذة مستقلة لإعدادات الفحص (نُقلت من شاشة الأصناف لتقليل تشعبها):
/// معايير الفحص المخبري · أنواع نتائج الفحص · تخصيص النتائج لصنف/مجموعة.
/// لا ثوابت في الكود: كل ما يُعرَّف هنا يظهر تلقائياً في استمارة الفحص حسب الصنف.
/// </summary>
public class QualitySetupWindow : Window
{
    private sealed class Opt { public int? Id { get; set; } public string Name { get; set; } public override string ToString() => Name; }

    public QualitySetupWindow()
    {
        Title = "إعدادات الفحص ونتائج الفرز";
        Width = 1050; Height = 660; MinWidth = 860; MinHeight = 540;
        // §B84/B8: إغلاق بـ Escape.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
        };
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── معايير الفحص ──
        var stName = new TextBox { Width = 170 };
        var stUnit = new TextBox { Width = 60, Text = "%" };
        var stMin = new TextBox { Width = 70 };
        var stMax = new TextBox { Width = 70 };
        var stDef = new TextBox { Width = 70 };
        var stGrid = new DataGrid { Height = 240, IsReadOnly = true, RowHeight = 28, AutoGenerateColumns = false };
        stGrid.Columns.Add(new DataGridTextColumn { Header = "الكود", Binding = new System.Windows.Data.Binding("Code"), Width = 90 });
        stGrid.Columns.Add(new DataGridTextColumn { Header = "المعيار", Binding = new System.Windows.Data.Binding("Name"), Width = 200 });
        stGrid.Columns.Add(new DataGridTextColumn { Header = "الوحدة", Binding = new System.Windows.Data.Binding("Unit"), Width = 60 });
        stGrid.Columns.Add(new DataGridTextColumn { Header = "أدنى", Binding = new System.Windows.Data.Binding("Min"), Width = 70 });
        stGrid.Columns.Add(new DataGridTextColumn { Header = "أقصى", Binding = new System.Windows.Data.Binding("Max"), Width = 70 });
        stGrid.Columns.Add(new DataGridTextColumn { Header = "افتراضي", Binding = new System.Windows.Data.Binding("Def"), Width = 70 });
        stGrid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("Status"), Width = 80 });
        var addSt = new Button { Content = "➕ إضافة معيار", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton") };
        var togSt = new Button { Content = "🔁 تفعيل/إيقاف", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };

        // ── أنواع نتائج الفحص ──
        var rtName = new TextBox { Width = 140 };
        var rtKind = new ComboBox { Width = 140 };
        rtKind.Items.Add("مقبول للإفراج"); rtKind.Items.Add("مرفوض"); rtKind.Items.Add("مخرج ثانوي"); rtKind.Items.Add("فاقد");
        rtKind.SelectedIndex = 0;
        var rtUnit = new ComboBox { Width = 120, DisplayMemberPath = "UnitNameAr", SelectedValuePath = "Id" };
        var rtDef = new TextBox { Width = 60, Text = "0" };
        var rtFin = new CheckBox { Content = "منتج تام", IsChecked = true };
        var rtBy = new CheckBox { Content = "مخرج ثانوي" };
        var rtInv = new CheckBox { Content = "يدخل المخزون", IsChecked = true };
        var rtLoss = new CheckBox { Content = "يُحسب من الفاقد" };
        var rtScrap = new CheckBox { Content = "مرفوض نهائي / عوادم (وإلا فغير مطابق / منسم)", ToolTip = "§B95 — للمرفوض فقط: يفصل المرفوض النهائي عن غير المطابق القابل للمعالجة في ملخص الدرجات" };
        var rtMan = new CheckBox { Content = "إجباري لكل الأصناف" };
        var rtGrid = new DataGrid { Height = 220, IsReadOnly = true, RowHeight = 28, AutoGenerateColumns = false };
        rtGrid.Columns.Add(new DataGridTextColumn { Header = "الكود", Binding = new System.Windows.Data.Binding("Code"), Width = 100 });
        rtGrid.Columns.Add(new DataGridTextColumn { Header = "النتيجة", Binding = new System.Windows.Data.Binding("Name"), Width = 150 });
        rtGrid.Columns.Add(new DataGridTextColumn { Header = "التصنيف", Binding = new System.Windows.Data.Binding("Kind"), Width = 110 });
        rtGrid.Columns.Add(new DataGridTextColumn { Header = "الوحدة", Binding = new System.Windows.Data.Binding("Unit"), Width = 80 });
        rtGrid.Columns.Add(new DataGridTextColumn { Header = "السمات", Binding = new System.Windows.Data.Binding("Flags"), Width = 170 });
        var addRt = new Button { Content = "➕ إضافة نوع نتيجة", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton") };
        var togRt = new Button { Content = "🔁 تفعيل/إيقاف", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };

        // ── تخصيص النتائج لصنف ──
        var pfProd = new ComboBox { Width = 200 };
        var pfGroup = new ComboBox { Width = 110 };
        pfGroup.Items.Add("—"); pfGroup.Items.Add("001"); pfGroup.Items.Add("002"); pfGroup.Items.Add("003"); pfGroup.Items.Add("004");
        pfGroup.SelectedIndex = 0;
        var pfType = new ComboBox { Width = 170, DisplayMemberPath = "NameAr", SelectedValuePath = "ResultTypeId" };
        var pfUnit = new ComboBox { Width = 120, DisplayMemberPath = "UnitNameAr", SelectedValuePath = "Id" };
        var pfDef = new TextBox { Width = 60, Text = "0" };
        var pfMan = new CheckBox { Content = "إجباري لهذا الصنف" };
        var pfGrid = new DataGrid { Height = 200, IsReadOnly = true, RowHeight = 28, AutoGenerateColumns = false };
        pfGrid.Columns.Add(new DataGridTextColumn { Header = "النطاق", Binding = new System.Windows.Data.Binding("Scope"), Width = 200 });
        pfGrid.Columns.Add(new DataGridTextColumn { Header = "النتيجة", Binding = new System.Windows.Data.Binding("Result"), Width = 150 });
        pfGrid.Columns.Add(new DataGridTextColumn { Header = "الوحدة", Binding = new System.Windows.Data.Binding("Unit"), Width = 90 });
        pfGrid.Columns.Add(new DataGridTextColumn { Header = "افتراضي", Binding = new System.Windows.Data.Binding("DefaultQty"), Width = 70 });
        pfGrid.Columns.Add(new DataGridTextColumn { Header = "إجباري", Binding = new System.Windows.Data.Binding("Mandatory"), Width = 70 });
        pfGrid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("Status"), Width = 80 });
        var addPf = new Button { Content = "➕ إضافة تخصيص", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton") };
        var togPf = new Button { Content = "🔁 تفعيل/إيقاف", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };

        object SelId(DataGrid g) => g.SelectedItem?.GetType().GetProperty("Id")?.GetValue(g.SelectedItem);

        void Refresh()
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            stGrid.ItemsSource = db.QualityStandards.OrderBy(x => x.SortNo).Select(x => new
            {
                x.Id, x.Code, Name = x.NameAr, Unit = x.UnitLabel, Min = x.MinValue, Max = x.MaxValue, Def = x.DefaultValue,
                Status = x.IsActive ? "نشط 🟢" : "موقوف ⚪"
            }).ToList();

            var units = db.UnitsOfMeasure.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UnitNameAr).ToList();
            rtUnit.ItemsSource = units; pfUnit.ItemsSource = units;

            var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
            var types = insp.GetResultTypes(includeInactive: true);
            rtGrid.ItemsSource = types.Select(t => new
            {
                Id = t.ResultTypeId, t.Code, Name = t.NameAr, Kind = t.ResultKindAr, Unit = t.UnitLabel ?? "—",
                Flags = (t.IsFinishedGood ? "تام " : "") + (t.IsByProduct ? "ثانوي " : "") +
                        (t.EntersInventory ? "مخزون " : "") + (t.CountsAsLoss ? "فاقد" : "")
            }).ToList();
            pfType.ItemsSource = types;

            var prods = db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.ProductNameAr).ToList();
            pfProd.ItemsSource = new[] { new Opt { Id = null, Name = "— كل الأصناف —" } }
                .Concat(prods.Select(p => new Opt { Id = p.Id, Name = $"{p.ProductNameAr} ({p.ProductCode})" })).ToList();
            if (pfProd.SelectedIndex < 0) pfProd.SelectedIndex = 0;
            pfGrid.ItemsSource = db.ItemInspectionProfiles.AsNoTracking().OrderBy(p => p.Id).ToList().Select(p => new
            {
                p.Id,
                Scope = p.ProductId != null ? (prods.FirstOrDefault(x => x.Id == p.ProductId)?.ProductNameAr ?? $"#{p.ProductId}") : $"مجموعة {p.GroupCode}",
                Result = types.FirstOrDefault(t => t.ResultTypeId == p.ResultTypeId)?.NameAr ?? $"#{p.ResultTypeId}",
                Unit = p.UnitId != null ? (units.FirstOrDefault(u => u.Id == p.UnitId)?.UnitNameAr ?? "—") : "(وحدة النوع)",
                p.DefaultQty,
                Mandatory = p.IsMandatory ? "إجباري" : "",
                Status = p.IsActive ? "نشط 🟢" : "موقوف ⚪"
            }).ToList();
        }

        addSt.Click += (_, _) =>
        {
            var name = stName.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name)) { AppContainer.Get<DialogService>().Error("أدخل اسم المعيار."); return; }
            try
            {
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                db.QualityStandards.Add(new DatesErp.Core.Domain.Entities.QualityStandard
                {
                    Code = "STD-" + Guid.NewGuid().ToString("N")[..6].ToUpper(),
                    NameAr = name,
                    UnitLabel = string.IsNullOrWhiteSpace(stUnit.Text) ? "%" : stUnit.Text.Trim(),
                    MinValue = double.TryParse(stMin.Text, out var mn) ? mn : null,
                    MaxValue = double.TryParse(stMax.Text, out var mx) ? mx : null,
                    DefaultValue = double.TryParse(stDef.Text, out var dv) ? dv : 0,
                    SortNo = (db.QualityStandards.Max(x => (int?)x.SortNo) ?? 0) + 1
                });
                db.SaveChanges();
                stName.Text = ""; stMin.Text = ""; stMax.Text = ""; stDef.Text = "";
                Refresh();
                AppContainer.Get<DialogService>().Info($"أُضيف المعيار «{name}» — يظهر تلقائياً في استمارة الفحص.");
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "QualitySetup.AddStandard"); }
        };
        togSt.Click += (_, _) =>
        {
            if (SelId(stGrid) is not int id) return;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var st = db.QualityStandards.FirstOrDefault(x => x.Id == id);
            if (st == null) return;
            st.IsActive = !st.IsActive; db.SaveChanges(); Refresh();
        };

        addRt.Click += (_, _) =>
        {
            var name = rtName.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name)) { AppContainer.Get<DialogService>().Error("أدخل اسم نوع النتيجة."); return; }
            try
            {
                string kind = rtKind.SelectedItem?.ToString() switch
                { "مرفوض" => "Rejected", "مخرج ثانوي" => "ByProduct", "فاقد" => "Loss", _ => "Accepted" };
                using var scope = AppContainer.NewScope();
                var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                int sort = (db.InspectionResultTypes.Max(x => (int?)x.SortNo) ?? 0) + 1;
                var r = insp.SaveResultType(null, null, name, kind,
                    rtUnit.SelectedValue as int?, rtFin.IsChecked == true, rtBy.IsChecked == true,
                    rtInv.IsChecked == true, rtLoss.IsChecked == true, sort, true,
                    isFinalScrap: kind == "Rejected" && rtScrap.IsChecked == true);
                if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
                if (rtMan.IsChecked == true)
                    insp.SetProfile(null, null, null, r.Id, rtUnit.SelectedValue as int?,
                        decimal.TryParse(rtDef.Text, out var dq) ? dq : 0, true, sort, true);
                rtName.Text = ""; rtDef.Text = "0"; rtMan.IsChecked = false; rtScrap.IsChecked = false;
                Refresh();
                AppContainer.Get<DialogService>().Info($"أُضيف نوع النتيجة «{name}» — يظهر تلقائياً في شاشة الفحص.");
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "QualitySetup.AddResultType"); }
        };
        togRt.Click += (_, _) =>
        {
            if (SelId(rtGrid) is not int id) return;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var t = db.InspectionResultTypes.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            t.IsActive = !t.IsActive; db.SaveChanges(); Refresh();
        };

        addPf.Click += (_, _) =>
        {
            try
            {
                if (pfType.SelectedValue is not int typeId) { AppContainer.Get<DialogService>().Error("اختر نوع النتيجة."); return; }
                int? productId = (pfProd.SelectedItem as Opt)?.Id;
                string group = pfGroup.SelectedItem?.ToString();
                if (group == "—") group = null;
                if (productId == null && group == null) { AppContainer.Get<DialogService>().Error("اختر الصنف أو المجموعة."); return; }
                using var scope = AppContainer.NewScope();
                var r = scope.ServiceProvider.GetRequiredService<IInspectionService>().SetProfile(
                    null, productId, group, typeId, pfUnit.SelectedValue as int?,
                    decimal.TryParse(pfDef.Text, out var dq) ? dq : 0, pfMan.IsChecked == true, 0, true);
                if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
                pfDef.Text = "0"; pfMan.IsChecked = false;
                Refresh();
                AppContainer.Get<DialogService>().Info("تم التخصيص — ستظهر هذه النتيجة للصنف المحدد في شاشة الفحص.");
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "QualitySetup.AddProfile"); }
        };
        togPf.Click += (_, _) =>
        {
            if (SelId(pfGrid) is not int id) return;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var p = db.ItemInspectionProfiles.FirstOrDefault(x => x.Id == id);
            if (p == null) return;
            p.IsActive = !p.IsActive; db.SaveChanges(); Refresh();
        };

        var stTab = new StackPanel { Margin = new Thickness(12) };
        stTab.Children.Add(Row(new (string, UIElement)[] { ("اسم المعيار *:", stName), ("الوحدة:", stUnit), ("أدنى:", stMin), ("أقصى:", stMax), ("افتراضي:", stDef) }, new UIElement[] { addSt, togSt }));
        stTab.Children.Add(stGrid);

        var rtTab = new StackPanel { Margin = new Thickness(12) };
        var rtRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        rtRow.Children.Add(F("الاسم *:", rtName)); rtRow.Children.Add(F("التصنيف *:", rtKind)); rtRow.Children.Add(F("الوحدة (من القاموس):", rtUnit));
        rtRow.Children.Add(F("افتراضي:", rtDef));
        rtRow.Children.Add(new StackPanel { Children = { rtFin, rtBy }, Margin = new Thickness(0, 0, 10, 0) });
        rtRow.Children.Add(new StackPanel { Children = { rtInv, rtLoss, rtScrap, rtMan }, Margin = new Thickness(0, 0, 10, 0) });
        var rtBtns = new StackPanel { Orientation = Orientation.Horizontal }; rtBtns.Children.Add(addRt); rtBtns.Children.Add(togRt);
        rtRow.Children.Add(rtBtns);
        rtTab.Children.Add(rtRow);
        rtTab.Children.Add(rtGrid);

        var pfTab = new StackPanel { Margin = new Thickness(12) };
        var pfRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        pfRow.Children.Add(F("الصنف:", pfProd)); pfRow.Children.Add(F("أو المجموعة:", pfGroup)); pfRow.Children.Add(F("نوع النتيجة *:", pfType));
        pfRow.Children.Add(F("وحدة هذا الصنف:", pfUnit)); pfRow.Children.Add(F("افتراضي:", pfDef));
        pfRow.Children.Add(new StackPanel { Children = { pfMan }, Margin = new Thickness(0, 0, 10, 0) });
        var pfBtns = new StackPanel { Orientation = Orientation.Horizontal }; pfBtns.Children.Add(addPf); pfBtns.Children.Add(togPf);
        pfRow.Children.Add(pfBtns);
        pfTab.Children.Add(pfRow);
        pfTab.Children.Add(pfGrid);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "🧪 معايير الفحص", Content = stTab });
        tabs.Items.Add(new TabItem { Header = "🧾 أنواع نتائج الفحص", Content = rtTab });
        tabs.Items.Add(new TabItem { Header = "🎯 تخصيص النتائج للصنف", Content = pfTab });
        Content = tabs;
        Loaded += (_, _) => Refresh();
    }

    private static StackPanel F(string label, UIElement el)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Bold, FontSize = 11.5 });
        sp.Children.Add(el);
        return sp;
    }

    private static UIElement Row((string, UIElement)[] fields, UIElement[] buttons)
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (label, el) in fields) wp.Children.Add(F(label, el));
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons) sp.Children.Add(b);
        wp.Children.Add(sp);
        return wp;
    }
}
