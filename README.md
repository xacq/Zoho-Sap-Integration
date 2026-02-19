# SohoSapIntegrator

Integrador de pedidos **Soho/Zoho → SAP Business One** para .NET Framework 4.0.

Recibe pedidos de venta vía HTTP POST, los valida contra SAP y los crea en SAP Business One usando la **DI API (SAPbobsCOM)**. Diseñado para correr como consola o como **Servicio Windows** en servidores con restricciones de framework.

---

## Arquitectura

```
Zoho/Soho
    │
    │  POST /orders  (JSON array)
    │  Header: X-API-KEY: tu-clave
    ▼
┌─────────────────────────────────────────┐
│         SohoSapIntegrator.exe           │
│  (HttpListener embebido, puerto 8080)   │
│                                         │
│  RequestRouter                          │
│    ├─ Autenticación (API Key)           │
│    ├─ Idempotencia (Z_SOHO_OrderMap)    │
│    ├─ Pre-validación SQL (OCRD/OITM)    │
│    └─ SapDiService → SAPbobsCOM.Company│
│                           │             │
│                    SAP DI API           │
└─────────────────────────────────────────┘
           │                    │
    SQL Server              SAP B1
   (OrderMap)           (Pedido creado)
```

---

## Requisitos del Servidor

- Windows Server / Windows (cualquier versión moderna)
- **.NET Framework 4.0** instalado
- **SAP Business One DI API** instalada y registrada como COM
  - La DLL `SAPbobsCOM.dll` debe estar registrada vía el instalador de SAP
  - Verificar: `regsvr32 SAPbobsCOM.dll` o que el instalador de SAP lo haya hecho
- SQL Server accesible (el mismo que usa SAP B1 o uno separado)
- **Newtonsoft.Json.dll** en la carpeta `lib\` junto al ejecutable

---

## Instalación Paso a Paso

### 1. Configurar App.config

Editar `SohoSapIntegrator.exe.config` (copia de App.config) con los valores reales:

```xml
<!-- Cadena de conexión SQL -->
<add name="DefaultConnection"
     connectionString="Server=MI_SERVIDOR;Database=MI_BD;User Id=sa;Password=MI_PASS;" />

<!-- API Key de seguridad -->
<add key="Soho.ApiKey" value="MI_CLAVE_SECRETA_MUY_LARGA"/>

<!-- Maestros SAP por defecto -->
<add key="Soho.DefaultCardCode"      value="CLIENTE_SOHO"/>
<add key="Soho.DefaultSlpCode"       value="1"/>
<add key="Soho.DefaultWarehouseCode" value="01"/>

<!-- SAP DI API -->
<add key="SapDi.Server"        value="192.168.1.100"/>
<add key="SapDi.DbServerType"  value="dst_MSSQL2016"/>
<add key="SapDi.CompanyDb"     value="NOMBRE_EMPRESA_SAP"/>
<add key="SapDi.DbUser"        value="sa"/>
<add key="SapDi.DbPassword"    value="PASSWORD_SQL"/>
<add key="SapDi.UserName"      value="manager"/>
<add key="SapDi.Password"      value="PASSWORD_SAP"/>
<add key="SapDi.LicenseServer" value="192.168.1.100:40000"/>
<add key="SapDi.SapDatabase"   value="NOMBRE_EMPRESA_SAP"/>
```

### 2. Ejecutar el script SQL

En SQL Server (la BD de integración, que puede ser la misma de SAP):

```sql
-- Editar la línea USE al inicio con el nombre real de la BD
sqlcmd -S SERVIDOR -d BD -i sql\01_setup.sql
```

Esto crea la tabla `dbo.Z_SOHO_OrderMap`.

### 3. Registrar el URL para HttpListener (si no es localhost)

Ejecutar como Administrador:

```batch
netsh http add urlacl url=http://+:8080/ user=DOMINIO\USUARIO
```

### 4. Ejecutar en modo consola (prueba)

```batch
SohoSapIntegrator.exe /console
```

### 5. Instalar como Servicio Windows (producción)

```batch
:: Ejecutar como Administrador
SohoSapIntegrator.exe /install

:: Iniciar el servicio
sc start SohoSapIntegrator

:: Verificar estado
sc query SohoSapIntegrator
```

Para desinstalar:
```batch
sc stop SohoSapIntegrator
SohoSapIntegrator.exe /uninstall
```

---

## Uso de la API

### Crear Pedidos

```http
POST http://TU_SERVIDOR:8080/orders
Content-Type: application/json
X-API-KEY: tu-clave-secreta

[
  {
    "zohoOrderId": "SO-00123",
    "instanceId": "inst-001",
    "businessObject": {
      "Transaction": {
        "date": "2026-02-18",
        "Customer": {
          "CustomerId": "CLI001",
          "Name": "Juan Pérez",
          "Phone": "0991234567"
        },
        "SaleItemList": [
          {
            "ProductId": "ART-001",
            "Quantity": 2,
            "Price": 150.00,
            "Discount": 0
          },
          {
            "ProductId": "ART-002",
            "Quantity": 1,
            "Price": 75.50,
            "Discount": 5
          }
        ]
      }
    }
  }
]
```

**Respuestas posibles:**

| Código | Descripción |
|--------|-------------|
| `CREATED` | Pedido creado con éxito en SAP. Incluye `sap.docEntry` y `sap.docNum`. |
| `DUPLICATE` | El pedido ya existía en SAP (idempotencia). Devuelve los IDs SAP guardados. |
| `IN_PROGRESS` | Otra solicitud está procesando el mismo pedido. Reintentar en segundos. |
| `CONFLICT_HASH` | El mismo ID llegó con datos diferentes. Error de consistencia. |
| `PREVALIDATION` | Falló la validación de maestros SAP (artículo no existe, etc.). |
| `ERROR` | Error al crear en SAP. Revisar `message` y logs. |

### Consultar Estado de un Pedido

```http
GET http://TU_SERVIDOR:8080/orders/SO-00123/inst-001/status
X-API-KEY: tu-clave-secreta
```

### Health Check

```http
GET http://TU_SERVIDOR:8080/health
```

---

## Idempotencia

El integrador **garantiza que un mismo pedido no se crea dos veces en SAP**, aunque Zoho envíe el mismo payload múltiples veces. El mecanismo usa:

1. La clave `zohoOrderId + instanceId` como identificador único.
2. Un hash SHA-256 del payload para detectar cambios en el contenido.
3. Bloqueo a nivel de fila SQL (`UPDLOCK + HOLDLOCK`) para evitar condiciones de carrera concurrentes.
4. Estados: `PROCESSING → CREATED` (éxito) o `PROCESSING → FAILED` (error, permite reintento).

---

## Estructura del Proyecto

```
SohoSapIntegrator/
├── Program.cs                    ← Punto de entrada (consola/servicio/install)
├── App.config                    ← Toda la configuración
├── Config/
│   └── AppSettings.cs            ← Lectura centralizada del App.config
├── Core/
│   ├── Interfaces/
│   │   ├── ILogger.cs
│   │   └── ISapDiService.cs
│   └── Models/
│       ├── SohoEnvelope.cs       ← Estructura del payload JSON
│       ├── SohoTransaction.cs    ← Modelos de líneas y cabecera
│       └── OrderMapEntry.cs      ← Modelos de la tabla de idempotencia
├── Data/
│   └── OrderMapRepository.cs     ← Idempotencia + pre-validación SQL
├── Services/
│   └── SapDiService.cs           ← Conexión y creación en SAP DI API
├── Http/
│   ├── HttpListenerServer.cs     ← Servidor HTTP embebido
│   └── RequestRouter.cs          ← Lógica de endpoints y procesamiento
├── Logging/
│   └── ConsoleFileLogger.cs      ← Log a consola + archivo diario
├── WinService/
│   └── IntegrationService.cs     ← Wrapper para Windows Service
├── Utils/
│   ├── JsonHelper.cs             ← Wrapper Newtonsoft.Json
│   └── HashHelper.cs             ← SHA-256 para idempotencia
└── sql/
    └── 01_setup.sql              ← Script de creación de tabla
```

---

## Dependencias

| Librería | Versión | Uso |
|----------|---------|-----|
| Newtonsoft.Json | 6.x+ | Serialización JSON (colocar en `lib\`) |
| SAPbobsCOM (COM) | SAP B1 version | DI API de SAP (late binding, no referencia directa) |
| System.Data.SqlClient | .NET 4.0 BCL | Acceso a SQL Server |
| System.Net.HttpListener | .NET 4.0 BCL | Servidor HTTP embebido |
| System.ServiceProcess | .NET 4.0 BCL | Servicio Windows |

### Obtener Newtonsoft.Json compatible con .NET 4.0

```
NuGet: Install-Package Newtonsoft.Json -Version 6.0.8
```
Copiar `Newtonsoft.Json.dll` a la carpeta `lib\` del proyecto.

---

## Logs

Los logs se escriben simultáneamente en:
- **Consola** (con colores por nivel)
- **Archivos diarios** en el directorio configurado en `Log.Directory`:
  ```
  C:\SohoSapIntegrator\Logs\SohoSapIntegrator_2026-02-18.log
  ```

---

## Troubleshooting

### Error: "No se encontró SAP DI API"
- La DI API de SAP no está instalada o el ProgID `SAPbobsCOM.Company` no está registrado.
- Verificar instalación del cliente SAP B1 en el servidor.
- Verificar con `regedit`: `HKLM\SOFTWARE\Classes\SAPbobsCOM.Company`

### Error: "No se pudo iniciar el servidor HTTP"
- Ejecutar como Administrador o registrar el URL:
  ```batch
  netsh http add urlacl url=http://+:8080/ user=%USERDOMAIN%\%USERNAME%
  ```

### Error de conexión SQL
- Verificar la cadena de conexión en App.config.
- Verificar que el usuario SQL tenga permisos sobre la tabla `Z_SOHO_OrderMap`.

### El pedido falla con PREVALIDATION
- El `DefaultCardCode`, `DefaultSlpCode` o `DefaultWarehouseCode` configurado no existe en SAP.
- Alguno de los `ProductId` del pedido no existe en la tabla `OITM` de SAP.
- Verificar con las consultas en el script SQL.
