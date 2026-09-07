using System.Windows.Controls;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §البحث والفلترة الموحدة لكل الشاشات: مربع بحث يفلتر صفوف الشبكة بأي نص
/// يظهر في أي عمود (اسم، كود، رقم مستند، تاريخ، حالة...) — لحظياً ومع كل تحديث.
/// </summary>
public static class ScreenSearch
{
    /// <summary>فلترة قائمة الصفوف حسب نص البحث وعرض النتيجة في الشبكة.</summary>
    public static void Apply<T>(TextBox searchBox, DataGrid grid, List<T> all)
    {
        if (grid == null || all == null) return;
        string term = searchBox?.Text?.Trim().ToLowerInvariant() ?? "";
        grid.ItemsSource = string.IsNullOrEmpty(term)
            ? all
            : all.Where(x => Matches(x, term)).ToList();
    }

    private static bool Matches(object row, string term)
    {
        if (row == null) return false;
        foreach (var p in row.GetType().GetProperties())
        {
            try
            {
                if (p.GetValue(row)?.ToString()?.ToLowerInvariant().Contains(term) == true)
                    return true;
            }
            catch { /* خاصية غير قابلة للقراءة — نتجاوزها */ }
        }
        return false;
    }
}
