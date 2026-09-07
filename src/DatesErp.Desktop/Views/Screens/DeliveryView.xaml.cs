using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

public class DelivBalanceRow
{
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    /// <summary>§إصلاح: تُنقل لسند التسليم ليفرض اتساق الكرتون/الكيلو.</summary>
    public int? PackagingTypeId { get; set; }
    public string ProductName { get; set; }
    public string LotCode { get; set; }
    public double Qty { get; set; }
    public int Packages { get; set; }
    /// <summary>§قاعدة الكرتون: وزن الكرتون لاشتقاق الوزن المكافئ عند تعديل الكراتين.</summary>
    public double UnitWeight { get; set; }
    /// <summary>§B105/P5 — نوع العبوة واسم الوحدة من البطاقات (عرض).</summary>
    public string PackName { get; set; }
    public string Unit { get; set; }
    /// <summary>§B105/P4 — حالة فحص البضاعة: ✔ معتمد / ⏳ بانتظار الفحص / ⛔ مرفوض أو محجوز.</summary>
    public string QcStatus { get; set; }
    public bool QcReady { get; set; }
}

/// <summary>تسليم العملاء — من رصيد العميل في مخزن التام فقط (لا تسليم دفعة عميل لعميل آخر، لا تسليم فوق الرصيد).</summary>
public partial class DeliveryView : UserControl
{
    private List<object> _deliveries_all = new();
    private readonly ObservableCollection<DelivBalanceRow> _balances = new();
    private readonly ObservableCollection<DelivBalanceRow> _items = new();
    private List<int> _deliveryIds = new();
    private int _currentId, _currentCustomerId;
    private bool _locked;
    private Views.ErpToolbar _toolbar;

    public DeliveryView()
    {
        InitializeComponent();
        BalanceGrid.ItemsSource = _balances;
        ItemsGrid.ItemsSource = _items;
        // §قاعدة الكرتون: تعديل الكراتين يشتق الوزن المكافئ فوراً
        ItemsGrid.CellEditEnding += (_, e) =>
        {
            if (e.Row?.Item is DelivBalanceRow row && e.Column?.Header?.ToString() == "الكراتين *")
            {
                if (row.Packages < 0) row.Packages = 0;   // §B105/P1 — لا سالب في الشبكة
                row.Qty = Math.Round(row.Packages * (row.UnitWeight > 0 ? row.UnitWeight : 7.5), 1);
                ItemsGrid.Items.Refresh();
            }
        };
        Loaded += (_, _) => Load();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("الشحنات وتسليم العميل");
        chrome.SetScreenCode("MRPINV1005");
        // §1 — الترتيب القياسي الموحد للأزرار الأساسية
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => NewForm(), "سند تسليم جديد (F2)")
            .WithSave((_, _) => Save(), "حفظ سند التسليم — يبقى السند أمامك كما هو (F10)")
            .WithSearch((_, _) => RefreshList(), "بحث في سندات التسليم المحفوظة (F9)")
            .WithUndo((_, _) => UndoSmart(), "تراجع: يلغي الإدخالات غير المحفوظة ويعيد آخر نسخة محفوظة — لا يحذف أي سند")
            .WithApprove((_, _) => Approve(), "🔒 اعتماد التسليم وخصم الرصيد")
            .WithUnapprove((_, _) => Unapprove(), "إلغاء التسليم وإعادة الكميات للرصيد")
            .WithPrint((_, _) => Print(), "طباعة الإذن (Ctrl+P)")
            .WithExcel((_, _) => Export())
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithList((_, _) => RefreshList(), "عرض كل سندات التسليم")
            // §إصلاح: MarkInvoiced كانت خدمة كاملة ومحمية من الفوترة المكررة لكنها غير قابلة
            // للوصول من أي شاشة، فبقي InvoicedQtyKg صفراً دائماً وعمودا «المفوتر/غير المفوتر»
            // في التقارير يعرضان صفراً والإجمالي.
            .WithCustom("💰 تسجيل فوترة", "ErpButton", (_, _) => MarkInvoiced(),
                "تسجيل الكمية المفوترة من السند المعتمد — يمنع تكرار الفوترة لنفس الكمية")
            .WithCustom("🗑 حذف المسودة", "ErpDangerButton", (_, _) => DeleteDraft(),
                "§B105: حذف سند مسودة غير معتمد (لم يخصم شيئاً) — المعتمد لا يُحذف")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        chrome.SetToolbar(_toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            CustomerBox.ItemsSource = db.Customers.Where(c => c.IsActive).ToList();
            // §2 — الشاشة تفتح فارغة في وضع «مستند جديد» — نتائج البحث تظهر عند الضغط على «بحث»
            NewForm();

            // §التنقل من التقارير: فتح سند تسليم محدد فور تحميل الشاشة
            if (MainWindow.PendingDeliveryIdToOpen is int pendingDlv)
            {
                MainWindow.PendingDeliveryIdToOpen = null;
                OpenDelivery(pendingDlv);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Load"); }
    }

    /// <summary>§7/§8 — نتائج البحث في جدول واضح: نقرتان متتاليتان تفتحان السند في هذه الواجهة.</summary>
    private void RefreshList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var list = db.CustomerDeliveries.OrderByDescending(d => d.Id).ToList();
            _deliveryIds = list.Select(d => d.Id).ToList();
            _deliveries_all = list.Select(d => new
            {
                Id = d.Id,
                DocNo = d.DocumentNumber,
                Customer = db.Customers.Where(c => c.Id == d.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                Date = Core.Common.UiFormat.D(d.DeliveryDate),
                Packages = d.TotalCartons,
                Qty = d.TotalQtyKg,
                StatusAr = d.IsApproved ? "معتمد ✅" : "مسودة 🟡"
            }).ToList().Cast<object>().ToList();
            ScreenSearch.Apply(DelivSearchBox, DeliveriesGrid, _deliveries_all);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.List"); }
    }

    /// <summary>§13 — التراجع: سند جديد ← إفراغ؛ سند محفوظ ← إعادة آخر نسخة محفوظة دون حذف.</summary>
    private void UndoSmart()
    {
        if (_currentId > 0) OpenDelivery(_currentId);
        else NewForm();
    }

    private void Customer_Changed(object sender, SelectionChangedEventArgs e)
    {
        var cust = CustomerBox.SelectedItem as Core.Domain.Entities.Customer;
        _balances.Clear();
        if (cust == null) { BalanceChip.Text = "رصيد العميل: —"; return; }
        _currentCustomerId = cust.Id;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var whFg = db.Warehouses.FirstOrDefault(w => w.WarehouseCode == "WFG")?.Id ?? 0;
            var rows = db.StockBalances
                .Where(b => b.WarehouseId == whFg && b.CustomerId == cust.Id && (b.QtyKg > 0 || b.PackageCount > 0))
                .ToList();
            foreach (var b in rows)
            {
                var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == b.ProductId);
                // §B105/P4 — حالة فحص كل سطر رصيد (للعرض والمنع المبكر — القرار النهائي عند الاعتماد)
                var (ready, qcLabel) = DatesErp.Application.Services.QualityGate.DeliveryReadiness(db, b.LotId, b.ProductId ?? 0);
                _balances.Add(new DelivBalanceRow
                {
                    ProductId = b.ProductId ?? 0,
                    LotId = b.LotId,
                    PackagingTypeId = b.PackagingTypeId,
                    ProductName = prod?.ProductNameAr ?? "-",
                    LotCode = db.Lots.Where(l => l.Id == b.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                    Qty = b.QtyKg,
                    Packages = b.PackageCount,
                    UnitWeight = b.PackageCount > 0 ? Math.Round(b.QtyKg / b.PackageCount, 3) : (prod?.CartonWeightKg > 0 ? prod.CartonWeightKg : 7.5),
                    PackName = b.PackagingTypeId != null ? db.PackagingTypes.Where(k => k.Id == b.PackagingTypeId).Select(k => k.PackageNameAr).FirstOrDefault() ?? "—" : "—",
                    Unit = prod?.UnitOfMeasure ?? "—",
                    QcStatus = qcLabel,
                    QcReady = ready
                });
            }
            BalanceChip.Text = $"رصيد العميل: {rows.Sum(r => r.QtyKg):N1} كجم";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Balance"); }
    }

    private void Balance_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
        if (BalanceGrid.SelectedItem is DelivBalanceRow row)
        {
            // §B105/P4 — المرفوض/المحجوز لا يُضاف إطلاقاً؛ وبانتظار الفحص يُضاف بتحذير (الاعتماد سيرفضه)
            if (row.QcStatus != null && row.QcStatus.StartsWith("⛔"))
            { AppContainer.Get<DialogService>().Error($"لا يمكن تسليم هذه البضاعة: {row.QcStatus} — معالجة قرار الجودة أولاً."); return; }
            if (!row.QcReady)
                AppContainer.Get<DialogService>().Info("تنبيه: فحص هذه البضاعة غير معتمد بعد — الاعتماد سيرفض التسليم حتى يُعتمد الفحص.");
            _items.Add(new DelivBalanceRow
            {
                ProductId = row.ProductId,
                LotId = row.LotId,
                PackagingTypeId = row.PackagingTypeId,
                ProductName = row.ProductName,
                LotCode = row.LotCode,
                Qty = row.Qty,
                Packages = row.Packages,
                UnitWeight = row.UnitWeight,
                PackName = row.PackName,
                Unit = row.Unit,
                QcStatus = row.QcStatus,
                QcReady = row.QcReady
            });
        }
    }

    private void DeliverAll_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
        _items.Clear();
        // §B105/P4 — «تسليم كامل المتاح» يشمل المعتمد جاهزاً فقط
        foreach (var b in _balances.Where(b => b.Qty > 0.001 && b.QcReady))
        {
            _items.Add(new DelivBalanceRow
            {
                ProductId = b.ProductId, LotId = b.LotId, PackagingTypeId = b.PackagingTypeId, ProductName = b.ProductName,
                LotCode = b.LotCode, Qty = b.Qty, Packages = b.Packages, UnitWeight = b.UnitWeight,
                PackName = b.PackName, Unit = b.Unit, QcStatus = b.QcStatus, QcReady = b.QcReady
            });
        }
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is DelivBalanceRow row) _items.Remove(row);
    }

    private void Save()
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
            if (_currentCustomerId == 0) { AppContainer.Get<DialogService>().Error("اختر العميل."); return; }
            if (_items.Count == 0) { AppContainer.Get<DialogService>().Error("أضف بنداً من رصيد العميل (نقر مزدوج أو زر تسليم الكامل)."); return; }

            using var scope = AppContainer.NewScope();
            var svc = (ICustomerDeliveryService)scope.ServiceProvider.GetService(typeof(ICustomerDeliveryService));
            // §إصلاح حرج: كان يُمرَّر orderId = null فتتخطى بوابة الجودة كلياً.
            // نشتق أمر الإنتاج من دفعة أول بند إن لم يكن محدداً.
            int? orderId = null;
            {
                using var oscope = AppContainer.NewScope();
                var odb = oscope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var firstLot = _items.FirstOrDefault(i => i.LotId != null)?.LotId;
                if (firstLot != null)
                    orderId = odb.ProductionOrderItems.AsNoTracking()
                        .Where(i => i.LotId == firstLot).OrderBy(i => i.Id)
                        .Select(i => (int?)i.OrderId).FirstOrDefault();
            }
            var itemsDto = _items.Select(i => new CustomerDeliveryItemDto
            {
                ProductId = i.ProductId, LotId = i.LotId, PackagingTypeId = i.PackagingTypeId,
                QtyKg = i.Qty, PackageCount = i.Packages
            }).ToList();
            // §B105/P2 — سند مسودة محفوظ ومفتوح ← تحديث نفس السند (لا إنشاء مكرر)
            OpResult r = _currentId > 0 && !_locked
                ? svc.Update(_currentId, _currentCustomerId, (DateBox.SelectedDate ?? DateTime.Now).ToString("dd/MM/yyyy"), orderId, itemsDto)
                : svc.Save(_currentCustomerId, (DateBox.SelectedDate ?? DateTime.Now).ToString("dd/MM/yyyy"), orderId, itemsDto);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            // §4/§5 — الحفظ ينجح ويبقى السند مفتوحاً في الواجهة كما هو
            bool wasUpdate = _currentId > 0 && _currentId == r.Id && DocNoBox.Text == r.DocumentNumber && DocNoBox.Text != "(تلقائي عند الحفظ)";
            _currentId = r.Id;
            DocNoBox.Text = r.DocumentNumber;
            AppContainer.Get<DialogService>().Info(wasUpdate
                ? $"تم تحديث سند التسليم {r.DocumentNumber} — اعتمده لخصم الكميات."
                : $"تم حفظ سند التسليم رقم: {r.DocumentNumber}\nالسند باقٍ أمامك — اعتمده لخصم الكميات من رصيد العميل.");
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Save"); }
    }

    private void Approve()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("احفظ سند التسليم أولاً."); return; }
            if (!AppContainer.Get<DialogService>().Confirm("الاعتماد سيخصم الكميات نهائياً من رصيد العميل في مخزن التام. متابعة؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (ICustomerDeliveryService)scope.ServiceProvider.GetService(typeof(ICustomerDeliveryService));
            var r = svc.Approve(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            // §5 — السند يبقى في الواجهة بعد الاعتماد (مقفلاً)
            SetLocked(true);
            Customer_Changed(null, null);
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Approve"); }
    }

    private void Unapprove()
    {
        try
        {
            if (_currentId == 0) return;
            if (!AppContainer.Get<DialogService>().Confirm("إلغاء التسليم سيعيد الكميات إلى رصيد العميل. متابعة؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (ICustomerDeliveryService)scope.ServiceProvider.GetService(typeof(ICustomerDeliveryService));
            var r = svc.Unapprove(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            SetLocked(false);
            Customer_Changed(null, null);
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Unapprove"); }
    }

    /// <summary>§B105/P2 — حذف مسودة غير معتمدة (لم تخصم شيئاً) — المعتمد يُرفض في الخدمة.</summary>
    private void DeleteDraft()
    {
        if (_currentId == 0) { AppContainer.Get<DialogService>().Error("لا يوجد سند محفوظ ومفتوح لحذفه."); return; }
        if (_locked) { AppContainer.Get<DialogService>().Error("السند معتمد — لا يُحذف. ألغِ الاعتماد أولاً إن لزم التصحيح."); return; }
        if (!AppContainer.Get<DialogService>().Confirm($"حذف سند التسليم المسودة {DocNoBox.Text}؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (ICustomerDeliveryService)scope.ServiceProvider.GetService(typeof(ICustomerDeliveryService));
            var r = svc.DeleteDraft(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            NewForm();
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.DeleteDraft"); }
    }

    private void NewForm()
    {
        _currentId = 0;
        _items.Clear();
        DocNoBox.Text = "(تلقائي عند الحفظ)";
        DateBox.SelectedDate = DateTime.Now;
        SetLocked(false);
    }

    private void SetLocked(bool locked)
    {
        _locked = locked;
        if (_toolbar != null)
        {
            if (_toolbar.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = !locked;
            if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.IsEnabled = !locked;
            if (_toolbar.UnapproveBtn != null) _toolbar.UnapproveBtn.IsEnabled = locked;
        }
    }

    private void Nav(int dir)
    {
        if (_deliveryIds.Count == 0) return;
        int idx = _deliveryIds.IndexOf(_currentId);
        idx = dir switch { 0 => 0, int.MaxValue => _deliveryIds.Count - 1, _ => Math.Clamp(idx + dir, 0, _deliveryIds.Count - 1) };
        OpenDelivery(_deliveryIds[idx]);
    }

    private void OpenDelivery(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var d = db.CustomerDeliveries.Include(x => x.Items).FirstOrDefault(x => x.Id == id);
            if (d == null) return;
            _currentId = d.Id;
            _currentCustomerId = d.CustomerId;
            CustomerBox.SelectedValue = d.CustomerId;
            DateBox.SelectedDate = d.DeliveryDate;
            DocNoBox.Text = d.DocumentNumber;
            _items.Clear();
            foreach (var it in d.Items)
            {
                // §B105/P3 — العبوة ووزنها والوحدة تُعاد كما حُفظت (كانت تضيع فيُحسب الوزن بـ7.5 افتراضي)
                var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == it.ProductId);
                double unitW = it.PackagingTypeId != null
                    ? db.PackagingTypes.Where(k => k.Id == it.PackagingTypeId).Select(k => k.UnitWeightKg).FirstOrDefault()
                    : 0;
                if (unitW <= 0) unitW = it.CartonWeightKg > 0 ? it.CartonWeightKg : (prod?.CartonWeightKg > 0 ? prod.CartonWeightKg : 7.5);
                _items.Add(new DelivBalanceRow
                {
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    PackagingTypeId = it.PackagingTypeId,
                    ProductName = prod?.ProductNameAr ?? "-",
                    LotCode = db.Lots.Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                    Qty = it.QtyKg,
                    Packages = it.PackageCount,
                    UnitWeight = unitW,
                    PackName = it.PackagingTypeId != null ? db.PackagingTypes.Where(k => k.Id == it.PackagingTypeId).Select(k => k.PackageNameAr).FirstOrDefault() ?? "—" : "—",
                    Unit = prod?.UnitOfMeasure ?? "—"
                });
            }
            SetLocked(d.IsApproved);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Open"); }
    }

    private void Print()
    {
        // §نمط خطط الإنتاج: إذن تسليم رسمي بترويسة وتوقيعات مع معاينة وPDF
        var custName = (CustomerBox?.SelectedItem as Core.Domain.Entities.Customer)?.CustomerName ?? "-";
        var m = new PhaseDocModel
        {
            // §القالب المرجعي print_customer_delivery.html — تصريح خروج من البوابة
            DocTitle = "سند إخراج وتسليم بضاعة للعميل (Gate Pass)",
            DocNo = DocNoBox.Text,
            StatusAr = _currentId > 0 ? "محفوظ" : "مسودة",
            MainTitle = "📦 بنود البضاعة المُخرَجة والمسلَّمة للعميل",
            Columns = new[] { "الصنف", "الدفعة", "الكراتين", "الوزن المكافئ (كجم)" },
            // §القالب المرجعي: تصريح خروج — المستودع والسائق وأمن البوابة
            Signatures = { "أمين مخزن الإنتاج التام WFG", "السائق الناقل / استلام الشحنة", "أمن بوابة المصنع / تصريح الخروج" }
        };
        foreach (var i in _items)
            m.Rows.Add(new object[] { i.ProductName, i.LotCode, i.Packages, i.Qty });
        m.Info.Add(("العميل", custName));
        m.Info.Add(("تاريخ التسليم", DateTime.Now.ToString("dd/MM/yyyy")));
        m.Totals.Add(("إجمالي الكمية (كجم)", _items.Sum(i => i.Qty).ToString("N1")));
        m.Totals.Add(("إجمالي العبوات (كرتون)", _items.Sum(i => i.Packages).ToString("N0")));
        new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}", p => PhasePrint.ExportPdf(m, p))
        { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void Export()
    {
        var report = new ReportResult
        {
            TitleAr = $"إذن تسليم عميل {DocNoBox.Text}",
            Columns = new List<string> { "الصنف", "الدفعة", "الكمية (كجم)", "العبوات" },
            Rows = _items.Select(i => new object[] { i.ProductName, i.LotCode, i.Qty, i.Packages }).ToList()
        };
        AppContainer.Get<ExportPrintService>().ExportExcel(report);
    }

    private void DeliveriesGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DeliveriesGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(DeliveriesGrid.SelectedItem) is int id)
            OpenDelivery(id);
    }

    /// <summary>§بحث وفلترة لحظية على كل الأعمدة.</summary>
    /// <summary>§إصلاح: تسجيل الفوترة — الخدمة كانت موجودة بلا واجهة.</summary>
    private void MarkInvoiced()
    {
        if (_currentId == 0) { AppContainer.Get<DialogService>().Error("افتح سند تسليم معتمداً أولاً."); return; }
        var dlg = new Views.InputDialog("تسجيل فوترة", "الكمية المفوترة (كجم):") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (!double.TryParse(dlg.Value, out var qty) || qty <= 0)
        { AppContainer.Get<DialogService>().Error("أدخل كمية صحيحة أكبر من صفر."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
            var r = svc.MarkInvoiced(_currentId, qty);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Invoice"); }
    }

    private void DelivSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ScreenSearch.Apply(DelivSearchBox, DeliveriesGrid, _deliveries_all);
}

