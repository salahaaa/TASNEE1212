using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §قلب الفحص الذاتي — بلا أي اعتماد على WPF، فيُختبر ويُشغَّل على أي منصة.
/// شاشة «معلومات النظام» تستدعيه، ومشغّل القبول يستدعيه للتحقق من صحته.
///
/// الغرض: يجعل «هل العطل من جهازي أم من البرنامج؟» سؤالاً له جواب مكتوب.
/// </summary>
public static class DiagnosticCore
{
    /// <summary>نتيجة فحص واحد.</summary>
    public record Finding(string Name, bool Ok, string Detail);

    /// <summary>
    /// §B81 — هوية النظام والقاعدة: «هل أنا متصل بالقاعدة الصحيحة؟» بصرف النظر عن أي شيء آخر.
    /// الخادم والقاعدة يُقرآن من الاتصال المفتوح نفسه (SELECT @@SERVERNAME / DB_NAME())
    /// لا من نص الاتصال المطلوب — فالقاعدة الفعلية هي الحجة. مع إصدار القاعدة وأعداد
    /// الجداول الرئيسية التي تطابقها عين المستخدم مع ما يراه في الشاشات.
    /// </summary>
    public static List<(string Name, string Value)> GetIdentity(DatesErpDbContext db)
    {
        var rows = new List<(string, string)>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        bool sqlite = !db.Database.IsSqlServer();

        if (sqlite)
        {
            rows.Add(("المزوّد", "SQLite (قاعدة محلية على هذا الجهاز)"));
            // DataSource فارغ لقواعد الذاكرة (الاختبارات) — نصرّح بها بدل سطر فارغ مضلِّل
            rows.Add(("ملف القاعدة", string.IsNullOrWhiteSpace(conn.DataSource) ? "(قاعدة ذاكرة — بلا ملف)" : conn.DataSource));
        }
        else
        {
            rows.Add(("المزوّد", "SQL Server"));
            rows.Add(("الخادم (فعلياً من الاتصال)", Scalar(conn, "SELECT @@SERVERNAME") ?? conn.DataSource ?? "—"));
            rows.Add(("قاعدة البيانات (فعلياً)", Scalar(conn, "SELECT DB_NAME()") ?? conn.Database ?? "—"));
        }

        string dbv;
        try { dbv = db.DbVersions.AsNoTracking().OrderByDescending(v => v.Id).Select(v => v.VersionNumber).FirstOrDefault() ?? "غير معروف"; }
        catch { dbv = "تعذرت القراءة"; }
        rows.Add(("إصدار قاعدة البيانات", dbv));

        rows.Add(("عدد الأصناف", Count(db, "Products")));
        rows.Add(("عدد العملاء", Count(db, "Customers")));
        rows.Add(("خطط الإنتاج", Count(db, "ProductionPlans")));
        rows.Add(("أوامر الإنتاج", Count(db, "ProductionOrders")));
        rows.Add(("دفعات الخام", Count(db, "Lots")));
        rows.Add(("المستخدمون", Count(db, "Users")));
        return rows;
    }

    private static string Scalar(System.Data.Common.DbConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString();
        }
        catch { return null; }
    }

    private static string Count(DatesErpDbContext db, string table)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
            return Convert.ToInt32(cmd.ExecuteScalar()).ToString("N0");
        }
        catch { return "تعذر العد"; }
    }

    /// <summary>فحوصات الاتصال والمخطط: كل جدول وعمود في النموذج موجود فعلاً في القاعدة.</summary>
    public static List<Finding> CheckDatabase(DatesErpDbContext db)
    {
        var list = new List<Finding>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        bool sqlite = !db.Database.IsSqlServer();

        list.Add(new Finding("الاتصال بقاعدة البيانات", Safe(() => db.Database.CanConnect(), out var can) && can,
            db.Database.IsSqlServer() ? "SQL Server" : "SQLite"));

        var entities = db.Model.GetEntityTypes().Where(e => !string.IsNullOrEmpty(e.GetTableName())).ToList();
        var missingTables = new List<string>();
        var missingCols = new List<string>();

        foreach (var e in entities)
        {
            var t = e.GetTableName();
            if (!TableExists(conn, t, sqlite)) { missingTables.Add(t); continue; }
            var have = Columns(conn, t, sqlite);
            var id = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(t, e.GetSchema());
            foreach (var p in e.GetProperties())
            {
                var c = p.GetColumnName(id);
                if (!string.IsNullOrEmpty(c) && !have.Contains(c, StringComparer.OrdinalIgnoreCase))
                    missingCols.Add($"{t}.{c}");
            }
        }

        list.Add(new Finding($"جداول النموذج موجودة ({entities.Count} جدولاً)",
            missingTables.Count == 0,
            missingTables.Count == 0 ? "كل الجداول موجودة" : "ناقصة: " + string.Join("، ", missingTables.Take(10))));

        list.Add(new Finding("أعمدة النموذج موجودة",
            missingCols.Count == 0,
            missingCols.Count == 0 ? "كل الأعمدة موجودة"
                : $"ناقصة ({missingCols.Count}): " + string.Join("، ", missingCols.Take(10))));

        return list;
    }

    /// <summary>فحوصات البيانات الأولية التي بدونها تنهار الشاشات أو تختفي الأزرار.</summary>
    public static List<Finding> CheckSeedData(DatesErpDbContext db) => new()
    {
        Min("مستخدمون", db.Users.Count(), 1, "لا مستخدمين — لن تستطيع الدخول"),
        Min("أدوار", db.Roles.Count(), 1, "لا أدوار"),
        // §الكتالوج الفعلي 21 مورداً × 12 عملية (PermissionService.ResourceCatalog) — الحد 20 لا 50
        Min("موارد الصلاحيات", db.PermissionResources.Count(), 20, "كتالوج الصلاحيات ناقص — أزرار ستختفي"),
        Min("وحدات قياس", db.UnitsOfMeasure.Count(), 2, "لا وحدات — قوائم الوحدات ستظهر فارغة"),
        Min("مخازن", db.Warehouses.Count(), 1, "لا مخازن — الاستلام سيفشل"),
        Min("ورديات", db.Shifts.Count(), 1, "لا ورديات — الخطة ستفشل"),
        Min("أنواع نتائج الفحص", db.InspectionResultTypes.Count(x => x.IsActive), 1, "لا أنواع نتائج — شاشة الفحص بلا صفوف"),
        Min("مخططات الترقيم", db.NumberingSchemes.Count(), 1, "لا مخططات ترقيم — المستندات لن تأخذ أرقاماً"),
    };

    // ── أدوات ──

    private static Finding Min(string name, int actual, int min, string problem)
        => new(name, actual >= min, actual >= min ? $"الموجود {actual}" : $"{problem} (الموجود {actual} · المطلوب ≥ {min})");

    private static bool Safe(Func<bool> act, out bool value)
    {
        try { value = act(); return true; }
        catch { value = false; return false; }
    }

    private static bool TableExists(System.Data.Common.DbConnection conn, string table, bool sqlite)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlite
            ? $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table.Replace("'", "''")}'"
            : $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='{table.Replace("'", "''")}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static HashSet<string> Columns(System.Data.Common.DbConnection conn, string table, bool sqlite)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        if (sqlite)
        {
            cmd.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(1));
            return set;
        }
        cmd.CommandText = $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='{table.Replace("'", "''")}'";
        using var r2 = cmd.ExecuteReader();
        while (r2.Read()) set.Add(r2.GetString(0));
        return set;
    }
}
