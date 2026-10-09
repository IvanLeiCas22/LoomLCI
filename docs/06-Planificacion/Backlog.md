---
tipo: backlog
estado: vigente
actualizado: 2026-10-09
---
# Backlog

Esta página contiene **lo que aún no está implementado o no se ha aprobado para despliegue**. No usar Work Plan efímero como backlog del proyecto. Si se aprueba un bloque, crear una nota de investigación técnica y conservar la evidencia de la decisión.

## Sin próximo bloque de implementación aprobado

El mini-roadmap de robustez **RB-01 a RB-06** terminó end-to-end para su alcance documentado. [[Roadmap de robustez post-auditoría]] y [[Roadmap post-G1]] son **planes cerrados**, disponibles en el historial. No tratarlos como prioridades actuales.

## Candidatos diferidos

| Tema | Situación | Qué faltaría antes de implementar |
| --- | --- | --- |
| **Computer H1** (captura, UIA, input) | Diseñado, pausado; **sin tools productivas** | Revalidar diseño frente al código y confirmar alcance/aprobación. Ver [[Bloque H - Computer]] |
| Publicación y aplicación automática de releases | Diferido | Diseñar distribución/feed, firma, rollback, supervisión externa e interacción con el usuario |
| Actualización automática del Launcher | Diferido | Mecanismo seguro de reemplazo que no se autodestruya |
| Funciones extra de Python (salidas genéricas/audio y Computer bridge) | Diferido | Justificación, seguridad y casos de uso |
| Instalación/distribución ampliadas (MSI/MSIX, Authenticode, offline, arm64, multi-PC) | Diferido | Priorizar por necesidad real y compatibilidad |
| Streamable HTTP productivo | PoC investigada, no deployment productivo | Validar autenticación, seguridad, redundancia y beneficio respecto de STDIO/túnel |
| Work Plan con dependencias/DAG durable | No justificado | Evaluación que demuestre ventaja sobre plan lógico simple |

## Problemas conocidos, no necesariamente tareas aprobadas

- Render inline de imágenes en la interfaz de ChatGPT puede no estar disponible, aunque el modelo procese imagen MCP. Depende del consumidor; no atribuirlo sin prueba al Host.
- La observabilidad best-effort puede perder eventos; no es audit trail fuerte.
- Los documentos históricos contienen estados y conteos válidos **para sus fechas**, no constituyen pendientes nuevos.
- Para detectar bugs nuevos, realizar una auditoría de código/pruebas y proponer tareas explícitas antes de añadirlas.

El siguiente trabajo debe comenzar por una pregunta/propuesta aprobada por el usuario, no por reabrir en bloque todos los hitos históricos.
