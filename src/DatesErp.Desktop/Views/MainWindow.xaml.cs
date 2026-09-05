using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DatesErp.Core.Domain.Enums;
using DatesErp.Desktop.Screens;
using DatesErp.Desktop.Views.Screens;
using DatesErp.Desktop.Services;
using Microsoft.EntityFrameworkCore;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Session;

namespace DatesErp.Desktop.Views;

/// <summary>§24 — الصدفة الرئيسية المطابقة للتصميم المعتمد: شريط علوي كحلي + قائمة إدارات + مسار + حالة.</summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock;
    private string _currentScreen = "dashboard";

    /// <summary>§معرف خطة مطلوب فتحه فور فتح شاشة التخطيط (يُستهلك مرة واحدة).</summary>
    public static int? PendingPlanIdToOpen;
    /// <summary>§التنقل من التقارير: معرفات مستندات تُفتح فور تحميل شاشاتها.</summary>
    public static int? PendingShipmentIdToOpen;
    public static int? PendingOrderIdToOpen;
    public static int? PendingCheckIdToOpen;
    public static int? PendingFGIdToOpen;
    public static int? PendingDeliveryIdToOpen;

    public MainWindow()
    {
        InitializeComponent();
        // §ختم البناء: يظهر في شريط العنوان لمعرفة أي نسخة تعمل فعلاً على جهاز المستخدم
        Title = "DateERP — إصدار " + Services.BuildInfo.Stamp;
        BuildNav();
        UpdateStatus();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => TopClock.Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        _clock.Start();
        Closed += (_, _) => { _clock.Stop(); _idleTimer?.Stop(); };

        // §لمسة مؤسسية: مهلة خمول الجلسة — خروج تلقائي بعد مدة قابلة للضبط (افتراضي 30 دقيقة).
        // أي حركة فأرة أو لوحة مفاتيح تُصفّر المؤقت.
        _lastActivity = DateTime.Now;
        PreviewMouseDown += (_, _) => _lastActivity = DateTime.Now;
        PreviewKeyDown += (_, _) => _lastActivity = DateTime.Now;
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();

        KeyDown += MainWindow_KeyDown;
        Loaded += (_, _) => OpenScreen("dashboard");
    }

    /// <summary>
    /// §اختصارات احترافية عامة (كالأنظمة الكبيرة):
    /// F2 جديد | F3 تعديل | F5 تحديث | F8 حذف | F9 بحث | F10 حفظ | Ctrl+P طباعة
    /// F12 أو Ctrl+K: الفتح السريع للشاشات.
    /// </summary>
    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.F5) { ReloadCurrent(); e.Handled = true; return; }
            if (e.Key == Key.F12 || (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control))
            { ShowQuickOpen(); e.Handled = true; return; }

            var tb = (ContentArea.Content as ErpChrome)?.CurrentToolbar;
            if (tb == null) return;
            Button target = e.Key switch
            {
                Key.F2 => tb.NewBtn,
                Key.F3 => tb.EditBtn,
                Key.F8 => tb.DeleteBtn,
                Key.F9 => tb.SearchBtn,
                Key.F10 => tb.SaveBtn,
                Key.P when Keyboard.Modifiers == ModifierKeys.Control => tb.PrintBtn,
                _ => null
            };
            if (target != null && target.IsEnabled && target.Visibility == Visibility.Visible)
            {
                target.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                e.Handled = true;
            }
        }
        catch { /* الاختصارات لا تعطل العمل */ }
    }

    /// <summary>§الفتح السريع: بحث فوري في كل شاشات النظام والانتقال لأي شاشة بضغطة (F12 / Ctrl+K).</summary>
    private void ShowQuickOpen()
    {
        var win = new QuickOpenWindow { Owner = this };
        if (win.ShowDialog() == true && win.SelectedCode != null)
            OpenScreen(win.SelectedCode);
    }

    private void BuildNav()
    {
        // الإدارات السبع المعتمدة مع عداد الشاشات
        foreach (var dept in ScreenCatalog.Departments)
        {
            var count = ScreenCatalog.All.Count(s => s.Group == dept.Title);
            var item = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 3),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                Tag = dept.Id
            };
            var navText = new TextBlock
            {
                Text = $"{dept.Icon}  {dept.Title}",
                Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                FontSize = 13, FontWeight = FontWeights.SemiBold
            };
            DockPanel.SetDock(navText, Dock.Right);
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(7, 2, 7, 2),
                MinWidth = 22,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = count.ToString(),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
                    FontSize = 11, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            DockPanel.SetDock(badge, Dock.Left);
            var navPanel = new DockPanel();
            navPanel.Children.Add(navText);
            navPanel.Children.Add(badge);
            item.Child = navPanel;
            item.MouseEnter += (_, _) => item.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, _) => OpenDeptDashboardPublic(dept.Id);
            DeptNavPanel.Children.Add(item);
        }

        // الروابط الفرعية (المعتمدة)
        AddSubItem("•  مركز التقارير الموحد", () => OpenScreen("reports"));
        AddSubItem("▶  لوحة المؤشرات (Dashboard)", () => OpenScreen("dashboard"), true);
        AddSubItem("•  التحليلات والإحصائيات", () => OpenScreen("reports"));
    }

    private void AddSubItem(string text, Action onClick, bool active = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = active ? new SolidColorBrush(Color.FromRgb(0xFE, 0xF0, 0x8A)) : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            FontSize = 12.5,
            FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
            Margin = new Thickness(10, 6, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        tb.MouseLeftButtonUp += (_, _) => onClick();
        SubNavPanel.Children.Add(tb);
    }

    public void OpenDeptDashboardPublic(string deptId)
    {
        try
        {
            _currentScreen = "dashboard:" + deptId;
            CrumbCurrent.Text = "لوحة المعلومات — " + ScreenCatalog.DeptTitle(deptId);
            ContentArea.Content = new Views.Screens.DashboardView(deptId);
            SetNavVisible(true);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OpenDept"); }
    }

    /// <summary>
    /// §B89 — وضع ملء الشاشة: دخول أي وحدة يُخفي القائمة الجانبية ويتمدد المحتوى على كامل العرض،
    /// ولوحة المعلومات وحدها تُظهر القائمة. زر العودة يظهر فقط والقائمة مخفية.
    /// </summary>
    public void SetNavVisible(bool visible)
    {
        if (SideBar == null || NavColumn == null || BackToDashBtn == null) return;
        SideBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NavColumn.Width = new GridLength(visible ? 250 : 0);
        BackToDashBtn.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BackToDash_Click(object sender, RoutedEventArgs e)
    {
        OpenScreen("dashboard");
    }

    /// <summary>إعادة تحميل الشاشة الحالية.</summary>
    public void ReloadCurrent()
    {
        var code = _currentScreen;
        if (code.StartsWith("dashboard:")) { OpenDeptDashboardPublic(code.Split(':')[1]); return; }
        OpenScreen(code);
    }

    /// <summary>
    /// §B94 — الوحدات الخاضعة لبوابة العرض.
    /// §الإصلاح الأمني: كانت قائمة يدوية تسقط منها products/cartons/employees، فكان أي مستخدم
    /// — ولو «مشاهدة» — يفتح «طاقات الأصناف» و«الكرتون» و«الموظفون وأرقام الدخول» بلا فحص،
    /// رغم أن الخادم يفرضها. الآن مشتقة من المصدر الواحد PermissionModules فلا تسقط وحدة أبداً.
    /// </summary>
    private static readonly HashSet<string> GatedModules =
        new(PermissionModules.ScreenGated, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// §B94 — بوابة الصلاحيات المركزية: لا دخول لأي شاشة (قائمة أو قفزة أو بطاقة لوحة)
    /// بلا صلاحية (الوحدة / عرض). اللوحة للجميع؛ الرفض برسالة تسمي الصلاحية المطلوبة.
    /// </summary>
    public bool CanOpenScreen(string code)
    {
        if (code == "dashboard" || code.StartsWith("dashboard:")) return true;
        var def = ScreenCatalog.All.FirstOrDefault(s => s.Code == code);
        string module = def?.Module;
        if (string.IsNullOrEmpty(module) || !GatedModules.Contains(module)) return true;
        bool ok = true;
        try { ok = AppContainer.Get<SessionContext>().Can(module, "View"); }
        catch { ok = true; } // الجلسة غير جاهزة (بدء التشغيل) — لا نقفل الشاشة
        if (!ok)
            AppContainer.Get<DialogService>().Error(
                $"لا تملك صلاحية عرض «{def?.Title ?? code}».\nالصلاحية المطلوبة: {module} / عرض — راجع مدير النظام لمنحها.");
        return ok;
    }

    public void OpenScreen(string code)
    {
        try
        {
            if (!CanOpenScreen(code)) return;
            _currentScreen = code;
            var def = ScreenCatalog.All.FirstOrDefault(s => s.Code == code);
            CrumbCurrent.Text = def?.Title ?? code;
            ContentArea.Content = ScreenFactory.Create(code);
            SetNavVisible(code == "dashboard" || code.StartsWith("dashboard:"));
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, $"OpenScreen:{code}");
        }
    }

    /// <summary>§فتح شاشة التخطيط وتحميل خطة محددة برقمها (من لوحة التحكم أو أي شاشة).</summary>
    public void OpenPlanById(int planId)
    {
        PendingPlanIdToOpen = planId;
        OpenScreen("planning");
    }

    /// <summary>§التنقل الاحترافي من التقارير: فتح مستند في شاشته حسب نوعه.</summary>
    public void OpenDocument(string docType, int id)
    {
        switch (docType)
        {
            case "receiving": PendingShipmentIdToOpen = id; OpenScreen("receiving"); break;
            case "planning": OpenPlanById(id); break;
            case "orders": PendingOrderIdToOpen = id; OpenScreen("orders"); break;
            case "quality": PendingCheckIdToOpen = id; OpenScreen("quality"); break;
            case "finishedgoods": PendingFGIdToOpen = id; OpenScreen("finishedgoods"); break;
            case "delivery": PendingDeliveryIdToOpen = id; OpenScreen("delivery"); break;
            default: AppContainer.Get<DialogService>().Error("لا توجد شاشة مرتبطة بهذا النوع من المستندات."); break;
        }
    }

    private void CrumbHome_Click(object sender, MouseButtonEventArgs e) => OpenScreen("dashboard");

    // ═══ مهلة خمول الجلسة ═══
    private DispatcherTimer _idleTimer;
    private DateTime _lastActivity = DateTime.Now;
    private bool _idleWarned;

    private static int IdleMinutes()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
            var st = db.SystemSettings.AsNoTracking()
                .FirstOrDefault(x => x.SettingKey == "SessionIdleMinutes");
            if (st != null && int.TryParse(st.SettingValue, out var m) && m > 0) return m;
        }
        catch { }
        return 30;
    }

    private void CheckIdle()
    {
        try
        {
            int limit = IdleMinutes();
            var idle = DateTime.Now - _lastActivity;
            // تحذير قبل دقيقتين من الخروج — فرصة للمستخدم لحفظ ما بيده
            if (!_idleWarned && idle.TotalMinutes >= limit - 2 && idle.TotalMinutes < limit)
            {
                _idleWarned = true;
                StatusUser.Text = $"⚠ لا يوجد نشاط — سيُسجَّل الخروج تلقائياً خلال {(int)Math.Ceiling(limit - idle.TotalMinutes)} دقيقة";
                StatusUser.Foreground = System.Windows.Media.Brushes.Firebrick;
                return;
            }
            if (idle.TotalMinutes >= limit)
            {
                _idleTimer?.Stop();
                AppContainer.Get<DialogService>().Info($"انتهت مهلة الخمول ({limit} دقيقة) — تم تسجيل الخروج تلقائياً حفاظاً على أمان النظام.");
                Logout_Click(null, null);
            }
        }
        catch { }
    }

    private void UpdateStatus()
    {
        try
        {
            var session = AppContainer.Get<SessionContext>();
            var name = session.UserName ?? "—";
            TopUserName.Text = "👤 " + name;
            StatusUser.Text = $"المستخدم: {name}";
            StatusUser.FontWeight = FontWeights.Bold;
        }
        catch { }
        // §B84/B4: شريط الحالة كان يعرض نصوصاً ثابتة كاذبة (1.0.0 / متصلة / 2026) — الآن مصادر حية.
        try
        {
            var ver = typeof(MainWindow).Assembly.GetName().Version;
            StatusVer.Text = $"الإصدار: {ver?.ToString(3) ?? "?"} ({Services.BuildInfo.Stamp})";
        }
        catch { }
        try
        {
            using var scope = AppContainer.NewScope();
            var db = (DatesErp.Infrastructure.Persistence.DatesErpDbContext)
                scope.ServiceProvider.GetService(typeof(DatesErp.Infrastructure.Persistence.DatesErpDbContext));
            bool isServer = db != null && db.Database.IsSqlServer();
            StatusDb.Text = isServer ? "قاعدة البيانات: SQL Server مركزية 🟢" : "قاعدة البيانات: محلية SQLite 🟢";
        }
        catch { StatusDb.Text = "قاعدة البيانات: محلية 🟢"; }
        try { StatusFy.Text = $"السنة التشغيلية: {DateTime.Now.Year}"; } catch { }
        TopClock.Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (!AppContainer.Get<DialogService>().Confirm("هل تريد تسجيل الخروج؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            ((Core.Interfaces.Services.IAuthService)scope.ServiceProvider.GetService(typeof(Core.Interfaces.Services.IAuthService))).Logout();
        }
        catch { }
        // §إصلاح الخروج: تُخفى شاشة العمل فوراً — لا تبقى ظاهرة خلف نموذج الدخول
        Hide();
        var login = new LoginWindow();
        if (login.ShowDialog() == true)
        {
            UpdateStatus();
            OpenScreen("dashboard");
            Show();
        }
        else Close();
    }

    private void Logout_Click(object sender, MouseButtonEventArgs e) => Logout_Click(sender, (RoutedEventArgs)e);
}
