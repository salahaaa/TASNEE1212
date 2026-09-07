# سجل إصلاحات B94 — لا قفز بين الشاشات + بوابة صلاحيات العرض (2026-09-05)

## §1 — القرار (طلب الإدارة)
النظام مبني على صلاحيات وكل موظف مختص بشاشته — فلا زر في شاشة ينقل لأخرى.

## §2 — الإزالة
- حُذف زر «📋 إصدار الأوامر» من شاشة التخطيط ومعالجه (`IssueOrders_Click`)،
  وحُذفت آلية القفز (`OpenIssuePlanOrders` + `PendingIssuePlanIdToOpen`) والفتح التلقائي من شاشة الأوامر.
- الترحيل باقٍ **من داخل شاشة الأوامر فقط** (زرها «📋 ترحيل خطة إلى أوامر» — بصلاحية `production/Create`) —
  موظف التخطيط يعتمد، وموظف الأوامر يرحّل، كلٌّ في شاشته.

## §3 — بوابة العرض المركزية (`MainWindow.CanOpenScreen`)
- `OpenScreen` يفحص قبل الفتح: (وحدة الشاشة / عرض) — والرفض برسالة تسمي الصلاحية المطلوبة.
- البوابة تشمل القائمة الجانبية وبطاقات اللوحة وتدقيق التقارير — نقطة فحص واحدة للجميع.
- أمان الإقفال: البوابة تُطبق فقط على الوحدات الـ16 المزروعة في المصفوفة
  (كل دور مزروع يملك View عليها — فلا يُقفل أحد كذباً)؛ اللوحة للجميع؛
  والوحدات بلا سطر صلاحيات (منتجات/كرتون/موظفون) تُفتح بلا منع صامت.
- ملاحظة للمدير: الأدوار المخصصة التي تُنشأ من المصفوفة تُمنع من الشاشات بلا View —
  امنح (الوحدة / عرض) من «الأدوار والصلاحيات» لمن يستحق.

## §4 — الملفات
1. `Views/MainWindow.xaml.cs` — حذف القفز + `GatedModules` + `CanOpenScreen` + الفحص.
2. `Views/Screens/PlanningView.xaml` + `.xaml.cs` — حذف الزر والمعالج.
3. `Views/Screens/OrdersView.xaml.cs` — حذف الاستهلاك التلقائي.
4. `Services/BuildInfo.cs` — الختم B94.
5. `tests/.../UiB94Tests.cs` — جديد (3 اختبارات)؛ حُذف `UiB93Tests.cs` (كان يوثق القفزة).

## §5 — التحقق على ويندوز (تسلسلياً)
```powershell
dotnet build DateERP.sln -c Release
dotnet test tests/DatesErp.Tests/DatesErp.Tests.csproj -c Release --filter "FullyQualifiedName~UiB94Tests"
dotnet test tests/DatesErp.Tests/DatesErp.Tests.csproj -c Release --filter "FullyQualifiedName~PlanIssueB93Tests"
```
فحص يدوي: ادخل بدور إنتاج — لا زر يقفز بك لشاشة أخرى؛ امنع View عن دور مخصص —
رسالة المنع تظهر عند محاولة الفتح.
