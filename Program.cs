using System;
using System.Configuration.Install;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using SohoSapIntegrator.Config;
using SohoSapIntegrator.Logging;
using SohoSapIntegrator.WinService;

namespace SohoSapIntegrator
{
    /// <summary>
    /// Punto de entrada del integrador Soho → SAP Business One.
    ///
    /// MODOS DE EJECUCIÓN:
    ///
    ///   SohoSapIntegrator.exe             → Modo consola (por defecto si no hay servicio)
    ///   SohoSapIntegrator.exe /console    → Modo consola explícito
    ///   SohoSapIntegrator.exe /install    → Instala como Servicio Windows
    ///   SohoSapIntegrator.exe /uninstall  → Desinstala el Servicio Windows
    ///   (sin argumentos, corriendo como SCM) → Modo Servicio Windows automático
    ///
    /// DESCRIPCIÓN GENERAL:
    ///   Este integrador levanta un servidor HTTP embebido (HttpListener) que recibe
    ///   pedidos de Soho/Zoho via POST /orders, los valida contra SAP Business One
    ///   y los crea en SAP usando la DI API (SAPbobsCOM).
    ///
    ///   La idempotencia garantiza que un mismo pedido no se crea dos veces en SAP
    ///   aunque Soho/Zoho envíe el mismo pedido múltiples veces.
    /// </summary>
    internal sealed class Program
    {
        static void Main(string[] args)
        {
            // Asegurar que la consola muestre caracteres UTF-8 correctamente
            Console.OutputEncoding = Encoding.UTF8;

            var logger = new ConsoleFileLogger();

            try
            {
                var arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";

                switch (arg)
                {
                    case "/install":
                    case "-install":
                        InstallService(logger);
                        break;

                    case "/uninstall":
                    case "-uninstall":
                        UninstallService(logger);
                        break;

                    case "/console":
                    case "-console":
                        RunAsConsole(logger);
                        break;

                    default:
                        // Si no hay argumentos y el proceso fue iniciado por el SCM de Windows,
                        // correr como servicio. De lo contrario, modo consola.
                        if (Environment.UserInteractive)
                            RunAsConsole(logger);
                        else
                            RunAsWindowsService();
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.Error("Error fatal en Main", ex);
                Console.Error.WriteLine("ERROR FATAL: " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                Environment.Exit(1);
            }
        }

        // ── Modo Consola ──────────────────────────────────────────────────────────

        private static void RunAsConsole(ConsoleFileLogger logger)
        {
            Console.Title = AppSettings.ServiceDisplayName;

            PrintBanner();

            logger.Info("=== Iniciando en modo CONSOLA ===");
            logger.Info("Puerto HTTP : " + AppSettings.HttpPort);
            logger.Info("Prefijo     : " + AppSettings.HttpPrefix);
            logger.Info("SAP Empresa : " + AppSettings.SapCompanyDb);
            logger.Info("SAP Servidor: " + AppSettings.SapServer);
            logger.Info("Log Dir     : " + AppSettings.LogDirectory);
            logger.Info("API Key     : " + (string.IsNullOrEmpty(AppSettings.ApiKey) ? "(no configurada - INSEGURO)" : "configurada"));
            logger.Info("");

            var service = new IntegrationService();

            try
            {
                service.StartIntegrator();
            }
            catch (Exception ex)
            {
                logger.Error("No se pudo iniciar el integrador", ex);
                Console.WriteLine("\nPresione cualquier tecla para salir...");
                Console.ReadKey();
                Environment.Exit(1);
                return;
            }

            // Capturar Ctrl+C para apagar ordenadamente
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                logger.Info("Señal de interrupción recibida (Ctrl+C). Cerrando...");
                service.StopIntegrator();
                Environment.Exit(0);
            };

            Console.WriteLine();
            Console.WriteLine("  Integrador activo. Presione ENTER para detener.");
            Console.WriteLine();

            Console.ReadLine();

            logger.Info("Deteniendo integrador por solicitud del usuario...");
            service.StopIntegrator();
            logger.Info("Integrador detenido. Hasta pronto.");
        }

        // ── Modo Servicio Windows ─────────────────────────────────────────────────

        private static void RunAsWindowsService()
        {
            ServiceBase.Run(new IntegrationService());
        }

        // ── Instalación / Desinstalación de Servicio Windows ─────────────────────

        private static void InstallService(ConsoleFileLogger logger)
        {
            logger.Info(string.Format("Instalando servicio '{0}'...", AppSettings.ServiceName));
            try
            {
                ManagedInstallerClass.InstallHelper(new[]
                {
                    Assembly.GetExecutingAssembly().Location
                });
                logger.Info("Servicio instalado correctamente.");
                logger.Info(string.Format(
                    "Para iniciarlo: sc start {0}", AppSettings.ServiceName));
            }
            catch (Exception ex)
            {
                logger.Error("Error al instalar el servicio", ex);
                throw;
            }
        }

        private static void UninstallService(ConsoleFileLogger logger)
        {
            logger.Info(string.Format("Desinstalando servicio '{0}'...", AppSettings.ServiceName));
            try
            {
                ManagedInstallerClass.InstallHelper(new[]
                {
                    "/u",
                    Assembly.GetExecutingAssembly().Location
                });
                logger.Info("Servicio desinstalado correctamente.");
            }
            catch (Exception ex)
            {
                logger.Error("Error al desinstalar el servicio", ex);
                throw;
            }
        }

        // ── Banner de consola ─────────────────────────────────────────────────────

        private static void PrintBanner()
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 70));
            Console.WriteLine("  SOHO SAP INTEGRADOR  -  Pedidos Soho/Zoho → SAP Business One");
            Console.WriteLine(new string('-', 70));
            Console.WriteLine("  Framework : .NET 4.0");
            Console.WriteLine("  SAP       : Business One vía DI API (SAPbobsCOM COM)");
            Console.WriteLine("  Transport : HTTP embebido (HttpListener)");
            Console.WriteLine(new string('=', 70));
            Console.WriteLine();
        }
    }
}
