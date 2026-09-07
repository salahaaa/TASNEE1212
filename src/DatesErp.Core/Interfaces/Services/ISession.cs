namespace DatesErp.Core.Interfaces.Services;

/// <summary>جلسة المستخدم الحالي عبر كل الطبقات.</summary>
public interface ICurrentSession
{
    int UserId { get; }
    string UserName { get; set; }
    string MachineName { get; }
    bool IsInRole(string roleCode);
    bool Can(string module, string action);
}

/// <summary>§11 — نتيجة تسجيل الدخول.</summary>
public class LoginResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public int UserId { get; set; }
    public string FullName { get; set; }
    public List<string> Roles { get; set; } = new();
    public bool MustChangePassword { get; set; }
    /// <summary>§لمسة مؤسسية: انتهت مدة كلمة المرور (قرار #47) — يُجبر التغيير.</summary>
    public bool PasswordExpired { get; set; }
    /// <summary>عمر كلمة المرور بالأيام — لعرضه للمستخدم.</summary>
    public int PasswordAgeDays { get; set; }
    /// <summary>§وعي أمني: آخر دخول ناجح لهذا الحساب.</summary>
    public DateTime? LastLoginDate { get; set; }
}

/// <summary>§12 — نتيجة اختبار الاتصال بالخادم — تفصيلي لكل طبقة.</summary>
public class ConnectionTestResult
{
    public bool ServerReachable { get; set; }
    public bool SqlServerResponding { get; set; }
    public bool DatabaseExists { get; set; }
    public bool LoginOk { get; set; }
    public bool ConnectionOk { get; set; }
    public string Message { get; set; }
    public string ServerVersion { get; set; }

    public bool AllOk => ServerReachable && SqlServerResponding && DatabaseExists && LoginOk && ConnectionOk;
}
