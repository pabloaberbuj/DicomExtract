using System.Collections.Generic;

namespace DicomExport.Core
{
    /// <summary>UIDs a pedir al daemon. Los arma el llamador (ESAPI, CSV, etc.); Core no depende de ESAPI.</summary>
    public class PlanUids
    {
        /// <summary>SeriesInstanceUID de la CT (plan.StructureSet.Image.Series.UID).</summary>
        public string CtSeriesUid { get; set; }
        /// <summary>SOPInstanceUID del RTSTRUCT (plan.StructureSet.UID).</summary>
        public string RsUid { get; set; }
        /// <summary>SOPInstanceUID del RTPLAN (plan.UID).</summary>
        public string RpUid { get; set; }
        /// <summary>SOPInstanceUID del RTDOSE (plan.Dose.UID).</summary>
        public string RdUid { get; set; }
        /// <summary>SOPInstanceUIDs de RT Images (DRR/imágenes de referencia). Opcional.</summary>
        public List<string> RiUids { get; set; } = new List<string>();
    }

    /// <summary>Qué tipos exportar; cada uno es independiente.</summary>
    public class ExportOptions
    {
        public bool CT { get; set; }
        public bool RS { get; set; }
        public bool RP { get; set; }
        public bool RD { get; set; }
        public bool RI { get; set; }
        public bool Anonimizar { get; set; }
        public bool Any { get { return CT || RS || RP || RD || RI; } }
    }

    /// <summary>Carpeta de destino por tipo; la que quede vacía usa <see cref="Default"/>.</summary>
    public class ExportDestinos
    {
        public string Default { get; set; }
        public string CT { get; set; }
        public string RS { get; set; }
        public string RP { get; set; }
        public string RD { get; set; }
        public string RI { get; set; }

        /// <summary>Resuelve por Modality DICOM (CT, RTSTRUCT, RTPLAN, RTDOSE, RTIMAGE).</summary>
        public string Resolve(string modality)
        {
            string p;
            switch ((modality ?? "").ToUpperInvariant())
            {
                case "CT": p = CT; break;
                case "RTSTRUCT": p = RS; break;
                case "RTPLAN": p = RP; break;
                case "RTDOSE": p = RD; break;
                case "RTIMAGE": p = RI; break;
                default: p = null; break;
            }
            return string.IsNullOrWhiteSpace(p) ? Default : p;
        }
    }
}
