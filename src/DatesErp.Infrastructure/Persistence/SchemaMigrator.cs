using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DatesErp.Infrastructure.Persistence;

/// <summary>
/// §الترحيل الآمن الشامل: يضمن أن قاعدة بيانات المستخدم (مهما كان عمرها وإصدارها)
/// تحتوي على كل الجداول وكل الأعمدة الموجودة في نموذج النظام الحالي — بلا حذف بيانات أبداً.
/// يُنفَّذ عند الإقلاع على SQLite وعلى SQL Server على حد سواء.
///
/// لماذا هذا ضروري: المستخدم يحدّث النظام بنسخ ملفات DLL فقط (لا مثبّت ولا حذف قاعدة)،
/// فأي جدول أو عمود أُضيف في إصدار لاحق يجب إنشاؤه/إلحاقه تلقائياً وإلا فشلت الشاشات
/// بأخطاء «عمود/جدول غير موجود» — وهذا بالضبط ما كان يُخفي العملاء ويعطل زر اختيار الأصناف.
/// </summary>
public static class SchemaMigrator
{
    /// <summary>ينفذ الترحيل ويعيد قائمة وصفية بما تم إنشاؤه/إضافته (أو الأخطاء إن وقعت).</summary>
    public static List<string> Migrate(DatesErpDbContext db)
    {
        var report = new List<string>();
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) conn.Open();
            bool sqlite = !db.Database.IsSqlServer();

            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (string.IsNullOrEmpty(table)) continue;
                var tableId = StoreObjectIdentifier.Table(table, entity.GetSchema());

                try
                {
                    if (!TableExists(conn, table, sqlite))
                    {
                        Exec(conn, BuildCreateTable(entity, table, tableId, sqlite));
                        report.Add($"تم إنشاء الجدول المفقود: {table}");
                        continue; // جدول جديد — كل أعمدته موجودة بالتعريف
                    }

                    // جدول قائم: تأكد من وجود كل الأعمدة
                    var existing = GetColumns(conn, table, sqlite);
                    foreach (var prop in entity.GetProperties())
                    {
                        var col = prop.GetColumnName(tableId);
                        if (string.IsNullOrEmpty(col)) continue;
                        if (existing.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;

                        var type = prop.GetColumnType(tableId);
                        // SQLite يقبل «ADD COLUMN» بينما SQL Server يتطلب «ADD» بدون COLUMN
                        var addSql = sqlite
                            ? $"ALTER TABLE [{table}] ADD COLUMN [{col}] {type} NULL"
                            : $"ALTER TABLE [{table}] ADD [{col}] {type} NULL";
                        Exec(conn, addSql);
                        FillNulls(conn, table, col, prop);
                        report.Add($"تمت إضافة العمود المفقود: {table}.{col}");
                    }

                    // §إصلاح الأعطال الشاملة: تعبئة أي قيمة فارغة (NULL) في الأعمدة غير
                    // القابلة للفراغ — هذا يحل خطأ «The data is NULL at ordinal» نهائياً،
                    // سواء كان الفراغ من عمود أُضيف حديثاً أو من بيانات قديمة.
                    SweepNulls(conn, table, entity, tableId);
                }
                catch (Exception exTable)
                {
                    report.Add($"خطأ في ترحيل {table}: {exTable.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            report.Add("خطأ عام في الترحيل: " + ex.Message);
        }

        // §تتبع الصنف: الربط الآلي لتعريفات التحويل الرسمية في القواعد القديمة
        LinkProductSources(db, report);

        // §نظام الوحدات: توحيد المجموعات (001 خام | 002 تام | 003 مخرجات ثانوية)
        NormalizeGroups(db, report);

        // §B87/M6: توحيد العميل الفارغ — الصفر القديم يعني «بلا عميل» فيُرحَّل إلى NULL
        NullifyZeroCustomers(db, report);

        return report;
    }

    /// <summary>
    /// §نظام الوحدات — المعيار الرسمي للمجموعات:
    /// 001 مواد خام | 002 منتجات تامة | 003 مخرجات ثانوية (وحداتها كجم).
    /// القواعد القديمة: المجموعة 003 كانت «مواد مساعدة» و004 «أصناف ثانوية» —
    /// تُرحَّل الأصناف الثانوية إلى 003، والمواد المساعدة كيان مستقل خارج مجموعات الأصناف.
    /// </summary>
    private static void NormalizeGroups(DatesErpDbContext db, List<string> report)
    {
        try
        {
            bool changed = false;

            // مجموعة 003 تصبح «المخرجات الثانوية»
            var g3 = db.ItemGroups.FirstOrDefault(g => g.GroupCode == "003");
            if (g3 != null && g3.GroupType != "ByProduct")
            {
                g3.GroupNameAr = "المخرجات الثانوية";
                g3.GroupType = "ByProduct";
                g3.DefaultUnit = "كجم";
                changed = true;
                report.Add("تم توحيد المجموعة 003 = المخرجات الثانوية (كجم)");
            }

            // مجموعة 004 (أصناف ثانوية قديمة) تُعلَّم كياناتها وتنقل أصنافها إلى 003
            var g4 = db.ItemGroups.FirstOrDefault(g => g.GroupCode == "004");
            if (g4 != null && g4.GroupType == "ByProduct")
            {
                g4.GroupNameAr = "أصناف ثانوية (قديمة — رُحلت إلى 003)";
                g4.IsActive = false;
                changed = true;
            }
            foreach (var p in db.Products.Where(p => p.GroupCode == "004"))
            {
                p.GroupCode = "003";
                if (p.ItemType != "ByProduct") p.ItemType = "ByProduct";
                if (p.UnitOfMeasure != "كجم") p.UnitOfMeasure = "كجم"; // §المخرجات الثانوية كجم دائماً
                changed = true;
            }

            // مطابقة المجموعة مع نوع الصنف للسلالات الثلاث
            foreach (var p in db.Products.Where(p => p.ItemType == "Raw" && (p.GroupCode == null || p.GroupCode == "")))
            { p.GroupCode = "001"; changed = true; }
            foreach (var p in db.Products.Where(p => p.ItemType == "Finished" && (p.GroupCode == null || p.GroupCode == "")))
            { p.GroupCode = "002"; changed = true; }
            foreach (var p in db.Products.Where(p => p.ItemType == "ByProduct" && (p.GroupCode == null || p.GroupCode == "" || p.GroupCode == "004")))
            { p.GroupCode = "003"; changed = true; }

            if (changed) db.SaveChanges();
        }
        catch (Exception ex) { report.Add("خطأ في توحيد المجموعات: " + ex.Message); }
    }

    /// <summary>
    /// §تتبع الصنف: كل منتج تام بلا تعريف مصدر يُربط آلياً بالصنف الخام الذي تظهر
    /// كلمته (السلالة) في اسمه — «سكري فاخر 1كجم» ← «تمر خام - سكري».
    /// هكذا لا تكسر الترقية القواعد القديمة: التحويل يُستنتج من الأسماء ثم يُثبَّت في البطاقة.
    /// </summary>
    private static void LinkProductSources(DatesErpDbContext db, List<string> report)
    {
        try
        {
            var raws = db.Products.AsNoTracking().Where(p => p.ItemType == "Raw" && p.IsActive).ToList();
            if (raws.Count == 0) return;
            bool changed = false;
            foreach (var fin in db.Products.Where(p => p.ItemType == "Finished" && p.SourceProductId == null).ToList())
            {
                var match = raws.Select(r => (r, token: RawToken(r.ProductNameAr)))
                    .Where(x => x.token.Length >= 2 && (fin.ProductNameAr ?? "").Contains(x.token))
                    .OrderByDescending(x => x.token.Length)
                    .Select(x => x.r)
                    .FirstOrDefault();
                if (match != null)
                {
                    fin.SourceProductId = match.Id;
                    changed = true;
                    report.Add($"تم ربط تحويل رسمي آلياً: {fin.ProductNameAr} ← {match.ProductNameAr}");
                }
            }
            if (changed) db.SaveChanges();
        }
        catch (Exception ex) { report.Add("خطأ في ربط تحويلات الأصناف: " + ex.Message); }
    }

    /// <summary>كلمة السلالة في اسم الخام: «تمر خام - سكري» ← «سكري».</summary>
    private static string RawToken(string rawName)
    {
        var t = (rawName ?? "").Replace("تمر خام", "").Replace("مواد خام", "").Replace("خام", "");
        return t.Trim(' ', '-', '–', '—', ':', '.');
    }

    /// <summary>هل الجدول موجود؟</summary>
    public static bool TableExists(IDbConnection conn, string table, bool sqlite)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlite
            ? $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"
            : $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='{table}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static HashSet<string> GetColumns(IDbConnection conn, string table, bool sqlite)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlite
            ? $"SELECT name FROM pragma_table_info('{table}')"
            : $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='{table}'";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) set.Add(rd.GetString(0));
        return set;
    }

    /// <summary>بناء أمر CREATE TABLE لجدول مفقود من وصف نموذج EF مباشرة.</summary>
    private static string BuildCreateTable(IEntityType entity, string table, StoreObjectIdentifier tableId, bool sqlite)
    {
        var pk = entity.FindPrimaryKey();
        var colDefs = new List<string>();
        foreach (var prop in entity.GetProperties())
        {
            var col = prop.GetColumnName(tableId);
            var type = prop.GetColumnType(tableId);
            bool isPk = pk?.Properties.Any(p => p.Name == prop.Name) == true;
            bool autoInt = isPk && pk!.Properties.Count == 1
                           && prop.ClrType == typeof(int)
                           && prop.ValueGenerated == ValueGenerated.OnAdd;

            string def = $"[{col}] {type}";
            if (sqlite && autoInt)
                def += " PRIMARY KEY AUTOINCREMENT";          // SQLite: مفتاح تلقائي
            else if (!sqlite && autoInt)
                def += " IDENTITY(1,1) NOT NULL";              // SQL Server: عدّاد تلقائي
            else if (!prop.IsNullable)
                def += " NOT NULL";
            colDefs.Add(def);
        }
        // مفتاح مركّب (مثل UserRoles / RolePermissions)
        if (pk != null && pk.Properties.Count > 1)
            colDefs.Add($"CONSTRAINT [PK_{table}] PRIMARY KEY (" +
                        string.Join(", ", pk.Properties.Select(p => $"[{p.GetColumnName(tableId)}]")) + ")");
        else if (pk != null && pk.Properties.Count == 1)
        {
            var kp = pk.Properties[0];
            bool autoInt = kp.ClrType == typeof(int) && kp.ValueGenerated == ValueGenerated.OnAdd;
            if (sqlite && autoInt) { /* عولج داخل تعريف العمود */ }
            else if (!sqlite && autoInt) colDefs.Add($"CONSTRAINT [PK_{table}] PRIMARY KEY ([{kp.GetColumnName(tableId)}])");
            else colDefs.Add($"CONSTRAINT [PK_{table}] PRIMARY KEY ([{kp.GetColumnName(tableId)}])");
        }
        return $"CREATE TABLE [{table}] ({string.Join(", ", colDefs)})";
    }

    /// <summary>قيمة افتراضية لعمود غير قابل للفراغ — أو null إن لم يكن له افتراضي معروف.</summary>
    private static string DefaultFill(IProperty prop)
    {
        // §الحالة النشطة افتراضياً: أعمدة IsActive الفارغة تُملأ «نشط» (1) — لا «موقوف»،
        // حتى لا تختفي السجلات (عملاء/أصناف/موردون...) من القوائم المنسدلة
        if (prop.Name == "IsActive") return "1";

        var t = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return "0";
        if (t == typeof(bool)) return "0";
        if (t == typeof(string)) return "''";
        if (t == typeof(DateTime)) return "'2000-01-01'";
        if (t.IsEnum) return "0";
        return null;
    }

    /// <summary>تعبئة الفراغات في عمود واحد (بعد إضافته حديثاً).</summary>
    private static void FillNulls(IDbConnection conn, string table, string col, IProperty prop)
    {
        if (prop.IsNullable) return;
        var fill = DefaultFill(prop);
        if (fill == null) return;
        try { Exec(conn, $"UPDATE [{table}] SET [{col}] = {fill} WHERE [{col}] IS NULL"); }
        catch { /* الأفضل ألا يعطل الترحيل */ }
    }

    /// <summary>
    /// §إصلاح الأعطال الشاملة: يمسح كل جدول ويملأ أي قيمة فارغة (NULL) في أي عمود غير
    /// قابل للفراغ — بصرف النظر إن كان العمود مضافاً حديثاً أو قديماً وفيه بيانات فارغة.
    /// هذا يحل نهائياً خطأ: «The data is NULL at ordinal ... can't be called on NULL values».
    /// </summary>
    private static void SweepNulls(IDbConnection conn, string table, IEntityType entity, StoreObjectIdentifier tableId)
    {
        foreach (var prop in entity.GetProperties())
        {
            if (prop.IsNullable) continue; // القابل للفراغ يتحمله EF بلا مشكلة
            var col = prop.GetColumnName(tableId);
            if (string.IsNullOrEmpty(col)) continue;
            var fill = DefaultFill(prop);
            if (fill == null) continue;
            try { Exec(conn, $"UPDATE [{table}] SET [{col}] = {fill} WHERE [{col}] IS NULL"); }
            catch { /* لا نوقف الإقلاع على عمود واحد */ }
        }
    }

    /// <summary>
    /// §B87/M6: «بدون عميل» كانت تُخزَّن صفراً في بعض المسارات القديمة — تُرحَّل إلى NULL
    /// (الصفر ليس عميلاً حقيقياً ويكسر التقارير والتجميع). آمنة تكرارياً: الصفر فقط يتحول.
    /// </summary>
    private static void NullifyZeroCustomers(DatesErpDbContext db, List<string> report)
    {
        try
        {
            int n = 0;
            foreach (var i in db.ProductionPlanItems.Where(i => i.CustomerId == 0)) { i.CustomerId = null; n++; }
            foreach (var i in db.ProductionOrderItems.Where(i => i.CustomerId == 0)) { i.CustomerId = null; n++; }
            foreach (var o in db.ProductionOrders.Where(o => o.CustomerId == 0)) { o.CustomerId = null; n++; }
            foreach (var l in db.Lots.Where(l => l.CustomerId == 0)) { l.CustomerId = null; n++; }
            if (n > 0) { db.SaveChanges(); report.Add($"تم ترحيل {n} سجلاً من عميل-صفر إلى «بدون عميل» (NULL)"); }
        }
        catch (Exception ex) { report.Add("خطأ في ترحيل العملاء الأصفار: " + ex.Message); }
    }

    private static void Exec(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
