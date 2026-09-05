using DatesErp.Core.Common;

namespace DatesErp.Core.Domain.Entities;

/// <summary>§7 — استلام التمور (شحنة واردة من عميل/مورد).</summary>
public class Shipment : WorkflowDocument
{
    public int CustomerId { get; set; }
    public string ContainerNumber { get; set; }
    public string VesselName { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public int? ReceivedBy { get; set; }
    public double TotalWeightKg { get; set; }
    public int TotalCartons { get; set; }
    public int ItemCount { get; set; }
    /// <summary>§استلام جزئي: سند لاحق يكمل بنوداً علّقت في سند سابق.</summary>
    public int? ParentShipmentId { get; set; }
    /// <summary>§المخازن المتعددة: مخزن الاستلام الفعلي الذي وصلت إليه الحاوية — الاعتماد يقيّد الوارد فيه.
    /// فارغ = مخزن الخام الافتراضي WRM (توافق مع البيانات القديمة).</summary>
    public int? ReceivingWarehouseId { get; set; }

    public List<ShipmentItem> Items { get; set; } = new();
    public List<Lot> Lots { get; set; } = new();
}

public class ShipmentItem : BaseEntity
{
    public int ShipmentId { get; set; }
    public int ProductId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int PackageCount { get; set; }
    public double UnitWeightKg { get; set; }
    public double TotalWeightKg { get; set; }
    public string Status { get; set; } = DocStatuses.Draft;
    /// <summary>§نظام الوحدات: وحدة الاستلام الأصلية كما وصلت فعلياً (كرتون/سلة/كجم...) — لا تُفقد أبداً،
    /// والكمية القياسية للمخزون الخام هي الكيلو (TotalWeightKg).</summary>
    public string ReceiptUnit { get; set; }
}

/// <summary>§7 — الدفعة (Lot) الناتجة عن اعتماد الاستلام — أساس التتبع الكامل.</summary>
public class Lot : AuditableEntity
{
    public string LotCode { get; set; }
    public int? ShipmentId { get; set; }
    public int? ShipmentItemId { get; set; }
    public int ProductId { get; set; }
    public int? CustomerId { get; set; }
    public int? PackagingTypeId { get; set; }
    public DateTime? LotDate { get; set; }
    public double InitialQtyKg { get; set; }
    public double ProducedQtyKg { get; set; }
    public double InStockQtyKg { get; set; }
    public double DeliveredQtyKg { get; set; }
    public double WastageQtyKg { get; set; }
    public string Status { get; set; } = DocStatuses.Approved;

    /// <summary>المتاح للتخطيط = المخزون غير المحجوز لخطط نشطة.</summary>
    public double ReservedQtyKg { get; set; }
    public double AvailableQtyKg => Math.Max(0, InStockQtyKg - ReservedQtyKg);
}
