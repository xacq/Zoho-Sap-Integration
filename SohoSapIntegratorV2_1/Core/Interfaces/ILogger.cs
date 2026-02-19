using System;

namespace SohoSapIntegrator.Core.Interfaces
{
    /// <summary>
    /// Interfaz de logging. Permite cambiar la implementación sin modificar el resto del código.
    /// </summary>
    public interface ILogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Error(string message, Exception ex);
    }
}
