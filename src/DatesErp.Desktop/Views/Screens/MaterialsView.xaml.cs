using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

public partial class MaterialsView : UserControl
{
    private List<int> _orderIds = new();

    public MaterialsView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadOrders();
    }

    private void OpenAdmin_Click(object sender, RoutedEventArgs e)
        => new AuxAdminWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void LoadOrders()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var orders = db.ProductionOrders.Where(o => o.IsApproved).OrderByDescending(o => o.Id).ToList();
            _orderIds = orders.Select(o => o.Id).ToList();
            OrderBox.ItemsSource = orders.Select(o => $"{o.DocumentNumber} ({o.ProductionDate:dd/MM/yyyy})").ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Materials.Load"); }
    }

    private void Order_Changed(object sender, SelectionChangedEventArgs e) => RefreshGrid();
    // §B84/B7: زر التحديث كان يستدعي Order_Changed (مضلل في التتبع) — معالج باسمه الحقيقي.
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshGrid();

    private int? SelectedOrder() => OrderBox.SelectedIndex >= 0 && OrderBox.SelectedIndex < _orderIds.Count ? _orderIds[OrderBox.SelectedIndex] : null;

    private void RefreshGrid()
    {
        var oid = SelectedOrder();
        if (oid == null) { MatGrid.ItemsSource = null; return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            MatGrid.ItemsSource = db.ProductionOrderMaterials.Where(m => m.OrderId == oid.Value).ToList().Select(m => new
            {
                MaterialId = m.MaterialId,
                Name = db.AuxiliaryMaterials.Where(a => a.Id == m.MaterialId).Select(a => a.MaterialNameAr).FirstOrDefault(),
                Calculated = m.CalculatedQty,
                Issued = m.ActualIssuedQty,
                Consumed = m.ConsumedQty,
                Wasted = m.WastedQty,
                Unused = m.ActualIssuedQty - m.ConsumedQty - m.WastedQty - m.ReturnedQty
            }).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Materials.Grid"); }
    }

    private void Issue_Click(object sender, RoutedEventArgs e)
    {
        var oid = SelectedOrder();
        if (oid == null) { AppContainer.Get<DialogService>().Error("اختر أمر الإنتاج."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IProductionOrderService)scope.ServiceProvider.GetService(typeof(IProductionOrderService));
            var r = svc.IssueMaterials(oid.Value);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else AppContainer.Get<DialogService>().Info(r.Message);
            RefreshGrid();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Materials.Issue"); }
    }

    private void Return_Click(object sender, RoutedEventArgs e)
    {
        var oid = SelectedOrder();
        if (oid == null) { AppContainer.Get<DialogService>().Error("اختر أمر الإنتاج."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IProductionOrderService)scope.ServiceProvider.GetService(typeof(IProductionOrderService));
            var r = svc.ReturnUnusedMaterials(oid.Value);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else AppContainer.Get<DialogService>().Info(r.Message);
            RefreshGrid();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Materials.Return"); }
    }

    private void Consume_Click(object sender, RoutedEventArgs e)
    {
        var oid = SelectedOrder();
        if (oid == null) return;
        if (sender is Button btn && btn.Tag is int materialId)
        {
            var dlgC = new InputDialog("تسجيل الاستهلاك", "الكمية المستهلكة:");
            if (dlgC.ShowDialog() != true) return;
            double.TryParse(dlgC.Value, out var consumed);

            var dlgW = new InputDialog("تسجيل الهالك", "كمية الهالك:");
            double wasted = 0;
            if (dlgW.ShowDialog() == true) double.TryParse(dlgW.Value, out wasted);

            try
            {
                using var scope = AppContainer.NewScope();
                var svc = (IProductionOrderService)scope.ServiceProvider.GetService(typeof(IProductionOrderService));
                var r = svc.ConsumeMaterials(oid.Value, materialId, consumed, wasted);
                if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
                else AppContainer.Get<DialogService>().Info(r.Message);
                RefreshGrid();
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Materials.Consume"); }
        }
    }
}
