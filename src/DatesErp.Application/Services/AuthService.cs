using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Security;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§11 — تسجيل الدخول وتحميل الأدوار والصلاحيات في الجلسة.</summary>
public class AuthService : IAuthService
{
    private readonly DatesErpDbContext _db;
    private readonly SessionContext _session;
    private readonly IAuditService _audit;

    public AuthService(DatesErpDbContext db, SessionContext session, IAuditService audit)
    {
        _db = db;
        _session = session;
        _audit = audit;
    }

    public LoginResult Login(string userName, string password)
    {
        // §الدخول بالرقم: يقبل رقم الدخول (UserCode) أو رقم الموظف أو اسم المستخدم
        var loginKey = (userName ?? "").Trim();
        var user = _db.Users.Include(u => u.UserRoles).FirstOrDefault(u => u.UserName == loginKey)
            ?? _db.Users.Include(u => u.UserRoles).FirstOrDefault(u => u.UserCode == loginKey)
            ?? _db.Users.Include(u => u.UserRoles).FirstOrDefault(u =>
                u.EmployeeId != null && u.EmployeeId == _db.Employees.Where(e => e.EmployeeCode == loginKey).Select(e => (int?)e.Id).FirstOrDefault());
        if (user == null || !user.IsActive)
            return new LoginResult { Success = false, Message = "رقم الدخول أو اسم المستخدم غير موجود أو غير نشط." };
        // §إصلاح: فك القفل التلقائي بعد انقضاء المدة — كان القفل دائماً لا يفكّه إلا المدير
        // أو ملف استعادة الطوارئ، فأي قفل عارض كان يوقف العمل حتى تدخل إداري.
        if (user.IsLocked && user.LockoutDate != null)
        {
            int minutes = 30;
            var st = _db.SystemSettings.AsNoTracking().FirstOrDefault(x => x.SettingKey == "LockoutMinutes");
            if (st != null && int.TryParse(st.SettingValue, out var m) && m > 0) minutes = m;
            if ((DateTime.Now - user.LockoutDate.Value).TotalMinutes >= minutes)
            {
                user.IsLocked = false;
                user.FailedLoginCount = 0;
                user.LockoutDate = null;
                _db.SaveChanges();
            }
        }
        if (user.IsLocked)
        {
            string when = user.LockoutDate != null
                ? user.LockoutDate.Value.AddMinutes(30).ToString("HH:mm")
                : "—";
            return new LoginResult { Success = false, Message = $"الحساب مقفل — يُفك تلقائياً الساعة {when} أو راجع مسؤول النظام." };
        }
        if (!PasswordHasher.Verify(password ?? "", user.PasswordHash, user.PasswordSalt))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= 5) { user.IsLocked = true; user.LockoutDate = DateTime.Now; }
            _db.SaveChanges();
            return new LoginResult { Success = false, Message = "كلمة المرور غير صحيحة." };
        }

        user.FailedLoginCount = 0;
        var previousLogin = user.LastLoginDate;
        user.LastLoginDate = DateTime.Now;

        // §لمسة مؤسسية (قرار #47): انتهاء صلاحية كلمة المرور بعد مدة قابلة للضبط (افتراضي 90 يوماً).
        int maxAge = 90;
        var ageSetting = _db.SystemSettings.AsNoTracking().FirstOrDefault(x => x.SettingKey == "PasswordMaxAgeDays");
        if (ageSetting != null && int.TryParse(ageSetting.SettingValue, out var parsedAge) && parsedAge > 0)
            maxAge = parsedAge;
        int ageDays = user.PasswordChangedDate != null
            ? (int)(DateTime.Now - user.PasswordChangedDate.Value).TotalDays
            : int.MaxValue;   // لم تُغيَّر قط (حساب مبذوق) → تُعتبر منتهية
        bool expired = ageDays >= maxAge;
        if (expired) user.MustChangePassword = true;

        // تعبئة الجلسة: الأدوار + مصفوفة الصلاحيات المركزية
        var roleIds = user.UserRoles.Where(r => r.IsActive).Select(r => r.RoleId).ToList();
        _session.UserId = user.Id;
        _session.UserName = user.UserName;
        _session.Roles = _db.Roles.Where(r => roleIds.Contains(r.Id)).Select(r => r.RoleCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // §النموذج الهرمي: الصلاحية الفعلية = أدوار المستخدم + استثناءاته الصريحة (بلا مفتاح is_admin)
        var permSvc = new PermissionService(_db, _session);
        _session.PermissionCache.Clear();
        foreach (var kv in permSvc.BuildEffectiveCache(user.Id, roleIds))
            _session.PermissionCache[kv.Key] = kv.Value;
        // §تفويض زمني: دمج صلاحيات المفوِّض النشط ضمن الفترة (كلياً أو لوحدة محددة)
        var today = DateTime.Now.Date;
        var delegs = _db.Delegations.AsNoTracking().Where(d => d.IsActive && d.ToUserId == user.Id
            && d.StartDate <= today && d.EndDate >= today).ToList();
        foreach (var dg in delegs)
        {
            var fromRoles = _db.UserRoles.AsNoTracking().Where(ur => ur.UserId == dg.FromUserId && ur.IsActive).Select(ur => ur.RoleId).ToList();
            foreach (var kv in permSvc.BuildEffectiveCache(dg.FromUserId, fromRoles))
            {
                if (dg.ScopeModule != null && kv.Key.module != dg.ScopeModule) continue;
                _session.PermissionCache[kv.Key] = (_session.PermissionCache.TryGetValue(kv.Key, out var v) && v) || kv.Value;
            }
        }

        _db.SaveChanges();
        _audit.Log("Login", "Login", "Users", user.UserName, user.Id);
        return new LoginResult
        {
            Success = true,
            UserId = user.Id,
            FullName = user.FullName,
            Roles = _session.Roles.ToList(),
            MustChangePassword = user.MustChangePassword,
            PasswordExpired = expired,
            PasswordAgeDays = ageDays == int.MaxValue ? -1 : ageDays,
            LastLoginDate = previousLogin,
            Message = "تم تسجيل الدخول."
        };
    }

    public void Logout()
    {
        if (_session.UserId > 0)
            _audit.Log("Logout", "Logout", "Users", _session.UserName, _session.UserId);
        _session.UserId = 0;
        _session.UserName = null;
        _session.Roles.Clear();
        _session.PermissionCache.Clear();
    }
}
