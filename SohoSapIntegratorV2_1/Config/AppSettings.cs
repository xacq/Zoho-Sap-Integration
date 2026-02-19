using System;
using System.Configuration;

namespace SohoSapIntegrator.Config
{
    /// <summary>
    /// Centraliza la lectura de todos los valores de configuración del App.config.
    /// Usar esta clase en lugar de acceder a ConfigurationManager directamente.
    /// </summary>
    public static class AppSettings
    {
        // ── Seguridad ────────────────────────────────────────────────────────────
        public static string ApiKey
        {
            get { return Get("Soho.ApiKey", ""); }
        }

        // ── HTTP ─────────────────────────────────────────────────────────────────
        public static int HttpPort
        {
            get { return GetInt("Http.Port", 8080); }
        }

        public static string HttpPrefix
        {
            get { return Get("Http.Prefix", "http://+:8080/"); }
        }

        // ── Valores por defecto de mapeo SAP ────────────────────────────────────
        public static string DefaultCardCode
        {
            get { return Get("Soho.DefaultCardCode", ""); }
        }

        public static int DefaultSlpCode
        {
            get { return GetInt("Soho.DefaultSlpCode", 1); }
        }

        public static string DefaultWarehouseCode
        {
            get { return Get("Soho.DefaultWarehouseCode", "01"); }
        }

        // ── SAP DI API ───────────────────────────────────────────────────────────
        public static string SapServer
        {
            get { return Get("SapDi.Server", ""); }
        }

        public static string SapDbServerType
        {
            get { return Get("SapDi.DbServerType", "dst_MSSQL2016"); }
        }

        public static string SapCompanyDb
        {
            get { return Get("SapDi.CompanyDb", ""); }
        }

        public static string SapDbUser
        {
            get { return Get("SapDi.DbUser", ""); }
        }

        public static string SapDbPassword
        {
            get { return Get("SapDi.DbPassword", ""); }
        }

        public static string SapUserName
        {
            get { return Get("SapDi.UserName", "manager"); }
        }

        public static string SapPassword
        {
            get { return Get("SapDi.Password", ""); }
        }

        public static string SapLicenseServer
        {
            get { return Get("SapDi.LicenseServer", ""); }
        }

        public static string SapDatabase
        {
            get { return Get("SapDi.SapDatabase", ""); }
        }

        // ── Logging ──────────────────────────────────────────────────────────────
        public static string LogDirectory
        {
            get { return Get("Log.Directory", @"C:\SohoSapIntegrator\Logs"); }
        }

        public static string LogLevel
        {
            get { return Get("Log.Level", "INFO"); }
        }

        // ── Servicio Windows ─────────────────────────────────────────────────────
        public static string ServiceName
        {
            get { return Get("Service.Name", "SohoSapIntegrator"); }
        }

        public static string ServiceDisplayName
        {
            get { return Get("Service.DisplayName", "Soho SAP Integrador"); }
        }

        public static string ServiceDescription
        {
            get { return Get("Service.Description", "Integrador de pedidos Soho → SAP Business One"); }
        }

        // ── Cadena de conexión ────────────────────────────────────────────────────
        public static string DefaultConnection
        {
            get
            {
                var cs = ConfigurationManager.ConnectionStrings["DefaultConnection"];
                if (cs == null)
                    throw new ConfigurationErrorsException(
                        "No se encontró la cadena de conexión 'DefaultConnection' en App.config.");
                return cs.ConnectionString;
            }
        }

        // ── Helpers privados ─────────────────────────────────────────────────────
        private static string Get(string key, string defaultValue)
        {
            var val = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(val) ? defaultValue : val.Trim();
        }

        private static int GetInt(string key, int defaultValue)
        {
            var val = ConfigurationManager.AppSettings[key];
            int result;
            return int.TryParse(val, out result) ? result : defaultValue;
        }
    }
}
