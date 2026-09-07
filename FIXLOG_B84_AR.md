# سجل إصلاحات B84 — من B83 إلى B84 (2026-09-05)

> النطاق: كل ملاحظات القاعدة الآمنة التنفيذ عميانياً (بلا مُصرّف/قاعدة هنا).
> الملاحظات الخطرة عميانياً أُجّلت بقرار موثق آخر هذا الملف — لا تُنفَّذ تخميناً.

## 1) طبقة الخدمات (Backend)

| الكود | الملف | الإصلاح |
|---|---|---|
| C1 | `ServiceBase.cs` | إعادة محاولة تلقائية (3 محاولات) داخل `RunInTransaction` عند تعارض القيد الفريد فقط: SQL Server (2627/2601) + SQLite (كود 19/نص UNIQUE بالاسم بلا اعتماديات). التكرار الحقيقي يفشل بالخطأ الأصلي نفسه. |
| C1 | `DependencyInjection.cs` | تصحيح تعليق «مضاد للتكرار نهائياً» المضلل → توثيق الطبقتين بصدق. |
| S1 | `PermissionService.cs` | `EnsureCatalogUpsert()` في أول `EnsureCatalog` (يضيف الموارد/العمليات الناقصة للقواعد القائمة) + `GrantReopenToApprovers()` (كل حامل Approve على planning يُمنح Reopen تلقائياً عند كل إقلاع — فلا إقفال). |
| S2 | `PlanClosureService.cs` | `ReopenPlan` يفحص `("planning","Reopen")` بدل `Approve` — تفعيل الصلاحية الميتة. |
| V3 | `CapacityService.cs` | `SaveShift` يرفض الأوقات غير المطابقة لـ HH:mm (كل البذور والاختبارات عليها أصلاً — فحص استدعاءات شامل). |
| V5 | `MasterDataService.cs` | `SaveDelegation` يرفض النطاق غير الفارغ ما لم يكن كوداً من `ResourceCatalog`. |
| H9 | `IPlatformServices.cs` + `AuthService.cs` + `LoginWindow` + 7 ملفات اختبار | حذف معامل `rememberMe` الميت (20 موقع استدعاء) + الثلاثي المضلل. صفر `rememberMe` الآن. |
| H6 | `Bootstrapper.cs` | `AppVersion` من التجميع (`Version.ToString(3)`) بدل الثابت `1.0.0` + خط أساس `1.0.x` متوافق بالتعريف + ختم `DbVersion` بعد نجاح الفحص (يتطلب `Version 1.45.1` في csproj — طُبّق). |
| H8 | `Bootstrapper.cs` | `reset_admin*` يعيد كلمة حاملي دور `Administrator` فقط (+ احتياطي `admin` بالاسم) بدل *كل* الحسابات. |

## 2) الشاشات والنوافذ (Desktop UI)

| الكود | الملف | الإصلاح |
|---|---|---|
| B1 | `ErpChrome.xaml.cs` | زر ✕ بلا مشتركين → عودة للوحة المؤشرات (كان ميتاً في ~17 شاشة). |
| B2 | `LoginWindow.xaml.cs` | عكس التركيز المعكوس: فارغ ← الاسم، مملوء ← كلمة المرور. |
| B3 | `LoginWindow.xaml.cs` | `remember.txt` إلى `LocalApplicationData` + تهجير تلقائي لمرة واحدة من المسار القديم. |
| B5 | `LoginWindow.xaml(.cs)` | أزرار ─/□/✕ الزخرفية أصبحت وظيفية (تصغير/تكبير/إغلاق) + `IsDefault` لزر الدخول. |
| B4/D1 | `MainWindow` + `DashboardView` | شريط الحالة حي: الإصدار من التجميع + نوع القاعدة (`IsSqlServer`) + السنة الحالية. |
| B6/V7/K1 | `EntityFormDialog.xaml.cs` | نوع `number` حقيقي (أرقام فقط) + إزالة التسمية المكررة لـ `check` + حقل `Required` اختياري (افتراضي false: لا يتغير سلوك الشاشات القائمة) + `Trim` + Enter/Escape. |
| B8/K1 | `InputDialog`, `ChangePassword` (حُذف معالج Enter اليدوي المكرر), `DocSearch`, `LotsEditor`, `FairDistribution`, `FairSummary`, `DetailList`, `CloseDay` | Enter/Escap موحدة. |
| B8 | `QuickOpenWindow` | Enter يفتح الأول + Escape يغلق + مؤشر «لا توجد شاشات مطابقة»/عداد. |
| B8 | `TraceabilityWindow` | `CenterOwner` + Escape + حد أدنى 900×520 (كانت بلا زر إغلاق وتضيع خلف النوافذ). |
| V5 | `DelegationWindow` | النطاق `ComboBox` من `ResourceCatalog` + أول عنصر «كل الوحدات» + Escape + حد أدنى. |
| B8 | `GroupsAndCategoriesWindow`, `QualitySetupWindow` | Escape + حد أدنى. |
| B7 | `MaterialsView` | زر التحديث كان يستدعي `Order_Changed` → معالج `Refresh_Click` باسمه. |
| K1 | `ConnectionSetupWindow` | Enter يختبر أولاً ثم يتحول الافتراضي لزر الحفظ بعد النجاح. |
| M4 | `DocSearchWindow` | سقف 500 صف مع تنبيه التضييق. |
| D7 | `QuickOpen`, `PrintPreview`, `ItemsCapacities`, `PlanningView` | تلميحات 10.5/11 ← 12 (مقروئية). عناوين جداول الطباعة 10.5 تُركت (كثافة طباعية). |
| — | `PrintPreviewWindow.xaml` | صياغة التلميح: «معاينة حية مطابقة للمطبوع — راجع ثم اطبع أو صدّر PDF». |

## 3) الطباعة والتقارير (P1–P8)

| الكود | الملف | الإصلاح |
|---|---|---|
| P1 | `ExportPrintService.cs` | `ComputeTotals` يستبعد الأعمدة غير القابلة للجمع (نسب/متوسطات/أسعار/أرقام تعريفية) + حارس سنوات (ترويسة سنوية + قيم 1900–2100). |
| P2 | `ExportPrintService.cs` | الإجماليات N1 ← N2 في الشريط النصي + PDF + المعاينة (موحدة مع الخلايا). |
| P3 | `ExportPrintService.cs` | حذف سطري `Fax/P.O.Box: -` نهائياً + سطر الهاتف شرطي. |
| P4 | `ExportPrintService.cs` | PDF كان عرضياً دائماً ← يتبع قاعدة المعاينة (>8 أعمدة). |
| P6 | `ExportPrintService.cs` | فصل `WritePdf(report, path)` العامة — `ExportPdf` غلاف رفيع (حوار + رسالة). |
| P5 | `ExportPrintService.cs` + `PhasePrintDocuments.cs` | `FlowDirection.RightToLeft` (الجداول العربية كانت تُرسم LTR). |
| P7 | `PhasePrintDocuments.cs` | التوقيعات كانت تُحذف بصمت عند امتلاء الصفحة ← صفحة جديدة تلقائياً. |
| P8 | `PlanningPrintDocument.cs` | بديل الشعار 🌴 ← اسم الشركة بخط مميز + الأبعاد 1160×820 ← 1122×794 + حذف إيموجي الرؤوس/التعليقات + فاصل اليوم يعرض التاريخ الفعلي بدل 📅 وحيدة. |
| P8 | `ReceivingPrintDocument.cs` | بديل الشعار 🌴 ← اسم الشركة + الأبعاد 820×1160 ← 794×1122. |
| — | — | علامات ✓ في المطبوعات أُبقيت (dingbat آمنة). إيموجي الشاشات التفاعلية أُبقي. |

## 4) البنية والمشروع

| الكود | الإصلاح |
|---|---|
| C3 | `DateERP.sln` يضم الآن Core + Application + Infrastructure + AcceptanceRunner (مجلد tools جديد) — GUIDs جديدة، BOM+CRLF محفوظان. |
| C7 | فك ترميز 44 مساراً `#UXXXX` ← عربية سليمة في `Documentation/` (صفر `#U` الآن، ولا مرجع برمجي للأسماء القديمة). |
| M6 | مهمة CI «اختبارات القبول» (`dotnet run tools/AcceptanceRunner --no-build` بعد البناء). |
| M1 | توسيع `.gitignore` الجذري (كان 5 سطور: bin/obj/user/vs/publish فقط) ليشمل out/log/suo/TestResults/trx/logs/remember/reset_admin*/appsettings.local. |
| M9 | `LICENSE` عربية (ملكية خاصة). |
| H6 | `Version 1.45.1` + `BuildInfo.Stamp = 2026-09-05 B84`. |

## 5) فحوصات أُجريت (بلا مُصرّف هنا — تحقق استاتيكي)

- توازن الأقواس: 29/29 ملفاً معدلاً سليماً (بما فيها إصلاح تلف عارض في `PlanningWindows.cs` أُعيدت فيه سطور من B83 حرفياً).
- مراجعة سطر-بسطر لكل سطور الـ diff المضافة (366 سطراً): كلها مقصودة، لا دخيل.
- كل مرجع جديد مُتحقق: `DialogService.Error`، `SystemRoles.Administrator`، حقول `UserRole`، `ResourceCatalog` (static + الشكل)، `AppContainer.NewScope()`، usings (EFCore/Services/Persistence/Input/Globalization)، أشكال الكيانات.
- `Login(` ثنائية في كل المواقع (20/20) + الواجهة + التنفيذ. صفر 3-arg.
- كل استدعاءات `SaveShift` (بذور + اختبارات) بصيغة HH:mm — تحقق شامل.
- `SaveDelegation` مستدعاة من `DelegationWindow` فقط — آمنة للتحقق الجديد.

## 6) ملاحظات لم تُنفَّذ (بقرار)

- **D2**: لم يُعثر على اسم شركة ثابت في B84 (الترويسة تربط `TitleText` حياً) — أُسقطت كإنذار كاذب.
- **B7-Orders**: لا ازدواج سفلي في `OrdersView` (زر طباعة/تحديث واحد، والطباعة حقيقية) — أُسقطت.
- **BackupView**: لا `IsDefault` عمداً — Enter أثناء كتابة المسار قد يشغّل نسخاً/استعادة بالخطأ.
- **المؤجل الخطر عميانياً** (يحتاج مُصرّف/قاعدة/اختبار حي): C2 الترحيلات، C4 الـ async، C5 العشري-كنص، C6 كتابة الواجهة، H1 الـ double، H2 الـ Serializable، H3 الـ SessionContext، H4 الـ UtcNow، H5 مزود الاختبار، H7 التحذيرات، M5 انحراف Docs، M7 التعداد، M8 ازدواج الطباعة، M10 توثيق الكاش.

## 7) البناء بعد الاستلام (ويندوز + .NET 8 SDK)

```powershell
dotnet build DateERP.sln -c Release
dotnet test tests/DatesErp.Tests/DatesErp.Tests.csproj -c Release --no-build
dotnet run --project tools/AcceptanceRunner/AcceptanceRunner.csproj -c Release --no-build
```
