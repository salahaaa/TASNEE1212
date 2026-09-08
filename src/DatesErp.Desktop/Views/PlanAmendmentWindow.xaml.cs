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

namespace DatesErp.Desktop.Views;

/// <summary>
/// §تعديلات العملاء أثناء التنفيذ — نافذة طلب تعديل صنف على خطة معتمدة.
/// يختار المستخدم بند الخطة المستهدف، فيعرض النظام الحالة الفعلية (لم يبدأ/جزئي/مكتمل)
/// والمنفذ المحمي والمتبقي، ثم الصنف الجديد والكمية والسبب — ويُسجَّل الطلب للاعتماد.
/// لا تعديل في مكانه: الخطة الأصلية تبقى، والاعتماد يُنشئ إصداراً جديداً.
/// </summary>
public partial class PlanAmendmentWindow : Window
{
    private readonly int _planId;
    private ProductionPlan _plan;
    private List<ProductionPlanItem> _items = new();
    private Dictionary<int, string> _products = new();
    private Dictionary<int, string> _packs = new();

    public PlanAmendmentWindow(int planId)
    {
        InitializeComponent();
        _planId = planId;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _plan = db.ProductionPlans.AsNoTracking().Include(p => p.Items).FirstOrDefault(p => p.Id == _planId);
            if (_plan == null) { AppContainer.Get<DialogService>().Error("الخطة غير موجودة."); Close(); return; }
            if (!_plan.IsApproved) { AppContainer.Get<DialogService>().Error("الخطة غير معتمدة — التعديل على الخطة المعتمدة فقط."); Close(); return; }

            Title = $"طلب تعديل خطة — {_plan.DocumentNumber}";
            HeadTitle.Text = $"طلب تعديل خطة: {_plan.PlanTitle}";
            HeadPlan.Text = $"الخطة: {_plan.DocumentNumber} (الإصدار {_plan.RevisionNo})";

            _products = db.Products.AsNoTracking().Where(p => p.IsActive).ToDictionary(p => p.Id, p => p.ProductNameAr);
            _packs = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);

            NewProductBox.ItemsSource = _products.OrderBy(kv => kv.Value)
                .Select(kv => new { Id = kv.Key, ProductNameAr = kv.Value }).ToList();
            NewPackBox.ItemsSource = _packs.OrderBy(kv => kv.Value)
                .Select(kv => new { Id = kv.Key, PackageNameAr = kv.Value }).ToList();

            // البنود القابلة للتعديل: غير المقفلة فقط
            _items = _plan.Items.Where(i => !i.IsClosed).OrderBy(i => i.ScheduledDate).ThenBy(i => i.Id).ToList();
            var customers = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
            ItemBox.ItemsSource = _items.Select(i => new
            {
                ItemId = i.Id,
                Label = $"#{i.Id} — {(_products.ContainsKey(i.ProductId) ? _products[i.ProductId] : "صنف")} — " +
                        $"{i.PlannedQtyKg:N1} كجم — " +
                        $"{(i.CustomerId != null && customers.ContainsKey(i.CustomerId.Value) ? customers[i.CustomerId.Value] : "عميل")}"
            }).ToList();

            if (ItemBox.Items.Count > 0) ItemBox.SelectedIndex = 0;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanAmendment.Load"); }
    }

    private void ItemBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemBox.SelectedValue is int itemId)
        {
            var item = _items.FirstOrDefault(i => i.Id == itemId);
            if (item == null) return;
            double executed = item.ProducedQtyKg;
            double remaining = Math.Max(0, item.PlannedQtyKg - executed);

            OldProductText.Text = _products.ContainsKey(item.ProductId) ? _products[item.ProductId] : $"صنف #{item.ProductId}";
            PlannedText.Text = item.PlannedQtyKg.ToString("N1");
            ExecutedText.Text = executed.ToString("N1");
            RemainingText.Text = remaining.ToString("N1");

            // حالة التنفيذ: لم يبدأ / جزئي / مكتمل — وسؤال الحماية للمنفذ
            string state = executed <= 0.001 ? "لم يبدأ" : (remaining <= 0.001 ? "مكتمل" : "جزئي");
            ExecText.Text = $"حالة تنفيذ البند وقت الطلب: {(state == "لم يبدأ" ? "⚪ لم يبدأ" : state == "جزئي" ? "🟠 جزئي (بدأ الإنتاج)" : "✅ مكتمل")}";
            ProtectText.Text = executed <= 0.001
                ? "لا كمية منفذة بعد — سيُحوَّل المتبقي كاملاً للصنف الجديد."
                : $"⚠️ حماية المنفذ: {executed:N1} كجم أُنتجت فعلياً وتبقى كما هي (لا تُعاد للخطة) — " +
                  $"يُوقَف المتبقي {remaining:N1} كجم من الصنف الحالي ويُنشأ الإصدار الجديد بالصنف الجديد.";

            // الصنف الجديد والعبوة والكمية الافتراضية
            var cur = NewProductBox.SelectedValue;
            if (cur == null || (int)cur == item.ProductId)
                NewProductBox.SelectedValue = _products.Keys.FirstOrDefault(k => k != item.ProductId);
            NewPackBox.SelectedValue = item.PackagingTypeId;
            NewQtyBox.Text = remaining.ToString("N1");
        }
    }

    private void NewQtyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!double.TryParse(NewQtyBox.Text, out var v)) return;
        if (ItemBox.SelectedValue is int itemId)
        {
            var item = _items.FirstOrDefault(i => i.Id == itemId);
            if (item != null)
            {
                double remaining = Math.Max(0, item.PlannedQtyKg - item.ProducedQtyKg);
                if (v > remaining)
                    HintText.Text = $"⚠️ الكمية المدخلة {v:N1} تتجاوز المتبقي {remaining:N1} كجم — ستُرفض عند الاعتماد أو تُخفَّض للمتبقي.";
                else HintText.Text = "";
            }
        }
    }

    private void Request_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ItemBox.SelectedValue is not int itemId) { AppContainer.Get<DialogService>().Error("اختر بند الخطة المستهدف."); return; }
            if (NewProductBox.SelectedValue is not int newProductId) { AppContainer.Get<DialogService>().Error("اختر الصنف الجديد."); return; }
            string reason = (ReasonBox.Text ?? "").Trim();
            if (reason.Length < 10) { AppContainer.Get<DialogService>().Error("سبب التعديل إلزامي — 10 أحرف على الأقل (يُسجَّل للتدقيق)."); return; }

            var item = _items.First(i => i.Id == itemId);
            double? newQty = null;
            if (double.TryParse(NewQtyBox.Text, out var q) && q > 0) newQty = Math.Round(q, 1);

            var confirm = AppContainer.Get<DialogService>().Confirm(
                $"تأكيد طلب التعديل:\n" +
                $"• الخطة: {_plan.DocumentNumber} — بند #{item.Id}\n" +
                $"• الصنف الحالي: {OldProductText.Text} (المخطط {item.PlannedQtyKg:N1} كجم)\n" +
                $"• المنفذ المحمي: {item.ProducedQtyKg:N1} كجم\n" +
                $"• الصنف الجديد: {_products[newProductId]}" +
                (newQty != null ? $" — {newQty:N1} كجم" : " — المتبقي كاملاً") +
                $"\n• السبب: {reason}\n\n" +
                "سيُسجَّل الطلب ويبقى الخطة الأصلية كما هي حتى الاعتماد.");
            if (!confirm) return;

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanAmendmentService>();
            var r = svc.RequestAmendment(new PlanAmendmentRequestDto
            {
                OriginalPlanId = _planId,
                PlanItemId = item.Id,
                CustomerId = item.CustomerId ?? _plan.SingleCustomerId ?? 0,
                NewProductId = newProductId,
                NewPackagingTypeId = NewPackBox.SelectedValue as int?,
                NewQtyKg = newQty,
                Reason = reason
            });
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            DialogResult = true;
            Close();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanAmendment.Request"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
