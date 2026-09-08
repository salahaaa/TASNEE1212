using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// شاشة الخطط المحفوظة (MPS) — عرض واطلاع فقط:
/// جدول واحد يعرض الخطط المحفوظة، والنقر المزدوج على خطة يفتح نافذة تفاصيلها للاطلاع
/// (قراءة فقط) بدل نموذج اختيار الأصناف.
/// </summary>
public partial class PlanningView : UserControl
{
    private List<object> _plans_all = new();

    public PlanningView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshPlansList();
            // §فتح خطة محددة طُلبت من شاشة أخرى (لوحة التحكم / إقفال الخطط / المهام) ثم تصفير الطلب
            if (MainWindow.PendingPlanIdToOpen is int pid)
            {
                MainWindow.PendingPlanIdToOpen = null;
                OpenDetail(pid);
            }
        };
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("خطط الإنتاج (MPS)");
        chrome.SetScreenCode("MRPMPS1001");
        var toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => CreateNewPlan(), "➕ خطة جديدة")
            .WithRefresh((_, _) => RefreshPlansList())
            .WithSearch((_, _) => { RefreshPlansList(); PlansSearchBox.Focus(); }, "بحث في الخطط المحفوظة (F9)")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        chrome.SetToolbar(toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void RefreshPlansList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var list = db.ProductionPlans.OrderByDescending(p => p.Id).ToList();
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

    /// <summary>§النقر المزدوج على خطة يعرض تفاصيلها للاطلاع — لا نموذج اختيار الأصناف.</summary>
    private void PlansGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlansGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(PlansGrid.SelectedItem) is int id)
            OpenDetail(id);
    }

    /// <summary>➕ خطة جديدة — يفتح معالج إنشاء الخطة (رأس + توزيع + مراجعة بنود + حفظ).</summary>
    private void CreateNewPlan()
    {
        var win = new PlanCreationWindow { Owner = Window.GetWindow(this) };
        win.ShowDialog();
        RefreshPlansList();
    }

    private void OpenDetail(int id)
    {
        var win = new Views.PlanDetailWindow(id) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
        RefreshPlansList();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPlansList();

    private void NewPlan_Click(object sender, RoutedEventArgs e) => CreateNewPlan();

    /// <summary>§بحث وفلترة لحظية على كل الأعمدة.</summary>
    private void PlansSearch_Changed(object sender, TextChangedEventArgs e)
        => ScreenSearch.Apply(PlansSearchBox, PlansGrid, _plans_all);
}
