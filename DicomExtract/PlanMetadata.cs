using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace DicomExtract
{
    /// <summary>Hechos de un plan necesarios para la cadena de filtros de versiones (sin objetos ESAPI: testeable).</summary>
    internal class PlanInfo
    {
        public string Status { get; set; }
        public double DosisGy { get; set; } = double.NaN;
        public int NFx { get; set; }
        public string StructureSetUid { get; set; }
        public string TargetVolumeId { get; set; }
    }

    /// <summary>
    /// Metadata de plan para el CSV de auditoría/gestión del dataset (proyecto site-agnostic).
    /// Python calcula todo lo derivado de dosis/geometría; acá solo hechos de ARIA.
    /// </summary>
    internal static class PlanMetadata
    {
        public const string APROBADO = "TreatmentApproved";
        // Solo estos cuentan como "no aprobado". No se trackea PlanningApproved ni Reviewed.
        private static readonly HashSet<string> NO_APROBADOS = new HashSet<string> { "UnApproved", "Rejected" };
        private const double TOL_GY = 0.01;

        public static readonly string[] Columnas =
        {
            "AnonID", "Sitio", "AnioPlan", "Status", "NombrePTV", "PTV_Resuelto", "NFracciones", "DosisTotal_Gy", "Tecnica", "NArcos", "MUs",
            "PTV_D95_Gy_Eclipse", "NPlanesCurso", "NAprobados", "NNoAprobados", "NIntermedios", "NPlanSums", "FlagRevision"
        };

        public static bool Valido(PlanInfo p)
        {
            return p.DosisGy > 0 && p.NFx > 0 && !string.IsNullOrEmpty(p.StructureSetUid);
        }

        /// <summary>Motivos por los que no se pueden contar versiones del plan de referencia (vacío = ok).</summary>
        public static List<string> Bloqueos(PlanInfo r)
        {
            var m = new List<string>();
            if (string.IsNullOrEmpty(r.TargetVolumeId)) m.Add("sin TargetVolumeID: conteos no calculados");
            if (!Valido(r)) m.Add("plan sin prescripción o sin structure set: conteos no calculados");
            return m;
        }

        /// <summary>
        /// Versiones rechazadas reales del tratamiento de <paramref name="r"/> (plan del CSV) dentro de su curso:
        /// UnApproved/Rejected → con prescripción y structure set válidos → misma prescripción (dosis total + fx)
        /// → mismo structure set (UID) → TargetVolumeID idéntico. Mejor perder un plan que contaminar con otra localización.
        /// </summary>
        public static int ContarNoAprobados(PlanInfo r, IEnumerable<PlanInfo> cursoSinReferencia)
        {
            return cursoSinReferencia
                .Where(p => NO_APROBADOS.Contains(p.Status))
                .Where(Valido)
                .Where(p => p.NFx == r.NFx && Math.Abs(p.DosisGy - r.DosisGy) <= TOL_GY)
                .Where(p => p.StructureSetUid == r.StructureSetUid)
                .Count(p => p.TargetVolumeId == r.TargetVolumeId);
        }

        // ─── Normalización de nombre de PTV (misma regla que el preprocesador de Python) ─────
        // Preserva SOLO la distinción semántica "recortado (04) o no"; todo el ruido de tipeo se absorbe:
        //  1) minúsculas;
        //  2) marca de crop = separador de ruido (-._! espacio, cualquier combinación) + "04" al final; "0.4" cuenta
        //     como tipeo de 04 (NO es un margen de 0,4): cropped=true y se quita ese sufijo;
        //  3) en el resto, las corridas de separadores se colapsan a "_" (ptv_high = ptv-high = ptv high);
        //  4) debe quedar EXACTAMENTE ptv_(high|mid|low). Sufijos numéricos que no son 04 (-05, -10, -14...),
        //     prefijos (zring, zopt) o separadores sueltos al borde (ptv_high!) NO son ruido → desconocido.
        private static readonly Regex CROP = new Regex(@"^(?<base>.*?)[\-._! ]*0\.?4$", RegexOptions.Compiled);
        private static readonly Regex SEP = new Regex(@"[\-._! ]+", RegexOptions.Compiled);
        private static readonly Regex NIVEL = new Regex(@"^ptv_(?<n>high|mid|low)$", RegexOptions.Compiled);

        // Alias EXPLÍCITOS → nivel. Quedan fuera de la regla de normalización (para la regla, "_P" es un sufijo
        // desconocido). ÚNICO lugar del extractor. Es una COPIA de la sección `aliases` del repo Python
        // (harmonization_schema.yaml / docs/normalize_structure_name.py): si se agrega un alias acá, agregarlo allá, y viceversa.
        private static readonly Dictionary<string, string> ALIAS_PTV =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "PTV", "high" },
                { "PTV_High_P", "high" },
                { "PTV_Ptta", "high" },
            };

        /// <summary>Alias explícito primero (match exacto, sin distinguir mayúsculas); si no, la regla de normalización.</summary>
        public static string ResolverNombrePtv(string nombre, out bool cropped)
        {
            string lvl;
            if (nombre != null && ALIAS_PTV.TryGetValue(nombre, out lvl)) { cropped = false; return lvl; }
            return NivelPtv(nombre, out cropped);
        }

        /// <summary>"high" | "mid" | "low" si el nombre normaliza a un PTV conocido; null si no (→ revisión). cropped = lleva 04.</summary>
        public static string NivelPtv(string nombre, out bool cropped)
        {
            cropped = false;
            string s = (nombre ?? "").ToLowerInvariant();
            string b = s;
            Match c = CROP.Match(s);
            if (c.Success) { cropped = true; b = c.Groups["base"].Value; }
            Match n = NIVEL.Match(SEP.Replace(b, "_"));
            if (!n.Success) { cropped = false; return null; }
            return n.Groups["n"].Value;
        }

        /// <summary>
        /// PTV_High de un structure set: entre las estructuras de volumen > 0 que normalizan a PTV_High, la recortada (04)
        /// tiene prioridad sobre la original. Si hay más de una en el mismo grupo → ambiguo; si no hay ninguna →
        /// desconocido. En ambos casos devuelve null y un motivo (el llamador marca FlagRevision; nunca se adivina).
        /// </summary>
        public static string ElegirPtv(IEnumerable<KeyValuePair<string, double>> estructuras, out bool cropped, out string motivo)
        {
            motivo = null;
            cropped = false;
            var high = new List<KeyValuePair<string, bool>>();
            foreach (var e in estructuras)
            {
                bool c;
                if (e.Value > 0 && ResolverNombrePtv(e.Key, out c) == "high")
                    high.Add(new KeyValuePair<string, bool>(e.Key, c));
            }
            foreach (bool crop in new[] { true, false })
            {
                var g = high.Where(x => x.Value == crop).Select(x => x.Key).ToList();
                if (g.Count == 1) { cropped = crop; return g[0]; }
                if (g.Count > 1) { motivo = "PTV_High ambiguo (" + string.Join(", ", g) + ")"; return null; }
            }
            motivo = "PTV_High no resuelto: ninguna estructura con volumen > 0 normaliza a PTV_High";
            return null;
        }

        /// <summary>
        /// Entre planes del mismo paciente: índice del más antiguo por fecha de creación, o null si no se puede
        /// decidir (alguna fecha falta o el mínimo está empatado → no adivinar, resolución manual).
        /// </summary>
        public static int? MasAntiguo(IList<DateTime?> fechas)
        {
            if (fechas.Count == 0 || fechas.Any(f => !f.HasValue)) return null;
            DateTime min = fechas.Min(f => f.Value);
            var idx = Enumerable.Range(0, fechas.Count).Where(i => fechas[i].Value == min).ToList();
            return idx.Count == 1 ? (int?)idx[0] : null;
        }

        // ─── ESAPI → PlanInfo ─────────────────────────────────────────────────
        public static PlanInfo Info(PlanSetup p)
        {
            var i = new PlanInfo { Status = p.ApprovalStatus.ToString() };
            try { i.DosisGy = p.TotalPrescribedDose.Dose / 100.0; } catch { }
            try { i.NFx = p.UniqueFractionation?.NumberOfFractions ?? 0; } catch { }
            try { i.StructureSetUid = p.StructureSet?.UID; } catch { }
            try { i.TargetVolumeId = p.TargetVolumeID; } catch { }
            return i;
        }

        /// <summary>Técnica a partir de MLCPlanType de los campos de tratamiento ("VMAT" si todos son VMAT).</summary>
        public static string Tecnica(PlanSetup p)
        {
            var tipos = p.Beams.Where(b => !b.IsSetupField).Select(b => b.MLCPlanType.ToString()).Distinct().OrderBy(x => x).ToList();
            return string.Join("+", tipos);
        }

        public static int NArcos(PlanSetup p)
        {
            return p.Beams.Count(b => !b.IsSetupField && b.GantryDirection != GantryDirection.None);
        }
    }
}
