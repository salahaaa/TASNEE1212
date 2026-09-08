using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §وصل شاشة إنشاء الخطة — معالج رأس الخطة ثم التوزيع ثم المراجعة ثم الحفظ.
/// كان <see cref="LotsEditorWindow"/> و<see cref="FairSummaryWindow"/> ومحرك
/// <see cref="IPlanningService.SuggestFairDistribution"/> مبنيّة لكن بلا مستدعٍ؛ هذه النافذة
/// هي حلقة الوصل: تجمع رأس الخطة (عنوان/نوع/نطاق/عميل/فترة/وردية/خط) ثم تُشغّل المسار
/// المختار (توزيع عادل آلي أو اختيار يدوي) وتنزل البنود إلى <see cref="IPlanningService.SavePlan"/>.
/// </summary>
public class PlanCreationWindow : Window
{
    // ── حقول الرأس ──
    private readonly TextBox _titleBox = new();
    private readonly ComboBox _typeBox = new();
    private readonly ComboBox _scopeBox = new();
    private readonly ComboBox _customerBox = new();
    private readonly DatePicker _from = new();
    private readonly DatePicker _to = new();
    private readonly ComboBox _shiftBox = new();
    private readonly ComboBox _lineBox = new();
    private readonly CheckBox _friBox = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

    // ── مراجع ──
    private List<Shift> _shifts = new();
    private List<ProductionLine> _lines = new();
    private List<Customer> _customers = new();
    private List<Product> _finished = new();
    private List<PackagingType> _packs = new();
    private List<ProductOption> _productOptions = new();
    private List<PackOption> _packOptions = new();
    private Dictionary<int, string> _productUnits = new();

    // §تخزين معدلات الطاقة المؤقت لكل (صنف × عبوة × وردية) لتجنب الاستعلام المتكرر
    private readonly Dictionary<(int product, int? pack, int shift), double> _rateMemo = new();

    public PlanCreationWindow()
    {
        Title = "➕ خطة إنتاج جديدة";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 660; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#ECE9D8");

        BuildUi();
        Loaded += (_, _) => LoadData();
    }

    // ═══════════════ بناء الواجهة ═══════════════

    private void BuildUi()
    {
        _typeBox.Items.Add("يومية (Daily)");
        _typeBox.Items.Add("فترية — خطط طويلة (Period)");
        _typeBox.SelectedIndex = 0;

        _scopeBox.Items.Add("متعددة العملاء (Multi)");
        _scopeBox.Items.Add("عميل واحد (Single)");
        _scopeBox.SelectedIndex = 0;
        _scopeBox.SelectionChanged += (_, _) => _customerBox.IsEnabled = _scopeBox.SelectedIndex == 1;

        _customerBox.DisplayMemberPath = "CustomerName";
        _customerBox.SelectedValuePath = "Id";
        _customerBox.IsEnabled = false;

        _shiftBox.DisplayMemberPath = "ShiftNameAr";
        _shiftBox.SelectedValuePath = "Id";
        _lineBox.DisplayMemberPath = "LineNameAr";
        _lineBox.SelectedValuePath = "Id";

        _from.SelectedDate = DateTime.Today;
        _to.SelectedDate = DateTime.Today;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "أدخل بيانات رأس الخطة ثم اختر طريقة التعبئة: توزيع عادل آلي على أيام الفترة، أو اختيار يدوي للدفعات.",
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int r = 0;
        void AddRow(string label, FrameworkElement el, string hint = null)
        {
            var lb = new TextBlock { Text = label, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
            Grid.SetRow(lb, r); Grid.SetColumn(lb, 0);
            el.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetRow(el, r); Grid.SetColumn(el, 1);
            grid.Children.Add(lb); grid.Children.Add(el);
            r++;
            if (hint != null)
            {
                var ht = new TextBlock { Text = hint, FontSize = 10.5, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, -4, 0, 6), TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(ht, r); Grid.SetColumn(ht, 1);
                grid.Children.Add(ht); r++;
            }
        }

        AddRow("عنوان الخطة *:", _titleBox);
        AddRow("نوع الخطة *:", _typeBox);
        AddRow("نطاق الخطة *:", _scopeBox, "«عميل واحد» يقفل الخطة على عميل محدد؛ «متعددة» تسمح ببنود لعدة عملاء.");
        AddRow("العميل (لخطة العميل الواحد):", _customerBox);
        AddRow("من تاريخ *:", _from);
        AddRow("إلى تاريخ *:", _to, "يومية = نفس اليوم. فترية = حدد المدى (أسبوع/شهر...) وسيوزع المحرك البنود على الأيام.");
        AddRow("الوردية الأساسية *:", _shiftBox);
        AddRow("خط الإنتاج *:", _lineBox);
        AddRow("تخطي الجمعة:", _friBox, "الأسبوع: السبت–الخميس. ألغِ التحديد إن كان المصنع يعمل أيام الجمعة.");
        panel.Children.Add(grid);

        var fairBtn = new Button { Content = "⚖ التوزيع العادل الآلي", Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton"), Margin = new Thickness(0, 8, 8, 0), IsDefault = true };
        fairBtn.Click += (_, _) => RunFairDistribution();
        var manualBtn = new Button { Content = "✍️ الاختيار اليدوي للدفعات", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(0, 8, 0, 0) };
        manualBtn.Click += (_, _) => RunManual();
        var cancelBtn = new Button { Content = "إلغاء", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
        cancelBtn.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        bar.Children.Add(fairBtn); bar.Children.Add(manualBtn); bar.Children.Add(cancelBtn);
        panel.Children.Add(bar);

        Content = panel;
    }

    private void LoadData()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _shifts = db.Shifts.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
            _lines = db.ProductionLines.AsNoTracking().OrderBy(l => l.Id).ToList();
            _customers = db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CustomerName).ToList();
            _finished = db.Products.AsNoTracking()
                .Where(p => p.IsActive && p.ItemType == "Finished").OrderBy(p => p.ProductNameAr).ToList();
            _packs = db.PackagingTypes.AsNoTracking().Where(k => k.IsActive).OrderBy(k => k.PackageNameAr).ToList();

            _productOptions = _finished.Select(p => new ProductOption { Id = p.Id, Name = $"{p.ProductNameAr} ({p.ProductCode})" }).ToList();
            _packOptions = _packs.Select(k => new PackOption
            {
                Id = k.Id, Name = k.PackageNameAr, UnitWeightKg = k.UnitWeightKg,
                MoldsCount = k.MoldsCount, MoldWeightKg = k.MoldWeightKg
            }).ToList();
            _productUnits = _finished.ToDictionary(p => p.Id,
                p => string.IsNullOrWhiteSpace(p.TradingUnit) ? (p.UnitOfMeasure ?? "كرتون") : p.TradingUnit);

            _shiftBox.ItemsSource = _shifts;
            _lineBox.ItemsSource = _lines;
            _customerBox.ItemsSource = _customers;
            if (_shifts.Count > 0) _shiftBox.SelectedIndex = 0;
            if (_lines.Count > 0) _lineBox.SelectedIndex = 0;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanCreation.LoadData"); }
    }

    // ═══════════════ التحقق من الرأس ═══════════════

    /// <summary>يتحقق من رأس الخطة ويعيد (title, planType, scopeMode, singleCustomerId, fromDate, toDate, shiftId, lineId) أو null مع رسالة.</summary>
    private (string title, string planType, string scopeMode, int? singleCustomerId,
        string fromDate, string toDate, int shiftId, int lineId)? ValidateHeader()
    {
        string title = _titleBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length < 3)
        { MessageBox.Show("عنوان الخطة إلزامي — 3 أحرف على الأقل.", "خطة جديدة", MessageBoxButton.OK, MessageBoxImage.Warning); return null; }
        if (_from.SelectedDate == null || _to.SelectedDate == null || _to.SelectedDate < _from.SelectedDate)
        { MessageBox.Show("حدد فترة صحيحة: من تاريخ ≤ إلى تاريخ.", "خطة جديدة", MessageBoxButton.OK, MessageBoxImage.Warning); return null; }
        if (_shiftBox.SelectedValue is not int shiftId)
        { MessageBox.Show("اختيار الوردية إلزامي.", "خطة جديدة", MessageBoxButton.OK, MessageBoxImage.Warning); return null; }
        if (_lineBox.SelectedValue is not int lineId)
        { MessageBox.Show("اختيار خط الإنتاج إلزامي.", "خطة جديدة", MessageBoxButton.OK, MessageBoxImage.Warning); return null; }

        string planType = _typeBox.SelectedIndex == 1 ? "Period" : "Daily";
        string scopeMode = _scopeBox.SelectedIndex == 1 ? "Single" : "Multi";
        int? singleCustomerId = scopeMode == "Single" ? (_customerBox.SelectedValue as int?) : null;
        if (scopeMode == "Single" && singleCustomerId == null)
        { MessageBox.Show("اختر عميل الخطة لنطاق «عميل واحد».", "خطة جديدة", MessageBoxButton.OK, MessageBoxImage.Warning); return null; }

        string fromDate = _from.SelectedDate.Value.ToString("dd/MM/yyyy");
        string toDate = _to.SelectedDate.Value.ToString("dd/MM/yyyy");
        return (title, planType, scopeMode, singleCustomerId, fromDate, toDate, shiftId, lineId);
    }

    // ═══════════════ المسار: التوزيع العادل الآلي ═══════════════

    private void RunFairDistribution()
    {
        var h = ValidateHeader();
        if (h == null) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            bool excludeFriday = _friBox.IsChecked != false;
            var proposal = planning.SuggestFairDistribution(h.Value.fromDate, h.Value.toDate, h.Value.shiftId, h.Value.lineId,
                null, null, excludeFriday);

            if (proposal.Rows.Count == 0)
            {
                var fallback = MessageBox.Show(
                    "⚖ لم يقترح المحرك أي بند في هذه الفترة" +
                    (proposal.SkippedNotes.Count > 0 ? $"\nالأسباب:\n• " + string.Join("\n• ", proposal.SkippedNotes.Take(5)) : "") +
                    "\n\nهل تريد الانتقال إلى الاختيار اليدوي للدفعات؟",
                    "التوزيع العادل", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (fallback == MessageBoxResult.Yes) RunManual();
                return;
            }

            var summary = new FairSummaryWindow(proposal) { Owner = this };
            if (summary.ShowDialog() != true) return;

            var rows = BuildRowsFromProposal(proposal, h.Value.lineId, h.Value.singleCustomerId);
            var editor = new LotsEditorWindow(rows, $"تحرير بنود الخطة — {h.Value.title}",
                h.Value.scopeMode == "Single", _from.SelectedDate, _to.SelectedDate, _shifts, h.Value.shiftId)
            { Owner = this };
            if (editor.ShowDialog() != true || editor.Inserted.Count == 0) return;

            Save(h.Value, editor.Inserted);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanCreation.Fair"); }
    }

    // ═══════════════ المسار: الاختيار اليدوي ═══════════════

    private void RunManual()
    {
        var h = ValidateHeader();
        if (h == null) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var lots = planning.GetAvailableLots(h.Value.singleCustomerId, _from.SelectedDate);
            if (lots.Count == 0)
            {
                MessageBox.Show("لا توجد دفعات متاحة للتخطيط في هذه الفترة لهذا العميل.",
                    "اختيار يدوي", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var rows = BuildRowsFromLots(lots, h.Value.shiftId, h.Value.lineId);
            var editor = new LotsEditorWindow(rows, $"تحرير بنود الخطة — {h.Value.title}",
                h.Value.scopeMode == "Single", _from.SelectedDate, _to.SelectedDate, _shifts, h.Value.shiftId)
            { Owner = this };
            if (editor.ShowDialog() != true || editor.Inserted.Count == 0) return;

            Save(h.Value, editor.Inserted);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanCreation.Manual"); }
    }

    // ═══════════════ بناء صفوف المحرر ═══════════════

    private List<ShiftOption> ShiftOptions()
        => _shifts.Select(s => new ShiftOption { Id = s.Id, Name = $"{s.ShiftNameAr} ({s.EffectiveProductiveHours:0.#}س)" }).ToList();

    private List<LotEditorRow> BuildRowsFromProposal(FairDistributionProposal proposal, int lineId, int? singleCustomerId)
    {
        var shiftOpts = ShiftOptions();
        var rows = new List<LotEditorRow>();
        using var scope2 = AppContainer.NewScope();
        var planning = scope2.ServiceProvider.GetRequiredService<IPlanningService>();
        var cap = scope2.ServiceProvider.GetRequiredService<ICapacityService>();

        foreach (var r in proposal.Rows)
        {
            // §عميل واحد: لا تسريب بنود عميل آخر إلى خطة Single
            if (singleCustomerId != null && r.CustomerId != singleCustomerId) continue;

            var row = new LotEditorRow
            {
                LotId = r.LotId, ShipmentId = r.ShipmentId, ShipmentNo = r.ShipmentNo,
                LotCode = r.LotCode, CustomerId = r.CustomerId, CustomerName = r.CustomerName,
                RawName = r.RawName, Available = r.AvailableKg, ReceiptUnit = r.ReceiptUnit,
                DaysInStockText = $"{r.DaysInStock} يوم",
                PresetDate = r.Date,
                DateValue = UiFormat.TryParseDate(r.Date, out var d) ? d : _from.SelectedDate
            };
            FillReference(row, r.ShiftId, lineId, planning, cap);
            row.AllShifts = shiftOpts;
            row.ProductId = r.ProductId;          // يشغّل RebuildPacks وUpdateAvailableDisplay وRecalc
            row.PackId = r.PackagingTypeId;       // يفرض عبوة المحرك (بعد RebuildPacks أعادها للأولى)
            row.CartonsText = r.PlannedCartons.ToString();
            row.ShiftId = r.ShiftId;              // بعد AllShifts — يزامن ShiftName
            rows.Add(row);
        }
        return rows;
    }

    private List<LotEditorRow> BuildRowsFromLots(List<AvailableLotDto> lots, int shiftId, int lineId)
    {
        var shiftOpts = ShiftOptions();
        var rows = new List<LotEditorRow>();
        using var scope = AppContainer.NewScope();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var cap = scope.ServiceProvider.GetRequiredService<ICapacityService>();

        foreach (var l in lots)
        {
            var row = new LotEditorRow
            {
                LotId = l.LotId, ShipmentId = l.ShipmentId, ShipmentNo = l.ShipmentNo,
                LotCode = l.LotCode, CustomerId = l.CustomerId, CustomerName = l.CustomerName,
                RawName = l.ProductName, ReceiptUnit = l.ReceiptUnit,
                Available = l.AvailableForDateKg > 0 ? l.AvailableForDateKg : Math.Max(0, l.RemainingKg),
                DaysInStockText = l.ArrivalDate != null ? $"{Math.Max(0, (DateTime.Today - l.ArrivalDate.Value.Date).Days)} يوم" : "",
                DateValue = _from.SelectedDate
            };
            FillReference(row, shiftId, lineId, planning, cap);
            row.AllShifts = shiftOpts;
            row.ShiftId = shiftId;
            // الصنف التام يُترك للاختيار اليدوي (ProductId = null) — العبوة والكمية يدخلها المستخدم
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>يملأ مراجع كل صف: الأصناف/العبوات/الوحدات/المعدلات/المتاح/الساعات المتبقية.</summary>
    private void FillReference(LotEditorRow row, int shiftId, int lineId, IPlanningService planning, ICapacityService cap)
    {
        row.AllProducts = _productOptions;
        row.AllPacks = _packOptions;
        row.ProductUnits = _productUnits;

        row.ProductRates = _finished.ToDictionary(p => p.Id, p => MemoRate(cap, p.Id, null, shiftId));
        row.PackRates = new Dictionary<(int productId, int? packId), double>();
        foreach (var p in _finished)
        {
            row.PackRates[(p.Id, (int?)null)] = MemoRate(cap, p.Id, null, shiftId);
            foreach (var k in _packs) row.PackRates[(p.Id, k.Id)] = MemoRate(cap, p.Id, k.Id, shiftId);
        }

        row.PerProductAvailable = _finished.ToDictionary(
            p => p.Id, p => planning.GetProductLotRemaining(row.LotId, p.Id));

        string date = (row.DateValue ?? _from.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy");
        var info = planning.GetShiftCapacityInfo(shiftId, lineId, date);
        row.RemainingShiftHours = info.RemainingHours;
    }

    private double MemoRate(ICapacityService cap, int product, int? pack, int shift)
    {
        var key = (product, pack, shift);
        if (!_rateMemo.TryGetValue(key, out var r))
        {
            var (rate, _) = cap.GetCapacity(product, shift, pack);
            r = rate;
            _rateMemo[key] = r;
        }
        return r;
    }

    // ═══════════════ الحفظ ═══════════════

    private void Save((string title, string planType, string scopeMode, int? singleCustomerId,
        string fromDate, string toDate, int shiftId, int lineId) h, List<LotEditorRow> inserted)
    {
        var items = inserted.Select(r => new PlanItemDto
        {
            SourceType = "FromReceiving",
            LotId = r.LotId,
            ShipmentId = r.ShipmentId,
            ReceiptUnit = r.ReceiptUnit,
            CustomerId = r.CustomerId,
            ProductId = r.ProductId ?? 0,
            PackagingTypeId = r.PackId,
            PlannedQtyKg = r.ComputedKg,
            PlannedCartons = int.TryParse(r.CartonsText, out var c) ? c : 0,
            ScheduledDate = r.DateValue?.ToString("dd/MM/yyyy") ?? h.fromDate,
            SuggestedShiftId = r.ShiftId ?? h.shiftId,
            SuggestedLineId = h.lineId,
            PriorityNo = 0
        }).ToList();

        if (items.Any(i => i.ProductId == 0))
        {
            AppContainer.Get<DialogService>().Error("بعض البنود بلا صنف تام محدد — عد إليها واختر الصنف.");
            return;
        }

        using var scope = AppContainer.NewScope();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var res = planning.SavePlan(h.title, h.planType, h.fromDate, h.toDate, h.shiftId, h.lineId, items,
            notes: null, scopeMode: h.scopeMode, singleCustomerId: h.singleCustomerId);
        if (!res.Ok) { AppContainer.Get<DialogService>().Error(res.Message); return; }
        AppContainer.Get<DialogService>().Info(res.Message);
        Close();
    }
}
