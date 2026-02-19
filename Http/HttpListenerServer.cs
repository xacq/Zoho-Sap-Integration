using System;
using System.Net;
using System.Threading;
using SohoSapIntegrator.Config;
using SohoSapIntegrator.Core.Interfaces;

namespace SohoSapIntegrator.Http
{
    /// <summary>
    /// Servidor HTTP embebido basado en System.Net.HttpListener.
    /// Disponible en .NET Framework 2.0+ sin necesidad de IIS ni ASP.NET.
    ///
    /// El servidor corre en un thread separado y acepta solicitudes de forma
    /// concurrente (ThreadPool) para manejar múltiples pedidos simultáneos.
    ///
    /// REQUISITO WINDOWS: Para escuchar en un prefijo distinto de localhost,
    /// se debe ejecutar como Administrador o reservar el URL con netsh:
    ///   netsh http add urlacl url=http://+:8080/ user=DOMINIO\Usuario
    /// </summary>
    public class HttpListenerServer
    {
        private readonly ILogger _log;
        private readonly RequestRouter _router;
        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;

        public HttpListenerServer(ILogger log, RequestRouter router)
        {
            _log    = log;
            _router = router;
        }

        /// <summary>Inicia el servidor HTTP en background.</summary>
        public void Start()
        {
            var prefix = AppSettings.HttpPrefix;

            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "No se pudo iniciar el servidor HTTP en '{0}'. " +
                        "Si el prefijo no es localhost, ejecuta como Administrador o registra el URL con:\n" +
                        "  netsh http add urlacl url={0} user=%USERDOMAIN%\\%USERNAME%\n" +
                        "Error original: {1}", prefix, ex.Message), ex);
            }

            _running = true;
            _listenerThread = new Thread(ListenLoop)
            {
                Name = "HttpListenerThread",
                IsBackground = true
            };
            _listenerThread.Start();

            _log.Info(string.Format("Servidor HTTP iniciado en: {0}", prefix));
            _log.Info(string.Format("  POST {0}orders              → Crear pedido en SAP", prefix));
            _log.Info(string.Format("  GET  {0}orders/{{id}}/{{inst}}/status → Estado de pedido", prefix));
            _log.Info(string.Format("  GET  {0}health              → Health check", prefix));
        }

        /// <summary>Detiene el servidor HTTP de forma ordenada.</summary>
        public void Stop()
        {
            _running = false;

            try
            {
                if (_listener != null && _listener.IsListening)
                    _listener.Stop();
            }
            catch (Exception ex)
            {
                _log.Warn("Advertencia al detener HttpListener: " + ex.Message);
            }

            _log.Info("Servidor HTTP detenido.");
        }

        // ── Loop principal de escucha ─────────────────────────────────────────────

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    // BeginGetContext/EndGetContext es el patrón async de HttpListener en .NET 4.0
                    var asyncResult = _listener.BeginGetContext(HandleContextCallback, null);
                    // Esperar a que llegue una solicitud o a que se detenga el servidor
                    asyncResult.AsyncWaitHandle.WaitOne();
                }
                catch (HttpListenerException)
                {
                    // Ocurre cuando _listener.Stop() es llamado; es la señal para terminar
                    if (!_running) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                        _log.Error("Error en ListenLoop", ex);
                }
            }
        }

        private void HandleContextCallback(IAsyncResult ar)
        {
            if (!_running || _listener == null) return;

            HttpListenerContext ctx = null;
            try
            {
                ctx = _listener.EndGetContext(ar);
            }
            catch (Exception)
            {
                // El listener fue detenido entre BeginGetContext y EndGetContext
                return;
            }

            // Procesar en el ThreadPool para no bloquear el loop de escucha
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    _router.Handle((HttpListenerContext)state);
                }
                catch (Exception ex)
                {
                    _log.Error("Error procesando solicitud HTTP", ex);
                }
            }, ctx);
        }
    }
}
