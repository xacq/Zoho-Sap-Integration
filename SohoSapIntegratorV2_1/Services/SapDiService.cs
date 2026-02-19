using System;
using System.Collections.Generic;
using SohoSapIntegrator.Config;
using SohoSapIntegrator.Core.Interfaces;
using SohoSapIntegrator.Core.Models;
using SohoSapIntegrator.Data;

namespace SohoSapIntegrator.Services
{
    /// <summary>
    /// V2 — Crea pedidos de venta en SAP Business One vía DI API.
    ///
    /// Cambios respecto a V1:
    ///   - Recibe ResolvedOrderData con cliente, vendedor y almacén ya resueltos
    ///   - Soporta almacén por línea (WarehouseCode en SohoSaleItem)
    ///   - Aplica sellerId real del payload cuando fue validado
    ///   - Aplica CardCode real del cliente cuando fue encontrado por RUC
    ///   - Log detallado de cada campo mapeado para facilitar diagnóstico
    /// </summary>
    public class SapDiService : ISapDiService
    {
        private readonly ILogger _log;

        public SapDiService(ILogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Crea el pedido en SAP usando los datos ya resueltos por la pre-validación.
        /// </summary>
        public SapOrderResult CreateSalesOrder(SohoEnvelope envelope, ResolvedOrderData resolved)
        {
            dynamic company = CreateSapCompany();

            try
            {
                // ── Conectar a SAP ──────────────────────────────────────────────
                company.Server        = AppSettings.SapServer;
                company.CompanyDB     = AppSettings.SapCompanyDb;
                company.LicenseServer = AppSettings.SapLicenseServer;
                company.DbUserName    = AppSettings.SapDbUser;
                company.DbPassword    = AppSettings.SapDbPassword;
                company.UserName      = AppSettings.SapUserName;
                company.Password      = AppSettings.SapPassword;
                company.UseTrusted    = false;
                company.language      = 25; // ln_Spanish
                company.DbServerType  = ParseDbServerType(company, AppSettings.SapDbServerType);

                var rc = (int)company.Connect();
                if (rc != 0)
                {
                    int errCode; string errMsg;
                    company.GetLastError(out errCode, out errMsg);
                    throw new InvalidOperationException(string.Format(
                        "Fallo al conectar SAP DI API: [{0}] {1}", errCode, errMsg));
                }

                _log.Debug(string.Format(
                    "SAP DI conectado → Empresa='{0}' | Cliente='{1}' ({2}) | Vendedor={3} | Almacén='{4}'",
                    AppSettings.SapCompanyDb,
                    resolved.CardCode,
                    resolved.ClienteResuelto ? "encontrado por RUC" : "default",
                    resolved.SlpCode,
                    resolved.WarehouseCode));

                var t = envelope.BusinessObject.Transaction;

                // ── Crear objeto pedido de venta ────────────────────────────────
                dynamic doc = company.GetBusinessObject(ParseBoObjectType(company, "oOrders"));

                // CABECERA
                doc.CardCode        = resolved.CardCode;
                doc.NumAtCard       = envelope.ZohoOrderId;   // referencia del pedido en Zoho
                doc.SalesPersonCode = resolved.SlpCode;

                DateTime docDate;
                if (DateTime.TryParse(t.Date, out docDate))
                    doc.DocDate = docDate;

                // Comentarios con datos del cliente de Zoho para trazabilidad
                if (t.Customer != null)
                {
                    var comentario = string.Format(
                        "Zoho OrderId: {0} | Cliente Zoho: {1} | RUC/Ced: {2} | Tel: {3}",
                        envelope.ZohoOrderId,
                        t.Customer.Name  ?? "",
                        t.Customer.CustomerId ?? "",
                        t.Customer.Phone ?? "");
                    doc.Comments = comentario;
                }

                // LÍNEAS
                bool first = true;
                int  lineNum = 0;

                foreach (var item in t.SaleItemList)
                {
                    if (!first) doc.Lines.Add();
                    first = false;
                    lineNum++;

                    // Almacén por línea: prioridad:
                    //   1. WarehouseCode propio de la línea (si Zoho lo manda a futuro)
                    //   2. WarehouseCode resuelto de la cabecera
                    var lineWhs = !string.IsNullOrWhiteSpace(item.WarehouseCode)
                        ? item.WarehouseCode.Trim()
                        : resolved.WarehouseCode;

                    doc.Lines.ItemCode        = item.ProductId;
                    doc.Lines.Quantity        = (double)item.Quantity;
                    doc.Lines.Price           = (double)item.Price;
                    doc.Lines.DiscountPercent = (double)item.Discount;
                    doc.Lines.WarehouseCode   = lineWhs;

                    _log.Debug(string.Format(
                        "  Línea {0}: Item='{1}' Qty={2} Price={3} Disc={4}% Almacén='{5}'",
                        lineNum, item.ProductId, item.Quantity,
                        item.Price, item.Discount, lineWhs));
                }

                // ── Agregar el pedido en SAP ────────────────────────────────────
                var addRc = (int)doc.Add();
                if (addRc != 0)
                {
                    int addErr; string addMsg;
                    company.GetLastError(out addErr, out addMsg);
                    throw new InvalidOperationException(string.Format(
                        "SAP rechazó el pedido: [{0}] {1}", addErr, addMsg));
                }

                // ── Obtener DocEntry y DocNum ───────────────────────────────────
                var key = (string)company.GetNewObjectKey();
                int docEntry;
                if (!int.TryParse(key, out docEntry))
                    throw new InvalidOperationException(
                        "GetNewObjectKey() devolvió valor no numérico: " + key);

                dynamic doc2 = company.GetBusinessObject(ParseBoObjectType(company, "oOrders"));
                if (!(bool)doc2.GetByKey(docEntry))
                    throw new InvalidOperationException(string.Format(
                        "Pedido creado (DocEntry={0}) pero no se pudo releer.", docEntry));

                var docNum = (int)doc2.DocNum;

                _log.Info(string.Format(
                    "PEDIDO SAP CREADO: ZohoOrderId='{0}' CardCode='{1}' " +
                    "DocEntry={2} DocNum={3} Líneas={4}",
                    envelope.ZohoOrderId, resolved.CardCode, docEntry, docNum, lineNum));

                return new SapOrderResult { DocEntry = docEntry, DocNum = docNum };
            }
            finally
            {
                try
                {
                    if ((bool)company.Connected)
                        company.Disconnect();
                }
                catch (Exception ex)
                {
                    _log.Warn("Advertencia al desconectar SAP DI API: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Sobrecarga que mantiene compatibilidad con la interfaz original.
        /// Crea un ResolvedOrderData mínimo con los defaults del config.
        /// </summary>
        public SapOrderResult CreateSalesOrder(SohoEnvelope envelope)
        {
            var resolved = new ResolvedOrderData
            {
                CardCode       = AppSettings.DefaultCardCode,
                SlpCode        = AppSettings.DefaultSlpCode,
                WarehouseCode  = AppSettings.DefaultWarehouseCode,
                ClienteResuelto = false,
                AlmacenResuelto = false
            };
            return CreateSalesOrder(envelope, resolved);
        }

        // ── Helpers COM late binding ──────────────────────────────────────────────

        private static dynamic CreateSapCompany()
        {
            var t = Type.GetTypeFromProgID("SAPbobsCOM.Company", false);
            if (t == null)
                throw new InvalidOperationException(
                    "SAP DI API no instalada. ProgID 'SAPbobsCOM.Company' no registrado en COM.");
            return Activator.CreateInstance(t)
                ?? throw new InvalidOperationException("No se pudo instanciar SAPbobsCOM.Company.");
        }

        private static object ParseBoObjectType(dynamic company, string enumName)
        {
            var enumType = ((Type)company.GetType()).Assembly.GetType("SAPbobsCOM.BoObjectTypes");
            if (enumType == null)
                throw new InvalidOperationException("No se encontró SAPbobsCOM.BoObjectTypes.");
            return Enum.Parse(enumType, enumName);
        }

        private static object ParseDbServerType(dynamic company, string dbServerType)
        {
            var enumType = ((Type)company.GetType()).Assembly.GetType("SAPbobsCOM.BoDataServerTypes");
            if (enumType == null)
                throw new InvalidOperationException("No se encontró SAPbobsCOM.BoDataServerTypes.");

            string normalized;
            switch (dbServerType)
            {
                case "dst_MSSQL2008": normalized = "dst_MSSQL2008"; break;
                case "dst_MSSQL2012": normalized = "dst_MSSQL2012"; break;
                case "dst_MSSQL2014": normalized = "dst_MSSQL2014"; break;
                case "dst_MSSQL2017": normalized = "dst_MSSQL2017"; break;
                case "dst_HANADB":    normalized = "dst_HANADB";    break;
                default:              normalized = "dst_MSSQL2016"; break;
            }
            return Enum.Parse(enumType, normalized);
        }
    }
}
