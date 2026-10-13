# DicomExport.Core

Librería (.NET Framework 4.8, AnyCPU, C# 7.3, **sin dependencia de ESAPI**) que exporta objetos DICOM RT desde el daemon de ARIA (10.0.1.71) vía **C-MOVE** hacia el File Service **VMSFD**, los recoge de la carpeta VMSFD y los entrega a la carpeta que se indique por tipo (CT, RS, RP, RD, RI), opcionalmente anonimizados.

Pensada para llamarse desde CalcIndep (script ESAPI 13.6 / 15.6 / 18.2) y desde `DicomExtract.exe`.

## Referenciar
Copiar a la solución consumidora (o referenciar el proyecto):
- `DicomExport.Core.dll`
- `EvilDICOM.dll` 3.0.8998.340 y sus dependencias (`Microsoft.Extensions.*` 8.0.0, `System.Memory`, `System.Buffers`, `System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`, `System.Threading.Tasks.Extensions`, `System.ValueTuple`, `System.Diagnostics.DiagnosticSource`, `Microsoft.Bcl.AsyncInterfaces`). Son las que ya copia MSBuild al `bin` de `DicomExtract`.
- Si el consumidor ya trae otra versión de alguna de esas, agregar `bindingRedirect` en su `App.config`/`.dll.config`.

Compilado: `DicomExport.Core\bin\Release\DicomExport.Core.dll`.

## API
```csharp
using DicomExport.Core;

var net = NetworkConfig.Load(@"ruta\config.ini");           // ver config.ini; si no existe, defaults
var exp = new DicomExporter(net, msg => /* log opcional */ );

var uids = new PlanUids {                                     // los arma el llamador desde ESAPI
    RpUid       = plan.UID,
    RsUid       = plan.StructureSet?.UID,
    CtSeriesUid = plan.StructureSet?.Image?.Series?.UID,
    RdUid       = plan.Dose?.UID,
    // RiUids: SOPInstanceUID de RT Images (opcional)
};

var opts = new ExportOptions { RP = true, CT = true, Anonimizar = false };   // cada tipo es independiente
var dest = new ExportDestinos {
    RP = @"\\fisica0\equipo2\DICOM RT\...",                  // carpeta por tipo
    CT = @"\\fisica0\otra\carpeta",
    Default = null                                            // para tipos sin carpeta propia
};

List<string> archivos = exp.Exportar(uids, opts, dest, anonId: null);  // anonId solo si Anonimizar
```
- `Exportar` solo pide lo marcado en `ExportOptions` **y** para lo que haya UID. Devuelve las rutas escritas.
- Lanza excepción si: `VMSFD_PATH` no es accesible, falta carpeta de destino para un tipo pedido (ni `Default`), o vence el lock.
- Los archivos se enrutan por `Modality` leída del DICOM (`CT`, `RTSTRUCT`, `RTPLAN`, `RTDOSE`, `RTIMAGE`).
- **RI**: ESAPI 13.6 no expone el UID de las RT Images (`Image` no tiene `UID`). Si el consumidor obtiene esos UIDs de otra forma, cargarlos en `PlanUids.RiUids` y poner `ExportOptions.RI = true`. No fue probado contra un daemon real.

## Threading
`Exportar` **bloquea** (C-MOVE con timeout 30 s por pedido, espera hasta 120 s por archivos + 5 s de estabilidad, y puede esperar el lock hasta `LOCK_TIMEOUT_SEG`). No llamarlo en el hilo de UI: usar `Task.Run` / `Thread`. No requiere STA.

## Red / varias PCs
- `VMSFD_PATH` debe apuntar a la carpeta donde escribe el File Service. Desde otra PC: ruta UNC compartida (`\\10.0.1.71\VMSFD\`) con permisos de **lectura, escritura y borrado**. El AE `VMSFD` ya está registrado en el daemon con el host de 10.0.1.71; no cambia.
- **Concurrencia**: ya no se limpia VMSFD. Cada export toma un lock exclusivo (`.export.lock` en VMSFD, `FileShare.None`) y solo toma los `.dcm` que aparecen *después* de su C-MOVE. Si otro usuario exporta, espera turno (mensaje en el log). Solo borra subcarpetas vacías.
- `AE_LOCAL` / `IP_LOCAL`: identidad de este equipo ante el daemon (default `EVIL_SCU` / `127.0.0.1`). Si el daemon valida el AE llamante, debe estar dado de alta (a confirmar con el admin de Varian).

## config.ini
Claves: `AE_ARIA`, `IP_DAEMON`, `PORT_DAEMON`, `AE_DESTINO`, `VMSFD_PATH`, `ROOT_EXPORT_PATH`, `AE_LOCAL`, `IP_LOCAL`, `LOCK_TIMEOUT_SEG`. Ejemplo comentado en `DicomExtract\config.ini`.

## Anonimización
Si `Anonimizar = true`: reemplaza/borra los tags de `TAGS_BORRAR` (`DicomExporter.cs`) y pone `anonId` en PatientName/PatientID. **No remapea UIDs** (igual que la versión anterior).

## Límites conocidos
- Sin C-FIND: no verifica que el objeto exista en el daemon; si el C-MOVE falla o no llega nada en 120 s se loguea `[TIMEOUT]` y se devuelve lista vacía (sin excepción).
- Sin pruebas automáticas contra un daemon; probado: build, parseo de config, exclusión mutua del lock.
