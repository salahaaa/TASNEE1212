# تفعيل خط التكامل المستمر (CI) — خطوة واحدة مطلوبة منك

## لماذا هذا الملف هنا وليس في مكانه؟

صلاحيات التطبيق الذي أعمل به **لا تسمح بإنشاء أو تعديل ملفات `.github/workflows/`** (قيد أمني في GitHub). لذلك وُضعت النسخة المصححة هنا، وتحتاج منك **نقلة واحدة**.

## ما عليك فعله

```bash
mkdir -p .github/workflows
git mv tools/ci/ci.yml .github/workflows/ci.yml
git rm .github/ci.yml          # النسخة المعطوبة في المسار الخاطئ
git commit -m "تفعيل CI: نقل ci.yml إلى المسار الصحيح"
git push
```

أو من متصفح GitHub: **Add file ← Create new file** باسم `.github/workflows/ci.yml` والصق محتوى `tools/ci/ci.yml`.

---

## العيوب الثلاثة التي أُصلحت في هذه النسخة

`ci.yml` مكتوب منذ B83 لكنه **لم يُشغَّل ولا مرة** — وثلاثة عيوب بنيوية كانت ستمنعه من العمل حتى لو وُضع في مستودع صحيح:

### ١) المسار خاطئ
حزمة B105 وضعته في **`.github/ci.yml`** بدل `.github/workflows/ci.yml`.
GitHub **لا يقرأ** إلا ما كان داخل `workflows/` — فلا وظيفة تُنشأ أصلاً.
*(في كودي B101 كان المسار صحيحاً — التسطيح حدث في تغليف B105.)*

### ٢) بناء WPF على لينكس — فشل حتمي
```yaml
build-and-test:
  runs-on: ubuntu-latest      # ❌
```
بينما:
```xml
<TargetFramework>net8.0-windows</TargetFramework>
<UseWPF>true</UseWPF>
```
**لا يمكن بناء WPF على لينكس.** أول أمر `dotnet build DateERP.sln` كان سيسقط فوراً.

**الإصلاح:** `runs-on: windows-latest` (يطابق منصة النشر أيضاً) + `shell: bash` للخطوات النصية لأن الصدفة الافتراضية على ويندوز هي PowerShell.

### ٣) خطوة القبول تستدعي `--no-build` في وظيفة لم تبنِ شيئاً
كانت داخل `structural-checks` التي **لا تُثبّت .NET SDK ولا تبني** ثم:
```yaml
dotnet run --project tools/AcceptanceRunner/... --no-build   # ❌
```
**الإصلاح:** أُضيف `setup-dotnet` وحُذف `--no-build`.
`AcceptanceRunner` هدفه `net8.0` ويعتمد على `Application`/`Infrastructure` فقط (لا `Desktop`) — فيبقى على لينكس بلا مشكلة.

---

## ماذا يحدث بعد التفعيل

أول تشغيل حقيقي لـCI في تاريخ المشروع — **وأول ترجمة فعلية للكود منذ B83**.

| الوظيفة | المنصة | ما تفعله |
|---|---|---|
| `build-and-test` | windows-latest | بناء Release · حارس التحذيرات (حد 700) · تشغيل الاختبارات · رفع النتائج |
| `structural-checks` | ubuntu-latest | لا أزرار ميتة · فحص RTL · حارس حقول `double` (حد 103) · سيناريوهات القبول |

**توقّع أن يفشل أول تشغيل** — وهذا هو المطلوب بالضبط: سيكشف أخطاء الترجمة المتراكمة منذ B84 دفعةً واحدة، بنصوص دقيقة. أرسلها لي وأعالجها.
