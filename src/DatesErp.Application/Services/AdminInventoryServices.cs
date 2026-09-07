using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§10/§27 — إدارة المستخدمين والأدوار والصلاحيات والأجهزة مركزياً.</summary>
public class AdminService : ServiceBase, IAdminService
{
    public AdminService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    [System.Obsolete("GenericListView يقرأ من DbContext مباشرة — هذه الدالة غير مستخدمة.")]
    public List<AppUser> GetUsers()
    {
        Require("users", "View");
        return Db.Users.Include(u => u.UserRoles).OrderBy(u => u.Id).ToList();
    }

    public OpResult SaveUser(int? id, string userCode, string userName, string fullName, string password, List<int> roleIds, bool isActive)
    {
        Require("users", id == null ? "Create" : "Edit");
        return RunOp(() =>
        {
            var user = id == null ? new AppUser() : Db.Users.Include(u => u.UserRoles).FirstOrDefault(u => u.Id == id);
            if (user == null) throw new DomainException("المستخدم غير موجود.");
            if (Db.Users.Any(u => u.UserName == userName && u.Id != user.Id))
                throw new DomainException("اسم المستخدم موجود مسبقاً.");
            // §الدخول بالرقم: رقم الدخول فريد — هذا الرقم محجوز
            if (!string.IsNullOrWhiteSpace(userCode) && Db.Users.Any(u => u.UserCode == userCode && u.Id != user.Id))
                throw new DomainException("هذا الرقم محجوز — اختر رقم دخول آخر.");
            if (string.IsNullOrWhiteSpace(userCode))
            {
                int max = Db.Users.AsNoTracking().Select(u => u.UserCode).ToList()
                    .Select(c => int.TryParse(c, out var n) ? n : 0).DefaultIfEmpty(1000).Max();
                userCode = (max + 1).ToString();
            }

            user.UserCode = userCode;
            user.UserName = userName;
            user.FullName = fullName;
            user.IsActive = isActive;
            if (!string.IsNullOrEmpty(password))
            {
                var (h, s) = PasswordHasher.Hash(password);
                user.PasswordHash = h;
                user.PasswordSalt = s;
                user.MustChangePassword = true;
            }
            if (id == null) Db.Users.Add(user);
            Db.SaveChanges();

            Db.UserRoles.RemoveRange(Db.UserRoles.Where(r => r.UserId == user.Id));
            foreach (var rid in roleIds.Distinct())
                Db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = rid });
            Db.SaveChanges();
            return OpResult.Success(id == null
                ? $"تم إنشاء المستخدم — رقم الدخول: {user.UserCode}."
                : $"تم حفظ التعديلات — رقم الدخول: {user.UserCode}.", user.Id, user.UserCode);
        });
    }

    public OpResult DeleteUser(int id)
    {
        Require("users", "Delete");
        if (id == Session?.UserId) return OpResult.Fail("لا يمكنك حذف حسابك الحالي.");
        var user = Db.Users.FirstOrDefault(u => u.Id == id);
        if (user == null) return OpResult.Fail("المستخدم غير موجود.");
        return RunOp(() =>
        {
            user.IsActive = false; // تعطيل بدل الحذف حفاظاً على سلسلة التدقيق
            Db.SaveChanges();
            return OpResult.Success("تم تعطيل المستخدم.");
        });
    }

    public List<Role> GetRolesWithPermissions()
    {
        Require("users", "View");
        return Db.Roles.Include(r => r.Permissions).OrderBy(r => r.Id).ToList();
    }

    [System.Obsolete("استخدم PermissionService.SetRolePermission — النموذج الهرمي هو المعتمد.")]
    public OpResult SaveRolePermissions(int roleId, Dictionary<string, int> moduleMasks)
    {
        Require("users", "Edit");
        return RunOp(() =>
        {
            foreach (var kv in moduleMasks)
            {
                var p = Db.RolePermissions.FirstOrDefault(x => x.RoleId == roleId && x.ModuleCode == kv.Key);
                if (p == null) Db.RolePermissions.Add(new RolePermission { RoleId = roleId, ModuleCode = kv.Key, PermissionMask = kv.Value });
                else p.PermissionMask = kv.Value;
            }
            Db.SaveChanges();
            return OpResult.Success("تم حفظ مصفوفة الصلاحيات.");
        });
    }

    public List<ClientMachine> GetMachines()
    {
        Require("settings", "View");
        return Db.ClientMachines.OrderByDescending(m => m.LastSeen).ToList();
    }
}

/// <summary>§9 — الاستعلام عن الأرصدة والحركات مع بيانات التتبع الكاملة.</summary>
public class InventoryService : ServiceBase, IInventoryService
{
    public InventoryService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public List<StockBalanceDto> GetBalances(int? warehouseId = null, int? productId = null)
    {
        Require("inventory", "View");
        var q = Db.StockBalances.AsQueryable();
        if (warehouseId != null) q = q.Where(b => b.WarehouseId == warehouseId);
        if (productId != null) q = q.Where(b => b.ProductId == productId);
        return q.Select(b => new StockBalanceDto
        {
            WarehouseId = b.WarehouseId,
            WarehouseName = Db.Warehouses.Where(w => w.Id == b.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
            ProductId = b.ProductId,
            ItemName = b.ProductId != null
                ? Db.Products.Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                : Db.AuxiliaryMaterials.Where(m => m.Id == b.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
            LotCode = Db.Lots.Where(l => l.Id == b.LotId).Select(l => l.LotCode).FirstOrDefault(),
            CustomerName = Db.Customers.Where(c => c.Id == b.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
            QtyKg = b.QtyKg,
            PackageCount = b.PackageCount
        }).Where(b => b.QtyKg != 0 || b.PackageCount != 0).ToList();
    }

    public List<InventoryTransactionDto> GetTransactions(DateTime? from = null, DateTime? to = null, int? warehouseId = null)
    {
        Require("inventory", "View");
        var q = Db.InventoryTransactions.AsQueryable();
        if (from != null) q = q.Where(t => t.TxnDate >= from);
        if (to != null) q = q.Where(t => t.TxnDate <= to.Value.AddDays(1));
        if (warehouseId != null) q = q.Where(t => t.WarehouseId == warehouseId);
        return q.OrderByDescending(t => t.TxnDate).Take(2000).Select(t => new InventoryTransactionDto
        {
            TxnNumber = t.TxnNumber,
            TxnDate = t.TxnDate.ToString("dd/MM/yyyy HH:mm"),
            WarehouseName = Db.Warehouses.Where(w => w.Id == t.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
            ItemName = t.ProductId != null
                ? Db.Products.Where(p => p.Id == t.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                : Db.AuxiliaryMaterials.Where(m => m.Id == t.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
            LotCode = Db.Lots.Where(l => l.Id == t.LotId).Select(l => l.LotCode).FirstOrDefault(),
            MovementTypeAr = t.MovementType == Core.Domain.Enums.MovementType.Inbound ? "وارد"
                : t.MovementType == Core.Domain.Enums.MovementType.Outbound ? "صادر" : "تحويل",
            QtyKg = t.QtyKg,
            ReferenceDoc = t.ReferenceDocNumber,
            CreatedByUser = Db.Users.Where(u => u.Id == t.CreatedBy).Select(u => u.FullName).FirstOrDefault(),
            MachineName = t.MachineName
        }).ToList();
    }
}

/// <summary>§27 — تسجيل أجهزة العملاء عند كل دخول.</summary>
public class MachineRegistry
{
    private readonly DatesErpDbContext _db;
    public MachineRegistry(DatesErpDbContext db) => _db = db;

    public void Heartbeat(string appVersion)
    {
        try
        {
            var machineId = $"{Environment.MachineName}-{Environment.UserName}";
            var m = _db.ClientMachines.FirstOrDefault(x => x.MachineId == machineId);
            if (m == null)
            {
                m = new ClientMachine { MachineId = machineId, MachineName = Environment.MachineName, WindowsUser = Environment.UserName };
                _db.ClientMachines.Add(m);
            }
            m.ApplicationVersion = appVersion;
            m.LastSeen = DateTime.Now;
            m.LastLogin ??= DateTime.Now;
            m.LastLogin = DateTime.Now;
            m.IsActive = true;
            _db.SaveChanges();
        }
        catch { /* تسجيل الأجهزة لا يعطل الدخول */ }
    }
}
