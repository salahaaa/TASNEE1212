# 📋 مواصفة إضافة: ترحيل المتبقي + إعادة التوزيع بالمراجعات لخطط الإنتاج

**النظام:** الأساسي (DateERP B83) — **الحالة:** مواصفة تصميمية (لا تمس الكود)
**المصدر:** دروس النظام المرجعي (DatePack v1.59: `rollover-incomplete` + `reallocate`)
**التوافق:** نفس أسلوب الكود القائم (`PlanningService` / `PlanClosureService` / `PermissionService` / `TestHost`)

---

## 0. الهدف والنطاق

| الميزة | الوصف |
|---|---|
| ⏭ **ترحيل المتبقي** | نقل الكميات غير المنتجة من بنود خطة معتمدة إلى **خطة يومية جديدة مرتبطة** بتاريخ مستهدف، مع إغلاق البنود المصدر وتحرير حجوزاتها |
| ⚖ **إعادة التوزيع** | تعديل كميات بنود خطة **معتمدة** (زيادة/إنقاص) بسبب موثق إلزامي + ترقيم مراجعات + حفظ الكمية الأصلية للأبد |

**خارج النطاق:** تغيير العميل/الصنف/الدفعة في إعادة التوزيع (الكميات فقط) — تغيير تواريخ البنود — الترحيل التلقائي عند إقفال الوردية (يُدرس لاحقاً).

### 0.1 ماذا نفعل أفضل من المرجعي؟

| المرجعي (نقاط ضعف موثقة) | تصميمنا |
|---|---|
| الترحيل **شكلي**: يوسم البنود `RolledOver` فقط ولا ينشئ خطة جديدة ولا رابطاً فعلياً | ترحيل **حقيقي**: خطة جديدة برقم مستند + رابط `RolloverFromPlanId/ItemId` في الاتجاهين |
| بلا فحص طاقة على التاريخ المستهدف | فحص طاقة إلزامي بآلية `EnsureSlotCapacity` نفسها — العملية ذرية (الكل أو لا شيء) |
| بلا خصم للأوامر النشطة → خطر تخطيط مزدوج | المرحَّل = المخطط − المنتَج − **المغطى بأوامر نشطة** |
| المراجعات بلا حماية إنقاص تحت المنتَج | يُرفض أي إنقاص تحت `max(المنتَج، المغطى بالأوامر)` — يحمي ثابت `CheckPlanRemaining` |

### 0.2 تفاعل مقصود مع قاعدة B79

`TryAutoCloseIfComplete` **لا يُقفل تلقائياً** بالتصميم (المكتملة ≠ المقفلة). الترحيل يحترم ذلك: البنود المرحّلة تُوسم `IsClosed` لكن الخطة المصدر تبقى تحتاج **الإقفال الرسمي** من شاشة «إقفال خطة الإنتاج» — لا إقفال ضمني.

---

## 1. الكيانات (DatesErp.Core/Domain/Entities/Production.cs)

### 1.1 حقول جديدة على `ProductionPlan`

```csharp
/// <summary>§TR1: رقم مراجعة إعادة التوزيع — يبدأ 0 ويزيد مع كل إعادة توزيع ناجحة.</summary>
public int RevisionNo { get; set; }
/// <summary>§TR1: سبب آخر مراجعة (نص حر) — السجل الكامل في التدقيق.</summary>
public string RevisionReason { get; set; }
/// <summary>§TR1: الخطة المصدر إن كانت هذه الخطة ناتجة عن ترحيل متبقٍ (وإلا null).</summary>
public int? RolloverFromPlanId { get; set; }
```

### 1.2 حقول جديدة على `ProductionPlanItem`

```csharp
/// <summary>§TR1: لقطة المخطط الأصلي عند أول إعادة توزيع — لا تُمس بعدها أبداً (أساس المقارنة).</summary>
public double? OriginalPlannedQtyKg { get; set; }
/// <summary>§TR1: بند المصدر إن كان هذا البند ناتجاً عن ترحيل (وإلا null).</summary>
public int? RolloverFromItemId { get; set; }
```

### 1.3 قيمة حالة تنفيذ جديدة

`ExecutionStatus` (نص حر أصلاً: `NotStarted | InProgress | Partial | Completed`) تُضاف له القيمة الموثقة:

- `RolledOver` = «رُحّل المتبقي لخطة تالية» — تُضبط مع `IsClosed = true` على البند المصدر.

### 1.4 ملاحظات التعيين (EF)

- الحقول الجديدة **أعمدة بسيطة بلا خصائص تنقل** — لا حاجة لأي ضبط في `OnModelCreating` (العلاقات هناك صريحة ولا تتأثر).
- `RolloverFromPlanId / RolloverFromItemId`: مجرد `int?` بلا علاقة — يُقرأ بالاستعلام المباشر عند الحاجة (سجل الخطة، الطباعة).

---

## 2. الخدمات

### 2.1 حقن التدقيق في `PlanningService`

المنشئ الحالي `(db, session, numbering)` بلا تدقيق. يُضاف `IAuditService audit` (مسجل أصلاً في `AppContainer` و`TestHost` — لا تغيير في التسجيلات، يُتحقق بالبناء فقط):

```csharp
public PlanningService(DatesErpDbContext db, ICurrentSession session,
    INumberingService numbering, IAuditService audit) : base(db, session, numbering)
{ _audit = audit; }
```

### 2.2 الواجهة `IPlanningService` (ملف IWorkflowServices.cs)

```csharp
/// <summary>§TR1: ترحيل المتبقي غير المنتج (بعد خصم الأوامر النشطة) إلى خطة يومية جديدة.</summary>
OpResult RolloverPlanItems(int planId, string targetDate, string reason, List<int> itemIds = null);
/// <summary>§TR1: إعادة توزيع كميات بنود خطة معتمدة بسبب موثق + ترقيم مراجعة.</summary>
OpResult ReallocatePlan(int planId, List<ReallocateItemDto> items, string reason);
```

### 2.3 DTO جديد (نفس الملف)

```csharp
/// <summary>§TR1: بند إعادة توزيع — الكمية الجديدة فقط (الكراتين تُشتق عبر UnitsPolicy).</summary>
public class ReallocateItemDto
{
    public int PlanItemId { get; set; }
    public double NewQtyKg { get; set; }
}
```

### 2.4 `RolloverPlanItems` — الخوارزمية والحراس

```
Require("planning", "Rollover")
1. الخطة موجودة + IsApproved + ليست IsClosed + Status ∉ {Closed, Cancelled} — وإلا رسالة مخصصة لكل حالة.
2. السبب إلزامي (≥ 10 أحرف بعد التشذيب) — «سبب الترحيل إلزامي (10 أحرف على الأقل) ويُسجَّل في التدقيق.»
3. التاريخ المستهدف: صالح (UiFormat.TryParseDate) + ليس ماضياً + بعد تاريخ آخر بند مرحّل
   (ترحيل للمستقبل فقط — يمنع الازدواج على نفس الخانة ويُبقي فحص الطاقة سليماً).
4. البنود المؤهلة: من itemIds (بعد التحقق من انتمائها للخطة) أو كل البنود حيث:
     !IsClosed  و  (PlannedQtyKg − ProducedQtyKg) > 0.001
   الكمية المرحّلة = المخطط − المنتَج − المغطى بأوامر نشطة
     (أوامر غير ملغاة/مقفلة عبر PlanItemId — نفس تعريف «أوامر سابقة» في CheckPlanRemaining)
   > تُستبعد البنود الصفرية من العملية مع ذكرها في رسالة النجاح («تُخطي N بنداً مغطى بالكامل»).
   > لا بنود مؤهلة إطلاقاً → Fail «لا يوجد متبقٍ قابل للترحيل في هذه الخطة.»
5. إنشاء الخطة الجديدة (مسودة تحتاج تقديم/اعتماد — لا اعتماد ضمني):
     DocumentNumber = Numbering.Next("PLAN")، العنوان = «ترحيل المتبقي من {SrcNo} — {target:dd/MM/yyyy}»
     PlanType = "Daily"، Start = End = التاريخ المستهدف
     ScopeMode / SingleCustomerId / ShiftId / LineId تُورَّث من المصدر
     Notes = «رُحّل من الخطة {SrcNo}: {reason}»، RolloverFromPlanId = المصدر
   لكل بند: نسخ (SourceType/LotId/ShipmentId/CustomerId/ProductId/PackagingTypeId/PriorityNo/
     SuggestedShiftId/SuggestedLineId) + Planned = المرحّلة + الكراتين مشتقة + ScheduledDate = المستهدف
     + Status = Draft + RolloverFromItemId = بند المصدر.
6. فحص الطاقة لكل بند مرحّل على التاريخ المستهدف بإعادة استخدام EnsureSlotCapacity
   (بناء PlanItemDto + تراكم محلي) — أي فشل يُفشل العملية كاملة (ذرية) برسالة على نمط ApprovePlan.
7. إغلاق بنود المصدر: IsClosed = true + ExecutionStatus = "RolledOver" + ReleasedQtyKg = المرحّلة
   (PlannedQtyKg لا يُمس — أساس B18).
8. ApplyLotReservations (للخطتين) + Db.SaveChanges().
9. تدقيق: Log("خطط الإنتاج"، "ترحيل المتبقي"، "Plan"، SrcNo، id،
     old: { البنود، الكميات_المرحلة }، new: { الخطة_الجديدة، السبب }).
10. Success «تم ترحيل المتبقي (X كجم / N بنداً) إلى الخطة الجديدة {NewNo} — قدّمها للاعتماد.» (+ رقم المستند تلقائياً).
```

### 2.5 `ReallocatePlan` — الخوارزمية والحراس

```
Require("planning", "EditAfterApproval")
1. الخطة موجودة + IsApproved + ليست مسودة (المسودات عبر UpdatePlan) + ليست IsClosed/ملغاة.
2. السبب إلزامي (≥ 10 أحرف) — «سبب إعادة التوزيع إلزامي…»
3. لكل بند: ينتمي للخطة + !IsClosed + NewQtyKg > 0
   + NewQtyKg ≥ ProducedQtyKg («لا يمكن إنقاص البند تحت ما أُنتج فعلاً»)
   + NewQtyKg ≥ المغطى بأوامر نشطة («…تحت ما غطته أوامر الإنتاج» — حماية ثابت CheckPlanRemaining).
4. فحص الطاقة للزيادات فقط على خانة البند (تاريخه/ورديته/خطه) باستثناء هذه الخطة + تراكم محلي
   (مرآة منطق ApprovePlan) — أي تجاوز يُفشل العملية كاملة.
5. اللقطة: إن كان OriginalPlannedQtyKg == null ← القيمة الحالية (أول إعادة توزيع فقط).
   PlannedQtyKg = الجديد + PlannedCartons يُشتق عبر UnitsPolicy.EnsureCartonKgConsistency.
6. RevisionNo++ + RevisionReason = السبب.
7. إرجاع الحالة: إن كان ExecutionStatus = "Completed" والجديد > المنتَج ← "InProgress" (لا إكمال تلقائي أبداً).
8. ApplyLotReservations(plan) + تدقيق per-item (قديم/جديد لكل بند + السبب + رقم المراجعة).
9. Success «تمت إعادة التوزيع (المراجعة رقم N) — M بنداً.»
```

### 2.6 رسائل الخطأ (عربية، على نمط الملف)

- «الخطة غير معتمدة — الترحيل من خطة معتمدة فقط.»
- «الخطة مقفلة/ملغاة — لا يمكن الترحيل منها.»
- «التاريخ المستهدف يجب أن يكون بعد تاريخ آخر بند (الترحيل للمستقبل فقط).»
- «⛔ طاقة الوردية يوم {d} لا تكفي المرحّل: المتاح {a} كرتون | المطلوب {r} — اختر تاريخاً آخر.»
- «لا يمكن إنقاص بند {name}: الجديد {n} < المنتَج {p}.»
- «لا يمكن إنقاص بند {name}: الجديد {n} < المغطى بالأوامر {o}.»

---

## 3. الصلاحيات (PermissionService)

### 3.1 القرار

| العملية | الصلاحية | السبب |
|---|---|---|
| إعادة التوزيع | `Require("planning", "EditAfterApproval")` — **موجودة** | تفعيل لصلاحية ميتة في الكتالوج بنفس المعنى تماماً («تعديل بعد الاعتماد») — صفر تغيير في الكتالوج |
| الترحيل | عملية جديدة `Rollover` = «ترحيل المتبقي لخطة تالية» (حساسة) | لا عملية قائمة تعبّر عنها (`Post` تعني الترحيل المحاسبي لا الزمني) |

### 3.2 التغييرات

1. `OperationCatalog` += `("Rollover", "ترحيل المتبقي لخطة تالية", true)`.
2. **إكمال الكتالوج بقوة** (إصلاح latent): `EnsureCatalog` حالياً يبذر فقط عندما تكون الجداول فارغة — فقواعد الإنتاج القائمة لن ترى العملية الجديدة أبداً. يُضاف `EnsureCatalogCompleteness()` يُستدعى من `EnsureCatalog`:
   - upsert ناقص الأكواد في `PermissionResources` و`PermissionOperations` (مقارنة بالـ Code، مع SortNo تالٍ).
   - منح `(planning, Rollover)` لكل دور يملك `(planning, Approve)` = مسموح → عملياً: Administrator وManagement وProduction (مطابق لمنح البذور).
   - المدير العام يُغطى أصلاً بحلقة الاستكمال القائمة.
3. **لا تغيير** في `PermissionFlags` القديمة (مسار البذر فقط) — المنح الجديد يتم عبر خطوة (2) الصريحة والموثقة.

---

## 4. الواجهة (DatesErp.Desktop)

### 4.1 شاشة الخطة (PlanningView) — زرّان جديدان

| الزر | متى يظهر مفعّلاً | السلوك |
|---|---|---|
| ⏭ «ترحيل المتبقي» | خطة حالية معتمدة وغير مقفلة | `InputDialog` للتاريخ المستهدف (افتراضي = الغد `dd/MM/yyyy`) ← `InputDialog` للسبب ← `svc.RolloverPlanItems` ← `Info`/`Error` ← `RefreshPlansList()` + إعادة تحميل اللوحات — نفس نمط `Approve()` (سطور 816-831) |
| ⚖ «إعادة التوزيع» | نفس الشرط | فتح `PlanReallocateWindow` أدناه |

- إنفاذ الصلاحيات في طبقة الخدمة (مطابق للملف الحالي الذي لا يحجب الأزرار مسبقاً) + رسالة الرفض من `PermissionDeniedException` تُعرض كما هي.
- سجل الخطط: عمود «مراجعة» (يعرض `–` لصفر أو `R{n}`) + شارة 🔁 للخطط المرحّلة (`RolloverFromPlanId != null`) مع Tooltip برقم المصدر.

### 4.2 نافذة جديدة `PlanReallocateWindow` (code-behind على نمط DelegationWindow)

- الرأس: رقم الخطة + عنوانها + «المراجعة الحالية: N».
- جدول البنود (للقراءة + عمود إدخال): البند | الصنف | الدفعة | المخطط | المنتَج | المغطى بالأوامر | **الكمية الجديدة** (قابلة للتحرير، افتراضي = الحالي) | الحد الأدنى المحسوب.
- حقل السبب (إلزامي، عدّاد يُظهر 10 أحرف كحد أدنى، زر الحفظ معطّل قبله).
- تحقق مسبق في الواجهة (UX فقط — الحقيقة في الخدمة): الجديد > 0 و≥ الحد الأدنى.
- زر «تنفيذ إعادة التوزيع» ← تأكيد `Confirm` يعرض عدد البنود المتغيرة ← `svc.ReallocatePlan` ← إغلاق + تحديث الشاشة الأم.

### 4.3 الطباعة (PlanningPrintDocument)

- سطر في الترويسة عند `RevisionNo > 0`: «مراجعة رقم {N} — {RevisionReason}».
- سطر فرعي عند `RolloverFromPlanId != null`: «خطة مرحّلة من {رقم المصدر}».
- (تُقرأ الأرقام باستعلام خفيف — لا تغيير في بنية المستند.)

---

## 5. التدقيق والتقارير

- كل ترحيل/إعادة توزيع = صف `AuditLog` عبر `IAuditService.Log` (الشاشة «خطط الإنتاج»، النوع «Plan») — القيم قبل/بعد كائنات بأسماء عربية على نمط `PlanClosureService`.
- تقرير «الخطة مقابل الفعلي»: يستخدم `PlannedQtyKg` (محفوظ دائماً — B18) — لا تغيير، لكن يُضاف تنبيه مراجعة: بنود `OriginalPlannedQtyKg != null` تُظهر الأصل بين قوسين.
- `PlanClosureService.GetInfo`: البنود المرحّلة (`IsClosed`) لا تحجز ولا تمنع — تُعامل كالمقفلة عادياً؛ تُذكر في ملخص الإقفال ضمن «المحرر بالترحيل» (مجموع `ReleasedQtyKg` حيث `ExecutionStatus = RolledOver`) — سطر عرض فقط.

---

## 6. الاختبارات (tests/DatesErp.Tests/PlanRolloverReallocateTests.cs)

نفس البنية: `TestHost` + `host.LoginAsAdmin()` + مساعد `Build` يزرع (استلام ← اعتماد ← دفعة ← خطة ← اعتماد ← أمر اختياري) على نمط `ClosingPreservesPlanBaselineTests.Build`.

| # | الاختبار ([Fact]) | التوقع |
|---|---|---|
| 1 | `Rollover_Creates_Linked_Daily_Plan_With_Remaining_Only` | خطة جديدة `Daily` بنفس التاريخ المستهدف + `RolloverFromPlanId` صحيح + كميات = المتبقي فقط |
| 2 | `Rollover_Marks_Source_Closed_Preserves_Baseline` | المصدر: `IsClosed` + `RolledOver` + `ReleasedQtyKg` = المرحّل + `PlannedQtyKg` **لم يتغير** |
| 3 | `Rollover_Skips_Items_Covered_By_Active_Orders` | بند مغطى كلياً بأمر نشط يُستبعد (لا بند مقابل في الجديدة) ويُذكر في الرسالة |
| 4 | `Rollover_Rejects_Without_Reason` | سبب فارغ/قصير ← `Ok == false` ولا خطة جديدة |
| 5 | `Rollover_Rejects_Past_Or_Same_Period_Target` | تاريخ ماضٍ أو ≤ تاريخ البنود ← رفض |
| 6 | `Rollover_Rejects_When_Target_Capacity_Insufficient` | ملء خانة التاريخ المستهدف مسبقاً ← رفض ذري (لا خطة + المصدر مفتوح) |
| 7 | `Rollover_Requires_Permission` | مستخدم بلا `Rollover` ← `PermissionDeniedException` |
| 8 | `Reallocate_Bumps_Revision_And_Snapshots_Original` | زيادة ← `RevisionNo` 0→1 + `Original` = القديم + `Planned` = الجديد |
| 9 | `Reallocate_Rejects_Below_Produced_And_Below_Ordered` | إنقاص تحت المنتَج أو تحت المغطى ← رفض + `RevisionNo` لم يتغير |
| 10 | `Reallocate_Rejects_Capacity_Overflow` | زيادة تتجاوز طاقة الخانة ← رفض ذري |
| 11 | `Reallocate_Second_Time_Keeps_First_Baseline` | ثانية ← `RevisionNo` = 2 + `Original` ما زال الأول |
| 12 | `Rollover_Plan_Closes_Normally` | الخطة المرحّلة تُقفل رسمياً عبر `ClosePlanFinal` بلا موانع order-based + المصدر يُقفل مستقلاً |

---

## 7. الترقية (قواعد البيانات القائمة)

النظام يستخدم `EnsureCreated` (بلا Migrations) — القواعد القائمة تحتاج سكربت يُنفَّذ **مرة واحدة** عند الترقية:

**SQLite:**
```sql
ALTER TABLE ProductionPlans ADD COLUMN RevisionNo INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ProductionPlans ADD COLUMN RevisionReason TEXT NULL;
ALTER TABLE ProductionPlans ADD COLUMN RolloverFromPlanId INTEGER NULL;
ALTER TABLE ProductionPlanItems ADD COLUMN OriginalPlannedQtyKg REAL NULL;
ALTER TABLE ProductionPlanItems ADD COLUMN RolloverFromItemId INTEGER NULL;
```

**SQL Server:**
```sql
ALTER TABLE ProductionPlans ADD RevisionNo INT NOT NULL DEFAULT 0;
ALTER TABLE ProductionPlans ADD RevisionReason NVARCHAR(MAX) NULL;
ALTER TABLE ProductionPlans ADD RolloverFromPlanId INT NULL;
ALTER TABLE ProductionPlanItems ADD OriginalPlannedQtyKg FLOAT NULL;
ALTER TABLE ProductionPlanItems ADD RolloverFromItemId INT NULL;
```

- القيم الافتراضية آمنة: `RevisionNo = 0` (لا مراجعات) + `NULL` (لا أصل/لا ترحيل) — سلوك البيانات القديمة لا يتغير.
- منح `(planning, Rollover)` للأدوار يتم تلقائياً عبر `EnsureCatalogCompleteness()` عند أول إقلاع بعد الترقية (§3.2).
- **توصية مستقبلية:** عدّاء ترحيل إصدارات في `Bootstrapper` بدل السكربت اليدوي (خارج هذه المواصفة).

---

## 8. ملحق اختياري: النوع الأسبوعي في الواجهة

`PeriodEndDate` يدعم `Weekly` أصلاً لكن الواجهة تعرض يومية/فترة فقط. التكلفة: إضافة خيار «أسبوعية» لقائمة النوع + `EndDate = Start + 6` تلقائياً. (مطابق للمرجعي: يومية/أسبوعية/فترة.) — يُنفَّذ مع هذه الحزمة أو يُؤجَّل.

---

## 9. معايير القبول

- [ ] كل حراس §2.4/§2.5 في طبقة الخدمة (لا الاعتماد على الواجهة) + رسائل عربية.
- [ ] ذرية كاملة: فشل الطاقة/التحقق = لا خطة جديدة + لا تعديل مصدر (يُغطى بالاختبار 6 و10).
- [ ] أساس الخطة لا يُمحى أبداً (`PlannedQtyKg` + `Original`) — الاختبارات 2 و8 و11.
- [ ] صف تدقيق لكل عملية ناجحة + سبب مسجل.
- [ ] الاختبارات الـ12 خضراء + كامل الحزمة بلا انحدار.
- [ ] سكربت الترقية مُجرَّب على نسخة من قاعدة إنتاج (SQLite وSQL Server).
- [ ] الطباعة تعرض المراجعة/الترحيل عند وجودهما فقط (لا ضجيج للخطط العادية).

---

## 10. الملفات المتأثرة (خلاصة)

| الملف | التغيير |
|---|---|
| `Core/Domain/Entities/Production.cs` | 5 حقول (§1) |
| `Core/Interfaces/Services/IWorkflowServices.cs` | دالتان + `ReallocateItemDto` (§2.2/§2.3) |
| `Application/Services/PlanningService.cs` | حقن `IAuditService` + الدالتان (§2.1/§2.4/§2.5) |
| `Application/Services/PermissionService.cs` | عملية `Rollover` + `EnsureCatalogCompleteness` (§3.2) |
| `Desktop/Views/Screens/PlanningView.*` | زرّان + عمود مراجعة + شارة 🔁 (§4.1) |
| `Desktop/Views/PlanReallocateWindow.cs` | **جديد** (§4.2) |
| `Desktop/Views/PlanningPrintDocument.cs` | سطرا مراجعة/ترحيل (§4.3) |
| `tests/.../PlanRolloverReallocateTests.cs` | **جديد** — 12 اختباراً (§6) |
| سكربت ترقية SQL | **جديد** (§7) |
