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

    /// <summary>§12 — الوحدة (المورد) التي يعمل عليها الشريط، لبوابة الأزرار المركزية.</summary>
    public string Module { get; private set; }

    /// <summary>العملية المطلوبة لكل زر — تُملأ تلقائياً عند بناء الزر ويقرأها <see cref="ApplyPermissions"/>.</summary>
    private readonly Dictionary<Button, string> _btnOps = new();

    /// <summary>أزرار عُطّلت بسبب الصلاحية — لا يجوز لأي منطق شاشة أن يعيد تفعيلها.</summary>
    private readonly HashSet<Button> _denied = new();

    public ErpToolbar()
    {
        Orientation = Orientation.Horizontal;
        FlowDirection = FlowDirection.RightToLeft;
        ItemHeight = 34;
    }

    /// <summary>
    /// §12 — بوابة الأزرار المركزية: تُربط الشاشة بوحدتها مرة واحدة، فيخفي الشريطُ تلقائياً
    /// كل زر لا يملك المستخدم عمليته (جديد/تعديل/حذف/اعتماد/طباعة/تصدير...)، بدل تكرار الفحص شاشةً شاشة.
    /// تُستدعى بعد اكتمال بناء الأزرار (يستدعيها ErpChrome.SetToolbar تلقائياً).
    /// </summary>
    public ErpToolbar ForModule(string module)
    {
        Module = module;
        ApplyPermissions();
        return this;
    }

    /// <summary>
    /// إخفاء ما لا يُسمح به. الإخفاء (لا التعطيل) هو السلوك: زر معطّل بلا سبب ظاهر يربك المستخدم.
    /// «تحديث» و«بحث» و«خروج» و«القائمة» والتنقل لا تُحجب — فتح الشاشة نفسه محكوم ببوابة View.
    /// </summary>
    public void ApplyPermissions()
    {
        if (string.IsNullOrWhiteSpace(Module)) return;
        foreach (var kv in _btnOps)
        {
            if (kv.Value == null || kv.Key == null) continue;
            bool ok = PermissionGate.Can(Module, kv.Value);
            kv.Key.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            if (ok) _denied.Remove(kv.Key); else _denied.Add(kv.Key);
        }
    }

    /// <summary>هل الزر محجوب بالصلاحية؟ تستخدمها الشاشات كي لا تعيد تفعيل ما مُنع.</summary>
    public bool IsDenied(Button b) => b != null && _denied.Contains(b);

    public ErpToolbar WithNew(RoutedEventHandler h, string label = "➕ جديد (F2)")
    { NewBtn = AddBtn(label, "ErpPrimaryButton", h, null, "Create"); return this; }

    public ErpToolbar WithSave(RoutedEventHandler h, string label = "💾 حفظ (F10)")
    { SaveBtn = AddBtn(label, "ErpPrimaryButton", h, null, "Edit"); return this; }

    public ErpToolbar WithUndo(RoutedEventHandler h, string label = "↩ تراجع")
    { UndoBtn = AddBtn(label, "ErpButton", h); return this; }

    public ErpToolbar WithEdit(RoutedEventHandler h)
    { EditBtn = AddBtn("✏️ تعديل (F3)", "ErpButton", h, null, "Edit"); return this; }

    public ErpToolbar WithDelete(RoutedEventHandler h)
    { DeleteBtn = AddBtn("🗑️ حذف (F8)", "ErpDangerButton", h, null, "Delete"); return this; }

    public ErpToolbar WithSearch(RoutedEventHandler h, string tooltip = null)
    { SearchBtn = AddBtn("🔍 بحث (F9)", "ErpButton", h, tooltip); return this; }

    public ErpToolbar WithRefresh(RoutedEventHandler h)
    { RefreshBtn = AddBtn("🔄 تحديث (F5)", "ErpButton", h); return this; }

    public ErpToolbar WithPrint(RoutedEventHandler h, string label = "🖨️ طباعة (Ctrl+P)")
    { PrintBtn = AddBtn(label, "ErpButton", h, null, "Print"); return this; }

    public ErpToolbar WithExcel(RoutedEventHandler h)
    { ExcelBtn = AddBtn("📊 Excel", "ErpButton", h, null, "Export"); return this; }

    public ErpToolbar WithApprove(RoutedEventHandler h, string label = "🔒 اعتماد")
    { ApproveBtn = AddBtn(label, "ErpApproveButton", h, null, "Approve"); return this; }

    public ErpToolbar WithUnapprove(RoutedEventHandler h, string tooltip = null)
    { UnapproveBtn = AddBtn("🔓 إلغاء الاعتماد", "ErpDangerButton", h, tooltip, "Cancel"); return this; }

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

    /// <summary>زر مخصص — مرّر <paramref name="requiresOp"/> ليخضع للبوابة المركزية مثل بقية الأزرار.</summary>
    public ErpToolbar WithCustom(string label, string style, RoutedEventHandler h, string tooltip = null, string requiresOp = null)
    { AddBtn(label, style, h, tooltip, requiresOp); return this; }

    public void SetLocked(bool locked, string lockMsg = null)
    {
        foreach (var b in new[] { NewBtn, EditBtn, DeleteBtn, SaveBtn, UndoBtn, ApproveBtn })
            if (b != null) b.IsEnabled = !locked && !_denied.Contains(b); // المحجوب بالصلاحية لا يُفتح بفكّ القفل
        if (locked && lockMsg != null && SaveBtn != null) SaveBtn.Content = lockMsg;
    }

    private Button AddBtn(string label, string style, RoutedEventHandler h, string tooltip = null, string requiresOp = null)
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
        if (requiresOp != null)
        {
            _btnOps[b] = requiresOp;
            if (!string.IsNullOrWhiteSpace(Module) && !PermissionGate.Can(Module, requiresOp))
            { b.Visibility = Visibility.Collapsed; _denied.Add(b); }
        }
        return b;
    }
}
