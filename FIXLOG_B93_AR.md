# سجل إصلاحات B93 — ترحيل الخطة المعتمدة إلى أوامر تشغيل (2026-09-05)

## §1 — المرحلة التالية بعد إعداد الخطة
بعد الاعتماد: «📋 إصدار الأوامر» من شاشة التخطيط (أو «📋 ترحيل خطة إلى أوامر» من شاشة الأوامر)
يرحّل بنود الخطة ذات المتبقي إلى أوامر إنتاج — **أمر واحد لكل (تاريخ مجدول × وردية × خط)**
بكامل المتبقي، مع فلاتر اختيارية للفترة والوردية.

## §2 — القواعد (صدق كامل)
- الترحيل من المعتمدة فقط (غير المعتمدة/المقفلة/الملغاة تُرفض بسببها).
- المتبقي فقط (المخطط − أوامر سابقة غير ملغاة)؛ الترحيل الثاني لنفس الخطة يتخطى بلا أوامر.
- البنود بلا تاريخ تُلحق ببداية الفترة، وبلا وردية بوردية الخطة — ويُذكر ذلك صراحة (لا صمت).
- مجموعة متعددة العملاء: رأس الأمر بلا عميل والعميل محفوظ على كل بند (يمر حراس §8).
- كل مجموعة معاملة مستقلة عبر `SaveOrder` نفسه (حراس المتبقي/الطاقة/الهوية/التحويل) —
  فشل مجموعة لا يوقف البقية، والملخص يعرض المنشأة والمتخطاة والفاشلة بأسبابها.

## §3 — الربط بين الشاشات
- `MainWindow.OpenIssuePlanOrders(planId)` + `PendingIssuePlanIdToOpen` — من التخطيط للأوامر
  ونافذة الترحيل تفتح والخطة محددة مسبقاً.
- `IssuePlanWindow`: اختيار الخطة/الفترة/الوردية → ترحيل → شريط نتيجة + جدول الأوامر + الملاحظات.

## §4 — الملفات (9 + اختباران)
1. `Core/.../IWorkflowServices.cs` — `IssueOrdersFromPlan` + `PlanIssueResult/IssuedOrderDto`.
2. `Application/.../ProductionOrderService.cs` — التنفيذ.
3. `Desktop/.../OrdersWindows.cs` — `IssuePlanWindow`.
4. `Desktop/.../OrdersView.xaml` + `.xaml.cs` — الزر + المعالج + استهلاك الترحيل المعلق.
5. `Desktop/.../MainWindow.xaml.cs` — المعلق + المساعد.
6. `Desktop/.../PlanningView.xaml` + `.xaml.cs` — زر إصدار الأوامر.
7. `Services/BuildInfo.cs` — الختم B93.
8. `tests/.../PlanIssueB93Tests.cs` — جديد (4 اختبارات). 9. `tests/.../UiB93Tests.cs` — جديد.

## §5 — التحقق على ويندوز (تسلسلياً)
```powershell
dotnet build DateERP.sln -c Release
dotnet test tests/DatesErp.Tests/DatesErp.Tests.csproj -c Release --filter "FullyQualifiedName~PlanIssueB93Tests"
dotnet test tests/DatesErp.Tests/DatesErp.Tests.csproj -c Release --filter "FullyQualifiedName~UiB93Tests"
```
فحص يدوي: اعتمد خطة فترية → «📋 إصدار الأوامر» → رحّل → راجع الأوامر المنشأة بتجميع أيامها ووردياتها.
