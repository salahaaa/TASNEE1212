using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Core.Exceptions;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §النظام الهرمي للصلاحيات: مورد (شاشة فعلية) × عملية (فعل) × حالة (تُفحص في طبقة الخدمات)،
/// مع استثناءات مستخدم فوق صلاحيات الأدوار، وسجل تدقيق إلحاقي، ونسخ/مقارنة، ومنع تعطيل آخر مدير صلاحيات.
/// البيانات الفعلية للشاشات هي الأساس — لا أسماء وهمية.
/// </summary>
public class PermissionService
{
    private readonly DatesErpDbContext _db;
    private readonly ICurrentSession _session;

    public PermissionService(DatesErpDbContext db, ICurrentSession session)
    { _db = db; _session = session; }

    // ═══ الكتالوج الفعلي: الموارد = أكواد الوحدات المستخدمة في Require عبر النظام ═══
    // §الإصلاح الأمني: صار مشتقاً من PermissionModules.All (المصدر الواحد في Core) بدل
    // نسخة يدوية ثالثة. كان «products/cartons/employees» مُعرَّفة هنا أو مُطبَّقة في الخادم
    // بينما تسقط من البذور وبوابة الشاشات — فتُفتح الشاشة بلا فحص. الآن مستحيل بنيوياً.
    public static readonly (string Code, string NameAr, string GroupAr)[] ResourceCatalog =
        PermissionModules.All;

    public static readonly (string Code, string NameAr, bool Sensitive)[] OperationCatalog =
    {
        ("View", "عرض", false),
        ("Create", "إضافة", false),
        ("Edit", "تعديل", false),
        ("Delete", "حذف", true),
        ("Approve", "اعتماد", true),
        ("Post", "ترحيل", false),
        ("Print", "طباعة", false),
        ("Export", "تصدير", false),
        ("Cancel", "إلغاء/عكس", true),
        ("Reopen", "إعادة فتح مستند معتمد", true),
        ("EditAfterApproval", "تعديل بعد الاعتماد", true),
        ("BypassInspection", "تجاوز الفحص", true),
        ("ManagePermissions", "إدارة الصلاحيات", true)
    };

    // ═══ التهيئة: بذر الكتالوج + ترحيل صلاحيات الأدوار القديمة إلى النموذج الجديد ═══
    public void EnsureCatalog()
    {
        // §B84/S1: إكمال الكتالوج أولاً — القواعد القائمة لا ترى الموارد/العمليات المضافة لاحقاً
        // (البذر الأصلي يعمل فقط عندما تكون الجداول فارغة).
        EnsureCatalogUpsert();

        if (!_db.PermissionResources.Any())
        {
            int s = 1;
            foreach (var (code, name, group) in ResourceCatalog)
                _db.PermissionResources.Add(new PermissionResource { Code = code, NameAr = name, GroupAr = group, SortNo = s++ });
            s = 1;
            foreach (var (code, name, sens) in OperationCatalog)
                _db.PermissionOperations.Add(new PermissionOperation { Code = code, NameAr = name, IsSensitive = sens, SortNo = s++ });
            _db.SaveChanges();
        }

        if (!_db.RoleResourcePermissions.Any())
        {
            var resByCode = _db.PermissionResources.ToDictionary(r => r.Code, r => r.Id);
            var opByCode = _db.PermissionOperations.ToDictionary(o => o.Code, o => o.Id);
            foreach (var rp in _db.RolePermissions.ToList())
                foreach (var opc in OperationCatalog)
                {
                    var op = opc.Code;
                    bool allowed = rp.Can(op);
                    if (rp.ModuleCode != null && resByCode.TryGetValue(rp.ModuleCode, out var rid) && opByCode.TryGetValue(op, out var oid))
                        _db.RoleResourcePermissions.Add(new RoleResourcePermission { RoleId = rp.RoleId, ResourceId = rid, OperationId = oid, IsAllowed = allowed });
                }
            // §لا مفتاح is_admin: دور المدير العام يُسجل صلاحياته كاملة في الجداول كأي دور
            var adminRole = _db.Roles.FirstOrDefault(r => r.RoleCode == "Administrator");
            if (adminRole != null)
                foreach (var rid in resByCode.Values)
                    foreach (var oid in opByCode.Values)
                        if (!_db.RoleResourcePermissions.Any(x => x.RoleId == adminRole.Id && x.ResourceId == rid && x.OperationId == oid))
                            _db.RoleResourcePermissions.Add(new RoleResourcePermission { RoleId = adminRole.Id, ResourceId = rid, OperationId = oid, IsAllowed = true });
            _db.SaveChanges();
        }

        // §ظهرت موارد جديدة لاحقاً؟ يُستكمل المدير العام بها حتى لا يُقفل النظام
        var admin = _db.Roles.FirstOrDefault(r => r.RoleCode == "Administrator");
        if (admin != null)
        {
            bool added = false;
            foreach (var rid in _db.PermissionResources.Select(r => r.Id).ToList())
                foreach (var oid in _db.PermissionOperations.Select(o => o.Id).ToList())
                    if (!_db.RoleResourcePermissions.Any(x => x.RoleId == admin.Id && x.ResourceId == rid && x.OperationId == oid))
                    { _db.RoleResourcePermissions.Add(new RoleResourcePermission { RoleId = admin.Id, ResourceId = rid, OperationId = oid, IsAllowed = true }); added = true; }
            if (added) _db.SaveChanges();
        }

        // §B84/S1: منح إعادة الفتح لمن يملك الاعتماد (تفعيل صلاحية Reopen الميتة سابقاً).
        GrantReopenToApprovers();
        // §B95: منح «تعديل بعد الاعتماد» لمعتمدي الجودة — التصحيح المعتمد على المحاضر المعتمدة.
        GrantQualityCorrectionToApprovers();
        // §الإصلاح الأمني: ترحيل القواعد القائمة إلى الوحدات التي دخلت البوابة حديثاً.
        BackfillNewlyGatedModules();
    }

    /// <summary>
    /// §الإصلاح الأمني — ترحيل آمن للقواعد القائمة.
    ///
    /// «products» و«cartons» و«employees» كانت خارج بذور الأدوار وخارج بوابة فتح الشاشات،
    /// فتُفتح شاشاتها بلا فحص. بعد إدخالها البوابة، القاعدة القائمة (المُرقّاة لا الجديدة)
    /// لا تحوي لها أي صف صلاحية لأي دور ⟵ سيُحجب عن كل المستخدمين ما كانوا يعملون عليه.
    ///
    /// هذا الترحيل يمنح «عرض» فقط لكل دور نشط على هذه الوحدات إن لم يكن له صف مسجّل أصلاً —
    /// فلا ينكسر عمل قائم، ولا تُمنح صلاحية تعديل أو حذف لأحد. ما زاد عن العرض يُمنح يدوياً
    /// من شاشة الصلاحيات. idempotent: لا يكتب شيئاً بعد أول تشغيل، ولا يلمس أي صف موجود.
    /// </summary>
    public void BackfillNewlyGatedModules()
    {
        string[] newlyGated = { PermissionModules.Products, PermissionModules.Cartons, PermissionModules.Employees };
        var viewOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "View");
        if (viewOp == null) return;

        var resources = _db.PermissionResources.Where(r => newlyGated.Contains(r.Code)).ToList();
        if (resources.Count == 0) return;

        var roleIds = _db.Roles.Where(r => r.IsActive).Select(r => r.Id).ToList();
        bool added = false;
        foreach (var roleId in roleIds)
            foreach (var res in resources)
            {
                // أي صف مسجّل لهذا الدور على هذا المورد = الإدارة ضبطته سابقاً ⟵ لا نلمسه
                bool hasAny = _db.RoleResourcePermissions.Any(x => x.RoleId == roleId && x.ResourceId == res.Id);
                if (hasAny) continue;
                _db.RoleResourcePermissions.Add(new RoleResourcePermission
                { RoleId = roleId, ResourceId = res.Id, OperationId = viewOp.Id, IsAllowed = true });
                added = true;
            }
        if (added) _db.SaveChanges();
    }

    /// <summary>
    /// §B84/S1: إكمال ناقص الكتالوج بالقوة (idempotent): يضيف أي مورد/عملية من الكتالوج
    /// البرمجي غير موجودة في القاعدة — فيرى التحديثُ القواعدَ القائمة لا الجديدة فقط.
    /// </summary>
    public void EnsureCatalogUpsert()
    {
        var resCodes = _db.PermissionResources.Select(r => r.Code).ToList();
        int s = _db.PermissionResources.Any() ? _db.PermissionResources.Max(r => r.SortNo) + 1 : 1;
        foreach (var (code, name, group) in ResourceCatalog)
            if (!resCodes.Contains(code))
                _db.PermissionResources.Add(new PermissionResource { Code = code, NameAr = name, GroupAr = group, SortNo = s++ });
        var opCodes = _db.PermissionOperations.Select(o => o.Code).ToList();
        s = _db.PermissionOperations.Any() ? _db.PermissionOperations.Max(o => o.SortNo) + 1 : 1;
        foreach (var (code, name, sens) in OperationCatalog)
            if (!opCodes.Contains(code))
                _db.PermissionOperations.Add(new PermissionOperation { Code = code, NameAr = name, IsSensitive = sens, SortNo = s++ });
        _db.SaveChanges();
    }

    /// <summary>
    /// §B84/S1: كل دور يملك (planning + Approve) يُمنح (planning + Reopen) تلقائياً —
    /// عملياً: مدير النظام والإدارة والإنتاج. تُستدعى عند كل إقلاع (EnsureCatalog) فلا إقفال
    /// للأدوار القائمة بعد تفعيل فحص Reopen في PlanClosureService.ReopenPlan.
    /// </summary>
    public void GrantReopenToApprovers()
    {
        var planRes = _db.PermissionResources.FirstOrDefault(r => r.Code == "planning");
        var approveOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "Approve");
        var reopenOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "Reopen");
        if (planRes == null || approveOp == null || reopenOp == null) return;
        var approverRoles = _db.RoleResourcePermissions
            .Where(x => x.ResourceId == planRes.Id && x.OperationId == approveOp.Id && x.IsAllowed)
            .Select(x => x.RoleId).Distinct().ToList();
        bool added = false;
        foreach (var roleId in approverRoles)
            if (!_db.RoleResourcePermissions.Any(x => x.RoleId == roleId && x.ResourceId == planRes.Id && x.OperationId == reopenOp.Id))
            {
                _db.RoleResourcePermissions.Add(new RoleResourcePermission
                    { RoleId = roleId, ResourceId = planRes.Id, OperationId = reopenOp.Id, IsAllowed = true });
                added = true;
            }
        if (added) _db.SaveChanges();
    }

    /// <summary>
    /// §B95: منح «الجودة/تعديل بعد الاعتماد» لكل دور يملك «الجودة/اعتماد» —
    /// التصحيح المعتمد على محضر معتمد حقٌّ لمن اعتمده أصلاً، بسبب مكتوب يُسجَّل في التدقيق.
    /// تُستدعى عند كل إقلاع (EnsureCatalog) فلا إقفال للأدوار القائمة بعد تفعيل البوابة في RequestCorrection.
    /// </summary>
    public void GrantQualityCorrectionToApprovers()
    {
        var qRes = _db.PermissionResources.FirstOrDefault(r => r.Code == "quality");
        var approveOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "Approve");
        var corrOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "EditAfterApproval");
        if (qRes == null || approveOp == null || corrOp == null) return;
        var approverRoles = _db.RoleResourcePermissions
            .Where(x => x.ResourceId == qRes.Id && x.OperationId == approveOp.Id && x.IsAllowed)
            .Select(x => x.RoleId).Distinct().ToList();
        bool added = false;
        foreach (var roleId in approverRoles)
            if (!_db.RoleResourcePermissions.Any(x => x.RoleId == roleId && x.ResourceId == qRes.Id && x.OperationId == corrOp.Id))
            {
                _db.RoleResourcePermissions.Add(new RoleResourcePermission
                    { RoleId = roleId, ResourceId = qRes.Id, OperationId = corrOp.Id, IsAllowed = true });
                added = true;
            }
        if (added) _db.SaveChanges();
    }

    // ═══ الصلاحية الفعلية: أدوار المستخدم ثم استثناءاته الصريحة فوقها ═══
    public Dictionary<(string module, string action), bool> BuildEffectiveCache(int userId, List<int> roleIds)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        var cache = new Dictionary<(string, string), bool>();

        var roleRows = _db.RoleResourcePermissions.Where(x => roleIds.Contains(x.RoleId)).ToList();
        foreach (var x in roleRows)
        {
            var key = (resById[x.ResourceId], opById[x.OperationId]);
            cache[key] = cache.TryGetValue(key, out var v) && v || x.IsAllowed;
        }
        var userRows = _db.UserResourcePermissions.Where(x => x.UserId == userId).ToList();
        foreach (var x in userRows)
            cache[(resById[x.ResourceId], opById[x.OperationId])] = x.IsAllowed; // الاستثناء يعلو
        return cache;
    }

    public HashSet<(string res, string op)> GetRoleSet(int roleId)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        return _db.RoleResourcePermissions.Where(x => x.RoleId == roleId && x.IsAllowed).ToList()
            .Select(x => (resById[x.ResourceId], opById[x.OperationId])).ToHashSet();
    }

    /// <summary>
    /// §7 — الموروث من الأدوار فقط (بلا استثناءات المستخدم): ما يملكه المستخدم بحكم أدواره.
    /// تستخدمه شاشة الصلاحيات لعرض عمود «من الدور» منفصلاً عن عمود «استثناء».
    /// </summary>
    public HashSet<(string res, string op)> GetInheritedSet(List<int> roleIds)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        return _db.RoleResourcePermissions.Where(x => roleIds.Contains(x.RoleId) && x.IsAllowed).ToList()
            .Select(x => (resById[x.ResourceId], opById[x.OperationId])).ToHashSet();
    }

    /// <summary>
    /// §7 — استثناءات المستخدم الصريحة بقيمتها: true = منح فوق الدور، false = منع رغم الدور.
    /// غياب المفتاح = لا استثناء (وراثة صافية) — وهي الحالة الثالثة التي كان المربع المسطّح يخفيها.
    /// </summary>
    public Dictionary<(string res, string op), bool> GetUserExceptions(int userId)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        return _db.UserResourcePermissions.Where(x => x.UserId == userId).ToList()
            .ToDictionary(x => (resById[x.ResourceId], opById[x.OperationId]), x => x.IsAllowed);
    }

    /// <summary>
    /// §7 — إزالة الاستثناء نهائياً فيعود المستخدم إلى وراثة دوره.
    /// (كانت مستحيلة سابقاً: المربع المسطّح لا يفرّق بين «لا استثناء» و«منع صريح».)
    /// </summary>
    public void ClearUserPermission(int userId, string resCode, string opCode)
    {
        var res = _db.PermissionResources.FirstOrDefault(r => r.Code == resCode) ?? throw new DomainException("مورد غير معروف.");
        var op = _db.PermissionOperations.FirstOrDefault(o => o.Code == opCode) ?? throw new DomainException("عملية غير معروفة.");
        var row = _db.UserResourcePermissions.FirstOrDefault(x => x.UserId == userId && x.ResourceId == res.Id && x.OperationId == op.Id);
        if (row == null) return;
        string oldV = row.IsAllowed ? "استثناء: مسموح" : "استثناء: ممنوع";
        _db.UserResourcePermissions.Remove(row);
        Log(null, userId, resCode, opCode, oldV, "وراثة من الدور", "inherit");
        _db.SaveChanges();
    }

    public HashSet<(string res, string op)> GetUserSet(int userId)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        return _db.UserResourcePermissions.Where(x => x.UserId == userId).ToList()
            .Select(x => (resById[x.ResourceId], opById[x.OperationId])).ToHashSet();
    }

    // ═══ التعديل مع التدقيق ═══
    public void SetRolePermission(int roleId, string resCode, string opCode, bool allowed)
    {
        var res = _db.PermissionResources.FirstOrDefault(r => r.Code == resCode) ?? throw new DomainException("مورد غير معروف.");
        var op = _db.PermissionOperations.FirstOrDefault(o => o.Code == opCode) ?? throw new DomainException("عملية غير معروفة.");
        var row = _db.RoleResourcePermissions.FirstOrDefault(x => x.RoleId == roleId && x.ResourceId == res.Id && x.OperationId == op.Id);
        string oldV = row == null ? "غير مسجلة" : row.IsAllowed ? "مسموح" : "مرفوض";
        if (row == null) { row = new RoleResourcePermission { RoleId = roleId, ResourceId = res.Id, OperationId = op.Id }; _db.RoleResourcePermissions.Add(row); }
        row.IsAllowed = allowed;
        Log(roleId, null, resCode, opCode, oldV, allowed ? "مسموح" : "مرفوض", allowed ? "grant" : "revoke");
        _db.SaveChanges();
    }

    public void SetUserPermission(int userId, string resCode, string opCode, bool allowed)
    {
        var res = _db.PermissionResources.FirstOrDefault(r => r.Code == resCode) ?? throw new DomainException("مورد غير معروف.");
        var op = _db.PermissionOperations.FirstOrDefault(o => o.Code == opCode) ?? throw new DomainException("عملية غير معروفة.");
        var row = _db.UserResourcePermissions.FirstOrDefault(x => x.UserId == userId && x.ResourceId == res.Id && x.OperationId == op.Id);
        string oldV = row == null ? "بدون استثناء" : row.IsAllowed ? "مسموح" : "مرفوض";
        if (row == null) { row = new UserResourcePermission { UserId = userId, ResourceId = res.Id, OperationId = op.Id }; _db.UserResourcePermissions.Add(row); }
        row.IsAllowed = allowed;
        Log(null, userId, resCode, opCode, oldV, allowed ? "مسموح" : "مرفوض", allowed ? "grant" : "revoke");
        _db.SaveChanges();
    }

    public void CopyRolePermissions(int srcRoleId, int dstRoleId)
    {
        if (srcRoleId == dstRoleId) throw new DomainException("لا يمكن نسخ الدور إلى نفسه.");
        var src = GetRoleSet(srcRoleId);
        var resByCode = _db.PermissionResources.ToDictionary(r => r.Code, r => r.Id);
        var opByCode = _db.PermissionOperations.ToDictionary(o => o.Code, o => o.Id);
        foreach (var kv in _db.RoleResourcePermissions.Where(x => x.RoleId == dstRoleId).ToList()) _db.RoleResourcePermissions.Remove(kv);
        foreach (var (res, op) in src)
            _db.RoleResourcePermissions.Add(new RoleResourcePermission { RoleId = dstRoleId, ResourceId = resByCode[res], OperationId = opByCode[op], IsAllowed = true });
        Log(dstRoleId, null, "*", "*", "صلاحيات سابقة", $"نسخ من دور #{srcRoleId} ({src.Count} صلاحية)", "copy");
        _db.SaveChanges();
    }

    public void CopyUserPermissions(int srcUserId, int dstUserId)
    {
        if (srcUserId == dstUserId) throw new DomainException("لا يمكن نسخ المستخدم إلى نفسه.");
        var src = _db.UserResourcePermissions.Where(x => x.UserId == srcUserId).ToList();
        foreach (var kv in _db.UserResourcePermissions.Where(x => x.UserId == dstUserId).ToList()) _db.UserResourcePermissions.Remove(kv);
        foreach (var s in src)
            _db.UserResourcePermissions.Add(new UserResourcePermission { UserId = dstUserId, ResourceId = s.ResourceId, OperationId = s.OperationId, IsAllowed = s.IsAllowed });
        Log(null, dstUserId, "*", "*", "استثناءات سابقة", $"نسخ من مستخدم #{srcUserId} ({src.Count})", "copy");
        _db.SaveChanges();
    }

    // ═══ منع الإغلاق الكامل: لا تعطيل لآخر من يملك إدارة الصلاحيات ═══
    public void GuardLastPermissionAdmin(int? deactivateUserId, int? deactivateRoleId)
    {
        bool UserCanManage(int uid)
        {
            var roleIds = _db.UserRoles.Where(ur => ur.UserId == uid && ur.IsActive).Select(ur => ur.RoleId).ToList();
            var cache = BuildEffectiveCache(uid, roleIds);
            return cache.TryGetValue(("permissions", "ManagePermissions"), out var v) && v;
        }
        var activeUsers = _db.Users.Where(u => u.IsActive && !u.IsLocked).ToList();
        var remaining = activeUsers.Where(u =>
            (deactivateUserId == null || u.Id != deactivateUserId) &&
            !(deactivateRoleId != null && u.UserRoles.Any(ur => ur.RoleId == deactivateRoleId && ur.IsActive)) &&
            UserCanManage(u.Id)).ToList();
        if (!remaining.Any())
            throw new DomainException("مرفوض: لا يمكن تعطيل آخر مستخدم يملك صلاحية إدارة الصلاحيات — سيُغلق النظام كلياً.");
    }

    public void DeactivateUser(int userId)
    {
        GuardLastPermissionAdmin(userId, null);
        var u = _db.Users.FirstOrDefault(x => x.Id == userId) ?? throw new DomainException("المستخدم غير موجود.");
        u.IsActive = false;
        Log(null, userId, "*", "*", "نشط", "معطل", "deactivate");
        _db.SaveChanges();
    }

    public void DeactivateRole(int roleId)
    {
        GuardLastPermissionAdmin(null, roleId);
        var r = _db.Roles.FirstOrDefault(x => x.Id == roleId) ?? throw new DomainException("الدور غير موجود.");
        r.IsActive = false;
        Log(roleId, null, "*", "*", "نشط", "معطل", "deactivate");
        _db.SaveChanges();
    }

    public List<PermissionAuditLog> GetAudit()
        => _db.PermissionAuditLogs.OrderByDescending(a => a.Id).Take(500).ToList();

    private void Log(int? roleId, int? userId, string res, string op, string oldV, string newV, string action)
    {
        _db.PermissionAuditLogs.Add(new PermissionAuditLog
        {
            ChangedById = _session.UserId > 0 ? _session.UserId : null,
            ChangedByName = _session.UserName ?? "system",
            TargetRoleId = roleId,
            TargetUserId = userId,
            ResourceCode = res,
            OperationCode = op,
            OldValue = oldV,
            NewValue = newV,
            ActionType = action
        });
    }
}
