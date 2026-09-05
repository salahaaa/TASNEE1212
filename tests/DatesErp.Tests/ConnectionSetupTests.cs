using DatesErp.Infrastructure.Connection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §إصلاح شاشة إعداد الاتصال (B49):
/// 1) سبب NullReferenceException الجذري: AuthModeChanged كان ينفذ أثناء InitializeComponent
///    قبل إنشاء UidLabel/UidBox/PwdLabel/PwdBox. أُصلح بحارس _ready + OnInitialized + فحوص null.
/// 2) Named Instance (.\SQLEXPRESS01) كان يفشل خطأً في فحص TCP على 1433 لأن Named Instance
///    يستعمل منفذاً ديناميكياً عبر SQL Browser. أُصلح بتجاوز فحص TCP للـ Named Instance.
/// 3) سلسلة الاتصال تُبنى صحيحة لـ Windows و Sql auth.
/// </summary>
public class ConnectionSetupTests
{
    // ── سلسلة الاتصال ──

    [Fact]
    public void ConnectionString_WindowsAuth_UsesIntegratedSecurity()
    {
        var cfg = new AppConfig { Server = @".\SQLEXPRESS01", Database = "DateFactory", AuthMode = "Windows" };
        var cs = cfg.BuildSqlServerConnectionString();
        Assert.Contains("Integrated Security=True", cs);
        Assert.DoesNotContain("User Id", cs);
        Assert.Contains(@"Server=.\SQLEXPRESS01", cs);
        Assert.Contains("Database=DateFactory", cs);
    }

    [Fact]
    public void ConnectionString_SqlAuth_UsesUserIdAndPassword()
    {
        var cfg = new AppConfig
        {
            Server = @".\SQLEXPRESS01",
            Database = "DateFactory",
            AuthMode = "Sql",
            SqlUid = "sa",
            EncryptedSqlPassword = Protect.ProtectText("P@ssw0rd")
        };
        var cs = cfg.BuildSqlServerConnectionString();
        Assert.Contains("User Id=sa", cs);
        Assert.Contains("Password=", cs);
        Assert.DoesNotContain("Integrated Security=True", cs);
        // كلمة المرور تُفكّ من DPAPI وتُوضع في السلسلة (لا نص صريح في الملف)
        Assert.Contains("P@ssw0rd", cs);
    }

    [Fact]
    public void SqlPassword_Is_Not_Stored_As_Plaintext()
    {
        var cfg = new AppConfig { AuthMode = "Sql", SqlUid = "sa", EncryptedSqlPassword = Protect.ProtectText("Secret123") };
        var json = System.Text.Json.JsonSerializer.Serialize(cfg);
        Assert.DoesNotContain("Secret123", json);   // لا نص صريح في الملف المحفوظ
        Assert.Contains("EncryptedSqlPassword", json);
    }

    // ── Named Instance: تجاوز فحص TCP ──

    [Fact]
    public void NamedInstance_DoesNot_FailOnTcpPreCheck()
    {
        // §.\SQLEXPRESS01 named instance: لا منفذ 1433 ثابت، بل ديناميكي عبر SQL Browser.
        // على لينكس بلا SQL Server سيفشل الاتصال الفعلي، لكن يجب ألا يفشل بفحص TCP
        // برسالة "تعذر الوصول إلى الخادم" — بل برسالة اتصال حقيقية.
        var tester = new ConnectionTester();
        var r = tester.Test(@".\SQLEXPRESS01", "DateFactory", "Windows");
        // Named instance: ServerReachable يُفترض true (تجاوز فحص TCP)
        Assert.True(r.ServerReachable, "Named Instance يجب أن يتجاوز فحص TCP على 1433");
        // لكنه سيفشل في الاتصال الفعلي (بلا SQL Server هنا) برسالة حقيقية لا "تعذر الوصول"
        Assert.False(r.ConnectionOk);
        Assert.DoesNotContain("تعذر الوصول إلى الخادم", r.Message);
    }

    [Fact]
    public void DefaultInstance_Unreachable_FailsOnTcpPreCheck()
    {
        // Default instance بلا منفذ صريح: فحص TCP على 1433.
        // منفذ مغلق مؤكد على localhost → فشل TCP سريع (connection refused).
        var tester = new ConnectionTester();
        var r = tester.Test("127.0.0.1", "DateFactory", "Windows");
        Assert.False(r.ServerReachable);
        Assert.Contains("تعذر الوصول إلى الخادم", r.Message);
    }

    [Fact]
    public void ExplicitPort_SkipsTcpPreCheck_On1433()
    {
        // منفذ صريح (,9999): نتجاوز فحص TCP الافتراضي على 1433 ونترك الاتصال الفعلي يحسم.
        // ServerReachable يُفترض true لأننا تجاوزنا الفحص، لكن الاتصال الفعلي سيفشل.
        var tester = new ConnectionTester();
        var r = tester.Test("127.0.0.1,9999", "DateFactory", "Windows");
        Assert.True(r.ServerReachable, "منفذ صريح: يُتجاوز فحص TCP الافتراضي على 1433");
        Assert.False(r.ConnectionOk);   // لكن الاتصال الفعلي يفشل (بلا SQL Server)
    }
}
