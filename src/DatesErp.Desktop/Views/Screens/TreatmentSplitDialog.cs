using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B107 — تقسيم كمية **بند استلام واحد** إلى أجزاء بدرجات إصابة مختلفة
/// (خفيفة 5 أيام · متوسطة 7 · شديدة 10) — **بلا صنف جديد**.
///
/// تُفتح من داخل شاشة الاستلام على البند نفسه، فلا يعيد الموظف إدخال الدفعة والصنف
/// والكمية من ذاكرته في شاشة أخرى (وهي الفجوة التي رصدها FIXLOG B106 §9③ و§10①).
/// الأجزاء تُحفظ مع السند، وعند الاعتماد يصير كل جزء صف <c>RawTreatment</c> مستقلاً
/// على **نفس الدفعة** — وهو ما يجيده المحرك القائم ومختبَر بـ500+500.
/// </summary>
public class TreatmentSplitDialog : Window
{
    /// <summary>سطر جزء قابل للتحرير داخل الجدول.</summary>
    public sealed class PartRow : INotifyPropertyChanged
    {
        private string _levelAr = LevelMedium;
        private double _qty;
        private int _packages;

        public string LevelAr
        {
            get => _levelAr;
            set { _levelAr = value; Raise(nameof(LevelAr)); Raise(nameof(DaysAr)); }
        }

        public double QtyKg
        {
            get => _qty;
            set { _qty = value; Raise(nameof(QtyKg)); }
        }

        public int PackageCount
        {
            get => _packages;
            set { _packages = value; Raise(nameof(PackageCount)); }
        }

        public string Notes { get; set; }

        /// <summary>المدة المقابلة للدرجة — تُعرض للموظف ولا تُحرَّر (المدة صفة الإصابة لا اختيار حر).</summary>
        public string DaysAr => LevelAr switch
        {
            LevelLight => "5 أيام (120 ساعة)",
            LevelHigh => "10 أيام (240 ساعة)",
            _ => "7 أيام (168 ساعة)"
        };

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public const string LevelLight = "إصابة خفيفة";
    public const string LevelMedium = "إصابة متوسطة";
    public const string LevelHigh = "إصابة شديدة";

    public static string LevelToCode(string ar) => ar switch
    {
        LevelLight => InfestationLevels.Light,
        LevelHigh => InfestationLevels.High,
        _ => InfestationLevels.Medium
    };

    public static string CodeToLevel(string code) => InfestationLevels.Normalize(code) switch
    {
        InfestationLevels.Light => LevelLight,
        InfestationLevels.High => LevelHigh,
        _ => LevelMedium
    };

    private readonly double _itemQty;
    private readonly int _itemPackages;
    private readonly ObservableCollection<PartRow> _rows = new();
    private TextBlock _balance;
    private DataGrid _grid;

    /// <summary>الأجزاء الناتجة بعد الموافقة — فارغة تعني «كامل الكمية بدرجة واحدة».</summary>
    public List<TreatmentPartDto> Result { get; private set; }

    public TreatmentSplitDialog(string productName, double itemQtyKg, int itemPackages,
                                IEnumerable<TreatmentPartDto> existing)
    {
        _itemQty = itemQtyKg;
        _itemPackages = itemPackages;

        Title = $"تقسيم درجات الإصابة — {productName}";
        Width = 720;
        Height = 470;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var p in existing ?? Enumerable.Empty<TreatmentPartDto>())
            _rows.Add(new PartRow
            {
                LevelAr = CodeToLevel(p.InfestationLevel),
                QtyKg = p.QtyKg,
                PackageCount = p.PackageCount,
                Notes = p.Notes
            });

        if (_rows.Count == 0)
            _rows.Add(new PartRow { LevelAr = LevelMedium, QtyKg = _itemQty, PackageCount = _itemPackages });

        Build();
        _rows.CollectionChanged += (_, _) => UpdateBalance();
        UpdateBalance();
    }

    private void Build()
    {
        var root = new DockPanel { Margin = new Thickness(14), LastChildFill = true };

        var header = new TextBlock
        {
            Text = $"كمية البند: {_itemQty:N1} كجم · {_itemPackages:N0} طرد\n"
                 + "قسّم الكمية على درجات الإصابة — الصنف واحد ولا يُنشأ صنف جديد. "
                 + "المدة تتبع درجة الإصابة (5 / 7 / 10 أيام)، وتبدأ المعالجة تلقائياً عند اعتماد السند.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)),
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var add = new Button { Content = "➕ إضافة درجة", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => { _rows.Add(new PartRow { LevelAr = LevelMedium, QtyKg = Math.Max(0, Remaining()) }); UpdateBalance(); };

        var del = new Button { Content = "✕ حذف المحدد", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        del.Click += (_, _) =>
        {
            if (_grid.SelectedItem is PartRow r && _rows.Count > 1) { _rows.Remove(r); UpdateBalance(); }
        };

        var rest = new Button { Content = "⇦ الباقي إلى المحدد", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        rest.Click += (_, _) =>
        {
            if (_grid.SelectedItem is PartRow r) { r.QtyKg += Remaining(); _grid.Items.Refresh(); UpdateBalance(); }
        };

        var ok = new Button { Content = "✔ اعتماد التقسيم", Padding = new Thickness(14, 3, 14, 3), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Submit();

        var cancel = new Button { Content = "إلغاء", Padding = new Thickness(14, 3, 14, 3) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };

        foreach (var b in new[] { ok, add, rest, del, cancel }) buttons.Children.Add(b);
        root.Children.Add(buttons);

        _balance = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_balance, Dock.Bottom);
        root.Children.Add(_balance);

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            RowHeight = 30,
            FontSize = 13,
            ItemsSource = _rows
        };
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateBalance));

        var levelCol = new DataGridComboBoxColumn
        {
            Header = "درجة الإصابة",
            Width = 170,
            ItemsSource = new[] { LevelLight, LevelMedium, LevelHigh },
            SelectedItemBinding = new System.Windows.Data.Binding(nameof(PartRow.LevelAr))
            { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }
        };
        _grid.Columns.Add(levelCol);
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "المدة",
            Width = 150,
            IsReadOnly = true,
            Binding = new System.Windows.Data.Binding(nameof(PartRow.DaysAr))
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الكمية (كجم)",
            Width = 120,
            Binding = new System.Windows.Data.Binding(nameof(PartRow.QtyKg))
            { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus }
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "عدد الطرود",
            Width = 100,
            Binding = new System.Windows.Data.Binding(nameof(PartRow.PackageCount))
            { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus }
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "ملاحظات",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new System.Windows.Data.Binding(nameof(PartRow.Notes))
        });

        root.Children.Add(_grid);
        Content = root;
    }

    private double Sum() => _rows.Sum(r => r.QtyKg);
    private double Remaining() => Math.Round(_itemQty - Sum(), 3);

    private void UpdateBalance()
    {
        double rem = Remaining();
        _balance.Text = $"مجموع الأجزاء: {Sum():N1} كجم من {_itemQty:N1} كجم — "
                      + (Math.Abs(rem) < 0.001 ? "✔ مطابق" : rem > 0 ? $"⚠ متبقٍ غير موزَّع: {rem:N1} كجم" : $"⛔ زيادة: {-rem:N1} كجم");
        _balance.Foreground = Math.Abs(rem) < 0.001
            ? new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D))
            : new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    }

    private void Submit()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);

        if (_rows.Any(r => r.QtyKg <= 0))
        {
            MessageBox.Show("لا يجوز جزء بكمية صفر — احذف السطر أو أدخل كمية.", "تقسيم درجات الإصابة",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Math.Abs(Remaining()) > 0.001)
        {
            // §الحارس هنا وقائي فقط — الخدمة تفحصه ثانيةً عند الحفظ، فالواجهة لا تُؤتمن وحدها
            MessageBox.Show($"مجموع الأجزاء يجب أن يساوي كمية البند بالضبط ({_itemQty:N1} كجم).",
                "تقسيم درجات الإصابة", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // §توزيع الطرود: ما لم يحدده الموظف يُوزَّع بنسبة الكمية، والأخير يأخذ الباقي
        int left = _itemPackages;
        var list = new List<TreatmentPartDto>();
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            int pk = r.PackageCount > 0
                ? r.PackageCount
                : (i == _rows.Count - 1
                    ? Math.Max(0, left)
                    : (_itemQty <= 0 ? 0 : (int)Math.Round(_itemPackages * (r.QtyKg / _itemQty), MidpointRounding.AwayFromZero)));
            left -= pk;
            list.Add(new TreatmentPartDto
            {
                InfestationLevel = LevelToCode(r.LevelAr),
                QtyKg = r.QtyKg,
                PackageCount = Math.Max(0, pk),
                Notes = r.Notes
            });
        }

        Result = list;
        DialogResult = true;
        Close();
    }
}
