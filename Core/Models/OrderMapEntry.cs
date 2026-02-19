using System;

namespace SohoSapIntegrator.Core.Models
{
    /// <summary>
    /// Representa una fila de la tabla dbo.Z_SOHO_OrderMap.
    /// </summary>
    public class OrderMapEntry
    {
        public string ZohoOrderId { get; set; }
        public string InstanceId { get; set; }
        public string PayloadHash { get; set; }
        public string Status { get; set; }
        public int? SapDocEntry { get; set; }
        public int? SapDocNum { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime? ProcessingAt { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Posibles resultados del intento de iniciar el procesamiento de un pedido.
    /// </summary>
    public enum BeginCode
    {
        /// <summary>El procesamiento puede comenzar. Se insertó/actualizó a PROCESSING.</summary>
        Started,
        /// <summary>El pedido ya fue creado con éxito en SAP. No procesar de nuevo.</summary>
        DuplicateCreated,
        /// <summary>El pedido está siendo procesado ahora mismo. Reintentar más tarde.</summary>
        InProgress,
        /// <summary>El mismo ZohoOrderId+InstanceId llegó con un payload diferente (hash distinto).</summary>
        ConflictHash
    }

    /// <summary>
    /// Resultado del intento de inicio de procesamiento (TryBegin).
    /// </summary>
    public class BeginResult
    {
        public BeginCode Code { get; set; }
        public string PayloadHash { get; set; }
        public int? SapDocEntry { get; set; }
        public int? SapDocNum { get; set; }
    }

    /// <summary>
    /// Resultado de la pre-validación de datos contra SAP.
    /// </summary>
    public class PreValidationResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }

        public static PreValidationResult Success()
        {
            return new PreValidationResult { Ok = true, Message = "OK" };
        }

        public static PreValidationResult Fail(string message)
        {
            return new PreValidationResult { Ok = false, Message = message };
        }
    }

    /// <summary>
    /// Resultado de la consulta de estado de una orden.
    /// </summary>
    public class OrderStatusResult
    {
        public bool Found { get; set; }
        public string Status { get; set; }
        public int? SapDocEntry { get; set; }
        public int? SapDocNum { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
