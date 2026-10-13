# Resumen Técnico del Proyecto

## 1. Propósito
Aplicación WinForms para apoyar el cálculo independiente de planes de tratamiento radioterápico y la gestión de corrimientos ISO. Verifica planes desde Eclipse TPS, genera archivos de intercambio (`.PATMOVE`, `.PPF`, `.txt`), crea reportes PDF y envía datos a Google Sheets o guarda localmente cuando no hay acceso a Google.

## 2. Tipo de ejecución
- Tipo: standalone WinForms.
- Dependencias clave:
  - `VMS.TPS.Common.Model.API` / `VMS.TPS.Common.Model.Types` (ESAPI de Eclipse TPS)
  - `Google.Apis.Sheets.v4`, `Google.Apis.Auth`
  - `MigraDoc` / `PdfSharp`
  - `Extreme.Numerics` (referenciado en `App.config`)
  - `EvilDICOM` (referenciado en código)

## 3. Entradas
- Paciente y plan seleccionados manualmente en la UI.
- Cursos/planes leídos desde Eclipse TPS vía ESAPI.
- Configuración en `App.config` / Settings:
  - `PathPacientes`, `PathIsos`, `PathCopiaIsos`, `PathPrograma`, carpetas DICOM y DRRs.
- Archivo de equipos remoto:
  - `\\ARIAMEVADB-SVR\va_data$\Calculo Independiente\Equipos.csv`
- Conectividad de red / internet:
  - Resolución de `sheets.googleapis.com` para conectar a Google Sheets.

## 4. Salidas
- Archivos generados:
  - `.PATMOVE`
  - `.txt` de corrimientos
  - `.PPF`
  - `.pdf` de informe CI
  - `.txt` de sesgos CI
  - `.csv` local de corrimientos cuando falla Google
- Escribe en rutas de red UNC definidas en configuración.
- Envía filas a hojas de Google Sheets.
- Muestra diálogos e impresiones al usuario.

## 5. Estructura de clases principales
- `Program`
  - Inicia la aplicación WinForms con `Form1`.
- `Form1`
  - UI principal; abre paciente, lista cursos/planes y lanza cálculos.
- `CargaEnGoogleDrive`
  - Decide si subir a Google Sheets o guardar localmente; maneja credenciales y conexión.
- `crearPatMove`
  - Genera `.PATMOVE`, `.txt` y envía corrimientos; corrige orientación de paciente.
- `crearPPF`
  - Construye archivos `.PPF`; calcula datos de campos e isocentros.
- `Chequeos`
  - Reglas de validación de plan y alertas clínicas.
- `IO`
  - Creación de carpetas y gestión de archivos en red.
- `Equipo`
  - Modelo de equipo con rutas, DICOM ID y configuración por equipo.
- `ReporteCI`
  - Genera, exporta e imprime informes CI con MigraDoc.
- `Estilos`, `VistaPrevia`
  - Apoyo UI y formato del informe.

## 6. Flujo principal
- Usuario abre aplicación y busca paciente por HC.
- Selecciona curso y plan disponible en Eclipse.
- Ejecuta acciones:
  - chequeo de plan
  - cálculo independiente
  - generación de archivos PatMove/PPF
  - generación de informe CI
- Para corrimientos:
  - obtiene lista de isocentros únicos
  - valida si hay desplazamiento
  - si el equipo soporta RefToIso, sube datos a Google Sheets
  - si no, guarda CSV en red
- Para archivos locales:
  - usa rutas definidas por equipo y paciente
  - escribe en carpetas de red

## 7. Integraciones externas
- Eclipse ESAPI (`VMS.TPS.Common.Model.API`)
- Google Sheets API:
  - `sheets.googleapis.com`
  - Spreadsheet IDs:
    - `1Z6dTImEfdJjvmungbXOPxYyRJZL0huVGaEfeoelVVoY`
    - `1HvxYpnQAe3eklrKRYf79mRkSb5R7ThePgOR7kglN-bE`
- Rutas UNC de red:
  - `\\Ariamevadb-svr\va_data$\Pacientes`
  - `\\ARIAMEVADB-SVR\va_data$\Calculo Independiente`
  - `\\FISICA0\isos`
  - `\\fisica0\equipo2\DICOM RT\...`
  - `\\fisica0\equipo3\DICOM RT\...`
  - `\\FISICA0\centro_de_datos2018\000_Centro de Datos 2021\Pacientes DRRs`

## 8. Build multi-versión ESAPI (13.6 / 15.6 / 18.2)

Migrado de un único `.csproj` (contra Eclipse 13.6 vía UNC) a un `.csproj` por versión de Eclipse,
con DLL de ESAPI vendorizadas en `lib/ESAPI/<version>/` (no versionadas en git, ver `.gitignore`).

- `CalcIndep_Carpeta.Eclipse13_6.csproj`, `.Eclipse15_6.csproj`, `.Eclipse18_2.csproj` — mismo árbol de
  fuente, difieren solo en `ProjectGuid`, `OutputPath` (`bin\Eclipse<version>\...`) y `DefineConstants`
  (`ECLIPSE13_6`/`ECLIPSE15_6`/`ECLIPSE18_2`). Plataforma única **x64** (única que soportan las DLL de
  ESAPI; ya no existe configuración `AnyCPU`, ni en los `.csproj` ni en el `.sln`).
- Cada versión genera **2 outputs del mismo código** (mismo `.csproj`, sin tocarlo, vía overrides de
  MSBuild): `CalcIndep_Carpeta.exe` standalone (`Program.Main`, `OutputType=WinExe` default del
  `.csproj`) y `CalcIndep_Carpeta.esapi.dll` plugin ESAPI (`VMS.TPS.Script.Execute(ScriptContext)`,
  override a `OutputType=Library` + `AssemblyName=CalcIndep_Carpeta.esapi`). Cada invocación usa un
  `IntermediateOutputPath` (`obj\`) distinto para que el incremental build de una no borre el output de
  la otra (mismo `bin\`, conviven sin pisarse).
- Build automatizado con `CalcIndep_Carpeta\build.ps1` (hace las 2 invocaciones de MSBuild, siempre
  `-p:Platform=x64`):
  ```
  .\build.ps1 -Version 13_6        # o 15_6 / 18_2, Configuration Debug (default) o Release
  ```
  Equivalente manual (ajustar ruta de MSBuild si cambia la instalación de Visual Studio):
  ```
  MSBuild.exe CalcIndep_Carpeta.Eclipse<version>.csproj -p:Configuration=Debug -p:Platform=x64 -nologo -v:minimal -t:Rebuild -p:IntermediateOutputPath=obj\Eclipse<version>\Debug\exe\
  MSBuild.exe CalcIndep_Carpeta.Eclipse<version>.csproj -p:Configuration=Debug -p:Platform=x64 -nologo -v:minimal -t:Rebuild -p:OutputType=Library -p:AssemblyName=CalcIndep_Carpeta.esapi -p:IntermediateOutputPath=obj\Eclipse<version>\Debug\dll\
  ```
- Breaking change encontrado entre 13.6 y 15.6/18.2: `PlanSetup.UniqueFractionation` (con
  `.NumberOfFractions`/`.PrescribedDosePerFraction`) desapareció; las mismas 2 propiedades quedaron
  aplanadas directo en `PlanSetup`. Aislado en `EsapiCompat.cs`
  (`GetNumberOfFractions()`/`GetPrescribedDosePerFraction()`), con `#if ECLIPSE13_6/#else`. Call-sites
  actualizados en `Chequeos.cs`, `crearInforme.cs`, `crearInformeSuma.cs`, `crearPPF.cs`, `Form1.cs`.
- Resultado de compilar (2026-09-16): 13.6, 15.6 y 18.2 compilan sin errores, en x64, EXE y DLL — solo
  warnings preexistentes (variables sin usar, miembros obsoletos de ESAPI; los warnings de arquitectura
  MSIL/AMD64 que había con `AnyCPU` desaparecieron al pasar a x64). El `VMS.TPS.Common.Model.Interface.dll`
  real de 18.2 (v1.0.700.249, confirmado con reflection) ya está vendorizado en `lib/ESAPI/18.2/`
  (reemplazó al archivo copiado antes por error, `VMS.TPS.PlanningModelLibrary.Interface.dll`, que era
  otra librería distinta y no servía).
- No se pudo correr dentro de un Eclipse real de ninguna versión (no hay esa instalación en este
  entorno) — validado solo por compilación, pendiente de prueba en vivo.
- `.sln` actualizado con los 3 proyectos y plataforma `x64`.

## 9. TODOs o limitaciones conocidas
- Código comentado en varios bloques:
  - lógica de asignación de equipos alternativos
  - verificación de conectividad con `Ping`
  - manejo parcial de `PlanSum` y `isosListos`
- Comentarios explícitos:
  - `//FALTA SRS!!!!!!!!!!!!!!!!!!`
  - `//header[9] += ""; //completar`
  - `//header[10] += ""; //completar`
- Fallback de conectividad:
  - si está en Citrix o no hay acceso a Google, guarda localmente en `Corrimientos`
- Credenciales Google asumen archivos `credentials.json` y `token.json` en red.
