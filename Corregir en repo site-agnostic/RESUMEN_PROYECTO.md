# DicomExtract — resumen del proyecto (para retomar en Claude chat)

## Contexto
Departamento de física de un centro de radioterapia con TPS **Varian Eclipse 13.6**. DicomExtract extrae pacientes (métricas DVH, objetivos de optimización y archivos DICOM RT) para entrenar redes CNN de predicción de dosis. Los DICOM se piden al **DICOM daemon / ARIA** por **C-MOVE**; no hay SCP propio: el File Service de Varian (AE `VMSFD`) recibe los archivos y el programa los recoge de su carpeta. Basado en https://github.com/VarianAPIs/DICOM_Communication_101 (aunque usa EvilDICOM, no fo-dicom).

## Estado actual
Funciona desde otras PCs de la red (probado por el usuario tras registrar el AE en el daemon). Compila `Release|x64` con MSBuild de VS 2022.

### Estructura (`DicomExtract.sln`)
| Proyecto | Qué es |
|---|---|
| `DicomExport.Core` | DLL (net48, AnyCPU, C# 7.3, **sin ESAPI**). Exporta CT/RS/RP/RD (y RI si el llamador da los UIDs) por C-MOVE, enruta cada archivo por `Modality` a una carpeta por tipo, anonimiza opcionalmente. Ver su `README.md` (API y uso). |
| `DicomExtract` | App WPF (WinExe, x64, net48, ESAPI 13.6). `MainWindow.cs` (UI en código, sin XAML), `Extractor.cs` (constraints, métricas D95-norm, objetivos de optimización, orquestación). |
| `lib/ESAPI/13.6` | DLL de ESAPI copiadas al repo (ya no dependen de la unidad Z: ni de Eclipse instalado para compilar). |

### Funcionalidad
- **Proyecto actual: site-agnostic (3 sitios, solo VMAT).** División de trabajo fija: **Python calcula todo lo derivado de dosis/geometría** (D95, normalización, constraints, volúmenes, solapamientos) desde la RTDOSE cruda; **el extractor aporta solo hechos de ARIA** (estado, versiones de plan, técnica, fracciones, PTV por nombre). Por eso el CSV de entrega ya no es de métricas sino de **metadata de plan**.
- UI: login Eclipse, pacientes desde CSV o cargados a mano, casillas independientes: metadata, objetivos, CT, RS, RP, RD, anonimizar; carpeta de destino por tipo (vacío = `{salida}\{sesión}\{paciente}\`); cancelar; log en pantalla. La casilla de métricas DVH (legacy próstata) está oculta salvo `EXTRAER_METRICAS_DVH=true`.
- CSV de entrada: separador `;` o `,` detectado por el encabezado; columnas `HC, Apellido, Curso, Plan` por nombre (fallback a posiciones 0,1,3,4). Formato nuevo de ejemplo: `...\101_Cosas de\PABLO\site agnostic\Listas para minar\Fase 0\ProstataHipo.csv` (encabezado `HC,Apellido,Nombre,Curso,Plan,Estado,Fecha,Equipo,Fx,Dosis/fx (cGy),Dosis total (cGy),Modalidad,Haz`; las columnas extra se ignoran).
- **`metadata_planes.csv`** (entregable; una fila por plan; `;` como separador; **sin HC**, solo `AnonID`): `AnonID, AnioPlan, Status, NombrePTV, NFracciones, DosisTotal_Gy, Tecnica, NArcos, MUs, PTV_D95_Gy_Eclipse, NPlanesCurso, NAprobados, NNoAprobados, NIntermedios, NPlanSums, FlagRevision`. Es para **auditoría y gestión del dataset, no input del modelo**. Detalles:
  - `PTV_D95_Gy_Eclipse`: valor de Eclipse (D95 de `NombrePTV`), solo para que Python lo compare con el suyo (chequeo de higiene de vóxel/contorno).
  - `NombrePTV` / `PTV_D95_Gy_Eclipse` (addendum export 1, CyC): **PTV_High por match EXACTO** (sin distinguir mayúsculas) contra `PTV_CANDIDATOS` de `config.ini` (default `PTV_High-04, PTV_High-0.4, PTV_High!04, PTV_High`, en ese orden de prioridad, con volumen > 0). **Nunca substring ni `TargetVolumeID`** (en el primer export salieron `zRingPTV_High`, `zOptPTV_High!`, con D95 al 34% de Rx). Si ninguno coincide: `NO_ENCONTRADO`, D95 en blanco y `FlagRevision`. `TargetVolumeID` queda solo para el filtro de versiones.
  - **Duplicados de paciente** (mismo HC ⇒ mismo AnonID) en una lista: se conserva el plan **más antiguo** por `PlanSetup.CreationDateTime` (fecha fina; el año empata). Se resuelve **antes de exportar** (los descartados no generan DICOM ni fila de metadata). Siempre se informa en `duplicados_descartados.csv` (AnonID, Curso, Plan, fecha, acción, motivo; las fechas finas viven solo ahí, no en la metadata). Empate o fecha no disponible ⇒ no se adivina: se conservan ambas filas con `FlagRevision` y carpetas DICOM separadas (`{AnonID}__{Plan}`). Filas idénticas (mismo HC+curso+plan) se descartan como repetidas. Tras esto `AnonID` es único por fila.
  - `Tecnica` = valores distintos de `Beam.MLCPlanType` de los campos de tratamiento (`VMAT` si todos lo son; mezclas salen como `A+B`); `NArcos` = campos de tratamiento con `GantryDirection != None`.
  - **Conteo de versiones** (`PlanMetadata.cs`, curso = el del CSV de entrada): no aprobados = planes del curso `UnApproved`/`Rejected` → con prescripción y structure set válidos → misma prescripción (dosis total ±0,01 Gy + fracciones) → mismo structure set por UID → `TargetVolumeID` idéntico. `NAprobados` = 1 si el plan del CSV es `TreatmentApproved` (otros aprobados del curso no entran). `NPlanesCurso = NAprobados + NNoAprobados`. `NIntermedios` = 0 siempre (PlanningApproved/Reviewed no se trackean). `NPlanSums` = plan sums del curso que incluyen el plan. Criterio deliberado: mejor perder un plan que contaminar con otra localización.
  - **Falla ruidosa:** si el `TargetVolumeID` del plan del CSV viene vacío, o el plan no tiene prescripción/structure set, los conteos quedan **en blanco** y `FlagRevision` lo dice. `FlagRevision` también avisa si el plan del CSV no es `TreatmentApproved` o si hubo un error.
- Salida por sesión: `metadata_planes.csv`, `duplicados_descartados.csv` (solo si hubo repetidos), `objetivos_optimizacion.csv`, `mapping_ids.csv` (HC ↔ AnonID, **sensible**, no va a git; se escribe también cuando solo se pide metadata) y, solo con el flag legacy, `metricas_planes_hipofx_D95norm.csv`.
- **Legacy próstata (apagado):** métricas DVH/constraints/`FactorNorm_*`/`s_D95`/`_D95norm`/flags/volúmenes/solapamientos siguen compilados en `Extractor.cs` detrás de `EXTRAER_METRICAS_DVH` (default `false`, en `config.ini`/`config.local.ini`). Hay un comentario de bloque con el porqué, el origen y el uso futuro posible (ML clásico de constraints, p.ej. mama/DIBH). Reactivar = poner `true` y reiniciar. Ese CSV legacy ya no lleva las columnas de conteo de planes.

## Red / multi-PC (decisiones y aprendizajes)
- `VMSFD_PATH` = carpeta donde escribe VMSFD, hoy `\\Eclipse-vm\VMSFD` (share con permisos de lectura, escritura y borrado). `ROOT_EXPORT_PATH` (salidas) puede ser local o UNC.
- **Concurrencia:** ya no se limpia VMSFD. Cada export toma un lock exclusivo (`.export.lock` en VMSFD) y solo procesa los `.dcm` nuevos que aparecen tras su C-MOVE.
- **Config:** `config.ini` compartido junto al exe + override por PC opcional en `%LOCALAPPDATA%\DicomExtract\config.local.ini` (claves que pisa: cualquiera; típico `AE_LOCAL`, `IP_LOCAL`). Claves: `AE_ARIA, IP_DAEMON, PORT_DAEMON, AE_DESTINO, VMSFD_PATH, ROOT_EXPORT_PATH, AE_LOCAL, IP_LOCAL, LOCK_TIMEOUT_SEG`.
- **Lección clave:** el C-MOVE era "no aceptado" desde otras PCs hasta registrar el AE llamante (ej. `EVIL_PC2` con la IP real de la PC) en **dos** lugares: *DICOM Service Daemon* y *DBDicom service* (este último tiene forzado "Permit Trusted Application Entities only"), y reiniciar los servicios. Cada PC cliente debe tener su `config.local.ini` con su AE e IP reales.
- La app corre desde una única copia en carpeta de red (decisión del usuario; funciona en .NET 4.8). Al actualizar, los archivos quedan bloqueados mientras alguien la use.

## Pendiente / ideas
0. **Addendum export 1, puntos 3 y 4 (lado Python, no se tocaron acá):** `harmonization_schema.yaml` / `sites_*.yaml` y el preprocesador viven en el repo de Python. Pendiente allí: ignore `zring*`, `zopt*`; alias de tipeo `ptv_high-0.4`, `ptv_high!04` (y revisar Mid/Low) hacia el candidato `-04`; luego **recalcular el cross-check de D95** con el export corregido (varios de los 15/198 fuera de banda eran artefacto de PTV mal resuelto). Validación abierta (punto 5): falta un paciente con tratamiento simultáneo en otra localización para confirmar que el filtro por `TargetVolumeID` no deja colar rechazados de la otra zona en `NNoAprobados`.
1. **Integración con CalcIndep** (software in-house, WinForms, ESAPI script en 13.6/15.6/18.2 con `.csproj` por versión): decisión tomada = consumir `DicomExport.Core.dll`; **no se tocó CalcIndep** (el usuario lo reestructura desde allí). Opciones por tipo (solo RP, +CT, RS, RI) y carpetas de red distintas ya están soportadas por la API.
2. **RI (RT Images):** ESAPI 13.6 no expone el UID de `Image`; la DLL acepta UIDs si el llamador los obtiene; no probado contra daemon.
3. **Nueva red / site-agnostic:** implementado el ajuste del `PROMPT_code_ajuste_extractor.md` (metadata de plan + legacy detrás de flag + conteo de versiones por cadena de filtros). Sin implementar (fuera de alcance): remapeo consistente de UIDs al anonimizar (hoy **no** remapea), limpieza de tags privados/texto libre, fijar resolución de dosis, guardar `DoseGridScaling`. `Tecnica`/`NArcos` y el conteo no se probaron contra Eclipse real: solo la lógica de filtros con casos sintéticos.
4. Loguear el código de estado del C-MOVE cuando es "no aceptado" (hoy solo avisa) — ofrecido, no pedido.
5. Usar columnas extra del CSV nuevo (Estado, Equipo, Fx, Dosis) para filtrar/validar contra Eclipse — no implementado.
6. Sin pruebas automáticas; verificado: build, parseo de config/override, exclusión mutua del lock, y uso real por el usuario desde otra PC.

## Notas técnicas
- Compilar: `MSBuild.exe DicomExtract.sln -p:Configuration=Release -p:Platform=x64` (VS 2022 Community). LangVersion 7.3 (no usar sintaxis posterior).
- Dependencias: EvilDICOM 3.0.8998.340 y `Microsoft.Extensions.*` 8.0.0 (NuGet en `packages/`).
- ESAPI exige hilo STA: la extracción corre en un `Thread` STA dedicado; la UI se actualiza vía `Dispatcher`.
- Insumos en `Insumos/`: `CalcIndepResumenTecnico.md`, `PROMPT_code_exploracion_extractor.md`, `Prueba.cs` (script ESAPI de objetivos de optimización).
- Cuidado con datos: los CSV de pacientes contienen HC y apellidos reales; no pegarlos en chats.
