"""
Normalización de nombre de PTV — referencia para el repo Python (preprocesador + build_harmonization.py).

Misma regla que DicomExtract (C#, PlanMetadata.NivelPtv). Aplicar ANTES de matchear contra canónicos y alias.
Preserva SOLO la distinción semántica "recortado (04) o no"; el ruido de tipeo se absorbe sin enumerar variantes:

  1) minúsculas;
  2) marca de crop = separador de ruido (- . _ ! espacio, cualquier combinación) + "04" al final.
     "0.4" cuenta como tipeo de 04 (NO es un margen de 0,4) -> cropped=True y se quita ese sufijo;
  3) en el resto, las corridas de separadores se colapsan a "_"  (ptv_high == ptv-high == ptv high);
  4) debe quedar EXACTAMENTE ptv_(high|mid|low). Sufijos numéricos que NO son 04 (-05, -10, -14...),
     prefijos (zring, zopt) o separadores sueltos al borde (ptv_high!) NO son ruido -> None (desconocido:
     falla ruidoso y va a revisión, nunca se colapsa a 04).

cropped=True  -> candidato "-04" de geometry_candidates;  cropped=False -> candidato sin sufijo.

Ejecutar este archivo corre las pruebas (mismos casos que las del extractor).
"""
import re

# Alias EXPLÍCITOS -> nivel; quedan FUERA de la regla de normalización (para la regla "_P" es un sufijo desconocido).
# En el repo Python esto es la sección `aliases` de harmonization_schema.yaml. DicomExtract (C#, PlanMetadata.ALIAS_PTV)
# mantiene una COPIA: si se agrega un alias en uno, hay que agregarlo en el otro.
ALIASES = {"ptv": "high", "ptv_high_p": "high", "ptv_ptta": "high"}

_CROP = re.compile(r"^(?P<base>.*?)[\-._! ]*0\.?4$")
_SEP = re.compile(r"[\-._! ]+")
_NIVEL = re.compile(r"^ptv_(?P<n>high|mid|low)$")


def normalize_ptv_name(name):
    """-> (nivel, cropped) con nivel en {'high','mid','low'}, o (None, False) si no es un PTV conocido.
    Alias explícito primero (match exacto, sin distinguir mayúsculas); si no, la regla."""
    if name is not None and name.lower() in ALIASES:
        return ALIASES[name.lower()], False
    s = (name or "").lower()
    base, cropped = s, False
    m = _CROP.match(s)
    if m:
        base, cropped = m.group("base"), True
    n = _NIVEL.match(_SEP.sub("_", base))
    if not n:
        return None, False
    return n.group("n"), cropped


def pick_ptv(structures, level="high"):
    """structures: iterable de (nombre, volumen). Devuelve (nombre, motivo).
    Entre las de volumen > 0 que normalizan al nivel pedido, la recortada (04) gana sobre la original.
    Más de una en el mismo grupo -> ambiguo; ninguna -> desconocido. En ambos casos (None, motivo)."""
    found = []
    for nm, vol in structures:
        lvl, cropped = normalize_ptv_name(nm)
        if vol > 0 and lvl == level:
            found.append((nm, cropped))
    for crop in (True, False):
        g = [nm for nm, c in found if c == crop]
        if len(g) == 1:
            return g[0], None
        if len(g) > 1:
            return None, "PTV_%s ambiguo (%s)" % (level.capitalize(), ", ".join(g))
    return None, "PTV_%s no resuelto: ninguna estructura con volumen > 0 normaliza a ese nivel" % level.capitalize()


if __name__ == "__main__":
    H, M, L = "high", "mid", "low"
    for n in ["PTV_High-04", "PTV_High-0.4", "PTV_High!04", "PTV_High04", "PTV_High_04", "PTV High-04",
              "ptv_high-04", "PTV-High-04", "PTV_High - 04", "PTV_High!-0.4"]:
        assert normalize_ptv_name(n) == (H, True), n
    for n in ["PTV_High", "ptv-high", "PTV High", "PTV__High", "PTV.High"]:
        assert normalize_ptv_name(n) == (H, False), n
    assert normalize_ptv_name("PTV_Mid-04") == (M, True)
    assert normalize_ptv_name("PTV_Mid!04") == (M, True)
    assert normalize_ptv_name("PTV_Low-0.4") == (L, True)
    assert normalize_ptv_name("PTV_Low") == (L, False)
    for n in ["PTV_High-05", "PTV_High-10", "PTV_High-14", "PTV_High-4", "PTV_High_2004", "PTV_High-004",
              "PTV_High2", "PTV_High_Alt", "zRingPTV_High", "zOptPTV_High!", "zRingPTV_High-04", "PTV_High!",
              "!PTV_High", "CTV_High", "PTV_Eval", "PTV_High-0.5", "PTV_High_P-04", "PTV_High_Px", "", None]:
        assert normalize_ptv_name(n) == (None, False), n

    assert pick_ptv([("PTV_High", 50), ("PTV_High-04", 48), ("zRingPTV_High", 20)])[0] == "PTV_High-04"
    assert pick_ptv([("zRingPTV_High", 20), ("zOptPTV_High!", 40), ("Body", 9000)])[0] is None
    assert pick_ptv([("PTV_High-0.4", 47), ("zRingPTV_High", 20)])[0] == "PTV_High-0.4"
    assert pick_ptv([("PTV_High!04", 47), ("zOptPTV_High!", 40)])[0] == "PTV_High!04"
    assert pick_ptv([("PTV_High_04", 47), ("PTV_High", 50)])[0] == "PTV_High_04"
    assert pick_ptv([("PTV_High-04", 0), ("PTV_High", 50)])[0] == "PTV_High"
    assert pick_ptv([("PTV_High-04", 47), ("PTV_High-0.4", 46)])[0] is None          # ambiguo
    assert pick_ptv([("PTV_High-04", 0), ("PTV_High-0.4", 46)])[0] == "PTV_High-0.4"
    assert pick_ptv([("PTV_Mid-04", 30), ("PTV_Low", 20)])[0] is None
    assert pick_ptv([("PTV_High-05", 40)])[0] is None
    assert pick_ptv([("PTV_High-05", 40), ("PTV_High", 50)])[0] == "PTV_High"
    assert pick_ptv([("PTV_Mid-0.4", 30)], level="mid")[0] == "PTV_Mid-0.4"
    # alias explícitos
    assert normalize_ptv_name("PTV") == (H, False)
    assert normalize_ptv_name("ptv") == (H, False)
    assert normalize_ptv_name("PTV_High_P") == (H, False)
    assert normalize_ptv_name("PTV_Ptta") == (H, False)
    assert normalize_ptv_name("PTV_Ptta-04") == (None, False)   # el alias es exacto, no se generaliza
    assert pick_ptv([("PTV", 50)])[0] == "PTV"
    assert pick_ptv([("PTV_High_P", 50), ("Body", 9000)])[0] == "PTV_High_P"
    assert pick_ptv([("PTV", 50), ("PTV_High-04", 48)])[0] == "PTV_High-04"      # recortado gana sobre alias
    assert pick_ptv([("PTV", 50), ("PTV_High", 48)])[0] is None                    # ambos sin recortar: ambiguo
    print("OK")
