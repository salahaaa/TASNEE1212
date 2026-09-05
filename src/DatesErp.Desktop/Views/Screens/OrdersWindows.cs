using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §أمر الإنتاج من الخطة — لا إعادة إدخال: العميل والصنف والمنتج والمخطط تُجلب من الخطة،
/// والمستخدم يحدد فقط كمية التنفيذ من المتبقي والتاريخ والوردية والخط.
/// المتبقي = المخطط − أوامر سابقة، والطاقة تُحسب لحظياً، والتوزيع على عدة ورديات بضغطة واحدة.
/// </summary>
public class NewOrderPanel : UserControl
{
    /// <summary>صف قيد التوزيع على الورديات.</summary>
    private sealed class PendingRow { public ItemRowUi Row; public int RemainingCartons; public double RemainingKg; }

    private class ItemRowUi
    {
        public OrderableItemDto Src { get; set; }
        public bool IsChecked { get; set; }
        public string CustomerName { get; set; }
        public string LotCode { get; set; }
        public string RawDisplay { get; set; }
        public string ProductName { get; set; }
        public string PackName { get; set; }
        public double PlannedKg { get; set; }
        public int PlannedCartons { get; set; }
        public double OrderedKg { get; set; }
        public int OrderedCartons { get; set; }
        public double RemainingKg { get; set; }
        public int RemainingCartons { get; set; }
        public double QtyKg { get; set; }
        public int Cartons { get; set; }
        public double PackWeight { get; set; }
        /// <summary>§إصلاح: التاريخ المجدول لبند الخطة — كان الـ Backend يرسله والواجهة تهمله.</summary>
        public string ScheduledDate { get; set; }
        public string ShiftName { get; set; }
    }

    private readonly ComboBox _planBox = new() { Width = 380, MinHeight = 28 };
    private readonly DatePicker _date = new() { Width = 140 };
    private readonly ComboBox _shiftBox = new() { Width = 200, MinHeight = 28 };
    private readonly ComboBox _lineBox = new() { Width = 180, MinHeight = 28 };
    private readonly TextBox _notes = new() { Width = 320, MinHeight = 28 };
    private readonly CheckBox _autoSplit = new() { Content = "وزّع تلقائياً على الورديات حسب الطاقة (أمر لكل وردية)", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _capacityInfo = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)), Margin = new Thickness(0, 6, 0, 0) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, Height = 300, RowHeight = 30 };
    private readonly List<ItemRowUi> _rows = new();
    private List<(int id, string label)> _plans = new();
    private List<(int id, string name)> _shifts = new();
    private List<(int id, string name)> _lines = new();

    /// <summary>أوامر الإنتاج التي أنشأتها هذه النافذة — لفتحها في الواجهة الرئيسية مباشرة.</summary>
    public List<int> CreatedOrderIds { get; } = new();
    public event Action<int> OrderCreated;
    public event Action CloseRequested;

    public NewOrderPanel()
    {
        FlowDirection = FlowDirection.RightToLeft;

        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "اختيار", Binding = new System.Windows.Data.Binding("IsChecked") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 55 });
        _grid.Columns.Add(IdentCol("العميل المالك 👤", "CustomerName", 1.2, 140));
        _grid.Columns.Add(IdentCol("الدفعة 📦", "LotCode", 0.9, 110));
        _grid.Columns.Add(IdentCol("الصنف الخام المستلم (رصيد الدفعة) 🌴", "RawDisplay", 1.5, 190));
        _grid.Columns.Add(IdentCol("المنتج النهائي 🏷️", "ProductName", 1.3, 160));
            _grid.Columns.Add(IdentCol("العبوة 📦", "PackName", 0.9, 110));
            // §إصلاح: عمود التاريخ المجدول — بدونه كانت كل بنود خطة الـ14 يوماً تنزل بلا تمييز أيام
            _grid.Columns.Add(new DataGridTextColumn { Header = "تاريخ الإنتاج المجدول 📅", Binding = new System.Windows.Data.Binding("ScheduledDate"), Width = 130, IsReadOnly = true });
            // §طلب المستخدم: المخطط بالكرتون كما في الخطة — أعمدة الكراتين أولاً ثم الكجم
            _grid.Columns.Add(new DataGridTextColumn { Header = "المخطط (كرتون)", Binding = new System.Windows.Data.Binding("PlannedCartons"), Width = 100, IsReadOnly = true });
            _grid.Columns.Add(new DataGridTextColumn { Header = "أوامر سابقة (كرتون)", Binding = new System.Windows.Data.Binding("OrderedCartons"), Width = 110, IsReadOnly = true });
            _grid.Columns.Add(new DataGridTextColumn { Header = "المتبقي (كرتون)", Binding = new System.Windows.Data.Binding("RemainingCartons"), Width = 100, IsReadOnly = true });
                                        // §قاعدة الكرتون: الكراتين هي وحدة الأمر الأساسية، والكجم وزن مكافئ للقراءة فقط
            _grid.Columns.Add(new DataGridTextColumn { Header = "كراتين الأمر *", Binding = new System.Windows.Data.Binding("Cartons") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 95 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "الوزن المكافئ (كجم)", Binding = new System.Windows.Data.Binding("QtyKg"), Width = 110, IsReadOnly = true });
        _grid.ItemsSource = _rows;

        _planBox.SelectionChanged += (_, _) => LoadItems();
        _shiftBox.SelectionChanged += (_, _) => ShowCapacity();
        _date.SelectedDateChanged += (_, _) => ShowCapacity();
        _grid.SelectionChanged += (_, _) => ShowCapacity();
        // §قاعدة الكرتون: تعديل الكراتين يشتق الوزن المكافئ فوراً
        _grid.CellEditEnding += (_, e) =>
        {
            if (e.Row?.Item is ItemRowUi row && e.Column?.Header?.ToString() == "كراتين الأمر *")
            {
                row.QtyKg = row.PackWeight > 0 ? Math.Round(row.Cartons * row.PackWeight, 1) : 0; // §B85/M3: بلا وزن معرَّف لا يُشتق وزن — يُرفض عند الإنشاء برسالة صريحة
                _grid.Items.Refresh();
                ShowCapacity();
            }
        };

        var createBtn = new Button { Content = "💾 حفظ وإنشاء أمر الإنتاج", Padding = new Thickness(18, 8, 18, 8), FontSize = 13 };
        createBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton");
        createBtn.Click += (_, _) => Create();
        // §زر إنزال بنود الخطة المطلوب صراحة: يعيد تحميل الخطط إن غابت وينزل بنود الخطة المحددة
        var pullBtn = new Button { Content = "⬇ إنزال بنود الخطة", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 0, 0, 0) };
        pullBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton");
        pullBtn.Click += (_, _) => { if (_plans.Count == 0) Init(); LoadItems(); };
        var cancelBtn = new Button { Content = "إغلاق", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => CloseRequested?.Invoke();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Lbl("خطة الإنتاج المعتمدة:")); head.Children.Add(_planBox);
        head.Children.Add(pullBtn);
        head.Children.Add(Lbl("تاريخ الإنتاج:")); head.Children.Add(_date);
        head.Children.Add(Lbl("الوردية:")); head.Children.Add(_shiftBox);
        head.Children.Add(Lbl("خط الإنتاج:")); head.Children.Add(_lineBox);

        var exec = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        exec.Children.Add(Lbl("ملاحظات:")); exec.Children.Add(_notes);
        exec.Children.Add(createBtn); exec.Children.Add(cancelBtn);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "§اختر الخطة لتظهر بنودها بمرجعها الكامل (العميل/الدفعة/الصنف المستلم/المنتج النهائي) مع المتبقي بعد الأوامر السابقة — حدد الكمية من المتبقي فقط، ولا يمكن تجاوز متبقي الخطة أو طاقة الوردية.",
            FontSize = 11.5, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(head);
        panel.Children.Add(_grid);
        panel.Children.Add(_autoSplit);
        panel.Children.Add(_capacityInfo);
        panel.Children.Add(exec);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Loaded += (_, _) => Init();
    }

    private static TextBlock Lbl(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontWeight = FontWeights.Bold };

    private void Init()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _plans = db.ProductionPlans.AsNoTracking()
                .Where(p => p.IsApproved && !p.IsClosed && p.Status != DocStatuses.Cancelled)
                .OrderByDescending(p => p.Id)
                .Select(p => new { p.Id, p.DocumentNumber, p.PlanTitle }).ToList()
                .Select(x => (x.Id, $"{x.DocumentNumber} — {x.PlanTitle}")).ToList();
            _planBox.Items.Clear();
            foreach (var p in _plans) _planBox.Items.Add(p.label);
            if (_plans.Count > 0) _planBox.SelectedIndex = 0;

            _shifts = db.Shifts.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.ShiftNameAr, s.StartTime, s.EndTime }).ToList()
                .Select(x => (x.Id, $"{x.ShiftNameAr} ({x.StartTime}–{x.EndTime})")).ToList();
            _shiftBox.Items.Clear();
            foreach (var s in _shifts) _shiftBox.Items.Add(s.name);
            if (_shifts.Count > 0) _shiftBox.SelectedIndex = 0;

            _lines = db.ProductionLines.AsNoTracking().OrderBy(l => l.Id).Select(l => new { l.Id, l.LineNameAr }).ToList()
                .Select(x => (x.Id, x.LineNameAr)).ToList();
            _lineBox.Items.Clear();
            foreach (var l in _lines) _lineBox.Items.Add(l.name);
            if (_lines.Count > 0) _lineBox.SelectedIndex = 0;

            _date.SelectedDate = DateTime.Today;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "NewOrder.Init"); }
    }

    /// <summary>
    /// §عمود هوية مقروء: عرض نجمي يملأ المتاح بحد أدنى، واقتطاع بنقاط، وToolTip يُظهر النص كاملاً.
    /// كانت الأعمدة بعرض ثابت ومجموعها 1515px فتُقتطع ولا يظهر الصنف للمستخدم.
    /// </summary>
    internal static DataGridTextColumn IdentCol(string header, string path, double star, double minWidth)
    {
        var col = new DataGridTextColumn
        {
            Header = header,
            Width = new DataGridLength(star, DataGridLengthUnitType.Star),
            MinWidth = minWidth,
            IsReadOnly = true,
            Binding = new System.Windows.Data.Binding(path)
        };
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.ToolTipProperty, new System.Windows.Data.Binding(path)));
        col.ElementStyle = style;
        return col;
    }

    private void LoadItems()
    {
        _rows.Clear();
        if (_planBox.SelectedIndex < 0) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var items = svc.GetOrderableItems(_plans[_planBox.SelectedIndex].id);
            foreach (var it in items)
            {
                double packW = it.PackagingTypeId != null
                    ? db.PackagingTypes.Where(p => p.Id == it.PackagingTypeId).Select(p => p.UnitWeightKg).FirstOrDefault()
                    : 0;
                if (packW <= 0) packW = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.CartonWeightKg).FirstOrDefault();
                if (packW <= 0) packW = 7.2;
                _rows.Add(new ItemRowUi
                {
                    Src = it,
                    IsChecked = it.RemainingKg > 0,
                    CustomerName = it.CustomerName,
                    LotCode = it.LotCode,
                    RawDisplay = $"{it.RawName} ({it.LotRemainingKg:N0} كجم)",
                    ProductName = it.ProductName,
                    // §نظام الوحدات: وزن الكرتون من تعريف العبوة/المنتج — ظاهر للمستخدم قبل الإنشاء
                    PackName = $"{it.PackName ?? "-"} = {packW:N1} كجم/كرتون",
                    PlannedKg = it.PlannedKg,
                    PlannedCartons = it.PlannedCartons,
                    OrderedKg = it.OrderedKg,
                    OrderedCartons = it.OrderedCartons,
                    RemainingKg = Math.Round(it.RemainingKg, 1),
                    RemainingCartons = it.RemainingCartons,
                    QtyKg = Math.Round(it.RemainingKg, 1),
                    Cartons = it.RemainingCartons > 0 ? it.RemainingCartons : (it.RemainingKg > 0 ? (int)Math.Ceiling(it.RemainingKg / packW) : 0),
                    PackWeight = packW,
                    ScheduledDate = string.IsNullOrWhiteSpace(it.ScheduledDate) ? "—" : it.ScheduledDate,
                    ShiftName = it.SuggestedShiftId != null
                        ? db.Shifts.AsNoTracking().Where(x => x.Id == it.SuggestedShiftId).Select(x => x.ShiftNameAr).FirstOrDefault() ?? "—"
                        : "—"
                });
            }
            // §اقتراح الوردية/الخط من أول بند إن وُجد
            var first = items.FirstOrDefault(i => i.SuggestedShiftId != null);
            if (first != null)
            {
                int idx = _shifts.FindIndex(s => s.id == first.SuggestedShiftId);
                if (idx >= 0) _shiftBox.SelectedIndex = idx;
            }
            // §إصلاح: تاريخ الأمر يقترح من تاريخ أول بند مجدول — لا من «اليوم».
            var firstDated = items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ScheduledDate));
            if (firstDated != null && DatesErp.Core.Common.UiFormat.TryParseDate(firstDated.ScheduledDate, out var suggestedDay))
                _date.SelectedDate = suggestedDay;
            ShowCapacity();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "NewOrder.LoadItems"); }
    }

    /// <summary>§10 — الطاقة المتاحة لحظياً: المعدل والساعات والطاقة والمطلوب والمتبقي.</summary>
    private void ShowCapacity()
    {
        if (_date.SelectedDate == null) return;
        var selected = _rows.Where(r => r.IsChecked && r.Cartons > 0).ToList();
        if (selected.Count == 0) { _capacityInfo.Text = "علّم بنداً واحداً على الأقل لعرض حساب الطاقة."; return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            int shiftId = _shiftBox.SelectedIndex >= 0 ? _shifts[_shiftBox.SelectedIndex].id : 1;
            int lineId = _lineBox.SelectedIndex >= 0 ? _lines[_lineBox.SelectedIndex].id : 1;
            string date = _date.SelectedDate.Value.ToString("dd/MM/yyyy");

            var lines = new List<string>();
            foreach (var r in selected)
            {
                var slot = svc.GetOrderSlot(r.Src.ProductId, r.Src.PackagingTypeId, shiftId, lineId, date);
                double reqHours = r.Cartons / (slot.RatePerHour > 0 ? slot.RatePerHour : 500);
                string state = r.Cartons <= slot.RemainingCartons ? "✅ ضمن الطاقة" : $"⛔ يتجاوز المتاح بـ {r.Cartons - slot.RemainingCartons:N0} كرتون";
                lines.Add($"• {r.ProductName}: معدل {slot.RatePerHour:N0} كرتون/س × {slot.ProductionHours:N1} س = طاقة {slot.CapacityCartons:N0} كرتون | المتاح في الفتحة: {slot.RemainingCartons:N0} | المطلوب: {r.Cartons:N0} ({reqHours:N1} س) — {state}");
            }
            _capacityInfo.Text = $"حساب الطاقة — {(_shiftBox.SelectedIndex >= 0 ? _shifts[_shiftBox.SelectedIndex].name : "-")} يوم {date}:\n" + string.Join("\n", lines)
                + "\nإن تجاوزت الكمية طاقة وردية واحدة: فعّل التوزيع التلقائي لتوزيعها على الورديات بأوامر مستقلة.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "NewOrder.Capacity"); }
    }

    private void Create()
    {
        try
        {
            _grid.CommitEdit();
            // §قاعدة الكرتون: الكراتين أساس والكيلو مكافئ مشتق
            // §B85/M3: لا وزن مخترع — الكراتين بلا وزن معرَّف تُرفض صراحة (كانت × 7.5 بصمت)
            var noWeight = _rows.Where(r => r.IsChecked && r.Cartons > 0 && r.PackWeight <= 0).ToList();
            if (noWeight.Count > 0)
            {
                AppContainer.Get<DialogService>().Error(
                    "⛔ لا يمكن اشتقاق الوزن — وزن الكرتون غير معرَّف للأصناف التالية:\n• " +
                    string.Join("\n• ", noWeight.Select(z => $"{z.ProductName} ({z.CustomerName})")) +
                    "\n\nعرّف وزن الكرتون (أو القوالب × وزن القالب) في بطاقة الصنف أو العبوة أولاً.");
                return;
            }
            foreach (var rr in _rows.Where(r => r.IsChecked && r.Cartons > 0))
                rr.QtyKg = Math.Round(rr.Cartons * rr.PackWeight, 1);
            // §B80: بند مُعلَّم بكمية صفر = خطأ صريح — لا إسقاط صامت ولا أمر بصفر إنتاج
            var zeroRows = _rows.Where(r => r.IsChecked && r.Cartons <= 0 && r.QtyKg <= 0).ToList();
            if (zeroRows.Count > 0)
            {
                AppContainer.Get<DialogService>().Error(
                    "⛔ لا يمكن إنشاء أمر بصفر إنتاج — البنود المعلَّمة التالية بلا كمية:\n• " +
                    string.Join("\n• ", zeroRows.Select(z => $"{z.ProductName} ({z.CustomerName})")) +
                    "\n\nأدخل عدد الكراتين لكل بند معلم، أو أزل عنه علامة الاختيار.");
                return;
            }
            var selected = _rows.Where(r => r.IsChecked && (r.QtyKg > 0 || r.Cartons > 0)).ToList();
            if (selected.Count == 0) { AppContainer.Get<DialogService>().Error("علّم بنداً واحداً على الأقل وأدخل كمية أكبر من صفر."); return; }
            if (_date.SelectedDate == null) { AppContainer.Get<DialogService>().Error("حدد تاريخ الإنتاج."); return; }

            // §8 — فحص المتبقي في الواجهة برسالة واضحة (ويعاد فحصه في الـ Backend إلزامياً)
            foreach (var r in selected)
            {
                if (r.QtyKg > r.Src.RemainingKg + 0.001)
                {
                    AppContainer.Get<DialogService>().Error(
                        $"⛔ الكمية المطلوبة تتجاوز الكمية المتبقية في خطة الإنتاج.\n" +
                        $"الصنف: {r.ProductName} | المتبقي: {r.Src.RemainingKg:N1} كجم | المطلوب: {r.QtyKg:N1} كجم");
                    return;
                }
                if (r.Cartons <= 0 && r.QtyKg > 0 && r.PackWeight > 0) r.Cartons = (int)Math.Ceiling(r.QtyKg / r.PackWeight); // §B85/M3: حراسة القسمة على صفر
            }

            int shiftId = _shiftBox.SelectedIndex >= 0 ? _shifts[_shiftBox.SelectedIndex].id : 1;
            int lineId = _lineBox.SelectedIndex >= 0 ? _lines[_lineBox.SelectedIndex].id : 1;
            string date = _date.SelectedDate.Value.ToString("dd/MM/yyyy");
            int planId = _plans[_planBox.SelectedIndex].id;

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var created = new List<string>();

            if (!_autoSplit.IsChecked == true)
            {
                // §إصلاح: أمر لكل (عميل × يوم إنتاج مجدول × وردية) — لا أمر واحد بكل الأيام.
                // كان كل بنود خطة الـ14 يوماً تأخذ تاريخاً واحداً فتُهدر جدولة الخطة.
                foreach (var grp in selected.GroupBy(r => (
                             Cust: r.Src.CustomerId ?? 0,
                             Day: !string.IsNullOrWhiteSpace(r.Src.ScheduledDate) && DatesErp.Core.Common.UiFormat.TryParseDate(r.Src.ScheduledDate, out var gd) ? gd.Date : (DateTime?)null,
                             Shift: r.Src.SuggestedShiftId ?? 0)))
                {
                    string grpDate = grp.Key.Day?.ToString("dd/MM/yyyy") ?? date;
                    int grpShift = grp.Key.Shift > 0 ? grp.Key.Shift : shiftId;
                    var res = svc.SaveOrder("FromPlan", planId, grp.Key.Cust == 0 ? null : grp.Key.Cust, grpDate, grpShift, lineId,
                        grp.Select(r => new OrderItemDto
                        {
                            PlanItemId = r.Src.PlanItemId,
                            LotId = r.Src.LotId,
                            CustomerId = r.Src.CustomerId,
                            ProductId = r.Src.ProductId,
                            PackagingTypeId = r.Src.PackagingTypeId,
                            PlannedQtyKg = r.QtyKg,
                            PlannedCartons = r.Cartons
                        }).ToList());
                    if (!res.Ok) { AppContainer.Get<DialogService>().Error(res.Message, "إنشاء أمر الإنتاج"); return; }
                    created.Add(res.DocumentNumber);
                    CreatedOrderIds.Add(res.Id);
                    OrderCreated?.Invoke(res.Id);
                }
            }
            else
            {
                // §12 — التوزيع التلقائي على الورديات: أمر مستقل لكل وردية، وكلها من نفس بنود الخطة
                foreach (var grp in selected.GroupBy(r => r.Src.CustomerId ?? 0))
                {
                    var pending = grp.Select(r => new PendingRow { Row = r, RemainingCartons = r.Cartons, RemainingKg = r.QtyKg }).ToList();
                    foreach (var (shId, _) in _shifts)
                    {
                        if (pending.All(p => p.RemainingCartons <= 0)) break;
                        var shiftItems = new List<(ItemRowUi row, int cartons, double kg)>();
                        foreach (var p in pending.Where(p => p.RemainingCartons > 0))
                        {
                            var slot = svc.GetOrderSlot(p.Row.Src.ProductId, p.Row.Src.PackagingTypeId, shId, lineId, date);
                            int take = Math.Min(p.RemainingCartons, slot.RemainingCartons);
                            if (take <= 0) continue;
                            double kg = Math.Min(p.RemainingKg, Math.Round(take * p.Row.PackWeight, 1));
                            shiftItems.Add((p.Row, take, kg));
                            p.RemainingCartons -= take; p.RemainingKg -= kg;
                        }
                        if (shiftItems.Count == 0) continue;
                        var res = svc.SaveOrder("FromPlan", planId, grp.Key == 0 ? null : grp.Key, date, shId, lineId,
                            shiftItems.Select(x => new OrderItemDto
                            {
                                PlanItemId = x.row.Src.PlanItemId,
                                LotId = x.row.Src.LotId,
                                CustomerId = x.row.Src.CustomerId,
                                ProductId = x.row.Src.ProductId,
                                PackagingTypeId = x.row.Src.PackagingTypeId,
                                PlannedQtyKg = x.kg,
                                PlannedCartons = x.cartons
                            }).ToList());
                        if (!res.Ok) { AppContainer.Get<DialogService>().Error(res.Message, "إنشاء أمر الإنتاج (توزيع)"); return; }
                        created.Add($"{res.DocumentNumber} ({_shifts.First(s => s.id == shId).name})");
                        CreatedOrderIds.Add(res.Id);
                    OrderCreated?.Invoke(res.Id);
                    }
                    foreach (var p in pending.Where(p => p.RemainingCartons > 0))
                        AppContainer.Get<DialogService>().Error(
                            $"⚠ لم تتسع ورديات يوم {date} لكل كمية «{p.Row.ProductName}» — المتبقي بدون أمر: {p.RemainingCartons} كرتون.\nأضف يوماً آخر أو قلّل الكمية.");
                }
            }

            if (created.Count > 0)
            {
                AppContainer.Get<DialogService>().Info(
                    $"✅ تم إنشاء {created.Count} أمر إنتاج:\n• " + string.Join("\n• ", created) +
                    "\n\nالخطوة التالية: اعتماد الأمر (صرف المواد المساعدة) — والخام يُصرف فعلياً عند إقفال يوم الإنتاج.", "أمر إنتاج جديد");
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "NewOrder.Create"); }
    }
}

    // §حُذف غلافَا NewOrderWindow وProductionOrderWindow في B40: لم ينشئهما أي كود —
    // OrdersView يستضيف NewOrderPanel وOrderDocumentPanel مباشرة داخل الشاشة.

/// <summary>§B80 — صف بند أمر قابل للتعديل (مسودة): الكراتين تُعدَّل والوزن المكافئ يُحسب في الخدمة.</summary>
public class OrderItemEditRow
{
    public int Id { get; set; }
    public string Customer { get; set; }
    public string Shipment { get; set; }
    public string Lot { get; set; }
    public string Raw { get; set; }
    public string Product { get; set; }
    public string Pack { get; set; }
    public double PlannedKg { get; set; }
    public int PlannedCartons { get; set; }
    /// <summary>الكراتين القابلة للتعديل — تبدأ بالمخطط.</summary>
    public int Cartons { get; set; }
    public double ProducedKg { get; set; }
    public string StatusAr { get; set; }
}

public class OrderDocumentPanel : UserControl
{
    private readonly int _orderId;
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) };
    private readonly WrapPanel _cardPanel = new();
    private readonly ProgressBar _progress = new() { Height = 20, Maximum = 100 };
    private readonly TextBlock _progressText = new() { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
    private readonly DataGrid _itemsGrid = new() { AutoGenerateColumns = false, IsReadOnly = false, Height = 150, RowHeight = 28 };
    private DataGridTextColumn _cartonsEditCol;
    private List<OrderItemEditRow> _itemRows = new();
    private readonly DataGrid _materialsGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, Height = 110, RowHeight = 26 };
    private readonly DataGrid _eventsGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, Height = 160, RowHeight = 26 };
    private readonly DatePicker _date = new() { Width = 140 };
    private readonly ComboBox _shiftBox = new() { Width = 190, MinHeight = 26 };
    private readonly ComboBox _lineBox = new() { Width = 160, MinHeight = 26 };
    private readonly TextBox _notesBox = new() { Width = 300, MinHeight = 26 };
    private readonly StackPanel _actionsPanel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    private List<(int id, string name)> _shifts = new();
    private List<(int id, string name)> _lines = new();
    private OrderCardDto _card;

    public OrderDocumentPanel(int orderId)
    {
        _orderId = orderId;
        FlowDirection = FlowDirection.RightToLeft;

        _itemsGrid.Columns.Add(NewOrderPanel.IdentCol("العميل المالك 👤", "Customer", 1.2, 140));
        _itemsGrid.Columns.Add(NewOrderPanel.IdentCol("الشحنة 🚢", "Shipment", 0.9, 110));
        _itemsGrid.Columns.Add(NewOrderPanel.IdentCol("الدفعة 📦", "Lot", 0.9, 110));
        _itemsGrid.Columns.Add(NewOrderPanel.IdentCol("الصنف الخام المستلم 🌴", "Raw", 1.3, 160));
        _itemsGrid.Columns.Add(NewOrderPanel.IdentCol("المنتج النهائي 🏷️", "Product", 1.3, 160));
        _itemsGrid.Columns.Add(NewOrderPanel.IdentCol("العبوة 📦", "Pack", 0.9, 110));
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "المخطط (كجم)", Binding = new System.Windows.Data.Binding("PlannedKg"), Width = 90, IsReadOnly = true });
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "المخطط (كرتون)", Binding = new System.Windows.Data.Binding("PlannedCartons"), Width = 95, IsReadOnly = true });
        // §B80: عمود تعديل الكراتين — يُفتح لأمر مسودة لم يبدأ تنفيذه
        _cartonsEditCol = new DataGridTextColumn { Header = "كراتين الأمر ✏️", Binding = new System.Windows.Data.Binding("Cartons") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus }, Width = 110, IsReadOnly = true };
        _itemsGrid.Columns.Add(_cartonsEditCol);
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "المنتَج (كجم)", Binding = new System.Windows.Data.Binding("ProducedKg"), Width = 90, IsReadOnly = true });
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("StatusAr"), Width = 80, IsReadOnly = true });

        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المادة المساعدة", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المحتسبة", Binding = new System.Windows.Data.Binding("Calculated"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المصروفة", Binding = new System.Windows.Data.Binding("Issued"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المستهلكة", Binding = new System.Windows.Data.Binding("Consumed"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "الوحدة", Binding = new System.Windows.Data.Binding("Unit"), Width = 80 });

        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "الوقت", Binding = new System.Windows.Data.Binding("Time"), Width = 130 });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "المستخدم", Binding = new System.Windows.Data.Binding("User"), Width = 140 });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "العملية", Binding = new System.Windows.Data.Binding("Action"), Width = 200 });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "التفاصيل", Binding = new System.Windows.Data.Binding("Detail"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var editRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        editRow.Children.Add(Lbl("بيانات التنفيذ (قبل بدء الإنتاج فقط):"));
        editRow.Children.Add(_date);
        editRow.Children.Add(Lbl("الوردية:")); editRow.Children.Add(_shiftBox);
        editRow.Children.Add(Lbl("الخط:")); editRow.Children.Add(_lineBox);
        editRow.Children.Add(Lbl("ملاحظات:")); editRow.Children.Add(_notesBox);
        var saveEditBtn = new Button { Content = "💾 حفظ التعديل", Margin = new Thickness(8, 0, 0, 0) };
        saveEditBtn.Click += (_, _) => SaveHeaderEdit();
        editRow.Children.Add(saveEditBtn);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(_title);
        panel.Children.Add(_cardPanel);
        panel.Children.Add(_progress);
        panel.Children.Add(_progressText);
        panel.Children.Add(Section("📦 بنود الأمر — الهوية كاملة: العميل/الشحنة/الدفعة/الصنف المستلم/المنتج النهائي", _itemsGrid));
        panel.Children.Add(Section("🧰 المواد المساعدة المحتسبة", _materialsGrid));
        panel.Children.Add(editRow);
        panel.Children.Add(_actionsPanel);
        panel.Children.Add(Section("📋 سجل العمليات — من فعل ماذا ومتى", _eventsGrid));
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Loaded += (_, _) => Refresh();
    }

    private static TextBlock Lbl(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), FontSize = 11.5 };

    private static Border Section(string header, UIElement body)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)), Margin = new Thickness(0, 8, 0, 4) });
        sp.Children.Add(body);
        return new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xC8, 0xB0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 0), Background = Brushes.White };
    }

    private bool Can(string module, string action)
    {
        try { return AppContainer.Get<SessionContext>().Can(module, action); }
        catch { return false; }
    }

    private void Refresh()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

            _card = svc.GetOrderCard(_orderId);
            if (_card == null)
            {
                _title.Text = "أمر الإنتاج غير موجود.";
                return;
            }

            _title.Text = $"📝 أمر الإنتاج {_card.OrderNumber} — {_card.StatusAr}";
            _cardPanel.Children.Clear();
            AddCard("العميل", _card.CustomerName);
            AddCard("الصنف المستلم", _card.RawName);
            AddCard("المنتج النهائي", _card.ProductName);
            AddCard("الخطة", _card.PlanNumber);
            AddCard("الدفعة", _card.LotCode);
            AddCard("الشحنة", _card.ShipmentNumber);
            AddCard("التاريخ", _card.ProductionDate);
            AddCard("الوردية", _card.ShiftName);
            AddCard("الخط", _card.LineName);
            AddCard("وقت البداية", _card.StartTime);
            AddCard("النهاية المتوقع", _card.ExpectedEndTime);
            AddCard("المخطط في الخطة", $"{_card.PlannedInPlanKg:N1} كجم / {_card.PlannedInPlanCartons:N0} كرتون");
            AddCard("كمية الأمر", $"{_card.OrderedKg:N1} كجم / {_card.OrderedCartons:N0} كرتون");
            AddCard("الإنتاج الفعلي", $"{_card.ProducedKg:N1} كجم / {_card.ProducedCartons:N0} كرتون");
            AddCard("المقبول/المرفوض", $"{_card.AcceptedKg:N1} / {_card.RejectedKg:N1} كجم");
            AddCard("المتبقي", $"{_card.RemainingKg:N1} كجم");
            AddCard("المعدل", $"{_card.RatePerHour:N0} كرتون/س — {_card.ExpectedHours:N1} س متوقعة");
            AddCard("أنشأه", $"{_card.CreatedBy} — {_card.CreatedDate}");

            _progress.Value = _card.ProgressPct;
            _progressText.Text = $"شريط التقدم: {_card.ProducedKg:N1} / {_card.OrderedKg:N1} كجم — {_card.ProgressPct:N0}%";

            // البنود بالهوية الكاملة — §B80 صفوف قابلة لتعديل الكراتين لأمر المسودة
            var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == _orderId).ToList();
            bool hasExec = db.ProductionExecutions.AsNoTracking().Any(e => e.OrderId == _orderId);
            bool itemsEditable = _card.Status == DocStatuses.Draft && !hasExec && Can("production", "Edit");
            _itemRows = items.Select(it => new OrderItemEditRow
            {
                Id = it.Id,
                Customer = it.CustomerId != null ? db.Customers.AsNoTracking().Where(c => c.Id == it.CustomerId).Select(c => c.CustomerName).FirstOrDefault() : "-",
                Shipment = db.Lots.AsNoTracking().Where(l => l.Id == it.ShipmentId).Select(l => l.ShipmentId).FirstOrDefault() != null
                    ? db.Shipments.AsNoTracking().Where(s => s.Id == it.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() : "-",
                Lot = db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "-",
                Raw = db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => p.ProductNameAr).FirstOrDefault() ?? "-",
                Product = db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                Pack = it.PackagingTypeId != null ? db.PackagingTypes.AsNoTracking().Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() : "-",
                PlannedKg = it.PlannedQtyKg,
                PlannedCartons = it.PlannedCartons,
                Cartons = it.PlannedCartons,
                ProducedKg = it.ProducedQtyKg,
                StatusAr = DocStatuses.ToArabic(it.Status)
            }).ToList();
            _itemsGrid.ItemsSource = _itemRows;
            if (_cartonsEditCol != null) _cartonsEditCol.IsReadOnly = !itemsEditable;

            _materialsGrid.ItemsSource = db.ProductionOrderMaterials.AsNoTracking().Where(m => m.OrderId == _orderId).ToList()
                .Select(m => new
                {
                    Name = db.AuxiliaryMaterials.AsNoTracking().Where(a => a.Id == m.MaterialId).Select(a => a.MaterialNameAr).FirstOrDefault(),
                    Calculated = m.CalculatedQty,
                    Issued = m.ActualIssuedQty,
                    Consumed = m.ConsumedQty,
                    Unit = m.UnitOfMeasure
                }).ToList();

            _eventsGrid.ItemsSource = svc.GetOrderEvents(_orderId);

            // بيانات التنفيذ القابلة للتعديل
            _shifts = db.Shifts.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.ShiftNameAr }).ToList().Select(x => (x.Id, x.ShiftNameAr)).ToList();
            _lines = db.ProductionLines.AsNoTracking().OrderBy(l => l.Id).Select(l => new { l.Id, l.LineNameAr }).ToList().Select(x => (x.Id, x.LineNameAr)).ToList();
            _shiftBox.Items.Clear();
            foreach (var s in _shifts) _shiftBox.Items.Add(s.name);
            _lineBox.Items.Clear();
            foreach (var l in _lines) _lineBox.Items.Add(l.name);
            var order = db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == _orderId);
            if (order != null)
            {
                _date.SelectedDate = order.ProductionDate;
                int si = _shifts.FindIndex(s => s.id == (order.ShiftId ?? 0)); if (si >= 0) _shiftBox.SelectedIndex = si;
                int li = _lines.FindIndex(l => l.id == (order.LineId ?? 0)); if (li >= 0) _lineBox.SelectedIndex = li;
                _notesBox.Text = order.Notes ?? "";
            }

            BuildActions();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderWindow.Refresh"); }
    }

    private void AddCard(string label, string value)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF2, 0xE8)),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 4, 6, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD0, 0xBB)), BorderThickness = new Thickness(1)
        };
        b.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label, FontSize = 10, Foreground = Brushes.Gray },
                new TextBlock { Text = value ?? "-", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) }
            }
        };
        _cardPanel.Children.Add(b);
    }

    private void BuildActions()
    {
        _actionsPanel.Children.Clear();
        string st = _card.Status;
        Button Btn(string text, string style, Action act, bool enabled)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0), IsEnabled = enabled };
            b.Style = (Style)System.Windows.Application.Current.FindResource(style);
            b.Click += (_, _) => { act(); };
            return b;
        }

        if (st == DocStatuses.Draft)
            _actionsPanel.Children.Add(Btn("🔒 اعتماد الأمر (صرف المواد المساعدة)", "ErpApproveButton", () => Do(s => s.ApproveOrder(_orderId)), Can("production", "Approve")));
        if (st is DocStatuses.Approved or DocStatuses.Scheduled)
        {
            _actionsPanel.Children.Add(Btn("🏭 بدء الإنتاج", "ErpApproveButton", () => Do(s => s.StartOrder(_orderId)), Can("execution", "Create")));
            _actionsPanel.Children.Add(Btn("↩ إلغاء الاعتماد", "ErpButton", () => Do(s => s.UnapproveOrder(_orderId)), Can("production", "Cancel")));
        }
        if (st == DocStatuses.InProgress)
        {
            _actionsPanel.Children.Add(Btn("⏸ إيقاف مؤقت", "ErpDangerButton", () => Do(s => s.StopOrder(_orderId, null)), Can("execution", "Edit")));
            _actionsPanel.Children.Add(Btn("🔒 إقفال يوم الإنتاج", "ErpPrimaryButton", CloseDay, Can("execution", "Edit")));
        }
        if (st == DocStatuses.Stopped)
            _actionsPanel.Children.Add(Btn("▶ استئناف الإنتاج", "ErpApproveButton", () => Do(s => s.ResumeOrder(_orderId)), Can("execution", "Edit")));
        if (st is DocStatuses.Draft or DocStatuses.Approved or DocStatuses.Scheduled)
            _actionsPanel.Children.Add(Btn("✖ إلغاء الأمر", "ErpDangerButton", CancelWithReason, Can("production", "Cancel")));
        if (st is DocStatuses.Completed or DocStatuses.InProgress)
            _actionsPanel.Children.Add(Btn("🔒 إغلاق الأمر", "ErpButton", () => CloseOrderWithReason(), Can("production", "Cancel")));

        _actionsPanel.Children.Add(Btn("🖨 طباعة", "ErpButton", Print, true));
        _actionsPanel.Children.Add(Btn("📄 PDF", "ErpButton", Pdf, true));

        bool editable = st is DocStatuses.Draft or DocStatuses.Approved or DocStatuses.Scheduled && Can("production", "Edit");
        _date.IsEnabled = editable; _shiftBox.IsEnabled = editable; _lineBox.IsEnabled = editable; _notesBox.IsEnabled = editable;
    }

    private void Do(Func<IProductionOrderService, OpResult> op)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var res = op(scope.ServiceProvider.GetRequiredService<IProductionOrderService>());
            if (!res.Ok) AppContainer.Get<DialogService>().Error(res.Message);
            else AppContainer.Get<DialogService>().Info(res.Message);
            Refresh();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderWindow.Action"); }
    }

    private void SaveHeaderEdit()
    {
        try
        {
            _itemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var res = svc.UpdateOrderHeader(_orderId,
                _date.SelectedDate?.ToString("dd/MM/yyyy"),
                _shiftBox.SelectedIndex >= 0 ? _shifts[_shiftBox.SelectedIndex].id : null,
                _lineBox.SelectedIndex >= 0 ? _lines[_lineBox.SelectedIndex].id : null,
                _notesBox.Text);
            if (!res.Ok) { AppContainer.Get<DialogService>().Error(res.Message); return; }
            // §B80: حفظ تعديل كميات البنود (مسودة فقط — الحرس في الخدمة)
            if (_cartonsEditCol != null && !_cartonsEditCol.IsReadOnly && _itemRows.Count > 0)
            {
                var changed = _itemRows.Where(r => r.Cartons != r.PlannedCartons).ToList();
                if (changed.Count > 0)
                {
                    var resItems = svc.UpdateOrderItems(_orderId, changed.Select(r => new OrderItemDto
                    {
                        Id = r.Id,
                        ProductId = 0,
                        PlannedQtyKg = 0,               // الكيلو المكافئ يُشتق من وزن الكرتون في الخدمة
                        PlannedCartons = r.Cartons
                    }).ToList());
                    if (!resItems.Ok) { AppContainer.Get<DialogService>().Error(resItems.Message); Refresh(); return; }
                    AppContainer.Get<DialogService>().Info(res.Message + "\n" + resItems.Message);
                    Refresh();
                    return;
                }
            }
            AppContainer.Get<DialogService>().Info(res.Message);
            Refresh();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderWindow.Edit"); }
    }

    private void CancelWithReason()
    {
        if (!AppContainer.Get<DialogService>().Confirm("إلغاء أمر الإنتاج؟ إن كان معتمداً سيُعكس الصرف ويعود المتبقي للخطة.")) return;
        var dlg = new InputDialog("إلغاء أمر الإنتاج", "سبب الإلغاء (اختياري):");
        string reason = dlg.ShowDialog() == true ? dlg.Value : "";
        Do(s => s.CancelOrder(_orderId, reason));
    }

    /// <summary>§B95 — إغلاق الأمر: السبب فارغ عند الاكتمال وإجباري عند العجز (تسوية موثقة تُحفظ في الأمر).</summary>
    private void CloseOrderWithReason()
    {
        var dlg = new InputDialog("إغلاق أمر الإنتاج", "سبب الإغلاق (فارغ عند اكتمال الإنتاج — إجباري عند وجود عجز):");
        if (dlg.ShowDialog() != true) return;
        Do(s => s.CloseOrder(_orderId, dlg.Value));
    }

    private void CloseDay()
    {
        var dlg = new CloseDayDialog(_orderId, _card.OrderNumber, _card.RemainingKg) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IExecutionService>();
            var res = svc.CloseProductionDay(_orderId, dlg.ProducedKg, dlg.ProducedCartons, dlg.HashfKg, dlg.NawaKg, dlg.WastageKg,
                dlg.CarryToNextDay, dlg.Downtimes, dlg.SendToQuality, dlg.Notes, dlg.ByProducts, consumedRawKg: dlg.ConsumedKg, itemQtys: dlg.ItemQtys,
                actualAux: dlg.ActualAux, emptyCartonsActual: dlg.EmptyCartonsActual);
            if (!res.Ok) AppContainer.Get<DialogService>().Error(res.Message, "إقفال يوم الإنتاج");
            else AppContainer.Get<DialogService>().Info(res.Message, "إقفال يوم الإنتاج");
            Refresh();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderWindow.CloseDay"); }
    }

    private ReportResult BuildReport()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == _orderId).ToList();
        var report = new ReportResult
        {
            TitleAr = $"أمر إنتاج رقم: {_card.OrderNumber}",
            Columns = new List<string> { "العميل", "الدفعة", "الصنف المستلم", "المنتج النهائي", "المخطط (كجم)", "الكراتين", "المنتَج (كجم)" }
        };
        foreach (var it in items)
            report.Rows.Add(new object[]
            {
                it.CustomerId != null ? db.Customers.AsNoTracking().Where(c => c.Id == it.CustomerId).Select(c => c.CustomerName).FirstOrDefault() : "-",
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "-",
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => p.ProductNameAr).FirstOrDefault() ?? "-",
                db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                it.PlannedQtyKg, it.PlannedCartons, it.ProducedQtyKg
            });
        report.Summary["الخطة"] = _card.PlanNumber;
        report.Summary["العميل"] = _card.CustomerName;
        report.Summary["التاريخ والوردية"] = $"{_card.ProductionDate} — {_card.ShiftName}";
        report.Summary["وقت البداية / النهاية المتوقع"] = $"{_card.StartTime} / {_card.ExpectedEndTime}";
        report.Summary["الحالة"] = _card.StatusAr;
        report.Summary["توقيع مدير الإنتاج"] = "____________________";
        report.Summary["توقيع مشرف الوردية"] = "____________________";
        report.Summary["توقيع مسؤول الجودة"] = "____________________";
        return report;
    }

    private void Print() => AppContainer.Get<ExportPrintService>().Print(BuildReport());
    private void Pdf() => AppContainer.Get<ExportPrintService>().ExportPdf(BuildReport());
}

    // §حُذف غلافَا NewOrderWindow وProductionOrderWindow في B40: لم ينشئهما أي كود —
    // OrdersView يستضيف NewOrderPanel وOrderDocumentPanel مباشرة داخل الشاشة.

public class CloseDayDialog : Window
{
    public double ProducedKg { get; private set; }
    public int ProducedCartons { get; private set; }
    public double HashfKg { get; private set; }
    public double NawaKg { get; private set; }
    public double WastageKg { get; private set; }
    public double ConsumedKg { get; private set; } // §B85/H2: الخام المستهلك فعلياً — يُدخله المستخدم بدل التقدير
    /// <summary>§المخرجات الثانوية بأصنافها المعرَّفة — تُمرَّر إلى CloseProductionDay.</summary>
    public List<ByProductQtyDto> ByProducts { get; private set; }
    /// <summary>§B88/M10: التوقفات المدخلة (ساعات/سبب/بداية/نهاية) — كانت تُمرَّر فارغة دائماً.</summary>
    public List<DowntimeDto> Downtimes { get; private set; } = new();
    /// <summary>§B88/M13: إنتاج كل بند (كجم + كراتين) — يُفحص كل بند بهوية صنفه وعبوته.</summary>
    public List<CloseItemQtyDto> ItemQtys { get; private set; }
    /// <summary>§B95 — الاستهلاك الفعلي للمواد المساعدة (تسوية تلقائية عند الإقفال: الفرق مرتجع/صرف آلي).</summary>
    public List<AuxActualDto> ActualAux { get; private set; }
    /// <summary>§B95 — الكرتون الفارغ الفعلي المؤكد — فارغ = تقدير النظام من الخام المصروف.</summary>
    public double? EmptyCartonsActual { get; private set; }
    public bool CarryToNextDay { get; private set; }
    public bool SendToQuality { get; private set; }
    public string Notes { get; private set; }

    /// <summary>§B88/M13: صف بند أمر — يُحمَّل من القاعدة مع أسماء الصنف/العبوة والمتبقي.</summary>
    private sealed class CloseItemRow
    {
        public int OrderItemId;
        public string Title;
        public readonly TextBox KgBox = new() { Width = 90 };
        public readonly TextBox BoxBox = new() { Width = 70 };
    }
    private readonly List<CloseItemRow> _itemRows = new();

    /// <summary>§B88/M10: صف توقف قابل للتحرير في الشبكة.</summary>
    public class DowntimeRowUi
    {
        public string HoursText { get; set; } = "";
        public string ReasonAr { get; set; } = "";
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
    }
    private readonly ObservableCollection<DowntimeRowUi> _downRows = new();
    private readonly DataGrid _downGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, Height = 120 };

    private readonly TextBox _consumed = new() { Width = 120 };
    private readonly TextBox _produced = new() { Width = 120 };
    private readonly TextBox _cartons = new() { Width = 120 };
    private readonly TextBox _waste = new() { Width = 120, Text = "0" };
    private readonly TextBox _emptyCartons = new() { Width = 120 };
    /// <summary>§B95 — صف مادة مساعدة: المصروف للعرض + الفعلي المستهلك للإدخال (فارغ = بالمعادلة فقط).</summary>
    public class AuxRowUi
    {
        public int MaterialId { get; set; }
        public string MaterialName { get; set; } = "";
        public string IssuedText { get; set; } = "";
        public string ActualText { get; set; } = "";
    }
    /// <summary>§B95 — خيار منتقي المواد (فئة بخصائص — الـ ValueTuple حقول لا يقبلها ربط WPF).</summary>
    private sealed class AuxOpt { public int Id { get; set; } public string Name { get; set; } = ""; }
    private readonly ObservableCollection<AuxRowUi> _auxRows = new();
    private readonly DataGrid _auxGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, Height = 110 };
    private readonly ComboBox _auxPicker = new() { Width = 260 };
    private readonly int _orderId;
    /// <summary>§لا ثوابت: حقل لكل مخرج ثانوي معرَّف في إعدادات الأصناف.</summary>
    private readonly List<(int id, string name, TextBox box)> _byBoxes = new();
    private readonly CheckBox _carry = new() { Content = "إعادة المتبقي في الصالة لخام دفعته (يُعاد تخطيطه يدوياً)", FontSize = 12 };
    private readonly CheckBox _quality = new() { Content = "إرسال للفحص (النتيجة متوقعة بعد يومَي تبريد)", FontSize = 12, IsChecked = true };
    private readonly TextBox _notes = new() { Width = 380 };

    public CloseDayDialog(int orderId, string orderNumber, double remainingKg)
    {
        _orderId = orderId;
        Title = $"🔒 إقفال يوم الإنتاج — الأمر {orderNumber}";
        // §B84/K1+L5: Enter يقفل وEscape يلغي + حد أدنى للعرض.
        SizeToContent = SizeToContent.Height; Width = 680; MinWidth = 560;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        _produced.Text = remainingKg > 0 ? remainingKg.ToString("N1") : "0";
        _consumed.Text = remainingKg > 0 ? remainingKg.ToString("N1") : "0"; // §B85/H2: الافتراضي = المخطط — عدّله بالفعلي

        LoadOrderItems(orderId);
        LoadOrderMaterials(orderId);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "سجل ما دخل الصالة (الخام المستهلك فعلياً) وما خرج منها: إنتاج كل بند (بالكراتين ووزنها) والمخرجات الثانوية بالكيلو، ثم التوقفات إن وُجدت.",
            FontSize = 11.5, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(Field("الخام المستهلك فعلياً (كجم):", _consumed));

        if (_itemRows.Count > 0)
        {
            // §B88/M13: إنتاج كل بند على حدة — المعبأ مسبقاً = المتبقي، عدّله بالفعلي
            panel.Children.Add(new TextBlock { Text = "إنتاج البنود (كل بند بصنفه وعبوته — الإجمالي = المجموع):", FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 2, 0, 6) });
            foreach (var r in _itemRows)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                sp.Children.Add(new TextBlock { Text = r.Title, Width = 360, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
                sp.Children.Add(new TextBlock { Text = "كجم:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) });
                sp.Children.Add(r.KgBox);
                sp.Children.Add(new TextBlock { Text = "كرتون:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) });
                sp.Children.Add(r.BoxBox);
                panel.Children.Add(sp);
            }
        }
        else
        {
            panel.Children.Add(Field("الكمية المنتجة (كجم):", _produced));
            panel.Children.Add(Field("عدد الكراتين المنتجة:", _cartons));
        }

        // §المخرجات الثانوية من جدول ByProducts — تُضاف من إعدادات الأصناف فتظهر هنا تلقائياً
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            foreach (var b in db.ByProducts.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id).ToList())
            {
                var box = new TextBox { Width = 120, Text = "0" };
                _byBoxes.Add((b.Id, b.ByProductNameAr, box));
                panel.Children.Add(Field($"{b.ByProductNameAr} ({b.UnitOfMeasure}):", box));
            }
        }
        catch { /* تعذّر جلب البطاقة — يبقى المنتَج والفاقد فقط */ }
        if (_byBoxes.Count == 0)
            panel.Children.Add(new TextBlock
            {
                Text = "لا مخرجات ثانوية معرَّفة — أضفها من «إعدادات الأصناف ← إدارة المخرجات الثانوية».",
                FontSize = 11, Foreground = Brushes.DarkOrange, Margin = new Thickness(0, 0, 0, 6)
            });
        panel.Children.Add(Field("الفاقد/الهالك (كجم):", _waste));

        // §B88/M10: شبكة التوقفات — ساعات/سبب/بداية/نهاية (كانت ميتة من الواجهة)
        panel.Children.Add(new TextBlock { Text = "التوقفات (اختياري — سطر لكل توقف):", FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) });
        _downGrid.Columns.Add(new DataGridTextColumn { Header = "الساعات", Binding = new System.Windows.Data.Binding("HoursText"), Width = 70 });
        _downGrid.Columns.Add(new DataGridTextColumn { Header = "السبب *", Binding = new System.Windows.Data.Binding("ReasonAr"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _downGrid.Columns.Add(new DataGridTextColumn { Header = "من (HH:mm)", Binding = new System.Windows.Data.Binding("StartTime"), Width = 95 });
        _downGrid.Columns.Add(new DataGridTextColumn { Header = "إلى (HH:mm)", Binding = new System.Windows.Data.Binding("EndTime"), Width = 95 });
        _downGrid.ItemsSource = _downRows;
        panel.Children.Add(_downGrid);
        var downBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        var addBtn = new Button { Content = "＋ إضافة توقف", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        addBtn.Click += (_, _) => _downRows.Add(new DowntimeRowUi());
        var delBtn = new Button { Content = "－ حذف المحدد", Padding = new Thickness(10, 3, 10, 3) };
        delBtn.Click += (_, _) => { if (_downGrid.SelectedItem is DowntimeRowUi sel) _downRows.Remove(sel); };
        downBtns.Children.Add(addBtn); downBtns.Children.Add(delBtn);
        panel.Children.Add(downBtns);

        // §B95 — تسوية المواد المساعدة: أدخل الفعلي المستهلك لكل مادة (يُترك فارغاً للاكتفاء بالمعادلة) —
        // الفرق عن المصروف يُرتجع/يُصرف آلياً عند الإقفال. زر «＋» يضيف مادة غير مصروفة (ديزل/وقود).
        panel.Children.Add(new TextBlock { Text = "المواد المساعدة — الفعلي المستهلك (اختياري — التسوية آلية):", FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) });
        _auxGrid.Columns.Add(new DataGridTextColumn { Header = "المادة", Binding = new System.Windows.Data.Binding("MaterialName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        _auxGrid.Columns.Add(new DataGridTextColumn { Header = "المصروف", Binding = new System.Windows.Data.Binding("IssuedText"), Width = 90, IsReadOnly = true });
        _auxGrid.Columns.Add(new DataGridTextColumn { Header = "الفعلي المستهلك", Binding = new System.Windows.Data.Binding("ActualText"), Width = 110 });
        _auxGrid.ItemsSource = _auxRows;
        panel.Children.Add(_auxGrid);
        var auxBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        auxBtns.Children.Add(_auxPicker);
        var auxAdd = new Button { Content = "＋ إضافة مادة", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 6, 0) };
        auxAdd.Click += (_, _) => AuxAddPicked();
        var auxDel = new Button { Content = "－ حذف المحدد", Padding = new Thickness(10, 3, 10, 3) };
        auxDel.Click += (_, _) => { if (_auxGrid.SelectedItem is AuxRowUi sel) _auxRows.Remove(sel); };
        auxBtns.Children.Add(auxAdd); auxBtns.Children.Add(auxDel);
        panel.Children.Add(auxBtns);
        panel.Children.Add(Field("كرتون فارغ فعلي (يُترك فارغاً للتقدير الآلي):", _emptyCartons));

        panel.Children.Add(_carry);
        panel.Children.Add(_quality);
        panel.Children.Add(Field("ملاحظات:", _notes));

        var ok = new Button { Content = "🔒 إقفال يوم الإنتاج", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 10, 6, 0), IsDefault = true };
        ok.Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton");
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "إلغاء", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        panel.Children.Add(btns);

        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = scroll;
    }

    /// <summary>§B88/M13: تحميل بنود الأمر مع أسماء الصنف/العبوة — الفشل الصامت = المسار الإجمالي القديم.</summary>
    private void LoadOrderItems(int orderId)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == orderId).OrderBy(i => i.Id).ToList();
            var prodNames = db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
            var packNames = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);
            foreach (var i in items)
            {
                string pn = prodNames.TryGetValue(i.ProductId, out var x) ? x : $"صنف #{i.ProductId}";
                string pk = i.PackagingTypeId != null && packNames.TryGetValue(i.PackagingTypeId.Value, out var y) ? y : "—";
                double remKg = Math.Max(0, i.PlannedQtyKg - i.ProducedQtyKg);
                int remBox = Math.Max(0, i.PlannedCartons - i.ProducedCartons);
                var row = new CloseItemRow
                {
                    OrderItemId = i.Id,
                    Title = $"{pn} — {pk} | مخطط {i.PlannedQtyKg:N1} كجم ({i.PlannedCartons:N0} كرتون) — متبقي {remKg:N1} / {remBox:N0}"
                };
                row.KgBox.Text = remKg.ToString("N1");
                row.BoxBox.Text = remBox.ToString();
                _itemRows.Add(row);
            }
        }
        catch { /* تعذّر التحميل — المسار الإجمالي القديم */ }
    }

    /// <summary>§B95 — تحميل مواد الأمر (المصروف للعرض) + منتقي المواد لإضافة غير المصروفة.</summary>
    private void LoadOrderMaterials(int orderId)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var names = db.AuxiliaryMaterials.AsNoTracking().ToDictionary(m => m.Id, m => $"{m.MaterialCode} · {m.MaterialNameAr} ({m.UnitOfMeasure ?? "—"})");
            foreach (var m in db.ProductionOrderMaterials.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.Id).ToList())
                _auxRows.Add(new AuxRowUi
                {
                    MaterialId = m.MaterialId,
                    MaterialName = names.TryGetValue(m.MaterialId, out var n) ? n : $"مادة #{m.MaterialId}",
                    IssuedText = m.ActualIssuedQty.ToString("N1")
                });
            var picked = new HashSet<int>(_auxRows.Select(r => r.MaterialId));
            var opts = new List<AuxOpt> { new() { Id = 0, Name = "— اختر مادة —" } };
            foreach (var m in db.AuxiliaryMaterials.AsNoTracking().OrderBy(x => x.MaterialCode).ToList())
                if (!picked.Contains(m.Id)) opts.Add(new AuxOpt { Id = m.Id, Name = $"{m.MaterialCode} · {m.MaterialNameAr} ({m.UnitOfMeasure ?? "—"})" });
            _auxPicker.ItemsSource = opts;
            _auxPicker.DisplayMemberPath = "Name";
            _auxPicker.SelectedValuePath = "Id";
            _auxPicker.SelectedIndex = 0;
        }
        catch { /* تعذّر التحميل — تُترك التسوية للمعادلة */ }
    }

    private void AuxAddPicked()
    {
        if (_auxPicker.SelectedValue is not int id || id <= 0) return;
        if (_auxRows.Any(r => r.MaterialId == id)) return;
        string name = _auxPicker.SelectedItem is AuxOpt o ? o.Name : $"مادة #{id}";
        _auxRows.Add(new AuxRowUi { MaterialId = id, MaterialName = name, IssuedText = "0" });
    }

    private static StackPanel Field(string label, UIElement input)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(new TextBlock { Text = label, Width = 170, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        sp.Children.Add(input);
        return sp;
    }

    private void Accept()
    {
        double.TryParse(_consumed.Text, out var ck);
        double.TryParse(_waste.Text, out var w);

        double pk = 0; int pc = 0;
        if (_itemRows.Count > 0)
        {
            // §B88/M13: تجميع البنود — كل بند يُقرأ ويُجمَع، والخدمة تفحص وتكتب لكل بند
            var qtys = new List<CloseItemQtyDto>();
            foreach (var r in _itemRows)
            {
                if (!double.TryParse(r.KgBox.Text, out var kg) || !int.TryParse(r.BoxBox.Text, out var bx))
                { AppContainer.Get<DialogService>().Error($"كمية البند «{r.Title}» غير صالحة — أدخل أرقاماً."); return; }
                if (kg < 0 || bx < 0)
                { AppContainer.Get<DialogService>().Error("كميات البنود لا يمكن أن تكون سالبة."); return; }
                qtys.Add(new CloseItemQtyDto { OrderItemId = r.OrderItemId, ProducedKg = kg, ProducedCartons = bx });
                pk += kg; pc += bx;
            }
            ItemQtys = qtys;
        }
        else
        {
            double.TryParse(_produced.Text, out pk);
            int.TryParse(_cartons.Text, out pc);
            ItemQtys = null;
        }
        if (pk < 0 || w < 0 || pc < 0 || ck < 0)
        { AppContainer.Get<DialogService>().Error("الكميات لا يمكن أن تكون سالبة."); return; }

        // §جمع المخرجات الديناميكية + مطابقة الأسماء القديمة بالأعمدة السابقة للتوافق
        ByProducts = new List<ByProductQtyDto>();
        double byTotal = 0, h = 0, n = 0;
        foreach (var (id, name, box) in _byBoxes)
        {
            if (!double.TryParse(box.Text, out var v) || v < 0)
            { AppContainer.Get<DialogService>().Error($"كمية «{name}» غير صالحة أو سالبة."); return; }
            if (v <= 0) continue;
            ByProducts.Add(new ByProductQtyDto { ByProductId = id, QtyKg = v });
            byTotal += v;
            string nm = name ?? "";
            if (nm.Contains("حشف")) h += v; else if (nm.Contains("نوى")) n += v;
        }
        HashfKg = h; NawaKg = n;

        if (pk <= 0 && pc <= 0 && byTotal + w <= 0)
        { AppContainer.Get<DialogService>().Error("أدخل كمية منتجة أو مخرجات ثانوية."); return; }

        // §B88/M10: جمع التوقفات — الساعات بلا سبب مرفوضة، والوقت بصيغة HH:mm
        var downs = new List<DowntimeDto>();
        foreach (var d in _downRows)
        {
            string hrs = (d.HoursText ?? "").Trim(), rsn = (d.ReasonAr ?? "").Trim();
            string st = (d.StartTime ?? "").Trim(), en = (d.EndTime ?? "").Trim();
            if (hrs == "" && rsn == "" && st == "" && en == "") continue; // سطر فارغ
            if (!double.TryParse(hrs, out var hh) || hh <= 0)
            { AppContainer.Get<DialogService>().Error("ساعات التوقف يجب أن تكون رقماً أكبر من صفر — أو اترك السطر فارغاً."); return; }
            if (rsn == "")
            { AppContainer.Get<DialogService>().Error("أدخل سبب التوقف — التوقف بلا سبب لا يُقبل."); return; }
            if (st != "" && !TimeSpan.TryParse(st, out _))
            { AppContainer.Get<DialogService>().Error($"بداية التوقف «{st}» غير صالحة — الصيغة HH:mm (مثال 14:30)."); return; }
            if (en != "" && !TimeSpan.TryParse(en, out _))
            { AppContainer.Get<DialogService>().Error($"نهاية التوقف «{en}» غير صالحة — الصيغة HH:mm (مثال 15:10)."); return; }
            downs.Add(new DowntimeDto { Hours = hh, ReasonAr = rsn, StartTime = st, EndTime = en });
        }
        Downtimes = downs;

        // §B95 — جمع الفعلي المساعد + تأكيد الكرتون الفارغ (الفارغ = آلي)
        var aux = new List<AuxActualDto>();
        foreach (var r in _auxRows)
        {
            string t = (r.ActualText ?? "").Trim();
            if (t == "") continue;
            if (!double.TryParse(t, out var v) || v < 0)
            { AppContainer.Get<DialogService>().Error($"الفعلي المستهلك للمادة «{r.MaterialName}» غير صالح أو سالب."); return; }
            if (v > 0) aux.Add(new AuxActualDto { OrderId = _orderId, MaterialId = r.MaterialId, Qty = v });
        }
        ActualAux = aux;
        string ec = _emptyCartons.Text.Trim();
        if (ec != "")
        {
            if (!double.TryParse(ec, out var ev) || ev < 0)
            { AppContainer.Get<DialogService>().Error("الكرتون الفارغ الفعلي غير صالح أو سالب — اتركه فارغاً للتقدير الآلي."); return; }
            EmptyCartonsActual = ev;
        }

        ProducedKg = pk; ProducedCartons = pc; WastageKg = w; ConsumedKg = ck;
        CarryToNextDay = _carry.IsChecked == true;
        SendToQuality = _quality.IsChecked == true;
        Notes = _notes.Text;
        DialogResult = true;
        Close();
    }
}

/// <summary>
/// §B93 — 📋 ترحيل خطة إلى أوامر: اختيار الخطة المعتمدة + فترة اختيارية + وردية اختيارية،
/// ثم الترحيل (أمر لكل تاريخ×وردية×خط) وعرض المنشأة والمتخطاة والفاشلة بأسبابها.
/// </summary>
public class IssuePlanWindow : Window
{
    private readonly ComboBox _planBox = new() { Width = 420, MinHeight = 28 };
    private readonly DatePicker _from = new() { Width = 140 };
    private readonly DatePicker _to = new() { Width = 140 };
    private readonly ComboBox _shiftBox = new() { Width = 200, MinHeight = 28 };
    private readonly List<(int id, string label)> _plans;
    private readonly List<(int id, string name)> _shifts;

    public IssuePlanWindow(List<(int id, string label)> plans, List<(int id, string name)> shifts, int? preselectedPlanId = null)
    {
        _plans = plans;
        _shifts = shifts;
        Title = "📋 ترحيل خطة الإنتاج إلى أوامر تشغيل";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 640; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        Background = (Brush)new BrushConverter().ConvertFromString("#ECE9D8");

        foreach (var p in _plans) _planBox.Items.Add(p.label);
        if (preselectedPlanId != null)
        {
            int idx = _plans.FindIndex(p => p.id == preselectedPlanId.Value);
            if (idx >= 0) _planBox.SelectedIndex = idx;
        }
        if (_planBox.SelectedIndex < 0 && _plans.Count > 0) _planBox.SelectedIndex = 0;
        _shiftBox.Items.Add("— كل الورديات —");
        foreach (var s in _shifts) _shiftBox.Items.Add(s.name);
        _shiftBox.SelectedIndex = 0;

        var issueBtn = new Button { Content = "📋 ترحيل إلى أوامر", Padding = new Thickness(18, 8, 18, 8), FontSize = 13, IsDefault = true,
            Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton") };
        issueBtn.Click += (_, _) => Issue();
        var closeBtn = new Button { Content = "إغلاق", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0), IsCancel = true,
            Style = (Style)System.Windows.Application.Current.FindResource("ErpButton") };
        closeBtn.Click += (_, _) => Close();

        StackPanel Row(string label, Control c)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Bold, Width = 150, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(c);
            return sp;
        }
        var dates = new StackPanel { Orientation = Orientation.Horizontal };
        dates.Children.Add(_from);
        dates.Children.Add(new TextBlock { Text = "  إلى  ", VerticalAlignment = VerticalAlignment.Center });
        dates.Children.Add(_to);
        dates.Children.Add(new TextBlock { Text = "   (فارغ = كل الفترة)", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "تُرحَّل بنود الخطة ذات المتبقي إلى أوامر — أمر واحد لكل (تاريخ × وردية × خط) بكامل المتبقي.",
            FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(Row("خطة الإنتاج المعتمدة:", _planBox));
        panel.Children.Add(Row("الفترة (اختياري):", dates));
        panel.Children.Add(Row("الوردية (اختياري):", _shiftBox));
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        btns.Children.Add(issueBtn); btns.Children.Add(closeBtn);
        panel.Children.Add(btns);
        Content = panel;
    }

    private void Issue()
    {
        if (_planBox.SelectedIndex < 0) { AppContainer.Get<DialogService>().Error("اختر الخطة أولاً."); return; }
        int planId = _plans[_planBox.SelectedIndex].id;
        int? shiftId = _shiftBox.SelectedIndex > 0 ? _shifts[_shiftBox.SelectedIndex - 1].id : null;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var r = svc.IssueOrdersFromPlan(planId,
                _from.SelectedDate?.ToString("dd/MM/yyyy"), _to.SelectedDate?.ToString("dd/MM/yyyy"), shiftId);
            ShowResults(r);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Plan.Issue"); }
    }

    private void ShowResults(PlanIssueResult r)
    {
        var conv = new BrushConverter();
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new Border
        {
            Background = (Brush)conv.ConvertFromString(r.Ok ? "#DCFCE7" : "#FEE2E2"),
            BorderBrush = (Brush)conv.ConvertFromString(r.Ok ? "#16A34A" : "#DC2626"),
            BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock { Text = r.Message, FontWeight = FontWeights.Bold, FontSize = 13,
                Foreground = (Brush)conv.ConvertFromString(r.Ok ? "#14532D" : "#7F1D1D"), TextWrapping = TextWrapping.Wrap }
        });
        if (r.Created.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = $"الأوامر المنشأة ({r.Created.Count}):", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 28, MaxHeight = 220 };
            grid.Columns.Add(new DataGridTextColumn { Header = "رقم الأمر", Width = 120, Binding = new System.Windows.Data.Binding("OrderNumber") });
            grid.Columns.Add(new DataGridTextColumn { Header = "التاريخ", Width = 100, Binding = new System.Windows.Data.Binding("ProductionDate") });
            grid.Columns.Add(new DataGridTextColumn { Header = "الوردية", Width = 130, Binding = new System.Windows.Data.Binding("ShiftName") });
            grid.Columns.Add(new DataGridTextColumn { Header = "البنود", Width = 60, Binding = new System.Windows.Data.Binding("ItemsCount") });
            grid.Columns.Add(new DataGridTextColumn { Header = "الكجم", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new System.Windows.Data.Binding("TotalKg") { StringFormat = "N1" } });
            grid.ItemsSource = r.Created;
            panel.Children.Add(grid);
        }
        var issues = r.Skipped.Select(s => "⏭ " + s).Concat(r.Failed.Select(f => "❌ " + f)).ToList();
        if (issues.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "ملاحظات:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 4) });
            var box = new ListBox { MaxHeight = 150, FontSize = 11.5 };
            foreach (var s in issues) box.Items.Add(s);
            panel.Children.Add(box);
        }
        var closeBtn = new Button { Content = "إغلاق", Padding = new Thickness(24, 6, 24, 6), Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, IsCancel = true,
            Style = (Style)System.Windows.Application.Current.FindResource("ErpButton") };
        closeBtn.Click += (_, _) => { DialogResult = r.Created.Count > 0; Close(); };
        panel.Children.Add(closeBtn);
        Width = 720;
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
