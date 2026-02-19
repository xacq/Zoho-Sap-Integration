using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using SohoSapIntegrator.Config;
using SohoSapIntegrator.Core.Interfaces;
using SohoSapIntegrator.Core.Models;
using SohoSapIntegrator.Data;
using SohoSapIntegrator.Services;
using SohoSapIntegrator.Utils;

namespace SohoSapIntegrator.Http
{
    /// <summary>
    /// V2 — RequestRouter actualizado para pasar ResolvedOrderData a SapDiService.
    /// El flujo ahora es:
    ///   1. Autenticación
    ///   2. Idempotencia (TryBegin)
    ///   3. PreValidate → devuelve ResolvedOrderData con todo resuelto
    ///   4. CreateSalesOrder(envelope, resolved) → usa datos ya verificados
    /// </summary>
    public class RequestRouter
    {
        private readonly ILogger           _log;
        private readonly SapDiService      _sap;
        private readonly OrderMapRepository _repo;

        public RequestRouter(ILogger log, SapDiService sap, OrderMapRepository repo)
        {
            _log  = log;
            _sap  = sap;
            _repo = repo;
        }

        public void Handle(HttpListenerContext ctx)
        {
            var method = ctx.Request.HttpMethod.ToUpperInvariant();
            var path   = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();

            _log.Debug(string.Format("→ {0} {1}", method, ctx.Request.Url.AbsolutePath));

            try
            {
                if (!AuthorizeRequest(ctx.Request, ctx.Response)) return;

                if (method == "POST" && path == "/orders")
                    HandlePostOrders(ctx.Request, ctx.Response);
                else if (method == "GET" && path == "/health")
                    HandleHealth(ctx.Response);
                else if (method == "GET" && path.StartsWith("/orders/"))
                    HandleGetOrderStatus(ctx.Request, ctx.Response, path);
                else
                    WriteJson(ctx.Response, 404, new { ok = false, code = "NOT_FOUND",
                        message = "Ruta no encontrada: " + ctx.Request.Url.AbsolutePath });
            }
            catch (Exception ex)
            {
                _log.Error("Error no manejado en Handle", ex);
                try { WriteJson(ctx.Response, 500, new { ok = false, code = "INTERNAL_ERROR",
                    message = "Error interno del servidor." }); }
                catch { }
            }
        }

        // ── POST /orders ──────────────────────────────────────────────────────────

        private void HandlePostOrders(HttpListenerRequest req, HttpListenerResponse resp)
        {
            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                body = reader.ReadToEnd();

            List<SohoEnvelope> envelopes;
            string parseError;
            if (!JsonHelper.TryDeserialize<List<SohoEnvelope>>(body, out envelopes, out parseError))
            {
                WriteJson(resp, 400, new { ok = false, code = "BAD_REQUEST", message = parseError });
                return;
            }

            if (envelopes == null || envelopes.Count == 0)
            {
                WriteJson(resp, 400, new { ok = false, code = "BAD_REQUEST",
                    message = "Payload vacío: se espera un array con al menos un pedido." });
                return;
            }

            var results = new List<object>();

            foreach (var env in envelopes)
            {
                var zohoOrderId = env.ZohoOrderId != null ? env.ZohoOrderId.Trim() : "";
                var instanceId  = env.InstanceId  != null ? env.InstanceId.Trim()  : "";

                // ── Validación básica del envelope ──────────────────────────────
                if (string.IsNullOrEmpty(zohoOrderId) || string.IsNullOrEmpty(instanceId))
                {
                    results.Add(new { ok = false, code = "VALIDATION",
                        message = "Falta zohoOrderId o instanceId." });
                    continue;
                }

                if (env.BusinessObject == null ||
                    env.BusinessObject.Transaction == null ||
                    env.BusinessObject.Transaction.SaleItemList == null ||
                    env.BusinessObject.Transaction.SaleItemList.Count == 0)
                {
                    results.Add(new { ok = false, code = "VALIDATION",
                        zohoOrderId, instanceId,
                        message = "businessObject.Transaction.SaleItemList está vacío." });
                    continue;
                }

                var payloadHash = HashHelper.Sha256Hex(JsonHelper.Serialize(env));

                // ── PASO 1: Idempotencia ────────────────────────────────────────
                BeginResult begin;
                try { begin = _repo.TryBegin(zohoOrderId, instanceId, payloadHash); }
                catch (Exception ex)
                {
                    _log.Error("TryBegin falló: " + zohoOrderId, ex);
                    results.Add(new { ok = false, code = "ERROR", zohoOrderId, instanceId,
                        message = "Error de base de datos: " + ex.Message });
                    continue;
                }

                if (begin.Code == BeginCode.DuplicateCreated)
                {
                    results.Add(new { ok = true, code = "DUPLICATE", zohoOrderId, instanceId,
                        payloadHash = begin.PayloadHash,
                        sap = new { docEntry = begin.SapDocEntry, docNum = begin.SapDocNum } });
                    continue;
                }

                if (begin.Code == BeginCode.InProgress)
                {
                    results.Add(new { ok = false, code = "IN_PROGRESS", zohoOrderId, instanceId,
                        message = "Pedido en procesamiento. Reintente en unos segundos." });
                    continue;
                }

                if (begin.Code == BeginCode.ConflictHash)
                {
                    results.Add(new { ok = false, code = "CONFLICT_HASH", zohoOrderId, instanceId,
                        message = "Mismo zohoOrderId+instanceId con payload diferente." });
                    continue;
                }

                // ── PASO 2: Pre-validación → resuelve cliente, vendedor, almacén ─
                ResolvedOrderData resolved;
                PreValidationResult preVal;
                try { preVal = _repo.PreValidate(env, out resolved); }
                catch (Exception ex)
                {
                    _log.Error("PreValidate lanzó excepción", ex);
                    preVal   = PreValidationResult.Fail("Error en pre-validación: " + ex.Message);
                    resolved = new ResolvedOrderData();
                }

                if (!preVal.Ok)
                {
                    _repo.SafeMarkFailed(zohoOrderId, instanceId, "PREVALIDATION: " + preVal.Message);
                    results.Add(new { ok = false, code = "PREVALIDATION", zohoOrderId, instanceId,
                        message = preVal.Message });
                    continue;
                }

                // ── PASO 3: Crear en SAP con datos ya resueltos ─────────────────
                try
                {
                    var created = _sap.CreateSalesOrder(env, resolved);

                    _repo.MarkCreated(zohoOrderId, instanceId, created.DocEntry, created.DocNum);

                    _log.Info(string.Format(
                        "OK ZohoOrderId='{0}' CardCode='{1}' DocEntry={2} DocNum={3}",
                        zohoOrderId, resolved.CardCode, created.DocEntry, created.DocNum));

                    results.Add(new
                    {
                        ok  = true,
                        code = "CREATED",
                        zohoOrderId,
                        instanceId,
                        payloadHash,
                        cliente = new
                        {
                            cardCode        = resolved.CardCode,
                            resuelto        = resolved.ClienteResuelto,
                            rucCedula       = env.BusinessObject.Transaction.Customer != null
                                                ? env.BusinessObject.Transaction.Customer.CustomerId
                                                : null
                        },
                        vendedor  = new { slpCode = resolved.SlpCode },
                        almacen   = new { whsCode = resolved.WarehouseCode, resuelto = resolved.AlmacenResuelto },
                        advertencias = resolved.ArticulosSinAlmacen.Count > 0
                            ? string.Format("Artículos sin OITW en almacén '{0}': {1}",
                                resolved.WarehouseCode,
                                string.Join(", ", resolved.ArticulosSinAlmacen))
                            : null,
                        sap = new { docEntry = created.DocEntry, docNum = created.DocNum }
                    });
                }
                catch (Exception ex)
                {
                    _log.Error(string.Format(
                        "ERROR SAP ZohoOrderId='{0}'", zohoOrderId), ex);
                    _repo.SafeMarkFailed(zohoOrderId, instanceId, ex.ToString());
                    results.Add(new { ok = false, code = "ERROR", zohoOrderId, instanceId,
                        message = ex.Message });
                }
            }

            WriteJson(resp, 200, new { ok = true, results });
        }

        // ── GET /orders/{zohoOrderId}/{instanceId}/status ──────────────────────────

        private void HandleGetOrderStatus(HttpListenerRequest req, HttpListenerResponse resp, string path)
        {
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 4 || segments[3] != "status")
            {
                WriteJson(resp, 400, new { ok = false, code = "BAD_REQUEST",
                    message = "Formato: GET /orders/{zohoOrderId}/{instanceId}/status" });
                return;
            }

            var zohoOrderId = Uri.UnescapeDataString(segments[1]).Trim();
            var instanceId  = Uri.UnescapeDataString(segments[2]).Trim();

            if (string.IsNullOrEmpty(zohoOrderId) || string.IsNullOrEmpty(instanceId))
            {
                WriteJson(resp, 400, new { ok = false, code = "BAD_REQUEST",
                    message = "zohoOrderId e instanceId son requeridos." });
                return;
            }

            try
            {
                var status = _repo.GetStatus(zohoOrderId, instanceId);
                if (!status.Found)
                {
                    WriteJson(resp, 404, new { ok = false, code = "NOT_FOUND",
                        zohoOrderId, instanceId,
                        message = "No existe registro para esa combinación." });
                    return;
                }

                WriteJson(resp, 200, new
                {
                    ok = true, code = "STATUS",
                    zohoOrderId, instanceId,
                    status = status.Status,
                    sap    = new { docEntry = status.SapDocEntry, docNum = status.SapDocNum },
                    errorMessage = status.ErrorMessage,
                    updatedAt    = status.UpdatedAt
                });
            }
            catch (Exception ex)
            {
                _log.Error("Error en GetStatus", ex);
                WriteJson(resp, 500, new { ok = false, code = "ERROR", message = ex.Message });
            }
        }

        // ── GET /health ────────────────────────────────────────────────────────────

        private void HandleHealth(HttpListenerResponse resp)
        {
            WriteJson(resp, 200, new
            {
                ok        = true,
                code      = "HEALTHY",
                version   = "2.0",
                timestamp = DateTime.UtcNow.ToString("o"),
                empresa   = AppSettings.SapCompanyDb,
                puerto    = AppSettings.HttpPort
            });
        }

        // ── Autenticación ──────────────────────────────────────────────────────────

        private bool AuthorizeRequest(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var configuredKey = AppSettings.ApiKey;
            if (string.IsNullOrWhiteSpace(configuredKey)) return true;

            var provided = req.Headers["X-API-KEY"];
            if (string.IsNullOrEmpty(provided) ||
                !string.Equals(provided.Trim(), configuredKey, StringComparison.Ordinal))
            {
                WriteJson(resp, 401, new { ok = false, code = "UNAUTHORIZED",
                    message = "API Key inválida o ausente (header X-API-KEY)." });
                return false;
            }
            return true;
        }

        // ── Helper JSON ────────────────────────────────────────────────────────────

        private static void WriteJson(HttpListenerResponse resp, int statusCode, object body)
        {
            var json  = JsonHelper.Serialize(body);
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.StatusCode    = statusCode;
            resp.ContentType   = "application/json; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            resp.Headers["Access-Control-Allow-Origin"] = "*";
            try   { resp.OutputStream.Write(bytes, 0, bytes.Length); }
            finally { resp.OutputStream.Close(); }
        }
    }
}
