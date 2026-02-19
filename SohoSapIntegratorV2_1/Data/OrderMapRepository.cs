using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using SohoSapIntegrator.Config;
using SohoSapIntegrator.Core.Interfaces;
using SohoSapIntegrator.Core.Models;

namespace SohoSapIntegrator.Data
{
    /// <summary>
    /// V2.1 — Cambios respecto a V2:
    ///   - ResolveWarehouseCode ahora busca en Z_SOHO_AlmacenMap por warehouseId Y nombre
    ///   - ResolveCardCode ahora busca por LicTradNum Y por CardCode directo (VINESA: CardCode = RUC)
    ///   - PreValidate pasa warehouseId al resolver el almacén
    ///   - Almacenes inactivos en Z_SOHO_AlmacenMap dan error claro en lugar de error genérico SAP
    /// </summary>
    public class OrderMapRepository
    {
        private readonly ILogger _log;

        public OrderMapRepository(ILogger log)
        {
            _log = log;
        }

        // ── Conexiones ────────────────────────────────────────────────────────────

        private SqlConnection OpenConnection()
        {
            var conn = new SqlConnection(AppSettings.DefaultConnection);
            conn.Open();
            return conn;
        }

        private SqlConnection OpenSapConnection()
        {
            var builder = new SqlConnectionStringBuilder(AppSettings.DefaultConnection)
            {
                InitialCatalog = AppSettings.SapDatabase
            };
            var conn = new SqlConnection(builder.ConnectionString);
            conn.Open();
            return conn;
        }

        // ── Idempotencia ──────────────────────────────────────────────────────────

        public BeginResult TryBegin(string zohoOrderId, string instanceId, string payloadHash)
        {
            using (var conn = OpenConnection())
            using (var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                try
                {
                    const string selectSql = @"
SELECT TOP 1 Status, PayloadHash, SapDocEntry, SapDocNum
FROM dbo.Z_SOHO_OrderMap WITH (UPDLOCK, HOLDLOCK)
WHERE ZohoOrderId = @zoho AND InstanceId = @inst;";

                    string existingStatus = null;
                    string existingHash   = null;
                    int?   sapDocEntry    = null;
                    int?   sapDocNum      = null;

                    using (var cmd = new SqlCommand(selectSql, conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@zoho", zohoOrderId);
                        cmd.Parameters.AddWithValue("@inst", instanceId);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                existingStatus = r["Status"] as string;
                                existingHash   = r["PayloadHash"] as string;
                                sapDocEntry    = r["SapDocEntry"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["SapDocEntry"]);
                                sapDocNum      = r["SapDocNum"]   == DBNull.Value ? (int?)null : Convert.ToInt32(r["SapDocNum"]);
                            }
                        }
                    }

                    if (existingStatus != null)
                    {
                        if (!string.IsNullOrEmpty(existingHash) &&
                            !string.Equals(existingHash, payloadHash, StringComparison.OrdinalIgnoreCase))
                        {
                            tx.Commit();
                            return new BeginResult { Code = BeginCode.ConflictHash, PayloadHash = existingHash, SapDocEntry = sapDocEntry, SapDocNum = sapDocNum };
                        }
                        if (string.Equals(existingStatus, "CREATED", StringComparison.OrdinalIgnoreCase))
                        {
                            tx.Commit();
                            return new BeginResult { Code = BeginCode.DuplicateCreated, PayloadHash = existingHash ?? payloadHash, SapDocEntry = sapDocEntry, SapDocNum = sapDocNum };
                        }
                        if (string.Equals(existingStatus, "PROCESSING", StringComparison.OrdinalIgnoreCase))
                        {
                            tx.Commit();
                            return new BeginResult { Code = BeginCode.InProgress, PayloadHash = existingHash ?? payloadHash };
                        }

                        // FAILED → reintento
                        const string updateSql = @"
UPDATE dbo.Z_SOHO_OrderMap
SET Status='PROCESSING', PayloadHash=@hash, ProcessingAt=GETDATE(), UpdatedAt=GETDATE(), ErrorMessage=NULL
WHERE ZohoOrderId=@zoho AND InstanceId=@inst;";
                        using (var cmd = new SqlCommand(updateSql, conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@hash", payloadHash);
                            cmd.Parameters.AddWithValue("@zoho", zohoOrderId);
                            cmd.Parameters.AddWithValue("@inst", instanceId);
                            cmd.ExecuteNonQuery();
                        }
                        tx.Commit();
                        return new BeginResult { Code = BeginCode.Started, PayloadHash = payloadHash };
                    }

                    const string insertSql = @"
INSERT INTO dbo.Z_SOHO_OrderMap (ZohoOrderId,InstanceId,PayloadHash,Status,ProcessingAt,CreatedAt,UpdatedAt)
VALUES (@zoho,@inst,@hash,'PROCESSING',GETDATE(),GETDATE(),GETDATE());";
                    using (var cmd = new SqlCommand(insertSql, conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@zoho", zohoOrderId);
                        cmd.Parameters.AddWithValue("@inst", instanceId);
                        cmd.Parameters.AddWithValue("@hash", payloadHash);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    return new BeginResult { Code = BeginCode.Started, PayloadHash = payloadHash };
                }
                catch (Exception ex)
                {
                    try { tx.Rollback(); } catch { }
                    _log.Error("TryBegin error: " + zohoOrderId + "/" + instanceId, ex);
                    throw;
                }
            }
        }

        public void MarkCreated(string zohoOrderId, string instanceId, int docEntry, int docNum)
        {
            const string sql = @"
UPDATE dbo.Z_SOHO_OrderMap
SET Status='CREATED', SapDocEntry=@de, SapDocNum=@dn, UpdatedAt=GETDATE()
WHERE ZohoOrderId=@zoho AND InstanceId=@inst;";
            using (var conn = OpenConnection())
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@de",   docEntry);
                cmd.Parameters.AddWithValue("@dn",   docNum);
                cmd.Parameters.AddWithValue("@zoho", zohoOrderId);
                cmd.Parameters.AddWithValue("@inst", instanceId);
                cmd.ExecuteNonQuery();
            }
        }

        public void MarkFailed(string zohoOrderId, string instanceId, string errorMessage)
        {
            const string sql = @"
UPDATE dbo.Z_SOHO_OrderMap
SET Status='FAILED', ErrorMessage=@err, UpdatedAt=GETDATE()
WHERE ZohoOrderId=@zoho AND InstanceId=@inst;";
            using (var conn = OpenConnection())
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add("@err", SqlDbType.NVarChar, -1).Value = errorMessage ?? "";
                cmd.Parameters.AddWithValue("@zoho", zohoOrderId);
                cmd.Parameters.AddWithValue("@inst", instanceId);
                cmd.ExecuteNonQuery();
            }
        }

        public void SafeMarkFailed(string zohoOrderId, string instanceId, string error)
        {
            try { MarkFailed(zohoOrderId, instanceId, error); }
            catch (Exception ex)
            {
                _log.Error(string.Format("CRITICAL SafeMarkFailed falló {0}/{1}", zohoOrderId, instanceId), ex);
            }
        }

        public OrderStatusResult GetStatus(string zohoOrderId, string instanceId)
        {
            const string sql = @"
SELECT TOP 1 Status, SapDocEntry, SapDocNum, ErrorMessage, UpdatedAt
FROM dbo.Z_SOHO_OrderMap WHERE ZohoOrderId=@zoho AND InstanceId=@inst;";
            using (var conn = OpenConnection())
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@zoho", zohoOrderId);
                cmd.Parameters.AddWithValue("@inst", instanceId);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return new OrderStatusResult { Found = false };
                    return new OrderStatusResult
                    {
                        Found        = true,
                        Status       = r["Status"] as string,
                        SapDocEntry  = r["SapDocEntry"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["SapDocEntry"]),
                        SapDocNum    = r["SapDocNum"]   == DBNull.Value ? (int?)null : Convert.ToInt32(r["SapDocNum"]),
                        ErrorMessage = r["ErrorMessage"] == DBNull.Value ? null : r["ErrorMessage"] as string,
                        UpdatedAt    = r["UpdatedAt"]   == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(r["UpdatedAt"])
                    };
                }
            }
        }

        // ── Resolución de maestros ────────────────────────────────────────────────

        /// <summary>
        /// V2.1 — Resuelve WhsCode SAP desde la tabla Z_SOHO_AlmacenMap.
        ///
        /// Orden de búsqueda:
        ///   1. Por warehouseId numérico (campo más confiable de Zoho)
        ///   2. Por ZohoWarehouseName (fallback si no hay ID)
        ///
        /// Si el almacén está mapeado pero Activo='N' (inactivo en SAP),
        /// devuelve null con mensaje de error específico.
        /// </summary>
        public string ResolveWarehouseCode(string zohoWarehouseName, int? zohoWarehouseId)
        {
            try
            {
                using (var conn = OpenConnection())
                {
                    // Intento 1: por warehouseId numérico
                    if (zohoWarehouseId.HasValue && zohoWarehouseId.Value > 0)
                    {
                        using (var cmd = new SqlCommand(
                            "SELECT TOP 1 SapWhsCode, Activo, SapWhsName " +
                            "FROM dbo.Z_SOHO_AlmacenMap WHERE ZohoWarehouseId=@id", conn))
                        {
                            cmd.Parameters.AddWithValue("@id", zohoWarehouseId.Value);
                            using (var r = cmd.ExecuteReader())
                            {
                                if (r.Read())
                                {
                                    var activo    = r["Activo"].ToString();
                                    var sapCode   = r["SapWhsCode"].ToString();
                                    var sapName   = r["SapWhsName"].ToString();

                                    if (activo != "Y")
                                    {
                                        _log.Warn(string.Format(
                                            "Almacén warehouseId={0} ('{1}') está INACTIVO en SAP ({2}). " +
                                            "SAP rechazará el pedido.",
                                            zohoWarehouseId.Value, zohoWarehouseName ?? "", sapName));
                                        return null;
                                    }

                                    _log.Debug(string.Format(
                                        "Almacén resuelto por warehouseId={0} → SapWhsCode='{1}' ({2})",
                                        zohoWarehouseId.Value, sapCode, sapName));
                                    return sapCode;
                                }
                            }
                        }
                    }

                    // Intento 2: por nombre (ZohoWarehouseName)
                    if (!string.IsNullOrWhiteSpace(zohoWarehouseName))
                    {
                        using (var cmd = new SqlCommand(
                            "SELECT TOP 1 SapWhsCode, Activo, SapWhsName " +
                            "FROM dbo.Z_SOHO_AlmacenMap WHERE ZohoWarehouseName=@nombre", conn))
                        {
                            cmd.Parameters.AddWithValue("@nombre", zohoWarehouseName.Trim());
                            using (var r = cmd.ExecuteReader())
                            {
                                if (r.Read())
                                {
                                    var activo  = r["Activo"].ToString();
                                    var sapCode = r["SapWhsCode"].ToString();
                                    var sapName = r["SapWhsName"].ToString();

                                    if (activo != "Y")
                                    {
                                        _log.Warn(string.Format(
                                            "Almacén '{0}' está INACTIVO en SAP ({1}).",
                                            zohoWarehouseName, sapName));
                                        return null;
                                    }

                                    _log.Debug(string.Format(
                                        "Almacén resuelto por nombre '{0}' → SapWhsCode='{1}' ({2})",
                                        zohoWarehouseName, sapCode, sapName));
                                    return sapCode;
                                }
                            }
                        }
                    }

                    // No encontrado en tabla de mapeo
                    _log.Warn(string.Format(
                        "Almacén NO MAPEADO: warehouseId={0}, warehouseName='{1}'. " +
                        "Agregar registro en dbo.Z_SOHO_AlmacenMap de SohoIntegracion.",
                        zohoWarehouseId.HasValue ? zohoWarehouseId.Value.ToString() : "null",
                        zohoWarehouseName ?? "null"));
                    return null;
                }
            }
            catch (Exception ex)
            {
                _log.Error("ResolveWarehouseCode error", ex);
                return null;
            }
        }

        /// <summary>
        /// V2.1 — Busca CardCode SAP por RUC/cédula.
        ///
        /// Orden de búsqueda (específico para VINESA donde CardCode = RUC):
        ///   1. Por LicTradNum (campo fiscal estándar SAP)
        ///   2. Por CardCode directo (en VINESA son iguales al RUC)
        ///
        /// Solo clientes activos (CardType='C', frozenFor='N').
        /// Devuelve null si no se encuentra — el llamador decide qué hacer.
        /// </summary>
        public string ResolveCardCode(string customerId)
        {
            if (string.IsNullOrWhiteSpace(customerId)) return null;
            var id = customerId.Trim();

            try
            {
                using (var conn = OpenSapConnection())
                {
                    // Intento 1: por LicTradNum (RUC/cédula en campo fiscal)
                    using (var cmd = new SqlCommand(
                        string.Format(
                            "SELECT TOP 1 CardCode FROM {0}..OCRD " +
                            "WHERE LicTradNum=@id AND CardType='C' AND frozenFor='N'",
                            AppSettings.SapDatabase), conn))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        var r = cmd.ExecuteScalar();
                        if (r != null && r != DBNull.Value)
                        {
                            _log.Debug(string.Format(
                                "Cliente resuelto por LicTradNum='{0}' → CardCode='{1}'", id, r));
                            return r.ToString();
                        }
                    }

                    // Intento 2: por CardCode directo (VINESA: CardCode = RUC)
                    using (var cmd = new SqlCommand(
                        string.Format(
                            "SELECT TOP 1 CardCode FROM {0}..OCRD " +
                            "WHERE CardCode=@id AND CardType='C' AND frozenFor='N'",
                            AppSettings.SapDatabase), conn))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        var r = cmd.ExecuteScalar();
                        if (r != null && r != DBNull.Value)
                        {
                            _log.Debug(string.Format(
                                "Cliente resuelto por CardCode directo='{0}'", r));
                            return r.ToString();
                        }
                    }

                    return null;
                }
            }
            catch (Exception ex)
            {
                _log.Error("ResolveCardCode error: " + id, ex);
                return null;
            }
        }

        // ── Pre-validación completa V2.1 ──────────────────────────────────────────

        /// <summary>
        /// V2.1 — Pre-validación que usa la tabla de mapeo para almacenes
        /// y búsqueda doble para clientes.
        ///
        /// Resuelve y valida en orden:
        ///   1. Cliente → por LicTradNum o CardCode; si no existe usa default + WARN
        ///   2. Vendedor → por sellerId del payload; si no existe usa default + WARN
        ///   3. Almacén → por warehouseId/nombre via Z_SOHO_AlmacenMap; si no existe FALLA
        ///   4. Artículos → validFor='Y' y SellItem='Y' en OITM; si falta FALLA
        ///   5. Artículo+Almacén → verifica OITW; si falta WARN (SAP decide)
        ///   6. Descuento 100% → WARN por línea
        /// </summary>
        public PreValidationResult PreValidate(SohoEnvelope env, out ResolvedOrderData resolved)
        {
            resolved = new ResolvedOrderData();
            var t     = env.BusinessObject.Transaction;
            var sapDb = AppSettings.SapDatabase;

            if (string.IsNullOrWhiteSpace(sapDb))
                return PreValidationResult.Fail("SapDi.SapDatabase no configurado en App.config.");

            if (t == null || t.SaleItemList == null || t.SaleItemList.Count == 0)
                return PreValidationResult.Fail("SaleItemList está vacío o no existe en el payload.");

            try
            {
                using (var sapConn = OpenSapConnection())
                {
                    // ── 1. CLIENTE ────────────────────────────────────────────────
                    resolved.CardCode        = AppSettings.DefaultCardCode;
                    resolved.ClienteResuelto = false;

                    if (t.Customer != null && !string.IsNullOrWhiteSpace(t.Customer.CustomerId))
                    {
                        var cardCode = ResolveCardCode(t.Customer.CustomerId);
                        if (cardCode != null)
                        {
                            resolved.CardCode        = cardCode;
                            resolved.ClienteResuelto = true;
                            _log.Info(string.Format(
                                "CLIENTE OK: RUC/Cédula='{0}' → CardCode='{1}' | {2}",
                                t.Customer.CustomerId, cardCode, t.Customer.Name ?? ""));
                        }
                        else
                        {
                            _log.Warn(string.Format(
                                "CLIENTE NO ENCONTRADO: RUC/Cédula='{0}' ({1}) no existe en OCRD. " +
                                "Se usará cliente default='{2}'.",
                                t.Customer.CustomerId,
                                t.Customer.Name ?? "sin nombre",
                                AppSettings.DefaultCardCode));
                        }
                    }

                    // Verificar que el CardCode que se usará no esté bloqueado
                    if (!Exists(sapConn,
                        string.Format("SELECT 1 FROM {0}..OCRD WHERE CardCode=@p AND frozenFor='N'", sapDb),
                        new SqlParameter("@p", resolved.CardCode)))
                        return PreValidationResult.Fail(string.Format(
                            "CardCode '{0}' no existe o está bloqueado en OCRD.", resolved.CardCode));

                    // ── 2. VENDEDOR ───────────────────────────────────────────────
                    resolved.SlpCode = AppSettings.DefaultSlpCode;

                    if (t.SellerId.HasValue && t.SellerId.Value > 0)
                    {
                        if (Exists(sapConn,
                            string.Format("SELECT 1 FROM {0}..OSLP WHERE SlpCode=@p", sapDb),
                            new SqlParameter("@p", t.SellerId.Value)))
                        {
                            resolved.SlpCode = t.SellerId.Value;
                            _log.Debug(string.Format("VENDEDOR OK: SlpCode={0}", resolved.SlpCode));
                        }
                        else
                        {
                            _log.Warn(string.Format(
                                "VENDEDOR NO ENCONTRADO: SlpCode={0} no existe en OSLP. " +
                                "Se usará vendedor default={1}.",
                                t.SellerId.Value, AppSettings.DefaultSlpCode));
                        }
                    }

                    // ── 3. ALMACÉN (via tabla de mapeo) ───────────────────────────
                    resolved.WarehouseCode   = AppSettings.DefaultWarehouseCode;
                    resolved.AlmacenResuelto = false;

                    if (!string.IsNullOrWhiteSpace(t.WarehouseCode) || t.WarehouseId.HasValue)
                    {
                        var whsCode = ResolveWarehouseCode(t.WarehouseCode, t.WarehouseId);

                        if (whsCode != null)
                        {
                            // Verificar que el WhsCode resuelto exista y esté activo en SAP
                            if (!Exists(sapConn,
                                string.Format("SELECT 1 FROM {0}..OWHS WHERE WhsCode=@p AND Inactive='N'", sapDb),
                                new SqlParameter("@p", whsCode)))
                                return PreValidationResult.Fail(string.Format(
                                    "El almacén resuelto '{0}' no existe o está inactivo en OWHS de SAP. " +
                                    "Verificar la tabla Z_SOHO_AlmacenMap.",
                                    whsCode));

                            resolved.WarehouseCode   = whsCode;
                            resolved.AlmacenResuelto = true;
                        }
                        else
                        {
                            // No encontrado en tabla de mapeo → FALLA con instrucción clara
                            return PreValidationResult.Fail(string.Format(
                                "Almacén de Zoho NO MAPEADO: warehouseId={0}, warehouseCode='{1}'. " +
                                "Agregar el mapeo en la tabla dbo.Z_SOHO_AlmacenMap de SohoIntegracion: " +
                                "INSERT INTO Z_SOHO_AlmacenMap (ZohoWarehouseId, ZohoWarehouseName, SapWhsCode, SapWhsName) " +
                                "VALUES ({0}, '{1}', 'CODIGO_SAP', 'NOMBRE_SAP');",
                                t.WarehouseId.HasValue ? t.WarehouseId.Value.ToString() : "null",
                                t.WarehouseCode ?? "null"));
                        }
                    }
                    else
                    {
                        // Zoho no mandó almacén → verificar que el default exista
                        if (!Exists(sapConn,
                            string.Format("SELECT 1 FROM {0}..OWHS WHERE WhsCode=@p AND Inactive='N'", sapDb),
                            new SqlParameter("@p", resolved.WarehouseCode)))
                            return PreValidationResult.Fail(string.Format(
                                "Almacén default '{0}' no existe en OWHS.", resolved.WarehouseCode));

                        _log.Debug(string.Format(
                            "Zoho no mandó almacén. Usando default='{0}'.", resolved.WarehouseCode));
                    }

                    // ── 4. ARTÍCULOS en OITM ─────────────────────────────────────
                    var productIds = new List<string>();
                    var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var it in t.SaleItemList)
                        if (!string.IsNullOrWhiteSpace(it.ProductId) && seen.Add(it.ProductId.Trim()))
                            productIds.Add(it.ProductId.Trim());

                    if (productIds.Count == 0)
                        return PreValidationResult.Fail("Ningún ítem tiene ProductId válido.");

                    var pParams = new string[productIds.Count];
                    for (int i = 0; i < productIds.Count; i++) pParams[i] = "@p" + i;

                    var foundItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var cmd = new SqlCommand(
                        string.Format(
                            "SELECT ItemCode FROM {0}..OITM " +
                            "WHERE ItemCode IN ({1}) AND validFor='Y' AND SellItem='Y'",
                            sapDb, string.Join(",", pParams)), sapConn))
                    {
                        for (int i = 0; i < productIds.Count; i++)
                            cmd.Parameters.AddWithValue(pParams[i], productIds[i]);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read()) foundItems.Add(r.GetString(0));
                    }

                    var missingItems = new List<string>();
                    foreach (var pid in productIds)
                        if (!foundItems.Contains(pid)) missingItems.Add(pid);

                    if (missingItems.Count > 0)
                        return PreValidationResult.Fail(string.Format(
                            "Artículos no encontrados o inactivos en OITM: {0}",
                            string.Join(", ", missingItems)));

                    _log.Debug(string.Format(
                        "ARTÍCULOS OK: {0} artículo(s) validados en OITM.", productIds.Count));

                    // ── 5. ARTÍCULOS vs ALMACÉN en OITW ──────────────────────────
                    var wParams = new string[productIds.Count];
                    for (int i = 0; i < productIds.Count; i++) wParams[i] = "@w" + i;

                    var foundInWhs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var cmd = new SqlCommand(
                        string.Format(
                            "SELECT ItemCode FROM {0}..OITW " +
                            "WHERE ItemCode IN ({1}) AND WhsCode=@whs",
                            sapDb, string.Join(",", wParams)), sapConn))
                    {
                        for (int i = 0; i < productIds.Count; i++)
                            cmd.Parameters.AddWithValue(wParams[i], productIds[i]);
                        cmd.Parameters.AddWithValue("@whs", resolved.WarehouseCode);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read()) foundInWhs.Add(r.GetString(0));
                    }

                    var sinAlmacen = new List<string>();
                    foreach (var pid in productIds)
                        if (!foundInWhs.Contains(pid)) sinAlmacen.Add(pid);

                    if (sinAlmacen.Count > 0)
                    {
                        resolved.ArticulosSinAlmacen = sinAlmacen;
                        _log.Warn(string.Format(
                            "ADVERTENCIA OITW: {0} artículo(s) sin registro en almacén '{1}': {2}. " +
                            "SAP podría rechazar el pedido.",
                            sinAlmacen.Count, resolved.WarehouseCode, string.Join(", ", sinAlmacen)));
                    }

                    // ── 6. DESCUENTO 100% ─────────────────────────────────────────
                    foreach (var it in t.SaleItemList)
                        if (it.Discount >= 100)
                            _log.Warn(string.Format(
                                "DESCUENTO 100%%: ProductId='{0}' Qty={1} Price={2}. " +
                                "SAP puede rechazar líneas con precio resultante cero.",
                                it.ProductId, it.Quantity, it.Price));
                }
            }
            catch (Exception ex)
            {
                return PreValidationResult.Fail(
                    "Error de conexión en pre-validación: " + ex.Message);
            }

            _log.Info(string.Format(
                "PRE-VALIDACIÓN OK: Cliente='{0}'({1}) Vendedor={2} Almacén='{3}'",
                resolved.CardCode,
                resolved.ClienteResuelto ? "real" : "default",
                resolved.SlpCode,
                resolved.WarehouseCode));

            return PreValidationResult.Success();
        }

        // ── Helper ────────────────────────────────────────────────────────────────

        private static bool Exists(SqlConnection conn, string sql, SqlParameter param)
        {
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.Add(param);
                var r = cmd.ExecuteScalar();
                return r != null && r != DBNull.Value;
            }
        }
    }

    // ── Clases auxiliares ─────────────────────────────────────────────────────────

    public class ResolvedOrderData
    {
        public string       CardCode            { get; set; }
        public bool         ClienteResuelto     { get; set; }
        public int          SlpCode             { get; set; }
        public string       WarehouseCode       { get; set; }
        public bool         AlmacenResuelto     { get; set; }
        public List<string> ArticulosSinAlmacen { get; set; }

        public ResolvedOrderData()
        {
            ArticulosSinAlmacen = new List<string>();
        }
    }
}
