using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B10 — شاشة الكرتون الفارغ: رصيد + دفتر حركات، عدّ فعلي يقيّد الفروق آلياً،
/// بيع موثق يخصم الرصيد ويمنع تجاوزه، وطباعة رسمية بنمط المراحل السابقة.
/// </summary>
public partial class CartonView : UserControl
{
    private sealed class CountRow { public int ProductId { get; set; } public string Product { get; set; } public int Book { get; set; } public int Counted { get; set; } public int Diff => Counted - Book; }
    private sealed class SaleRow { public int ProductId { get; set; } public string Product { get; set; } public int Cartons { get; set; } public double Amount { get; set; } }
    private readonly ObservableCollection<CountRow> _countRows = new();
    private readonly ObservableCollection<SaleRow> _saleRows = new();

    public CartonView()
    {
        InitializeComponent();
        CountGrid.ItemsSource = _countRows;
        CountGrid.AutoGeneratingColumn += (_, e) => { };
        SaleGrid.ItemsSource = _saleRows;
        CountDateBox.SelectedDate = DateTime.Now;
        Loaded += (_, _) => Load();
    }

    private CartonService Svc()
    {
        var scope = AppContainer.NewScope();
        return scope.ServiceProvider.GetRequiredService<CartonService>();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var whs = db.Warehouses.AsNoTracking().Where(w => w.IsActive).ToList();
            CountWhBox.ItemsSource = whs;
            SaleWhBox.ItemsSource = whs;
            CountWhBox.SelectedValue = Svc().DefaultCartonWarehouseId();
            SaleWhBox.SelectedValue = Svc().DefaultCartonWarehouseId();
            var packs = db.Products.AsNoTracking().Where(p => p.GroupCode == "004" && p.IsActive).ToList();
            CountProdBox.ItemsSource = packs;
            SaleProdBox.ItemsSource = packs;
            SaleCustBox.ItemsSource = db.Customers.AsNoTracking().Where(c => c.IsActive).ToList();
            SaleCustBox.Items.Insert(0, null);
            RefreshData();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.Load"); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();

    private void RefreshData()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var packIds = db.Products.AsNoTracking().Where(p => p.GroupCode == "004").Select(p => p.Id).ToList();
            BalGrid.ItemsSource = db.StockBalances.AsNoTracking()
                .Where(b => b.ProductId != null && packIds.Contains(b.ProductId.Value) && b.LotId == null && (b.PackageCount != 0))
                .Select(b => new
                {
                    Warehouse = db.Warehouses.Where(w => w.Id == b.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                    Product = db.Products.Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                    Cartons = b.PackageCount
                }).ToList();
            MovGrid.ItemsSource = db.InventoryTransactions.AsNoTracking()
                .Where(t => t.ProductId != null && packIds.Contains(t.ProductId.Value))
                .OrderByDescending(t => t.Id).Take(300)
                .Select(t => new
                {
                    Date = t.TxnDate.ToString("dd/MM/yyyy"),
                    Txn = t.ReferenceDocType == ReferenceDocType.CartonReturn ? "تولّد 🔄" : t.ReferenceDocType == ReferenceDocType.CartonSale ? "بيع 💰" : "عدّ/تسوية 🔢",
                    Ref = t.ReferenceDocNumber,
                    Warehouse = db.Warehouses.Where(w => w.Id == t.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                    Inb = t.PackageCount > 0 ? t.PackageCount.ToString() : "",
                    Outb = t.PackageCount < 0 ? t.PackageCount.ToString() : ""
                }).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.Refresh"); }
    }

    // ═══ العدّ ═══
    private void AddCountRow_Click(object sender, RoutedEventArgs e)
    {
        if (CountProdBox.SelectedItem is not Product p) { AppContainer.Get<DialogService>().Error("اختر الصنف."); return; }
        if (!int.TryParse(CountedBox.Text, out var counted) || counted < 0) { AppContainer.Get<DialogService>().Error("أدخل عدّاً صحيحاً."); return; }
        if (CountWhBox.SelectedValue is not int wid) return;
        var book = Svc().BookCartons(p.Id, wid);
        _countRows.Add(new CountRow { ProductId = p.Id, Product = p.ProductNameAr, Book = book, Counted = counted });
        CountedBox.Text = "0";
    }

    private void SaveCount_Click(object sender, RoutedEventArgs e)
    {
        if (CountWhBox.SelectedValue is not int wid) { AppContainer.Get<DialogService>().Error("اختر مخزن العدّ."); return; }
        if (_countRows.Count == 0) { AppContainer.Get<DialogService>().Error("أضف سطراً واحداً على الأقل."); return; }
        try
        {
            var r = Svc().CreateCountDoc(wid, CountDateBox.SelectedDate?.ToString("dd/MM/yyyy"), CountHint.Text, _countRows.Select(c => (c.ProductId, c.Counted)).ToList());
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _countRows.Clear();
            RefreshData();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.Count"); }
    }

    // ═══ البيع ═══
    private void AddSaleRow_Click(object sender, RoutedEventArgs e)
    {
        if (SaleProdBox.SelectedItem is not Product p) { AppContainer.Get<DialogService>().Error("اختر الصنف."); return; }
        if (!int.TryParse(SaleQtyBox.Text, out var q) || q <= 0) { AppContainer.Get<DialogService>().Error("أدخل كمية بيع صحيحة."); return; }
        double.TryParse(PriceBox.Text, out var price);
        _saleRows.Add(new SaleRow { ProductId = p.Id, Product = p.ProductNameAr, Cartons = q, Amount = Math.Round(q * price, 2) });
        SaleTotal.Text = $"الإجمالي: {_saleRows.Sum(s => s.Amount):N2}";
        SaleQtyBox.Text = "0";
    }

    private void SaveSale_Click(object sender, RoutedEventArgs e)
    {
        if (SaleWhBox.SelectedValue is not int wid) { AppContainer.Get<DialogService>().Error("اختر مخزن الصرف."); return; }
        if (_saleRows.Count == 0) { AppContainer.Get<DialogService>().Error("أضف سطراً واحداً على الأقل."); return; }
        double.TryParse(PriceBox.Text, out var price);
        try
        {
            var r = Svc().CreateSaleDoc((SaleCustBox.SelectedItem as Customer)?.Id, wid, price, null, _saleRows.Select(s => (s.ProductId, s.Cartons)).ToList());
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _saleRows.Clear();
            SaleTotal.Text = "الإجمالي: 0";
            RefreshData();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.Sale"); }
    }

    private void PrintSale_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var doc = db.CartonSaleDocs.Include(d => d.Items).OrderByDescending(d => d.Id).FirstOrDefault();
            if (doc == null) { AppContainer.Get<DialogService>().Error("لا يوجد سند بيع للطباعة."); return; }
            var m = new PhaseDocModel
            {
                DocTitle = "سند بيع كرتون فارغ",
                DocNo = doc.DocumentNumber,
                StatusAr = "معتمد ✅",
                Columns = new[] { "الصنف", "الكراتين", "سعر الكرتون", "الإجمالي" },
                Signatures = { "أمين المخزن", "المشتري/المندوب", "المدير المالي" }
            };
            m.Info.Add(("العميل", doc.CustomerId != null ? db.Customers.Where(c => c.Id == doc.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-" : "نقدي"));
            m.Info.Add(("المخزن", db.Warehouses.Where(w => w.Id == doc.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault() ?? "-"));
            m.Info.Add(("التاريخ", doc.SaleDate.ToString("dd/MM/yyyy")));
            foreach (var it in doc.Items)
                m.Rows.Add(new object[]
                {
                    db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    it.Cartons, doc.PricePerCarton, it.Amount
                });
            m.Totals.Add(("إجمالي الكراتين", doc.Items.Sum(i => i.Cartons).ToString("N0")));
            m.Totals.Add(("إجمالي القيمة", doc.TotalAmount.ToString("N2")));
            new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}", p => PhasePrint.ExportPdf(m, p))
            { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.PrintSale"); }
    }
}
