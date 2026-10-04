# Bloque F - Agent Support / Work Plan

## Estado

**F1.1 Core y F1.2 MCP implementados y validados; F1.3 (Secure MCP Tunnel + fresh-agent) pendiente.**

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
- read-only sobre el estado lógico del plan;
- la lectura refresca la actividad/idle timeout de la WorkSession, por lo que no debe anunciarse como idempotente en la metadata de Loom;
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

La investigación específica de F1.2 cierra las annotations así:

### `work_plan_get`

- `ReadOnly = true`
- `Destructive = false`
- `Idempotent = false`
- `OpenWorld = false`

MCP define `idempotentHint` formalmente como significativo sólo para tools no-read-only. Aun así Loom ya explicita `false` en operaciones read-only que refrescan lifetime, como `process_status`/`process_read`. `work_plan_get` también renueva actividad/idle timeout mediante `InvocationRunner`, por lo que se conserva esa política consistente.

### `work_plan_update`

- `ReadOnly = false`
- `Destructive = true`
- `Idempotent = false`
- `OpenWorld = false`

El full replacement puede modificar y eliminar steps al omitirlos. MCP define `destructiveHint=false` para mutations sólo aditivas, por lo que anunciar `false` aquí sería semánticamente incorrecto. Si un host introduce fricción de confirmación, F1.3 debe medir esa UX; no se debe falsear la annotation para evitar prompts.

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

#### Resultado de la investigación específica

F1.1 puede implementarse sólo con BCL/.NET 10 y el Core actual. No requiere nuevas dependencias NuGet, provider Windows, ResourceRegistry ni cambios en MCP/Host.

Archivos propuestos:

```text
src/LoomLCI.Core/AgentSupport/
  WorkPlanContracts.cs
  WorkPlanCapability.cs
src/LoomLCI.Core/Work/
  WorkSession.WorkPlan.cs
tests/LoomLCI.Core.Tests/
  WorkPlanCapabilityTests.cs
```

Además:

- `WorkSession` pasa a `partial` para mantener separado el estado de Agent Support sin inflar `WorkSessionManager.cs`;
- `Identifiers.cs` agrega `WorkPlanStepId` con IDs `step_<opaque>`;
- `LoomResult.cs` puede ampliar `Conflict(...)` con `details` opcionales para devolver `currentRevision` sin cambiar el código de error estable.

#### Ownership y lifecycle

El plan debe estar físicamente dentro de cada `WorkSession`, no en un diccionario paralelo de `WorkPlanCapability`.

Estado interno mínimo conceptual:

```text
revision: long = 0
steps: immutable/read-only snapshot = []
```

`Get` y `Update` se ejecutan bajo el mismo `_stateGate` que protege `WorkSessionState`.

Esto da una semántica linealizable frente a `work_close`:

- si update obtiene el gate primero, publica el nuevo snapshot y después close entra, marca Closing y descarta el plan;
- si close obtiene el gate primero, marca Closing/descarta el plan y el update falla con `resource_closed`;
- nunca puede quedar un plan escrito después del cierre.

Expiry es todavía más simple: el sweeper sólo puede comenzar expiry con `ActiveInvocationCount == 0`; `WorkPlanCapability` usa `InvocationRunner`, por lo que una operación de plan activa impide expiry mientras tiene su invocation lease.

El plan debe limpiarse al comenzar `TryBeginClose` / `TryBeginExpiry`, no esperar a `CompleteClose`. Los tombstones no conservan el contenido del plan.

No conviene migrar `_stateGate` a otra primitiva dentro de F1.1. .NET 10 recomienda `System.Threading.Lock` para código nuevo, pero el gate actual es un objeto privado dedicado y es correcto; cambiarlo sería churn no relacionado. Las secciones críticas del plan son pequeñas y no contienen `await`.

#### Snapshot inmutable

Los records de C# sólo son shallow-immutable: una propiedad `IReadOnlyList<T>` todavía puede apuntar a una colección mutable. Por eso no se debe devolver la lista interna ni confiar sólo en `record`.

Propuesta:

```text
WorkPlanSnapshot
  Revision: long
  Steps: IReadOnlyList<WorkPlanStep>
```

Cada update construye un array nuevo de `WorkPlanStep` y lo encapsula en una colección read-only cuya referencia mutable original no se filtra. El `WorkSession` reemplaza atómicamente la referencia al snapshot completo bajo `_stateGate`.

Los `WorkPlanStep` son valores inmutables (`id`, `text`, `status`), por lo que un snapshot publicado no cambia posteriormente y `Get` puede devolverlo sin copiar en cada lectura.

No hace falta `System.Collections.Immutable` ni `ConcurrentDictionary`.

#### Contratos Core propuestos

```text
WorkPlanStepStatus
  Pending
  Active
  Waiting
  Completed

WorkPlanStepId
WorkPlanStep
WorkPlanSnapshot
WorkPlanStepInput
WorkPlanUpdateRequest
```

Capability:

```text
GetAsync(WorkId, CancellationToken)
UpdateAsync(WorkPlanUpdateRequest, CancellationToken)
```

Operaciones internas sugeridas para observabilidad:

- `agent_support.work_plan.get`
- `agent_support.work_plan.update`

Ambas pasan por `InvocationRunner`, para reutilizar adquisición de WorkSession, cancellation, Activity y eventos de Invocation.

#### Revision / CAS

`revision` será `long` no negativo.

Reglas:

- sesión nueva: revision 0;
- cada `Update` exitoso incrementa exactamente una vez la revision, incluso si el snapshot resultante es equivalente al anterior;
- la revision nunca debe hacer wrap: `long.MaxValue` se trata como condición interna defensiva, no vuelve a 0;
- `expectedRevision < 0` => `invalid_argument`;
- `expectedRevision != currentRevision` => `conflict`;
- el error `conflict` devuelve `currentRevision` en `details` para recuperación determinística;
- el check de revision y el swap de snapshot ocurren en la misma sección crítica.

No usar `Interlocked.CompareExchange` sobre el snapshot: el mismo gate debe coordinar también state/close y validación de IDs. Un CAS lock-free separado agregaría complejidad sin resolver el lifecycle de WorkSession.

#### IDs

Cada paso nuevo (`id == null`) recibe un ID opaco generado por Loom, sugerido `step_<128-bit random hex>`, reutilizando `IdentifierFactory`.

- IDs existentes se comparan ordinal/case-sensitive;
- un ID no vacío que no pertenezca al snapshot actual => `invalid_argument`;
- IDs repetidos dentro del request => `invalid_argument`;
- el check de revision ocurre antes del check dinámico de IDs: si la revision está stale, la respuesta correcta es `conflict`, no un error secundario por IDs viejos.

Los IDs identifican pasos dentro del plan; no son ResourceHandles y no entran al ResourceRegistry.

#### Límites y texto

Mantener los límites propuestos:

- 0..32 pasos (lista vacía limpia el plan);
- máximo 512 Unicode scalar values por texto;
- texto obligatorio, no whitespace-only;
- texto normalizado con `Trim()`;
- rechazar CR/LF para que un step siga siendo una unidad breve de checklist;
- rechazar strings Unicode mal formados antes de contar longitud.

Para consistencia con las decisiones Unicode de Python, el límite no debe medirse con `string.Length` (UTF-16 code units). En .NET 10 puede validarse Unicode estricto y contar `Rune`/`EnumerateRunes`; `EnumerateRunes` por sí solo reemplaza secuencias inválidas por ReplacementChar, por lo que la validación de well-formed Unicode debe ocurrir primero.

También validar `Enum.IsDefined(status)` en Core; un enum de C# puede contener valores numéricos fuera de sus miembros declarados aunque el futuro schema MCP sea más restrictivo.

#### Orden de validación

Para evitar efectos secundarios y errores confusos:

1. validaciones estáticas antes de adquirir WorkSession: `workId`, revision no negativa, count, texto/status;
2. `InvocationRunner` adquiere la sesión y la action comprueba inmediatamente `token.ThrowIfCancellationRequested()` antes de leer/mutar el plan;
3. dentro de `_stateGate`: comprobar Active, comprobar `expectedRevision`, validar IDs contra snapshot actual, generar IDs nuevos, construir/swap snapshot;
4. fuera del gate: publicar evento y devolver snapshot.

No generar IDs ni publicar eventos en requests fallidos.

#### Observabilidad

Un único evento de dominio en F1.1:

- `WorkPlanUpdated`
- source: `agent_support.work_plan`
- workId + invocationId
- payload: `revision`, `stepCount` y conteos por status.

No incluir textos ni lista de IDs en el event bus: evita duplicar contenido del plan y mantiene eventos acotados.

No hace falta `WorkPlanCreated`: el plan vacío revision 0 es estado implícito de toda WorkSession.

#### Hallazgo que impacta F1.2

Como `GetAsync` pasa por `InvocationRunner`, leer el plan cuenta como actividad de la WorkSession y refresca su idle timeout. Esto es consistente con otras operaciones asociadas a WorkSession, pero significa que en F1.2 debe revisarse cuidadosamente `IdempotentHint`: el contenido devuelto es idempotente, pero la lectura afecta lifetime, de forma parecida a las decisiones ya tomadas para operations de Process que refrescan retención.

#### Tests Core refinados

Mínimo recomendado:

1. nueva WorkSession => revision 0 / steps vacíos;
2. primer update => revision 1 + IDs generados;
3. segundo update preserva IDs existentes;
4. reorder conserva identidad;
5. múltiples `active`/`waiting` válidos;
6. update con lista vacía limpia el plan e incrementa revision;
7. stale revision => `conflict` + `currentRevision`;
8. dos updates concurrentes con la misma revision => exactamente uno success y uno conflict;
9. ID duplicado => invalid_argument;
10. ID desconocido con revision vigente => invalid_argument;
11. 32 steps aceptados / 33 rechazados;
12. 512 Unicode scalars aceptados / 513 rechazados, incluyendo surrogate pairs/emoji;
13. Unicode mal formado rechazado;
14. whitespace-only y CR/LF rechazados;
15. enum inválido rechazado;
16. `work_close` vuelve get/update `resource_closed` y el contenido queda descartado;
17. expiry vuelve get/update `resource_expired`;
18. Get/Update refrescan activity mediante InvocationRunner;
19. update emite `WorkPlanUpdated` sin texto del plan en payload;
20. usar Work Plan no aumenta `ResourceRegistry.Count`.

No considero necesario un test probabilístico de race update-vs-close: la propiedad queda determinada por usar el mismo `_stateGate`; los tests de close, CAS concurrente y lifecycle cubren las fronteras observables.

#### Implementación y validación F1.1 ✓

Implementado:

- contratos `WorkPlanStepStatus`, `WorkPlanStep`, `WorkPlanSnapshot`, `WorkPlanStepInput` y `WorkPlanUpdateRequest`;
- `WorkPlanStepId` opaco `step_*` generado por Loom;
- estado del plan dentro de `WorkSession` mediante `partial WorkSession` + `WorkSession.WorkPlan.cs`;
- snapshots read-only sin filtrar arrays mutables;
- CAS lógico por `expectedRevision` bajo `_stateGate`;
- `conflict` con `currentRevision` en `details`;
- cleanup al comenzar close/expiry;
- límites de 32 pasos y 512 Unicode scalar values;
- Unicode estricto, texto single-line y normalización por `Trim()`;
- múltiples `active` / `waiting`;
- `WorkPlanUpdated` acotado sin textos/IDs;
- `WorkPlanCapability.GetAsync` / `UpdateAsync` sobre `InvocationRunner`;
- sin entradas nuevas en `ResourceRegistry` y sin nuevas dependencias NuGet.

Validación:

- Core Debug: **84/84**;
- Core Release: **84/84**;
- solución completa Debug: **195/195**;
- solución completa Release: **195/195**;
- Windows permanece **104/104**;
- Integration MCP permanece **7/7**;
- **0 warnings** en la validación final;
- `git diff --check` limpio antes de documentar/cerrar F1.1.

Los nuevos casos cubren revisión/CAS concurrente, IDs, reorder, límites, emoji/surrogates, Unicode inválido, snapshots no mutables, close/expiry, activity refresh, eventos acotados y ausencia de recursos adicionales.

### F1.2 - MCP + exposición opcional

#### Resultado de la investigación específica

F1.2 puede implementarse sobre el `ModelContextProtocol` **2.2.0** que ya usa LoomLCI; no hace falta actualizar paquetes. La versión 2.2.0 continúa siendo la release estable más reciente observada al 2026-10-04.

El SDK actual confirma que:

- `.WithTools<T>()` agrega `McpServerTool` al `IServiceCollection` y devuelve el mismo `IMcpServerBuilder`, por lo que el registro condicional es directo;
- las tools attribute-based resuelven sus constructores mediante DI al invocarse;
- `Description` y DataAnnotations alimentan JSON Schema 2020-12, pero el SDK no las ejecuta como validación runtime: el Core debe seguir siendo la autoridad;
- ServerInstructions se configuran por servidor y viajan en el handshake;
- no hace falta tool filtering dinámico ni `tools/list_changed` para un perfil estático.

#### Superficie pública cerrada

Agregar `LoomLCI.Mcp/WorkPlanTools.cs` con dos tools:

```text
work_plan_get(workId)
work_plan_update(workId, expectedRevision, steps[])
```

DTOs MCP separados de Core:

```text
WorkPlanStepDto
  id: string
  text: string
  status: pending | active | waiting | completed

WorkPlanSnapshotDto
  revision: integer
  steps: WorkPlanStepDto[]

WorkPlanStepInputDto
  id: string?       // null/omitido => step nuevo
  text: string
  status: pending | active | waiting | completed
```

No exponer enums C# directamente en el contrato MCP. El adapter convierte strings lowercase ↔ `WorkPlanStepStatus`, siguiendo el patrón actual de Process/Filesystem y evitando depender de defaults de serialización de enums.

Ambas tools usan `UseStructuredContent = true` y `OutputSchemaType = typeof(ToolEnvelope<WorkPlanSnapshotDto>)`. Los errores continúan por `McpToolResults.From`, así que un stale revision produce `IsError=true` y conserva en `structuredContent.error.details.currentRevision` la información necesaria para recuperación.

#### Schema de `work_plan_get`

Input obligatorio:

- `workId: string`.

Descripción debe explicar que:

- devuelve el snapshot lógico actual;
- no ejecuta, observa ni sincroniza procesos reales;
- conviene usarlo al retomar una tarea o antes de reconciliar un `conflict`;
- la lectura refresca el idle timeout de WorkSession.

Título propuesto: **Get work plan**.

#### Schema de `work_plan_update`

Inputs obligatorios:

- `workId: string`;
- `expectedRevision: integer`, `minimum: 0`;
- `steps: array`, `maxItems: 32`, permitiendo array vacío para limpiar el plan.

Cada item:

- `id`: opcional/nullable; cuando existe debe reutilizarse exactamente como fue devuelto;
- `text`: requerido, `minLength: 1`, `maxLength: 512` y pattern compatible para excluir CR/LF;
- `status`: requerido, enum `pending | active | waiting | completed`.

JSON Schema 2020-12 define `maxLength` en Unicode code points, alineado con el límite de 512 Unicode scalar values de Core para strings Unicode válidos. La regla whitespace-only/`Trim()` sigue validándose en Core porque el schema no debe duplicar toda la semántica de negocio.

El adapter debe validar defensivamente `steps == null`, items null y status desconocidos antes de mapear a Core, aun cuando el schema los desaconseje. El Core continúa validando todos los invariants reales porque MCP considera los argumentos no confiables y el SDK no aplica DataAnnotations en runtime.

Descripción de `work_plan_update` debe dejar explícito:

- reemplaza atómicamente el snapshot completo;
- `id = null` crea step;
- omitir un ID existente elimina ese step;
- preserve IDs devueltos por Loom;
- `expectedRevision` debe ser la última revision observada;
- ante `conflict`, hacer `work_plan_get`, reconciliar y reintentar;
- múltiples `active`/`waiting` son válidos;
- no ejecuta steps ni altera Process/Python/Filesystem.

Título propuesto: **Update work plan**.

#### Annotations cerradas

`work_plan_get`:

```text
ReadOnly    = true
Destructive = false
Idempotent  = false
OpenWorld   = false
```

`work_plan_update`:

```text
ReadOnly    = false
Destructive = true
Idempotent  = false
OpenWorld   = false
```

No habilitar MCP Tasks/task-augmented execution: ambas operaciones son cortas y síncronas; Work Plan no es MCP Tasks.

#### Exposición opcional

No introducir un capability registry ni tool filtering dinámico.

Cambiar el registro a:

```text
AddLoomMcpStdio(enableWorkPlan: false)
```

Implementación conceptual:

```text
builder = AddMcpServer(...)
  .WithStdioServerTransport()
  .WithTools<WorkTools>()
  .WithTools<ProcessTools>()
  .WithTools<FilesystemTools>()
  .WithTools<PythonTools>();

if (enableWorkPlan)
    builder.WithTools<WorkPlanTools>();
```

El default debe ser **false** porque Agent Support es deliberadamente opcional y un adapter/harness con planificación propia no debería recibirlo por accidente.

El Host actual, cuyo camino objetivo es ChatGPT normal mediante Secure MCP Tunnel, hace opt-in explícito:

```text
AddSingleton<WorkPlanCapability>();
AddLoomMcpStdio(enableWorkPlan: true);
```

No hace falta condicionar el registro Core: `WorkPlanCapability` puede estar disponible internamente aunque el adapter no la exponga.

Resultado esperado:

- Host ChatGPT actual: **19 tools** (17 existentes + 2 Work Plan);
- adapter con `enableWorkPlan=false`: **17 tools**.

La selección es estática al construir el servidor. No usar `ToolCollection` mutable ni `tools/list_changed`; además de ser innecesario, la adopción de cambios dinámicos de catálogo sigue siendo desigual entre clientes.

#### ServerInstructions

Construir la string base actual y agregar sólo cuando `enableWorkPlan=true` una instrucción breve equivalente a:

> When Work Plan tools are available, use them only for non-trivial multi-step tasks. They track logical progress but do not execute or monitor work. Preserve returned step IDs and revision; on conflict, reread and reconcile the plan.

Objetivos:

- evitar plan mecánico para tareas triviales;
- no confundir `waiting` con estado real de Process;
- enseñar el protocolo revision/IDs sin inflar cada prompt;
- no mencionar Work Plan cuando las tools no están registradas.

La descripción de cada tool sigue siendo autosuficiente; ServerInstructions sólo establece la política general de uso.

#### Wiring / archivos propuestos

```text
src/LoomLCI.Mcp/WorkPlanTools.cs
src/LoomLCI.Mcp/McpServiceCollectionExtensions.cs
src/LoomLCI.Host/Program.cs
tests/LoomLCI.IntegrationTests/McpStdioTests.cs
```

No tocar `LoomLCI.Windows` ni `ResourceRegistry`.

#### Estrategia de tests F1.2

1. **Contrato/self-description con Host habilitado**
   - catálogo contiene `work_plan_get` y `work_plan_update`;
   - títulos/descripciones correctos;
   - annotations exactas;
   - `work_plan_get` requiere `workId`;
   - `work_plan_update` requiere `workId`, `expectedRevision`, `steps`;
   - `expectedRevision.minimum = 0`;
   - `steps.maxItems = 32` y no exige `minItems > 0`;
   - nested `status` expone enum lowercase;
   - nested `text` expone `minLength/maxLength` y single-line pattern;
   - output schema corresponde al envelope/snapshot esperado.

2. **Opt-in/opt-out sin iniciar STDIO**
   - construir `ServiceCollection` con `AddLoomMcpStdio(false)` y resolver los `McpServerTool` registrados: Work Plan ausente;
   - repetir con `true`: Work Plan presente;
   - resolver `McpServerOptions` y comprobar que ServerInstructions sólo mencionan Work Plan en el caso habilitado;
   - disponer el ServiceProvider de forma async; no arrancar el hosted STDIO transport.

3. **Roundtrip STDIO real con Host habilitado**
   - `work_create`;
   - `work_plan_get` => revision 0 / empty;
   - update con un `active` y un `waiting`, ambos nuevos;
   - comprobar revision 1 e IDs `step_*`;
   - get preserva snapshot;
   - stale update con revision 0 => `IsError=true`, code `conflict`, `details.currentRevision=1`;
   - update válido con IDs existentes y reorder/status changes;
   - `work_close`;
   - get posterior => `resource_closed`.

4. **Adapter robustness**
   - status string inválido => `invalid_argument`, no excepción genérica;
   - array vacío limpia el plan;
   - no duplicar el resultado completo en `Content`: mantener el patrón textual corto + `structuredContent`.

No hace falta repetir en F1.2 los 24 casos de invariants ya cubiertos por Core.Tests; Integration debe probar frontera MCP, wiring y serialización.

#### Criterio de cierre F1.2

F1.2 queda listo cuando:

- el Host objetivo expone 19 tools y un adapter opt-out conserva 17;
- contrato JSON/schema es autoexplicativo;
- annotations representan el comportamiento real, sin optimizarlas artificialmente para approvals;
- ServerInstructions son condicionales;
- stale revision es recuperable desde structuredContent;
- el roundtrip STDIO demuestra get/update/conflict/close;
- suites Debug/Release completas quedan verdes y sin warnings.

El smoke por Secure MCP Tunnel y el comportamiento natural de un fresh-agent pertenecen a F1.3, no deben mezclarse con el criterio de implementación F1.2.

#### Implementación y validación F1.2 ✓

Implementado:

- `LoomLCI.Mcp/WorkPlanTools.cs`;
- `work_plan_get` y `work_plan_update` con structured content;
- DTOs MCP separados del enum Core;
- status públicos lowercase `pending | active | waiting | completed`;
- schema de `expectedRevision`, `steps`, nested status/text y límite de 32 pasos;
- annotations cerradas:
  - get: read-only, non-destructive, non-idempotent, closed-world;
  - update: mutating, destructive, non-idempotent, closed-world;
- stale revision preserva `error.details.currentRevision`;
- `AddLoomMcpStdio(enableWorkPlan: false)` como opt-in estático;
- Host objetivo registra `WorkPlanCapability` y llama `AddLoomMcpStdio(enableWorkPlan: true)`;
- catálogo esperado: 17 tools con opt-out y 19 con Work Plan habilitado;
- ServerInstructions mencionan Work Plan sólo en la rama habilitada y recomiendan usarlo sólo para tareas multi-step no triviales.

Validación F1.2:

- build Host Debug: **0 warnings / 0 errors**;
- Integration Debug: **9/9**;
- solución completa Debug: **197/197**;
- solución completa Release: **197/197**;
- Core permanece **84/84**;
- Windows permanece **104/104**;
- Integration queda **9/9**;
- `git diff --check` limpio.

Los tests F1.2 verifican:

- opt-in/opt-out por cantidad de registrations MCP (17 vs 19);
- ServerInstructions del Host habilitado;
- títulos, descripciones y annotations;
- schema de inputs nested;
- roundtrip STDIO real `get -> update -> get`;
- conflict stale con `currentRevision`;
- reconciliación con IDs existentes;
- status inválido como `invalid_argument`;
- array vacío para limpiar;
- `resource_closed` después de `work_close`.

Durante la validación se detuvo una instancia previa de `LoomLCI.Host` que bloqueaba las DLL Debug. Eso fue sólo una condición operativa del túnel existente; la reconexión/smoke real queda deliberadamente para F1.3.

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
- Microsoft Learn - `lock`: https://learn.microsoft.com/dotnet/csharp/language-reference/statements/lock
- Microsoft Learn - synchronization: https://learn.microsoft.com/dotnet/standard/threading/synchronizing-data-for-multithreading
- Microsoft Learn - records / shallow immutability: https://learn.microsoft.com/dotnet/csharp/language-reference/builtin-types/record
- Microsoft Learn - `String.EnumerateRunes`: https://learn.microsoft.com/dotnet/api/system.string.enumeraterunes?view=net-10.0
- MCP Tool Annotations: https://blog.modelcontextprotocol.io/posts/2026-03-16-tool-annotations/
- MCP C# SDK 2.2 tools/schema/runtime validation: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tools/tools.md
- MCP C# SDK `WithTools<T>` contract (2.2.0 local XML docs / upstream SDK)
- NuGet `ModelContextProtocol` 2.2.0: https://www.nuget.org/packages/ModelContextProtocol/2.2.0
- JSON Schema 2020-12 `maxLength`: https://json-schema.org/draft/2020-12/json-schema-validation#section-6.3.1
