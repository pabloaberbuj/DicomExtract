# Prompt de exploración — software de extracción y anonimización de DICOM

> Pegar como primer mensaje en una sesión de Code abierta sobre el repo del extractor.
> Es una exploración de **solo lectura**: el entregable es un informe, no cambios de código.

---

## Objetivo

Voy a reajustar este software de extracción y anonimización de DICOM (exportación desde
Eclipse/ARIA). Antes de tocar nada necesito que lo **entiendas y lo audites** con foco en lo que
importa para un proyecto de ML de predicción de dosis. Entregá un informe; **no modifiques
código** en esta sesión.

Cambios que quiero hacer después (para que los tengas presentes al explorar):
- Incluir el **número de planes por curso**, distinguiendo aprobados, no aprobados e intermedios
  (esto decide la viabilidad de un trabajo futuro que usa varias versiones de plan por paciente).
- `[COMPLETAR: otros cambios ya identificados]`

## Reglas de la sesión

- **Solo lectura.** No edites, no refactorices, no corras el software contra datos de pacientes
  reales salvo que te lo pida explícitamente.
- **No imprimas información identificable** (nombre, apellido, HC, fechas de nacimiento, UIDs
  reales) en tu respuesta ni en el informe. Si necesitás un ejemplo, enmascaralo.
- Distinguí siempre entre lo que **leíste en el código** y lo que **inferís**. Si no podés
  confirmar algo desde el código, decilo y marcalo como pregunta para mí.
- Citá archivo y función/línea para cada afirmación relevante.

## Qué mapear

**1. Arquitectura y flujo.** Puntos de entrada, módulos, lenguajes (hay C# y Python), dependencias
y cómo se conectan. Flujo completo: qué se consulta en Eclipse/ARIA, qué se exporta, en qué
formato, a dónde se escribe, y en qué punto ocurre la anonimización.

**2. Qué se exporta por paciente.** Para CT, RTSTRUCT, RTDOSE, RTPLAN y cualquier CSV o metadata
adicional: qué campos, cómo se eligen las series, y qué se deja afuera.

**3. Planes y cursos (el cambio que quiero hacer).** Cómo se enumeran cursos y planes; qué plan
se exporta cuando hay varios y con qué criterio; cómo se determina el estado de aprobación; si
hay plan sums, planes de reirradiación, planes de verificación o intermedios; y qué haría falta
para contar versiones por paciente y exportar su dosis. Anotá límites de la API que ya aparezcan
en el código.

**4. Anonimización (revisar con lupa).**
- Qué tags se modifican o eliminan y cuáles no. Buscá específicamente lo que suele filtrarse:
  tags privados, fechas, descripciones de series y estudios, comentarios de plan, nombres de
  operadores o médicos, nombres de estructuras o de planes que contengan datos del paciente,
  y texto libre en general.
- **Consistencia referencial:** si los UIDs se remapean, ¿se remapean de forma consistente entre
  CT, RTSTRUCT, RTPLAN y RTDOSE, de modo que las referencias entre archivos sigan válidas?
- ¿Dónde y cómo se guarda el mapeo identificador real → anónimo? ¿Es estable entre corridas y
  entre sitios? ¿Queda dentro del mismo directorio que los datos exportados?
- Riesgos de re-identificación residual y cualquier camino por el que un dato identificable pueda
  llegar al CSV o al DICOM de salida.

**5. Fidelidad de la dosis y la geometría.** Es donde un bug cuesta corridas enteras de ML.
- ¿La dosis exportada es **absoluta en Gy**? ¿Se aplica alguna normalización del plan o escalado
  (`DoseGridScaling`, normalización de Eclipse) antes de exportar? ¿Queda registrado?
- Resolución de la grilla de dosis: ¿la define el software, el usuario o Eclipse? ¿Se recorta
  (ROI/FOV)? ¿Cambia entre pacientes?
- Orientación, origen, `FrameOfReferenceUID` y spacing: ¿se preservan tal cual? ¿Hay
  resampleos escondidos?
- Selección de la serie de CT: ¿cómo se identifica? (Precedente de bug en este proyecto: una
  función cargaba la serie de RTDOSE como si fuera CT.)
- Estructuras: ¿se exportan con su nombre original? ¿Qué pasa con estructuras vacías, de
  alta resolución, con varios contornos o con nombres duplicados?

**6. Metadata del plan que sirve para el proyecto.** Qué se exporta hoy de técnica (VMAT vs IMRT
vs otra), número y geometría de arcos o campos, energía, máquina, isocentro, fraccionamiento y
dosis por fracción. Sirve para verificar que cada paciente es VMAT y para etiquetar protocolo.

**7. Robustez.** Manejo de errores, qué pasa cuando falla un paciente a mitad de una exportación,
logging, idempotencia (¿re-correr duplica o pisa?), reanudación, y si hay fallos silenciosos
(excepciones tragadas, defaults que ocultan datos faltantes).

## Entregable

Un informe en Markdown (`docs/auditoria_extractor.md`) con:
1. **Mapa del sistema** (diagrama de texto del flujo y tabla de módulos).
2. **Qué hace hoy** respecto de cada punto de arriba, con referencias a código.
3. **Riesgos priorizados** (alto / medio / bajo), cada uno con: qué es, dónde está, qué rompería
   en un pipeline de ML, y cómo lo detectarías.
4. **Preguntas para mí**: todo lo que no pudiste confirmar desde el código.
5. **Propuesta de cambios** mínimos para (a) el conteo de planes por curso y (b) cada riesgo
   alto, ordenados y con estimación de esfuerzo. Sin implementar.

Empezá por el mapa del sistema y la sección de anonimización; si algo del tamaño del código te
obliga a priorizar, esas dos y la de fidelidad de dosis van primero.
