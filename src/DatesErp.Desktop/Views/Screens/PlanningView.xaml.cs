using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §إصلاح: يُبلّغ عن التغيير — Renumber() يُعيد ترقيم الصفوف بعد كل تعديل في المجموعة،
/// وبلا إشعار كانت خلية «م» تعرض أرقاماً قديمة وتصبح وسوم أزرار الحذف خاطئة.
/// </summary>
public class PlanRowUi : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

    private int _no;
    public int No { get => _no; set { if (_no != value) { _no = value; OnChanged(nameof(No)); } } }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public int? ShipmentId { get; set; }
    public string ShipmentNo { get; set; }
    public int? LotId { get; set; }
    public string LotCode { get; set; }
    public string RawName { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    public int? PackId { get; set; }
    public string PackName { get; set; }
    /// <summary>§B80: وحدة الصنف التام كما في بطاقته (شاشة الأصناف) — مثل «كرتون 5كجم».</summary>
    public string UnitDisplay { get; set; }
    public double QtyKg { get; set; }
    /// <summary>§B58: وزن كرتون الصنف لاشتقاق الكيلو عند تعديل الكراتين داخل الجدول.</summary>
    public double CartonWeight { get; set; }
    private int _cartons;
    public int Cartons { get => _cartons; set { if (_cartons != value) { _cartons = value; _cartonsText = value.ToString(); OnChanged(nameof(Cartons)); OnChanged(nameof(CartonsText)); } } }
    private string _cartonsText = "0";
    /// <summary>§B58: تحرير الكراتين داخل الجدول يعيد حساب الوزن المكافئ آلياً.</summary>
    public string CartonsText
    {
        get => _cartonsText;
        set
        {
            if (_cartonsText == value) return;
            _cartonsText = value; OnChanged(nameof(CartonsText));
            if (int.TryParse(value, out var c) && c >= 0)
            {
                _cartons = c; OnChanged(nameof(Cartons));
                if (CartonWeight > 0) { QtyKg = Math.Round(c * CartonWeight, 1); OnChanged(nameof(QtyKg)); }
            }
        }
    }
    private string _date;
    public string Date
    {
        get => _date;
        set
        {
            if (_date == value) return;
            _date = value; OnChanged(nameof(Date));
            // §B80: المزامنة مع DatePicker — تحرير نصي أو اختيار من التقويم يحدّث الطرفين
            if (DatesErp.Core.Common.UiFormat.TryParseDate(value, out var dv)) { _dateValue = dv; OnChanged(nameof(DateValue)); }
        }
    }
    private DateTime? _dateValue;
    /// <summary>§B80: تاريخ الإنتاج كتاريخ حقيقي لعمود DatePicker — قابل للتعديل دائماً في الجدول.</summary>
    public DateTime? DateValue
    {
        get => _dateValue;
        set
        {
            if (_dateValue == value) return;
            _dateValue = value; OnChanged(nameof(DateValue));
            _date = value?.ToString("dd/MM/yyyy") ?? ""; OnChanged(nameof(Date));
        }
    }
    private int _shiftId;
    public int ShiftId { get => _shiftId; set { if (_shiftId != value) { _shiftId = value; OnChanged(nameof(ShiftId)); } } }
    public string ShiftName { get; set; }
    public string LineName { get; set; }
    private int _lineId;
    public int LineId { get => _lineId; set { if (_lineId != value) { _lineId = value; OnChanged(nameof(LineId)); } } }
    public int Priority { get; set; }
}

/// <summary>§B58: خيار قائمة (وردية/خط/عبوة) لخلايا الجدولEditable.</summary>
public class OptUi { public int Id { get; set; } public string Name { get; set; } }

/// <summary>
/// شاشة إعداد واعتماد خطط الإنتاج (MPS) — مطابقة للنموذج المعتمد:
/// مسار المعاملة (إعداد ← اعتماد المدير العام ← أوامر التشغيل ← الإقفال)،
/// نطاق التخطيط (عدة عملاء/عميل محدد)، أزرار الإدراج، شريط طاقة الوردية،
/// خطة اليوم وحالة الأيام وتقدم العملاء المستقل.
/// </summary>
public partial class PlanningView : UserControl
{
    private List<object> _plans_all = new();
    private readonly ObservableCollection<PlanRowUi> _rows = new();
    private List<(int? id, string name)> _planCustomers = new();
    private List<int> _planIds = new();
    // §إصلاح: معرّفات الورديات/الخطوط الفعلية المعروضة — بدل افتراض SelectedIndex+1
    private List<int> _shiftIds = new();
    private List<int> _lineIds = new();
    private int SelectedShiftId() => ShiftBox.SelectedIndex >= 0 && ShiftBox.SelectedIndex < _shiftIds.Count ? _shiftIds[ShiftBox.SelectedIndex] : 1;
    private int SelectedLineId() => LineBox.SelectedIndex >= 0 && LineBox.SelectedIndex < _lineIds.Count ? _lineIds[LineBox.SelectedIndex] : 1;
    private int _currentPlanId;
    private bool _locked;
    private bool _programmaticScope; // حارس: تغييرات النطاق البرمجية لا تفتح النوافذ تلقائياً
    private Views.ErpToolbar _toolbar;

    // §B58: قوائم الخلاياEditable (وردية/خط/عبوة) — تُقرأ من قاعدة البيانات في Load
    public List<OptUi> ShiftOptions { get; } = new();
    public List<OptUi> LineOptions { get; } = new();
    public List<OptUi> PackOptions { get; } = new();

    public PlanningView()
    {
        InitializeComponent();
        RowsGrid.ItemsSource = _rows;
        _rows.CollectionChanged += (_, e) =>
        {
            Renumber(); UpdateCapacityBar(); UpdateTotals();
            if (e.NewItems != null)
                foreach (var r in e.NewItems) ((PlanRowUi)r).PropertyChanged += RowUi_Changed;
        };
        Loaded += (_, _) =>
        {
            Load();
            // §إصلاح: قائمة الخطط المحفوظة تُحمّل فور فتح الشاشة لتظهر مباشرة في شبكة السجل
            RefreshPlansList();
            // §فتح خطة محددة طُلبت من شاشة أخرى (لوحة التحكم) ثم تصفير الطلب
            if (MainWindow.PendingPlanIdToOpen is int pid)
            {
                MainWindow.PendingPlanIdToOpen = null;
                OpenPlan(pid);
            }
        };
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("خطة الإنتاج — التخطيط والجدولة (MPS)");
        chrome.SetScreenCode("MRPMPS1001");
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => NewPlan(), "خطة إنتاج جديدة (F2)")
            .WithSave((_, _) => Save_Click(null, null), "حفظ الخطة (F10)")
            // §إصلاح: الاعتماد كان مكتوباً في Approve() لكنه غير موصول بأي زر — فلم يكن ممكناً
            // اعتماد خطة من شاشتها إطلاقاً، وكان المسار الوحيد عبر لوحة التحكم وللمدير فقط.
            .WithApprove((_, _) => Approve(), "اعتماد الخطة ونقلها لأوامر التشغيل")
            .WithDelete((_, _) => DeletePlan())
            .WithUndo((_, _) => UndoInput(), "تراجع / مسح التعديلات والبدء من جديد")
            .WithSearch((_, _) => { RefreshPlansList(); PlansSearchBox.Focus(); }, "بحث واختيار من الخطط المحفوظة (F9)")
            .WithPrint((_, _) => Print())
            .WithExcel((_, _) => Export())
            .WithUnapprove((_, _) => Unapprove(), "إلغاء الاعتماد وإعادة الفتح")
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithCustom("📋 تحديث القائمة", "ErpButton", (_, _) => RefreshPlansList())
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        if (_toolbar.UnapproveBtn != null) _toolbar.UnapproveBtn.Visibility = Visibility.Collapsed;
        if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.Visibility = Visibility.Collapsed;
        chrome.SetToolbar(_toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void Load()
    {
        // كل قسم يُحمَّل مستقلاً: فشل قسم (كالورديات) لا يجوز أن يمنع ظهور قائمة العملاء
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var shiftRows = db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
            _shiftIds = shiftRows.Select(s => s.Id).ToList();
            // §B58: خيارات خلايا الجدول (وردية/خط/عبوة) من قاعدة البيانات
            ShiftOptions.Clear(); ShiftOptions.AddRange(shiftRows.Select(x => new OptUi { Id = x.Id, Name = x.ShiftNameAr }));
            LineOptions.Clear(); LineOptions.AddRange(db.ProductionLines.AsNoTracking().OrderBy(x => x.Id).Select(x => new OptUi { Id = x.Id, Name = x.LineNameAr }));
            PackOptions.Clear(); PackOptions.Add(new OptUi { Id = 0, Name = "عام (أي عبوة)" });
            PackOptions.AddRange(db.PackagingTypes.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).Select(x => new OptUi { Id = x.Id, Name = x.PackageNameAr }));
            ShiftBox.ItemsSource = shiftRows
                .Select(s => $"{s.ShiftNameAr} (ساعات فعلية: {s.EffectiveProductiveHours} س)").ToList();
            var lineRows = db.ProductionLines.Where(l => l.IsActive).OrderBy(l => l.Id).ToList();
            _lineIds = lineRows.Select(l => l.Id).ToList();
            LineBox.ItemsSource = lineRows
                .Select(l => $"{l.LineNameAr} (طاقة: {l.CapacityPerShift} كجم)").ToList();
            if (ShiftBox.Items.Count > 0) ShiftBox.SelectedIndex = 0;
            if (LineBox.Items.Count > 0) LineBox.SelectedIndex = 0;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Load.ShiftsLines"); }

        RefreshCustomerList();

        CodeBox.Text = "PLN-تلقائي";
        if (PlanMetaBox != null) PlanMetaBox.Text = "خطة جديدة — لم تُحفظ بعد · أنشأها: — · اعتمدها: —";
        // §2 — الشاشة تفتح فارغة في وضع «خطة جديدة» — الخطط المحفوظة تظهر عبر زر «قائمة الخطط / بحث»
        UpdateCapacityBar();
    }

    /// <summary>تحميل/إعادة تحميل قائمة العملاء لخطة العميل المحدد — مع إظهار سبب الفشل الحقيقي إن فشل.</summary>
    private void RefreshCustomerList()
    {
        try
        {
            if (SingleCustBox == null) return;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var customers = db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CustomerName).ToList();
            SingleCustBox.ItemsSource = customers;
            if (customers.Count == 0)
                AppContainer.Get<DialogService>().Error("لا يوجد عملاء نشطون في البيانات الأساسية — أضف العملاء أولاً من شاشة بيانات العملاء.");
            else if (SingleCustBox.SelectedIndex < 0)
                SingleCustBox.Text = "-- اختر العميل --";
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "Planning.Load.Customers");
        }
    }

    // ══════════ نطاق التخطيط ونوع الخطة ══════════

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (SingleCustPanel == null) return;
        bool single = SingleRadio.IsChecked == true;
        SingleCustPanel.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        // الخطة لعميل واحد: العميل يُحفظ في رأس النموذج فلا يظهر عموده بجوار البنود
        if (CustomerColumn != null)
            CustomerColumn.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        // في كل مرة يختار المستخدم «خطة لعميل محدد» تُعاد قراءة قائمة العملاء لضمان ألا تكون فارغة
        if (single) RefreshCustomerList();
    }

    /// <summary>نوع الخطة (مطابق لـ v1.59 onPlanTypeChanged): اليومية تاريخ واحد — «إلى تاريخ» يقفل عليه.</summary>
    private void TypeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TypeBox == null || EndBox == null) return;
        bool period = TypeBox.SelectedIndex == 3;
        EndBox.IsEnabled = period && !_locked;
        if (!period && StartBox != null && StartBox.SelectedDate != null)
            EndBox.SelectedDate = PlanningService.PeriodEndDate(PlanTypeKey(), StartBox.SelectedDate.Value);
    }

    private void PlanDates_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TypeBox != null && TypeBox.SelectedIndex != 3 && StartBox != null && EndBox != null && StartBox.SelectedDate != null)
            EndBox.SelectedDate = PlanningService.PeriodEndDate(PlanTypeKey(), StartBox.SelectedDate.Value);
    }

    /// <summary>مطابق لـ v1.59 onSinglePlanCustomerChanged: اختيار العميل في الخطة الفردية يفتح نافذة دفعاته فوراً.</summary>
    private void SingleCust_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_programmaticScope || SingleCustBox == null || SingleCustBox.SelectedItem == null) return;
        if (SingleRadio.IsChecked != true) return;
        try
        {
            var cust = SingleCustBox.SelectedItem;
            var id = (int)cust.GetType().GetProperty("Id")!.GetValue(cust)!;
            var name = (string)cust.GetType().GetProperty("CustomerName")!.GetValue(cust)!;
            OpenLotsEditor(id, name);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.SingleCustomer"); }
    }

    // ══════════ الإدراج: أصناف العميل / العملاء ══════════

    /// <summary>👤 أصناف العميل (F4): عميل محدد ← نافذة شحناته ودفعاته ← اختيار الدفعة ← تفاصيل البند.</summary>
    private void PickLot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int? custId = null; string custName = null;
            if (SingleRadio.IsChecked == true)
            {
                if (SingleCustBox.SelectedItem == null)
                { AppContainer.Get<DialogService>().Error("اختر العميل المحدد أولاً."); return; }
                custId = (int)SingleCustBox.SelectedItem.GetType().GetProperty("Id").GetValue(SingleCustBox.SelectedItem);
                custName = (string)SingleCustBox.SelectedItem.GetType().GetProperty("CustomerName").GetValue(SingleCustBox.SelectedItem);
                AddRowForCustomer(custId.Value, custName);
            }
            else
            {
                OpenLotsEditor(null, null);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.PickLot"); }
    }

    /// <summary>👥 أصناف العملاء (F6): النافذة المجمعة لشحنات ودفعات كافة العملاء (مطابقة لـ v1.59).</summary>
    private void MultiCustomers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // §B76: في وضع «عميل محدد» لا تُفتح قائمة كل العملاء — دفعات عميله فقط
            if (SingleRadio.IsChecked == true)
            {
                if (SingleCustBox.SelectedItem == null)
                { AppContainer.Get<DialogService>().Error("اختر العميل المحدد أولاً."); return; }
                var cust = SingleCustBox.SelectedItem as DatesErp.Core.Domain.Entities.Customer;
                OpenLotsEditor(cust.Id, cust.CustomerName);
                return;
            }
            OpenLotsEditor(null, null);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.MultiCustomers"); }
    }

    /// <summary>👤 أصناف العميل (F4): فتح النافذة المنبثقة لدفعات العميل المحدد فقط.</summary>
    private void AddRowForCustomer(int custId, string custName)
    {
        OpenLotsEditor(custId, custName);
    }

    /// <summary>
    /// النافذة المنبثقة لاختيار الأصناف — مطابقة لنموذج v1.59:
    /// كل دفعة صف قابل للتحرير (الصنف التام ← العبوة ← الكراتين ← الخام يُحسب) مع تحديد متعدد
    /// وإدراج فردي وإنزال جماعي. إن مرَّ عميل محدد تُعرض دفعاته فقط.
    /// </summary>
    private void OpenLotsEditor(int? custId, string custName)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        int shiftId = SelectedShiftId();
        string rowDate = (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy");

        // الساعات المتبقية للوردية في اليوم المحدد (بعد خصم بنود الخطة الحالية)
        double remainingHours;
        {
            var prog = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
            var dayRows = prog.GetDailyPlan(rowDate, _currentPlanId > 0 ? _currentPlanId : null);
            var shiftRow = db.Shifts.AsNoTracking().FirstOrDefault(x => x.Id == shiftId);
            double eff = shiftRow?.EffectiveProductiveHours ?? 8;
            remainingHours = eff - dayRows.Sum(r => r.RequiredHours);
            if (remainingHours < 0) remainingHours = 0;
        }

        var capSvc = scope.ServiceProvider.GetRequiredService<ICapacityService>();
        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        // فلتر مطابق لـ v1.59: أصناف المجموعة 002 أو بلا مجموعة — كي تظهر أصناف المصنع مهما كان تعبئة المجموعة
        var products = svc.GetFinishedProducts();
        if (products.Count == 0)
        {
            AppContainer.Get<DialogService>().Error(
                "لا توجد أصناف تامة معرفّة في النظام (المجموعة 002 أو بدون مجموعة).\n" +
                "أضف الأصناف التامة أولاً من شاشة الأصناف ثم أعد المحاولة — ولهذا السبب تظهر قائمة الأصناف فارغة في نافذة الاختيار.");
            return;
        }
        var packs = db.PackagingTypes.Where(p => p.IsActive).ToList();
        if (packs.Count == 0)
        {
            AppContainer.Get<DialogService>().Error("لا توجد عبوات معرفّة — أضف العبوات (الكراتين والقوالب) من شاشة العبوات أولاً.");
            return;
        }

        // §B67: مصدر واحد مُفلتر: عميل محدد ← دفعاته فقط (حتى الموروثة من السند)؛ عدة عملاء ← الكل
        var lotDtos = svc.GetAvailableLots(custId);
        var today = DateTime.Today;
        var editorRows = lotDtos.Select(l =>
        {
            var row = new LotEditorRow
            {
                LotId = l.LotId,
                ShipmentId = l.ShipmentId,
                ShipmentNo = l.ShipmentNo ?? "—",
                LotCode = l.LotCode,
                CustomerId = l.CustomerId, // §B87/M6: null = «بدون عميل» — يُحفَظ NULL لا صفراً
                CustomerName = l.CustomerName ?? (custName ?? "—"),
                RawName = l.ProductName ?? "—",
                Available = l.RemainingKg,
                DaysInStockText = l.ArrivalDate != null ? $"{Math.Max(0, (today - l.ArrivalDate.Value.Date).Days)} يوماً" : "",
                // §B68: لا تُعرض الأصناف التي نفذ رصيدها من هذه الدفعة
                AllProducts = svc.GetPlannableProducts(l.LotId).Select(p => new ProductOption { Id = p.Id, Name = $"{p.ProductNameAr} ({p.ProductCode})" }).ToList(),
                AllPacks = packs.Select(p => new PackOption { Id = p.Id, Name = p.PackageNameAr, UnitWeightKg = p.UnitWeightKg, MoldsCount = p.MoldsCount, MoldWeightKg = p.MoldWeightKg }).ToList(),
                RemainingShiftHours = remainingHours,
                // §B80: وحدة كل صنف تام كما في بطاقته — تظهر فور اختيار الصنف
                ProductUnits = products.ToDictionary(x => x.Id, x => string.IsNullOrWhiteSpace(x.UnitOfMeasure) ? "—" : x.UnitOfMeasure)
            };
            foreach (var p2 in products)
            {
                row.ProductRates[p2.Id] = capSvc.GetCapacity(p2.Id, shiftId).rate;
                foreach (var pk in packs)
                    row.PackRates[(p2.Id, pk.Id)] = capSvc.GetCapacity(p2.Id, shiftId, pk.Id).rate;
            }
            int? exclPlan = _currentPlanId > 0 ? _currentPlanId : (int?)null;
            foreach (var p2 in products)
                row.PerProductAvailable[p2.Id] = svc.GetProductLotRemaining(l.LotId, p2.Id, exclPlan);
            return row;
        }).Where(r => r.Available > 0 && r.AllProducts.Count > 0).ToList();
        var allLots = lotDtos;

        if (editorRows.Count == 0)
        {
            // §B56: رسالة تشخيصية صادقة تفرز السببين: لا دفعات أصلاً، أم دفعات محجوزة/مستهلكة بالكامل
            if (allLots.Count == 0)
            {
                int anyLots = db.Lots.Count();
                AppContainer.Get<DialogService>().Error(custId != null
                    ? $"لا توجد دفعات خام في المخزن باسم العميل «{custName}» (إجمالي الدفعات بالنظام: {anyLots}).\n" +
                      "التخطيط يستهلك الخام من دفعات الاستلام — استلم خاماً لهذا العميل من شاشة الاستلام ثم عد للتخطيط."
                    : "لا توجد شحنات خام في المخزن حالياً — التخطيط يبني بنوده على دفعات الاستلام.\n" +
                      "استلم الخام أولاً من شاشة «الاستلام وسندات الاستلام» (يعتمد الاستلام فتتكوّن الدفعات)، ثم عد إلى الخطط.");
            }
            else
            {
                double totalReserved = allLots.Sum(l => l.ReservedQtyKg); // lotDtos
                AppContainer.Get<DialogService>().Error(
                    $"توجد {allLots.Count} دفعة بالمخزن لكن المتاح منها للتخطيط صفر — كل الكميات محجوزة لخطط/أوامر قائمة أو مستهلكة.\n" +
                    $"إجمالي المحجوز: {totalReserved:N1} كجم. أقفل أو احذف الخطط المنتهية لتحرير الحجوزات، أو استلم خاماً إضافياً.");
            }
            return;
        }

        string title = custId != null
            ? $"👤 أصناف وشحنات العميل: {custName} — اختر الصنف التام والكمية ثم إنزال"
            : "👥 أصناف وشحنات كافة العملاء المتاحة بالمستودع — دليل الاختيار المجمع";
        // §B80: فترة الخطة تُمرر للنافذة — تاريخ كل بند إلزامي داخلها
        DateTime? planFrom = StartBox.SelectedDate ?? DateTime.Today;
        DateTime? planTo = EndBox.SelectedDate ?? planFrom;
        // §B92: الاختيار اليدوي للوردية — الورديات النشطة + وردية الشاشة افتراضياً
        var shiftsForManual = db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
        var win = new LotsEditorWindow(editorRows, title, custId != null, planFrom, planTo, shiftsForManual, shiftId) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.Inserted.Count == 0) return;

        // تحويل الصفوف المدرجة إلى بنود الخطة (صنف كامل أو جزء — حسب ما أدخله المستخدم)
        InsertEditorRows(win.Inserted, products, packs, rowDate, shiftId,
            LineBox.SelectedIndex >= 0 ? SelectedLineId() : 1, db);
    }

    /// <summary>
    /// §B91 — 🔍 فحص الخطة: يوزّع بنود الخطة المحفوظة (عملاء/أصناف) على أيام الفترة (من–إلى)
    /// بنفس عمل محرك التوزيع، ويعرض الحكم (قابلة للتنفيذ/عجز) + الأيام + العملاء + الأصناف + التحذيرات.
    /// </summary>
    private void CheckPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlanId == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً أو اختر خطة من السجل لفحصها."); return; }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var result = svc.CheckPlan(_currentPlanId);
            var win = new PlanCheckWindow(result) { Owner = Window.GetWindow(this) };
            win.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Plan.Check"); }
    }

    /// <summary>
    /// ⚖ اقتراح توزيع عادل — معالج آلي بفترة حرة (أسبوع/20 يوماً/شهر...) ووردية إلزامية:
    /// يبني البنود من الأرصدة المتاحة بالتناوب العادل (الأقل إنجازاً أولاً + أقدم الحاويات FIFO)،
    /// يعرض نصيب كل عميل وأيام إنتاجه، ثم يتيح تنزيلها كاملة أو جزئياً بعد التعديل.
    /// </summary>
    private void Fair_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var shifts = db.Shifts.Where(s => s.IsActive).ToList();
            var lines = db.ProductionLines.Where(l => l.IsActive).ToList();
            var products = svc.GetFinishedProducts();

            var start = StartBox.SelectedDate ?? DateTime.Today;
            var end = EndBox.SelectedDate ?? start.AddDays(6);
            var wiz = new FairDistributionWizardWindow(shifts, lines, products, start, end,
                ShiftBox.SelectedIndex >= 0 ? SelectedShiftId() : 1,
                LineBox.SelectedIndex >= 0 ? SelectedLineId() : 1)
            { Owner = Window.GetWindow(this) };
            if (wiz.ShowDialog() != true) return;

            var proposal = svc.SuggestFairDistribution(wiz.FromDate, wiz.ToDate, wiz.ShiftId, wiz.LineId, wiz.TargetProductId, wiz.DailyKg, wiz.ExcludeFriday);
            if (!proposal.Ok || proposal.Rows.Count == 0)
            { AppContainer.Get<DialogService>().Error(proposal.Message ?? "لا توجد نتائج للتوزيع."); return; }

            var summary = new FairSummaryWindow(proposal) { Owner = Window.GetWindow(this) };
            if (summary.ShowDialog() != true) return;

            // تحويل الاقتراح إلى صفوف قابلة للتحرير في نافذة الاختيار (صنف كامل أو جزء = عدّل الكراتين)
            var packs = db.PackagingTypes.Where(p => p.IsActive).ToList();
            // §B87: المحرك يملأ كل الورديات — الساعات المتبقية تُحسب لكل (يوم×وردية البند)
            var shiftsEff = db.Shifts.AsNoTracking().Where(s => s.IsActive)
                .ToDictionary(s => s.Id, s => s.EffectiveProductiveHours > 0 ? s.EffectiveProductiveHours : 8);
            var prog = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
            var usedByDayShift = new Dictionary<(string day, int shift), double>();
            double UsedOnDayShift(string day, int shift)
            {
                var key = (day, shift);
                if (!usedByDayShift.TryGetValue(key, out var u))
                { u = prog.GetDailyPlan(day, null).Where(x => x.ShiftId == shift).Sum(x => x.RequiredHours); usedByDayShift[key] = u; }
                return u;
            }
            var capSvc = scope.ServiceProvider.GetRequiredService<ICapacityService>();
            var editorRows = proposal.Rows.Select(r =>
            {
                var er = new LotEditorRow
                {
                    LotId = r.LotId, ShipmentId = r.ShipmentId, ShipmentNo = r.ShipmentNo,
                    LotCode = r.LotCode, CustomerId = r.CustomerId, CustomerName = r.CustomerName,
                    RawName = r.RawName, Available = r.AvailableKg,
                    DaysInStockText = $"{r.DaysInStock} يوماً",
                    PresetDate = r.Date,
                    ShiftId = r.ShiftId, ShiftName = r.ShiftName ?? "—",
                    ProductId = r.ProductId, PackId = r.PackagingTypeId,
                    CartonsText = r.PlannedCartons.ToString(),
                    AllProducts = products.Select(p => new ProductOption { Id = p.Id, Name = $"{p.ProductNameAr} ({p.ProductCode})" }).ToList(),
                    AllPacks = packs.Select(p => new PackOption { Id = p.Id, Name = p.PackageNameAr, UnitWeightKg = p.UnitWeightKg, MoldsCount = p.MoldsCount, MoldWeightKg = p.MoldWeightKg }).ToList(),
                    RemainingShiftHours = Math.Max(0, (shiftsEff.TryGetValue(r.ShiftId, out var se) ? se : 8) - UsedOnDayShift(r.Date, r.ShiftId))
                };
                foreach (var p in products)
                    er.ProductRates[p.Id] = capSvc.GetCapacity(p.Id, r.ShiftId).rate;
                return er;
            }).ToList();

            DatesErp.Core.Common.UiFormat.TryParseDate(wiz.FromDate, out var fairFrom);
            DatesErp.Core.Common.UiFormat.TryParseDate(wiz.ToDate, out var fairTo);
            var fairUnits = products.ToDictionary(x => x.Id, x => string.IsNullOrWhiteSpace(x.UnitOfMeasure) ? "—" : x.UnitOfMeasure);
            foreach (var er2 in editorRows) er2.ProductUnits = fairUnits;
            var win = new LotsEditorWindow(editorRows,
                "⚖ بنود التوزيع العادل — راجع وعدّل (صنف كامل أو جزء، تاريخ ووردية) ثم أنزل للخطة", false, fairFrom, fairTo,
                shifts, wiz.ShiftId)
            { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() != true || win.Inserted.Count == 0) return;
            InsertEditorRows(win.Inserted, products, packs, wiz.FromDate, wiz.ShiftId, wiz.LineId, db);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Fair"); }
    }

    /// <summary>إنزال الصفوف المدرجة من نافذة الاختيار إلى جدول بنود الخطة (مشترك بين اليدوي والتوزيع العادل).</summary>
    private void InsertEditorRows(List<LotEditorRow> inserted, List<DatesErp.Core.Domain.Entities.Product> products,
        List<DatesErp.Core.Domain.Entities.PackagingType> packs, string fallbackDate, int shiftId, int lineId, DatesErpDbContext db)
    {
        foreach (var row in inserted)
        {
            var pack = packs.FirstOrDefault(p => p.Id == row.PackId);
            _rows.Add(new PlanRowUi
            {
                CustomerId = row.CustomerId,
                CustomerName = row.CustomerName,
                ShipmentId = row.ShipmentId,
                ShipmentNo = row.ShipmentNo,
                LotId = row.LotId,
                LotCode = row.LotCode,
                RawName = row.RawName,
                ProductId = row.ProductId ?? 0,
                ProductName = products.FirstOrDefault(p => p.Id == row.ProductId)?.ProductNameAr ?? "-",
                PackId = row.PackId,
                PackName = pack?.PackageNameAr ?? "-",
                UnitDisplay = products.FirstOrDefault(p => p.Id == row.ProductId)?.UnitOfMeasure ?? "—",
                CartonWeight = products.FirstOrDefault(p => p.Id == row.ProductId)?.CartonWeightKg ?? pack?.UnitWeightKg ?? 0,
                QtyKg = row.ComputedKg,
                Cartons = int.TryParse(row.CartonsText, out var c) ? c : 0,
                // §B80: التاريخ من عمود التاريخ في النافذة (إلزامي) ثم السقط المسبق ثم بداية الفترة
                DateValue = row.DateValue
                    ?? (DatesErp.Core.Common.UiFormat.TryParseDate(row.PresetDate, out var pdv) ? pdv : (DateTime?)null)
                    ?? (DatesErp.Core.Common.UiFormat.TryParseDate(fallbackDate, out var fdv) ? fdv : DateTime.Today),
                ShiftId = row.ShiftId ?? shiftId, // §B87: وردية البند من المحرك، أو وردية الشاشة لليدوي
                ShiftName = db.Shifts.AsNoTracking().Where(x => x.Id == (row.ShiftId ?? shiftId)).Select(x => x.ShiftNameAr).FirstOrDefault() ?? "-",
                LineId = lineId,
                LineName = db.ProductionLines.AsNoTracking().Where(x => x.Id == lineId).Select(x => x.LineNameAr).FirstOrDefault() ?? "-"
            });
            if (!_planCustomers.Any(x => x.id == row.CustomerId))
                _planCustomers.Add((row.CustomerId, row.CustomerName));
        }
        UpdateCapacityBar();
        RowsGrid.Items.Refresh();
        AppContainer.Get<DialogService>().Info($"تم إنزال ({inserted.Count}) بند إلى الخطة بنجاح.");
    }

    /// <summary>✍️ إضافة يدوي (بدون دفعة).</summary>
    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var products = scope.ServiceProvider.GetRequiredService<IPlanningService>().GetFinishedProducts();
            // §B76: في وضع العميل الواحد يُثبَّت العميل؛ والقوائم تُعرض بالمعرّف المرفق بالاسم (لا مسار أسماء مجردة)
            bool single = SingleRadio.IsChecked == true;
            var selCust = SingleCustBox.SelectedItem as DatesErp.Core.Domain.Entities.Customer;
            if (single && selCust == null) { AppContainer.Get<DialogService>().Error("اختر العميل المحدد أولاً."); return; }
            var customers = single ? new List<DatesErp.Core.Domain.Entities.Customer> { selCust } : db.Customers.Where(c => c.IsActive).ToList();
            // §B92: اليدوي الفتري — وردية باختيار الإدارة + تاريخ إلزامي داخل فترة الخطة
            var shiftsManual = db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
            if (shiftsManual.Count == 0) { AppContainer.Get<DialogService>().Error("لا توجد ورديات نشطة — فعّل وردية أولاً."); return; }
            string LabelC(DatesErp.Core.Domain.Entities.Customer c) => $"{c.CustomerName} ({c.CustomerCode})";
            string LabelP(DatesErp.Core.Domain.Entities.Product p) => $"{p.ProductNameAr} ({p.ProductCode})";
            string LabelS(DatesErp.Core.Domain.Entities.Shift s) => $"{s.ShiftNameAr} ({s.ShiftCode})";
            var fields = new List<Views.FieldDef>
            {
                new() { Key = "cust", LabelAr = "العميل *", Kind = "combo", Options = customers.Select(LabelC).ToArray() },
                new() { Key = "product", LabelAr = "الصنف التام *", Kind = "combo", Options = products.Select(LabelP).ToArray() },
                new() { Key = "qty", LabelAr = "الوزن المطلوب (كجم)", Default = "0" },
                new() { Key = "cartons", LabelAr = "الكراتين", Default = "0" },
                new() { Key = "date", LabelAr = "تاريخ الإنتاج (داخل الفترة)", Default = (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy") },
                new() { Key = "shift", LabelAr = "الوردية *", Kind = "combo", Options = shiftsManual.Select(LabelS).ToArray() }
            };
            var dlg = new Views.EntityFormDialog("إضافة بند يدوي", fields) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            var cust = customers.FirstOrDefault(c => LabelC(c) == dlg.Values["cust"]?.ToString());
            var prod = products.FirstOrDefault(p => LabelP(p) == dlg.Values["product"]?.ToString());
            var shf = shiftsManual.FirstOrDefault(s => LabelS(s) == dlg.Values["shift"]?.ToString());
            if (cust == null || prod == null) { AppContainer.Get<DialogService>().Error("اختر العميل والصنف."); return; }
            if (shf == null) { AppContainer.Get<DialogService>().Error("اختر وردية الإنتاج للبند."); return; }
            double.TryParse(dlg.Values["qty"]?.ToString(), out var qty);
            int.TryParse(dlg.Values["cartons"]?.ToString(), out var cartons);
            if (qty <= 0) { AppContainer.Get<DialogService>().Error("أدخل الكمية."); return; }
            string dateStr = dlg.Values["date"]?.ToString() ?? "";
            if (!DatesErp.Core.Common.UiFormat.TryParseDate(dateStr, out var dateVal))
            { AppContainer.Get<DialogService>().Error("تاريخ الإنتاج غير صحيح — أدخله بصيغة يوم/شهر/سنة."); return; }
            DateTime pFrom = StartBox.SelectedDate ?? DateTime.Today, pTo = EndBox.SelectedDate ?? pFrom;
            if (dateVal.Date < pFrom.Date || dateVal.Date > pTo.Date)
            { AppContainer.Get<DialogService>().Error($"تاريخ الإنتاج يجب أن يكون داخل فترة الخطة ({pFrom:dd/MM/yyyy} – {pTo:dd/MM/yyyy})."); return; }
            _rows.Add(new PlanRowUi
            {
                CustomerId = cust.Id, CustomerName = cust.CustomerName,
                LotCode = "—", ShipmentNo = "—", RawName = "—",
                ProductId = prod.Id, ProductName = prod.ProductNameAr, PackName = "-",
                UnitDisplay = prod.UnitOfMeasure ?? "—",
                CartonWeight = prod?.CartonWeightKg ?? 0,
                QtyKg = qty, Cartons = cartons,
                Date = dateVal.ToString("dd/MM/yyyy"),
                ShiftId = shf.Id,
                ShiftName = shf.ShiftNameAr,
                LineName = db.ProductionLines.AsNoTracking().Where(x => x.Id == (LineBox.SelectedIndex >= 0 && LineBox.SelectedIndex < _lineIds.Count ? _lineIds[LineBox.SelectedIndex] : 1)).Select(x => x.LineNameAr).FirstOrDefault() ?? "-",
                LineId = SelectedLineId()
            });
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Manual"); }
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        // §إصلاح: الحذف بالمرجع من DataContext لا برقم الصف. الترقيم يُعاد بعد كل تغيير في
        // المجموعة، فوسم الزر (Tag=No) يصبح قديماً وكان المستخدم يحذف بنداً غير المقصود.
        if (sender is Button b && b.DataContext is PlanRowUi row) _rows.Remove(row);
    }

    private void Renumber()
    {
        int n = 1;
        foreach (var r in _rows) { r.No = n; r.Priority = n; n++; }
    }

    private void Duration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out var days))
        {
            var start = StartBox.SelectedDate ?? DateTime.Today;
            StartBox.SelectedDate = start;
            EndBox.SelectedDate = start.AddDays(days - 1);
        }
    }

    // ══════════ شريط طاقة الوردية ══════════

    private void CapacityInputs_Changed(object sender, EventArgs e) => UpdateCapacityBar();

    /// <summary>
    /// §إصلاح شامل لشريط الطاقة — كان:
    ///  • يأخذ معدل «صنف أول بند» فقط (والخطة متعددة الأصناف بمعدلات مختلفة)
    ///  • يتجاهل العبوة رغم أن الـ Backend يحسب الطاقة لكل عبوة
    ///  • يجمع كراتين كل الأيام ويقارنها بطاقة يوم واحد
    ///  • يتجاهل وردية كل بند
    ///  • يفبرك rate=500 عند غياب التعريف
    ///  • يبتلع الأخطاء بـ catch { }
    /// الآن: يُحسب لكل (يوم × وردية) بمعدل كل صنف وعبوته — نفس منطق EnsureSlotCapacity في الـ Backend.
    /// </summary>
    private void UpdateCapacityBar()
    {
        try
        {
            if (CapacitySummary == null) return;
            if (_rows.Count == 0)
            {
                CapacitySummary.Text = "لا بنود بعد — أضف بنوداً من الخطة لعرض تحميل الطاقة لكل يوم ووردية.";
                RemainingBadge.Text = "";
                RemainingBadge.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x80, 0x00));
                return;
            }

            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var capSvc = scope.ServiceProvider.GetRequiredService<ICapacityService>();
            var hoursByShift = db.Shifts.AsNoTracking().ToList().ToDictionary(x => x.Id, x => x.EffectiveProductiveHours > 0 ? x.EffectiveProductiveHours : 8);
            var shiftNames = db.Shifts.AsNoTracking().ToList().ToDictionary(x => x.Id, x => x.ShiftNameAr);

            // تجميع لكل (يوم × وردية) — بمعدل كل صنف وعبوته
            var slots = new Dictionary<(string day, int shift), (double used, double cap, int cartons)>();
            foreach (var r in _rows)
            {
                string day = string.IsNullOrWhiteSpace(r.Date) ? "بلا تاريخ" : r.Date;
                int shiftId = r.ShiftId > 0 ? r.ShiftId : SelectedShiftId();
                double hours = hoursByShift.TryGetValue(shiftId, out var h) ? h : 8;
                var (rate, cap) = capSvc.GetCapacity(r.ProductId, shiftId, r.PackId);
                if (rate <= 0) rate = cap > 0 ? cap / hours : 500;
                double need = rate > 0 ? r.Cartons / rate : 0;
                var key = (day, shiftId);
                var cur = slots.TryGetValue(key, out var v) ? v : (used: 0.0, cap: hours, cartons: 0);
                slots[key] = (cur.used + need, cur.cap, cur.cartons + r.Cartons);
            }

            var ordered = slots.OrderBy(kv => kv.Key.day).ThenBy(kv => kv.Key.shift).ToList();
            var lines = new List<string>();
            bool anyOver = false;
            foreach (var kv in ordered)
            {
                double rem = Math.Max(0, kv.Value.cap - kv.Value.used);
                bool over = kv.Value.used > kv.Value.cap + 0.0001;
                if (over) anyOver = true;
                string shiftName = shiftNames.TryGetValue(kv.Key.shift, out var sn) ? sn : $"وردية {kv.Key.shift}";
                lines.Add($"{kv.Key.day} · {shiftName}: {kv.Value.cartons:N0} كرتون = {kv.Value.used:N1} س من {kv.Value.cap:N1} س" +
                          (over ? $" ⛔ تجاوز {kv.Value.used - kv.Value.cap:N1} س" : $" · متبقٍ {rem:N1} س"));
            }

            CapacitySummary.Text = string.Join("\n", lines);
            double totalCartons = _rows.Sum(r => r.Cartons);
            double totalHours = ordered.Sum(kv => kv.Value.used);
            RemainingBadge.Text = $"الإجمالي: {totalCartons:N0} كرتون ({totalHours:N1} س) · {(anyOver ? "⛔ يوجد تجاوز للطاقة" : "✅ ضمن الطاقة")}";
            RemainingBadge.Foreground = new System.Windows.Media.SolidColorBrush(
                anyOver ? System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26) : System.Windows.Media.Color.FromRgb(0x00, 0x80, 0x00));
        }
        // §إصلاح: لا ابتلاع صامت — يُسجَّل الخطأ بدل إخفائه
        catch (Exception ex) { Services.ErrorLog.Write(ex, "Planning.CapacityBar"); }
    }

    // ══════════ الحفظ وسير الاعتماد ══════════

    /// <summary>§B58: تحرير خلية (كراتين/تاريخ/وردية/خط) يُحدّث الطاقة والعدادات فوراً.</summary>
    private void RowUi_Changed(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlanRowUi.CartonsText) or nameof(PlanRowUi.Date) or nameof(PlanRowUi.DateValue)
            or nameof(PlanRowUi.ShiftId) or nameof(PlanRowUi.LineId) or nameof(PlanRowUi.PackId) or nameof(PlanRowUi.QtyKg))
        { UpdateCapacityBar(); UpdateTotals(); }
    }

    /// <summary>§B58: عدادات الخطة (بنود/كراتين/وزن/عملاء) + تلميح الجدول الفارغ — كمخطط المرجع.</summary>
    private void UpdateTotals()
    {
        if (TotItemsBox == null) return;
        TotItemsBox.Text = $"البنود: {_rows.Count}";
        TotCartonsBox.Text = $"كراتين: {_rows.Sum(r => r.Cartons):N0}";
        TotQtyBox.Text = $"الوزن: {_rows.Sum(r => r.QtyKg):N1} كجم";
        TotCustsBox.Text = $"عملاء: {_rows.Select(r => r.CustomerId).Distinct().Count()}";
        GridHintPlan.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>§B58: «من استلام مباشر» — فتح نافذة الدفعات المتاحة بلا عميل محدد.</summary>
    private void DirectReceipt_Click(object sender, RoutedEventArgs e) => OpenLotsEditor(null, null);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
            if (string.IsNullOrWhiteSpace(TitleBox.Text)) { AppContainer.Get<DialogService>().Error("أدخل عنوان الخطة."); return; }
            if (_rows.Count == 0) { AppContainer.Get<DialogService>().Error("أضف بنداً واحداً على الأقل (أصناف العميل / أصناف العملاء)."); return; }
            // §B80: فرض تاريخ كل إنتاج — كل بند بتاريخ صالح داخل فترة الخطة (قبل الخلفية أيضاً)
            var perStart = (StartBox.SelectedDate ?? DateTime.Today).Date;
            var perEnd = (EndBox.SelectedDate ?? DateTime.Today).Date;
            foreach (var rowD in _rows)
            {
                if (!Core.Common.UiFormat.TryParseDate(rowD.Date, out var rdD))
                { AppContainer.Get<DialogService>().Error($"البند ({rowD.No}) «{rowD.ProductName}» بلا تاريخ إنتاج — حدّد تاريخ كل بند في عمود «تاريخ الإنتاج»."); return; }
                if (rdD.Date < perStart || rdD.Date > perEnd)
                { AppContainer.Get<DialogService>().Error($"تاريخ البند ({rowD.No}) «{rowD.ProductName}» ({rowD.Date}) خارج فترة الخطة ({perStart:dd/MM/yyyy} ← {perEnd:dd/MM/yyyy})."); return; }
            }

            string ptype = TypeBox.SelectedIndex switch { 0 => "Daily", 1 => "Weekly", 2 => "Monthly", _ => "Period" };
            // §B75: النطاق والعميل المحدد يُحفظان في رأس الخطة
            string scopeMode = SingleRadio.IsChecked == true ? "Single" : "Multi";
            int? singleCustId = scopeMode == "Single"
                ? (SingleCustBox.SelectedItem as DatesErp.Core.Domain.Entities.Customer)?.Id
                : null;
            using var scope = AppContainer.NewScope();
            var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
            var itemsDto = _rows.Select(row => new PlanItemDto
            {
                SourceType = row.LotId != null ? "FromReceiving" : "Manual",
                LotId = row.LotId,
                ShipmentId = row.ShipmentId,
                CustomerId = row.CustomerId,
                ProductId = row.ProductId,
                PackagingTypeId = row.PackId,
                PlannedQtyKg = row.QtyKg,
                PlannedCartons = row.Cartons,
                ScheduledDate = row.Date,
                SuggestedShiftId = row.ShiftId,
                SuggestedLineId = row.LineId,
                PriorityNo = row.Priority
            }).ToList();

            // §تعديل خطة قائمة (مسودة) بدل إنشاء نسخة مكررة — الحفظ يعمل كحفظ وتحديث معاً
            OpResult r = _currentPlanId > 0
                ? svc.UpdatePlan(_currentPlanId, TitleBox.Text, ptype,
                    (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    (EndBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    SelectedShiftId(), SelectedLineId(), itemsDto, NotesBox.Text, scopeMode, singleCustId)
                : svc.SavePlan(TitleBox.Text, ptype,
                    (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    (EndBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    SelectedShiftId(), SelectedLineId(), itemsDto, NotesBox.Text, scopeMode, singleCustId);

            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            _currentPlanId = r.Id;
            CodeBox.Text = r.DocumentNumber;
            FillPlanMeta();
            AppContainer.Get<DialogService>().Info(r.Message + "\nحُفظت البنود كما أدخلتها (عميل/شحنة/دفعة/صنف/عبوة).\nأرسلها للاعتماد للمدير العام عند الجاهزية.");
            SetStatusUI("Draft");
            RefreshPlansList();
            LoadPlanDashboards(_currentPlanId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Save"); }
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlanId == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً."); return; }
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.SubmitPlan(_currentPlanId);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        SetStatusUI("UnderApproval");
        RefreshPlansList();
    }

    /// <summary>§إصلاح: زر «اعتماد الخطة» داخل الشبكة — يحفظ أولاً إن لزم ثم يعتمد.</summary>
    private void ApproveAction_Click(object sender, RoutedEventArgs e)
    {
        // §B58: «تعليق» = حفظ مسودة بلا اعتماد؛ «اعتماد» = حفظ ثم اعتماد/إرسال
        if (HoldRadio.IsChecked == true) { Save_Click(sender, e); return; }
        ApproveAction_ClickCore(sender, e);
    }

    private void ApproveAction_ClickCore(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
        if (_currentPlanId == 0)
        {
            if (!AppContainer.Get<DialogService>().Confirm("الخطة غير محفوظة بعد. هل تريد حفظها ثم اعتمادها؟")) return;
            Save_Click(sender, e);
            if (_currentPlanId == 0) return;   // الحفظ فشل — رسالة الخطأ ظهرت بالفعل
        }
        Approve();
    }

    private void Approve()
    {
        try
        {
            if (_currentPlanId == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً أو اختر خطة من السجل."); return; }
            if (!AppContainer.Get<DialogService>().Confirm("اعتماد الخطة رسمياً ونقلها لأوامر التشغيل؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
            var r = svc.ApprovePlan(_currentPlanId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            SetLocked(true);
            SetStatusUI("Approved");
            RefreshPlansList();
            LoadPlanDashboards(_currentPlanId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Approve"); }
    }

    private void ReturnForRevision_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlanId == 0) return;
        var dlg = new Views.InputDialog("إعادة الخطة للمدير التنفيذي للتعديل", "سبب الإعادة / الملاحظات:") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.ReturnPlan(_currentPlanId, dlg.Value);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        SetLocked(false);
        SetStatusUI("RevisionRequired");
        RefreshPlansList();
    }

    private void Unapprove()
    {
        if (_currentPlanId == 0) return;
        if (!AppContainer.Get<DialogService>().Confirm("إلغاء الاعتماد وإعادة فتح الخطة للتعديل؟")) return;
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.UnapprovePlan(_currentPlanId);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        SetLocked(false);
        SetStatusUI("Draft");
        RefreshPlansList();
    }

    private void DeletePlan()
    {
        if (_currentPlanId == 0) { AppContainer.Get<DialogService>().Error("لا توجد خطة محددة."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف الخطة (المسودة)؟")) return;
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.DeletePlan(_currentPlanId);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        NewPlan();
        RefreshPlansList();
    }

    /// <summary>
    /// §إصلاح — تراجع بوظيفتين حسب المعيار المعلن («جديد ← إفراغ؛ محفوظ ← إعادة آخر نسخة محفوظة — لا حذف أبداً»).
    /// كان يمسح البنود دائماً، فيفقد المستخدم خطة محفوظة من العرض بدل استعادتها.
    /// </summary>
    private void UndoInput()
    {
        if (_currentPlanId > 0)
        {
            OpenPlan(_currentPlanId);   // استعادة آخر نسخة محفوظة
            AppContainer.Get<DialogService>().Info("أُعيدت آخر نسخة محفوظة من الخطة.");
            return;
        }
        _rows.Clear();
        NotesBox.Text = "";
        UpdateCapacityBar();
    }

    private void NewPlan()
    {
        _programmaticScope = true;
        try
        {
            _currentPlanId = 0;
            _rows.Clear();
            _planCustomers.Clear();
            CodeBox.Text = "PLN-تلقائي";
            TitleBox.Text = "";
            NotesBox.Text = "";
            StartBox.SelectedDate = EndBox.SelectedDate = null;
            MultiRadio.IsChecked = true;
            SetLocked(false);
            SetStatusUI("Draft");
            TypeBox_Changed(null, null); // إعادة تطبيق قاعدة «اليومية = تاريخ واحد»
            UpdateCapacityBar();
        }
        finally { _programmaticScope = false; }
    }

    // ══════════ الحالة والقفل ══════════

    private void SetStatusUI(string status)
    {
        string text; System.Windows.Media.Color fg, bg, bd;
        switch (status)
        {
            case "Approved":
                text = "معتمدة ومجدولة 🟢 (مقفل 🔒)";
                fg = System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D);
                bg = System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7);
                bd = System.Windows.Media.Color.FromRgb(0x86, 0xEF, 0xAC);
                break;
            case "UnderApproval":
                text = "بانتظار اعتماد المدير العام ⏳";
                fg = System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E);
                bg = System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7);
                bd = System.Windows.Media.Color.FromRgb(0xFC, 0xD3, 0x4D);
                break;
            case "RevisionRequired":
                text = "معادة للتعديل من المدير العام ↩️";
                fg = System.Windows.Media.Color.FromRgb(0x99, 0x1B, 0x1B);
                bg = System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2);
                bd = System.Windows.Media.Color.FromRgb(0xFC, 0xA5, 0xA5);
                break;
            default:
                text = "مسودة قيد الإعداد 📝";
                fg = System.Windows.Media.Color.FromRgb(0x03, 0x69, 0xA1);
                bg = System.Windows.Media.Color.FromRgb(0xE0, 0xF2, 0xFE);
                bd = System.Windows.Media.Color.FromRgb(0xBA, 0xE6, 0xFD);
                break;
        }
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(fg);
        StatusBanner.Background = new System.Windows.Media.SolidColorBrush(bg);
        StatusBanner.BorderBrush = new System.Windows.Media.SolidColorBrush(bd);

        // مسار المعاملة
        SetStep(Step1, status is "Draft" or "RevisionRequired");
        SetStep(Step2, status == "UnderApproval" || status == "Approved");
        SetStep(Step3, status == "Approved");
        SetStep(Step4, false);

        SubmitBtn.Visibility = status is "Draft" or "RevisionRequired" ? Visibility.Visible : Visibility.Collapsed;
        ReturnBtn.Visibility = status == "UnderApproval" ? Visibility.Visible : Visibility.Collapsed;
        if (_toolbar != null && _toolbar.UnapproveBtn != null)
            _toolbar.UnapproveBtn.Visibility = status == "Approved" ? Visibility.Visible : Visibility.Collapsed;
        // §إصلاح: زر الاعتماد يظهر ما دامت الخطة غير معتمدة — كان مخفياً دائماً فلا اعتماد من الشاشة
        if (_toolbar != null && _toolbar.ApproveBtn != null)
            _toolbar.ApproveBtn.Visibility = status == "Approved" ? Visibility.Collapsed : Visibility.Visible;
        if (_toolbar != null && _toolbar.DeleteBtn != null)
            _toolbar.DeleteBtn.Visibility = status == "Approved" ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetStep(Border step, bool active)
    {
        step.Background = new System.Windows.Media.SolidColorBrush(active
            ? System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7)
            : System.Windows.Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
        step.BorderBrush = new System.Windows.Media.SolidColorBrush(active
            ? System.Windows.Media.Color.FromRgb(0x86, 0xEF, 0xAC)
            : System.Windows.Media.Colors.Transparent);
        if (step.Child is TextBlock tb)
            tb.Foreground = new System.Windows.Media.SolidColorBrush(active
                ? System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D)
                : System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));
    }

    private string PlanTypeKey() => TypeBox.SelectedIndex switch { 0 => "Daily", 1 => "Weekly", 2 => "Monthly", _ => "Period" };

    /// <summary>§B75: عرض منشئ الخطة ومعتمدها وتاريخيهما في الرأس — بيانات محفوظة تُقرأ حية.</summary>
    private void FillPlanMeta()
    {
        if (PlanMetaBox == null) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var plan = db.ProductionPlans.AsNoTracking().FirstOrDefault(pl => pl.Id == _currentPlanId);
            if (plan == null) { PlanMetaBox.Text = "خطة جديدة — لم تُحفظ بعد · أنشأها: — · اعتمدها: —"; return; }
            string NameOf(int? uid) => uid == null ? "—"
                : db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefault() ?? "—";
            PlanMetaBox.Text = $"أنشأها: {NameOf(plan.CreatedBy)} في {plan.CreatedDate:dd/MM/yyyy HH:mm}" +
                               (plan.IsApproved ? $" · اعتمدها: {NameOf(plan.ApprovedBy)} في {plan.ApprovedDate:dd/MM/yyyy HH:mm}" : " · لم تُعتمد بعد");
        }
        catch { }
    }

    private void SetLocked(bool locked)
    {
        _locked = locked;
        StartBox.IsEnabled = !locked;
        EndBox.IsEnabled = !locked;
        TypeBox.IsEnabled = !locked;
        TitleBox.IsEnabled = !locked;
        NotesBox.IsEnabled = !locked;
        ShiftBox.IsEnabled = !locked;
        LineBox.IsEnabled = !locked;
        MultiRadio.IsEnabled = !locked;
        SingleRadio.IsEnabled = !locked;
        SingleCustBox.IsEnabled = !locked;
        if (_toolbar != null)
        {
            if (_toolbar.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = !locked;
            if (_toolbar.NewBtn != null) _toolbar.NewBtn.IsEnabled = !locked;
            if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.IsEnabled = !locked;
            if (_toolbar.DeleteBtn != null) _toolbar.DeleteBtn.IsEnabled = !locked;
        }
        if (SaveActionBtn != null) SaveActionBtn.IsEnabled = !locked;
        if (ApproveActionBtn != null) ApproveActionBtn.IsEnabled = !locked;
    }

    // ══════════ التنقل والسجل ══════════

    private void Nav(int dir)
    {
        if (_planIds.Count == 0) return;
        int idx = _planIds.IndexOf(_currentPlanId);
        idx = dir switch { 0 => 0, int.MaxValue => _planIds.Count - 1, _ => Math.Clamp(idx + dir, 0, _planIds.Count - 1) };
        OpenPlan(_planIds[idx]);
    }

    private void OpenPlan(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var plan = db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == id);
            if (plan == null) return;
            _currentPlanId = plan.Id;
            CodeBox.Text = plan.DocumentNumber;
            TitleBox.Text = plan.PlanTitle;
            NotesBox.Text = plan.Notes;
            StartBox.SelectedDate = plan.StartDate;
            EndBox.SelectedDate = plan.EndDate;
            TypeBox.SelectedIndex = plan.PlanType switch { "Daily" => 0, "Weekly" => 1, "Monthly" => 2, _ => 3 };
            if (plan.ShiftId != null) { int si = _shiftIds.IndexOf(plan.ShiftId.Value); if (si >= 0) ShiftBox.SelectedIndex = si; }
            if (plan.LineId != null) { int li = _lineIds.IndexOf(plan.LineId.Value); if (li >= 0) LineBox.SelectedIndex = li; }
            // §B75: استعادة نطاق التخطيط والعميل المحدد من الرأس
            if (plan.ScopeMode == "Single") SingleRadio.IsChecked = true; else MultiRadio.IsChecked = true;
            if (plan.SingleCustomerId != null)
                for (int ci = 0; ci < SingleCustBox.Items.Count; ci++)
                    if ((SingleCustBox.Items[ci] as DatesErp.Core.Domain.Entities.Customer)?.Id == plan.SingleCustomerId) { SingleCustBox.SelectedIndex = ci; break; }
            FillPlanMeta();

            _rows.Clear();
            foreach (var it in plan.Items.OrderBy(i => i.PriorityNo))
            {
                _rows.Add(new PlanRowUi
                {
                    CustomerId = it.CustomerId,
                    CustomerName = db.Customers.Where(c => c.Id == it.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                    ShipmentId = it.ShipmentId,
                    ShipmentNo = db.Shipments.Where(s => s.Id == it.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() ?? "—",
                    LotId = it.LotId,
                    LotCode = db.Lots.Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                    RawName = db.Lots.Where(l => l.Id == it.LotId).Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => p.ProductNameAr).FirstOrDefault() ?? "—",
                    ProductId = it.ProductId,
                    ProductName = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    PackId = it.PackagingTypeId,
                    PackName = db.PackagingTypes.Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-",
                    UnitDisplay = db.Products.AsNoTracking().Where(pp => pp.Id == it.ProductId).Select(pp => pp.UnitOfMeasure).FirstOrDefault() ?? "—",
                    CartonWeight = db.Products.AsNoTracking().Where(pp => pp.Id == it.ProductId).Select(pp => pp.CartonWeightKg).FirstOrDefault(),
                    QtyKg = it.PlannedQtyKg,
                    Cartons = it.PlannedCartons,
                    DateValue = it.ScheduledDate,
                    ShiftId = it.SuggestedShiftId ?? 1,
                    ShiftName = db.Shifts.Where(s => s.Id == (it.SuggestedShiftId ?? 1)).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-",
                    LineId = it.SuggestedLineId ?? 1,
                    LineName = db.ProductionLines.AsNoTracking().Where(x => x.Id == (it.SuggestedLineId ?? 1)).Select(x => x.LineNameAr).FirstOrDefault() ?? "-",
                    Priority = it.PriorityNo
                });
            }
            // نطاق الخطة: عميل واحد ← يُخفى عمود العميل (محفوظ في رأس النموذج) | عدة عملاء ← يظهر العمود
            // (الحارس يمنع الفتح التلقائي للنافذة أثناء الاسترجاع البرمجي)
            _programmaticScope = true;
            try
            {
                var custIds = plan.Items.Where(i => i.CustomerId != null).Select(i => i.CustomerId).Distinct().ToList();
                if (custIds.Count <= 1)
                {
                    SingleRadio.IsChecked = true;
                    if (custIds.Count == 1)
                    {
                        RefreshCustomerList(); // نضمن وجود القائمة قبل ضبط القيمة
                        SingleCustBox.SelectedValue = custIds[0];
                    }
                }
                else MultiRadio.IsChecked = true;
            }
            finally { _programmaticScope = false; }

            Renumber();
            RowsGrid.Items.Refresh();
            SetLocked(plan.IsApproved);
            SetStatusUI(plan.IsApproved ? "Approved" : plan.Status == "UnderApproval" ? "UnderApproval" : plan.Status == "RevisionRequired" ? "RevisionRequired" : "Draft");
            UpdateCapacityBar();
            LoadPlanDashboards(plan.Id);
            if (plan.StartDate != null) { DailyDateBox.SelectedDate = plan.StartDate; ShowDailyPlan_Click(null, null); }
            // §توحيد الواجهات: عند فتح خطة من البحث، ابدأ العرض من أعلى النموذج ليكون متماسكاً غير منقسم
            // §لم يعد هناك تمرير رأسي — الشاشة كلها معروضة فلا حاجة للعودة للأعلى
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Open"); }
    }

    private void RefreshPlansList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var list = db.ProductionPlans.OrderByDescending(p => p.Id).ToList();
            _planIds = list.Select(p => p.Id).ToList();
            _plans_all = list.Select(p => new
            {
                Id = p.Id,
                DocNo = p.DocumentNumber,
                Title = p.PlanTitle,
                Period = $"{p.StartDate:dd/MM/yyyy} إلى {p.EndDate:dd/MM/yyyy}",
                Customers = db.ProductionPlanItems.Where(i => i.PlanId == p.Id && i.CustomerId != null).Select(i => i.CustomerId).Distinct().Count(),
                Items = db.ProductionPlanItems.Count(i => i.PlanId == p.Id),
                Qty = db.ProductionPlanItems.Where(i => i.PlanId == p.Id).Sum(i => i.PlannedQtyKg),
                StatusAr = p.IsApproved ? "معتمدة 🟢" : p.Status == "UnderApproval" ? "بانتظار الاعتماد ⏳" : p.Status == "RevisionRequired" ? "معادة للتعديل ↩️" : "مسودة 📝"
            }).ToList().Cast<object>().ToList();
            ScreenSearch.Apply(PlansSearchBox, PlansGrid, _plans_all);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.List"); }
    }

    private void PlansGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlansGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(PlansGrid.SelectedItem) is int id)
            OpenPlan(id);
    }

    // ══════════ خطة اليوم وحالة الأيام وتقدم العملاء ══════════

    private void ShowDailyPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var date = DailyDateBox.SelectedDate ?? DateTime.Today;
            using var scope = AppContainer.NewScope();
            var svc = (IPlanProgressService)scope.ServiceProvider.GetService(typeof(IPlanProgressService));
            var rows = svc.GetDailyPlan(date.ToString("dd/MM/yyyy"), _currentPlanId > 0 ? _currentPlanId : null);
            DailyGrid.ItemsSource = rows;
            if (rows.Count == 0)
                AppContainer.Get<DialogService>().Info($"لا توجد بنود مخططة ليوم {date:dd/MM/yyyy}" + (_currentPlanId > 0 ? " في هذه الخطة." : " في أي خطة."));
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Daily"); }
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        DailyDateBox.SelectedDate = DateTime.Today;
        ShowDailyPlan_Click(sender, e);
    }

    private void DailyGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EditRowBtn != null)
            EditRowBtn.Visibility = DailyGrid.SelectedItem != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>§النقر المزدوج على بند اليوم يفتح تعديله مباشرة.</summary>
    private void DailyGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DailyGrid.SelectedItem != null) EditRow_Click(sender, e);
    }

    /// <summary>تعديل بند مستقبلي (تاريخ/كمية/وردية/صنف/عبوة) مع إعادة فحص الطاقة تلقائياً — يدعم اشتراطات العملاء المتغيرة.</summary>
    private void EditRow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DailyGrid.SelectedItem is not PlanRowDto row) return;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var products = scope.ServiceProvider.GetRequiredService<IPlanningService>().GetFinishedProducts();
            var packs = db.PackagingTypes.Where(p => p.IsActive).ToList();
            var fields = new List<Views.FieldDef>
            {
                new() { Key = "date", LabelAr = "التاريخ الجديد (dd/MM/yyyy)", Default = row.Date },
                new() { Key = "qty", LabelAr = "الكمية الجديدة (كجم)", Default = row.PlannedKg.ToString() },
                new() { Key = "shift", LabelAr = "رقم الوردية", Default = row.ShiftId?.ToString() ?? "1" },
                new() { Key = "product", LabelAr = "الصنف التام", Kind = "combo", Options = products.Select(p => p.ProductNameAr).ToArray(), Default = row.ProductName },
                new() { Key = "pack", LabelAr = "العبوة", Kind = "combo", Options = packs.Select(p => p.PackageNameAr).ToArray(), Default = row.PackName }
            };
            var dlg = new Views.EntityFormDialog($"تعديل البند — {row.CustomerName} / {row.ProductName}", fields) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            double? qty = null;
            if (double.TryParse(dlg.Values["qty"]?.ToString(), out var q) && q > 0) qty = q;
            int? shift = null;
            if (int.TryParse(dlg.Values["shift"]?.ToString(), out var sh) && sh > 0) shift = sh;
            // تغيير الصنف/العبوة: يُرسل المعرف الجديد فقط إن غيّر المستخدم الاختيار فعلياً
            int? newProductId = null; int? newPackId = null;
            var prodSel = dlg.Values["product"]?.ToString();
            if (!string.IsNullOrEmpty(prodSel) && prodSel != row.ProductName)
                newProductId = products.FirstOrDefault(p => p.ProductNameAr == prodSel)?.Id;
            var packSel = dlg.Values["pack"]?.ToString();
            if (!string.IsNullOrEmpty(packSel) && packSel != row.PackName)
                newPackId = packs.FirstOrDefault(p => p.PackageNameAr == packSel)?.Id;

            var svc = (IPlanProgressService)scope.ServiceProvider.GetService(typeof(IPlanProgressService));
            var r = svc.UpdatePlanItem(row.ItemId, dlg.Values["date"]?.ToString(), qty, shift, null, newProductId, newPackId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            ShowDailyPlan_Click(sender, e);
            if (_currentPlanId > 0) LoadPlanDashboards(_currentPlanId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.EditRow"); }
    }

    private void LoadPlanDashboards(int planId)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IPlanProgressService)scope.ServiceProvider.GetService(typeof(IPlanProgressService));
            DaysGrid.ItemsSource = svc.GetPlanDayStatuses(planId);
            CustomersProgGrid.ItemsSource = svc.GetPlanProgressByCustomer(planId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Dashboards"); }
    }

    // ══════════ الطباعة والتصدير ══════════

    /// <summary>§طباعة الخطة بنموذج نظامنا: A4 أفقي (Landscape) — ترويسة الشركة + بطاقة الخطة +
    /// ملخص العملاء + بنود مرتبة بالتاريخ (فاصل لكل يوم) + الإجماليات + تذييل الاعتماد —
    /// معاينة إلزامية قبل الطباعة (تكبير/تصغير + تصدير PDF).</summary>
    private void Print()
    {
        try
        {
            if (_currentPlanId == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً قبل الطباعة."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var model = Views.PlanningPrintModel.Load(db, _currentPlanId);
            if (model == null) { AppContainer.Get<DialogService>().Error("تعذر تحميل بيانات الخطة للطباعة."); return; }
            var doc = Views.PlanningPrintDocument.Build(model);
            var preview = new Views.PrintPreviewWindow(doc, $"خطة الإنتاج {model.PlanNumber} — {model.Title}")
            { Owner = Window.GetWindow(this) };
            preview.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Print"); }
    }

    private void Export()
    {
        var report = new ReportResult
        {
            TitleAr = $"خطة الإنتاج — {TitleBox.Text}",
            Columns = new List<string> { "م", "العميل", "الشحنة", "الدفعة", "الصنف", "العبوة", "الكراتين", "الوزن (كجم)", "التاريخ", "الوردية" },
            Rows = _rows.Select(r => new object[] { r.No, r.CustomerName, r.ShipmentNo, r.LotCode, r.ProductName, r.PackName, r.Cartons, r.QtyKg, r.Date, r.ShiftName }).ToList()
        };
        AppContainer.Get<ExportPrintService>().ExportExcel(report);
    }

    /// <summary>§بحث وفلترة لحظية على كل الأعمدة.</summary>
    private void PlansSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ScreenSearch.Apply(PlansSearchBox, PlansGrid, _plans_all);
}

