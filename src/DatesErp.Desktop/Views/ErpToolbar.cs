using System.Windows;
using System.Windows.Controls;

namespace DatesErp.Desktop.Views;

/// <summary>
/// شريط أدوات ERP الكلاسيكي الموحّد — نفس أزرار الشاشات المعتمدة مع اختصارات لوحة المفاتيح:
/// جديد (F2) | تعديل (F3) | حذف (F8) | بحث (F9) | تحديث (F5) | طباعة (Ctrl+P) | Excel | حفظ (F10) | تراجع | خروج.
/// §الهوية: الحاوية WrapPanel — الأزرار تنتقل لسطر جديد عند ضيق المساحة فلا زر فوق زر ولا خروج عن النطاق.
/// </summary>
public class ErpToolbar : WrapPanel
{
    public Button NewBtn, EditBtn, DeleteBtn, SearchBtn, RefreshBtn, PrintBtn, ExcelBtn, SaveBtn, UndoBtn, ApproveBtn, UnapproveBtn, ExitBtn;
    public Button NavFirst, NavPrev, NavNext, NavLast, ListBtn;

    public ErpToolbar()
    {
        Orientation = Orientation.Horizontal;
        FlowDirection = FlowDirection.RightToLeft;
        ItemHeight = 34;
    }

    public ErpToolbar WithNew(RoutedEventHandler h, string label = "➕ جديد (F2)")
    { NewBtn = AddBtn(label, "ErpPrimaryButton", h); return this; }

    public ErpToolbar WithSave(RoutedEventHandler h, string label = "💾 حفظ (F10)")
    { SaveBtn = AddBtn(label, "ErpPrimaryButton", h); return this; }

    public ErpToolbar WithUndo(RoutedEventHandler h, string label = "↩ تراجع")
    { UndoBtn = AddBtn(label, "ErpButton", h); return this; }

    public ErpToolbar WithEdit(RoutedEventHandler h)
    { EditBtn = AddBtn("✏️ تعديل (F3)", "ErpButton", h); return this; }

    public ErpToolbar WithDelete(RoutedEventHandler h)
    { DeleteBtn = AddBtn("🗑️ حذف (F8)", "ErpDangerButton", h); return this; }

    public ErpToolbar WithSearch(RoutedEventHandler h, string tooltip = null)
    { SearchBtn = AddBtn("🔍 بحث (F9)", "ErpButton", h, tooltip); return this; }

    public ErpToolbar WithRefresh(RoutedEventHandler h)
    { RefreshBtn = AddBtn("🔄 تحديث (F5)", "ErpButton", h); return this; }

    public ErpToolbar WithPrint(RoutedEventHandler h, string label = "🖨️ طباعة (Ctrl+P)")
    { PrintBtn = AddBtn(label, "ErpButton", h); return this; }

    public ErpToolbar WithExcel(RoutedEventHandler h)
    { ExcelBtn = AddBtn("📊 Excel", "ErpButton", h); return this; }

    public ErpToolbar WithApprove(RoutedEventHandler h, string label = "🔒 اعتماد")
    { ApproveBtn = AddBtn(label, "ErpApproveButton", h); return this; }

    public ErpToolbar WithUnapprove(RoutedEventHandler h, string tooltip = null)
    { UnapproveBtn = AddBtn("🔓 إلغاء الاعتماد", "ErpDangerButton", h, tooltip); return this; }

    public ErpToolbar WithNavigation(RoutedEventHandler first, RoutedEventHandler prev, RoutedEventHandler next, RoutedEventHandler last)
    {
        NavFirst = AddBtn("|◀", "ErpButton", first, "السجل الأول");
        NavPrev = AddBtn("◀", "ErpButton", prev, "السابق");
        NavNext = AddBtn("▶", "ErpButton", next, "التالي");
        NavLast = AddBtn("▶|", "ErpButton", last, "السجل الأخير");
        return this;
    }

    public ErpToolbar WithList(RoutedEventHandler h, string label = "📋 القائمة")
    { ListBtn = AddBtn(label, "ErpButton", h); return this; }

    public ErpToolbar WithExit(RoutedEventHandler h)
    { ExitBtn = AddBtn("🚪 خروج", "ErpButton", h); return this; }

    public ErpToolbar WithCustom(string label, string style, RoutedEventHandler h, string tooltip = null)
    { AddBtn(label, style, h, tooltip); return this; }

    public void SetLocked(bool locked, string lockMsg = null)
    {
        foreach (var b in new[] { NewBtn, EditBtn, DeleteBtn, SaveBtn, UndoBtn, ApproveBtn })
            if (b != null) b.IsEnabled = !locked;
        if (locked && lockMsg != null && SaveBtn != null) SaveBtn.Content = lockMsg;
    }

    private Button AddBtn(string label, string style, RoutedEventHandler h, string tooltip = null)
    {
        var b = new Button
        {
            Content = label,
            Style = (Style)System.Windows.Application.Current.FindResource(style),
            ToolTip = tooltip ?? label,
            Margin = new Thickness(3, 2, 3, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (h != null) b.Click += h;
        Children.Add(b);
        return b;
    }
}
