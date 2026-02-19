using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SohoSapIntegrator.Utils
{
    /// <summary>
    /// Wrapper de Newtonsoft.Json con configuración consistente en todo el proyecto.
    /// Usa camelCase para serialización (estándar REST) y es tolerante al deserializar.
    /// </summary>
    public static class JsonHelper
    {
        private static readonly JsonSerializerSettings _writeSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include,
            Formatting = Formatting.None
        };

        private static readonly JsonSerializerSettings _readSettings = new JsonSerializerSettings
        {
            // Tolerante: ignora propiedades desconocidas, no falla
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>Serializa un objeto a JSON (camelCase).</summary>
        public static string Serialize(object obj)
        {
            return JsonConvert.SerializeObject(obj, _writeSettings);
        }

        /// <summary>Serializa un objeto a JSON con indentado (para debugging).</summary>
        public static string SerializePretty(object obj)
        {
            return JsonConvert.SerializeObject(obj, Formatting.Indented, _writeSettings);
        }

        /// <summary>
        /// Deserializa JSON a un objeto del tipo T.
        /// Devuelve null si el JSON es inválido o vacío.
        /// </summary>
        public static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<T>(json, _readSettings);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Intenta deserializar JSON. Devuelve true si tuvo éxito.
        /// </summary>
        public static bool TryDeserialize<T>(string json, out T result, out string error) where T : class
        {
            result = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "El body de la solicitud está vacío.";
                return false;
            }

            try
            {
                result = JsonConvert.DeserializeObject<T>(json, _readSettings);
                if (result == null)
                {
                    error = "El body deserializado es null.";
                    return false;
                }
                return true;
            }
            catch (JsonException ex)
            {
                error = "JSON inválido: " + ex.Message;
                return false;
            }
        }
    }
}
