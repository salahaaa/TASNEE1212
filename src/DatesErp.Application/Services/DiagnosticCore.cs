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

    /// <summary>
    /// §فحوصات الإعداد التشغيلي — أشياء موجودة في القاعدة لكنها تُعطّل عملية بعينها.
    /// الفرق عن CheckSeedData: تلك تعدّ الصفوف، وهذه تتحقق أن **ما يطلبه الكود بالاسم**
    /// موجود فعلاً. غيابها لا يظهر عند بدء التشغيل بل عند أول محاولة استخدام،
    /// فيبدو عطلاً عشوائياً في شاشة بريئة.
    /// </summary>
    public static List<Finding> CheckOperational(DatesErpDbContext db)
    {
        var list = new List<Finding>();

        // §المخازن تُطلب بالكود الحرفي عبر WarehouseId(code) التي ترمي DomainException.
        // WTRT مضاف لاحقاً لدورة المعالجة، فقاعدة مُرقّاة من إصدار أقدم قد تفتقده
        // فتفشل كل عمليات المعالجة برسالة «المخزن WTRT غير معرّف».
        var wh = new[]
        {
            ("WRM", "مخزن المواد الخام — الاستلام والصرف للإنتاج"),
            ("WFG", "مخزن الإنتاج التام — استلام التام والتسليم"),
            ("WAUX", "مخزن المواد المساعدة"),
            ("WTRT", "مستودع المعالجة والتعقيم — دورة المعالجة")
        };
        var haveWh = db.Warehouses.AsNoTracking().Select(w => w.WarehouseCode).ToList();
        foreach (var (code, use) in wh)
            list.Add(new Finding($"المخزن {code}",
                haveWh.Contains(code),
                haveWh.Contains(code) ? use : $"مفقود — سيتعطل: {use}"));

        // §مخططات الترقيم تُطلب بالكود عبر Numbering.Next(code).
        var schemes = new[] { "SHIP", "PLAN", "ORD", "EXE", "QC", "FGR", "RCV", "CD", "TXN", "PCL", "LOT", "TASK", "TRT" };
        var haveSch = db.NumberingSchemes.AsNoTracking().Select(x => x.SchemeCode).ToList();
        var missSch = schemes.Where(x => !haveSch.Contains(x)).ToList();
        list.Add(new Finding("مخططات ترقيم المستندات",
            missSch.Count == 0,
            missSch.Count == 0 ? $"كل المخططات موجودة ({schemes.Length})"
                : "مفقودة: " + string.Join("، ", missSch) + " — المستندات لن تأخذ أرقاماً"));

        // §بوابة الصلاحيات: مورد ناقص في القاعدة = أزرار تختفي بلا سبب ظاهر.
        var haveRes = db.PermissionResources.AsNoTracking().Select(x => x.Code).ToList();
        var missRes = Core.Domain.Enums.PermissionModules.All
            .Where(m => !haveRes.Contains(m.Code)).Select(m => m.Code).ToList();
        list.Add(new Finding($"موارد الصلاحيات ({Core.Domain.Enums.PermissionModules.All.Length} مورداً)",
            missRes.Count == 0,
            missRes.Count == 0 ? "الكتالوج مكتمل"
                : "ناقصة: " + string.Join("، ", missRes) + " — أزرار ستختفي من الشاشات"));

        // §مستخدم فعّال واحد على الأقل: القفل خارج النظام عطل لا رجعة فيه من الواجهة.
        int active = db.Users.AsNoTracking().Count(u => u.IsActive);
        list.Add(new Finding("مستخدمون فعّالون",
            active >= 1,
            active >= 1 ? $"الموجود {active}" : "لا مستخدم فعّال — لن يستطيع أحد الدخول"));

        return list;
    }

    /// <summary>
    /// §فحوصات اتساق البيانات — العطل الصامت الذي لا يرفع استثناءً ولا يمنع الحفظ،
    /// بل يعطي **أرقاماً خاطئة** يبني عليها المستخدم قراراً. هذه أخطر من العطل الظاهر:
    /// العطل الظاهر يوقف العمل، والرقم الخاطئ يمرّ ويُعتمد.
    /// كلها للقراءة فقط — تُبلّغ ولا تُصلح، فالإصلاح التلقائي للأرصدة قرار محاسبي لا تقني.
    /// </summary>
    public static List<Finding> CheckDataIntegrity(DatesErpDbContext db)
    {
        var list = new List<Finding>();

        var lots = db.Lots.AsNoTracking().ToList();

        // §1) لا كمية سالبة: الرصيد السالب يعني حركة صرف تجاوزت حارس المنع.
        var neg = lots.Where(l => l.InStockQtyKg < -0.001 || l.ReservedQtyKg < -0.001
                               || l.UnderTreatmentQtyKg < -0.001 || l.TreatmentReadyQtyKg < -0.001).ToList();
        list.Add(new Finding("لا أرصدة دفعات سالبة",
            neg.Count == 0,
            neg.Count == 0 ? $"{lots.Count} دفعة سليمة"
                : $"{neg.Count} دفعة برصيد سالب: " + string.Join("، ", neg.Take(5).Select(x => x.LotCode))));

        // §2) المحجوز + تحت المعالجة لا يتجاوز المخزون، وإلا صار AvailableQtyKg صفراً
        // بلا سبب مفهوم فتبدو الدفعة «غير متاحة» وهي مليئة.
        var over = lots.Where(l => l.ReservedQtyKg + l.UnderTreatmentQtyKg > l.InStockQtyKg + 0.001).ToList();
        list.Add(new Finding("المحجوز وتحت المعالجة ضمن المخزون",
            over.Count == 0,
            over.Count == 0 ? "كل الدفعات متسقة"
                : $"{over.Count} دفعة الالتزام فيها يتجاوز الرصيد: "
                  + string.Join("، ", over.Take(5).Select(x =>
                      $"{x.LotCode} (مخزون {x.InStockQtyKg:N0} · محجوز {x.ReservedQtyKg:N0} · معالجة {x.UnderTreatmentQtyKg:N0})"))));

        // §3) تطابق «تحت المعالجة» على الدفعة مع مجموع العمليات الجارية فعلاً.
        // اختلافهما يعني كمية محتجزة عن الإنتاج بلا عملية معالجة تفسّرها — أو العكس.
        var openByLot = db.RawTreatments.AsNoTracking()
            .Where(t => t.Status == TreatmentStatuses.InProgress)
            .ToList()
            .GroupBy(t => t.LotId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.RemainingQtyKg));
        var mismatch = new List<string>();
        foreach (var l in lots)
        {
            double open = openByLot.TryGetValue(l.Id, out var v) ? v : 0;
            if (Math.Abs(open - l.UnderTreatmentQtyKg) > 0.01)
                mismatch.Add($"{l.LotCode} (الدفعة {l.UnderTreatmentQtyKg:N1} · العمليات {open:N1})");
        }
        list.Add(new Finding("«تحت المعالجة» يطابق عمليات المعالجة الجارية",
            mismatch.Count == 0,
            mismatch.Count == 0 ? "متطابق"
                : $"{mismatch.Count} دفعة غير متطابقة: " + string.Join("، ", mismatch.Take(5))));

        // §4) لكل حركة مخزون مستند: الحركة اليتيمة تكسر التتبع من التقرير إلى مصدره.
        int orphanTxn = db.InventoryTransactions.AsNoTracking()
            .Count(t => t.ReferenceDocNumber == null || t.ReferenceDocNumber == "");
        list.Add(new Finding("كل حركة مخزون مرتبطة بمستند",
            orphanTxn == 0,
            orphanTxn == 0 ? "لا حركات يتيمة" : $"{orphanTxn} حركة بلا رقم مستند — التتبع مقطوع"));

        // §5) رصيد المستودعات لا يكون سالباً على مستوى الصف.
        int negBal = db.StockBalances.AsNoTracking().Count(b => b.QtyKg < -0.001);
        list.Add(new Finding("لا أرصدة مخازن سالبة",
            negBal == 0,
            negBal == 0 ? "كل الأرصدة موجبة" : $"{negBal} رصيد سالب في StockBalances"));

        // §6) الدفعات المعلّقة على شحنة محذوفة — يتيمة تظهر في التقارير بلا مصدر.
        var shipIds = db.Shipments.AsNoTracking().Select(x => x.Id).ToHashSet();
        var orphanLots = lots.Where(l => l.ShipmentId != null && !shipIds.Contains(l.ShipmentId.Value)).ToList();
        list.Add(new Finding("كل دفعة مرتبطة بشحنة قائمة",
            orphanLots.Count == 0,
            orphanLots.Count == 0 ? "لا دفعات يتيمة"
                : $"{orphanLots.Count} دفعة تشير إلى شحنة غير موجودة: "
                  + string.Join("، ", orphanLots.Take(5).Select(x => x.LotCode))));

        return list;
    }

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
