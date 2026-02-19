using SohoSapIntegrator.Core.Models;
using SohoSapIntegrator.Data;

namespace SohoSapIntegrator.Core.Interfaces
{
    public interface ISapDiService
    {
        /// <summary>Crea pedido usando datos ya resueltos por la pre-validación.</summary>
        SapOrderResult CreateSalesOrder(SohoEnvelope envelope, ResolvedOrderData resolved);

        /// <summary>Crea pedido usando solo los defaults del config (compatibilidad).</summary>
        SapOrderResult CreateSalesOrder(SohoEnvelope envelope);
    }

    public sealed class SapOrderResult
    {
        public int DocEntry { get; set; }
        public int DocNum   { get; set; }
    }
}
