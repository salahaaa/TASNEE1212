using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>سطر محرر الدفعات داخل النافذة المنبثقة (مطابق لنموذج v1.59).</summary>
public class LotEditorRow : INotifyPropertyChanged
{
    public int LotId { get; set; }
    public int? ShipmentId { get; set; }
    public string ShipmentNo { get; set; }
    public string LotCode { get; set; }
    /// <summary>§B87/M6: null = «بدون عميل» — يُحفَظ NULL في القاعدة لا صفراً.</summary>
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public string RawName { get; set; }
    public double Available { get; set; }
    /// <summary>عدد أيام الشحنة في المخازن (الأقدم أولوية الإنتاج).</summary>
    public string DaysInStockText { get; set; } = "";
    /// <summary>تاريخ مجدول مسبقاً (من محرك التوزيع العادل) — يُقدَّم على تاريخ الشاشة.</summary>
    public string PresetDate { get; set; }
    /// <summary>§B92 — ورديات الاختيار اليدوي: تعبأ من الورديات النشطة، والإدارة تختار وردية كل بند.</summary>
    public List<ShiftOption> AllShifts { get; set; } = new();
    private int? _shiftId;
    /// <summary>§B87: وردية البند كما جدوله المحرك — §B92: قابلة للاختيار اليدوي لكل بند (تزامن الاسم تلقائياً).</summary>
    public int? ShiftId
    {
        get => _shiftId;
        set
        {
            _shiftId = value;
            var hit = value != null ? AllShifts.FirstOrDefault(s => s.Id == value.Value) : null;
            if (hit != null) ShiftName = hit.Name;
            OnChange(nameof(ShiftId));
        }
    }
    public string ShiftName { get; set; } = "—";

    private DateTime? _dateValue;
    /// <summary>§B80: تاريخ إنتاج البند — عمود DatePicker في النافذة، إلزامي داخل فترة الخطة.</summary>
    public DateTime? DateValue
    {
        get => _dateValue;
        set { _dateValue = value; OnChange(nameof(DateValue)); }
    }

    /// <summary>§B80: وحدة كل صنف تام كما في بطاقته (شاشة الأصناف) — لعرضها عند اختيار الصنف.</summary>
    public Dictionary<int, string> ProductUnits { get; set; } = new();

    /// <summary>§B80: وحدة الصنف المختار — كما عُرّفت في بطاقة الصنف (مثل «كرتون 5كجم»).</summary>
    public string UnitDisplay
        => _productId != null && ProductUnits.TryGetValue(_productId.Value, out var u) && !string.IsNullOrWhiteSpace(u)
            ? u : "—";

    /// <summary>§المتاح لكل صنف تام من هذه الدفعة (يخصم حجوزات ذلك الصنف فقط — لا تداخل).</summary>
    public Dictionary<int, double> PerProductAvailable { get; set; } = new();

    private string _availableDisplay;
    /// <summary>عرض المتاح: إجمالي الدفعة، وإن اختير صنف يعرض المتاح لهذا الصنف تحديداً.</summary>
    public string AvailableDisplay
    {
        get => _availableDisplay ?? $"{Available:N1} كجم";
        private set { _availableDisplay = value; OnChange(nameof(AvailableDisplay)); }
    }

    private void UpdateAvailableDisplay()
    {
        if (_productId != null && PerProductAvailable.TryGetValue(_productId.Value, out var avail))
        {
            var prodName = AllProducts.FirstOrDefault(p => p.Id == _productId.Value)?.Name ?? "الصنف";
            AvailableDisplay = $"{avail:N1} كجم متاح لهذا الصنف";
        }
        else
        {
            AvailableDisplay = $"{Available:N1} كجم";
        }
    }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; OnChange(nameof(IsChecked)); } }

    private int? _productId;
    public int? ProductId
    {
        get => _productId;
        set
        {
            _productId = value;
            OnChange(nameof(ProductId));
            OnChange(nameof(UnitDisplay));
            RebuildPacks();
            UpdateAvailableDisplay();
            Recalc();
        }
    }

    private ObservableCollection<PackOption> _packs = new();
    public ObservableCollection<PackOption> Packs { get => _packs; private set { _packs = value; OnChange(nameof(Packs)); } }

    private int? _packId;
    public int? PackId
    {
        get => _packId;
        set { _packId = value; OnChange(nameof(PackId)); Recalc(); }
    }

    private string _cartonsText = "0";
    public string CartonsText
    {
        get => _cartonsText;
        set { _cartonsText = value; OnChange(nameof(CartonsText)); Recalc(); }
    }

    private double _computedKg;
    public double ComputedKg { get => _computedKg; private set { _computedKg = value; OnChange(nameof(ComputedKg)); } }

    private string _capAlert = "حدد الصنف والكمية…";
    public string CapAlert { get => _capAlert; private set { _capAlert = value; OnChange(nameof(CapAlert)); } }

    // مراجع للحساب
    public List<ProductOption> AllProducts { get; set; } = new();
    public List<PackOption> AllPacks { get; set; } = new();
    public Dictionary<int, double> ProductRates { get; set; } = new(); // معدل الصنف العام للوردية المحددة
    /// <summary>§معدل الصنف + العبوة للوردية المحددة (سكري 7.5 كجم ≠ سكري 4 كجم).</summary>
    public Dictionary<(int productId, int? packId), double> PackRates { get; set; } = new();
    public double RemainingShiftHours { get; set; }

    /// <summary>معدل الصنف المحدد بعبوته الحالية: الخاصة بالعبوة أولاً ثم العامة.</summary>
    private double CurrentRate()
    {
        if (_productId == null) return 0;
        if (_packId != null && PackRates.TryGetValue((_productId.Value, _packId), out var pr) && pr > 0) return pr;
        if (PackRates.TryGetValue((_productId.Value, (int?)null), out var g) && g > 0) return g;
        return ProductRates.TryGetValue(_productId.Value, out var r) && r > 0 ? r : 0;
    }

    private void RebuildPacks()
    {
        Packs = new ObservableCollection<PackOption>(AllPacks);
        _packId = Packs.Count > 0 ? Packs[0].Id : (int?)null;
        OnChange(nameof(PackId));
    }

    private void Recalc()
    {
        int.TryParse(_cartonsText, out var ctn);
        var pack = AllPacks.FirstOrDefault(p => p.Id == _packId);
        double unitW = pack?.UnitWeightKg ?? 0;
        ComputedKg = ctn > 0 && unitW > 0 ? Math.Round(ctn * unitW, 2) : 0;

        if (_productId == null || ctn <= 0)
        {
            CapAlert = "حدد الصنف والكمية…";
            return;
        }
        double rate = CurrentRate();
        if (rate <= 0) { CapAlert = "لا يوجد معدل معرّف"; return; }

        // §تنبيه الطاقة الثلاثي (مطابق لـ v1.60): ⛔ تجاوز | 🟡 أقل من الطاقة | ✅ ضمن الطاقة
        double need = ctn / rate;
        double remHours = Math.Max(0, RemainingShiftHours);
        int remCartons = (int)Math.Floor(remHours * rate);
        if (ctn > remCartons)
        {
            CapAlert = $"⛔ تتجاوز طاقة الوردية! المتبقي {remCartons:N0} كرتون فقط — طلبت {ctn:N0} (زيادة {ctn - remCartons:N0})";
        }
        else if ((remCartons - ctn) > Math.Max(20, remCartons * 0.1))
        {
            CapAlert = $"🟡 أقل من الطاقة — يتبقى {remCartons - ctn:N0} كرتون ({remHours - need:N1} س) غير مستغلة";
        }
        else
        {
            CapAlert = $"✅ ضمن الطاقة — {need:N1} ساعة من {remHours:N1} متبقية";
        }
    }

    public bool IsOverCapacity
    {
        get
        {
            int.TryParse(_cartonsText, out var ctn);
            if (_productId == null || ctn <= 0) return false;
            double rate = CurrentRate();
            if (rate <= 0) return false;
            return ctn / rate > RemainingShiftHours + 0.0001;
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnChange(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class ProductOption { public int Id { get; set; } public string Name { get; set; } }
/// <summary>§B92 — خيار وردية في الاختيار اليدوي (الاسم يتضمن الساعات الفعالة لقرار الإدارة).</summary>
public class ShiftOption { public int Id { get; set; } public string Name { get; set; } }
public class PackOption { public int Id { get; set; } public string Name { get; set; } public double UnitWeightKg { get; set; } public int MoldsCount { get; set; } public double MoldWeightKg { get; set; } }

/// <summary>
/// النافذة المنبثقة لاختيار أصناف وشحنات العملاء — مطابقة لنموذج v1.59:
/// كل دفعة صف قابل للتحرير: الصنف التام (002) ← العبوة والقوالب ← الكراتين ← الخام المطلوب (يُحسب)
/// مع تحديد متعدد، فلتر بحث، تحديد الكل، إدراج فردي لكل صف، وإنزال كل المحدد دفعة واحدة.
/// </summary>
public class LotsEditorWindow : Window
{
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = false, Height = 380, CanUserAddRows = false };
    private readonly TextBox _filterBox = new() { Width = 240 };
    private readonly CheckBox _checkAll = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly List<LotEditorRow> _rows;
    private readonly List<LotEditorRow> _all;

    /// <summary>البنود الجاهزة للإنزال إلى الخطة.</summary>
    public List<LotEditorRow> Inserted { get; } = new();

    /// <summary>§B80: فترة الخطة — تاريخ كل بند إلزامي داخلها.</summary>
    private readonly DateTime? _planFrom;
    private readonly DateTime? _planTo;

    public LotsEditorWindow(List<LotEditorRow> rows, string title, bool singleCustomer,
        DateTime? planFrom = null, DateTime? planTo = null,
        List<DatesErp.Core.Domain.Entities.Shift> shifts = null, int defaultShiftId = 0)
    {
        _planFrom = planFrom;
        _planTo = planTo;
        // §B80: تاريخ افتراضي لكل بند = بداية فترة الخطة (قابل للتعديل لكل بند على حدة)
        foreach (var r in rows)
            if (r.DateValue == null)
            {
                if (!string.IsNullOrWhiteSpace(r.PresetDate) && DatesErp.Core.Common.UiFormat.TryParseDate(r.PresetDate, out var pd))
                    r.DateValue = pd;
                else r.DateValue = planFrom ?? DateTime.Today;
            }
        // §B92: الاختيار اليدوي للوردية — خيارات كل بند من الورديات النشطة (بالساعات الفعالة)،
        // والافتراضي وردية الشاشة؛ بنود المحرك تحتفظ بورديتها المجدولة (قابلة للتجاوز يدوياً).
        var shiftOpts = (shifts ?? new List<DatesErp.Core.Domain.Entities.Shift>())
            .Select(s => new ShiftOption { Id = s.Id, Name = $"{s.ShiftNameAr} ({s.EffectiveProductiveHours:0.#}س)" }).ToList();
        foreach (var r in rows)
        {
            if (r.AllShifts.Count == 0 && shiftOpts.Count > 0) r.AllShifts = shiftOpts;
            if (r.ShiftId == null)
                r.ShiftId = r.AllShifts.Any(x => x.Id == defaultShiftId) ? defaultShiftId
                    : (r.AllShifts.FirstOrDefault()?.Id);
        }
        _all = rows;
        _rows = rows;
        Title = title;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(1480, SystemParameters.WorkArea.Width - 20);
        SizeToContent = SizeToContent.Height;
        // §لا تخرج النافذة عن نطاق الشاشة أبداً
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MaxWidth = SystemParameters.WorkArea.Width - 20;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#ECE9D8");

        BuildGrid(singleCustomer);
        _grid.ItemsSource = _rows;
        _checkAll.Checked += (_, _) => { foreach (var r in _rows) r.IsChecked = true; };
        _checkAll.Unchecked += (_, _) => { foreach (var r in _rows) r.IsChecked = false; };
        _filterBox.TextChanged += (_, _) => ApplyFilter(_filterBox.Text);

        var insertAllBtn = new Button
        {
            Content = "📥 إنزال الدفعات المحددة للخطة",
            Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton"),
            Margin = new Thickness(0, 0, 8, 0),
            // §B84/K1: Enter يُنزل المحدد وEscape يغلق.
            IsDefault = true
        };
        insertAllBtn.Click += (_, _) => InsertChecked();
        var closeBtn = new Button { Content = "إغلاق", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), IsCancel = true };
        closeBtn.Click += (_, _) => Close();

        var headerBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var rightStack = new StackPanel { Orientation = Orientation.Horizontal };
        rightStack.Children.Add(new TextBlock { Text = "فلترة سريعة بالاسم أو الدفعة:", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        rightStack.Children.Add(_filterBox);
        DockPanel.SetDock(rightStack, Dock.Right);
        headerBar.Children.Add(rightStack);
        headerBar.Children.Add(insertAllBtn);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        var insertAllBtn2 = new Button
        {
            Content = "➕ إدراج كافة الدفعات المحددة بالخطة",
            Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        insertAllBtn2.Click += (_, _) => InsertChecked();
        footer.Children.Add(insertAllBtn2);
        // §B92: قرار الإدارة بعين مفتوحة — حمولة أيام الفترة من البنود المحددة حالياً قبل الإنزال
        var dayLoadBtn = new Button
        {
            Content = "📅 حمولة الأيام المحددة",
            Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        dayLoadBtn.Click += (_, _) => ShowDayLoad();
        footer.Children.Add(dayLoadBtn);
        footer.Children.Add(closeBtn);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(headerBar);
        panel.Children.Add(_grid);
        panel.Children.Add(footer);
        Content = panel;
    }

    private void BuildGrid(bool singleCustomer)
    {
        // §إصلاح «زر المربع لا يقبل التحديد»: عمود القالب يحتوي مربع اختيار حقيقي
        // يستجيب لنقرة واحدة مباشرة (عكس DataGridCheckBoxColumn الذي يتطلب دخول وضع التحرير)
        var chkCol = new DataGridTemplateColumn { Header = "اختيار", Width = 55 };
        var chkFactory = new FrameworkElementFactory(typeof(CheckBox));
        chkFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        chkFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chkFactory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding("IsChecked")
            {
                Mode = System.Windows.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            });
        chkCol.CellTemplate = new DataTemplate { VisualTree = chkFactory };
        _grid.Columns.Add(chkCol);

        // §إصلاح احترافي: كانت الحقول مكدَّسة في عمود واحد بعرض 190px (عميل + دفعة + خام)
        // فتُقتطع ولا يظهر الصنف للمستخدم. الآن عمود مستقل لكل حقل، بعرض نجمي يملأ المتاح،
        // مع ToolTip يُظهر النص كاملاً عند الاقتطاع.
        if (!singleCustomer)
            _grid.Columns.Add(TextCol("العميل المالك 👤", "CustomerName", new DataGridLength(1.1, DataGridLengthUnitType.Star), 150));
        _grid.Columns.Add(TextCol("الدفعة 📦", "LotCode", new DataGridLength(0.8, DataGridLengthUnitType.Star), 110));
        _grid.Columns.Add(TextCol("الصنف الخام المستلم 🌴", "RawName", new DataGridLength(1.1, DataGridLengthUnitType.Star), 150));

        _grid.Columns.Add(new DataGridTextColumn { Header = "المتاح (كجم)", Width = 150, IsReadOnly = true, Binding = new System.Windows.Data.Binding("AvailableDisplay") });
        _grid.Columns.Add(new DataGridTextColumn { Header = "أيام بالمخزن ⏳", Width = 95, IsReadOnly = true, Binding = new System.Windows.Data.Binding("DaysInStockText") });

        var prodCol = new DataGridTemplateColumn { Header = "الصنف التام (002) *", Width = 200 };
        var prodCombo = new FrameworkElementFactory(typeof(ComboBox));
        prodCombo.SetValue(ComboBox.ItemsSourceProperty, new System.Windows.Data.Binding("AllProducts"));
        prodCombo.SetValue(ComboBox.DisplayMemberPathProperty, "Name");
        prodCombo.SetValue(ComboBox.SelectedValuePathProperty, "Id");
        prodCombo.SetValue(ComboBox.SelectedValueProperty, new System.Windows.Data.Binding("ProductId") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        prodCol.CellTemplate = new DataTemplate { VisualTree = prodCombo };
        _grid.Columns.Add(prodCol);

        var packCol = new DataGridTemplateColumn { Header = "العبوة والقوالب", Width = 180 };
        var packCombo = new FrameworkElementFactory(typeof(ComboBox));
        packCombo.SetValue(ComboBox.ItemsSourceProperty, new System.Windows.Data.Binding("Packs"));
        packCombo.SetValue(ComboBox.DisplayMemberPathProperty, "Name");
        packCombo.SetValue(ComboBox.SelectedValuePathProperty, "Id");
        packCombo.SetValue(ComboBox.SelectedValueProperty, new System.Windows.Data.Binding("PackId") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        packCol.CellTemplate = new DataTemplate { VisualTree = packCombo };
        _grid.Columns.Add(packCol);

        var ctnCol = new DataGridTemplateColumn { Header = "الكراتين", Width = 80 };
        var ctnBox = new FrameworkElementFactory(typeof(TextBox));
        ctnBox.SetValue(TextBox.TextProperty, new System.Windows.Data.Binding("CartonsText") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        ctnBox.SetValue(TextBox.TextAlignmentProperty, TextAlignment.Center);
        ctnBox.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        ctnCol.CellTemplate = new DataTemplate { VisualTree = ctnBox };
        _grid.Columns.Add(ctnCol);

        // §B80: وحدة الصنف التام كما في بطاقته (تتبدل مع اختيار الصنف)
        _grid.Columns.Add(new DataGridTextColumn { Header = "الوحدة 📏", Width = 110, IsReadOnly = true, Binding = new System.Windows.Data.Binding("UnitDisplay") });

        // §B80: تاريخ إنتاج كل بند — إلزامي داخل فترة الخطة (يفرضه النظام عند الإنزال والحفظ)
        var dateCol = new DataGridTemplateColumn { Header = "تاريخ الإنتاج 📅", Width = 130 };
        var datePick = new FrameworkElementFactory(typeof(DatePicker));
        datePick.SetValue(FrameworkElement.WidthProperty, 120.0);
        datePick.SetBinding(DatePicker.SelectedDateProperty,
            new System.Windows.Data.Binding("DateValue") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        dateCol.CellTemplate = new DataTemplate { VisualTree = datePick };
        _grid.Columns.Add(dateCol);

        // §B92: وردية كل بند باختيار الإدارة اليدوي (بنود المحرك تبدأ بمجدولتها — قابلة للتجاوز)
        var shiftCol = new DataGridTemplateColumn { Header = "الوردية 🕐 *", Width = 170 };
        var shiftCombo = new FrameworkElementFactory(typeof(ComboBox));
        shiftCombo.SetValue(ComboBox.ItemsSourceProperty, new System.Windows.Data.Binding("AllShifts"));
        shiftCombo.SetValue(ComboBox.DisplayMemberPathProperty, "Name");
        shiftCombo.SetValue(ComboBox.SelectedValuePathProperty, "Id");
        shiftCombo.SetValue(ComboBox.SelectedValueProperty, new System.Windows.Data.Binding("ShiftId") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        shiftCol.CellTemplate = new DataTemplate { VisualTree = shiftCombo };
        _grid.Columns.Add(shiftCol);

        _grid.Columns.Add(new DataGridTextColumn { Header = "الخام المطلوب (كجم)", Width = 110, IsReadOnly = true, Binding = new System.Windows.Data.Binding("ComputedKg") { StringFormat = "N2" } });
        _grid.Columns.Add(new DataGridTextColumn { Header = "تنبيه الطاقة ⚡", Width = 210, IsReadOnly = true, Binding = new System.Windows.Data.Binding("CapAlert") });

        var actCol = new DataGridTemplateColumn { Header = "إجراء", Width = 80 };
        var btnFactory = new FrameworkElementFactory(typeof(Button));
        btnFactory.SetValue(Button.ContentProperty, "➕ إدراج");
        btnFactory.SetValue(Button.StyleProperty, System.Windows.Application.Current.FindResource("ErpButton"));
        btnFactory.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
        {
            if ((s as Button)?.DataContext is LotEditorRow row) InsertSingle(row);
        }));
        actCol.CellTemplate = new DataTemplate { VisualTree = btnFactory };
        _grid.Columns.Add(actCol);
    }

    /// <summary>
    /// §عمود نصي مستقل: عرض نجمي بحد أدنى، واقتطاع بنقاط، وToolTip يُظهر النص كاملاً.
    /// </summary>
    private static DataGridTextColumn TextCol(string header, string path, DataGridLength width, double minWidth)
    {
        var col = new DataGridTextColumn
        {
            Header = header,
            Width = width,
            MinWidth = minWidth,
            IsReadOnly = true,
            Binding = new System.Windows.Data.Binding(path)
        };
        // DataGridTextColumn يستخدم ElementStyle لا CellTemplate
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.ToolTipProperty, new System.Windows.Data.Binding(path)));
        col.ElementStyle = style;
        return col;
    }

    private static DataTemplate MakeTextTemplate(string line1, string line2, string line3)
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var t1 = new FrameworkElementFactory(typeof(TextBlock));
        t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(line1));
        t1.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        stack.AppendChild(t1);
        if (line2 != null)
        {
            var t2 = new FrameworkElementFactory(typeof(TextBlock));
            t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(line2));
            t2.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Navy);
            stack.AppendChild(t2);
        }
        if (line3 != null)
        {
            var t3 = new FrameworkElementFactory(typeof(TextBlock));
            t3.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(line3));
            t3.SetValue(TextBlock.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E)));
            stack.AppendChild(t3);
        }
        return new DataTemplate { VisualTree = stack };
    }

    private void ApplyFilter(string term)
    {
        term = (term ?? "").Trim().ToLower();
        if (string.IsNullOrEmpty(term))
        {
            _grid.ItemsSource = _all;
            return;
        }
        _grid.ItemsSource = _all.Where(r =>
            (r.CustomerName ?? "").ToLower().Contains(term) ||
            (r.LotCode ?? "").ToLower().Contains(term) ||
            (r.ShipmentNo ?? "").ToLower().Contains(term) ||
            (r.RawName ?? "").ToLower().Contains(term)).ToList();
    }

    /// <summary>
    /// §B92 — حمولة الأيام: إجماليات البنود المحددة حالياً (المرئية بعد الفلترة) مجمعة بالتاريخ —
    /// الإدارة ترى توزيعها اليدوي على أيام الفترة قبل الإنزال، لا بعده.
    /// </summary>
    private void ShowDayLoad()
    {
        var checkedRows = (_grid.ItemsSource as IEnumerable<LotEditorRow> ?? _all).Where(r => r.IsChecked).ToList();
        if (checkedRows.Count == 0)
        { MessageBox.Show("لم تحدد أي بنود بعد — علّم بمربعات الاختيار أولاً لعرض حمولة الأيام.", "حمولة الأيام", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var groups = checkedRows
            .GroupBy(r => r.DateValue?.Date)
            .OrderBy(g => g.Key)
            .Select(g => new object[]
            {
                g.Key?.ToString("dd/MM/yyyy") ?? "—",
                g.Count(),
                g.Sum(r => int.TryParse(r.CartonsText, out var c) ? c : 0),
                Math.Round(g.Sum(r => r.ComputedKg), 1),
                string.Join("، ", g.Select(r => r.ShiftName).Distinct())
            }).ToList();
        var dlg = new DetailListWindow("📅 حمولة الأيام المحددة",
            $"إجماليات {checkedRows.Count} بنداً محدداً موزعة على {groups.Count} أيام — راجع ثم أنزل.",
            new List<string> { "اليوم", "البنود", "الكراتين", "الوزن (كجم)", "الورديات" }, groups)
        { Owner = this };
        dlg.ShowDialog();
    }

    private void InsertSingle(LotEditorRow row)
    {
        string err = ValidateRow(row);
        if (err != null) { MessageBox.Show(err, "إنزال البند", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (row.IsOverCapacity)
        {
            var c = MessageBox.Show("⚠️ هذا البند يتجاوز الطاقة الإنتاجية المتبقية للوردية المحددة!\nسيُرفض حفظ الخطة لاحقاً إن تجاوز الطاقة.\nهل تريد إدراجه على أي حال لمراجعته؟",
                "تنبيه الطاقة", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (c != MessageBoxResult.Yes) return;
        }
        Inserted.Clear();
        Inserted.Add(row);
        DialogResult = true;
        Close();
    }

    private void InsertChecked()
    {
        var checkedRows = (_grid.ItemsSource as IEnumerable<LotEditorRow> ?? _all).Where(r => r.IsChecked).ToList();
        if (checkedRows.Count == 0)
        { MessageBox.Show("لم تقم بتحديد أي دفعات للإدراج — علّم بمربعات الاختيار أولاً.", "إنزال الدفعات", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        int overs = checkedRows.Count(r => r.IsOverCapacity);
        if (overs > 0)
        {
            var c = MessageBox.Show($"⚠️ يوجد ({overs}) بند يتجاوز طاقة الوردية المحددة!\nسيُرفض حفظ الخطة لاحقاً إن تجاوزت الطاقة.\nهل تريد إنزالها على أي حال للمراجعة؟",
                "تنبيه الطاقة", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (c != MessageBoxResult.Yes) return;
        }

        Inserted.Clear();
        var skippedReasons = new List<string>();
        foreach (var row in checkedRows)
        {
            string err = ValidateRow(row);
            if (err != null) { skippedReasons.Add(err); continue; }
            Inserted.Add(row);
        }
        if (Inserted.Count == 0)
        {
            MessageBox.Show(
                "لم ينزل أي بند — أسباب التخطي:\n• " + string.Join("\n• ", skippedReasons),
                "إنزال الدفعات", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // §B80: لا إسقاط صامت — المستخدم يرى كل بند تخطّى وسببه قبل الإنزال
        if (skippedReasons.Count > 0)
        {
            var c2 = MessageBox.Show(
                $"⚠️ سيُنزَّل ({Inserted.Count}) بنداً من ({checkedRows.Count}) — تخطّى النظام ({skippedReasons.Count}):\n• " +
                string.Join("\n• ", skippedReasons) +
                "\n\nهل تريد إنزال البنود الصالحة فقط؟ («لا» = عودة لتصحيح البنود)",
                "إنزال الدفعات", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (c2 != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
        Close();
    }

    private string ValidateRow(LotEditorRow row)
    {
        if (row.ProductId == null) return $"الدفعة {row.LotCode}: يرجى اختيار الصنف التام المراد إنتاجه أولاً.";
        if (!int.TryParse(row.CartonsText, out var ctn) || ctn <= 0) return $"الدفعة {row.LotCode}: يرجى إدخال عدد كراتين صحيح أكبر من الصفر.";
        // §B80: تاريخ إنتاج كل بند إلزامي وداخل فترة الخطة
        if (row.DateValue == null) return $"الدفعة {row.LotCode}: حدّد تاريخ الإنتاج لهذا البند.";
        if (_planFrom != null && row.DateValue.Value.Date < _planFrom.Value.Date)
            return $"الدفعة {row.LotCode}: تاريخ الإنتاج قبل بداية فترة الخطة ({_planFrom.Value.Date:dd/MM/yyyy}).";
        if (_planTo != null && row.DateValue.Value.Date > _planTo.Value.Date)
            return $"الدفعة {row.LotCode}: تاريخ الإنتاج بعد نهاية فترة الخطة ({_planTo.Value.Date:dd/MM/yyyy}).";
        // §B92: وردية كل بند إلزامية — الاختيار اليدوي بلا افتراض صامت عند الإنزال
        if (row.ShiftId == null) return $"الدفعة {row.LotCode}: اختر وردية الإنتاج لهذا البند.";
        // §التحقق ضد المتاح للصنف المحدد تحديداً (يخصم حجوزات هذا الصنف فقط — لا تداخل بين الأصناف)
        double avail = row.ProductId != null && row.PerProductAvailable.TryGetValue(row.ProductId.Value, out var pa)
            ? pa : row.Available;
        if (row.ComputedKg > avail + 0.001)
            return $"الدفعة {row.LotCode}: الكمية ({row.ComputedKg:N1} كجم) أكبر من المتاح لهذا الصنف ({avail:N1} كجم).";
        return null;
    }
}

/// <summary>
/// ⚖ معالج التوزيع العادل — الخطوة الأولى: محددات الفترة الحرة
/// (أسبوع أو 20 يوماً أو شهر... بلا ربط بمدة ثابتة) مع اختيار إلزامي للوردية.
/// </summary>
public class FairDistributionWizardWindow : Window
{
    private readonly DatePicker _from = new();
    private readonly DatePicker _to = new();
    private readonly ComboBox _shiftBox = new();
    private readonly ComboBox _lineBox = new();
    private readonly ComboBox _productBox = new();
    private readonly TextBox _quotaBox = new();
    private readonly CheckBox _friBox = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Shift> _shifts;
    private readonly List<ProductionLine> _lines;
    private readonly List<Product> _products;

    public string FromDate { get; private set; }
    public string ToDate { get; private set; }
    public int ShiftId { get; private set; }
    public int LineId { get; private set; }
    public int? TargetProductId { get; private set; }
    public double? DailyKg { get; private set; }
    /// <summary>§B87: تخطي الجمعة (الأسبوع: السبت–الخميس) — افتراضياً نعم.</summary>
    public bool ExcludeFriday { get; private set; } = true;

    public FairDistributionWizardWindow(List<Shift> shifts, List<ProductionLine> lines, List<Product> products,
        DateTime defaultFrom, DateTime defaultTo, int currentShiftId, int currentLineId)
    {
        _shifts = shifts; _lines = lines; _products = products;
        Title = "⚖ معالج التوزيع العادل — فترة حرة ووردية إلزامية";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 560; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#ECE9D8");

        _from.SelectedDate = defaultFrom;
        _to.SelectedDate = defaultTo;
        _shiftBox.ItemsSource = shifts;
        _shiftBox.DisplayMemberPath = "ShiftNameAr";
        var shIdx = shifts.FindIndex(s => s.Id == currentShiftId);
        _shiftBox.SelectedIndex = shIdx >= 0 ? shIdx : (shifts.Count > 0 ? 0 : -1);
        _lineBox.ItemsSource = lines;
        _lineBox.DisplayMemberPath = "LineNameAr";
        var lnIdx = lines.FindIndex(l => l.Id == currentLineId);
        _lineBox.SelectedIndex = lnIdx >= 0 ? lnIdx : (lines.Count > 0 ? 0 : -1);

        _productBox.Items.Add("— تلقائي (دوّار الأصناف التامة) —");
        foreach (var p in products) _productBox.Items.Add($"{p.ProductNameAr} ({p.ProductCode})");
        _productBox.SelectedIndex = 0;
        _quotaBox.Text = "";

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "يوزع المحرك الأرصدة الخام المتاحة على أيام الفترة بالتناوب العادل بين العملاء:\nالأقل إنجازاً أولاً ثم أقدم الحاويات (FIFO) — لا إغراق لسوق عميل ولا انتظار لصاحب الحاوية الواحدة.",
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap
        });
        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int r = 0;
        void AddRow(string label, FrameworkElement el, string hint = null)
        {
            var lb = new TextBlock { Text = label, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
            System.Windows.Controls.Grid.SetRow(lb, r); System.Windows.Controls.Grid.SetColumn(lb, 0);
            el.Margin = new Thickness(0, 0, 0, 6);
            System.Windows.Controls.Grid.SetRow(el, r); System.Windows.Controls.Grid.SetColumn(el, 1);
            grid.Children.Add(lb); grid.Children.Add(el);
            r++;
            if (hint != null)
            {
                var ht = new TextBlock { Text = hint, FontSize = 10.5, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, -4, 0, 6), TextWrapping = TextWrapping.Wrap };
                System.Windows.Controls.Grid.SetRow(ht, r); System.Windows.Controls.Grid.SetColumn(ht, 1);
                grid.Children.Add(ht); r++;
            }
        }
        AddRow("من تاريخ *:", _from);
        AddRow("إلى تاريخ *:", _to, "الفترة حرة: أسبوع، 10 أيام، 20 يوماً، شهر أو أكثر — حسب موسمك.");
        AddRow("الوردية الأساسية * (إلزامي):", _shiftBox, "يملأ المحرك كل الورديات النشطة — يبدأ بهذه ثم يفيض للبقية بمعدل كل صنف في ورديته.");
        AddRow("خط الإنتاج:", _lineBox);
        AddRow("الصنف التام المستهدف:", _productBox, "اترك «تلقائي» ليُدوّر المحرك بين كل الأصناف التامة.");
        AddRow("الحصة اليومية (كجم):", _quotaBox, "اتركها فارغة لاستخدام طاقة الورديات تلقائياً.");
        AddRow("تخطي الجمعة:", _friBox, "الأسبوع: السبت–الخميس. ألغِ التحديد إن كان المصنع يعمل أيام الجمعة.");
        panel.Children.Add(grid);

        // §B84/K1: Enter يقترح وEscape يلغي.
        var okBtn = new Button { Content = "⚖ اقترح التوزيع العادل", Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton"), Margin = new Thickness(0, 8, 8, 0), IsDefault = true };
        okBtn.Click += (_, _) => Run();
        var cancelBtn = new Button { Content = "إلغاء", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
        cancelBtn.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        panel.Children.Add(bar);
        Content = panel;
    }

    private void Run()
    {
        if (_from.SelectedDate == null || _to.SelectedDate == null || _to.SelectedDate < _from.SelectedDate)
        { MessageBox.Show("حدد فترة صحيحة: من تاريخ ≤ إلى تاريخ.", "التوزيع العادل", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (_shiftBox.SelectedIndex < 0)
        { MessageBox.Show("اختيار الوردية إلزامي — حدد الوردية أولاً.", "التوزيع العادل", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        FromDate = _from.SelectedDate.Value.ToString("dd/MM/yyyy");
        ToDate = _to.SelectedDate.Value.ToString("dd/MM/yyyy");
        ShiftId = _shifts[_shiftBox.SelectedIndex].Id;
        LineId = _lineBox.SelectedIndex >= 0 && _lineBox.SelectedIndex < _lines.Count ? _lines[_lineBox.SelectedIndex].Id : 1;
        TargetProductId = _productBox.SelectedIndex > 0 && _productBox.SelectedIndex - 1 < _products.Count
            ? _products[_productBox.SelectedIndex - 1].Id : (int?)null;
        DailyKg = double.TryParse(_quotaBox.Text?.Trim(), out var q) && q > 0 ? q : (double?)null;
        ExcludeFriday = _friBox.IsChecked != false;
        DialogResult = true;
        Close();
    }
}

/// <summary>
/// ⚖ الخطوة الثانية من معالج التوزيع العادل: ملخص نصيب كل عميل —
/// وفي القلب منه «في أي يوم ننتج لهذا العميل» — قبل الانتقال للتنزيل والتعديل.
/// </summary>
public class FairSummaryWindow : Window
{
    public class SummaryRowUi
    {
        public string CustomerName { get; set; }
        public int ContainersCount { get; set; }
        public double TotalAvailableKg { get; set; }
        public double AllocatedKg { get; set; }
        public int AllocatedCartons { get; set; }
        public double ProgressRatio { get; set; }
        public string DaysText { get; set; }
    }

    public FairSummaryWindow(FairDistributionProposal proposal)
    {
        Title = "⚖ نتيجة التوزيع العادل — نصيب كل عميل وأيام إنتاجه";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 980; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MaxWidth = SystemParameters.WorkArea.Width - 20;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#ECE9D8");

        var panel = new StackPanel { Margin = new Thickness(14) };
        var msg = new TextBlock
        {
            Text = proposal.Message,
            FontWeight = FontWeights.Bold, FontSize = 13,
            Foreground = proposal.TotalRemainingKg > 0.01
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D)),
            Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(msg);
        panel.Children.Add(new TextBlock
        {
            Text = $"إجمالي البنود المقترحة: {proposal.Rows.Count} بنداً على {proposal.DaysUsed} يوماً — حصة يومية ≈ {proposal.DailyQuotaKg:N0} كجم.",
            Margin = new Thickness(0, 0, 0, 8)
        });
        // §B87: من أين جاءت الأرقام + ملاحظات التجاوُز الصاخبة (تُقرأ قبل المتابعة)
        if (!string.IsNullOrWhiteSpace(proposal.CapacityNote))
            panel.Children.Add(new TextBlock
            {
                Text = proposal.CapacityNote,
                FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });
        if (proposal.SkippedNotes != null && proposal.SkippedNotes.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = string.Join("\n", proposal.SkippedNotes.Take(6)) + (proposal.SkippedNotes.Count > 6 ? $"\n…و {proposal.SkippedNotes.Count - 6} ملاحظات أخرى." : ""),
                FontWeight = FontWeights.Bold, FontSize = 11.5,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E)),
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });

        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 220, CanUserAddRows = false, RowHeight = 28 };
        grid.Columns.Add(new DataGridTextColumn { Header = "العميل 👤", Width = 170, Binding = new System.Windows.Data.Binding("CustomerName") });
        grid.Columns.Add(new DataGridTextColumn { Header = "الحاويات 🚢", Width = 75, Binding = new System.Windows.Data.Binding("ContainersCount") });
        grid.Columns.Add(new DataGridTextColumn { Header = "الرصيد المتاح (كجم)", Width = 120, Binding = new System.Windows.Data.Binding("TotalAvailableKg") { StringFormat = "N0" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "المخصص (كجم)", Width = 105, Binding = new System.Windows.Data.Binding("AllocatedKg") { StringFormat = "N0" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "الكراتين", Width = 70, Binding = new System.Windows.Data.Binding("AllocatedCartons") { StringFormat = "N0" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "نسبة الإنجاز %", Width = 95, Binding = new System.Windows.Data.Binding("ProgressRatio") { StringFormat = "N1" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "📅 أيام الإنتاج لهذا العميل", Width = 300, Binding = new System.Windows.Data.Binding("DaysText") });
        grid.ItemsSource = proposal.Customers.Select(c => new SummaryRowUi
        {
            CustomerName = c.CustomerName,
            ContainersCount = c.ContainersCount,
            TotalAvailableKg = c.TotalAvailableKg,
            AllocatedKg = c.AllocatedKg,
            AllocatedCartons = c.AllocatedCartons,
            ProgressRatio = c.ProgressRatio,
            DaysText = string.Join("، ", c.ProductionDays)
        }).ToList();
        panel.Children.Add(grid);

        // §B84/K1: Enter يتابع وEscape يلغي.
        var okBtn = new Button { Content = "⬅ متابعة: مراجعة البنود وتعديلها ثم الإنزال للخطة", Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton"), Margin = new Thickness(0, 10, 8, 0), IsDefault = true };
        okBtn.Click += (_, _) => { DialogResult = true; Close(); };
        var cancelBtn = new Button { Content = "إلغاء", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
        cancelBtn.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        panel.Children.Add(bar);
        Content = panel;
    }
}

/// <summary>
/// §نافذة تفاصيل عامة للقراءة فقط — تُفتح بالنقر المزدوج على أي مستند في سجلاته
/// (مستندات الإقفال وغيرها): عنوان + جدول تفاصيل بلا تحرير.
/// </summary>
public class DetailListWindow : Window
{
    public DetailListWindow(string title, string subtitle, List<string> columns, List<object[]> rows)
    {
        Title = title;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 980; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MaxWidth = SystemParameters.WorkArea.Width - 20;

        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 28 };
        foreach (var col in columns)
            grid.Columns.Add(new DataGridTextColumn { Header = col, Binding = new System.Windows.Data.Binding($"[{columns.IndexOf(col)}]"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.ItemsSource = rows;
        grid.Height = Math.Min(420, Math.Max(120, rows.Count * 28 + 40));

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x24, 0x6A)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            panel.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 11.5,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5F, 0x63, 0x68)),
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });
        panel.Children.Add(grid);
        // §B84/K1: DialogResult=false الصريح حتى يعرف المستدعي أن المستخدم أغلق بلا اختيار.
        var closeBtn = new Button { Content = "إغلاق", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(22, 5, 22, 5), IsCancel = true };
        closeBtn.Click += (_, _) => Close();
        panel.Children.Add(closeBtn);
        Content = panel;
    }
}

/// <summary>
/// §B91 — 🔍 نافذة فحص الخطة: شريط الحكم (قابلة للتنفيذ/عجز) + ملخص الأرقام
/// + تبويبات الأيام والعملاء والأصناف والتحذيرات — كل رقم من المحاكاة نفسها.
/// </summary>
public class PlanCheckWindow : Window
{
    public PlanCheckWindow(PlanCheckResult r)
    {
        Title = $"🔍 فحص الخطة {r.PlanNumber} — {r.PlanTitle}";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 1020; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MaxWidth = SystemParameters.WorkArea.Width - 20;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#ECE9D8");

        var panel = new StackPanel { Margin = new Thickness(14) };
        var conv = new System.Windows.Media.BrushConverter();
        panel.Children.Add(new Border
        {
            Background = (System.Windows.Media.Brush)conv.ConvertFromString(r.Ok ? "#DCFCE7" : "#FEE2E2"),
            BorderBrush = (System.Windows.Media.Brush)conv.ConvertFromString(r.Ok ? "#16A34A" : "#DC2626"),
            BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock
            {
                Text = r.Verdict, FontWeight = FontWeights.Bold, FontSize = 13,
                Foreground = (System.Windows.Media.Brush)conv.ConvertFromString(r.Ok ? "#14532D" : "#7F1D1D"),
                TextWrapping = TextWrapping.Wrap
            }
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"أيام العمل: {r.WorkDays} · العملاء: {r.CustomersCount} · البنود: {r.ItemsCount} · المطلوب: {r.RequiredKg:N1} كجم · المغطى: {r.CoveredKg:N1} كجم · العجز: {r.ShortageKg:N1} كجم.",
            FontSize = 12, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(r.CapacityNote))
            panel.Children.Add(new TextBlock
            {
                Text = r.CapacityNote, FontSize = 11,
                Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });

        var tabs = new TabControl { Height = 340 };
        tabs.Items.Add(new TabItem { Header = "📅 توزيع الأيام", Content = DaysGrid(r) });
        tabs.Items.Add(new TabItem { Header = "👥 تغطية العملاء", Content = CustomersGrid(r) });
        tabs.Items.Add(new TabItem { Header = "🏷️ تغطية الأصناف", Content = ItemsGrid(r) });
        var warnBox = new ListBox { FontSize = 12 };
        foreach (var w in r.Warnings) warnBox.Items.Add(w);
        if (r.Warnings.Count == 0) warnBox.Items.Add("لا تحذيرات — الفحص نظيف.");
        tabs.Items.Add(new TabItem { Header = $"⚠ التحذيرات ({r.Warnings.Count})", Content = warnBox });
        panel.Children.Add(tabs);

        var closeBtn = new Button { Content = "إغلاق", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(26, 5, 26, 5), IsCancel = true };
        closeBtn.Click += (_, _) => Close();
        panel.Children.Add(closeBtn);
        Content = panel;
    }

    private static DataGrid BaseGrid()
        => new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 28 };

    private static DataGrid DaysGrid(PlanCheckResult r)
    {
        var g = BaseGrid();
        g.Columns.Add(new DataGridTextColumn { Header = "اليوم", Width = 100, Binding = new System.Windows.Data.Binding("Date") });
        g.Columns.Add(new DataGridTextColumn { Header = "مطلوب اليوم (كجم)", Width = 120, Binding = new System.Windows.Data.Binding("DemandKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الموزع (كجم)", Width = 110, Binding = new System.Windows.Data.Binding("AllocatedKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الساعات", Width = 90, Binding = new System.Windows.Data.Binding("HoursUsed") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الحمل %", Width = 70, Binding = new System.Windows.Data.Binding("LoadPct") });
        g.Columns.Add(new DataGridTextColumn { Header = "الحالة", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new System.Windows.Data.Binding("StatusAr") });
        g.ItemsSource = r.Days;
        return g;
    }

    private static DataGrid CustomersGrid(PlanCheckResult r)
    {
        var g = BaseGrid();
        g.Columns.Add(new DataGridTextColumn { Header = "العميل", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new System.Windows.Data.Binding("CustomerName") });
        g.Columns.Add(new DataGridTextColumn { Header = "المطلوب (كجم)", Width = 110, Binding = new System.Windows.Data.Binding("RequiredKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "المغطى (كجم)", Width = 110, Binding = new System.Windows.Data.Binding("CoveredKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "العجز (كجم)", Width = 100, Binding = new System.Windows.Data.Binding("ShortageKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الحالة", Width = 90, Binding = new System.Windows.Data.Binding("StatusAr") });
        g.ItemsSource = r.Customers;
        return g;
    }

    private static DataGrid ItemsGrid(PlanCheckResult r)
    {
        var g = BaseGrid();
        g.Columns.Add(new DataGridTextColumn { Header = "العميل", Width = 150, Binding = new System.Windows.Data.Binding("CustomerName") });
        g.Columns.Add(new DataGridTextColumn { Header = "الصنف", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new System.Windows.Data.Binding("ProductName") });
        g.Columns.Add(new DataGridTextColumn { Header = "الدفعة", Width = 100, Binding = new System.Windows.Data.Binding("LotCode") });
        g.Columns.Add(new DataGridTextColumn { Header = "المطلوب", Width = 90, Binding = new System.Windows.Data.Binding("RequiredKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "المغطى", Width = 90, Binding = new System.Windows.Data.Binding("CoveredKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "طاقة/يوم", Width = 90, Binding = new System.Windows.Data.Binding("DailyCapKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "أيام لازمة", Width = 80, Binding = new System.Windows.Data.Binding("DaysNeeded") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الحالة", Width = 90, Binding = new System.Windows.Data.Binding("StatusAr") });
        g.ItemsSource = r.Items;
        return g;
    }
}
