using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Application.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>لون حالة المطابقة للمعايير المخبرية.</summary>
public class MatchStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Contains("غير مطابق") ? Brushes.Red : Brushes.Green;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>قاعدة صفوف الشبكات القابلة للتحرير: تغيير الحقل ينعكس فوراً على الأعمدة المشتقة.</summary>
public abstract class EditableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

/// <summary>صف معيار فحص مخبري/حسي — يُعرض في نافذة المعايير المنبثقة.</summary>
public class StandardRowUi : EditableRow
    {
        private double _value;
        private string _notes;
        public int StandardId { get; set; }
        public string Key { get; set; }
        public string Name { get; set; }
        public string Standard { get; set; }
        public double Min { get; set; } = double.MinValue;
        public double Max { get; set; } = double.MaxValue;
        public double Value { get => _value; set { if (Set(ref _value, value)) Raise(nameof(StatusAr)); } }
        public string Notes { get => _notes; set => Set(ref _notes, value); }
        public string StatusAr => Value >= Min && Value <= Max ? "مطابق ✓" : "غير مطابق ✗";
    }


/// <summary>
/// 🔬 شاشة فحص وتأكيد جودة التمور — §نسخة ديناميكية بالكامل:
/// • البيانات الأساسية تُنزَّل آلياً من أمر الإنتاج (لا إعادة إدخال).
/// • أنواع نتائج الفحص ووحدتها من تعريف الصنف في الإعدادات — لا اسم نتيجة ولا وحدة مكتوبة في الكود.
/// • الإجماليات لكل وحدة على حدة — لا جمع وحدات مختلفة بلا تحويل معرَّف.
/// • تخطيط بشاشة واحدة: لا تمرير عمودي، والأزرار والإجماليات ظاهرة دائماً.
/// </summary>
public partial class QualityView : UserControl
{
    /// <summary>صف نتيجة فحص: النوع والكمية والوحدة — والأعمدة المشتقة تتحدث فور الكتابة.</summary>
    private class ResultRowUi : EditableRow
    {
        private readonly Func<int, AllowedResultType> _lookup;
        private int _resultTypeId;
        private double _qty;
        private int? _unitId;
        private string _notes;

        public ResultRowUi(Func<int, AllowedResultType> lookup) { _lookup = lookup; }

        public int RowNo { get; set; }
        public int? ProductId { get; set; }
        public string ProductName { get; set; }
        public int? LotId { get; set; }
        public string LotCode { get; set; }
        public int? CustomerId { get; set; }
        public string CustomerName { get; set; }

        public int ResultTypeId
        {
            get => _resultTypeId;
            set
            {
                if (Set(ref _resultTypeId, value))
                {
                    Raise(nameof(KindAr));
                    // §4 — الوحدة تتبع قواعد النوع المعرَّف: تغيير النوع يعيد ضبط وحدته
                    var t = _lookup?.Invoke(value);
                    if (t != null)
                    {
                        _unitId = t.UnitId;
                        Raise(nameof(UnitId));
                        Raise(nameof(UnitLabel));
                        if (_qty == 0 && t.DefaultQty > 0) { _qty = t.DefaultQty; Raise(nameof(Qty)); }
                    }
                }
            }
        }

        public double Qty { get => _qty; set => Set(ref _qty, value); }

        public int? UnitId
        {
            get => _unitId;
            set { if (Set(ref _unitId, value)) Raise(nameof(UnitLabel)); }
        }

        public string Notes { get => _notes; set => Set(ref _notes, value); }

        /// <summary>التصنيف العربي — مشتق من تعريف النوع لا مكتوب في الكود.</summary>
        public string KindAr
        {
            get
            {
                var t = _lookup?.Invoke(_resultTypeId);
                return t?.ResultKindAr ?? "—";
            }
        }

        public string UnitLabel
        {
            get
            {
                var t = _lookup?.Invoke(_resultTypeId);
                return _unitId != null ? (_unitNameResolver?.Invoke(_unitId.Value) ?? t?.UnitLabel) : t?.UnitLabel;
            }
        }

        /// <summary>يُحقن من الشاشة لعرض اسم الوحدة من القاموس.</summary>
        public Func<int, string> _unitNameResolver;

        public InspectionResultDto ToDto() => new()
        {
            ResultTypeId = ResultTypeId,
            Qty = Qty,
            UnitId = UnitId,
            ProductId = ProductId,
            LotId = LotId,
            Notes = Notes
        };
    }

    private readonly ObservableCollection<ResultRowUi> _results = new();
    private readonly ObservableCollection<StandardRowUi> _standards = new();

    private List<(int id, string label, int? orderId)> _sources = new();
    private List<AllowedResultType> _allowed = new();
    private List<UnitOfMeasure> _units = new();
    private int _currentCheckId;
    private bool _approved;
    private string _status = DocStatuses.Draft;
    private string _docNo = "";
    private int? _currentOrderId;
    private int? _currentProductId;
    private int _sampleCartons = 10;
    private Views.ErpToolbar _toolbar;

    public QualityView()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
        _results.CollectionChanged += (_, _) => { Renumber(); RecalcTotals(); };
        Loaded += (_, _) => Init();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("فحص وتأكيد جودة التمور — المواصفة القياسية");
        chrome.SetScreenCode("MRPQC1002");
        _toolbar = BuildToolbar();
        chrome.SetToolbar(_toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private Views.ErpToolbar BuildToolbar()
        => new Views.ErpToolbar()
            .WithNew((_, _) => NewForm(), "فحص جودة جديد (F2)")
            .WithSave((_, _) => Save(), "حفظ الفحص — يبقى أمامك كما هو (F10)")
            .WithSearch((_, _) => OpenSearchWindow(), "بحث في فحوصات الجودة المحفوظة (F9)")
            .WithEdit((_, _) => EditDocument())
            .WithUndo((_, _) => UndoSmart(), "تراجع: يلغي الإدخالات غير المحفوظة ويعيد آخر نسخة محفوظة — لا يحذف أي فحص")
            .WithPrint((_, _) => Print(), "طباعة استمارة الفحص (Ctrl+P)")
            .WithApprove((_, _) => Approve(), "🔒 اعتماد الفحص — يفتح تسليم إنتاج هذا الأمر")
            .WithCustom("🔓 تصحيح معتمد", "ErpButton", (_, _) => RequestCorrection(), "فتح محضر معتمد للتعديل بسبب مسجل في التدقيق (صلاحية خاصة)")
            .WithCustom("📋 سجل الفحوصات", "ErpButton", (_, _) => OpenSearchWindow(), "عرض كل الفحوصات المحفوظة")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));

    // ═══════════════════════════ التهيئة ═══════════════════════════

    private void Init()
    {
        try
        {
            LoadUnitsAndTypes();
            LoadSources();
            try { InspectorName.Text = AppContainer.Get<SessionContext>().UserName ?? "—"; } catch { InspectorName.Text = "—"; }
            NewForm();

            if (MainWindow.PendingCheckIdToOpen is int pendingCheck)
            {
                MainWindow.PendingCheckIdToOpen = null;
                OpenCheck(pendingCheck);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Init"); }
    }

    /// <summary>§4 — قاموس الوحدات وأنواع النتائج من قاعدة البيانات (لا قوائم ثابتة).</summary>
    private void LoadUnitsAndTypes()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        _units = db.UnitsOfMeasure.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UnitNameAr).ToList();
        ColUnit.ItemsSource = _units;
        ColResultType.ItemsSource = insp.GetResultTypes();
    }

    private void LoadSources()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        _sources = new List<(int, string, int?)>();

        var list = new List<(int id, string label, int? orderId)>();
        if (SrcOrder.IsChecked == true)
        {
            foreach (var o in db.ProductionOrders.AsNoTracking().Where(x => x.IsApproved).OrderByDescending(x => x.Id).Take(80).ToList())
            {
                var cust = db.Customers.AsNoTracking().Where(c => c.Id == o.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—";
                var kg = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == o.Id).Sum(i => i.ProducedQtyKg);
                list.Add((o.Id, $"أمر {o.DocumentNumber} — {cust} (منتَج {UiFormat.N(kg)} كجم)", o.Id));
            }
        }
        else if (SrcExec.IsChecked == true)
        {
            foreach (var e in db.ProductionExecutions.AsNoTracking()
                         .Where(x => x.Status == "Completed" || x.IsDayClosed).OrderByDescending(x => x.Id).Take(80).ToList())
            {
                var no = db.ProductionOrders.AsNoTracking().Where(o => o.Id == e.OrderId).Select(o => o.DocumentNumber).FirstOrDefault() ?? "—";
                list.Add((e.Id, $"جلسة {e.DocumentNumber} — أمر {no}", e.OrderId));
            }
        }
        _sources = list;

        SourceBox.Items.Clear();
        SourceBox.Items.Add(SrcManual.IsChecked == true
            ? "-- فحص يدوي: اختر الصنف المفحوص من بطاقة البيانات --"
            : "-- اختر لتنزيل بيانات الفحص ونتائجه تلقائياً --");
        foreach (var s in _sources) SourceBox.Items.Add(s.label);
        SourceBox.SelectedIndex = 0;
    }

    private void NewForm()
    {
        _currentCheckId = 0;
        _currentOrderId = null;
        _approved = false;
        _status = DocStatuses.Draft;
        _docNo = "";
        _results.Clear();
        LoadStandardsFrom(null);
        CheckDate.SelectedDate = DateTime.Now;
        TypeBox.SelectedIndex = 0;
        DecisionPassed.IsChecked = true;
        InspectorNotesBox.Text = "";
        UnitsHint.Text = "";
        ClearHeader();
        LoadProducts();
        SetEditable(true);
        UpdateState();
    }

    private void ClearHeader()
    {
        HOrderNo.Text = "—"; HPlanNo.Text = "—"; HCustomer.Text = "—"; HProdDate.Text = "—";
        HLot.Text = "—"; HProduct.Text = "—"; HQty.Text = "—"; HUnit.Text = "—";
        HDate.Text = UiFormat.D(CheckDate.SelectedDate); HShift.Text = "—"; HLine.Text = "—";
        HAllowed.Text = "—";
    }

    private void LoadProducts()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = _currentOrderId != null
            ? db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == _currentOrderId).Select(i => i.ProductId).Distinct().ToList()
            : db.Products.AsNoTracking().Where(p => p.IsActive && p.ItemType == "Finished").Select(p => p.Id).ToList();

        var opts = new List<(int Id, string Name)> { (0, "— اختر الصنف —") };
        foreach (var id in items)
        {
            var p = db.Products.AsNoTracking().FirstOrDefault(x => x.Id == id);
            if (p != null) opts.Add((p.Id, $"{p.ProductCode} · {p.ProductNameAr}"));
        }
        ProductBox.ItemsSource = opts;
        ProductBox.DisplayMemberPath = "Name";
        ProductBox.SelectedValuePath = "Id";
        ProductBox.SelectedIndex = _currentOrderId != null && opts.Count > 1 ? 1 : 0;
    }

    private void UpdateState()
    {
        // §B95 — دورة حياة المحضر: مسودة ← قيد الفحص ← مكتمل ← معتمد
        DocState.Text = _currentCheckId == 0
            ? "حالة الفحص: فحص جودة جديد 🟡 — اختر أمر الإنتاج لتنزيل بياناته ونتائجه تلقائياً"
            : _approved
                ? $"حالة الفحص: {_docNo} — معتمد 🔒 (عرض فقط — التعديل عبر «تصحيح معتمد» بسبب مسجل)"
                : _status == DocStatuses.Completed
                    ? $"حالة الفحص: {_docNo} — مكتمل 🟢 (اعتمده لفتح تسليم إنتاج الأمر)"
                    : $"حالة الفحص: {_docNo} — {QualityCheckStatuses.ToArabic(_status)} 🟡 (أكمل النتائج لتغطية كامل الإنتاج)";
        if (_toolbar == null) return;
        if (_toolbar.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = !_approved;
        if (_toolbar.EditBtn != null) _toolbar.EditBtn.IsEnabled = _currentCheckId > 0 && !_approved;
        if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.IsEnabled = _currentCheckId > 0 && !_approved;
    }

    private void SetEditable(bool editable)
    {
        DecisionPassed.IsEnabled = editable; DecisionQuarantine.IsEnabled = editable; DecisionRejected.IsEnabled = editable;
        SrcExec.IsEnabled = editable; SrcOrder.IsEnabled = editable; SrcManual.IsEnabled = editable;
        SourceBox.IsEnabled = editable; ProductBox.IsEnabled = editable;
        CheckDate.IsEnabled = editable; TypeBox.IsEnabled = editable;
        ResultsGrid.IsEnabled = editable; InspectorNotesBox.IsEnabled = editable;
    }

    private void EditDocument()
    {
        if (_currentCheckId == 0) { AppContainer.Get<DialogService>().Error("لا يوجد فحص محفوظ للتعديل — اضغط «جديد»."); return; }
        if (_approved) { AppContainer.Get<DialogService>().Error(UiFormat.MsgLocked + "\nالفحص معتمد — للتعديل استخدم «🔓 تصحيح معتمد» بسبب مسجل."); return; }
        SetEditable(true);
        DocState.Text = $"حالة الفحص: {_docNo} — وضع التعديل — احفظ التغييرات أو اضغط «تراجع»";
    }

    private void UndoSmart()
    {
        if (_currentCheckId > 0) OpenCheck(_currentCheckId);
        else NewForm();
    }

    // ═══════════════════════════ المصدر والتنزيل الآلي (§1) ═══════════════════════════

    private void Source_Changed(object sender, RoutedEventArgs e)
    {
        if (SourceBox == null) return;
        LoadSources();
        if (SrcManual.IsChecked == true) { _currentOrderId = null; ClearHeader(); LoadProducts(); }
    }

    private void Source_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid == null || _approved) return;
        if (SrcManual.IsChecked == true || SourceBox.SelectedIndex <= 0 || SourceBox.SelectedIndex - 1 >= _sources.Count) return;

        try
        {
            var sel = _sources[SourceBox.SelectedIndex - 1];
            using var scope = AppContainer.NewScope();
            var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

            // §1 — كل بيانات الفحص من أمر الإنتاج: لا يعيد المستخدم إدخالها
            int orderId = SrcExec.IsChecked == true && sel.orderId != null ? sel.orderId.Value : sel.id;
            var ctx = SrcManual.IsChecked == true ? null : insp.GetOrderContext(orderId);
            if (ctx == null) return;
            _currentOrderId = ctx.OrderId;

            HOrderNo.Text = ctx.OrderNo ?? "—";
            HPlanNo.Text = ctx.PlanNo ?? "—";
            HCustomer.Text = ctx.CustomerName ?? "—";
            HProdDate.Text = ctx.ProductionDate ?? "—";
            HLot.Text = ctx.LotCode ?? "—";
            HProduct.Text = ctx.FinishedProductName ?? "—";
            // §B95 — رأس التام: الكمية المنتجة كيلو + كراتين (وحدة التام الأساسية)
            HQty.Text = ctx.ProducedCartons > 0
                ? $"{UiFormat.N(ctx.ProducedQty)} كجم · {UiFormat.N(ctx.ProducedCartons)} كرتون"
                : UiFormat.N(ctx.ProducedQty);
            HUnit.Text = ctx.ProducedUnitLabel ?? "—";
            HDate.Text = ctx.Date ?? "—";
            HShift.Text = ctx.ShiftName ?? "—";
            HLine.Text = ctx.LineName ?? "—";

            LoadProducts();
            if (ctx.FinishedProductId != null) ProductBox.SelectedValue = ctx.FinishedProductId.Value;

            // §2 — نتائج الفحص تُنشأ تلقائياً من تعريف الصنف (لا أنواع ثابتة)
            _results.Clear();
            BuildResultRows(ctx);
            UpdateState();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Source"); }
    }

    /// <summary>§7 — صفوف النتائج = أنواع النتائج المعرَّفة للصنف، بوحدتها المعتمدة.</summary>
    private void BuildResultRows(InspectionOrderContext ctx)
    {
        using var scope = AppContainer.NewScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        var productId = ctx.FinishedProductId ?? (ProductBox.SelectedValue is int pid && pid > 0 ? pid : (int?)null);
        _currentProductId = productId;
        _allowed = insp.GetAllowedResultTypesForItem(productId);
        HAllowed.Text = _allowed.Count == 0
            ? "⚠️ لا أنواع نتائج معرَّفة — أضفها من «إعدادات الأصناف ← أنواع نتائج الفحص»"
            : string.Join(" · ", _allowed.Select(a => $"{a.NameAr} ({a.UnitLabel ?? "بلا وحدة"})"));

        var first = ctx.Items.FirstOrDefault();
        foreach (var a in _allowed)
        {
            _results.Add(NewRow(a.ResultTypeId, a.DefaultQty, a.UnitId,
                productId, first.ProductName, first.LotId, first.LotCode, first.CustomerId, ctx.CustomerName));
        }
        RecalcTotals();
    }

    private ResultRowUi NewRow(int resultTypeId, double qty, int? unitId,
        int? productId, string productName, int? lotId, string lotCode, int? customerId, string customerName)
    {
        var row = new ResultRowUi(Lookup) { _unitNameResolver = UnitLabelOf };
        row.ProductId = productId;
        row.ProductName = productName ?? "—";
        row.LotId = lotId;
        row.LotCode = lotCode ?? "—";
        row.CustomerId = customerId;
        row.CustomerName = customerName ?? "—";
        // §مهم: الإسناد بالترتيب — النوع أولاً ليضبط وحدته، ثم الكمية
        row.ResultTypeId = resultTypeId;
        row.Qty = qty;
        if (unitId != null) row.UnitId = unitId;
        return row;
    }

    private AllowedResultType Lookup(int resultTypeId)
    {
        var a = _allowed.FirstOrDefault(x => x.ResultTypeId == resultTypeId);
        if (a != null) return a;
        try
        {
            using var scope = AppContainer.NewScope();
            return scope.ServiceProvider.GetRequiredService<IInspectionService>()
                .GetResultTypes(true).FirstOrDefault(x => x.ResultTypeId == resultTypeId);
        }
        catch { return null; }
    }

    private string UnitLabelOf(int unitId)
        => _units.FirstOrDefault(u => u.Id == unitId)?.UnitNameAr;

    private void ProductBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid == null || _approved) return;
        if (ProductBox.SelectedValue is not int pid || pid <= 0) { _currentProductId = null; return; }
        _currentProductId = pid;
        try
        {
            using var scope = AppContainer.NewScope();
            var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var p = db.Products.AsNoTracking().FirstOrDefault(x => x.Id == pid);
            _allowed = insp.GetAllowedResultTypesForItem(pid);
            HAllowed.Text = _allowed.Count == 0
                ? "⚠️ لا أنواع نتائج معرَّفة لهذا الصنف — أضفها من «إعدادات الأصناف ← أنواع نتائج الفحص»"
                : string.Join(" · ", _allowed.Select(a => $"{a.NameAr} ({a.UnitLabel ?? "بلا وحدة"})"));
            if (p != null) { HProduct.Text = p.ProductNameAr; HUnit.Text = p.TradingUnit ?? p.UnitOfMeasure; }

            // إعادة بناء الصفوف على أنواع الصنف الجديد مع إبقاء الكميات المُدخلة
            var keep = _results.ToDictionary(r => r.ResultTypeId, r => r.Qty);
            var custName = _results.FirstOrDefault()?.CustomerName;
            var lotId = _results.FirstOrDefault()?.LotId;
            var lotCode = _results.FirstOrDefault()?.LotCode;
            var custId = _results.FirstOrDefault()?.CustomerId;
            _results.Clear();
            foreach (var a in _allowed)
                _results.Add(NewRow(a.ResultTypeId, keep.TryGetValue(a.ResultTypeId, out var q) ? q : a.DefaultQty,
                    a.UnitId, pid, p?.ProductNameAr, lotId, lotCode, custId, custName));
            RecalcTotals();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Product"); }
    }

    // ═══════════════════════════ الجدول (§الحقول تقبل الكتابة فوراً) ═══════════════════════════

    private void AddResult_Click(object sender, RoutedEventArgs e)
    {
        if (_approved) { AppContainer.Get<DialogService>().Error(UiFormat.MsgLocked); return; }
        var a = _allowed.FirstOrDefault();
        var first = _results.FirstOrDefault();
        _results.Add(NewRow(a?.ResultTypeId ?? 0, 0, a?.UnitId,
            _currentProductId ?? first?.ProductId, first?.ProductName, first?.LotId, first?.LotCode,
            first?.CustomerId, first?.CustomerName));
    }

    private void DeleteResult_Click(object sender, RoutedEventArgs e)
    {
        if (_approved) { AppContainer.Get<DialogService>().Error(UiFormat.MsgLocked); return; }
        if (ResultsGrid.SelectedItem is ResultRowUi r) _results.Remove(r);
        else AppContainer.Get<DialogService>().Error("اختر الصف المراد حذفه أولاً.");
    }

    /// <summary>§النقر المفرد يبدأ التحرير — كان المستخدم يحتاج نقرتين أو F2 فيظن أن الحقل معطّل.</summary>
    private void Results_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_approved) return;
        var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell == null || cell.IsReadOnly) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ResultsGrid.CurrentCell = new DataGridCellInfo(cell);
            cell.Focus();
            ResultsGrid.BeginEdit();
            if (cell.Content is TextBox tb) { tb.Focus(); tb.SelectAll(); }
            else if (cell.Content is ComboBox cb) { cb.IsDropDownOpen = true; }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Results_CurrentCellChanged(object sender, EventArgs e)
    {
        if (_approved) return;
        var cell = ResultsGrid.CurrentCell;
        if (cell.Column == null || cell.Column.IsReadOnly) return;
        if (ResultsGrid.SelectedCells.Count > 0)
            Dispatcher.BeginInvoke(new Action(() => ResultsGrid.BeginEdit()),
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void Results_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(RecalcTotals), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Renumber()
    {
        int n = 1;
        foreach (var r in _results) { r.RowNo = n++; }
        ItemsBadge.Text = $"{_results.Count} نتيجة";
    }

    /// <summary>§6 — الإجماليات لكل وحدة على حدة (لا جمع وحدات مختلفة بلا تحويل).</summary>
    private void RecalcTotals()
    {
        try
        {
            if (_results.Count == 0)
            {
                TotChecked.Text = TotAccepted.Text = TotRejected.Text = TotByProduct.Text = TotLoss.Text = TotPct.Text = "—";
                UnitsHint.Text = "";
                return;
            }
            using var scope = AppContainer.NewScope();
            var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
            var t = insp.Compute(_results.Select(r => r.ToDto()).ToList());

            string Fmt(double v, string u) => $"{UiFormat.N(v)} {u}";
            if (t.SingleUnit)
            {
                string u = t.PrimaryUnitLabel ?? "";
                TotChecked.Text = Fmt(t.TotalChecked, u);
                TotAccepted.Text = Fmt(t.TotalAccepted, u);
                TotRejected.Text = Fmt(t.TotalRejected, u);
                TotByProduct.Text = Fmt(t.TotalByProduct, u);
                TotLoss.Text = Fmt(t.TotalLoss, u);
                TotPct.Text = t.AcceptancePct != null ? $"{t.AcceptancePct:N1} ٪" : "—";
                UnitsHint.Text = "";
            }
            else
            {
                TotChecked.Text = TotAccepted.Text = TotRejected.Text = TotByProduct.Text = TotLoss.Text = "لكل وحدة ↓";
                TotPct.Text = "—";
                var parts = t.ByUnit.Select(u =>
                    $"{u.UnitLabel}: مفحوص {UiFormat.N(u.Checked)} · مقبول {UiFormat.N(u.Accepted)} · مرفوض {UiFormat.N(u.Rejected)}" +
                    (u.ByProduct > 0 ? $" · ثانوي {UiFormat.N(u.ByProduct)}" : "") +
                    (u.Loss > 0 ? $" · فاقد {UiFormat.N(u.Loss)}" : ""));
                TotPct.Text = string.Join(" | ", t.ByUnit.Where(u => u.Checked > 0)
                    .Select(u => $"{u.UnitLabel} {u.Accepted / u.Checked * 100:N1}٪"));
                UnitsHint.Text = "⚠️ " + string.Join("  ||  ", parts) +
                                 "\nالنظام لا يجمع وحدات مختلفة في إجمالي واحد — عرّف تحويل وحدات من نافذة الوحدات إن أردت إجماليّاً موحّداً.";
            }

            // §B95 — معادلة التلخيص الحية في شارة الجدول (1000 = 900 + 80 + 20)
            try
            {
                var g = insp.ComputeGradeSummary(
                    _results.Where(r => r.ResultTypeId > 0).Select(r => r.ToDto()).ToList(), _currentOrderId, _currentProductId);
                ItemsBadge.Text = g.Rows.Count == 3 && g.TotalQty > 0
                    ? $"{_results.Count} نتيجة · {UiFormat.N(g.TotalQty)} = " +
                      string.Join(" + ", g.Rows.Select(x => UiFormat.N(x.Qty))) + $" {g.UnitLabel}"
                    : $"{_results.Count} نتيجة";
            }
            catch { ItemsBadge.Text = $"{_results.Count} نتيجة"; }
        }
        catch { /* الحساب لا يُفشل الإدخال */ }
    }

    // ═══════════════════════════ معايير الفحص (§نافذة منبثقة بدل جدول دائم) ═══════════════════════════

    private void ShowStandards_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using (var scope = AppContainer.NewScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                LoadStandardsFrom(db.QualityChecks.AsNoTracking().Include(c => c.Items)
                    .FirstOrDefault(c => c.Id == _currentCheckId));
            }
            var win = new StandardsWindow(_standards, _sampleCartons) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true) _sampleCartons = win.SampleCartons;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Standards"); }
    }

    private void LoadStandardsFrom(QualityCheck check)
    {
        _standards.Clear();
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            foreach (var st in db.QualityStandards.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortNo).ToList())
            {
                string range = (st.MinValue != null && st.MaxValue != null) ? $"{st.MinValue:N1} — {st.MaxValue:N1} {st.UnitLabel}"
                    : st.MinValue != null ? $"≥ {st.MinValue:N1} {st.UnitLabel}"
                    : st.MaxValue != null ? $"الحد الأقصى {st.MaxValue:N1} {st.UnitLabel}" : st.UnitLabel;
                _standards.Add(new StandardRowUi
                {
                    StandardId = st.Id, Key = st.Code, Name = st.NameAr, Standard = range,
                    Min = st.MinValue ?? double.MinValue, Max = st.MaxValue ?? double.MaxValue, Value = st.DefaultValue
                });
            }
            if (check != null)
            {
                var recs = db.QualityStandardRecords.AsNoTracking().Where(r => r.CheckId == check.Id).ToDictionary(r => r.StandardId, r => r.Value);
                foreach (var row in _standards) if (recs.TryGetValue(row.StandardId, out var v)) row.Value = v;
                _sampleCartons = check.SampleCartons > 0 ? check.SampleCartons : 10;
                if (recs.Count == 0)
                    foreach (var (code, legacy) in new[] { ("MOIST", check.MoisturePct), ("BRIX", check.BrixDeg), ("SKIN", check.SkinSeparationPct), ("IMP", check.ImpuritiesPct) })
                    {
                        var row = _standards.FirstOrDefault(x => x.Key == code);
                        if (row != null && legacy > 0) row.Value = legacy;
                    }
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Standards.Load"); }
    }

    // ═══════════════════════════ الحفظ (§5 الربط + §8 التحقق) ═══════════════════════════

    private void Save()
    {
        try
        {
            if (_approved) { AppContainer.Get<DialogService>().Error(UiFormat.MsgLocked); return; }
            ResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (_results.Count == 0)
            {
                AppContainer.Get<DialogService>().Error(
                    "لا نتائج فحص.\nاختر أمر الإنتاج لتنزيل نتائجه تلقائياً، أو اختر الصنف ثم اضغط «➕ نتيجة».\n" +
                    "وإن كانت القائمة فارغة فعرّف أنواع النتائج من «إعدادات الأصناف ← أنواع نتائج الفحص».");
                return;
            }

            using var scope = AppContainer.NewScope();
            var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var dtos = _results.Where(r => r.ResultTypeId > 0).Select(r => r.ToDto()).ToList();

            // §8 — التحقق: النوع معرَّف، الوحدة معرفة ومسموحة، الإجباري مُدخل، لا صنف خارج الأمر
            insp.ValidateResults(dtos, _currentOrderId, _currentProductId);
            // §B95 — التحقق ضد الإنتاج: أمر موجود + إنتاج مسجل + أصناف تامة فقط + لا تجاوز للمنتَج
            if (_currentOrderId != null)
                insp.ValidateAgainstProduction(dtos, _currentOrderId.Value, _currentProductId);
            var totals = insp.Compute(dtos);

            // §6 — المقبول/المرفوض بالكيلو للتقارير ومزامنة الخطة (بتحويل معرَّف فقط — لا افتراض)
            var accepted = dtos.Where(d => KindOf(d) == InspectionResultType.KindAccepted).ToList();
            var rejected = dtos.Where(d => KindOf(d) == InspectionResultType.KindRejected).ToList();
            string whyA = null, whyR = null;
            double? accKg = accepted.Count > 0 ? ToKg(db, accepted, out whyA) : 0;
            double? rejKg = rejected.Count > 0 ? ToKg(db, rejected, out whyR) : 0;
            if (accKg == null) UnitsHint.Text = "⚠️ " + whyA;
            else if (rejKg == null) UnitsHint.Text = "⚠️ " + whyR;

            // §B95 — بنود المحضر بوحدتي التام (كيلو + كرتون): كل نتيجة تُعبَّر بالوحدتين عبر تحويل معرَّف فقط —
            // بلا تحويل تُستبعد من تلك الوحدة مع تنبيه (كان الكرتون يُضرب ×1 كجم عند غياب التحويل فيُفسد المعادلة بصمت).
            // المفحوص = مقبول + مرفوض لكل وحدة (الثانوي والفاقد خارج معادلة التام).
            int kgUnit = db.UnitsOfMeasure.AsNoTracking().Where(u => u.UnitNameAr == "كجم").Select(u => u.Id).FirstOrDefault();
            int ctnUnit = db.UnitsOfMeasure.AsNoTracking().Where(u => u.UnitNameAr == "كرتون").Select(u => u.Id).FirstOrDefault();
            var convWarn = new List<string>();
            double InUnit(InspectionResultDto x, int target, string targetAr)
            {
                if (target == 0) return 0;
                int? u = x.UnitId ?? Lookup(x.ResultTypeId)?.UnitId;
                if (u == null || u == target) return x.Qty;
                var f = db.UnitConversions.AsNoTracking()
                    .Where(c => c.IsActive && c.FromUnitId == u && c.ToUnitId == target).Select(c => (double?)c.Factor).FirstOrDefault();
                if (f == null)
                {
                    string from = db.UnitsOfMeasure.AsNoTracking().Where(v => v.Id == u).Select(v => v.UnitNameAr).FirstOrDefault() ?? "-";
                    string w = $"«{Lookup(x.ResultTypeId)?.NameAr ?? "-"}» بوحدة «{from}» بلا تحويل معرَّف إلى «{targetAr}» — استُبعدت من بند الـ{targetAr}.";
                    if (!convWarn.Contains(w)) convWarn.Add(w);
                    return 0;
                }
                return x.Qty * f.Value;
            }
            var items = dtos.Where(d => d.ProductId != null)
                .GroupBy(d => new { d.ProductId, d.LotId })
                .Select(g =>
                {
                    var acc = g.Where(x => KindOf(x) == InspectionResultType.KindAccepted).ToList();
                    var rej = g.Where(x => KindOf(x) == InspectionResultType.KindRejected).ToList();
                    double accKg = Math.Round(acc.Sum(x => InUnit(x, kgUnit, "كجم")), 3);
                    double rejKg = Math.Round(rej.Sum(x => InUnit(x, kgUnit, "كجم")), 3);
                    double accCtn = Math.Round(acc.Sum(x => InUnit(x, ctnUnit, "كرتون")), 3);
                    double rejCtn = Math.Round(rej.Sum(x => InUnit(x, ctnUnit, "كرتون")), 3);
                    return new QualityItemDto
                    {
                        ProductId = g.Key.ProductId.Value,
                        LotId = g.Key.LotId,
                        AcceptedQtyKg = accKg,
                        RejectedQtyKg = rejKg,
                        CheckedQtyKg = Math.Round(accKg + rejKg, 3),
                        AcceptedCartons = accCtn,
                        RejectedCartons = rejCtn,
                        CheckedCartons = Math.Round(accCtn + rejCtn, 3),
                        Notes = string.Join(" ؛ ", g.Where(x => !string.IsNullOrWhiteSpace(x.Notes)).Select(x => x.Notes).Distinct())
                    };
                }).ToList();
            if (convWarn.Count > 0)
                UnitsHint.Text = "⚠️ " + string.Join(" ", convWarn);
            if (items.Count == 0 && _currentProductId != null)
                items.Add(new QualityItemDto
                {
                    ProductId = _currentProductId.Value,
                    AcceptedQtyKg = accKg ?? 0,
                    RejectedQtyKg = rejKg ?? 0,
                    CheckedQtyKg = (accKg ?? 0) + (rejKg ?? 0)
                });

            var lab = new QualityLabDto
            {
                Decision = DecisionRejected.IsChecked == true ? "Rejected" : DecisionQuarantine.IsChecked == true ? "Quarantine" : "Passed",
                MoisturePct = _standards.FirstOrDefault(s => s.Key == "MOIST")?.Value ?? 0,
                BrixDeg = _standards.FirstOrDefault(s => s.Key == "BRIX")?.Value ?? 0,
                SkinSeparationPct = _standards.FirstOrDefault(s => s.Key == "SKIN")?.Value ?? 0,
                ImpuritiesPct = _standards.FirstOrDefault(s => s.Key == "IMP")?.Value ?? 0,
                SampleCartons = _sampleCartons,
                InspectorNotes = InspectorNotesBox.Text
            };

            var svc = scope.ServiceProvider.GetRequiredService<IQualityService>();
            var r = svc.SaveCheck(_currentOrderId, null, CheckDate.SelectedDate?.ToString(UiFormat.DatePattern),
                TypeBox.SelectedIndex == 0 ? "نهائي — بعد التبريد" : "أثناء العملية", items, null, lab);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }

            // §حفظ النتائج الديناميكية كما هي بوحدتها + قيم المعايير
            db.InspectionResults.RemoveRange(db.InspectionResults.Where(x => x.CheckId == r.Id));
            foreach (var d in dtos)
            {
                var t = db.InspectionResultTypes.AsNoTracking().FirstOrDefault(x => x.Id == d.ResultTypeId);
                int? uid = d.UnitId ?? t?.UnitId;
                db.InspectionResults.Add(new InspectionResult
                {
                    CheckId = r.Id,
                    ProductId = d.ProductId ?? _currentProductId,
                    LotId = d.LotId,
                    ResultTypeId = d.ResultTypeId,
                    Qty = (decimal)d.Qty,
                    UnitId = uid,
                    UnitLabel = uid != null ? db.UnitsOfMeasure.AsNoTracking().Where(u => u.Id == uid).Select(u => u.UnitNameAr).FirstOrDefault() : t?.UnitLabel,
                    Notes = d.Notes
                });
            }
            db.QualityStandardRecords.RemoveRange(db.QualityStandardRecords.Where(x => x.CheckId == r.Id));
            foreach (var st in _standards.Where(x => x.StandardId > 0))
                db.QualityStandardRecords.Add(new QualityStandardRecord { CheckId = r.Id, StandardId = st.StandardId, Value = st.Value });
            db.SaveChanges();

            _currentCheckId = r.Id;
            _docNo = r.DocumentNumber;
            _approved = false;
            _status = db.QualityChecks.AsNoTracking().Where(c => c.Id == r.Id).Select(c => c.Status).FirstOrDefault() ?? DocStatuses.Draft;
            SetEditable(false);
            UpdateState();
            // §B95 — ملخص الدرجات (1000 = 900 + 80 + 20) والنسب التلقائية من المنتَج
            var summary = insp.ComputeGradeSummary(dtos, _currentOrderId, _currentProductId);
            string eqLine = summary.Rows.Count == 3
                ? $"معادلة التلخيص ({summary.UnitLabel}): {UiFormat.N(summary.TotalQty)} = " +
                  string.Join(" + ", summary.Rows.Select(x => $"{UiFormat.N(x.Qty)} {x.GradeAr}")) + ".\n"
                : "";
            string pctLine = summary.Rows.Any(x => x.PctOfProduced != null)
                ? "النسب من المنتَج: " + string.Join(" · ", summary.Rows
                    .Where(x => x.PctOfProduced != null).Select(x => $"{x.GradeAr} {x.PctOfProduced:N1}٪")) + ".\n"
                : "";
            string balLine = summary.ProducedQty == null ? ""
                : summary.Balanced ? "✔ النتائج تغطي كامل الإنتاج — المحضر «مكتمل» وقابل للاعتماد.\n"
                : $"⚠️ النتائج ({UiFormat.N(summary.TotalQty)}) لا تغطي الإنتاج ({UiFormat.N(summary.ProducedQty ?? 0)}) — المحضر «قيد الفحص» حتى الاكتمال.\n";
            if (summary.Warnings.Count > 0)
                UnitsHint.Text = "⚠️ " + string.Join(" ", summary.Warnings);
            AppContainer.Get<DialogService>().Info(
                $"تم حفظ فحص الجودة رقم: {r.DocumentNumber}\n" +
                eqLine + pctLine + balLine +
                $"النتائج: {dtos.Count} نتيجة — مقبول {UiFormat.N(totals.TotalAccepted)}، مرفوض {UiFormat.N(totals.TotalRejected)}، " +
                $"مخرجات ثانوية {UiFormat.N(totals.TotalByProduct)}.\n" +
                "الفحص باقٍ أمامك — اعتمده عند مطابقة العينات لفتح تسليم إنتاج الأمر.");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Save"); }
    }

    private string KindOf(InspectionResultDto d) => Lookup(d.ResultTypeId)?.ResultKind ?? InspectionResultType.KindAccepted;

    // §B95 — حُذف Factor (كان يضرب الكرتون ×1 كجم عند غياب التحويل فيُفسد المعادلة بصمت):
    // بنود المحضر تُبنى الآن بوحدتي التام عبر تحويل معرَّف فقط (InUnit داخل Save).

    private static double? ToKg(DatesErpDbContext db, List<InspectionResultDto> list, out string reason)
    {
        reason = null;
        int kg = db.UnitsOfMeasure.AsNoTracking().Where(u => u.UnitNameAr == "كجم").Select(u => u.Id).FirstOrDefault();
        if (kg == 0) { reason = "لا وحدة «كجم» في قاموس الوحدات — أضفها لتفعيل المكافئ بالكيلو."; return null; }
        double total = 0;
        foreach (var d in list)
        {
            int? u = d.UnitId;
            if (u == null || u == kg) { total += d.Qty; continue; }
            var f = db.UnitConversions.AsNoTracking()
                .Where(c => c.IsActive && c.FromUnitId == u && c.ToUnitId == kg).Select(c => (double?)c.Factor).FirstOrDefault();
            if (f == null)
            {
                string from = db.UnitsOfMeasure.AsNoTracking().Where(x => x.Id == u).Select(x => x.UnitNameAr).FirstOrDefault() ?? "-";
                reason = $"لا تحويل معرَّف من «{from}» إلى «كجم» — المكافئ بالكيلو غير محسوب لهذه الكمية.";
                return null;
            }
            total += d.Qty * f.Value;
        }
        return Math.Round(total, 3);
    }

    private void Approve()
    {
        try
        {
            if (_currentCheckId == 0) { AppContainer.Get<DialogService>().Error("احفظ الفحص أولاً."); return; }
            if (_approved) { AppContainer.Get<DialogService>().Error("الفحص معتمد مسبقاً."); return; }
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IQualityService>().ApproveCheck(_currentCheckId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            _approved = true;
            SetEditable(false);
            UpdateState();
            AppContainer.Get<DialogService>().Info(r.Message + "\nأصبح بالإمكان تسليم إنتاج هذا الأمر للعميل.");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Approve"); }
    }

    /// <summary>§B95 — تصحيح معتمد على محضر معتمد: سبب مكتوب إجباري يُسجَّل في التدقيق، ثم يُفتح المحضر للتعديل.</summary>
    private void RequestCorrection()
    {
        try
        {
            if (_currentCheckId == 0) { AppContainer.Get<DialogService>().Error("احفظ الفحص أولاً."); return; }
            if (!_approved) { AppContainer.Get<DialogService>().Error("الفحص غير معتمد — عدّله بالحفظ العادي."); return; }
            var dlg = new Views.InputDialog("تصحيح معتمد", "سبب فتح المحضر المعتمد للتعديل (إجباري — يُسجَّل في التدقيق):")
                { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            if (string.IsNullOrWhiteSpace(dlg.Value)) { AppContainer.Get<DialogService>().Error("التصحيح المعتمد يتطلب سبباً مكتوباً."); return; }
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IQualityService>().RequestCorrection(_currentCheckId, dlg.Value);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            _approved = false;
            _status = DocStatuses.InProgress;
            SetEditable(true);
            UpdateState();
            AppContainer.Get<DialogService>().Info(r.Message + "\nالمحضر الآن «قيد الفحص» — عدّل النتائج ثم احفظ واعتمد من جديد.");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Correction"); }
    }

    // ═══════════════════════════ فتح فحص محفوظ ═══════════════════════════

    private void OpenCheck(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
            var check = db.QualityChecks.Include(c => c.Items).FirstOrDefault(c => c.Id == id);
            if (check == null) return;

            _currentCheckId = check.Id;
            _docNo = check.DocumentNumber;
            _approved = check.IsApproved;
            _status = check.Status ?? DocStatuses.Draft;
            _currentOrderId = check.OrderId;

            // §1 — رأس الشاشة من الأمر إن وُجد
            if (check.OrderId != null)
            {
                var ctx = insp.GetOrderContext(check.OrderId.Value);
                HOrderNo.Text = ctx.OrderNo ?? "—"; HPlanNo.Text = ctx.PlanNo ?? "—";
                HCustomer.Text = ctx.CustomerName ?? "—"; HProdDate.Text = ctx.ProductionDate ?? "—";
                HLot.Text = ctx.LotCode ?? "—"; HProduct.Text = ctx.FinishedProductName ?? "—";
                HQty.Text = ctx.ProducedCartons > 0
                    ? $"{UiFormat.N(ctx.ProducedQty)} كجم · {UiFormat.N(ctx.ProducedCartons)} كرتون"
                    : UiFormat.N(ctx.ProducedQty);
                HUnit.Text = ctx.ProducedUnitLabel ?? "—";
                HDate.Text = ctx.Date ?? "—"; HShift.Text = ctx.ShiftName ?? "—"; HLine.Text = ctx.LineName ?? "—";
                _currentProductId = ctx.FinishedProductId;
            }
            else ClearHeader();

            CheckDate.SelectedDate = check.CheckDate;
            TypeBox.SelectedIndex = (check.CheckType ?? "").Contains("أثناء") ? 1 : 0;
            DecisionPassed.IsChecked = check.Decision != "Quarantine" && check.Decision != "Rejected";
            DecisionQuarantine.IsChecked = check.Decision == "Quarantine";
            DecisionRejected.IsChecked = check.Decision == "Rejected";
            InspectorNotesBox.Text = check.InspectorNotes ?? "";
            _sampleCartons = check.SampleCartons > 0 ? check.SampleCartons : 10;

            // §النتائج كما سُجّلت بوحدتها — لا إعادة اشتقاق من الكيلو (هذا كان عيب النسخة السابقة)
            var saved = db.InspectionResults.AsNoTracking().Where(x => x.CheckId == id).ToList();
            _allowed = insp.GetAllowedResultTypesForItem(_currentProductId);
            HAllowed.Text = _allowed.Count == 0 ? "—" : string.Join(" · ", _allowed.Select(a => $"{a.NameAr} ({a.UnitLabel ?? "بلا وحدة"})"));
            LoadProducts();

            _results.Clear();
            if (saved.Count > 0)
            {
                foreach (var s in saved)
                {
                    var prod = s.ProductId != null ? db.Products.AsNoTracking().FirstOrDefault(p => p.Id == s.ProductId) : null;
                    var lot = s.LotId != null ? db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == s.LotId) : null;
                    var custId = lot?.CustomerId;
                    _results.Add(NewRow(s.ResultTypeId, (double)s.Qty, s.UnitId, s.ProductId, prod?.ProductNameAr,
                        s.LotId, lot?.LotCode, custId,
                        custId != null ? db.Customers.AsNoTracking().Where(c => c.Id == custId).Select(c => c.CustomerName).FirstOrDefault() : "—"));
                }
            }
            else
            {
                // فحص من نسخة أقدم (بلا نتائج ديناميكية): تُعرض إجمالياته القديمة بوحدة الكيلو
                int kgId = db.UnitsOfMeasure.AsNoTracking().Where(u => u.UnitNameAr == "كجم").Select(u => u.Id).FirstOrDefault();
                var accT = _allowed.FirstOrDefault(a => a.ResultKind == InspectionResultType.KindAccepted);
                var rejT = _allowed.FirstOrDefault(a => a.ResultKind == InspectionResultType.KindRejected);
                if (accT != null && check.AcceptedKg > 0)
                    _results.Add(NewRow(accT.ResultTypeId, check.AcceptedKg, kgId, _currentProductId, HProduct.Text, null, HLot.Text, null, HCustomer.Text));
                if (rejT != null && check.RejectedKg > 0)
                    _results.Add(NewRow(rejT.ResultTypeId, check.RejectedKg, kgId, _currentProductId, HProduct.Text, null, HLot.Text, null, HCustomer.Text));
                UnitsHint.Text = "ℹ️ هذا الفحص من نسخة أقدم (بلا نتائج مفصّلة) — عُرضت إجمالياته بالكيلو.";
            }

            LoadStandardsFrom(check);
            SetEditable(!_approved);
            UpdateState();
            RecalcTotals();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Open"); }
    }

    // ═══════════════════════════ البحث ═══════════════════════════

    private void OpenSearchWindow()
    {
        try
        {
            var win = new DocSearchWindow("فحوصات الجودة",
                new List<SearchFieldDef>
                {
                    new() { Key = "doc", LabelAr = "رقم الفحص" },
                    new() { Key = "order", LabelAr = "رقم الأمر" },
                    new() { Key = "from", LabelAr = "من تاريخ", Kind = "date" },
                    new() { Key = "to", LabelAr = "إلى تاريخ", Kind = "date" },
                    new() { Key = "decision", LabelAr = "القرار", Kind = "combo", Options = new[] { "مطابق ومقبول", "حجز وتحريز", "مرفوض/عوادم" } }
                },
                cond => SearchChecks(cond))
            { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true && win.SelectedId != null) OpenCheck(win.SelectedId.Value);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Search"); }
    }

    private SearchResult SearchChecks(Dictionary<string, string> cond)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var q = db.QualityChecks.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(cond.GetValueOrDefault("doc"))) q = q.Where(c => c.DocumentNumber.Contains(cond["doc"].Trim()));
        if (!string.IsNullOrWhiteSpace(cond.GetValueOrDefault("order")))
            q = q.Where(c => c.OrderId != null && db.ProductionOrders.Any(o => o.Id == c.OrderId && o.DocumentNumber.Contains(cond["order"].Trim())));
        if (DateTime.TryParseExact(cond.GetValueOrDefault("from"), UiFormat.DatePattern, null, DateTimeStyles.None, out var from))
            q = q.Where(c => c.CheckDate >= from);
        if (DateTime.TryParseExact(cond.GetValueOrDefault("to"), UiFormat.DatePattern, null, DateTimeStyles.None, out var to))
            q = q.Where(c => c.CheckDate <= to.AddDays(1));
        string dec = cond.GetValueOrDefault("decision");
        if (dec == "مطابق ومقبول") q = q.Where(c => c.Decision == "Passed");
        else if (dec == "حجز وتحريز") q = q.Where(c => c.Decision == "Quarantine");
        else if (dec == "مرفوض/عوادم") q = q.Where(c => c.Decision == "Rejected");

        var result = new SearchResult { Columns = new List<string> { "رقم الفحص", "الأمر", "التاريخ", "القرار", "عدد النتائج", "المقبول", "المرفوض", "الحالة" } };
        foreach (var c in q.OrderByDescending(x => x.Id).Take(300))
        {
            int nRes = db.InspectionResults.AsNoTracking().Count(r => r.CheckId == c.Id);
            result.Rows.Add((c.Id, new object[]
            {
                c.DocumentNumber,
                c.OrderId != null ? db.ProductionOrders.AsNoTracking().Where(o => o.Id == c.OrderId).Select(o => o.DocumentNumber).FirstOrDefault() : "يدوي",
                UiFormat.D(c.CheckDate),
                DecisionAr(c.Decision),
                nRes > 0 ? $"{nRes} نتيجة" : "—",
                UiFormat.N(c.AcceptedKg), UiFormat.N(c.RejectedKg),
                c.IsApproved ? "معتمد" : "مسودة"
            }));
        }
        return result;
    }

    private static string DecisionAr(string d) => d switch
    {
        "Quarantine" => "🟡 حجز وتحريز",
        "Rejected" => "🔴 مرفوض/عوادم",
        _ => "🟢 مطابق ومقبول"
    };

    // ═══════════════════════════ الطباعة ═══════════════════════════

    private void Print()
    {
        ResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var m = new PhaseDocModel
        {
            // §القالب المرجعي print_quality.html
            DocTitle = "استمارة وشهادة فحص جودة التمور (QC Lab Sheet)",
            DocNo = _currentCheckId > 0 ? _docNo : "(مسودة)",
            MainTitle = "🧪 نتائج فحص ومطابقة العينات التامة (الوحدة في عمودها لكل نتيجة)",
            StatusAr = DecisionRejected.IsChecked == true ? "🔴 مرفوض تماماً / عوادم"
                     : DecisionQuarantine.IsChecked == true ? "🟡 حجز وتحريز مؤقت" : "🟢 مطابق ومقبول للإفراج",
            Columns = new[] { "#", "نتيجة الفحص", "التصنيف", "الكمية", "الوحدة", "الصنف", "العميل", "الدفعة", "ملاحظات" },
            Signatures = { "أخصائي فحص الجودة والمختبر", "رئيس قسم الجودة وسلامة الغذاء", "مدير المصنع / اعتماد الإفراج المخزني" }
        };
        int n = 1;
        foreach (var r in _results)
            m.Rows.Add(new object[] { n++, NameOf(r.ResultTypeId), r.KindAr, UiFormat.N(r.Qty), r.UnitLabel ?? "—",
                                      r.ProductName ?? "—", r.CustomerName ?? "—", r.LotCode ?? "—", r.Notes ?? "" });

        m.Info.Add(("رقم أمر الإنتاج", HOrderNo.Text));
        m.Info.Add(("خطة الإنتاج", HPlanNo.Text));
        m.Info.Add(("العميل", HCustomer.Text));
        m.Info.Add(("تاريخ الإنتاج", HProdDate.Text));
        m.Info.Add(("المنتج التام", HProduct.Text));
        m.Info.Add(("الكمية المنتجة", $"{HQty.Text} {HUnit.Text}".Trim()));
        m.Info.Add(("الوردية / الخط", $"{HShift.Text} / {HLine.Text}"));
        m.Info.Add(("قرار الجودة", m.StatusAr));
        m.Info.Add(("تاريخ الفحص", UiFormat.D(CheckDate.SelectedDate)));
        m.Info.Add(("نوع الفحص", TypeBox.SelectedIndex == 0 ? "نهائي — بعد التبريد" : "أثناء العملية"));
        m.Info.Add(("عينة الفحص المخبري", $"{_sampleCartons} كرتون"));
        m.Info.Add(("حالة المحضر", QualityCheckStatuses.ToArabic(_approved ? DocStatuses.Approved : _status)));
        m.Info.Add(("مسؤول الجودة", InspectorName.Text));

        m.SecondTitle = "المعايير المخبرية والحسية";
        m.SecondColumns = new[] { "المعيار", "الحدود", "المقاس الفعلي", "الحالة", "ملاحظات" };
        foreach (var s in _standards)
            m.SecondRows.Add(new object[] { s.Name, s.Standard, s.Value, s.StatusAr, s.Notes ?? "" });

        // §الإجماليات لكل وحدة على حدة — لا جمع وحدات مختلفة
        try
        {
            using var scope = AppContainer.NewScope();
            var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
            var t = insp.Compute(_results.Select(r => r.ToDto()).ToList());
            foreach (var u in t.ByUnit)
            {
                m.Totals.Add((($"المفحوص ({u.UnitLabel})"), UiFormat.N(u.Checked)));
                m.Totals.Add((($"المقبول ({u.UnitLabel})"), UiFormat.N(u.Accepted)));
                if (u.Rejected > 0) m.Totals.Add((($"المرفوض ({u.UnitLabel})"), UiFormat.N(u.Rejected)));
                if (u.NonConforming > 0) m.Totals.Add((($"غير مطابق ({u.UnitLabel})"), UiFormat.N(u.NonConforming)));   // §B103: الترويسة من الوحدة (بيانات) لا من أسماء النتائج المكتوبة
                if (u.Scrap > 0) m.Totals.Add((($"مرفوض نهائي ({u.UnitLabel})"), UiFormat.N(u.Scrap)));
                if (u.ByProduct > 0) m.Totals.Add((($"مخرجات ثانوية ({u.UnitLabel})"), UiFormat.N(u.ByProduct)));
                if (u.Loss > 0) m.Totals.Add((($"الفاقد ({u.UnitLabel})"), UiFormat.N(u.Loss)));
                if (u.Checked > 0) m.Totals.Add((($"نسبة القبول ({u.UnitLabel})"), $"{u.Accepted / u.Checked * 100:N1} ٪"));
            }
        }
        catch { }

        m.Notes = InspectorNotesBox.Text ?? "";
        new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}", p => PhasePrint.ExportPdf(m, p))
        { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private string NameOf(int resultTypeId) => Lookup(resultTypeId)?.NameAr ?? $"#{resultTypeId}";
}

/// <summary>
/// §نافذة معايير الفحص المخبري والحسي — منبثقة بدل جدول دائم داخل شاشة الفحص،
/// حتى تبقى الشاشة في صفحة واحدة بلا صعود ونزول (§قاعدة التصميم الموحدة).
/// المعايير نفسها ديناميكية من «إعدادات الأصناف» — لا معايير ثابتة في الكود.
/// </summary>
public class StandardsWindow : Window
{
    public int SampleCartons { get; private set; }

    public StandardsWindow(System.Collections.ObjectModel.ObservableCollection<StandardRowUi> rows, int sampleCartons)
    {
        SampleCartons = sampleCartons;
        Title = "🔬 معايير الفحص المخبري والحسي — المواصفة القياسية المعتمدة";
        Width = 860; SizeToContent = SizeToContent.Height;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)new BrushConverter().ConvertFromString("#ECE9D8");

        var root = new StackPanel { Margin = new Thickness(12) };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = "عينة الفحص المخبري (كرتون):", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var sampleBox = new TextBox { Width = 70, Text = sampleCartons.ToString(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(sampleBox);
        sp.Children.Add(new TextBlock
        {
            Text = "المعايير تُعرَّف وتُضاف من «إعدادات الأصناف ← معايير الفحص المخبري»",
            FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0)
        });
        DockPanel.SetDock(sp, Dock.Right);
        head.Children.Add(sp);
        root.Children.Add(head);

        var grid = new DataGrid
        {
            ItemsSource = rows, AutoGenerateColumns = false, Height = 260, RowHeight = 30,
            IsReadOnly = false, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.Cell,
            HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "معيار الفحص", Binding = new Binding("Name"), Width = 200, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "الحد القياسي المعتمد", Binding = new Binding("Standard"), Width = 190, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "القيمة المقاسة بالعينة", Binding = new Binding("Value") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "حالة المطابقة", Binding = new Binding("StatusAr"), Width = 120, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "ملاحظات الفاحص", Binding = new Binding("Notes") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        root.Children.Add(grid);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "✔ حفظ المعايير", FontSize = 12, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (int.TryParse(sampleBox.Text, out var s) && s >= 0) SampleCartons = s;
            DialogResult = true;
        };
        var cancel = new Button { Content = "إلغاء", FontSize = 12, Padding = new Thickness(16, 6, 16, 6) };
        cancel.Click += (_, _) => DialogResult = false;
        btns.Children.Add(ok); btns.Children.Add(cancel);
        root.Children.Add(btns);

        Content = root;
    }
}
