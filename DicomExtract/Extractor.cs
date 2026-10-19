using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DicomExport.Core;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace DicomExtract
{
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Tipos de métrica de DVH soportados.</summary>
    internal enum MetricType { VolumeAtDose, DoseAtVolumePct, DoseAtVolumeCc, Dmean, Dmax }

    /// <summary>Un constraint individual (métrica + operador + umbral).</summary>
    internal class Constraint
    {
        public MetricType Type { get; set; }
        public double Param { get; set; }   // dosis (Gy) para Vx%; vol (%) o (cc) para Dx
        public string Operator { get; set; }   // "<" o ">"
        public double Threshold { get; set; }   // valor límite

        // Descripción legible para encabezado CSV
        public string Label { get; set; }

        /// <summary>Evalúa si el valor calculado cumple el constraint.</summary>
        public bool? Evaluate(double value)
        {
            if (double.IsNaN(value)) return null;
            return Operator == "<" ? value < Threshold : value > Threshold;
        }
    }

    /// <summary>Configuración completa leída desde el archivo de constraints.</summary>
    internal class ConstraintConfig
    {
        public double PrescDosisGy { get; set; } = double.NaN;
        public int NFracciones { get; set; } = 0;

        // Alias de estructuras: clave = nombre principal, valor = lista de aliases
        public Dictionary<string, List<string>> EstructuraAliases { get; set; }
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Overrides puntuales de PTV por paciente: clave = HC, valor = Id exacto de estructura
        public Dictionary<string, string> PtvOverrides { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Constraints por estructura: clave = nombre principal de la sección
        public Dictionary<string, List<Constraint>> Constraints { get; set; }
            = new Dictionary<string, List<Constraint>>(StringComparer.OrdinalIgnoreCase);

        // ─── Parser ──────────────────────────────────────────────────────────

        public static ConstraintConfig Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"No se encontró el archivo de constraints: {path}");

            var cfg = new ConstraintConfig();
            string currentSection = null;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                // Sección: [NombreSeccion]
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                if (currentSection == null) continue;

                switch (currentSection.ToLower())
                {
                    case "prescripcion":
                    case "estructuras":
                    case "ptv_overrides":
                        {
                            // Estas secciones usan formato clave = valor
                            int eqIdx = line.IndexOf('=');
                            if (eqIdx < 0) continue;
                            string key = line.Substring(0, eqIdx).Trim();
                            string val = line.Substring(eqIdx + 1).Split('#')[0].Trim();
                            switch (currentSection.ToLower())
                            {
                                case "prescripcion":  ParsePrescripcion(cfg, key, val); break;
                                case "estructuras":   ParseEstructura(cfg, key, val);   break;
                                case "ptv_overrides": cfg.PtvOverrides[key] = val;      break;
                            }
                            break;
                        }
                    default:
                        // Secciones de estructura: líneas tipo "V%50Gy < 50.0" (sin =)
                        ParseConstraintLine(cfg, currentSection, line);
                        break;
                }
            }

            return cfg;
        }

        private static void ParsePrescripcion(ConstraintConfig cfg, string key, string val)
        {
            if (key.Equals("DosisTotal_Gy", StringComparison.OrdinalIgnoreCase))
                if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
                    cfg.PrescDosisGy = d;

            if (key.Equals("NFracciones", StringComparison.OrdinalIgnoreCase))
                if (int.TryParse(val, out int n))
                    cfg.NFracciones = n;
        }

        private static void ParseEstructura(ConstraintConfig cfg, string key, string val)
        {
            var aliases = val.Split(',')
                             .Select(a => a.Trim())
                             .Where(a => !string.IsNullOrEmpty(a))
                             .ToList();
            cfg.EstructuraAliases[key] = aliases;
        }

        private static void ParseConstraintLine(ConstraintConfig cfg, string section, string line)
        {
            // Formato: "{metrica} {op} {valor}"  ej: "V%50Gy < 50.0", "Dmean < 40.0"
            string opStr;
            int opIdx;

            if ((opIdx = line.IndexOf('<')) >= 0) opStr = "<";
            else if ((opIdx = line.IndexOf('>')) >= 0) opStr = ">";
            else return; // línea malformada

            string key = line.Substring(0, opIdx).Trim();
            string threshStr = line.Substring(opIdx + 1).Split('#')[0].Trim();

            if (!double.TryParse(threshStr,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double threshold))
                return;

            var c = ParseMetric(key, opStr, threshold);
            if (c == null) return;

            if (!cfg.Constraints.ContainsKey(section))
                cfg.Constraints[section] = new List<Constraint>();
            cfg.Constraints[section].Add(c);
        }

        // ─── Parser de la métrica ─────────────────────────────────────────────
        // Formatos soportados:
        //   V%{dosis}Gy   → VolumeAtDose     (param = dosis en Gy)
        //   DGy{vol}%     → DoseAtVolumePct   (param = volumen en %)
        //   DGy{vol}cc    → DoseAtVolumeCc    (param = volumen en cc)
        //   Dmean         → Dmean
        //   Dmax          → Dmax
        private static Constraint ParseMetric(string key, string op, double threshold)
        {
            key = key.Trim();

            // V%{X}Gy
            var mV = Regex.Match(key, @"^V%(\d+(?:\.\d+)?)Gy$", RegexOptions.IgnoreCase);
            if (mV.Success)
                return new Constraint
                {
                    Type = MetricType.VolumeAtDose,
                    Param = double.Parse(mV.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Operator = op,
                    Threshold = threshold,
                    Label = $"V{mV.Groups[1].Value}Gy_{(op == "<" ? "lt" : "gt")}{threshold}"
                };

            // DGy{X}%
            var mDpct = Regex.Match(key, @"^DGy(\d+(?:\.\d+)?)%$", RegexOptions.IgnoreCase);
            if (mDpct.Success)
                return new Constraint
                {
                    Type = MetricType.DoseAtVolumePct,
                    Param = double.Parse(mDpct.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Operator = op,
                    Threshold = threshold,
                    Label = $"DGy{mDpct.Groups[1].Value}pct_{(op == "<" ? "lt" : "gt")}{threshold}"
                };

            // DGy{X}cc
            var mDcc = Regex.Match(key, @"^DGy(\d+(?:\.\d+)?)cc$", RegexOptions.IgnoreCase);
            if (mDcc.Success)
                return new Constraint
                {
                    Type = MetricType.DoseAtVolumeCc,
                    Param = double.Parse(mDcc.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Operator = op,
                    Threshold = threshold,
                    Label = $"DGy{mDcc.Groups[1].Value}cc_{(op == "<" ? "lt" : "gt")}{threshold}"
                };

            // Dmean / Dmax
            if (key.Equals("Dmean", StringComparison.OrdinalIgnoreCase))
                return new Constraint { Type = MetricType.Dmean, Operator = op, Threshold = threshold, Label = $"Dmean_{(op == "<" ? "lt" : "gt")}{threshold}" };
            if (key.Equals("Dmax", StringComparison.OrdinalIgnoreCase))
                return new Constraint { Type = MetricType.Dmax, Operator = op, Threshold = threshold, Label = $"Dmax_{(op == "<" ? "lt" : "gt")}{threshold}" };

            Console.WriteLine($"[WARN] Métrica no reconocida: '{key}' — ignorada.");
            return null;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parámetros de una corrida (los arma la UI)
    // ═════════════════════════════════════════════════════════════════════════
    internal class PacienteRow
    {
        public string Hc { get; set; }
        public string Apellido { get; set; }
        public string Curso { get; set; }
        public string Plan { get; set; }
    }

    internal class RunOptions
    {
        public List<PacienteRow> Pacientes { get; set; } = new List<PacienteRow>();
        public string Usuario { get; set; }
        public string Password { get; set; }
        public string ConstraintsPath { get; set; }
        /// <summary>Carpeta raíz de salida; si es null se usa ROOT_EXPORT_PATH del config.ini.</summary>
        public string CarpetaSalida { get; set; }
        public string NombreSesion { get; set; }
        /// <summary>Metadata de plan (CSV de auditoría/dataset). Es el entregable de este proyecto.</summary>
        public bool Metadata { get; set; }
        /// <summary>Sitio de la sesión de minado (CyC, Pelvis, Prostata): parámetro, no se infiere de la dosis.</summary>
        public string Sitio { get; set; }
        /// <summary>Opción extra (próstata): métricas DVH vs constraints con normalización D95. Requiere ConstraintsPath.</summary>
        public bool Metricas { get; set; }
        public bool Objetivos { get; set; }
        /// <summary>Qué DICOM exportar (CT/RS/RP/RD + anonimizar). Null o !Any = no exportar.</summary>
        public ExportOptions Dicom { get; set; }
        /// <summary>Carpetas por tipo; las vacías caen a {sesión}\{paciente}\.</summary>
        public ExportDestinos Destinos { get; set; } = new ExportDestinos();
        /// <summary>Se consulta entre pacientes; true = detener.</summary>
        public Func<bool> Cancelar { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Extracción (métricas, objetivos, DICOM vía DicomExport.Core)
    // ═════════════════════════════════════════════════════════════════════════
    internal static class Extractor
    {
        private static NetworkConfig _net;
        private static ConstraintConfig _constraints;
        private static Action<string> Log = s => { };

        // ─── Acumuladores para el resumen de renormalización D95 ──────────────
        private static int _totalFlagsCambiados = 0;
        private static readonly List<string> _casosCambiados = new List<string>();
        private static readonly List<string> _casosPtvSospechoso = new List<string>();

        /// <summary>Corre el lote completo. Devuelve la carpeta de sesión. Bloqueante: llamar desde un hilo de fondo.</summary>
        public static string Run(NetworkConfig net, RunOptions o, Action<string> log)
        {
            _net = net;
            Log = log ?? (s => { });
            _totalFlagsCambiados = 0; _casosCambiados.Clear(); _casosPtvSospechoso.Clear();

            bool dicom = o.Dicom != null && o.Dicom.Any;
            bool legacy = o.Metricas;
            if (!legacy && !o.Metadata && !o.Objetivos && !dicom) throw new Exception("Nada seleccionado para extraer");
            if (o.Metadata && string.IsNullOrWhiteSpace(o.Sitio)) throw new Exception("Falta el sitio (CyC, Pelvis o Prostata) para la metadata");

            if (legacy)
            {
                _constraints = ConstraintConfig.Load(o.ConstraintsPath);
                Log($"[OK] Constraints: {Path.GetFileName(o.ConstraintsPath)} — {_constraints.PrescDosisGy} Gy / {_constraints.NFracciones} fx; " +
                    $"estructuras: {string.Join(", ", _constraints.EstructuraAliases.Keys)}");
            }

            string root = string.IsNullOrWhiteSpace(o.CarpetaSalida) ? _net.RootExportPath : o.CarpetaSalida;
            string sessionPath = Path.Combine(root, o.NombreSesion ?? $"{System.DateTime.Now:yyyyMMdd_HHmm}");
            Directory.CreateDirectory(sessionPath);

            string mappingPath = Path.Combine(sessionPath, "mapping_ids.csv");
            string metadataCsvPath = Path.Combine(sessionPath, "metadata_planes.csv");
            string metricasCsvPath = Path.Combine(sessionPath, "metricas_planes_hipofx_D95norm.csv");
            string objetivosCsvPath = Path.Combine(sessionPath, "objetivos_optimizacion.csv");
            // El HC vive solo en el mapping: el CSV de metadata se cruza con el paciente por AnonID.
            if ((dicom && o.Dicom.Anonimizar) || o.Metadata) File.WriteAllText(mappingPath, "AnonID;HC;Apellido\n");
            if (o.Metadata) File.WriteAllText(metadataCsvPath, string.Join(";", PlanMetadata.Columnas) + "\n");
            if (legacy) EscribirEncabezadoMetricas(metricasCsvPath);
            if (o.Objetivos) EscribirEncabezadoObjetivos(objetivosCsvPath);

            using (var app = VMS.TPS.Common.Model.API.Application.CreateApplication(o.Usuario, o.Password))
            {
                ProcesarLote(app, o, sessionPath, mappingPath, metadataCsvPath, metricasCsvPath, objetivosCsvPath);
            }
            return sessionPath;
        }

        // ─── Procesamiento por lotes ──────────────────────────────────────────
        private static void ProcesarLote(
            VMS.TPS.Common.Model.API.Application app, RunOptions o,
            string sessionPath, string mappingPath, string metadataCsvPath, string metricasCsvPath, string objetivosCsvPath)
        {
            bool legacy = o.Metricas;
            bool incluirDicom = o.Dicom != null && o.Dicom.Any;
            bool anonimizar = incluirDicom && o.Dicom.Anonimizar;
            var exporter = new DicomExporter(_net, Log);
            int total = 0, ok = 0, err = 0;

            // Un paciente (misma patología) no puede quedar dos veces: se conserva el plan más antiguo.
            HashSet<int> empates;
            HashSet<int> descartados = ResolverDuplicados(app, o, sessionPath, out empates);

            for (int idx = 0; idx < o.Pacientes.Count; idx++)
            {
                if (descartados.Contains(idx)) continue;
                PacienteRow row = o.Pacientes[idx];
                if (o.Cancelar != null && o.Cancelar()) { Log("\n[CANCELADO por el usuario]"); break; }

                string hc = row.Hc, apellido = row.Apellido, cursoId = row.Curso, planId = row.Plan;
                total++;

                string anonId = GenerarAnonId(hc);
                string carpetaPaciente = !incluirDicom ? null
                    : (anonimizar ? Path.Combine(sessionPath, anonId)
                                  : Path.Combine(sessionPath, $"{hc}_{apellido}"));
                // Empate de fecha: ambas filas quedan; carpetas separadas para no mezclar los DICOM de los dos planes
                if (empates.Contains(idx) && carpetaPaciente != null)
                    carpetaPaciente += "__" + NombreSeguro(planId);

                Log($"\n[{total}/{o.Pacientes.Count - descartados.Count}] {hc} ({apellido}) → Curso: {cursoId} / Plan: {planId}");

                try
                {
                    app.ClosePatient();
                    Patient patient = app.OpenPatientById(hc);
                    if (patient == null) throw new Exception("Paciente no encontrado en Eclipse");

                    Course course = BuscarCurso(patient, cursoId) ?? throw new Exception($"Curso '{cursoId}' no encontrado");
                    PlanSetup plan = BuscarPlan(course, planId) ?? throw new Exception($"Plan '{planId}' en curso '{cursoId}' no encontrado");

                    if (incluirDicom)
                    {
                        Directory.CreateDirectory(carpetaPaciente);
                        ExportarDicom(exporter, plan, o, carpetaPaciente, anonId);
                    }

                    if (o.Metadata)
                        ExtraerYGuardarMetadata(plan, course, anonId, hc, metadataCsvPath, o.Sitio,
                            empates.Contains(idx) ? "AnonID repetido: fecha de creación empatada o no disponible, resolver manualmente" : null);

                    if (legacy)
                        ExtraerYGuardarMetricas(plan, anonId, hc, metricasCsvPath);

                    if (o.Objetivos)
                        ExtraerObjetivosOptimizacion(plan, anonId, hc, objetivosCsvPath);

                    if (anonimizar || o.Metadata)
                        File.AppendAllText(mappingPath, $"{anonId};{hc};{apellido}\n");

                    app.ClosePatient();
                    ok++;
                    Log("   ✓ OK");
                }
                catch (Exception ex)
                {
                    Log($"   ✗ ERROR: {ex.Message}");
                    if (incluirDicom && carpetaPaciente != null)
                        Directory.CreateDirectory(carpetaPaciente + "_ERROR");
                    err++;
                    try { app.ClosePatient(); } catch { }
                }
            }

            Log($"\n=== Resumen: {total} pacientes — {ok} OK, {err} errores ===");
            if (anonimizar || o.Metadata) Log($"Mapeo guardado en: {mappingPath}  (sensible: contiene HC)");
            if (o.Metadata) Log($"Metadata de planes guardada en: {metadataCsvPath}");
            if (legacy) Log($"Métricas guardadas en: {metricasCsvPath}");
            if (o.Objetivos) Log($"Objetivos de optimización guardados en: {objetivosCsvPath}");

            if (legacy) ImprimirResumenD95Norm();
        }

        // ─── Exportación DICOM (delegada a DicomExport.Core) ──────────────────
        private static void ExportarDicom(DicomExporter exporter, PlanSetup plan, RunOptions o, string carpetaPaciente, string anonId)
        {
            var uids = new PlanUids
            {
                RpUid = plan.UID,
                RsUid = plan.StructureSet?.UID,
                CtSeriesUid = plan.StructureSet?.Image?.Series?.UID,
                RdUid = plan.Dose?.UID,
            };
            // RI: ESAPI 13.6 no expone el UID de las RT Images; solo disponible vía DicomExport.Core si el llamador los tiene.

            // Carpeta por tipo si la dio el usuario; si no, {sesión}\{paciente}
            var d = new ExportDestinos
            {
                Default = carpetaPaciente,
                CT = o.Destinos.CT, RS = o.Destinos.RS, RP = o.Destinos.RP, RD = o.Destinos.RD
            };
            exporter.Exportar(uids, o.Dicom, d, anonId);
        }

        // ─── Duplicados de paciente: quedarse con el plan más antiguo, sin descartar en silencio ─────
        // anonId = hash del HC → "mismo AnonID" ≡ "mismo HC" dentro de la lista. Los planes descartados no se exportan
        // (ni DICOM ni metadata). Se informa en duplicados_descartados.csv. Empate o fecha no disponible → ambas filas
        // se conservan con FlagRevision (no se adivina).
        private static HashSet<int> ResolverDuplicados(VMS.TPS.Common.Model.API.Application app, RunOptions o,
            string sessionPath, out HashSet<int> empates)
        {
            var descartados = new HashSet<int>();
            empates = new HashSet<int>();
            var filas = new List<string>();

            var grupos = Enumerable.Range(0, o.Pacientes.Count)
                .GroupBy(i => (o.Pacientes[i].Hc ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Key.Length > 0 && g.Count() > 1);

            foreach (var g in grupos)
            {
                string anonId = GenerarAnonId(g.Key);
                // 1) filas idénticas (mismo curso y plan): se queda la primera
                var unicos = new List<int>();
                foreach (int i in g)
                {
                    var r = o.Pacientes[i];
                    if (unicos.Any(j => SameRow(o, j, i)))
                    {
                        descartados.Add(i);
                        filas.Add(string.Join(";", anonId, r.Curso, r.Plan, "", "descartado", "fila repetida en la lista"));
                    }
                    else unicos.Add(i);
                }
                if (unicos.Count < 2) continue;

                // 2) fecha de creación de cada plan (ESAPI)
                var fechas = new List<DateTime?>();
                foreach (int i in unicos) fechas.Add(FechaCreacion(app, o.Pacientes[i]));

                int? keep = PlanMetadata.MasAntiguo(fechas);
                for (int k = 0; k < unicos.Count; k++)
                {
                    int i = unicos[k]; var r = o.Pacientes[i];
                    string f = fechas[k].HasValue ? fechas[k].Value.ToString("yyyy-MM-dd HH:mm:ss") : "";
                    string accion, motivo;
                    if (keep.HasValue)
                    {
                        if (k == keep.Value) { accion = "conservado"; motivo = "plan más antiguo"; }
                        else { accion = "descartado"; motivo = "hay un plan más antiguo del mismo paciente"; descartados.Add(i); }
                    }
                    else { accion = "conservado_REVISAR"; motivo = "fecha de creación empatada o no disponible"; empates.Add(i); }
                    filas.Add(string.Join(";", anonId, r.Curso, r.Plan, f, accion, motivo));
                }
            }

            if (filas.Count > 0)
            {
                string path = Path.Combine(sessionPath, "duplicados_descartados.csv");
                File.WriteAllText(path, "AnonID;Curso;Plan;FechaCreacionPlan;Accion;Motivo\n" + string.Join("\n", filas) + "\n");
                Log($"[DUPLICADOS] {descartados.Count} fila(s) descartada(s), {empates.Count} en revisión manual → {path}");
            }
            return descartados;
        }

        private static bool SameRow(RunOptions o, int a, int b)
        {
            return string.Equals(o.Pacientes[a].Curso, o.Pacientes[b].Curso, StringComparison.OrdinalIgnoreCase)
                && string.Equals(o.Pacientes[a].Plan, o.Pacientes[b].Plan, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Fecha de creación del plan (ESAPI CreationDateTime); null si no se puede leer.</summary>
        private static DateTime? FechaCreacion(VMS.TPS.Common.Model.API.Application app, PacienteRow row)
        {
            try
            {
                app.ClosePatient();
                Patient pat = app.OpenPatientById(row.Hc);
                if (pat == null) return null;
                Course c = BuscarCurso(pat, row.Curso);
                PlanSetup p = c == null ? null : BuscarPlan(c, row.Plan);
                return p?.CreationDateTime;
            }
            catch { return null; }
            finally { try { app.ClosePatient(); } catch { } }
        }

        private static string NombreSeguro(string s)
        {
            foreach (char ch in Path.GetInvalidFileNameChars()) s = (s ?? "").Replace(ch, '_');
            return s;
        }

        // ─── Metadata de plan (CSV de auditoría/gestión del dataset; NO es input del modelo) ─────
        // Python recalcula D95/normalización/constraints desde la RTDOSE cruda. Acá solo hechos de ARIA.
        // Una fila por plan; sin HC (el cruce paciente↔metadata es por AnonID contra mapping_ids.csv).
        private static void ExtraerYGuardarMetadata(PlanSetup plan, Course course, string anonId, string hc, string csvPath, string sitio, string flagExtra = null)
        {
            string[] fila = new string[PlanMetadata.Columnas.Length];
            for (int i = 0; i < fila.Length; i++) fila[i] = "";
            Action<string, string> set = (col, v) => fila[Array.IndexOf(PlanMetadata.Columnas, col)] = v;
            set("AnonID", anonId);
            set("Sitio", sitio);
            var flags = new List<string>();
            if (flagExtra != null) flags.Add(flagExtra);

            try
            {
                PlanInfo info = PlanMetadata.Info(plan);
                set("AnioPlan", ExtraerAnio(plan.PlanningApprovalDate ?? ""));
                set("Status", info.Status);
                set("NFracciones", info.NFx > 0 ? info.NFx.ToString() : "");
                set("DosisTotal_Gy", F(info.DosisGy));
                set("Tecnica", PlanMetadata.Tecnica(plan));
                set("NArcos", PlanMetadata.NArcos(plan).ToString());
                set("MUs", F(plan.Beams.Where(b => !b.IsSetupField).Sum(b => b.Meterset.Value)));

                // PTV_High: nombre normalizado (ver PlanMetadata.NivelPtv; -04 con prioridad sobre el original), volumen > 0.
                // Nunca por substring ni por TargetVolumeID (puede apuntar a zRing*/zOpt*): ambiguo/desconocido → FlagRevision.
                Structure ptv = null;
                bool cropped = false;
                string motivoPtv = "PTV_High no resuelto: plan sin structure set";
                if (plan.StructureSet != null)
                {
                    string ptvId = PlanMetadata.ElegirPtv(
                        plan.StructureSet.Structures.Select(s => new KeyValuePair<string, double>(s.Id, s.IsEmpty ? 0 : s.Volume)),
                        out cropped, out motivoPtv);
                    if (ptvId != null) ptv = plan.StructureSet.Structures.First(s => s.Id == ptvId);
                }
                if (ptv == null) flags.Add(motivoPtv);
                set("NombrePTV", ptv?.Id ?? "NO_ENCONTRADO");                       // nombre crudo (auditoría)
                // Canónico, solo para cruzar por igualdad de strings con lo que resuelve Python (no es fuente de verdad).
                set("PTV_Resuelto", ptv == null ? "" : (cropped ? "PTV_High-04" : "PTV_High"));
                // Solo auditoría: Python calcula su propio D95 y lo compara con este valor de Eclipse.
                set("PTV_D95_Gy_Eclipse", F(ptv != null && plan.Dose != null ? ObtenerDoseAtVolumePct(plan, ptv, 95.0) : double.NaN));

                // Plan sums que incluyen el plan: solo se flaggea para revisión manual (la fila no se excluye).
                int nSums = course.PlanSums.Count(ps => ps.PlanSetups.Any(p => p.UID == plan.UID));
                set("NPlanSums", nSums.ToString());
                if (nSums > 0) flags.Add("plan sum en el curso");

                // Conteo de versiones del tratamiento (cadena de filtros en PlanMetadata)
                var bloqueos = PlanMetadata.Bloqueos(info);
                if (info.Status != PlanMetadata.APROBADO)
                    flags.Add($"plan del CSV no es TreatmentApproved ({info.Status})");
                // TargetVolumeID presente pero que no normaliza a PTV_High (ni es alias): se informa con su nombre crudo.
                bool tvCropped;
                if (!string.IsNullOrEmpty(info.TargetVolumeId) && PlanMetadata.ResolverNombrePtv(info.TargetVolumeId, out tvCropped) != "high")
                    flags.Add($"TargetVolumeID '{info.TargetVolumeId}' no normaliza a PTV_High");
                // Sin TargetVolumeID (o plan sin prescripción/structure set) no se buscan planes en el curso:
                // los conteos quedan VACÍOS = "no calculado" (no 0).
                if (bloqueos.Count > 0) flags.AddRange(bloqueos);
                else
                {
                    var otros = course.PlanSetups.Where(p => p.UID != plan.UID).Select(PlanMetadata.Info).ToList();
                    int nAprobados = info.Status == PlanMetadata.APROBADO ? 1 : 0;
                    int nNoAprobados = PlanMetadata.ContarNoAprobados(info, otros);
                    set("NPlanesCurso", (nAprobados + nNoAprobados).ToString());
                    set("NAprobados", nAprobados.ToString());
                    set("NNoAprobados", nNoAprobados.ToString());
                    set("NIntermedios", "0"); // PlanningApproved no se trackea (no sobrevive con un TreatmentApproved)
                }
                Log("   ✓ Metadata extraída");
            }
            catch (Exception ex)
            {
                flags.Add("error: " + ex.Message);
                Log($"   [WARN] No se pudo extraer metadata: {ex.Message}");
            }

            set("FlagRevision", string.Join(" | ", flags).Replace(";", ",").Replace("\r", " ").Replace("\n", " "));
            File.AppendAllText(csvPath, string.Join(";", fila) + "\n");
        }

        // ─── Resumen por consola: cambios de flag por renormalización D95 ─────
        private static void ImprimirResumenD95Norm()
        {
            Log("\n=== Renormalización D95(PTV)=Presc. de referencia ===");
            Log($"Flags que cambian (entregado vs. D95norm): {_totalFlagsCambiados}");

            if (_casosCambiados.Count > 0)
            {
                Log("Casos con al menos un flag distinto:");
                foreach (string linea in _casosCambiados)
                    Log($"  {linea}");
            }
            else
            {
                Log("Ningún caso cambió de flag con la renormalización.");
            }

            if (_casosPtvSospechoso.Count > 0)
            {
                Log("\n[ALERTA] Casos con D95(PTV) entregado sospechosamente bajo");
                Log("(posible estructura PTV incorrecta incluso tras el fix de prioridad):");
                foreach (string linea in _casosPtvSospechoso)
                    Log($"  {linea}");
            }
        }

        // ─── Búsqueda de plan ─────────────────────────────────────────────────
        private static Course BuscarCurso(Patient patient, string cursoId)
        {
            return patient.Courses.FirstOrDefault(c =>
                c.Id.Equals(cursoId, StringComparison.OrdinalIgnoreCase));
        }

        private static PlanSetup BuscarPlan(Course curso, string planId)
        {
            return curso.PlanSetups.FirstOrDefault(p =>
                p.Id.Equals(planId, StringComparison.OrdinalIgnoreCase));
        }

        // ─── Búsqueda flexible de estructura por aliases ──────────────────────
        private static Structure BuscarEstructura(StructureSet ss, string nombrePrincipal)
        {
            if (!_constraints.EstructuraAliases.TryGetValue(nombrePrincipal, out var aliases))
                return null;

            foreach (string alias in aliases)
            {
                var s = ss.Structures.FirstOrDefault(e =>
                    e.Id.Equals(alias, StringComparison.OrdinalIgnoreCase));
                if (s != null) return s;
            }
            return null;
        }

        // ─── Búsqueda priorizada del PTV de prescripción ───────────────────────
        /// <summary>
        /// Busca la estructura PTV a usar para D95/D98/factor de normalización/renormalización.
        /// ORDEN DE RESOLUCIÓN:
        ///   1) Override explícito por HC en [PTV_Overrides] del archivo de constraints —
        ///      para casos puntuales donde la heurística de alias no alcanza (ej. coexisten
        ///      dos estructuras con nombre plausible y la que aparece primero es la incorrecta).
        ///   2) Prioridad por alias: los alias específicos (PTV_High, PTV_High2, PTV_Abr22,
        ///      PTVp, PTV_Ptta, etc.) tienen prioridad sobre el alias genérico "PTV" bare, sin
        ///      importar en qué posición esté listado en el archivo de constraints. Motivo: en
        ///      la práctica "PTV" a secas suele ser un volumen auxiliar (suma, versión previa,
        ///      plantilla) y NO el blanco real de prescripción cuando coexiste con un PTV
        ///      específico.
        /// Si solo existe el alias genérico "PTV" en el paciente, se usa ese igual (fallback).
        /// </summary>
        private static Structure BuscarEstructuraPTVPrescripcion(StructureSet ss, string hc, out string aliasUsado)
        {
            aliasUsado = null;

            // Override explícito por paciente (HC) — bypassa la búsqueda por alias.
            if (_constraints.PtvOverrides.TryGetValue(hc, out string idOverride))
            {
                var sOverride = ss.Structures.FirstOrDefault(e =>
                    e.Id.Equals(idOverride, StringComparison.OrdinalIgnoreCase));
                if (sOverride != null)
                {
                    aliasUsado = $"OVERRIDE:{idOverride}";
                    return sOverride;
                }
                Log($"   [WARN] Override PTV_Overrides='{idOverride}' para HC={hc} " +
                                   "no encontrado en el StructureSet — cae a búsqueda por alias.");
            }

            if (!_constraints.EstructuraAliases.TryGetValue("PTV", out var aliasesDeclarados))
                return null;

            // Reordenar: todos los alias específicos primero (orden declarado preservado),
            // el alias bare "PTV" al final. OrderBy es estable → no reordena entre sí los
            // que comparten la misma prioridad.
            var prioridad = aliasesDeclarados
                .OrderBy(a => a.Equals("PTV", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToList();

            foreach (string alias in prioridad)
            {
                var s = ss.Structures.FirstOrDefault(e =>
                    e.Id.Equals(alias, StringComparison.OrdinalIgnoreCase));
                if (s != null) { aliasUsado = alias; return s; }
            }
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════
        // MÉTRICAS DVH + NORMALIZACIÓN D95 (opción extra, origen: proyecto de próstata)
        //
        // Se activa desde la UI ("Métricas DVH + normalización D95") eligiendo un constraints_*.txt
        // (p.ej. constraints_prostata_hipo.txt). Está APAGADA por defecto. Produce metricas_planes_hipofx_D95norm.csv:
        // métricas por constraint (V%XGy, DGyX%, DGyXcc, Dmean, Dmax), flags de cumplimiento, FactorNorm_D95/D98,
        // s_D95, familia _D95norm (renormalización lineal a D95(PTV)=Rx de referencia), volúmenes y solapamientos
        // PTV–OAR. OJO: ese CSV lleva HC (es de uso interno; nunca a git ni a datasets compartidos).
        //
        // POR QUÉ NO ES LA SALIDA DEL PROYECTO site-agnostic: ahí el preprocesador Python recalcula D95,
        // normalización, constraints, volúmenes y solapamientos desde la RTDOSE cruda; extraerlos de ESAPI mezclaría
        // dos fuentes de verdad (grilla de Eclipse vs grilla nativa). Para eso se usa metadata_planes.csv.
        // Sirve para proyectos donde el CSV de métricas es dato (próstata, ML clásico de constraints, mama/DIBH).
        // Alcance del bloque: ExtraerYGuardarMetricas, ConstruirEncabezadoColumnas, EscribirEncabezadoMetricas,
        // EvaluarMetrica y helpers DVH/geometría, ImprimirResumenD95Norm, ConstraintConfig.
        // ═════════════════════════════════════════════════════════════════════
        // ─── Extracción de métricas ───────────────────────────────────────────
        private static void ExtraerYGuardarMetricas(PlanSetup plan, string anonId, string hc, string csvPath)
        {
            try
            {
                var ss = plan.StructureSet ?? throw new Exception("Sin StructureSet");

                // Buscar estructuras usando aliases del config (Body/Rectum/Bladder: sin cambios)
                var estructuras = new Dictionary<string, Structure>(StringComparer.OrdinalIgnoreCase);
                foreach (string nombre in _constraints.EstructuraAliases.Keys)
                    estructuras[nombre] = BuscarEstructura(ss, nombre);

                // PTV de prescripción: búsqueda priorizada (fix), NO la genérica de arriba.
                string ptvAliasUsado;
                Structure ptv = BuscarEstructuraPTVPrescripcion(ss, hc, out ptvAliasUsado);
                Log($"   [PTV] Usado para D95/D98/s: '{ptv?.Id ?? "NO_ENCONTRADO"}' (alias: {ptvAliasUsado ?? "-"})");

                Structure body = estructuras.TryGetValue("Body", out var s2) ? s2 : null;
                Structure rectum = estructuras.TryGetValue("Rectum", out var s3) ? s3 : null;
                Structure bladder = estructuras.TryGetValue("Bladder", out var s4) ? s4 : null;

                // Info del plan
                double prescDosisGy = plan.TotalPrescribedDose.Dose / 100.0;
                int nFracciones = plan.UniqueFractionation.NumberOfFractions ?? 0;
                double mus = plan.Beams.Where(b => !b.IsSetupField).Sum(b => b.Meterset.Value);
                int nArcos = plan.Beams.Count(b => !b.IsSetupField);
                string status = plan.ApprovalStatus.ToString();
                string fechaPlan = ExtraerAnio(plan.PlanningApprovalDate ?? "");

                // Factor de normalización D95(PTV) → prescripción del plan (informativo)
                double ptvD95 = ptv != null && plan.Dose != null ? ObtenerDoseAtVolumePct(plan, ptv, 95.0) : double.NaN;
                double factorNorm95 = (!double.IsNaN(ptvD95) && ptvD95 > 0) ? prescDosisGy / ptvD95 : double.NaN;

                double ptvD98 = ptv != null && plan.Dose != null ? ObtenerDoseAtVolumePct(plan, ptv, 99.0) : double.NaN;
                double factorNorm98 = (!double.IsNaN(ptvD98) && ptvD98 > 0) ? prescDosisGy / ptvD98 : double.NaN;

                // Alerta: D95 sospechosamente bajo incluso con el PTV priorizado correcto
                // (umbral 60% de la prescripción de referencia — cubre el caso de ~44Gy/70Gy)
                if (!double.IsNaN(ptvD95) && !double.IsNaN(_constraints.PrescDosisGy) &&
                    ptvD95 < 0.60 * _constraints.PrescDosisGy)
                {
                    _casosPtvSospechoso.Add(
                        $"{anonId} ({hc}): PTV usado='{ptv?.Id}' (alias: {ptvAliasUsado}) → D95={F(ptvD95)} Gy");
                }

                // Calcular todas las métricas definidas en constraints (valores "entregados")
                // Resultado: {estructura}_{label} → valor
                var valores = new Dictionary<string, double>();
                var flags = new Dictionary<string, bool?>();

                foreach (var kv in _constraints.Constraints)
                {
                    string nombreEstr = kv.Key;
                    estructuras.TryGetValue(nombreEstr, out Structure estr);

                    foreach (var c in kv.Value)
                    {
                        string colKey = $"{nombreEstr}_{c.Label}";
                        double val = (estr != null && plan.Dose != null)
                            ? EvaluarMetrica(plan, estr, c)
                            : double.NaN;
                        valores[colKey] = val;
                        flags[colKey] = c.Evaluate(val);
                    }
                }

                // ─── Renormalización a D95(PTV) = Presc. de referencia ─────────────
                // s = DosisTotal_Gy(referencia, de constraints_prostata_hipo.txt) / D95(PTV) entregado
                // Renormalización lineal exacta: escala toda la distribución de dosis por s,
                // por lo que V{X}Gy_escalado(OAR) = V{X/s}Gy_entregado(OAR).
                double s_D95 = (!double.IsNaN(ptvD95) && ptvD95 > 0)
                    ? _constraints.PrescDosisGy / ptvD95
                    : double.NaN;

                var valoresD95norm = new Dictionary<string, double>();
                var flagsD95norm = new Dictionary<string, bool?>();
                var flagsCambiadosEsteCaso = new List<string>();

                foreach (var kv in _constraints.Constraints)
                {
                    string nombreEstr = kv.Key;
                    if (nombreEstr.Equals("PTV", StringComparison.OrdinalIgnoreCase)) continue; // solo OAR

                    estructuras.TryGetValue(nombreEstr, out Structure estr);

                    foreach (var c in kv.Value)
                    {
                        string colKeyBase = $"{nombreEstr}_{c.Label}";
                        string colKeyNorm = $"{colKeyBase}_D95norm";

                        if (c.Type == MetricType.VolumeAtDose)
                        {
                            double valNorm = double.NaN;
                            if (estr != null && plan.Dose != null && !double.IsNaN(s_D95) && s_D95 > 0)
                            {
                                double doseEscaladaGy = c.Param / s_D95;
                                valNorm = ObtenerVolumeAtDose(plan, estr, doseEscaladaGy);
                            }
                            bool? flagNorm = c.Evaluate(valNorm);
                            valoresD95norm[colKeyNorm] = valNorm;
                            flagsD95norm[colKeyNorm] = flagNorm;

                            // Comparar contra el flag entregado
                            flags.TryGetValue(colKeyBase, out bool? flagEntregado);
                            if (flagEntregado.HasValue && flagNorm.HasValue && flagEntregado.Value != flagNorm.Value)
                            {
                                _totalFlagsCambiados++;
                                flagsCambiadosEsteCaso.Add(
                                    $"{colKeyBase}: entregado={BoolToStr(flagEntregado)} → D95norm={BoolToStr(flagNorm)}");
                            }
                        }
                        else if (c.Type == MetricType.Dmean)
                        {
                            // Solo registro informativo — NO es constraint, no lleva flag.
                            valores.TryGetValue(colKeyBase, out double dmeanEntregado);
                            double dmeanNorm = (!double.IsNaN(dmeanEntregado) && !double.IsNaN(s_D95))
                                ? dmeanEntregado * s_D95
                                : double.NaN;
                            valoresD95norm[colKeyNorm] = dmeanNorm;
                        }
                        // Dmax / DoseAtVolumePct / DoseAtVolumeCc: fuera de alcance de esta tarea.
                    }
                }

                if (flagsCambiadosEsteCaso.Count > 0)
                    _casosCambiados.Add($"{anonId} ({hc}): " + string.Join(" | ", flagsCambiadosEsteCaso));

                // Volúmenes y solapamiento (usan el PTV priorizado/corregido)
                double volPtv = ptv?.Volume ?? double.NaN;
                double volRectum = rectum?.Volume ?? double.NaN;
                double volBladder = bladder?.Volume ?? double.NaN;
                double solapPtvRectum = CalcularSolapamiento(ptv, rectum);
                double solapPtvBladder = CalcularSolapamiento(ptv, bladder);

                // Construir línea CSV en el mismo orden que el encabezado
                var campos = new List<string>
                {
                    anonId, hc, fechaPlan, status,
                    nFracciones.ToString(), F(prescDosisGy), F(mus), nArcos.ToString(),
                    ptv?.Id ?? "NO_ENCONTRADO",
                    F(volPtv), F(ptvD95), F(factorNorm95),F(ptvD98),F(factorNorm98),
                    F(volRectum), F(volBladder),
                    F(solapPtvRectum), F(solapPtvBladder)
                };

                // Columnas dinámicas: valor + flag por cada constraint (entregado)
                foreach (var kv in _constraints.Constraints)
                    foreach (var c in kv.Value)
                    {
                        string colKey = $"{kv.Key}_{c.Label}";
                        campos.Add(F(valores.TryGetValue(colKey, out double v) ? v : double.NaN));
                        campos.Add(BoolToStr(flags.TryGetValue(colKey, out bool? f) ? f : null));
                    }

                // Columna s_D95 + columnas D95norm (solo OAR)
                campos.Add(F(s_D95));
                foreach (var kv in _constraints.Constraints)
                {
                    if (kv.Key.Equals("PTV", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var c in kv.Value)
                    {
                        string colKeyNorm = $"{kv.Key}_{c.Label}_D95norm";
                        if (c.Type == MetricType.VolumeAtDose)
                        {
                            campos.Add(F(valoresD95norm.TryGetValue(colKeyNorm, out double v) ? v : double.NaN));
                            campos.Add(BoolToStr(flagsD95norm.TryGetValue(colKeyNorm, out bool? f) ? f : null));
                        }
                        else if (c.Type == MetricType.Dmean)
                        {
                            campos.Add(F(valoresD95norm.TryGetValue(colKeyNorm, out double v) ? v : double.NaN));
                        }
                    }
                }

                File.AppendAllText(csvPath, string.Join(";", campos) + "\n");
                Log("   ✓ Métricas extraídas");
            }
            catch (Exception ex)
            {
                Log($"   [WARN] No se pudieron extraer métricas: {ex.Message}");
                var totalCols = ConstruirEncabezadoColumnas().Count;
                var camposError = new List<string> { anonId, hc };
                for (int i = 2; i < totalCols; i++) camposError.Add("");
                File.AppendAllText(csvPath, string.Join(";", camposError) + "\n");
            }
        }

        // ─── Encabezado CSV dinámico ──────────────────────────────────────────
        /// <summary>Construye la lista de columnas del CSV (compartida entre header y fallback de error).</summary>
        private static List<string> ConstruirEncabezadoColumnas()
        {
            var cols = new List<string>
            {
                "AnonID", "HC", "AnioPlan", "Status",
                "NFracciones", "DosisTotal_Gy", "MUs", "NArcos",
                "NombrePTV", "VolPTV_cc", "PTV_D95_Gy", "FactorNorm_D95","PTV_D98_Gy", "FactorNorm_D98",
                "VolRectum_cc", "VolBladder_cc",
                "Solap_PTV_Rectum_cc", "Solap_PTV_Bladder_cc"
            };

            // Columnas dinámicas "entregadas": valor + flag por cada constraint
            foreach (var kv in _constraints.Constraints)
                foreach (var c in kv.Value)
                {
                    string colKey = $"{kv.Key}_{c.Label}";
                    cols.Add(colKey);
                    cols.Add($"Flag_{colKey}");
                }

            // Columna s_D95 + columnas D95norm (renormalizadas), solo para OAR (no PTV)
            cols.Add("s_D95");
            foreach (var kv in _constraints.Constraints)
            {
                if (kv.Key.Equals("PTV", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var c in kv.Value)
                {
                    string colKeyNorm = $"{kv.Key}_{c.Label}_D95norm";
                    if (c.Type == MetricType.VolumeAtDose)
                    {
                        cols.Add(colKeyNorm);
                        cols.Add($"Flag_{colKeyNorm}");
                    }
                    else if (c.Type == MetricType.Dmean)
                    {
                        cols.Add(colKeyNorm); // solo valor informativo, sin flag
                    }
                }
            }

            return cols;
        }

        private static void EscribirEncabezadoMetricas(string path)
        {
            var cols = ConstruirEncabezadoColumnas();
            File.WriteAllText(path, string.Join(";", cols) + "\n");
        }

        // ─── Extracción de objetivos de optimización ──────────────────────────
        private static void EscribirEncabezadoObjetivos(string path)
        {
            var cols = new[]
            {
                "AnonID", "HC", "PlanId", "TipoObjetivo", "Estructura",
                "Operador", "Dosis", "UnidadDosis", "Volumen_pct", "Prioridad", "PtoIndex"
            };
            File.WriteAllText(path, string.Join(";", cols) + "\n");
        }

        /// <summary>
        /// Extrae los objetivos de optimización del plan (puntos Upper/Lower y curvas
        /// RapidPlan/línea, discretizando cada punto de la curva) y los agrega al CSV
        /// de la sesión. Adaptado del script standalone de Eclipse (Insumos/Prueba.cs).
        /// </summary>
        private static void ExtraerObjetivosOptimizacion(PlanSetup plan, string anonId, string hc, string csvPath)
        {
            try
            {
                OptimizationSetup optSetup = plan.OptimizationSetup;
                if (optSetup == null)
                {
                    Log("   [WARN] Plan sin configuración de optimización");
                    return;
                }

                foreach (OptimizationObjective objective in optSetup.Objectives)
                {
                    var pointOpt = objective as OptimizationPointObjective;
                    if (pointOpt != null)
                    {
                        var campos = new List<string>
                        {
                            anonId, hc, plan.Id, "PUNTO", pointOpt.StructureId,
                            pointOpt.Operator.ToString(),
                            F(pointOpt.Dose.Dose), pointOpt.Dose.Unit.ToString(),
                            F(pointOpt.Volume), F(pointOpt.Priority), ""
                        };
                        File.AppendAllText(csvPath, string.Join(";", campos) + "\n");
                        continue;
                    }

                    var lineOpt = objective as OptimizationLineObjective;
                    if (lineOpt != null)
                    {
                        if (lineOpt.CurveData != null && lineOpt.CurveData.Length > 0)
                        {
                            int ptoIndex = 1;
                            foreach (var punto in lineOpt.CurveData)
                            {
                                var campos = new List<string>
                                {
                                    anonId, hc, plan.Id, "LINEA", lineOpt.StructureId, "",
                                    F(punto.DoseValue.Dose), punto.DoseValue.Unit.ToString(),
                                    F(punto.Volume), F(lineOpt.Priority), ptoIndex.ToString()
                                };
                                File.AppendAllText(csvPath, string.Join(";", campos) + "\n");
                                ptoIndex++;
                            }
                        }
                        else
                        {
                            var campos = new List<string>
                            {
                                anonId, hc, plan.Id, "LINEA", lineOpt.StructureId, "",
                                "", "", "", F(lineOpt.Priority), ""
                            };
                            File.AppendAllText(csvPath, string.Join(";", campos) + "\n");
                        }
                    }
                }
                Log("   ✓ Objetivos de optimización extraídos");
            }
            catch (Exception ex)
            {
                Log($"   [WARN] No se pudieron extraer objetivos de optimización: {ex.Message}");
            }
        }

        // ─── Evaluación de una métrica ────────────────────────────────────────
        private static double EvaluarMetrica(PlanSetup plan, Structure estructura, Constraint c)
        {
            switch (c.Type)
            {
                case MetricType.VolumeAtDose:
                    return ObtenerVolumeAtDose(plan, estructura, c.Param);

                case MetricType.DoseAtVolumePct:
                    return ObtenerDoseAtVolumePct(plan, estructura, c.Param);

                case MetricType.DoseAtVolumeCc:
                    return ObtenerDoseAtVolumeCc(plan, estructura, c.Param);

                case MetricType.Dmean:
                    return ObtenerDmean(plan, estructura);

                case MetricType.Dmax:
                    return ObtenerDmax(plan, estructura);

                default:
                    return double.NaN;
            }
        }

        // ─── Helpers DVH ──────────────────────────────────────────────────────

        /// <summary>% de volumen que recibe >= doseGy Gy.</summary>
        private static double ObtenerVolumeAtDose(PlanSetup plan, Structure s, double doseGy)
        {
            try
            {
                return plan.GetVolumeAtDose(
                    s,
                    new DoseValue(doseGy * 100.0, DoseValue.DoseUnit.cGy),
                    VolumePresentation.Relative);
            }
            catch { return double.NaN; }
        }

        /// <summary>Dosis (Gy) que recibe el volPct% del volumen.</summary>
        private static double ObtenerDoseAtVolumePct(PlanSetup plan, Structure s, double volPct)
        {
            try
            {
                var dv = plan.GetDoseAtVolume(s, volPct, VolumePresentation.Relative, DoseValuePresentation.Absolute);
                return dv.Unit == DoseValue.DoseUnit.Unknown ? double.NaN : dv.Dose / 100.0;
            }
            catch { return double.NaN; }
        }

        /// <summary>Dosis (Gy) que recibe volCc cc del volumen (absoluto).</summary>
        private static double ObtenerDoseAtVolumeCc(PlanSetup plan, Structure s, double volCc)
        {
            try
            {
                var dv = plan.GetDoseAtVolume(s, volCc, VolumePresentation.AbsoluteCm3, DoseValuePresentation.Absolute);
                return dv.Unit == DoseValue.DoseUnit.Unknown ? double.NaN : dv.Dose / 100.0;
            }
            catch { return double.NaN; }
        }

        /// <summary>Dosis media (Gy).</summary>
        private static double ObtenerDmean(PlanSetup plan, Structure s)
        {
            try
            {
                var dvh = plan.GetDVHCumulativeData(s, DoseValuePresentation.Absolute, VolumePresentation.Relative, 0.1);
                return dvh == null ? double.NaN : dvh.MeanDose.Dose / 100.0;
            }
            catch { return double.NaN; }
        }

        /// <summary>Dosis máxima (Gy).</summary>
        private static double ObtenerDmax(PlanSetup plan, Structure s)
        {
            try
            {
                var dvh = plan.GetDVHCumulativeData(s, DoseValuePresentation.Absolute, VolumePresentation.Relative, 0.1);
                return dvh == null ? double.NaN : dvh.MaxDose.Dose / 100.0;
            }
            catch { return double.NaN; }
        }

        // ─── Helpers geometría ────────────────────────────────────────────────
        /// <summary>
        /// Calcula el volumen de solapamiento (cc) entre dos estructuras mediante
        /// muestreo por voxel counting sobre la intersección de sus bounding boxes.
        /// Para pares PTV-OAR el bbox de intersección es pequeño → cómputo rápido.
        /// Precisión: paso de 1 mm por defecto → ~1% de error relativo en volúmenes >5cc.
        /// </summary>
        private static double CalcularSolapamiento(Structure s1, Structure s2, double stepMm = 1.0)
        {
            if (s1 == null || s2 == null) return double.NaN;
            try
            {
                var b1 = s1.MeshGeometry.Bounds;
                var b2 = s2.MeshGeometry.Bounds;

                // Intersección de los bounding boxes
                double xMin = Math.Max(b1.X, b2.X);
                double yMin = Math.Max(b1.Y, b2.Y);
                double zMin = Math.Max(b1.Z, b2.Z);
                double xMax = Math.Min(b1.X + b1.SizeX, b2.X + b2.SizeX);
                double yMax = Math.Min(b1.Y + b1.SizeY, b2.Y + b2.SizeY);
                double zMax = Math.Min(b1.Z + b1.SizeZ, b2.Z + b2.SizeZ);

                // Sin solapamiento espacial
                if (xMax <= xMin || yMax <= yMin || zMax <= zMin) return 0.0;

                // Volumen del voxel de muestreo (mm³ → cc: /1000)
                double voxelCc = (stepMm * stepMm * stepMm) / 1000.0;

                long count = 0;
                for (double z = zMin; z <= zMax; z += stepMm)
                    for (double y = yMin; y <= yMax; y += stepMm)
                        for (double x = xMin; x <= xMax; x += stepMm)
                        {
                            var p = new VVector(x, y, z);
                            if (s1.IsPointInsideSegment(p) && s2.IsPointInsideSegment(p))
                                count++;
                        }

                return count * voxelCc;
            }
            catch { return double.NaN; }
        }
        // ─── AnonID ───────────────────────────────────────────────────────────
        private static string GenerarAnonId(string hc)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes("PROST_" + hc));
                return "PT_" + BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLower();
            }
        }

        // ─── Helpers varios ───────────────────────────────────────────────────
        private static string ExtraerAnio(string fechaStr)
        {
            if (string.IsNullOrEmpty(fechaStr)) return "";
            if (System.DateTime.TryParse(fechaStr, out System.DateTime dt)) return dt.Year.ToString();
            foreach (string token in fechaStr.Split(' ', ','))
                if (token.Length == 4 && int.TryParse(token, out int anio) && anio > 2000)
                    return anio.ToString();
            return "";
        }

        private static string F(double v) =>
            double.IsNaN(v) ? "" : v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

        private static string BoolToStr(bool? v) =>
            v.HasValue ? (v.Value ? "1" : "0") : "";
    }
}
