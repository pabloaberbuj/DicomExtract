using System;
using System.IO;
using System.Linq;
using VMS.TPS.Common.Model.API;

namespace VMS.TPS
{
  public class Script
  {
    public Script()
    {
    }

    public void Execute(ScriptContext context /*, System.Windows.Window window, ScriptEnvironment environment*/)
    {
            
			PlanSetup plan = context.PlanSetup;
            if (plan == null)
            {
                System.Windows.MessageBox.Show("No hay un plan activo seleccionado.", "Error");
                return;
            }

            OptimizationSetup optSetup = plan.OptimizationSetup;
            if (optSetup == null)
            {
                System.Windows.MessageBox.Show("No se encontró configuración de optimización en este plan.", "Información");
                return;
            }

            // Definir la ruta del archivo de texto en el Escritorio del usuario
            string desktopPath = @"\\ARIAMEVADB-SVR\va_data$";
            string fileName = string.Format("Optimización_{0}_{1}.txt", plan.Id, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string fullPath = Path.Combine(desktopPath, fileName);

try
            {
                using (StreamWriter writer = new StreamWriter(fullPath, false, System.Text.Encoding.UTF8))
                {
                    writer.WriteLine("=====================================================================");
                    writer.WriteLine("REPORTE DE OPTIMIZACIÓN Y DISCRETIZACIÓN DE CURVAS (Eclipse 13.6)");
                    writer.WriteLine("=====================================================================");
                    writer.WriteLine(string.Format("Fecha/Hora: {0}", DateTime.Now.ToString()));
                    writer.WriteLine(string.Format("ID del Plan: {0}", plan.Id));
                    writer.WriteLine("=====================================================================\n");

                    foreach (OptimizationObjective objective in optSetup.Objectives)
                    {
                        // 1. EVALUAR OBJETIVOS TIPO PUNTO (Upper / Lower)
                        OptimizationPointObjective pointOpt = objective as OptimizationPointObjective;
                        if (pointOpt != null)
                        {
                            writer.WriteLine(string.Format(
                                "[PUNTO] Estructura: {0} | Tipo: {1} | Dosis: {2} | Vol: {3}% | Prioridad: {4}",
                                pointOpt.StructureId,
                                pointOpt.Operator,
                                pointOpt.Dose,
                                pointOpt.Volume,
                                pointOpt.Priority 
                            ));
                            continue;
                        }

                        // 2. EVALUAR OBJETIVOS TIPO LÍNEA (RapidPlan / Curvas) - CON DISCRETIZACIÓN
                        OptimizationLineObjective lineOpt = objective as OptimizationLineObjective;
                        if (lineOpt != null)
                        {
                            writer.WriteLine(string.Format(
                                "[LÍNEA] Estructura: {0} | Prioridad: {1} (Iniciando volcado de puntos)",
                                lineOpt.StructureId,
                                lineOpt.Priority
                            ));

                            // Validación de seguridad para asegurar que la curva contiene puntos discretizados
                            if (lineOpt.CurveData != null && lineOpt.CurveData.Length > 0)
                            {
                                int pointIndex = 1;
                                foreach (var point in lineOpt.CurveData)
                                {
                                    // En ESAPI v13.6, la dosis se extrae de DoseValue.Dose y el volumen es un double directo (%)
                                    writer.WriteLine(string.Format(
                                        "   -> Punto {0:D3}: Dosis = {1:F2} {2} | Vol = {3:F2}%",
                                        pointIndex,
                                        point.DoseValue.Dose,
                                        point.DoseValue.Unit,
                                        point.Volume
                                    ));
                                    pointIndex++;
                                }
                            }
                            else
                            {
                                writer.WriteLine("   -> No se encontraron puntos en CurveData para esta línea.");
                            }
                            
                            writer.WriteLine(); // Espacio separador
                        }
                    }
                }

                // Notificar éxito al usuario clínico
                System.Windows.MessageBox.Show(
                    string.Format("¡Discretización completada con éxito!\nArchivo guardado en el Escritorio:\n\n{0}", fileName), 
                    "Exportación Completada"
                );
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    string.Format("Error al escribir el archivo TXT:\n\n{0}", ex.Message), 
                    "Error de Escritura"
                );
            }
        }
    }
}