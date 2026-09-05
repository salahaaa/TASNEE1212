# سير عمل CI

الملف `ci.yml` في هذا المجلد هو تعريف سير عمل GitHub Actions للمشروع
(بناء Release + الاختبارات + سقف التحذيرات + الفحوصات البنيوية).

**لتفعيله:** انقله يدوياً إلى `.github/workflows/ci.yml` عبر واجهة GitHub
أو من جهازك المحلي:

```bash
mkdir -p .github/workflows && git mv .github/ci.yml .github/workflows/ci.yml
git commit -m "تفعيل سير عمل CI" && git push
```

> سبب وضعه هنا: وكيل Arena لا يملك صلاحية `workflows` فيرفض GitHub دفع أي
> تعديل داخل `.github/workflows/`. النقل يتطلب خطوة يدوية واحدة من مالك المستودع.
