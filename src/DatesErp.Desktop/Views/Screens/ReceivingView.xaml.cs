using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>شاشة استلام التمور — مطابقة لسند الاستلام المعتمد (شحنة ← اعتماد ← دفعات).</summary>
public partial class ReceivingView : UserControl
{
    private List<object> _ship_all = new();
    private class ItemRow
    {
        public int RowNo { get; set; }
        public int ProductId { get; set; }
        public int? PackId { get; set; }
        public string ProductCode { get; set; }
        public string ProductName { get; set; }
        public string PackName { get; set; }
        /// <summary>§نظام الوحدات: وحدة الاستلام الأصلية كما وصلت — والكمية القياسية كجم.</summary>
        public string ReceiptUnit { get; set; }
        public int PackageCount { get; set; }
        public double UnitWeightKg { get; set; }
        public double QtyKg { get; set; }
        /// <summary>§استلام جزئي: مستلم | مرفوض/تالف | معلّق لاحقاً.</summary>
        public string Status { get; set; } = "مستلم";
    }

    private static string StatusToCode(string ar) => ar switch
    { "مرفوض/تالف" => "Rejected", "معلّق لاحقاً" => "Pending", "Moved" => "Moved", _ => "Received" };
    private static string CodeToStatus(string code) => code switch
    { "Rejected" => "مرفوض/تالف", "Pending" => "معلّق لاحقاً", _ => "مستلم" };

    private readonly ObservableCollection<ItemRow> _items = new();
    private List<int> _shipmentIds = new();
    private int _currentId;
    private bool _locked;
    private bool _approved;
    /// <summary>§يمنع التعبئة التلقائية أثناء فتح سند محفوظ (لا تُكتب فوق بيانات السند).</summary>
    private bool _loadingDocument;
    /// <summary>§28 — حالة الشاشة: جديد / عرض / تعديل.</summary>
    private string _mode = "New";
    private Views.ErpToolbar _toolbar;

    public ReceivingView()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _items;
        _items.CollectionChanged += (_, _) =>
        {
            int n = 1; foreach (var i in _items) i.RowNo = n++;
            GrandTotal.Text = $"{_items.Sum(i => i.QtyKg):N1} كجم";
            ItemsCount.Text = _items.Count.ToString();
            PackagesTotal.Text = _items.Sum(i => i.PackageCount).ToString("N0");
            ContainerSummary.Text = string.IsNullOrWhiteSpace(ContainerBox.Text) ? "" : $"🚢 الحاوية: {ContainerBox.Text}";
            ItemsGrid.Items.Refresh();
        };
        Loaded += (_, _) => Load();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("استلام الشحنات");
        chrome.SetScreenCode("MRPREC1001");
        chrome.SetToolbar(BuildToolbar());
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private Views.ErpToolbar BuildToolbar()
    {
        // §1 — الترتيب القياسي الموحد: جديد/حفظ/بحث/تعديل/تراجع/طباعة ثم الإضافات حسب طبيعة الشاشة
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => NewForm(), "أمر استلام جديد (F2)")
            .WithSave((_, _) => Save(), "حفظ أمر الاستلام — يبقى السند أمامك كما هو (F10)")
            .WithSearch((_, _) => OpenSearchWindow(), "بحث في سندات الاستلام (F9)")
            .WithEdit((_, _) => EditDocument())
            .WithUndo((_, _) => Undo(), "تراجع: يلغي الإدخالات غير المحفوظة ويعيد آخر نسخة محفوظة — لا يحذف أي مستند")
            .WithPrint((_, _) => Print(), "طباعة السند (Ctrl+P)")
            .WithDelete((_, _) => Delete())
            .WithApprove((_, _) => Approve(), "🔒 اعتماد وإنشاء الدفعات")
            .WithCustom("📥 استلام المتبقي", "ErpButton", (_, _) => ReceiveRemainingClick())
            .WithUnapprove((_, _) => Unapprove())
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithList((_, _) => RefreshList(), "عرض كل سندات الاستلام")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        return _toolbar;
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            CustomerBox.ItemsSource = db.Customers.Where(c => c.IsActive).ToList();
            EmployeeBox.ItemsSource = db.Employees.ToList();
            // §B80: قائمة الوحدات تُقرأ من شاشة الوحدات — مزامنة تلقائية: كل وحدة نشطة
            // يقابلها نوع عبوة بالاسم نفسه (يُنشأ إن غاب) فتبقى المعرفات والوثائق سليمة.
            try { scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.MasterDataService>().SyncPackagingFromUnits(); }
            catch { /* المزامنة تيسيرية — القائمة الكاملة أدناه تبقى احتياطاً */ }
            var unitNames = db.UnitsOfMeasure.AsNoTracking().Where(u => u.IsActive)
                .Select(u => u.UnitNameAr).ToList();
            var allPacks = db.PackagingTypes.ToList();
            var packsFromUnits = allPacks.Where(pk => unitNames.Contains(pk.PackageNameAr)).ToList();
            PackBox.ItemsSource = packsFromUnits.Count > 0 ? packsFromUnits : allPacks;
            // §المخازن المتعددة: مخازن الخام النشطة (رئيسي / خام 2 / ثلاجة...) — الافتراضي WRM أولًا
            WarehouseBox.ItemsSource = db.Warehouses
                .Where(w => w.IsActive && w.WarehouseType == "Raw")
                .OrderBy(w => w.WarehouseCode == "WRM" ? 0 : 1).ThenBy(w => w.Id).ToList();
            WarehouseBox.SelectedValue = db.Warehouses.Where(w => w.WarehouseCode == "WRM").Select(w => w.Id).FirstOrDefault();
            // §الاستلام للخام مباشرة — بلا حقل مجموعة: أصناف المجموعة 001 أو بلا مجموعة
            ProductBox.ItemsSource = db.Products
                .Where(p => p.IsActive && p.ItemType == "Raw")   // §B74: الخامات فقط حسب تصنيف شاشة الأصناف
                .OrderBy(p => p.ProductNameAr).ToList();
            // §2 — الشاشة تفتح فارغة في وضع «مستند جديد» — لا عرض تلقائي لآخر سجل
            NewForm();

            // §التنقل من التقارير: فتح سند استلام محدد فور تحميل الشاشة
            if (MainWindow.PendingShipmentIdToOpen is int pendingShip)
            {
                MainWindow.PendingShipmentIdToOpen = null;
                OpenShipment(pendingShip);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Load"); }
    }

    /// <summary>§7 — نافذة البحث الموحدة: نقرتان على أي سند تعيده كاملاً إلى هذه الواجهة.</summary>
    private void ReceiveRemainingClick()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("افتح سنداً معتمداً له بنود معلّقة أولاً."); return; }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IReceivingService>();
            var r = svc.ReceiveRemaining(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            OpenShipment(r.Id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Remaining"); }
    }

    private void OpenSearchWindow()
    {
        try
        {
            using (var scope = AppContainer.NewScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var customerNames = db.Customers.AsNoTracking().OrderBy(c => c.CustomerName).Select(c => c.CustomerName).ToList();
                var win = new DocSearchWindow("سندات الاستلام",
                    new List<SearchFieldDef>
                    {
                        new() { Key = "doc", LabelAr = "رقم السند" },
                        new() { Key = "customer", LabelAr = "العميل", Kind = "combo", Options = customerNames.ToArray() },
                        new() { Key = "from", LabelAr = "من تاريخ", Kind = "date" },
                        new() { Key = "to", LabelAr = "إلى تاريخ", Kind = "date" }
                    },
                    cond => SearchShipments(cond));
                win.Owner = Window.GetWindow(this);
                if (win.ShowDialog() == true && win.SelectedId != null)
                    OpenShipment(win.SelectedId.Value);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Search"); }
    }

    private SearchResult SearchShipments(Dictionary<string, string> cond)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var q = db.Shipments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(cond.GetValueOrDefault("doc")))
            q = q.Where(s => s.DocumentNumber.Contains(cond["doc"].Trim()));
        if (!string.IsNullOrWhiteSpace(cond.GetValueOrDefault("customer")))
            q = q.Where(s => db.Customers.Any(c => c.Id == s.CustomerId && c.CustomerName == cond["customer"]));
        if (DateTime.TryParseExact(cond.GetValueOrDefault("from"), Core.Common.UiFormat.DatePattern, null, System.Globalization.DateTimeStyles.None, out var from))
            q = q.Where(s => s.ReceivedDate >= from);
        if (DateTime.TryParseExact(cond.GetValueOrDefault("to"), Core.Common.UiFormat.DatePattern, null, System.Globalization.DateTimeStyles.None, out var to))
            q = q.Where(s => s.ReceivedDate <= to.AddDays(1));

        var result = new SearchResult
        {
            Columns = new List<string> { "رقم السند", "التاريخ", "العميل", "الوزن (كجم)", "الحالة" }
        };
        // §B80: ToList قبل الحلقة — استعلام اسم العميل داخل الحلقة كان يفتح قارئاً ثانياً
        // على SQL Server (بلا MARS) فتظهر «رسالة خطأ» وتبقى نافذة البحث فارغة.
        var ships = q.OrderByDescending(x => x.Id).ToList();
        var custNames = db.Customers.AsNoTracking().Select(c => new { c.Id, c.CustomerName }).ToList()
            .ToDictionary(c => c.Id, c => c.CustomerName);
        foreach (var s in ships)
        {
            result.Rows.Add((s.Id, new object[]
            {
                s.DocumentNumber,
                Core.Common.UiFormat.D(s.ReceivedDate),
                custNames.TryGetValue(s.CustomerId, out var cn) ? cn ?? "-" : "-",
                Core.Common.UiFormat.N(s.TotalWeightKg),
                s.IsApproved ? "معتمد" : "مسودة"
            }));
        }
        return result;
    }

    /// <summary>§12 — تعديل مستند محفوظ: يحول الحقول إلى وضع التحرير حسب الحالة والصلاحيات.</summary>
    private void EditDocument()
    {
        if (_currentId == 0) { AppContainer.Get<DialogService>().Error("لا يوجد مستند محفوظ للتعديل — اضغط «جديد» لإنشاء سند."); return; }
        if (_approved) { AppContainer.Get<DialogService>().Error(Core.Common.UiFormat.MsgLocked + "\nالسند معتمد — ألغِ الاعتماد أولاً (حسب صلاحيتك)."); return; }
        _mode = "Edit";
        ApplyMode();
    }

    /// <summary>§13 — التراجع: جديد ← إفراغ النموذج؛ محفوظ ← إلغاء التعديلات غير المحفوظة والعودة لآخر نسخة محفوظة.</summary>
    private void Undo()
    {
        if (_currentId > 0) OpenShipment(_currentId);
        else NewForm();
    }

    /// <summary>§28 — تطبيق حالة الشاشة على الحقول والأزرار.</summary>
    private void ApplyMode()
    {
        bool editable = !_approved && (_mode == "New" || _mode == "Edit");
        _locked = !editable;
        CustomerBox.IsEnabled = editable;
        ArrivalDate.IsEnabled = editable;
        ReceivedDate.IsEnabled = editable;
        EmployeeBox.IsEnabled = editable;
        ContainerBox.IsEnabled = editable;
        WarehouseBox.IsEnabled = editable;
        NotesBox.IsEnabled = editable;
        AddItemBtn.IsEnabled = editable;
        LockBanner.Visibility = _approved ? Visibility.Visible : Visibility.Collapsed;
        if (_toolbar != null)
        {
            if (_toolbar.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = editable;
            if (_toolbar.EditBtn != null) _toolbar.EditBtn.IsEnabled = !_approved && _mode == "View";
            if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.IsEnabled = !_approved && _currentId > 0;
            if (_toolbar.UnapproveBtn != null) _toolbar.UnapproveBtn.IsEnabled = _approved;
            if (_toolbar.DeleteBtn != null) _toolbar.DeleteBtn.IsEnabled = !_approved && _currentId > 0;
        }
        DocState.Text = _mode switch
        {
            "New" => "حالة المستند: مستند جديد — أدخل البيانات ثم اضغط حفظ",
            "View" => _approved
                ? $"حالة المستند: السند رقم {DocNoBox.Text} — معتمد 🔒 (عرض فقط)"
                : $"حالة المستند: السند رقم {DocNoBox.Text} — محفوظ (عرض) — اضغط «تعديل» لإجراء تغييرات",
            _ => $"حالة المستند: السند رقم {DocNoBox.Text} — وضع التعديل — احفظ التغييرات أو اضغط «تراجع» لإلغائها"
        };
    }

    /// <summary>§كشف تكرار رقم الحاوية: تحذير استباقي فور مغادرة الحقل — التكرار ممكن لكن بعلم المستخدم.</summary>
    private void ContainerBox_LostFocus(object sender, RoutedEventArgs e) => CheckDuplicateContainer(silent: true);

    private bool CheckDuplicateContainer(bool silent = false)
    {
        DuplicateWarn.Visibility = Visibility.Collapsed;
        var cn = ContainerBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(cn) || cn.Length < 3) return true;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var dup = svc.FindDuplicateContainers(cn, _currentId > 0 ? _currentId : null);
            if (dup.Count == 0) return true;
            var lines = string.Join("\n", dup.Take(3).Select(d =>
                $"• {d.DocumentNumber} — {d.CustomerName} — {Core.Common.UiFormat.D(d.ReceivedDate)} — {d.TotalWeightKg:N0} كجم {(d.IsApproved ? "(معتمد)" : "(مسودة)")}"));
            var msg = $"⚠ رقم الحاوية «{cn}» ورد سابقاً في:\n{lines}{(dup.Count > 3 ? $"\n... و{dup.Count - 3} سندات أخرى" : "")}\n\nتأكد أن هذا ليس استلاماً مكرراً لنفس الحاوية.";
            DuplicateWarn.Text = "⚠ " + msg.Split('\n')[1];
            DuplicateWarn.Visibility = Visibility.Visible;
            if (!silent && !AppContainer.Get<DialogService>().Confirm(msg + "\n\nهل تريد المتابعة بالحفظ على أي حال؟"))
                return false;
            return true;
        }
        catch { return true; } // فشل الفحص لا يمنع العمل
    }

    /// <summary>§نظامنا: اختيار الصنف ← عبوته الافتراضية + وزنها تلقائياً (بطاقة الصنف → الاستلام).</summary>
    private void ProductBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_loadingDocument) return; // أثناء فتح سند لا نعيد التعبئة
            var p = ProductBox.SelectedItem as Core.Domain.Entities.Product;
            if (p == null) return;
            // العبوة الافتراضية للصنف — وإلا أول عبوة نشطة
            if (p.DefaultPackagingTypeId is int dp && (PackBox.ItemsSource as List<Core.Domain.Entities.PackagingType>)?.Any(x_ => x_.Id == dp) == true)
                PackBox.SelectedValue = dp;
            else if (PackBox.SelectedIndex < 0 && PackBox.Items.Count > 0)
                PackBox.SelectedIndex = 0;
        }
        catch { /* لا تُعطّل الإدخال */ }
    }

    /// <summary>§نظامنا: اختيار العبوة ← وزن العبوة يُعبأ تلقائياً (مع سماح التعديل اليدوي).</summary>
    private void PackBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_loadingDocument) return;
            if (PackBox.SelectedItem is Core.Domain.Entities.PackagingType pk && pk.UnitWeightKg > 0)
            {
                
                UnitWeightBox.Text = pk.UnitWeightKg.ToString("0.##");
                
                Calc_Changed(null, null);
            }
        }
        catch { }
    }

    private void Calc_Changed(object sender, TextChangedEventArgs e)
    {
        if (CalcTotalBox == null) return;
        int.TryParse(PkgCountBox.Text, out var c);
        double.TryParse(UnitWeightBox.Text, out var w);
        CalcTotalBox.Text = $"{c * w:N1} كجم";
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
        var product = ProductBox.SelectedItem as Core.Domain.Entities.Product;
        var pack = PackBox.SelectedItem as Core.Domain.Entities.PackagingType;
        if (product == null) { AppContainer.Get<DialogService>().Error("اختر الصنف."); return; }
        if (!int.TryParse(PkgCountBox.Text, out var count) || count <= 0) { AppContainer.Get<DialogService>().Error("أدخل العدد."); return; }
        if (!double.TryParse(UnitWeightBox.Text, out var uw) || uw <= 0) { AppContainer.Get<DialogService>().Error("أدخل الوزن."); return; }

        _items.Add(new ItemRow
        {
            RowNo = _items.Count + 1,
            ProductId = product.Id,
            PackId = pack?.Id,
            ProductCode = product.ProductCode,
            ProductName = product.ProductNameAr,
            PackName = pack?.PackageNameAr ?? "-",
            ReceiptUnit = pack?.PackageNameAr ?? "كرتون",
            PackageCount = count,
            UnitWeightKg = uw,
            QtyKg = count * uw
        });
        PkgCountBox.Text = "0"; UnitWeightBox.Text = "0"; CalcTotalBox.Text = "";
    }

    /// <summary>حذف بند من بنود الشحنة قبل الحفظ.</summary>
    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
        if (sender is Button b && b.Tag is ItemRow row) _items.Remove(row);
    }

    private void Save()
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
            var cust = CustomerBox.SelectedItem as Core.Domain.Entities.Customer;
            if (cust == null) { AppContainer.Get<DialogService>().Error("اختر العميل المورد."); return; }
            if (_items.Count == 0) { AppContainer.Get<DialogService>().Error("أضف بنداً واحداً على الأقل."); return; }
            if (!CheckDuplicateContainer()) return; // §تحذير صارم قبل الحفظ

            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var emp = EmployeeBox.SelectedItem as Core.Domain.Entities.Employee;
            var r = svc.SaveShipment(cust.Id,
                ArrivalDate.SelectedDate?.ToString("dd/MM/yyyy"),
                (ReceivedDate.SelectedDate ?? DateTime.Now).ToString("dd/MM/yyyy"),
                _items.Select(i => new ShipmentItemDto
                {
                    ProductId = i.ProductId,
                    PackagingTypeId = i.PackId,
                    PackageCount = i.PackageCount,
                    UnitWeightKg = i.UnitWeightKg,
                    QtyKg = i.QtyKg,
                    ReceiptUnit = i.ReceiptUnit,
                    ItemStatus = StatusToCode(i.Status)
                }).ToList(),
                NotesBox.Text, ContainerBox.Text, emp?.Id,
                _currentId > 0 ? _currentId : null,
                WarehouseBox.SelectedValue as int?);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            // §4/§5 — الحفظ ينجح ويبقى نفس المستند مفتوحاً في الواجهة كما هو
            _currentId = r.Id;
            DocNoBox.Text = r.DocumentNumber;
            _approved = false;
            _mode = "View";
            ApplyMode();
            AppContainer.Get<DialogService>().Info($"تم حفظ سند الاستلام رقم: {r.DocumentNumber}\nالمستند باقٍ أمامك — يمكنك طباعته أو تعديله أو اعتماده.");
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Save"); }
    }

    private void Approve()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("احفظ سند الاستلام أولاً."); return; }
            if (!AppContainer.Get<DialogService>().Confirm("سيتم اعتماد الاستلام وإنشاء الدفعات وتقييد الوارد في مخزن الخام. متابعة؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var r = svc.ApproveShipment(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _approved = true; _mode = "View"; ApplyMode();
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Approve"); }
    }

    private void Unapprove()
    {
        try
        {
            if (_currentId == 0) return;
            if (!AppContainer.Get<DialogService>().Confirm("إلغاء الاعتماد سيعكس الدفعات والأرصدة. متابعة؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var r = svc.UnapproveShipment(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _approved = false; _mode = "View"; ApplyMode();
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Unapprove"); }
    }

    private void Delete()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("لا يوجد سند محدد."); return; }
            if (!AppContainer.Get<DialogService>().Confirm("حذف سند الاستلام (مسودة)؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var r = svc.DeleteShipment(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshList();
            NewForm();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Delete"); }
    }

    private void NewForm()
    {
        _currentId = 0;
        _approved = false;
        _mode = "New";
        _items.Clear();
        CustomerBox.SelectedIndex = -1;
        EmployeeBox.SelectedIndex = -1;
        ArrivalDate.SelectedDate = null;
        ReceivedDate.SelectedDate = DateTime.Now;
        ContainerBox.Text = ""; NotesBox.Text = "";
        DuplicateWarn.Visibility = Visibility.Collapsed;
        var wrm = (WarehouseBox.ItemsSource as System.Collections.Generic.List<Core.Domain.Entities.Warehouse>)?.FirstOrDefault(w => w.WarehouseCode == "WRM");
        if (wrm != null) WarehouseBox.SelectedValue = wrm.Id;
        DocNoBox.Text = "(تلقائي عند الحفظ)";
        ApplyMode();
    }

    private void Nav(int dir)
    {
        if (_shipmentIds.Count == 0) return;
        int idx = _shipmentIds.IndexOf(_currentId);
        idx = dir switch
        {
            0 => 0,
            int.MaxValue => _shipmentIds.Count - 1,
            _ => Math.Clamp(idx + dir, 0, _shipmentIds.Count - 1)
        };
        OpenShipment(_shipmentIds[idx]);
    }

    private void OpenShipment(int id)
    {
        try
        {
            _loadingDocument = true;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var ship = db.Shipments.Include(s => s.Items).FirstOrDefault(s => s.Id == id);
            if (ship == null) return;
            _currentId = ship.Id;
            DocNoBox.Text = ship.DocumentNumber;
            CustomerBox.SelectedValue = ship.CustomerId;
            ArrivalDate.SelectedDate = ship.ArrivalDate;
            ReceivedDate.SelectedDate = ship.ReceivedDate;
            EmployeeBox.SelectedValue = ship.ReceivedBy;
            ContainerBox.Text = ship.ContainerNumber ?? "";
            NotesBox.Text = ship.Notes ?? "";
            // §استعادة مخزن الاستلام المحفوظ (فارغ = الافتراضي WRM للسندات القديمة)
            WarehouseBox.SelectedValue = ship.ReceivingWarehouseId ??
                db.Warehouses.Where(w => w.WarehouseCode == "WRM").Select(w => w.Id).FirstOrDefault();
            _items.Clear();
            foreach (var it in ship.Items)
            {
                _items.Add(new ItemRow
                {
                    ProductId = it.ProductId,
                    PackId = it.PackagingTypeId,
                    ProductCode = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductCode).FirstOrDefault() ?? "-",
                    ProductName = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    PackName = db.PackagingTypes.Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-",
                    ReceiptUnit = it.ReceiptUnit ?? "كرتون",
                    PackageCount = it.PackageCount,
                    UnitWeightKg = it.UnitWeightKg,
                    QtyKg = it.TotalWeightKg,
                    Status = CodeToStatus(it.Status)
                });
            }
            // §10 — المستند يعود كما حُفظ بالضبط ويظهر كاملاً في الواجهة الرئيسية
            _approved = ship.IsApproved;
            _mode = "View";
            ApplyMode();
            _loadingDocument = false;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Open"); }
    }

    private void RefreshList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var list = db.Shipments.OrderByDescending(s => s.Id).ToList();
            _shipmentIds = list.Select(s => s.Id).ToList();
            _ship_all = list.Select(s => new
            {
                Id = s.Id,
                DocNo = s.DocumentNumber,
                Customer = db.Customers.Where(c => c.Id == s.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                Date = Core.Common.UiFormat.D(s.ReceivedDate),
                Weight = s.TotalWeightKg,
                StatusAr = s.IsApproved ? "معتمد 🟢" : "مسودة 🟡"
            }).ToList().Cast<object>().ToList();
            ScreenSearch.Apply(ShipsSearchBox, ShipGrid, _ship_all);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.List"); }
    }

    /// <summary>§الطباعة الرسمية: سند A4 كامل (ترويسة الشركة + الشعار + البيانات + البنود + الإجماليات + التوقيعات)
    /// مع معاينة إلزامية قبل الطباعة (تكبير/تصغير + تصدير PDF) — لا طباعة مباشرة بلا مراجعة.</summary>
    private void Print()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("احفظ السند أولاً قبل الطباعة — الطباعة تُنفَّذ من بيانات محفوظة."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var model = Views.ReceivingPrintModel.Load(db, _currentId);
            if (model == null) { AppContainer.Get<DialogService>().Error("تعذر تحميل بيانات السند للطباعة."); return; }
            var doc = Views.ReceivingPrintDocument.Build(model);
            var preview = new Views.PrintPreviewWindow(doc, $"سند استلام {model.DocumentNumber}",
                pdfPath => Views.ReceivingPrintPdf.Export(model, pdfPath))
            { Owner = Window.GetWindow(this) };
            preview.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Print"); }
    }

    private void FocusSearch() => ShipGrid.Focus();

    private void ShipGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ShipGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(ShipGrid.SelectedItem) is int id)
            OpenShipment(id);
    }

    /// <summary>§بحث وفلترة لحظية على كل الأعمدة.</summary>
    private void ShipsSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ScreenSearch.Apply(ShipsSearchBox, ShipGrid, _ship_all);
}

