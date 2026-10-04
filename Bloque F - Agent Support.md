# Bloque F - Agent Support / Work Plan

## Estado

**Investigación y análisis completados; implementación pendiente de aprobación.**

Este bloque propone una primera capability opcional de Agent Support para mantener una checklist estructurada del trabajo lógico de un agente dentro de una WorkSession. No es un scheduler, no ejecuta pasos y no reemplaza Process, Python, MCP Tasks ni la planificación propia del host.

## Objetivo

Agregar un Work Plan efímero y acotado que permita:

- mantener pasos con identidad estable;
- representar varios pasos activos o en espera simultáneamente;
- actualizar el plan con control optimista de concurrencia;
- sobrevivir entre llamadas mientras viva la WorkSession;
- desaparecer al cerrar/expirar la WorkSession;
- exponerse sólo en adapters/perfiles donde aporte valor.

## Hallazgos del código actual

La base actual encaja bien sin cambiar la arquitectura de ejecución:

- WorkSession ya es el scope explícito para estado que cruza llamadas;
- `InvocationRunner` ya aporta lease de sesión, cancelación, tracing y eventos;
- `work_close`/expiry ya controlan el lifecycle de la WorkSession;
- ResourceRegistry está orientado a recursos con handle/lifecycle propio; Work Plan no necesita handle;
- el adapter MCP registra tools explícitamente mediante `WithTools<T>()`;
- el Host no tiene todavía un sistema general de perfiles/features, por lo que F debe agregar sólo el mínimo necesario para exposición opcional.

No hay TODOs ni stubs en `src` que bloqueen este bloque.

## Investigación externa

### Codex

El `update_plan` actual sigue siendo una checklist/progress tool distinta de Plan Mode. Su schema usa una lista completa de pasos y estados `pending | in_progress | completed`, con como máximo un `in_progress`.

En Codex 0.152.0 la tool pasó a ser opt-in/default-off. Esto refuerza dos decisiones para Loom:

1. no copiar la restricción de un único paso activo;
2. no exponer Work Plan universalmente cuando el host ya ofrece planificación equivalente.

### Claude Code

Claude Code mantiene un sistema de tasks propio; en modo interactivo está habilitado por defecto y puede compartir una task list entre sesiones mediante `CLAUDE_CODE_TASK_LIST_ID`.

Esto demuestra utilidad real del estado estructurado externo al modelo, pero no obliga a Loom a implementar persistencia cross-session en F. El alcance inicial puede seguir siendo WorkSession-local.

### MCP Tasks

La extensión MCP Tasks 2026-07-28 modela ejecuciones asíncronas de requests/tools mediante task handles, polling, cancelación y resultado diferido.

No representa una checklist cognitiva del agente. Work Plan y MCP Tasks deben seguir siendo conceptos separados.

### MCP C# SDK

Loom usa ModelContextProtocol C# SDK 2.2.0, que continúa siendo la release estable más reciente observada durante esta investigación. El SDK permite registro explícito de tool types, por lo que la exposición opcional de Agent Support puede resolverse sin introducir un capability registry general.

## Alcance propuesto F1

Un único Work Plan efímero por WorkSession:

- revision monotónica;
- máximo **32 pasos**;
- orden definido por el array del snapshot;
- estados:
  - `pending`
  - `active`
  - `waiting`
  - `completed`
- múltiples `active` y `waiting` permitidos;
- texto por paso acotado a **512 caracteres**;
- IDs opacos generados por Loom para identidad estable.

No incluir:

- DAG/dependencias;
- scheduler;
- ejecución automática de pasos;
- links automáticos a ProcessHandle/Invocation;
- tareas duraderas fuera de WorkSession;
- multi-agent coordinator;
- MCP Tasks;
- conversación/prompts/memoria semántica.

## API pública propuesta

Para evitar confusión con tools nativas de hosts como `update_plan`, usar nombres explícitamente ligados a WorkSession.

### `work_plan_get`

```
work_plan_get(workId)
```

Devuelve el snapshot actual:

```json
{
  "revision": 0,
  "steps": []
}
```

Propiedades:

- nueva WorkSession comienza en revision `0`;
- read-only;
- idempotente;
- closed-world;
- una WorkSession cerrada/expirada conserva la semántica normal `resource_closed` / `resource_expired`.

### `work_plan_update`

```
work_plan_update(
    workId,
    expectedRevision,
    steps[]
)
```

Reemplaza atómicamente el snapshot completo.

Cada input step:

```json
{
  "id": null,
  "text": "Compilar proyecto",
  "status": "waiting"
}
```

Reglas propuestas:

- `id = null`: paso nuevo; Loom genera un ID opaco;
- `id != null`: debe corresponder a un paso existente del snapshot actual;
- omitir un paso existente lo elimina del nuevo snapshot;
- el orden del array es el orden del plan;
- IDs repetidos => `invalid_argument`;
- texto vacío/whitespace => `invalid_argument`;
- estado fuera del enum => schema/argument error;
- `expectedRevision` debe coincidir con la revision actual;
- revision stale => `conflict`, idealmente con `currentRevision` en `details`;
- una actualización exitosa incrementa revision y devuelve el snapshot normalizado con IDs asignados.

El full-snapshot update evita introducir un DSL de operaciones, permite reorder/removal naturalmente y mantiene el contrato pequeño.

## Diseño interno propuesto

### Core

Agregar:

```
LoomLCI.Core/AgentSupport/
  WorkPlanContracts.cs
  WorkPlanCapability.cs
```

WorkSession debe **poseer** el estado del plan. No crear un `ConcurrentDictionary<WorkId, Plan>` paralelo ni registrarlo como Resource.

Motivo: WorkSession ya es la fuente de verdad del lifecycle. El estado del plan debe quedar sincronizado con el mismo gate de estado de la sesión para evitar una carrera en la que un update termine después de que `work_close` haya marcado la sesión como cerrada.

Forma conceptual:

- snapshot vacío al crear WorkSession;
- get/update sólo mientras la sesión está Active;
- update protegido por el lock de estado de WorkSession;
- close/expiry descartan el snapshot;
- tombstone no necesita conservar texto del plan;
- `WorkPlanCapability` usa `InvocationRunner` para que las operaciones participen de tracing/cancelación/activity igual que las demás capabilities.

No usar ResourceRegistry.

### Observabilidad

Eventos mínimos:

- `WorkPlanUpdated`

Payload acotado:

- revision;
- stepCount;
- opcionalmente conteo por estado.

No copiar textos completos del plan al event bus por defecto.

### MCP

Agregar:

```
LoomLCI.Mcp/WorkPlanTools.cs
```

y registrar el tool type sólo cuando Agent Support esté habilitado.

Propuesta mínima de configuración:

```
AddLoomMcpStdio(enableWorkPlan: true|false)
```

o un pequeño options object equivalente.

No crear todavía un registry genérico de capabilities sólo para resolver este toggle.

Política inicial:

- perfil objetivo ChatGPT normal: enabled;
- hosts que ya tengan planificación propia (Codex/Claude): disabled por defecto en su integración específica.

Las ServerInstructions deben mencionar Work Plan sólo cuando esté expuesto.

## Annotations MCP

Propuesta cerrada para `work_plan_get`:

- readOnly = true
- idempotent = true
- destructive = false
- openWorld = false

Para `work_plan_update`:

- readOnly = false
- idempotent = false
- openWorld = false

`destructiveHint` conviene validarlo empíricamente antes de cerrar F1.2. Semánticamente un full replacement puede eliminar pasos, pero marcarlo destructive puede introducir prompts de confirmación indeseables para una mutation puramente interna/efímera. No conviene falsear la annotation sin observar primero el comportamiento del host objetivo.

## Concurrencia

La revision funciona como compare-and-swap:

1. dos agentes leen revision N;
2. ambos intentan update con `expectedRevision=N`;
3. sólo uno gana y produce N+1;
4. el otro recibe `conflict`;
5. debe volver a `work_plan_get`, reconciliar y reintentar.

Esto evita last-write-wins silencioso y deja abierta la posibilidad futura de multi-agent sin implementar un coordinator.

## Tests mínimos propuestos

### Core.Tests

1. nueva WorkSession devuelve plan vacío revision 0;
2. primer update genera IDs y revision 1;
3. IDs se preservan entre updates;
4. reorder funciona;
5. múltiples `active` y `waiting` son válidos;
6. stale revision devuelve `conflict`;
7. dos updates concurrentes sobre la misma revision: uno gana, otro conflict;
8. IDs duplicados se rechazan;
9. ID desconocido con revision vigente se rechaza;
10. límites de pasos/texto se validan;
11. omitir un paso lo elimina;
12. close vuelve el plan inaccesible y descarta contenido;
13. expiry idem;
14. plan no crea entries en ResourceRegistry.

### IntegrationTests / MCP

1. catálogo incluye/excluye las tools según feature flag;
2. schemas y enums correctos;
3. `work_plan_get -> update -> get` conserva snapshot;
4. stale revision se representa como error estructurado `conflict`;
5. close => `resource_closed`;
6. ServerInstructions sólo mencionan Work Plan cuando está habilitado.

### Fresh-agent

Con Agent Support habilitado:

- tarea suficientemente larga para justificar plan;
- el agente descubre las tools sin instrucciones privadas;
- crea un plan, mantiene varios pasos activos/waiting cuando corresponde y lo actualiza;
- se recupera de un conflict;
- no usa Work Plan de forma mecánica para una tarea trivial;
- cleanup correcto con `work_close`.

## Plan de implementación propuesto

### F1.1 - Core

- contracts;
- estado singleton dentro de WorkSession;
- CAS por revision;
- límites/validaciones;
- WorkPlanCapability;
- eventos;
- tests Core.

### F1.2 - MCP + exposición opcional

- `work_plan_get`;
- `work_plan_update`;
- schemas/descriptions/annotations;
- toggle mínimo por adapter/Host;
- ServerInstructions condicionales;
- IntegrationTests STDIO.

### F1.3 - Validación

- Debug/Release;
- smoke Secure MCP Tunnel;
- fresh-agent específico de Agent Support;
- decidir `destructiveHint` con evidencia del host real.

## Criterio de cierre

F1 queda cerrado cuando:

- Core no mantiene estado paralelo fuera de WorkSession;
- revision evita lost updates;
- close/expiry eliminan el plan;
- optional exposure funciona;
- no se duplica MCP Tasks;
- el catálogo y las instrucciones son autoexplicativos;
- tests Debug/Release quedan verdes;
- smoke/fresh-agent demuestran utilidad real sin forzar uso en tareas simples.

## Recomendación

Continuar con F1 antes de Computer, pero mantenerlo deliberadamente pequeño. Su valor principal no es “hacer un gestor de tareas”, sino validar una capa Agent Support opcional y robusta sobre WorkSession antes de entrar al bloque Computer, que agregará estado observable, input global y mucha más complejidad de Windows.

Si la evaluación fresh-agent muestra que ChatGPT no usa Work Plan de forma útil o que genera fricción, la feature debe poder quedar deshabilitada sin afectar Core execution.

## Fuentes

- Codex `update_plan`: https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/plan_spec.rs
- Codex Plan Mode vs `update_plan`: https://github.com/openai/codex/blob/main/codex-rs/collaboration-mode-templates/templates/plan.md
- Codex default-off discussion: https://github.com/openai/codex/issues/42365
- Claude Code environment/task list: https://code.claude.com/docs/en/env-vars
- MCP Tasks 2026-07-28: https://tasks.extensions.modelcontextprotocol.io/specification/draft/tasks
- MCP C# SDK tools: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tools/tools.md
