using DatesErp.Core.Domain.Entities;

namespace DatesErp.Core.Interfaces.Services;

/// <summary>§11 — المصادقة.</summary>
public interface IAuthService
{
    // §B84/H9: حُذف معامل rememberMe الميت (كان يخترق الواجهة والخدمة بلا أي أثر —
    // ميزة "تذكرني" الحقيقية ملف محلي في LoginWindow). من يحتاجه مستقبلاً يُنفَّذ فعلياً.
    LoginResult Login(string userName, string password);
    void Logout();
}

// §حُذفت IConnectionService في B40: كانت واجهة بخمس دوال لا مُنفِّذ لها ولا مُستخدِم.
// إعداد الاتصال يُدار فعلياً عبر AppConfig وConnectionTester وConnectionSetupWindow.

/// <summary>§9 — المخزون: أرصدة وحركات مرتبطة بمستنداتها.</summary>
public interface IInventoryService
{
    List<StockBalanceDto> GetBalances(int? warehouseId = null, int? productId = null);
    List<InventoryTransactionDto> GetTransactions(DateTime? from = null, DateTime? to = null, int? warehouseId = null);
}

public class StockBalanceDto
{
    public int WarehouseId { get; set; }
    public string WarehouseName { get; set; }
    public int? ProductId { get; set; }
    public string ItemName { get; set; }
    public string LotCode { get; set; }
    public string CustomerName { get; set; }
    public double QtyKg { get; set; }
    public int PackageCount { get; set; }
}

public class InventoryTransactionDto
{
    public string TxnNumber { get; set; }
    public string TxnDate { get; set; }
    public string WarehouseName { get; set; }
    public string ItemName { get; set; }
    public string LotCode { get; set; }
    public string MovementTypeAr { get; set; }
    public double QtyKg { get; set; }
    public string ReferenceDoc { get; set; }
    public string CreatedByUser { get; set; }
    public string MachineName { get; set; }
}

/// <summary>§26 — التدقيق.</summary>
public interface IAuditService
{
    void Log(string screen, string action, string docType, string docNumber, int? recordId = null, object oldValues = null, object newValues = null);
    List<AuditLog> Query(DateTime? from, DateTime? to, string user = null, string action = null);
}

/// <summary>ترقيم المستندات المركزي.</summary>
public interface INumberingService
{
    string Next(string schemeCode);
}

/// <summary>§10 — إدارة المستخدمين والأدوار والصلاحيات مركزياً.</summary>
public interface IAdminService
{
    List<AppUser> GetUsers();
    OpResult SaveUser(int? id, string userCode, string userName, string fullName, string password, List<int> roleIds, bool isActive);
    OpResult DeleteUser(int id);
    List<Role> GetRolesWithPermissions();
    OpResult SaveRolePermissions(int roleId, Dictionary<string, int> moduleMasks);
    List<ClientMachine> GetMachines();
}

/// <summary>§29/§30 — النسخ الاحتياطي والاستعادة من السيرفر مع التحقق.</summary>
public interface IBackupService
{
    OpResult FullBackup(string folderPath);
    OpResult VerifyBackup(string backupFile);
    OpResult Restore(string backupFile);
    List<string> ListBackups(string folderPath);
}

/// <summary>§25 — التقارير مع تصدير PDF/Excel وطباعة أصلية.</summary>
public interface IReportService
{
    List<ReportDefinition> GetReports();
    ReportResult Run(string reportCode, Dictionary<string, string> parameters);
}

public class ReportDefinition
{
    public string Code { get; set; }
    public string TitleAr { get; set; }
    public string Category { get; set; }
    public List<ReportParameter> Parameters { get; set; } = new();
}

public class ReportParameter
{
    public string Key { get; set; }
    public string LabelAr { get; set; }
    public string Kind { get; set; } = "text"; // text | date | number | list
    public List<(string value, string label)> Options { get; set; }
}

/// <summary>§التنقل من التقرير إلى المستند: زر + أمام كل صف يفتح مستنده الأصلي.</summary>
public class DocLinkDto
{
    /// <summary>receiving | planning | orders | quality | finishedgoods | delivery</summary>
    public string DocType { get; set; }
    public int Id { get; set; }
}

public class ReportResult
{
    public string TitleAr { get; set; }
    public List<string> Columns { get; set; } = new();
    public List<object[]> Rows { get; set; } = new();
    public Dictionary<string, string> Summary { get; set; } = new();
    /// <summary>§نموذج التقرير الاحترافي: نص الفترة المعروضة في الترويسة.</summary>
    public string PeriodLabel { get; set; } = "";
    /// <summary>§موازٍ للصفوف: رابط مستند كل صف (أو فارغ إن لا مستند) — للتقلقل الاحترافي.</summary>
    public List<DocLinkDto> RowLinks { get; set; }
}
