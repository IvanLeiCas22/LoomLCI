# Bloque F - Agent Support / Work Plan

## Estado

**Bloque F cerrado. F1.1 Core + F1.2 MCP implementados/validados; F1.3 pasó smoke por Secure MCP Tunnel y A/B fresh-agent final 2/3 positivos + 2/2 negativos con skill 0.2.1.**

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

Título propuesto: **Create or update work plan**.

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

#### Resultado de la investigación específica

F1.3 no necesita más código antes de probarse. Es una validación de integración real y ergonomía del agente sobre la superficie ya implementada.

Estado previo confirmado:

- repo limpio en `main`;
- runtime administrado del Secure MCP Tunnel: `healthy=true`, `runtime_state=ready`, proceso activo, `healthz`/ `readyz` OK;
- Host F1.2 expone 19 tools cuando Work Plan está habilitado;
- suites F1.2: 197/197 Debug y Release;
- la app `LoomLCI MCP` tiene actualmente permiso específico **Allow all actions**, por lo que en esta instalación no se espera una confirmación por cada `work_plan_update`;
- en el preflight inicial, la skill privada de LoomLCI no enumeraba Work Plan ni Python y declaraba que el schema MCP vivo era la fuente de verdad. Se mantuvo sin cambios durante las primeras corridas para medir adopción natural antes de cualquier ajuste.

Las annotations MCP siguen siendo hints y no garantías de UX. ChatGPT además decide approvals según permisos de la app, contexto e impacto. Por eso F1.3 debe observar comportamiento real sin reinterpretar un prompt de aprobación como verdad semántica del protocolo.

OpenAI documenta además que los cambios de herramientas de una app MCP pueden quedar detrás de un snapshot/catálogo aprobado y requerir refresh/revisión. Por eso una ausencia inicial de las dos tools nuevas en ChatGPT debe diagnosticarse primero como posible catálogo stale, no como fallo de LoomLCI.

#### Fase 0 - Preflight

Antes de cualquier prueba:

1. confirmar repo limpio;
2. confirmar tunnel `healthy/ready`;
3. confirmar que el Host local es el commit F1.2 esperado;
4. abrir un chat nuevo de ChatGPT normal con LoomLCI seleccionado;
5. comprobar que el catálogo visible contiene **19 tools**, incluyendo:
   - `work_plan_get`;
   - `work_plan_update`.

Si ChatGPT continúa viendo 17 tools:

- no ejecutar todavía el fresh-agent;
- refrescar/revisar las acciones de la app para que ChatGPT tome el catálogo MCP actual;
- repetir el preflight;
- sólo considerar un bug de Loom si el Host/túnel expone 19 pero el catálogo actualizado sigue sin transportar las nuevas tools.

No modificar la skill/plugin para mencionar Work Plan antes de las pruebas.

#### Fase 1 - Smoke determinista por Secure MCP Tunnel

Objetivo: demostrar que la misma semántica validada por STDIO atraviesa ChatGPT + app + túnel.

Usar una WorkSession temporal y no modificar archivos.

Secuencia:

1. `work_create`;
2. `work_plan_get`:
   - revision 0;
   - steps vacíos;
3. `work_plan_update(expectedRevision=0)` con dos pasos nuevos:
   - uno `active`;
   - uno `waiting`;
4. comprobar:
   - revision 1;
   - IDs `step_*`;
   - status lowercase;
5. `work_plan_get` y verificar persistencia exacta;
6. enviar deliberadamente un update stale con revision 0;
7. comprobar error semántico:
   - `conflict`;
   - idealmente `details.currentRevision=1` accesible al agente;
8. recuperar correctamente:
   - releer si hace falta;
   - reconciliar;
   - update con revision vigente y mismos IDs;
9. limpiar plan con array vacío;
10. `work_close`;
11. `work_plan_get` posterior => `resource_closed`.

Criterio importante: no instruir al agente sobre cómo está implementado el Core. Sólo usar el contrato público.

El wrapping externo de `conflict` o `resource_closed` puede diferir del envelope MCP. Es aceptable si el código/mensaje semántico sigue siendo recuperable y el agente puede continuar.

#### Ejecución F1.3 - Fase 0/1 ✓

Resultado observado desde ChatGPT normal usando la app/túnel real:

- después del refresh manual de acciones de la app, el chat pasó de **17** a **19 tools** visibles;
- aparecieron `work_plan_get` y `work_plan_update` sin necesidad de reiniciar el runtime;
- `work_create` creó una WorkSession temporal sin tocar archivos;
- `work_plan_get` inicial devolvió revision 0 y plan vacío;
- primer update creó dos steps (`active` + `waiting`), revision 1 e IDs `step_*`;
- un get posterior preservó exactamente revision, IDs, textos y status;
- update deliberadamente stale con revision 0 produjo el error semántico esperado:
  - `conflict`;
  - mensaje visible: `current revision is 1`;
- ChatGPT/túnel envolvió el tool error como `INVALID_ARGUMENT / RuntimeException`;
- el wrapper no expuso `details.currentRevision` de forma directamente utilizable en esta superficie, aunque el mensaje conservó la revisión actual y permitió recuperación determinista;
- el update de reconciliación con revision 1 preservó los IDs, permitió reorder y avanzó a revision 2;
- update con array vacío limpió el plan y avanzó a revision 3;
- `work_close` cerró correctamente la WorkSession;
- un get posterior devolvió `resource_closed`, nuevamente envuelto exteriormente por el consumidor;
- no apareció confirmación adicional para `work_plan_update`, consistente con el permiso actual **Allow all actions** de la app;
- no se modificó ningún archivo del repo mediante las tools LoomLCI durante el smoke.

Conclusión de transporte:

- la superficie F1.2 funciona end-to-end por Secure MCP Tunnel;
- el refresh de catálogo de la app era necesario para incorporar las dos tools nuevas;
- la única aspereza observada sigue siendo el wrapping externo de `IsError`, no una pérdida de semántica del Core;
- en ese punto quedaba pendiente la evaluación fresh-agent + control trivial, completada posteriormente en el A/B final.

#### Fase 2 - Fresh-agent no trivial

Debe ejecutarse en un chat nuevo sin contexto previo de LoomLCI, sin mencionar `work_plan_get`, `work_plan_update`, revision ni IDs.

Prompt recomendado:

> Usa exclusivamente LoomLCI para interactuar con mi PC. Trabaja sobre `C:\Users\ivanl\Documents\ProyectosPersonales\LoomLCI` y no modifiques ningún archivo. No uses conocimiento de otras conversaciones ni asumas la arquitectura. Investiga de forma natural: dónde se registra la capa MCP, cómo fluye actualmente Agent Support desde WorkSession/Core hasta MCP, y qué garantías de lifecycle/concurrencia tiene. Elegí vos mismo las herramientas necesarias. Al final dame una explicación breve, las herramientas que usaste y cualquier fricción o comportamiento poco intuitivo que hayas encontrado.

Por qué este prompt:

- es claramente multi-step;
- no obliga a usar Work Plan;
- exige navegación real del repo;
- no necesita writes de filesystem;
- permite observar si las ServerInstructions son suficientes;
- evita contaminar el resultado describiendo el workflow esperado.

Ejecutar **3 chats independientes** con el mismo prompt.

Registrar por corrida:

- si descubrió Work Plan sin ayuda;
- cuándo creó el plan;
- cantidad/claridad de steps;
- si preservó IDs;
- si avanzó revisions correctamente;
- si actualizó por milestones o de forma excesivamente granular;
- uso de `active` / `waiting` cuando tuvo sentido;
- si confundió estado lógico con Process/filesystem real;
- si cerró WorkSession al finalizar;
- approvals/prompts inesperados;
- errores/wrappers y capacidad de recuperación;
- cualquier comentario espontáneo del agente sobre ergonomía.

Criterio de utilidad:

- **3/3 correctas**: excelente;
- **2/3 correctas**: aceptable para F1, documentando variabilidad;
- **0-1/3 correctas**: no cerrar F1; revisar ServerInstructions/descripciones antes de Computer.

Una corrida cuenta como correcta si el agente usa Work Plan cuando lo considera útil y lo mantiene coherentemente. No es requisito que use todos los status ni que actualice después de cada tool call.

#### Resultado fresh-agent no trivial

Se ejecutaron tres chats nuevos independientes con el mismo prompt, sin mencionar Work Plan ni sus tools.

Resultado:

- **Fresh-agent #1: 0/1** — investigó correctamente la arquitectura, entendió Work Plan, pero no usó `work_plan_get` ni `work_plan_update`;
- **Fresh-agent #2: 0/1** — mismo patrón: comprendió Agent Support/Work Plan y resolvió la tarea con WorkSession + Filesystem, sin adoptar Work Plan;
- **Fresh-agent #3: 0/1** — nuevamente entendió el flujo MCP/Core, lifecycle y concurrencia, pero no usó ninguna tool de Work Plan.

En las tres corridas:

- la tarea era no trivial y multi-step;
- el agente descubrió conceptualmente Work Plan durante la investigación;
- no hubo confusión entre plan lógico y estado real;
- no se modificaron archivos;
- la WorkSession de inspección se cerró correctamente;
- la navegación se resolvió con herramientas de filesystem;
- aparecieron asperezas menores de volumen/ruido de búsqueda, no fallos de Agent Support.

**Resultado de adopción natural: 0/3.**

Esto falla deliberadamente el criterio de cierre definido para F1.3. La feature funciona técnicamente y es descubrible conceptualmente, pero las instrucciones actuales no inducen su uso natural en una tarea claramente multi-step.

La señal es especialmente clara porque el agente fue capaz de explicar Work Plan correctamente después de descubrirlo en el código, pero aun así no lo consideró útil para organizar su propia ejecución. Por lo tanto, el próximo paso debe enfocarse en **ergonomía/instrucciones públicas**, no en Core, lifecycle, concurrencia ni transporte.

Antes de cambiar nada:

- completar opcionalmente el control trivial para conservar la evidencia negativa de sobreplanning;
- investigar específicamente por qué las ServerInstructions actuales ("use Work Plan only for non-trivial multi-step tasks") no son suficientes para gatillar uso;
- revisar si conviene reforzar cuándo crear/actualizar el plan y qué granularidad esperar, sin convertirlo en uso obligatorio ni contaminar tareas simples;
- repetir fresh-agent después del ajuste con prompts equivalentes, no idénticos al punto de sobreentrenar la prueba.

#### Investigación específica post 0/3 - adopción natural

La evidencia 0/3 no apunta a un defecto del Core. El problema está en la selección/ergonomía del Work Plan como herramienta auxiliar del agente.

##### Hallazgo 1 - la ServerInstruction actual es una restricción, no un trigger

Texto actual:

> Use Work Plan tools only for non-trivial multi-step tasks.

La frase define cuándo **no** usar la feature, pero no recomienda positivamente usarla cuando la condición sí se cumple. Un agente puede resolver una tarea multi-step sin Work Plan y seguir cumpliendo literalmente la instrucción.

Esto coincide con las tres corridas: los agentes entendieron Work Plan al encontrarlo en el código, pero no lo incorporaron a su propio workflow.

La guía de OpenAI para function/tool calling recomienda describir explícitamente **cuándo y cómo** usar una función y usar instrucciones de sistema para indicar cuándo usarla y cuándo no. La guía de metadata para plugins también recomienda orientar la descripción a intención/selección, no comenzar por detalles internos de implementación.

##### Hallazgo 2 - las descripciones actuales explican contrato, no selección

`work_plan_get` empieza por `Returns the current logical Work Plan snapshot...` y sólo recomienda uso al resumir/reconciliar.

`work_plan_update` empieza por `Atomically replaces the complete logical Work Plan snapshot...`.

Ambas descripciones son precisas, pero están optimizadas para **cómo funciona la operación una vez elegida**, no para ayudar al modelo a decidir **por qué debería elegirla ahora**.

OpenAI recomienda que la descripción de una tool explique explícitamente qué objetivo resuelve, cuándo usarla y cómo distinguirla de alternativas.

##### Hallazgo 3 - crear el primer plan tiene fricción evitable

En una WorkSession nueva, el Work Plan empieza determinísticamente vacío en revision 0. Sin embargo, la descripción pública de `work_plan_update` sólo dice que `expectedRevision` debe venir del último `work_plan_get` o update exitoso.

Eso induce el flujo inicial `work_create -> work_plan_get -> work_plan_update`.

Para una feature auxiliar, dos llamadas de bookkeeping antes del trabajo sustantivo son un costo apreciable. No hace falta cambiar Core ni relajar CAS: la semántica actual ya permite `work_create -> work_plan_update(expectedRevision=0)` cuando la WorkSession acaba de ser creada. Para una sesión reutilizada/resumida, `work_plan_get` sigue siendo correcto.

##### Hallazgo 4 - Codex obtiene adopción con instrucciones mucho más operativas

Las instrucciones públicas actuales de Codex para `update_plan` no se limitan a `úsalo para tareas complejas`. Explican para qué sirve, cuándo usarlo, cuándo no usarlo, granularidad y mantenimiento del plan durante el trabajo.

En particular, enumeran triggers positivos como trabajo no trivial con múltiples acciones, fases/dependencias, ambigüedad, checkpoints y prompts con múltiples objetivos. La definición de la tool en sí es pequeña; gran parte de la adopción proviene de instrucciones de workflow.

##### Hallazgo 5 - ServerInstructions es la capa correcta para el workflow cruzado

La documentación oficial de MCP recomienda ServerInstructions precisamente para relaciones entre tools y workflows que no pertenecen a la descripción individual de una tool. El artículo oficial reporta además una evaluación del GitHub MCP Server donde una instrucción explícita de workflow mejoró la adherencia al patrón esperado.

Por lo tanto, reforzar ServerInstructions no es un workaround específico de ChatGPT: es el mecanismo previsto por MCP.

##### Hallazgo 6 - la skill del plugin también omite Work Plan, pero no conviene tocarla primero

La skill privada actual enumera un `Flujo general` con WorkSession, Filesystem, Process y cleanup, pero no menciona Work Plan. Eso puede reforzar indirectamente un workflow sin checklist.

Sin embargo, la propia skill declara que los schemas/descripciones vivos del MCP son la fuente de verdad. Duplicar reglas ahora haría más difícil saber qué capa corrigió la adopción y aumentaría riesgo de drift.

Recomendación experimental:

1. corregir primero sólo la superficie MCP: ServerInstructions + descriptions;
2. dejar la skill sin cambios;
3. refrescar catálogo y repetir fresh-agent;
4. sólo si la adopción sigue insuficiente, agregar una regla mínima a la skill.

##### Hallazgo 7 - no cambiar `destructiveHint` para mejorar adopción

`work_plan_update` hace full replacement y puede eliminar steps existentes. Mantener `Destructive=true` sigue siendo defendible según la semántica MCP de overwrite/destructive updates.

Aunque los hints pueden afectar UX, la instalación actual tiene `Allow all actions` y el smoke no mostró fricción. Cambiar la annotation sólo para aumentar tool selection mezclaría seguridad/semántica con ergonomía y no está justificado por la evidencia.

#### Ajuste propuesto para F1.3

No cambiar Core, schemas ni API.

**1. ServerInstructions**

Reemplazar la regla actual por una guía positiva y acotada, conceptualmente:

> Use Work Plan for non-trivial work with multiple meaningful phases, dependent actions, or checkpoints. Skip it for simple lookups or short single-step tasks. On a newly created WorkSession, you may create the initial plan directly with expectedRevision=0. Keep a concise plan of a few outcome-oriented steps and update it at meaningful milestones, not after every tool call. Work Plan tracks logical progress only and does not execute or monitor real work. Preserve returned step ids and revision; on conflict, reread, reconcile, and retry.

La redacción final debería mantenerse corta; no copiar el manual completo de Codex.

**2. `work_plan_update` description**

- empezar por intención de selección: crear/mantener una checklist breve para trabajo multi-step no trivial;
- aclarar que en una WorkSession recién creada puede iniciarse directamente con `expectedRevision=0`;
- indicar updates por milestones, no por cada tool call;
- luego mantener full replacement, IDs, conflict y semántica de no-ejecución.

Opcionalmente cambiar el Title visible de `Update work plan` a `Create or update work plan`; el nombre MCP `work_plan_update` no necesita romperse.

**3. `work_plan_get` description**

Aclarar que se usa al reanudar una WorkSession existente o reconciliar conflict, y que no hace falta llamarlo como ritual inmediatamente después de `work_create` porque una sesión nueva comienza en revision 0.

**4. No tocar inicialmente**

- Core;
- CAS/revision;
- statuses;
- límites;
- ResourceRegistry;
- plugin skill;
- annotations;
- tool count.

#### Evaluación propuesta después del ajuste

Usar un pequeño golden set con positivos y negativos, siguiendo la recomendación de OpenAI de evaluar selección de tools con prompts etiquetados.

**Positivos (3 chats):** investigación multiarchivo con varias preguntas; implementación acotada con inspección -> edición -> tests; debugging con localizar causa -> reproducir -> corregir -> verificar.

Ningún prompt debe mencionar Work Plan. Éxito: al menos 2/3 usan Work Plan coherentemente, con pocos pasos útiles y updates por milestones.

**Negativos (2 chats):** leer `global.json` y devolver SDK; leer un archivo conocido y responder una pregunta puntual.

Éxito: 2/2 no usan Work Plan.

Esto es mejor que repetir tres veces exactamente el mismo prompt: valida generalización y evita optimizar la instrucción para un caso único.

#### Decisión recomendada

El ajuste mínimo con mejor relación beneficio/riesgo es:

1. reforzar ServerInstructions con trigger positivo + anti-overplanning + milestone cadence;
2. hacer que las descriptions de `get/update` expliquen selección además de contrato;
3. explicitar revision 0 inicial para evitar un `get` innecesario;
4. mantener Core, CAS y annotations intactos;
5. retestar antes de tocar la skill del plugin.

No hay evidencia para rediseñar Work Plan ni agregar nuevas tools en F1.3.

#### Implementación del ajuste de ergonomía ✓

Se aplicó únicamente sobre la superficie MCP:

- ServerInstructions ahora usan un trigger positivo para trabajo con múltiples fases, acciones dependientes o checkpoints;
- también indican explícitamente omitir Work Plan en lookups simples/tareas single-step;
- una WorkSession recién creada se documenta como plan vacío en revision 0 y permite inicializar directamente con `work_plan_update(expectedRevision=0)`;
- se recomienda una checklist breve, outcome-oriented, actualizada por milestones y no después de cada tool call;
- `work_plan_update` pasó a título visible **Create or update work plan** y su descripción prioriza intención/selección antes del contrato de full replacement;
- `work_plan_get` aclara que sirve para resumir/reanudar o reconciliar conflict y que no hace falta como ritual de inicialización;
- la descripción del parámetro `expectedRevision` explicita revision 0 para una WorkSession nueva;
- Core, CAS, statuses, límites, annotations, tool names/count y skill del plugin permanecieron sin cambios.

Validación:

- Host Release: 0 warnings / 0 errors;
- Release: Core 84/84, Windows 104/104, Integration 9/9 = **197/197**;
- Host Debug: 0 warnings / 0 errors;
- Debug: Core 84/84, Windows 104/104, Integration 9/9 = **197/197**;
- `git diff --check` limpio;
- runtime del túnel reconstruido sobre Host Debug actualizado y restaurado a `healthy/ready`.

Nota del harness de tests: `McpStdioTests.GetHostDll` usa Host Debug por defecto incluso cuando la suite corre en Release. Para validar Release contra el binario correcto se apuntó `LOOMLCI_TEST_HOST_DLL` al Host Release explícitamente. No es un fallo de LoomLCI.

En ese momento, el siguiente paso fue refrescar nuevamente el catálogo de la app e iniciar el golden set fresh-agent positivo/negativo; esa etapa quedó completada posteriormente.

#### Resultado del golden set post-ajuste

Tras refrescar la app se ejecutaron cinco chats nuevos, sin mencionar Work Plan.

**Positivos:**

- investigación multiarchivo sobre lifecycle de process output: **falló adopción**; resolvió correctamente con WorkSession + Filesystem, sin Work Plan;
- implementación documental acotada con inspección -> edición -> validación: **pasó**; usó Work Plan espontáneamente junto con Filesystem/Process;
- debugging del harness Release/Debug: **falló adopción**; investigó y verificó correctamente, pero sin Work Plan.

Resultado positivo: **1/3**, por debajo del umbral 2/3.

**Negativos:**

- leer `global.json` y devolver SDK: **pasó**; sin Work Plan;
- leer `McpServiceCollectionExtensions.cs` y responder si Work Plan está enabled por default: **pasó**; sin Work Plan.

Resultado anti-overplanning: **2/2**.

Interpretación:

- el ajuste MCP mejoró adopción respecto del 0/3 inicial, pero sigue siendo insuficiente;
- no hay señal de sobreplanning en tareas simples;
- Work Plan parece seleccionarse con más naturalidad cuando la tarea incluye mutación + verificación que en investigación/debugging read-only;
- la próxima hipótesis a probar es la **skill del plugin**, que actualmente describe explícitamente WorkSession/Filesystem/Process pero omite Work Plan;
- antes de tocar Core o agregar nuevas tools, conviene hacer un segundo ajuste mínimo únicamente en la skill, manteniendo ServerInstructions/descriptions actuales;
- el retest posterior debe conservar positivos de investigación/debugging y negativos simples para comprobar que la skill mejora recall sin degradar precisión.

La modificación documental realizada por el positivo #2 en `Especificacion interna v0.1.md` se conserva como cambio pendiente independiente y no forma parte de este registro.

#### Investigación específica - segundo ajuste en la skill del plugin

El golden set post-ajuste MCP dio 1/3 positivos y 2/2 negativos. La evidencia indica un problema de recall de Work Plan en workflows no triviales, sin señal de sobreplanning en tareas simples.

##### Hallazgo 1 - la skill actual prescribe un workflow completo que omite Work Plan

La skill privada `skills/loomlci/SKILL.md` contiene un `Flujo general` numerado que enseña explícitamente:

- crear/reutilizar WorkSession;
- explorar filesystem;
- elegir pipes/terminal;
- leer/status de procesos;
- terminate/cleanup;
- cerrar WorkSession.

Work Plan no aparece en ese recorrido. Por eso un agente puede seguir la skill correctamente de punta a punta sin siquiera considerar la checklist lógica, aunque las ServerInstructions MCP sí la recomienden.

Esto encaja con el golden set: los negativos simples no sobreplanificaron, pero dos tareas read-only no triviales siguieron exactamente el recorrido WorkSession + Filesystem/Process sin Work Plan.

##### Hallazgo 2 - la skill es la capa correcta para esta segunda intervención

La documentación oficial de OpenAI define las skills como la capa de workflow alrededor de las MCP tools: deben enseñar cuándo llamar tools, en qué orden, cómo manejar decisiones y qué constituye un resultado correcto. El servidor MCP conserva live data/actions/contracts.

Por lo tanto, agregar a la skill la decisión `cuándo crear/mantener Work Plan` no viola la separación de responsabilidades; al contrario, corrige el hueco entre capability y workflow.

##### Hallazgo 3 - no hace falta cambiar la metadata de activación de la skill

La descripción actual de la skill ya cumple su objetivo de activación: se carga cuando el usuario pide trabajar sobre su PC mediante LoomLCI. En todas las corridas del golden set el agente utilizó LoomLCI correctamente.

El problema ocurre **después** de que el plugin/skill ya está seleccionado. Cambiar `name`, `description`, `defaultPrompt`, `shortDescription` o `longDescription` agregaría variables al experimento sin atacar la causa observada.

OpenAI recomienda separar problemas de activation/selection de problemas del workflow una vez activado. Aquí la activación no es el cuello de botella.

##### Hallazgo 4 - el ajuste debe ser deliberadamente corto

No conviene copiar las ServerInstructions completas dentro de la skill. Eso produciría drift y dos fuentes de verdad para detalles de protocolo.

La skill sólo necesita cuatro decisiones:

1. trigger positivo: tarea no trivial con varias fases significativas, acciones dependientes o checkpoints;
2. trigger negativo: lookup simple o tarea corta de un solo paso;
3. timing: si aplica, inicializar el plan inmediatamente después de `work_create`, antes del trabajo sustantivo;
4. cadence/granularidad: pocos pasos outcome-oriented y updates sólo en milestones.

Los detalles de IDs, revision/CAS, conflicts, límites y statuses deben seguir delegados al schema/descriptions vivos del MCP.

##### Hallazgo 5 - conviene mencionar explícitamente la tool inicial

La guía oficial de skills recomienda decir qué tools usar y en qué orden cuando el workflow depende de MCP.

Para eliminar ambigüedad en investigación/debugging, la skill debería indicar que una WorkSession recién creada parte en revision 0 y que, cuando Work Plan aplica, puede iniciarse directamente con `work_plan_update(expectedRevision=0)` tras `work_create`.

No hace falta enseñar `work_plan_get` en el flujo normal inicial; su uso para resume/conflict ya está cubierto por el schema vivo.

##### Hallazgo 6 - no tocar todavía manifest/README salvo el bump técnico de versión

El plugin manifest y README siguen algo desactualizados respecto de Python/Agent Support, pero eso no explica el fallo actual: el usuario invoca LoomLCI explícitamente y las tools están disponibles.

Actualizar esa metadata al mismo tiempo confundiría el A/B. Para esta iteración conviene modificar sólo `SKILL.md` y realizar el bump técnico requerido de versión del plugin (`0.2.0` -> `0.2.1`) en los manifests repetidos cuando se publique el update.

El README puede quedar para la reconciliación posterior a F1.3.

#### Ajuste mínimo recomendado para la skill

Insertar un único paso inmediatamente después del actual paso de WorkSession, conceptualmente:

> Si la tarea es no trivial y requiere varias fases significativas, acciones dependientes o checkpoints, usa Work Plan en esa WorkSession. Inicialízalo inmediatamente después de `work_create` con `work_plan_update(expectedRevision=0)` en una sesión nueva, antes del trabajo sustantivo. Mantén pocos pasos orientados a resultados y actualízalos sólo en hitos; omite Work Plan para lookups simples o tareas cortas de un solo paso. Para IDs, revisions, conflicts, límites y demás semántica, sigue el schema vivo.

Después se renumeran los pasos existentes. No hace falta una sección nueva ni ejemplos extensos.

##### Por qué esta formulación

- aumenta recall precisamente en investigación/debugging read-only, donde falló;
- conserva un anti-trigger explícito para proteger los 2/2 negativos;
- obliga a tomar la decisión temprano, antes de que el agente ya haya arrancado el trabajo sustantivo sin plan;
- evita duplicar detalles contractuales;
- mantiene al schema MCP como fuente de verdad;
- es compatible con el rol oficial de skills como workflow layer.

#### Retest recomendado

Después de actualizar el plugin y refrescarlo, repetir **exactamente el mismo golden set de 5 prompts** usado antes.

En esta iteración conviene reutilizar los mismos prompts porque el objetivo es un A/B controlado del único cambio: skill sin regla de Work Plan vs skill con regla mínima.

Criterio:

- positivos: al menos 2/3 usan Work Plan coherentemente;
- negativos: 2/2 siguen sin usarlo;
- en positivos, creación temprana tras `work_create`, pocos pasos y updates por milestones;
- cero confusión entre plan lógico y ejecución real.

Si alcanza 2/3 + 2/2, F1.3 puede continuar hacia cierre sin tocar Core/API.

Si sigue en 0-1/3 positivos, detener la optimización de prompting y reevaluar si Work Plan aporta suficiente valor en ChatGPT normal antes de introducir más mecanismos o complejidad.

#### Decisión recomendada

Aplicar **una sola modificación conductual a la skill**: agregar el paso anterior al `Flujo general`.

No cambiar en esta iteración:

- ServerInstructions;
- tool descriptions/schemas;
- Core/CAS/statuses;
- annotations;
- plugin activation metadata/defaultPrompt;
- README funcional;
- cantidad o nombres de tools.

Al publicar el plugin, sólo acompañar el cambio con el bump técnico de versión requerido por Plugin Creator.

#### Implementación del ajuste de skill ✓

Se actualizó el plugin privado LoomLCI de **0.2.0 a 0.2.1** con una única modificación conductual en `skills/loomlci/SKILL.md`:

- nuevo paso 2 del `Flujo general` que pide usar Work Plan en tareas no triviales con varias fases significativas, acciones dependientes o checkpoints;
- inicialización temprana tras `work_create` mediante `work_plan_update(expectedRevision=0)` en una WorkSession nueva;
- pocos pasos orientados a resultados y updates sólo en hitos;
- anti-trigger explícito para lookups simples o tareas cortas de un solo paso;
- IDs, revisions, conflicts, límites y demás semántica siguen delegados al schema vivo.

Para aislar el experimento no se modificaron:

- ServerInstructions ni tool descriptions;
- Core/API/annotations;
- `defaultPrompt`, descriptions o metadata de activación;
- README;
- configuración MCP;
- nombres ni cantidad de tools.

El bump técnico `0.2.0 -> 0.2.1` se sincronizó en `plugin.json` y `.codex-plugin/plugin.json`. El read-back de la release publicada confirmó la preservación de README y configuraciones MCP sin cambios.

#### Resultado A/B final con skill 0.2.1 ✓

Tras refrescar las herramientas se repitieron **exactamente los mismos 5 prompts**:

**Positivos**
- investigación multiarchivo sobre lifecycle de process output: no usó Work Plan;
- implementación documental acotada: usó Work Plan coherentemente;
- debugging del harness Release/Debug: usó Work Plan coherentemente.

Resultado: **2/3**, alcanzando el umbral definido.

**Negativos**
- lectura puntual de `global.json`: sin Work Plan;
- pregunta puntual sobre `AddLoomMcpStdio`: sin Work Plan.

Resultado: **2/2**, sin señal de overplanning.

Comparación controlada:

- antes de reforzar MCP: 0/3 positivos;
- tras reforzar ServerInstructions/descriptions: 1/3 positivos, 2/2 negativos;
- tras agregar la regla mínima a la skill: **2/3 positivos, 2/2 negativos**.

La skill mejoró recall sin degradar precisión. El caso de investigación multiarchivo todavía puede resolverse sin Work Plan, pero no impide el cierre porque el criterio deliberado era generalización >=2/3, no uso obligatorio.

**F1.3 queda aprobado.** No hay evidencia que justifique más prompting, cambios de Core/API ni nuevas tools en este bloque.

#### Fase 3 - Control trivial / anti-overplanning

Usar otro chat nuevo, sin nombrar Work Plan.

Prompt recomendado:

> Usa LoomLCI sobre `C:\Users\ivanl\Documents\ProyectosPersonales\LoomLCI`, sin modificar nada. Lee `global.json` y dime qué versión de .NET SDK fija el proyecto.

Resultado esperado:

- resolver la tarea con las mínimas tools naturales;
- **no crear ni actualizar Work Plan**.

Este control es tan importante como el caso positivo: las ServerInstructions dicen que Work Plan es para tareas multi-step no triviales.

Si el agente usa Work Plan aquí, considerar sobreplanificación y ajustar instrucciones antes de cerrar F1.

#### Fase 4 - Evaluación de approvals / destructiveHint

No cambiar permisos de la app durante la validación principal.

La app `LoomLCI MCP` está configurada actualmente con **Allow all actions**, por lo que la ausencia de confirmaciones es el comportamiento esperado de esta instalación. No se debe usar “no apareció popup” para concluir que `destructiveHint` es ignorado.

Qué medir:

- que `work_plan_update` no quede bloqueado;
- que no aparezcan confirmaciones repetitivas inesperadas bajo la configuración actual;
- que ChatGPT siga tratando la acción como write/mutation cuando corresponda.

La annotation F1.2 permanece:

- `Destructive=true` para update.

No cambiarla sólo para optimizar prompts. MCP define estas annotations como hints de riesgo; `destructive=false` sería incorrecto para un full replacement capaz de eliminar steps.

Opcional, fuera del criterio obligatorio de F1: repetir una única operación con permisos más restrictivos (`ask_before_writes`) para caracterizar UX de deployment alternativo. No es necesario para cerrar la instalación actual y no conviene modificar permisos del usuario sólo para completar F1.3.

#### Fase 5 - Diagnóstico de fallos

Clasificar cualquier problema antes de cambiar código.

**LoomLCI/Core/MCP**
- revision/IDs incorrectos;
- plan perdido dentro de la misma WorkSession;
- close no limpia;
- schema/descripción contradictoria;
- error semántico incorrecto.

**Secure MCP Tunnel / app catalog**
- Host local expone 19 pero ChatGPT sólo ve 17;
- herramientas nuevas no aparecen hasta refresh;
- definición stale de una tool.

**ChatGPT/consumer wrapper**
- `IsError` mostrado como excepción exterior;
- `details` no visible directamente pero código/mensaje sí;
- retry bloqueado por política/orquestación;
- confirmaciones determinadas por permisos/contexto.

**Ergonomía/instructions**
- fresh-agent nunca usa Work Plan en tarea claramente multi-step;
- usa Work Plan para tarea trivial;
- actualiza obsesivamente en cada tool call;
- confunde `waiting` con process state real.

Cada categoría tiene una solución diferente; no parchear Loom para compensar automáticamente una conducta del consumidor.

#### Criterio de cierre F1.3 / Bloque F

F1 puede cerrarse cuando:

1. ChatGPT normal ve el catálogo actualizado de **19 tools**;
2. smoke por Secure MCP Tunnel pasa `get/update/conflict/recovery/clear/close`;
3. error stale sigue siendo recuperable por el agente;
4. al menos **2 de 3** fresh-agents usan Work Plan coherentemente en la tarea no trivial;
5. el control trivial no usa Work Plan;
6. no hay fricción de approval bloqueante con la configuración real de la app;
7. no hubo cambios accidentales durante fresh-agent; cualquier modificación intencional pedida por un caso de implementación quedó validada y reconciliada;
8. WorkSessions de prueba quedan cerradas;
9. tunnel queda nuevamente `healthy/ready`;
10. repo sigue limpio;
11. se documentan las asperezas observadas sin atribuir wrappers externos al Core.

Si sólo falla el criterio de adopción natural (puntos 4/5), primero ajustar ServerInstructions/tool descriptions y repetir fresh-agent. No rediseñar Core.

#### Qué no probar nuevamente en F1.3

No repetir exhaustivamente:

- límites de 32 steps;
- Unicode 512 scalars;
- duplicate/unknown IDs;
- CAS concurrente;
- expiry;
- snapshot immutability;
- ResourceRegistry;
- schemas completos.

Todo eso ya está cubierto por Core/Integration F1.1/F1.2. F1.3 debe concentrarse en **transporte real, descubribilidad y comportamiento del agente**.

#### Recomendación

F1.3 debe hacerse en dos momentos:

1. smoke determinista en el chat actual o un chat controlado;
2. fresh-agent en chats nuevos con el prompt no trivial y el control trivial.

La skill ya fue actualizada y el A/B final fue aprobado. La metadata/README de la integración puede reconciliarse aparte para reflejar Python + Agent Support sin convertirla en una lista exhaustiva; no forma parte del criterio funcional de F1.3.

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
- OpenAI - Developer mode and MCP apps in ChatGPT: https://help.openai.com/en/articles/12584461-developer-mode-and-mcp-apps-in-chatgpt
- OpenAI - Apps in ChatGPT / action permissions: https://help.openai.com/en/articles/11487775-apps-in-chatgpt
- MCP Server Instructions: https://blog.modelcontextprotocol.io/posts/2025-11-03-using-server-instructions/
- OpenAI Function calling - function descriptions and tool-selection guidance: https://developers.openai.com/api/docs/guides/function-calling
- OpenAI Plugins - Define tools: https://developers.openai.com/plugins/plan/tools
- OpenAI Plugins - Optimize metadata / golden prompt sets: https://developers.openai.com/plugins/guides/optimize-metadata
- OpenAI Plugins - Skills: https://developers.openai.com/plugins/concepts/skills
- OpenAI Plugins - Build skills: https://developers.openai.com/plugins/build/skills
- Codex default planning instructions: https://github.com/openai/codex/blob/main/codex-rs/protocol/src/prompts/base_instructions/default.md
