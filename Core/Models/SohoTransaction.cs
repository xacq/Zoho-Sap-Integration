using System.Collections.Generic;

namespace SohoSapIntegrator.Core.Models
{
    /// <summary>
    /// Detalles de la transacción de venta (cabecera + líneas).
    /// </summary>
    public class SohoTransaction
    {
        /// <summary>Fecha del pedido (ej: "2026-02-18" o "2026-02-18T10:30:00").</summary>
        public string Date { get; set; }

        /// <summary>Código de almacén (opcional; si vacío se usa el default configurado).</summary>
        public string WarehouseCode { get; set; }

        /// <summary>ID del vendedor (opcional; si null se usa el default configurado).</summary>
        public int? SellerId { get; set; }

        public int? WarehouseId { get; set; }

        public SohoCustomer Customer { get; set; }

        public List<SohoSaleItem> SaleItemList { get; set; }

        // Totales informacionales (SAP los recalcula con sus propias reglas)
        public decimal ExtraExpense { get; set; }
        public decimal Subtotal { get; set; }
        public decimal IVA { get; set; }
        public decimal Total { get; set; }

        public SohoTransaction()
        {
            Date = "";
            WarehouseCode = "";
            Customer = new SohoCustomer();
            SaleItemList = new List<SohoSaleItem>();
        }
    }

    /// <summary>
    /// Datos del cliente en el payload de Soho.
    /// En la lógica actual se usa un cliente default de SAP; esta info
    /// se guarda para trazabilidad en el campo NumAtCard del pedido SAP.
    /// </summary>
    public class SohoCustomer
    {
        public string CustomerId { get; set; }
        public string Name { get; set; }
        public string Phone { get; set; }
    }

    /// <summary>
    /// Una línea del pedido de venta.
    /// </summary>
    public class SohoSaleItem
    {
        /// <summary>Código de artículo (ItemCode en SAP). Obligatorio.</summary>
        public string ProductId { get; set; }

        /// <summary>Cantidad. Debe ser mayor que 0.</summary>
        public decimal Quantity { get; set; }

        /// <summary>Precio unitario.</summary>
        public decimal Price { get; set; }

        /// <summary>Porcentaje de descuento (0-100).</summary>
        public decimal Discount { get; set; }

        /// <summary>Total de la línea (informacional; SAP recalcula).</summary>
        public decimal Total { get; set; }

        /// <summary>
        /// Almacén específico para esta línea (opcional).
        /// Si viene, sobreescribe el warehouseCode de la cabecera para esta línea.
        /// Preparado para cuando Zoho envíe almacén por línea.
        /// </summary>
        public string WarehouseCode { get; set; }
    }
}
