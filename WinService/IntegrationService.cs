using System;
using System.ServiceProcess;
using SohoSapIntegrator.Config;
using SohoSapIntegrator.Core.Interfaces;
using SohoSapIntegrator.Data;
using SohoSapIntegrator.Http;
using SohoSapIntegrator.Logging;
using SohoSapIntegrator.Services;

namespace SohoSapIntegrator.WinService
{
    public class IntegrationService : ServiceBase
    {
        private readonly ILogger _log;
        private HttpListenerServer _httpServer;

        public IntegrationService()
        {
            ServiceName         = AppSettings.ServiceName;
            CanStop             = true;
            CanPauseAndContinue = false;
            AutoLog             = true;
            _log = new ConsoleFileLogger();
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                _log.Info(string.Format("Iniciando servicio '{0}'...", AppSettings.ServiceDisplayName));
                StartIntegrator();
                _log.Info("Servicio iniciado correctamente.");
            }
            catch (Exception ex)
            {
                _log.Error("Error fatal al iniciar el servicio", ex);
                throw;
            }
        }

        protected override void OnStop()
        {
            _log.Info("Deteniendo servicio...");
            StopIntegrator();
            _log.Info("Servicio detenido.");
        }

        internal void StartIntegrator()
        {
            var repo   = new OrderMapRepository(_log);
            var sap    = new SapDiService(_log);
            var router = new RequestRouter(_log, sap, repo);
            _httpServer = new HttpListenerServer(_log, router);
            _httpServer.Start();
        }

        internal void StopIntegrator()
        {
            if (_httpServer != null)
            {
                _httpServer.Stop();
                _httpServer = null;
            }
        }
    }
}
