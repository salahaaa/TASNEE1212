using System.Text.Json;

namespace DatesErp.Infrastructure.Connection;

/// <summary>
/// §12/§13 — إعداد الاتصال المركزي يُحفظ على الجهاز في %ProgramData%\DateERP\config.json
/// ولا تُحفظ أي كلمة مرور بنص صريح: كلمات مرور SQL (إن لزم) تُشفَّر بـ DPAPI على ويندوز.
/// </summary>
public class AppConfig
{
    public string Server { get; set; }
    public string Database { get; set; } = "DateFactory";
    public string AuthMode { get; set; } = "Windows"; // Windows | Sql
    public string SqlUid { get; set; }
    public string EncryptedSqlPassword { get; set; }
    public string AppVersion { get; set; } = "1.0.0";

    /// <summary>مجلد الإعداد — في مجلد المستخدم المحلي (قابل للكتابة بلا صلاحيات إدارية).</summary>
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DateERP");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static bool Exists() => File.Exists(ConfigPath);

    public static AppConfig Load()
    {
        if (!Exists()) return null;
        try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)); }
        catch { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>بناء سلسلة الاتصال — §14 يدعم اسم الخادم أو Instance أو IP.</summary>
    public string BuildSqlServerConnectionString()
    {
        var cs = $"Server={Server};Database={Database};";
        cs += AuthMode == "Sql"
            ? $"User Id={SqlUid};Password={Protect.Unprotect(EncryptedSqlPassword)};TrustServerCertificate=True;"
            : "Integrated Security=True;TrustServerCertificate=True;";
        // §B80: MARS إلزامي — شاشات تفتح قارئاً وتنفذ استعلامات فرعية داخله
        // (نمط AsEnumerable + استعلام داخل الحلقة). بلا MARS ينهار SQL Server برسالة
        // «There is already an open DataReader...» بينما SQLite يتسامح — ولهذا كانت
        // أخطاء «رسالة خطأ عند الدخول/البحث» تظهر على جهاز المستخدم ولا تظهر في الاختبارات.
        cs += "Connect Timeout=8;MultipleActiveResultSets=True;";
        return cs;
    }
}

/// <summary>§13 — تشفير/فك تشفير كلمات المرور عبر DPAPI (ويندوز فقط) — لا نص صريح أبداً.</summary>
public static class Protect
{
    public static string ProtectText(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        if (!OperatingSystem.IsWindows())
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain)); // بيئة الاختبار فقط
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
            var enc = System.Security.Cryptography.ProtectedData.Protect(bytes, null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(enc);
        }
        catch { return null; }
    }

    public static string Unprotect(string protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return null;
        if (!OperatingSystem.IsWindows())
        {
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedB64)); }
            catch { return null; }
        }
        try
        {
            var enc = Convert.FromBase64String(protectedB64);
            var dec = System.Security.Cryptography.ProtectedData.Unprotect(enc, null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return System.Text.Encoding.UTF8.GetString(dec);
        }
        catch { return null; }
    }
}
