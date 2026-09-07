using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B97 — نافذة تفاصيل مستند عامة (نمط النقر المزدوج على بطاقات المهام لغير الخطط):
/// رأس + حقول الوثيقة + لافتة سبب الحالة + تاريخ المستند من سجل التدقيق.
/// قراءة فقط — التنفيذ داخل شاشة المستند نفسها (قاعدة B94).
/// </summary>
public class DocDetailWindow : Window
{
    public DocDetailWindow(string docType, int docId, string docNumber)
    {
        var (typeAr, header) = TypeAr(docType);
        Title = $"تفاصيل المستند — {docNumber}";
        Width = 720;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xE9, 0xD8));

        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xE9, 0xD8)) };

        // شريط العنوان
        var head = new Border { Padding = new Thickness(12, 6, 12, 6), Background = (Brush)new BrushConverter().ConvertFromString("#0A246A") };
        var headPanel = new DockPanel();
        DockPanel.SetDock(headPanel, Dock.Top);
        var title = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13, Text = header };
        DockPanel.SetDock(title, Dock.Right);
        head.Child = title;
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // لافتة سبب الحالة
        var reasonBanner = new Border { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 6, 12, 6) };
        var reasonText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B)), FontWeight = FontWeights.Bold, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        reasonBanner.Child = reasonText;
        DockPanel.SetDock(reasonBanner, Dock.Top);
        root.Children.Add(reasonBanner);

        // زر الإغلاق
        var closeBtn = new Button { Content = "✖ إغلاق", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10) };
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Bottom);
        root.Children.Add(closeBtn);

        // المحتوى: الحقول + التاريخ
        var grid = new Grid { Margin = new Thickness(10) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var fieldsPanel = new StackPanel();
        var historyPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var historyGrid = new DataGrid { IsReadOnly = true, FontSize = 11.5, Height = 220, AutoGenerateColumns = false };
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "الوقت", Binding = new System.Windows.Data.Binding("Time"), Width = 130 });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "المستخدم", Binding = new System.Windows.Data.Binding("User"), Width = 130 });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "الإجراء", Binding = new System.Windows.Data.Binding("Action"), Width = 110 });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "التفاصيل", Binding = new System.Windows.Data.Binding("Detail"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        historyPanel.Children.Add(new TextBlock { Text = "📜 تاريخ المستند", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)), Margin = new Thickness(0, 0, 0, 6) });
        historyPanel.Children.Add(historyGrid);

        Grid.SetRow(fieldsPanel, 0);
        Grid.SetRow(historyPanel, 2);
        grid.Children.Add(fieldsPanel);
        grid.Children.Add(historyPanel);
        var headWrap = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0x9D, 0xB9)), BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) };
        headWrap.Child = fieldsPanel;
        Grid.SetRow(headWrap, 0);
        grid.Children.Clear();
        grid.Children.Add(headWrap);
        grid.Children.Add(historyPanel);

        DockPanel.SetDock(grid, Dock.Top);
        root.Children.Add(grid);
        Content = root;

        Loaded += (_, _) =>
        {
            try
            {
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var users = db.Users.AsNoTracking().ToDictionary(u => u.Id, u => u.FullName);

                void AddField(string label, string value)
                {
                    if (string.IsNullOrWhiteSpace(value)) return;
                    var b = new Border { Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0x9D, 0xB9)), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 4) };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) });
                    sp.Children.Add(new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 });
                    b.Child = sp;
                    fieldsPanel.Children.Add(b);
                }

                WorkflowDocument doc = null;
                string statusAr = null;

                switch (docType)
                {
                    case "Order":
                    {
                        var o = db.ProductionOrders.AsNoTracking().Include(x => x.Items).FirstOrDefault(x => x.Id == docId);
                        if (o == null) { AppContainer.Get<DialogService>().Error("المستند غير موجود."); Close(); return; }
                        doc = o;
                        statusAr = DocStatuses.ToArabic(o.Status);
                        AddField("رقم الأمر", o.DocumentNumber);
                        AddField("الحالة", statusAr);
                        AddField("العميل", o.CustomerId != null ? db.Customers.AsNoTracking().FirstOrDefault(c => c.Id == o.CustomerId)?.CustomerName : null);
                        AddField("تاريخ الإنتاج", o.ProductionDate?.ToString("dd/MM/yyyy"));
                        AddField("المنتَج (كجم)", o.Items.Sum(i => i.PlannedQtyKg).ToString("N1"));
                        AddField("الفعلي (كجم)", o.Items.Sum(i => i.ProducedQtyKg).ToString("N1"));
                        AddField("سبب الإغلاق", o.CloseReason);
                        break;
                    }
                    case "QC":
                    {
                        var c = db.QualityChecks.AsNoTracking().FirstOrDefault(x => x.Id == docId);
                        if (c == null) { AppContainer.Get<DialogService>().Error("المستند غير موجود."); Close(); return; }
                        doc = c;
                        statusAr = QualityCheckStatuses.ToArabic(c.Status);
                        AddField("رقم المحضر", c.DocumentNumber);
                        AddField("الحالة", statusAr);
                        AddField("القرار", c.Decision switch { "Passed" => "مطابق", "Quarantine" => "محجوز", "Rejected" => "مرفوض", _ => c.Decision });
                        AddField("المفحوص (كجم)", c.TotalCheckedKg.ToString("N1"));
                        AddField("المقبول (كجم)", c.AcceptedKg.ToString("N1"));
                        AddField("المرفوض (كجم)", c.RejectedKg.ToString("N1"));
                        AddField("تاريخ الفحص", c.CheckDate?.ToString("dd/MM/yyyy"));
                        AddField("متوقع بعد التبريد", c.ExpectedCheckDate?.ToString("dd/MM/yyyy"));
                        AddField("ملاحظات الفاحص", c.InspectorNotes);
                        break;
                    }
                    case "Delivery":
                    {
                        var d = db.ProductionDeliveries.AsNoTracking().Include(x => x.Items).FirstOrDefault(x => x.Id == docId);
                        if (d == null) { AppContainer.Get<DialogService>().Error("المستند غير موجود."); Close(); return; }
                        doc = d;
                        statusAr = DocStatuses.ToArabic(d.Status);
                        AddField("رقم أمر التسليم", d.DocumentNumber);
                        AddField("الحالة", statusAr + " — استلام: " + (d.ReceiptStatus == "Full" ? "كامل" : d.ReceiptStatus == "Partial" ? "جزئي" : "لم يبدأ"));
                        AddField("تاريخ التسليم", d.DeliveryDate?.ToString("dd/MM/yyyy"));
                        AddField("الإجمالي (كجم)", d.Items.Sum(i => i.QtyKg).ToString("N1"));
                        AddField("سبب تجاوز الفحص", d.BypassReason);
                        break;
                    }
                    case "Receipt":
                    {
                        var r = db.FinishedGoodsReceipts.AsNoTracking().Include(x => x.Items).FirstOrDefault(x => x.Id == docId);
                        if (r == null) { AppContainer.Get<DialogService>().Error("المستند غير موجود."); Close(); return; }
                        doc = r;
                        statusAr = DocStatuses.ToArabic(r.Status);
                        AddField("رقم السند", r.DocumentNumber);
                        AddField("الحالة", statusAr + " — استلام: " + (r.ReceiptStatus == "Full" ? "كامل" : r.ReceiptStatus == "Partial" ? "جزئي" : "لم يبدأ"));
                        AddField("تاريخ الاستلام", r.DeliveryDate?.ToString("dd/MM/yyyy"));
                        AddField("الإجمالي (كجم)", r.Items.Sum(i => i.NetWeightKg).ToString("N1"));
                        AddField("المستلم (كجم)", r.Items.Sum(i => i.ReceivedQtyKg).ToString("N1"));
                        break;
                    }
                    case "CustomerDelivery":
                    {
                        var cd = db.CustomerDeliveries.AsNoTracking().FirstOrDefault(x => x.Id == docId);
                        if (cd == null) { AppContainer.Get<DialogService>().Error("المستند غير موجود."); Close(); return; }
                        doc = cd;
                        statusAr = DocStatuses.ToArabic(cd.Status);
                        AddField("رقم التسليم", cd.DocumentNumber);
                        AddField("الحالة", statusAr);
                        AddField("العميل", db.Customers.AsNoTracking().FirstOrDefault(cu => cu.Id == cd.CustomerId)?.CustomerName);
                        AddField("التاريخ", cd.DeliveryDate?.ToString("dd/MM/yyyy"));
                        AddField("الإجمالي (كجم)", cd.TotalQtyKg.ToString("N1"));
                        AddField("المفوتر (كجم)", cd.InvoicedQtyKg.ToString("N1"));
                        AddField("غير المفوتر (كجم)", cd.BillableQtyKg.ToString("N1"));
                        break;
                    }
                    default:
                    {
                        AddField("النوع", typeAr);
                        AddField("الرقم", docNumber);
                        break;
                    }
                }

                if (doc != null)
                {
                    AddField("المنشئ", doc.CreatedBy != null && users.TryGetValue(doc.CreatedBy.Value, out var cn) ? cn : "—");
                    AddField("تاريخ الإنشاء", doc.CreatedDate.ToString("dd/MM/yyyy HH:mm"));
                    if (!string.IsNullOrWhiteSpace(doc.StatusReason))
                    {
                        reasonBanner.Visibility = Visibility.Visible;
                        reasonText.Text = $"⚠️ سبب الحالة: {doc.StatusReason}";
                    }
                }

                historyGrid.ItemsSource = db.AuditLogs.AsNoTracking()
                    .Where(a => a.DocumentNumber == docNumber)
                    .OrderByDescending(a => a.ActionDate).Take(25)
                    .ToList()   // §سحب: switch expression غير قابل للترجمة في شجرة EF
                    .Select(a => new
                    {
                        Time = a.ActionDate.ToString("dd/MM/yyyy HH:mm"),
                        User = a.UserName,
                        Action = a.ActionType switch
                        {
                            "Create" => "إنشاء", "Edit" => "تعديل", "Approve" => "اعتماد",
                            "Cancel" => "إلغاء", "Issue" => "تحرير", "Delete" => "حذف",
                            "Post" => "ترحيل", _ => a.ActionType ?? "—"
                        },
                        Detail = (string)null
                    }).ToList();
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "DocDetail.Load"); }
        };
    }

    private static (string type, string header) TypeAr(string docType) => docType switch
    {
        "Plan" => ("الخطة", "خطة الإنتاج"),
        "Order" => ("أمر الإنتاج", "أمر الإنتاج"),
        "QC" => ("محضر الفحص", "محضر فحص الجودة"),
        "Delivery" => ("أمر التسليم", "أمر تسليم الإنتاج"),
        "Receipt" => ("سند الاستلام", "سند استلام الإنتاج"),
        "CustomerDelivery" => ("تسليم العميل", "تسليم العميل"),
        _ => ("مستند", "تفاصيل المستند")
    };
}
