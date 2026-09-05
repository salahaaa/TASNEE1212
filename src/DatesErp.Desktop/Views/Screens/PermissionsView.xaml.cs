using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §مركز التحكم بالصلاحيات — النموذج الهرمي (مورد×عملية×حالة):
/// يمين: الأدوار + المستخدمون مع بحث | وسط: شجرة الموارد الفعلية + جدول العمليات مع تحذير الحساسة،
/// حفظ بملخص وتأكيد إضافي للحساسة، نسخ، مقارنة، سجل تدقيق، وتعطيل آمن (منع إغلاق النظام).
/// </summary>
public partial class PermissionsView : UserControl
{
    private sealed class OpRow : INotifyPropertyChanged
    {
        public string ResCode { get; set; }
        public string Code { get; set; }
        public string NameAr { get; set; }
        public bool IsSensitive { get; set; }
        public string SensitiveMark => IsSensitive ? "⚠️ حساسة" : "";
        private bool _allowed;
        public bool Allowed { get => _allowed; set { if (_allowed != value) { _allowed = value; Changed?.Invoke(this, value); } OnChanged(nameof(Allowed)); } }
        public event Action<OpRow, bool> Changed;
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private List<PermissionResource> _res = new();
    private List<PermissionOperation> _ops = new();
    private readonly HashSet<(string res, string op)> _pending = new();
    private HashSet<(string res, string op)> _baseline = new();
    private readonly Dictionary<string, CheckBox> _treeChecks = new();
    private string _mode = "role";
    private int _targetId;
    private string _currentRes = "";
    private readonly ObservableCollection<OpRow> _opRows = new();

    public PermissionsView()
    {
        InitializeComponent();
        OpsGrid.ItemsSource = _opRows;
        Loaded += (_, _) => LoadAll();
    }

    private PermissionService Svc()
    {
        var scope = AppContainer.NewScope();
        return scope.ServiceProvider.GetRequiredService<PermissionService>();
    }

    private void LoadAll()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = new PermissionService(db, AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>());
            svc.EnsureCatalog();
            _res = db.PermissionResources.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.SortNo).ToList();
            _ops = db.PermissionOperations.AsNoTracking().OrderBy(o => o.SortNo).ToList();
            RolesList.ItemsSource = db.Roles.AsNoTracking().OrderBy(r => r.Id).Select(r => new { r.Id, Label = $"{r.RoleNameAr} {(r.IsActive ? "" : "(معطل)")} " }).ToList();
            FillUsers("");
            BuildTree("");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Perm.Load"); }
    }

    private void FillUsers(string term)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var q = db.Users.AsNoTracking().AsQueryable();
        var roleNames = db.Roles.AsNoTracking().ToDictionary(r => r.Id, r => r.RoleNameAr);
        var list = q.OrderBy(u => u.Id).ToList().Select(u => new
        {
            u.Id,
            u.UserName,
            u.FullName,
            u.UserCode,
            Roles = string.Join("+", u.UserRoles.Where(ur => ur.IsActive).Select(ur => roleNames.TryGetValue(ur.RoleId, out var rn) ? rn : "?")),
            u.IsActive
        }).ToList();
        if (!string.IsNullOrWhiteSpace(term))
            list = list.Where(u => (u.FullName ?? "").Contains(term) || (u.UserName ?? "").Contains(term) || (u.UserCode ?? "").Contains(term) || (u.Roles ?? "").Contains(term)).ToList();
        UsersList.ItemsSource = list.Select(u => new { u.Id, Label = $"{u.FullName} ({u.UserName}) — {u.Roles} {(u.IsActive ? "" : "(معطل)")} " }).ToList();
    }

    private void UserSearch_Changed(object sender, TextChangedEventArgs e) => FillUsers(UserSearchBox.Text?.Trim() ?? "");

    // ═══ الشجرة ═══
    private void BuildTree(string filter)
    {
        _treeChecks.Clear();
        ResTree.Items.Clear();
        var term = filter?.Trim().ToLower() ?? "";
        foreach (var group in _res.Select(r => r.GroupAr).Distinct())
        {
            var nodes = _res.Where(r => r.GroupAr == group)
                .Where(r => term == "" || r.NameAr.ToLower().Contains(term) || r.Code.Contains(term)
                            || _ops.Any(o => o.NameAr.ToLower().Contains(term) && _pendingOrBase(r.Code, o.Code)))
                .ToList();
            if (nodes.Count == 0) continue;
            var gItem = new TreeViewItem { Header = $"📁 {group}", IsExpanded = true, FontWeight = FontWeights.Bold };
            foreach (var r in nodes)
            {
                var cb = new CheckBox { Tag = r.Code, Margin = new Thickness(18, 2, 0, 2) };
                cb.Content = new TextBlock { Text = r.NameAr, FontSize = 12 };
                cb.Checked += (_, _) => SetResourceAll(r.Code, true);
                cb.Unchecked += (_, _) => SetResourceAll(r.Code, false);
                cb.PreviewMouseLeftButtonUp += (_, _) => { SelectResource(r.Code); };
                _treeChecks[r.Code] = cb;
                gItem.Items.Add(cb);
            }
            ResTree.Items.Add(gItem);
        }
        RefreshTreeChecks();
    }

    private bool _pendingOrBase(string res, string op) => _pending.Contains((res, op));

    private void RefreshTreeChecks()
    {
        foreach (var r in _res)
        {
            if (!_treeChecks.TryGetValue(r.Code, out var cb)) continue;
            int total = _ops.Count;
            int on = _ops.Count(o => _pending.Contains((r.Code, o.Code)));
            cb.IsChecked = on == 0 ? false : on == total ? true : null;
        }
    }

    private void SetResourceAll(string resCode, bool allowed)
    {
        foreach (var o in _ops)
        {
            if (allowed) _pending.Add((resCode, o.Code)); else _pending.Remove((resCode, o.Code));
        }
        if (_currentRes == resCode) SyncOpsGrid();
    }

    private void SelectResource(string resCode)
    {
        _currentRes = resCode;
        var r = _res.FirstOrDefault(x => x.Code == resCode);
        OpsTitle.Text = $"عمليات المورد: {r?.NameAr ?? resCode}";
        SyncOpsGrid();
    }

    private void SyncOpsGrid()
    {
        _opRows.Clear();
        foreach (var o in _ops)
        {
            var row = new OpRow
            {
                ResCode = _currentRes,
                Code = o.Code,
                NameAr = o.NameAr,
                IsSensitive = o.IsSensitive,
                Allowed = _pending.Contains((_currentRes, o.Code))
            };
            row.Changed += (r, v) =>
            {
                if (v && r.IsSensitive && !AppContainer.Get<DialogService>().Confirm($"⚠ العملية «{r.NameAr}» حساسة — منحها يمكّن تجاوز حالات الاعتماد. تأكيد المنح؟"))
                { r.Allowed = false; return; }
                if (v) _pending.Add((r.ResCode, r.Code)); else _pending.Remove((r.ResCode, r.Code));
                RefreshTreeChecks();
            };
            _opRows.Add(row);
        }
        RefreshTreeChecks();
    }

    private void TreeSearch_Changed(object sender, TextChangedEventArgs e) => BuildTree(TreeSearchBox.Text);

    // ═══ اختيار الهدف ═══
    private void Role_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (RolesList.SelectedItem?.GetType().GetProperty("Id")?.GetValue(RolesList.SelectedItem) is not int id) return;
        _mode = "role"; _targetId = id;
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var role = db.Roles.AsNoTracking().FirstOrDefault(r => r.Id == id);
        _baseline = new PermissionService(db, AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>()).GetRoleSet(id);
        _pending.Clear(); foreach (var k in _baseline) _pending.Add(k);
        TargetLabel.Text = $"✏️ تحرير صلاحيات الدور: {role?.RoleNameAr}";
        SyncOpsGrid();
    }

    private void User_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (UsersList.SelectedItem?.GetType().GetProperty("Id")?.GetValue(UsersList.SelectedItem) is not int id) return;
        _mode = "user"; _targetId = id;
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var u = db.Users.AsNoTracking().FirstOrDefault(x => x.Id == id);
        var roleIds = db.UserRoles.AsNoTracking().Where(ur => ur.UserId == id && ur.IsActive).Select(ur => ur.RoleId).ToList();
        var svc = new PermissionService(db, AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>());
        _baseline = svc.BuildEffectiveCache(id, roleIds).Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet();
        _pending.Clear(); foreach (var k in _baseline) _pending.Add(k);
        TargetLabel.Text = $"✏️ تحرير استثناءات المستخدم: {u?.FullName} (فوق صلاحيات أدواره)";
        SyncOpsGrid();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _res) foreach (var o in _ops) _pending.Add((r.Code, o.Code));
        SyncOpsGrid();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _pending.Clear();
        SyncOpsGrid();
    }

    // ═══ الحفظ بملخص ═══
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_targetId == 0) { AppContainer.Get<DialogService>().Error("اختر دوراً أو مستخدماً أولاً."); return; }
        var added = _pending.Except(_baseline).ToList();
        var removed = _baseline.Except(_pending).ToList();
        if (added.Count == 0 && removed.Count == 0) { AppContainer.Get<DialogService>().Info("لا تغييرات للحفظ."); return; }
        var sensitiveAdds = added.Where(a => _ops.Any(o => o.Code == a.op && o.IsSensitive)).Select(a => $"{a.res}:{a.op}").ToList();
        string sum = $"سيُحفَظ {added.Count} منح و{removed.Count} سحب.";
        if (sensitiveAdds.Count > 0)
            sum += $"\n⚠ منها {sensitiveAdds.Count} صلاحية حساسة: {string.Join("، ", sensitiveAdds.Take(6))}";
        if (!AppContainer.Get<DialogService>().Confirm(sum + "\n\nتأكيد الحفظ؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = new PermissionService(db, AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>());
            foreach (var (res, op) in added)
                if (_mode == "role") svc.SetRolePermission(_targetId, res, op, true); else svc.SetUserPermission(_targetId, res, op, true);
            foreach (var (res, op) in removed)
                if (_mode == "role") svc.SetRolePermission(_targetId, res, op, false); else svc.SetUserPermission(_targetId, res, op, false);
            _baseline = new HashSet<(string, string)>(_pending);
            AppContainer.Get<DialogService>().Info($"تم حفظ {added.Count + removed.Count} تغييراً وسُجلت في سجل التدقيق.");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Perm.Save"); }
    }

    // ═══ النسخ ═══
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window { Title = "نسخ الصلاحيات", Width = 460, Height = 240, FlowDirection = FlowDirection.RightToLeft, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) };
        var kind = new ComboBox(); kind.Items.Add("دور → دور"); kind.Items.Add("مستخدم → مستخدم"); kind.SelectedIndex = 0;
        var src = new ComboBox(); var dst = new ComboBox();
        void Fill()
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            if (kind.SelectedIndex == 0)
            {
                var roles = db.Roles.AsNoTracking().Select(r => new { r.Id, r.RoleNameAr }).ToList();
                src.ItemsSource = roles; src.DisplayMemberPath = "RoleNameAr"; src.SelectedValuePath = "Id";
                dst.ItemsSource = roles; dst.DisplayMemberPath = "RoleNameAr"; dst.SelectedValuePath = "Id";
            }
            else
            {
                var users = db.Users.AsNoTracking().Select(u => new { u.Id, Label = u.FullName }).ToList();
                src.ItemsSource = users; src.DisplayMemberPath = "Label"; src.SelectedValuePath = "Id";
                dst.ItemsSource = users; dst.DisplayMemberPath = "Label"; dst.SelectedValuePath = "Id";
            }
        }
        kind.SelectionChanged += (_, _) => Fill();
        Fill();
        var btn = new Button { Content = "تنفيذ النسخ (يُسجل كتغيير جماعي)", Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton"), Margin = new Thickness(0, 10, 0, 0) };
        btn.Click += (_, _) =>
        {
            try
            {
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var svc = new PermissionService(db, AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>());
                int s = (int)src.SelectedValue, d = (int)dst.SelectedValue;
                if (kind.SelectedIndex == 0) svc.CopyRolePermissions(s, d); else svc.CopyUserPermissions(s, d);
                AppContainer.Get<DialogService>().Info("تم النسخ وتسجيله في السجل.");
                win.Close(); LoadAll();
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().Error(ex.Message); }
        };
        var p = new StackPanel { Margin = new Thickness(14) };
        p.Children.Add(new TextBlock { Text = "المصدر:", FontWeight = FontWeights.Bold }); p.Children.Add(src);
        p.Children.Add(new TextBlock { Text = "الهدف:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) }); p.Children.Add(dst);
        p.Children.Add(kind); p.Children.Add(btn);
        win.Content = p; win.ShowDialog();
    }

    // ═══ المقارنة ═══
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var svc = new PermissionService(db, AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>());
        var roles = db.Roles.AsNoTracking().ToList();
        var win = new Window { Title = "مقارنة دورين (الفروقات)", Width = 860, Height = 520, FlowDirection = FlowDirection.RightToLeft, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var aBox = new ComboBox { ItemsSource = roles, DisplayMemberPath = "RoleNameAr", SelectedValuePath = "Id", Width = 200 };
        var bBox = new ComboBox { ItemsSource = roles, DisplayMemberPath = "RoleNameAr", SelectedValuePath = "Id", Width = 200 };
        var grid = new DataGrid { AutoGenerateColumns = false, Height = 380, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "المورد", Binding = new System.Windows.Data.Binding("Res"), Width = 220 });
        grid.Columns.Add(new DataGridTextColumn { Header = "العملية", Binding = new System.Windows.Data.Binding("Op"), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "أ", Binding = new System.Windows.Data.Binding("A"), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ب", Binding = new System.Windows.Data.Binding("B"), Width = 70 });
        void Run()
        {
            if (aBox.SelectedValue is not int ai || bBox.SelectedValue is not int bi) return;
            var sa = svc.GetRoleSet(ai); var sb = svc.GetRoleSet(bi);
            var rows = sa.Union(sb).OrderBy(x => x.res).ThenBy(x => x.op)
                .Where(x => sa.Contains(x) != sb.Contains(x))
                .Select(x => new { Res = x.res, Op = x.op, A = sa.Contains(x) ? "✔" : "—", B = sb.Contains(x) ? "✔" : "—" }).ToList();
            grid.ItemsSource = rows;
        }
        aBox.SelectionChanged += (_, _) => Run();
        bBox.SelectionChanged += (_, _) => Run();
        var sp = new StackPanel { Margin = new Thickness(12) };
        var hp = new StackPanel { Orientation = Orientation.Horizontal };
        hp.Children.Add(new TextBlock { Text = "الدور أ:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold }); hp.Children.Add(aBox);
        hp.Children.Add(new TextBlock { Text = "الدور ب:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 0, 0) }); hp.Children.Add(bBox);
        sp.Children.Add(hp);
        sp.Children.Add(new TextBlock { Text = "تُعرض الفروقات فقط:", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 6, 0, 4) });
        sp.Children.Add(grid);
        win.Content = sp; win.ShowDialog();
    }

    // ═══ السجل ═══
    private void Audit_Click(object sender, RoutedEventArgs e)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var svc = new PermissionService(db, AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>());
        var win = new Window { Title = "سجل تغييرات الصلاحيات (غير قابل للتعديل)", Width = 900, Height = 520, FlowDirection = FlowDirection.RightToLeft, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "التوقيت", Binding = new System.Windows.Data.Binding("ChangedAt") { StringFormat = "dd/MM/yyyy HH:mm" }, Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "بواسطة", Binding = new System.Windows.Data.Binding("ChangedByName"), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "نوع", Binding = new System.Windows.Data.Binding("ActionType"), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "الهدف", Binding = new System.Windows.Data.Binding("Target"), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "المورد", Binding = new System.Windows.Data.Binding("ResourceCode"), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "العملية", Binding = new System.Windows.Data.Binding("OperationCode"), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "قبل", Binding = new System.Windows.Data.Binding("OldValue"), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "بعد", Binding = new System.Windows.Data.Binding("NewValue"), Width = 90 });
        grid.ItemsSource = svc.GetAudit().Select(a => new
        {
            a.ChangedAt, a.ChangedByName, a.ActionType,
            Target = a.TargetRoleId != null ? $"دور #{a.TargetRoleId}" : a.TargetUserId != null ? $"مستخدم #{a.TargetUserId}" : "-",
            a.ResourceCode, a.OperationCode, a.OldValue, a.NewValue
        }).ToList();
        win.Content = grid; win.ShowDialog();
    }

    // ═══ تعطيل آمن ═══
    private void NewRole_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("إنشاء دور جديد", "اسم الدور (مثال: مشرف جودة متقدم):") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            db.Roles.Add(new DatesErp.Core.Domain.Entities.Role { RoleCode = "R-" + Guid.NewGuid().ToString("N")[..5].ToUpper(), RoleNameAr = dlg.Value.Trim(), IsActive = true });
            db.SaveChanges();
            AppContainer.Get<DialogService>().Info("أُنشئ الدور — امنحه الصلاحيات من الشجرة ثم اربطه بالمستخدمين.");
            LoadAll();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Perm.NewRole"); }
    }

    private void Delegation_Click(object sender, RoutedEventArgs e)
        => new DelegationWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (_targetId == 0) { AppContainer.Get<DialogService>().Error("اختر الهدف أولاً."); return; }
        if (!AppContainer.Get<DialogService>().Confirm(_mode == "role" ? "تعطيل الدور المحدد؟ (لا يُحذف)" : "تعطيل المستخدم المحدد؟ (لا يُحذف)")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = new PermissionService(db, AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>());
            if (_mode == "role") svc.DeactivateRole(_targetId); else svc.DeactivateUser(_targetId);
            AppContainer.Get<DialogService>().Info("تم التعطيل بأمان وسُجل.");
            LoadAll();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().Error(ex.Message); }
    }
}
