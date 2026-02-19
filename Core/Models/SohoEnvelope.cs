using System.Collections.Generic;

namespace SohoSapIntegrator.Core.Models
{
    /// <summary>
    /// Contenedor principal de un pedido proveniente de Soho/Zoho.
    /// La API espera un array JSON de estos objetos en el body del POST.
    /// 
    /// Ejemplo de payload:
    /// [
    ///   {
    ///     "zohoOrderId": "SO-00123",
    ///     "instanceId": "inst-001",
    ///     "businessObject": {
    ///       "Transaction": {
    ///         "date": "2026-02-18",
    ///         "Customer": { "CustomerId": "CLI001", "Name": "Juan Pérez" },
    ///         "SaleItemList": [
    ///           { "ProductId": "ART001", "Quantity": 2, "Price": 100.00, "Discount": 0 }
    ///         ]
    ///       }
    ///     }
    ///   }
    /// ]
    /// </summary>
    public class SohoEnvelope
    {
        /// <summary>ID único del pedido en Soho/Zoho. Parte de la clave de idempotencia.</summary>
        public string ZohoOrderId { get; set; }

        /// <summary>ID de instancia/envío. Junto con ZohoOrderId forma la clave única.</summary>
        public string InstanceId { get; set; }

        /// <summary>Objeto de negocio que contiene los datos del pedido.</summary>
        public SohoBusinessObject BusinessObject { get; set; }

        public SohoEnvelope()
        {
            ZohoOrderId = "";
            InstanceId = "";
            BusinessObject = new SohoBusinessObject();
        }
    }

    public class SohoBusinessObject
    {
        public SohoTransaction Transaction { get; set; }

        public SohoBusinessObject()
        {
            Transaction = new SohoTransaction();
        }
    }
}
