-- ============================================================
-- SCRIPT DE INSTALACIÓN V2.1: SohoSapIntegrator - VINESA
-- SQL Server 2016
-- ============================================================
-- INSTRUCCIONES:
--   1. Cambiar 'SohoIntegracion' por el nombre real de tu BD si difiere
--   2. Ejecutar completo en SQL Server Management Studio como sa
--   3. Verificar los mensajes PRINT al final
-- ============================================================

USE SohoIntegracion;
GO

-- ============================================================
-- TABLA 1: Z_SOHO_OrderMap — Idempotencia y trazabilidad
-- ============================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.objects
    WHERE object_id = OBJECT_ID(N'dbo.Z_SOHO_OrderMap') AND type = 'U'
)
BEGIN
    CREATE TABLE dbo.Z_SOHO_OrderMap
    (
        ZohoOrderId   NVARCHAR(100)  NOT NULL,
        InstanceId    NVARCHAR(100)  NOT NULL,
        PayloadHash   NVARCHAR(64)   NOT NULL,
        Status        NVARCHAR(20)   NOT NULL
            CONSTRAINT CK_Z_SOHO_OrderMap_Status
                CHECK (Status IN ('PROCESSING', 'CREATED', 'FAILED')),
        SapDocEntry   INT            NULL,
        SapDocNum     INT            NULL,
        ErrorMessage  NVARCHAR(MAX)  NULL,
        ProcessingAt  DATETIME       NULL,
        CreatedAt     DATETIME       NOT NULL DEFAULT GETDATE(),
        UpdatedAt     DATETIME       NOT NULL DEFAULT GETDATE(),
        CONSTRAINT PK_Z_SOHO_OrderMap
            PRIMARY KEY CLUSTERED (ZohoOrderId ASC, InstanceId ASC)
    );

    CREATE INDEX IX_Z_SOHO_OrderMap_Status
        ON dbo.Z_SOHO_OrderMap (Status, UpdatedAt DESC);

    PRINT '✓ Tabla Z_SOHO_OrderMap creada.';
END
ELSE
    PRINT '· Tabla Z_SOHO_OrderMap ya existe.';
GO

-- ============================================================
-- TABLA 2: Z_SOHO_AlmacenMap — Mapeo Zoho → SAP almacenes
-- ============================================================
-- Esta tabla es necesaria porque Zoho usa nombres propios
-- ("MATRIZ PLUSRAND") que no coinciden con los WhsCode/WhsName
-- de SAP VINESA. El mapeo se hace por warehouseId numérico
-- (más confiable) o por warehouseCode nombre (fallback).
-- ============================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.objects
    WHERE object_id = OBJECT_ID(N'dbo.Z_SOHO_AlmacenMap') AND type = 'U'
)
BEGIN
    CREATE TABLE dbo.Z_SOHO_AlmacenMap
    (
        -- ID numérico que Zoho manda en el campo "warehouseId"
        ZohoWarehouseId    INT            NOT NULL,

        -- Nombre que Zoho manda en el campo "warehouseCode"
        -- (puede ser diferente al nombre SAP, ej: "MATRIZ PLUSRAND")
        ZohoWarehouseName  NVARCHAR(100)  NOT NULL,

        -- WhsCode real en SAP Business One (tabla OWHS)
        SapWhsCode         NVARCHAR(20)   NOT NULL,

        -- WhsName en SAP (solo referencia visual, no se usa en queries)
        SapWhsName         NVARCHAR(100)  NOT NULL,

        -- Y = activo, N = ignorar este mapeo
        Activo             CHAR(1)        NOT NULL DEFAULT 'Y'
            CONSTRAINT CK_Z_SOHO_AlmacenMap_Activo CHECK (Activo IN ('Y','N')),

        Notas              NVARCHAR(200)  NULL,
        CreadoEn           DATETIME       NOT NULL DEFAULT GETDATE(),
        ModificadoEn       DATETIME       NOT NULL DEFAULT GETDATE(),

        CONSTRAINT PK_Z_SOHO_AlmacenMap PRIMARY KEY (ZohoWarehouseId)
    );

    -- Índice por nombre para el fallback de búsqueda por texto
    CREATE INDEX IX_Z_SOHO_AlmacenMap_Nombre
        ON dbo.Z_SOHO_AlmacenMap (ZohoWarehouseName);

    PRINT '✓ Tabla Z_SOHO_AlmacenMap creada.';
END
ELSE
    PRINT '· Tabla Z_SOHO_AlmacenMap ya existe.';
GO

-- ============================================================
-- DATOS: Mapeo de almacenes VINESA
-- ============================================================
-- Basado en los WhsCode/WhsName confirmados en TEST_VINESA..OWHS
-- Los ZohoWarehouseId son ESTIMADOS excepto el 17 (confirmado).
-- INSTRUCCIÓN: Ajustar los ZohoWarehouseId y ZohoWarehouseName
-- según los valores reales que mande Zoho para cada almacén.
-- ============================================================

-- Limpiar y recargar (safe: solo si la tabla existe y está vacía o para actualizar)
DELETE FROM dbo.Z_SOHO_AlmacenMap;
GO

INSERT INTO dbo.Z_SOHO_AlmacenMap
    (ZohoWarehouseId, ZohoWarehouseName,           SapWhsCode, SapWhsName,                        Activo, Notas)
VALUES
-- ── CONFIRMADO desde JSON de prueba ──────────────────────────────────────────
(17, 'MATRIZ PLUSRAND',                            '17',       'CONTROL DE CALIDAD MANTA',        'Y',    'Confirmado: warehouseId=17 en JSON Zoho'),

-- ── ALMACENES ACTIVOS VINESA (ZohoWarehouseId ESTIMADO, ajustar según Zoho) ─
(1,  'ALMACEN GENERAL UIO',                        '1',        'ALMACÉN GENERAL UIO',             'Y',    'Ajustar ZohoWarehouseId si Zoho usa otro ID'),
(10, 'CUENCA CONSIGNACIONES CLIENTES',             '10',       'CUENCA CONSIGNACIONES CLIENTES',  'Y',    NULL),
(12, 'CONTROL DE CALIDAD MATRIZ',                  '12',       'CONTROL DE CALIDAD MATRIZ',       'Y',    NULL),
(14, 'CONTROL DE CALIDAD CUENCA',                  '14',       'CONTROL DE CALIDAD CUENCA',       'Y',    NULL),
(15, 'BODEGA MANTA',                               '15',       'BODEGA MANTA',                    'Y',    NULL),
(18, 'MANTA CONSIGNACIONES CLIENTES',              '18',       'MANTA CONSIGNACIONES CLIENTES',   'Y',    NULL),
(20, 'TRANSITORIA VINESA',                         '20',       'TRANSITORIA VINESA',              'Y',    NULL),
(2,  'BODEGA MAL ESTADO UIO',                      '2',        'BODEGA MAL ESTADO UIO',           'Y',    NULL),
(3,  'VITRINA',                                    '3',        'VITRINA',                         'Y',    NULL),
(8,  'BODEGA CUENCA',                              '8',        'BODEGA CUENCA',                   'Y',    NULL),
(9,  'BODEGA CUENCA MAL ESTADO',                   '9',        'BODEGA CUENCA MAL ESTADO',        'Y',    NULL),

-- ── ALMACENES INACTIVOS EN SAP (Inactive='Y') — mapeados pero marcados ───────
-- Zoho podría mandarlos; SAP los rechazará. Se mapean para dar error claro.
(11, 'CUENCA CONSIGNACIONES PROVEEDORES',          '11',       'CUENCA CONSIGNACIONES PROVEEDORES','N',   'Inactivo en SAP OWHS'),
(13, 'CONTROL DE CALIDAD CARCELEN',                '13',       'CONTROL DE CALIDAD CARCELEN',     'N',    'Inactivo en SAP OWHS'),
(16, 'BODEGA MANTA MAL ESTADO',                    '16',       'BODEGA MANTA MAL ESTADO',         'N',    'Inactivo en SAP OWHS'),
(19, 'MANTA CONSIGNACIONES PROVEEDORES',           '19',       'MANTA CONSIGNACIONES PROVEEDORES','N',    'Inactivo en SAP OWHS'),
(4,  'BODEGA CONSIGNACIONES PROVEEDORES',          '4',        'BODEGA CONSIGNACIONES PROVEEDORES','N',   'Inactivo en SAP OWHS'),
(5,  'EXTERNA',                                    '5',        'EXTERNA',                         'N',    'Inactivo en SAP OWHS'),
(6,  'BODEGA CARCELEN',                            '6',        'BODEGA CARCELEN',                 'N',    'Inactivo en SAP OWHS'),
(7,  'BODEGA CONSIGNACIONES CLIENTES',             '7',        'BODEGA CONSIGNACIONES CLIENTES',  'N',    'Inactivo en SAP OWHS'),

-- ── FALLBACK: cuando Zoho no manda warehouseId ────────────────────────────────
(0,  'DEFAULT',                                    '1',        'ALMACÉN GENERAL UIO',             'Y',    'Fallback cuando Zoho no manda almacén');
GO

PRINT '✓ Datos Z_SOHO_AlmacenMap insertados (' + CAST(@@ROWCOUNT AS VARCHAR) + ' registros).';
GO

-- ============================================================
-- VISTA DE MONITOREO — Z_SOHO_OrderMap
-- ============================================================
IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'V_SOHO_Monitor')
    DROP VIEW dbo.V_SOHO_Monitor;
GO

CREATE VIEW dbo.V_SOHO_Monitor AS
SELECT
    ZohoOrderId,
    InstanceId,
    Status,
    SapDocEntry,
    SapDocNum,
    LEFT(ISNULL(ErrorMessage, ''), 300)     AS ErrorResumen,
    ProcessingAt,
    CreatedAt,
    UpdatedAt,
    DATEDIFF(SECOND, ProcessingAt, UpdatedAt) AS TiempoProcSeg
FROM dbo.Z_SOHO_OrderMap;
GO
PRINT '✓ Vista V_SOHO_Monitor creada.';
GO

-- ============================================================
-- VISTA DE MONITOREO — Mapeo de almacenes
-- ============================================================
IF EXISTS (SELECT 1 FROM sys.views WHERE name = 'V_SOHO_AlmacenMap')
    DROP VIEW dbo.V_SOHO_AlmacenMap;
GO

CREATE VIEW dbo.V_SOHO_AlmacenMap AS
SELECT
    ZohoWarehouseId,
    ZohoWarehouseName,
    SapWhsCode,
    SapWhsName,
    Activo,
    Notas
FROM dbo.Z_SOHO_AlmacenMap
ORDER BY ZohoWarehouseId;
GO
PRINT '✓ Vista V_SOHO_AlmacenMap creada.';
GO

-- ============================================================
-- VERIFICACIÓN FINAL
-- ============================================================
PRINT '';
PRINT '=== VERIFICACIÓN FINAL ===';

SELECT 'Z_SOHO_OrderMap'  AS Tabla, COUNT(*) AS Registros FROM dbo.Z_SOHO_OrderMap
UNION ALL
SELECT 'Z_SOHO_AlmacenMap', COUNT(*) FROM dbo.Z_SOHO_AlmacenMap;

PRINT '';
PRINT 'Almacenes activos mapeados:';
SELECT ZohoWarehouseId, ZohoWarehouseName, SapWhsCode, SapWhsName
FROM dbo.Z_SOHO_AlmacenMap WHERE Activo = 'Y' ORDER BY ZohoWarehouseId;

PRINT '';
PRINT '=== SCRIPT COMPLETADO EXITOSAMENTE ===';
GO
