using Microsoft.EntityFrameworkCore;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §المعالجة والتعقيم — نافذة بدء معالجة على **جزء** من دفعة.
///
/// هذه النافذة هي موضع تنفيذ جوهر طلب المستخدم: تقسيم الشحنة الواحدة إلى أجزاء
/// بمُدد مختلفة (4,000 جاهزة + 500 لسبعة أيام + 500 لعشرة أيام) — **بلا صنف جديد**.
/// تُفتح مرة لكل جزء، والدفعة تحمل عدة معالجات متوازية.
///
/// **لا قائمة أصناف مكررة**: الدفعات تُقرأ من مصدرها القائم، والصنف يُشتق منها.
/// </summary>
public class TreatmentStartDialog : Window
{
    private ComboBox _lot, _type;
    private TextBox _qty, _packages, _hours, _notes;
    private TextBlock _state, _ready;
    private List<LotOption> _lots = new();
    private List<TreatmentType> _types = new();

    private sealed class LotOption
    {
        public int Id { get; init; }
        public string Label { get; init; }
        public double Available { get; init; }
        public double UnitWeight { get; init; }
    }

    public TreatmentStartDialog()
    {
        Title = "بدء معالجة / تعقيم";
        Width = 560;
        Height = 520;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Build();
    }

    private void Build()
    {
        var p = new StackPanel { Margin = new Thickness(16) };

        p.Children.Add(Label("الدفعة (الخام المستلم):"));
        _lot = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Id" };
        _lot.SelectionChanged += (_, _) => OnLotChanged();
        p.Children.Add(_lot);

        _state = new TextBlock
        {
            FontSize = 11.5,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        p.Children.Add(_state);

        p.Children.Add(Label("نوع المعالجة:"));
        _type = new ComboBox { DisplayMemberPath = "TypeNameAr", SelectedValuePath = "Id" };
        _type.SelectionChanged += (_, _) => OnTypeChanged();
        p.Children.Add(_type);

        p.Children.Add(Label("الكمية (كجم):"));
        _qty = new TextBox();
        _qty.TextChanged += (_, _) => SyncPackagesFromQty();
        p.Children.Add(_qty);

        p.Children.Add(Label("عدد الطرود (سلال/كراتين):"));
        // §المستخدم يتعامل بالسلال والمخزون بالكيلو — الحقلان متزامنان تلقائياً
        // بوزن الوحدة، فلا يحسب المستخدم يدوياً ولا يرى رقماً لا يعرفه.
        _packages = new TextBox();
        _packages.LostFocus += (_, _) => SyncQtyFromPackages();
        p.Children.Add(_packages);

        p.Children.Add(Label("المدة (ساعات) — 168 = سبعة أيام · 240 = عشرة أيام:"));
        _hours = new TextBox();
        _hours.TextChanged += (_, _) => UpdateReady();
        p.Children.Add(_hours);

        _ready = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        p.Children.Add(_ready);

        p.Children.Add(Label("ملاحظات:"));
        _notes = new TextBox();
        p.Children.Add(_notes);

        var ok = new Button
        {
            Content = "▶ بدء المعالجة",
            Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton"),
            Margin = new Thickness(0, 16, 0, 0)
        };
        ok.Click += (_, _) => Submit();
        p.Children.Add(ok);

        Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => LoadData();
    }

    private static TextBlock Label(string t) => new()
    {
        Text = t,
        FontWeight = FontWeights.Bold,
        FontSize = 12,
        Margin = new Thickness(0, 10, 0, 3)
    };

    private void LoadData()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DatesErpDbContext>();

            // §الدفعات من مصدرها القائم — لا قائمة مكررة. ويُعرض المتاح للمعالجة
            // مباشرةً (بعد خصم ما هو تحت المعالجة والمحجوز للخطط) بدل الرصيد الخام،
            // كي لا يدخل المستخدم كمية سيرفضها الخادم بعد الضغط.
            _lots = db.Lots.AsNoTracking()
                .Where(l => l.InStockQtyKg > 0)
                .Select(l => new
                {
                    l.Id,
                    l.LotCode,
                    l.ProductId,
                    Avail = l.InStockQtyKg - l.UnderTreatmentQtyKg - l.ReservedQtyKg
                })
                .Where(x => x.Avail > 0.001)
                .ToList()
                .Select(x => new LotOption
                {
                    Id = x.Id,
                    Available = x.Avail,
                    UnitWeight = db.Products.AsNoTracking()
                        .Where(pr => pr.Id == x.ProductId).Select(pr => pr.CartonWeightKg).FirstOrDefault(),
                    Label = $"{x.LotCode} — "
                          + db.Products.AsNoTracking().Where(pr => pr.Id == x.ProductId)
                              .Select(pr => pr.ProductNameAr).FirstOrDefault()
                          + $" ({x.Avail:N0} كجم متاحة)"
                })
                .ToList();

            _types = db.TreatmentTypes.AsNoTracking().Where(t => t.IsActive).ToList();

            _lot.ItemsSource = _lots;
            _type.ItemsSource = _types;
            if (_lots.Count > 0) _lot.SelectedIndex = 0;
            if (_types.Count > 0) _type.SelectedIndex = 0;

            if (_lots.Count == 0)
                _state.Text = "⚠️ لا توجد دفعات بكمية متاحة للمعالجة.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Treatment.Dialog"); }
    }

    private LotOption Current => _lot.SelectedItem as LotOption;

    private void OnLotChanged()
    {
        var l = Current;
        if (l == null) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var st = scope.ServiceProvider.GetRequiredService<IRawTreatmentService>().GetLotState(l.Id);
            if (st != null)
                _state.Text = $"🔵 لم يدخل المعالجة: {st.NotTreatedQtyKg:N0} · "
                            + $"🟠 تحت المعالجة: {st.UnderTreatmentQtyKg:N0} · "
                            + $"🟢 جاهز: {st.ReadyQtyKg:N0} · "
                            + $"محجوز: {st.ReservedQtyKg:N0} (كجم)"
                            + (st.RequiresTreatment ? "" : "\nℹ️ هذا الصنف لا يشترط معالجة قبل الإنتاج.");
        }
        catch { _state.Text = $"المتاح للمعالجة: {l.Available:N0} كجم"; }
    }

    private void OnTypeChanged()
    {
        if (_type.SelectedItem is TreatmentType t && string.IsNullOrWhiteSpace(_hours.Text))
            _hours.Text = t.DefaultDurationHours.ToString("0.##");
        UpdateReady();
    }

    private void SyncPackagesFromQty()
    {
        var l = Current;
        if (l == null || l.UnitWeight <= 0) return;
        if (double.TryParse(_qty.Text, out var kg) && kg > 0)
            _packages.Text = Math.Round(kg / l.UnitWeight, MidpointRounding.AwayFromZero).ToString("0");
    }

    private void SyncQtyFromPackages()
    {
        var l = Current;
        if (l == null || l.UnitWeight <= 0) return;
        if (int.TryParse(_packages.Text, out var n) && n > 0)
            _qty.Text = (n * l.UnitWeight).ToString("0.##");
    }

    /// <summary>موعد الجاهزية يُعرض **قبل** الحفظ — المستخدم يرى أثر المدة فوراً.</summary>
    private void UpdateReady()
    {
        if (double.TryParse(_hours.Text, out var h) && h > 0)
            _ready.Text = $"⏱ الجاهزية المتوقعة: {DateTime.Now.AddHours(h):dd/MM/yyyy HH:mm}"
                        + $"  ({RawTreatmentView.FormatDuration(h)})";
        else
            _ready.Text = "";
    }

    private void Submit()
    {
        var l = Current;
        var dlg = AppContainer.Get<DialogService>();
        if (l == null) { dlg.Error("اختر الدفعة."); return; }
        if (!double.TryParse(_qty.Text, out var qty) || qty <= 0) { dlg.Error("أدخل كمية صحيحة."); return; }
        if (!double.TryParse(_hours.Text, out var hours) || hours <= 0) { dlg.Error("أدخل مدة صحيحة بالساعات."); return; }
        if (qty > l.Available + 0.001)
        {
            dlg.Error($"الكمية تتجاوز المتاح للمعالجة في هذه الدفعة ({l.Available:N1} كجم).");
            return;
        }
        int.TryParse(_packages.Text, out var packs);

        try
        {
            using var scope = AppContainer.NewScope();
            var res = scope.ServiceProvider.GetRequiredService<IRawTreatmentService>().Start(new TreatmentStartDto
            {
                LotId = l.Id,
                TreatmentTypeId = _type.SelectedValue as int?,
                QtyKg = qty,
                PackageCount = packs,
                DurationHours = hours,
                Notes = _notes.Text?.Trim()
            });
            if (!res.Ok) { dlg.Error(res.Message); return; }
            dlg.Info(res.Message);
            DialogResult = true;
            Close();
        }
        catch (Exception ex) { dlg.HandleException(ex, "Treatment.Start"); }
    }
}
