# Ergonomía - Work Plan patch

> Estado: **IMPLEMENTADO, VALIDADO E INSTALADO.** Runtime portable `0.1.0-dev-work-plan-patch` healthy/ready; suite Release **269/269**. Falta únicamente el smoke directo de `work_plan_patch` desde un chat con catálogo MCP refrescado: esta conversación conservó el catálogo anterior de 23 tools aunque el Host instalado ya expone 24.

## Motivo

F1.3 confirmó que Work Plan aporta valor real, pero dejó una aspereza concreta: `work_plan_update` exige reenviar el snapshot completo, incluidos IDs, textos y estados de pasos no modificados.

La mejora debía reducir ese costo sin relajar:

- `revision` / compare-and-swap;
- identidad estable `step_*`;
- atomicidad;
- seguridad frente a writers concurrentes;
- lifecycle de WorkSession.

## Contrato final

Se conserva `work_plan_update` con semántica de reemplazo completo y se agrega:

```text
work_plan_patch(workId, expectedRevision, changes[])
```

`changes` acepta entre 1 y 32 operaciones:

- `add`: requiere `text`, `status` es opcional y por defecto `pending`; genera un nuevo ID y agrega al final;
- `update`: requiere `id` y al menos `text` o `status`; los campos omitidos y la posición se preservan;
- `remove`: requiere únicamente `id`.

La patch inicial **no** implementa reorder ni posicionamiento. Para creación inicial, reorder, reemplazo completo o clear se usa `work_plan_update`.

## Garantías

- `expectedRevision` se comprueba bajo el mismo `_stateGate` que el snapshot actual;
- una revisión stale devuelve `conflict` + `currentRevision`;
- no hay auto-merge ni auto-rebase, aunque dos writers toquen pasos distintos;
- update y patch compiten sobre la misma revisión: sólo uno puede ganar;
- toda la patch se aplica sobre una copia y se publica una sola vez;
- error en cualquier cambio => no se publica nada ni aumenta la revisión;
- una patch exitosa incrementa revision exactamente una vez;
- pasos no tocados conservan ID, texto, estado y orden;
- IDs repetidos como target dentro de la misma patch se rechazan;
- el resultado sigue siendo el snapshot completo, incluyendo IDs asignados a nuevos pasos.

## Superficie MCP

`work_plan_patch`:

- `ReadOnly=false`;
- `Destructive=true`;
- `Idempotent=false`;
- `OpenWorld=false`.

Las ServerInstructions recomiendan patch para milestones pequeños y mantienen `work_plan_update` para creación/reorder/reemplazo/clear.

El catálogo del Host con Work Plan habilitado pasa de **23 a 24 tools**.

## Validación

- build Release: **0 warnings / 0 errors**;
- Work Plan Core: **33/33**;
- integración específica Work Plan: **3/3**;
- suite Release completa: **269/269** = Core 96 + Windows 141 + MCP 5 + PdfWorker 6 + Launcher 6 + Integration 15;
- builder portable: Launcher **6/6** + IntegrationTests contra Host publicado **15/15**;
- paquete: `LoomLCI-0.1.0-dev-work-plan-patch-win-x64.zip`;
- SHA-256: `75c18db8e955b57c13eac85445b4417e3b72096c38e10d15f2ea18e58c78c731`;
- setup side-by-side: OK;
- cutover realizado con IvanSpace únicamente para evitar detener LoomLCI desde su propia conexión;
- runtime instalado: `0.1.0-dev-work-plan-patch`, `process_running=true`, `healthy=true`, `ready=true`;
- IntegrationTests contra la DLL **instalada**: **15/15**;
- smoke posterior al cutover desde este mismo chat mediante una tool ya conocida (`process_run`): OK.

## Aceptación restante

Esta conversación conserva el catálogo cargado antes del cutover y por eso sigue viendo 23 tools. El runtime nuevo sí está activo y sus tests de contrato confirman `work_plan_patch`.

Para cerrar el smoke directo end-to-end falta únicamente abrir un chat con catálogo refrescado y ejecutar una patch real. No requiere cambios adicionales de código salvo que ese smoke descubra una discrepancia.
