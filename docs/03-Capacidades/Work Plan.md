---
tipo: referencia_capacidad
estado: vigente
actualizado: 2026-10-09
---
# Work Plan

El plan es **estado lógico opcional del agente** asociado a una WorkSession; no es scheduler, monitor de procesos, MCP Tasks, gestor de proyectos ni backlog durable.

## API MCP

- `work_plan_update`: creación inicial (`expectedRevision=0`), reemplazo completo, reordenamiento, reconciliación y vaciado.
- `work_plan_get`: recuperar snapshot actual y revision en sesión ya existente.
- `work_plan_patch`: modificar sólo pasos afectados con `add`, `update` y `remove`; usa la revision esperada.

Los IDs opacos `step_*` permanecen estables al actualizar. CAS evita pérdida silenciosa de escrituras: ante conflicto obtener el estado actual, reconciliar y reintentar; LoomLCI no hace auto-merge.

## Estados y restricciones

`pending`, `active`, `waiting` y `completed`. Se permiten **múltiples** pasos activos/en espera; un paso `waiting` no significa que el sistema vigile un ProcessHandle. El plan desaparece al cerrar o expirar su WorkSession.

Crear planes concisos orientados a hitos y actualizarlos cuando cambie realmente el progreso. Para tareas permanentes del proyecto, usar [[Backlog]], no Work Plan.

**Evidencias:** [[Bloque F - Agent Support]], [[F1.3 - Benchmark real-world Work Plan]], [[Ergonomía - Work Plan patch]].
