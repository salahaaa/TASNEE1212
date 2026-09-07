-- ═══════════════════════════════════════════════════════════════════════════════
--  DateERP — تصحيح أرصدة مخزن التام التي فقدت هوية العميل
--  السبب: FinishedGoodsService كان يترك effCust = null في «المسار المباشر»
--          (استلام غير مربوط بأمر تسليم)، فتُكتب StockBalances.CustomerId = NULL.
--          ونتيجتها: شاشة «التسليم للعميل» تبحث بـ CustomerId = العميل فلا تجد شيئاً
--          ← رصيد صفر، لا أصناف، لا كميات.
--
--  إصلاح الكود يمنع تكرارها مستقبلاً، لكنه لا يُصلح ما سُجِّل سابقاً — وهذا دور هذا الملف.
--
--  ⚠️  خذ نسخة احتياطية كاملة من قاعدة البيانات قبل تشغيل القسم 3.
--  ⚠️  شغّل القسمين 1 و2 أولاً (قراءة فقط) وراجع النتائج، ثم القسم 3.
--  المنصة: SQL Server. للنسخة المحلية SQLite انظر ملاحظة النهاية.
-- ═══════════════════════════════════════════════════════════════════════════════

-- ───────────────────────────────────────────────────────────────────────────────
-- القسم 1 — تشخيص: كم صفاً فقد هويته؟ (قراءة فقط — آمن تماماً)
-- ───────────────────────────────────────────────────────────────────────────────
SELECT
    COUNT(*)                AS [عدد الأرصدة بلا عميل],
    SUM(sb.QtyKg)           AS [إجمالي الكيلو المعلّق],
    SUM(sb.PackageCount)    AS [إجمالي الكراتين المعلّقة]
FROM StockBalances sb
JOIN Warehouses w ON w.Id = sb.WarehouseId
WHERE w.WarehouseCode = 'WFG'
  AND sb.CustomerId IS NULL
  AND (sb.QtyKg > 0 OR sb.PackageCount > 0);
GO

-- ───────────────────────────────────────────────────────────────────────────────
-- القسم 2 — استنتاج العميل لكل صف (قراءة فقط)
--   المسار: الرصيد ← بند سند استلام التام (نفس الصنف والدفعة)
--           ← السند ← أمر الإنتاج ← ملكية سطر الأمر ← عميل الأمر
--   وهو نفس الاشتقاق الذي طبّقه إصلاح الكود، فالنتيجة متسقة.
-- ───────────────────────────────────────────────────────────────────────────────
WITH Orphan AS (
    SELECT sb.Id, sb.WarehouseId, sb.ProductId, sb.LotId, sb.PackagingTypeId,
           sb.QtyKg, sb.PackageCount
    FROM StockBalances sb
    JOIN Warehouses w ON w.Id = sb.WarehouseId
    WHERE w.WarehouseCode = 'WFG'
      AND sb.CustomerId IS NULL
      AND (sb.QtyKg > 0 OR sb.PackageCount > 0)
),
Guess AS (
    SELECT o.*,
           (SELECT TOP 1 COALESCE(poi.CustomerId, po.CustomerId)
              FROM FinishedGoodsReceiptItems fri
              JOIN FinishedGoodsReceipts fr ON fr.Id = fri.ReceiptId
              JOIN ProductionOrders po      ON po.Id = fr.OrderId
              LEFT JOIN ProductionOrderItems poi
                     ON poi.OrderId = po.Id AND poi.ProductId = fri.ProductId
             WHERE fri.ProductId = o.ProductId
               AND (fri.LotId = o.LotId OR (fri.LotId IS NULL AND o.LotId IS NULL))
               AND COALESCE(poi.CustomerId, po.CustomerId) IS NOT NULL
             ORDER BY fri.Id DESC) AS GuessCustomerId
    FROM Orphan o
)
SELECT g.Id            AS [رقم صف الرصيد],
       p.ProductNameAr AS [الصنف],
       l.LotCode       AS [الدفعة],
       g.QtyKg         AS [الكيلو],
       g.PackageCount  AS [الكراتين],
       g.GuessCustomerId,
       c.CustomerName  AS [العميل المستنتج],
       CASE WHEN g.GuessCustomerId IS NULL
            THEN N'⛔ تعذّر الاستنتاج — يحتاج إسناداً يدوياً'
            ELSE N'✅ جاهز للتصحيح' END AS [الحالة]
FROM Guess g
LEFT JOIN Products  p ON p.Id = g.ProductId
LEFT JOIN Lots      l ON l.Id = g.LotId
LEFT JOIN Customers c ON c.Id = g.GuessCustomerId
ORDER BY [الحالة], p.ProductNameAr;
GO

-- ───────────────────────────────────────────────────────────────────────────────
-- القسم 3 — التصحيح الفعلي  ⚠️ يعدّل البيانات — خذ نسخة احتياطية أولاً ⚠️
--   يعالج حالتين:
--     (أ) لا يوجد صف مطابق للعميل  → يُحدَّث الصف اليتيم بهويته.
--     (ب) يوجد صف مطابق للعميل     → تُدمج الكميات فيه ثم يُصفَّر اليتيم.
--   الصفوف التي تعذّر استنتاج عميلها تُترك كما هي (لا تُمَس).
-- ───────────────────────────────────────────────────────────────────────────────
BEGIN TRANSACTION;

-- جدول مؤقت بالاستنتاجات القابلة للتطبيق فقط
SELECT sb.Id, sb.WarehouseId, sb.ProductId, sb.LotId, sb.PackagingTypeId,
       sb.QtyKg, sb.PackageCount,
       (SELECT TOP 1 COALESCE(poi.CustomerId, po.CustomerId)
          FROM FinishedGoodsReceiptItems fri
          JOIN FinishedGoodsReceipts fr ON fr.Id = fri.ReceiptId
          JOIN ProductionOrders po      ON po.Id = fr.OrderId
          LEFT JOIN ProductionOrderItems poi
                 ON poi.OrderId = po.Id AND poi.ProductId = fri.ProductId
         WHERE fri.ProductId = sb.ProductId
           AND (fri.LotId = sb.LotId OR (fri.LotId IS NULL AND sb.LotId IS NULL))
           AND COALESCE(poi.CustomerId, po.CustomerId) IS NOT NULL
         ORDER BY fri.Id DESC) AS CustId
INTO #Fix
FROM StockBalances sb
JOIN Warehouses w ON w.Id = sb.WarehouseId
WHERE w.WarehouseCode = 'WFG'
  AND sb.CustomerId IS NULL
  AND (sb.QtyKg > 0 OR sb.PackageCount > 0);

DELETE FROM #Fix WHERE CustId IS NULL;   -- لا نلمس ما تعذّر استنتاجه

-- (ب) دمج الكميات في الصف القائم للعميل إن وُجد
UPDATE tgt
   SET tgt.QtyKg        = tgt.QtyKg + f.QtyKg,
       tgt.PackageCount = tgt.PackageCount + f.PackageCount
FROM StockBalances tgt
JOIN #Fix f
  ON  tgt.WarehouseId = f.WarehouseId
  AND tgt.CustomerId  = f.CustId
  AND ISNULL(tgt.ProductId,-1)       = ISNULL(f.ProductId,-1)
  AND ISNULL(tgt.LotId,-1)           = ISNULL(f.LotId,-1)
  AND ISNULL(tgt.PackagingTypeId,-1) = ISNULL(f.PackagingTypeId,-1)
  AND tgt.Id <> f.Id;

-- تصفير اليتيم الذي دُمج
UPDATE sb
   SET sb.QtyKg = 0, sb.PackageCount = 0
FROM StockBalances sb
JOIN #Fix f ON f.Id = sb.Id
WHERE EXISTS (
    SELECT 1 FROM StockBalances t
     WHERE t.WarehouseId = f.WarehouseId
       AND t.CustomerId  = f.CustId
       AND ISNULL(t.ProductId,-1)       = ISNULL(f.ProductId,-1)
       AND ISNULL(t.LotId,-1)           = ISNULL(f.LotId,-1)
       AND ISNULL(t.PackagingTypeId,-1) = ISNULL(f.PackagingTypeId,-1)
       AND t.Id <> f.Id);

-- (أ) إسناد الهوية مباشرة لمن لا صفَّ مقابلاً له
UPDATE sb
   SET sb.CustomerId = f.CustId
FROM StockBalances sb
JOIN #Fix f ON f.Id = sb.Id
WHERE sb.CustomerId IS NULL
  AND NOT EXISTS (
    SELECT 1 FROM StockBalances t
     WHERE t.WarehouseId = f.WarehouseId
       AND t.CustomerId  = f.CustId
       AND ISNULL(t.ProductId,-1)       = ISNULL(f.ProductId,-1)
       AND ISNULL(t.LotId,-1)           = ISNULL(f.LotId,-1)
       AND ISNULL(t.PackagingTypeId,-1) = ISNULL(f.PackagingTypeId,-1)
       AND t.Id <> f.Id);

SELECT COUNT(*) AS [صفوف عولجت] FROM #Fix;
DROP TABLE #Fix;

-- ✋ راجع الأرقام أعلاه. إن كانت سليمة نفّذ COMMIT، وإلا ROLLBACK.
-- COMMIT TRANSACTION;
-- ROLLBACK TRANSACTION;
GO

-- ───────────────────────────────────────────────────────────────────────────────
-- القسم 4 — التحقق بعد التصحيح (قراءة فقط): يجب أن يعود صفراً
-- ───────────────────────────────────────────────────────────────────────────────
-- SELECT COUNT(*) AS [متبقٍ بلا عميل]
-- FROM StockBalances sb JOIN Warehouses w ON w.Id = sb.WarehouseId
-- WHERE w.WarehouseCode = 'WFG' AND sb.CustomerId IS NULL
--   AND (sb.QtyKg > 0 OR sb.PackageCount > 0);

-- ═══════════════════════════════════════════════════════════════════════════════
-- ملاحظة SQLite (وضع الجهاز المفرد): احذف كل أسطر GO، واستبدل
--   SELECT ... INTO #Fix   بـ   CREATE TEMP TABLE Fix AS SELECT ...
--   وصيغة UPDATE..FROM..JOIN بصيغة UPDATE ... WHERE Id IN (SELECT ...).
-- ═══════════════════════════════════════════════════════════════════════════════
