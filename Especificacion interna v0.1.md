# Especificación interna v0.1

> Estado: **baseline arquitectónica v0.1 reconciliada con la implementación al 2026-10-04**. Los principios centrales están adoptados; este documento mezcla contratos implementados y diseño deliberadamente diferido. El código actual y las notas de cada bloque cerrado son la fuente de verdad para el comportamiento ya implementado. No es una API pública congelada.

## Estado de implementación

Implementado actualmente:

- WorkSession, Invocation y Resource Registry;
- lifecycle explícito con close/expiry/tombstones;
- Process con pipes, Job Objects, ConPTY, retención post-exit y explicit release;
- Filesystem estructurado;
- Python Runtime E1 cerrado: contratos Core, worker persistente, provisioning privado, backend Windows/provider, tools MCP públicas `python_execute`/`python_reset` y validación final por Secure MCP Tunnel + fresh-agent;
- adapter MCP por STDIO y acceso de ChatGPT normal mediante Secure MCP Tunnel externo.

Diferido; **no debe interpretarse como implementado hoy**:

- abstracción explícita `ExecutionContext` / Policy;
- Work Plan / Agent Support;
- Computer/UI Automation/captura;
- Known Locations;
- Streamable HTTP y audit durable.

## Objetivo

Definir el modelo interno mínimo de LoomLCI antes de escribir código. Estos contratos deben sobrevivir cambios en MCP, nombres de tools y detalles de implementación.

LoomLCI es un runtime local. El agente decide qué hacer; Loom administra ejecución, recursos, estado explícito, cancelación y observabilidad.

## No objetivos de v0.1

- sandbox
- elevación administrativa
- multi-OS
- autenticación remota completa
- gestor persistente de proyectos/tareas
- planificación autónoma
- UI propia obligatoria
- API pública definitiva

## 1. WorkSession

WorkSession es el scope explícito de trabajo de Loom. No es una sesión MCP, HTTP ni de conexión.

Identidad conceptual: wrk_<opaque>.

El identificador se genera en Loom y debe poder viajar entre requests, conexiones y adapters.

Estados:

    active -> closing -> closed

Puede existir además un cierre por expiración de inactividad.

Una WorkSession mantiene únicamente estado que realmente necesita cruzar llamadas.

Implementado hoy:

- base directory opcional;
- recursos propiedad de la sesión;
- timestamps de creación/última actividad;
- CancellationTokenSource raíz de la sesión.

Diseño diferido para futuras capabilities:

- ExecutionContext asociado;
- Python worker de la sesión, si fue creado;
- Work Plan opcional.

No contiene conversación, prompts ni memoria semántica del agente.

### Base directory

Es contexto y conveniencia, no seguridad.

- una ruta absoluta siempre es válida si Windows la permite
- una ruta relativa puede resolverse contra base_directory
- si no existe base directory, una operación que necesita resolver una ruta relativa falla claramente

No existe concepto de allowed root en Full Trust.

### Creación y cierre

Conceptualmente:

    work.create(base_directory?, label?) -> work_id
    work.close(work_id)

Al cerrar:

1. deja de aceptar nuevas invocaciones asociadas
2. cancela invocaciones activas
3. cierra recursos session-owned
4. termina Python worker
5. descarta Work Plan efímero
6. marca la sesión closed

La expiración automática está implementada. El default actual de inactividad de WorkSession es **60 minutos**, con tombstones retenidos **60 minutos**; ver [[Bloque D0 - Resource lifetime y expiry]].

### Uso opcional

No toda operación requiere WorkSession.

Operaciones puramente stateless pueden ejecutarse sin ella si todos sus argumentos son explícitos. Ejemplos: leer una ruta absoluta o ejecutar un comando one-shot con cwd absoluto.

Las operaciones que crean estado duradero deben recibir o crear un scope explícito.

## 2. ExecutionContext

Abstracción interna que representa dónde y bajo qué entorno del sistema operativo se ejecutan las capabilities.

v0.1 implementa solamente HostFullTrustExecutionContext.

Características:

- sesión interactiva real del usuario
- token/permisos normales del usuario de Windows
- filesystem real
- red normal del usuario
- escritorio real
- sin restricciones adicionales impuestas por Loom

Full Trust no implica elevación.

ExecutionContext proporciona backends/providers, no lógica de agente:

- filesystem backend
- process backend
- computer backend
- identidad/plataforma del host
- contexto necesario para lanzar workers

No debe mantener cwd global mutable.

Una WorkSession queda ligada a un ExecutionContext durante toda su vida.

## 3. Invocation

Una Invocation representa una ejecución de una operación, no un recurso duradero.

Ejemplos:

- leer un archivo
- iniciar un proceso
- obtener el estado de un proceso
- ejecutar un fragmento Python
- capturar una ventana

Identidad conceptual: inv_<opaque>.

InvocationId sirve para correlación, cancelación interna, eventos y tracing. No tiene por qué convertirse en argumento público de todas las tools.

Estados:

    created -> running -> succeeded | failed | cancelled | deadline_exceeded

Una Invocation terminada es historial/telemetría; no es el lugar donde vive un proceso o un Python worker.

Contexto mínimo:

- invocation id
- operation id
- WorkSession opcional
- caller/adapter metadata
- ExecutionContext
- deadline opcional
- linked CancellationToken
- trace/activity correlation
- start/end timestamps

### Regla fundamental

Una operación larga y un recurso duradero no son la misma cosa.

Ejemplo: process.start(...).

La Invocation debe terminar en cuanto Loom haya arrancado y registrado correctamente el proceso. Devuelve un ProcessHandle. El proceso puede continuar durante horas sin mantener la Invocation abierta.

MCP Tasks puede mapearse más adelante sobre invocaciones realmente largas, pero no define el modelo interno.

## 4. Cancellation

Toda Invocation recibe un token enlazado a las causas relevantes:

- cancelación del caller/adapter
- cierre/expiración de WorkSession
- shutdown del host
- deadline de la operación

.NET CancellationToken es el mecanismo interno base.

cancelled y deadline_exceeded son resultados diferentes.

Las capabilities deben propagar cancelación a I/O y APIs nativas cuando sea posible.

Si una API no es cooperativamente cancelable, el provider debe usar su mecanismo de abort/kill cuando exista.

### Creación de recursos

La adquisición de recursos tiene un punto de commit:

1. provider crea recurso
2. Core lo registra
3. handle queda válido
4. recién entonces la Invocation se considera exitosa

Si falla o se cancela antes del registro, Loom debe limpiar el recurso parcial.

Si el resultado se pierde después del commit, el recurso puede quedar huérfano desde la perspectiva del modelo. Por eso los recursos duraderos necesitan expiry/cleanup y, cuando sea útil, operaciones de list/recovery.

El Core no hará retries transparentes de operaciones mutantes.

## 5. Handles y Resource Registry

Los handles representan estado que debe sobrevivir a una Invocation.

Reglas:

- opacos
- generados por Loom
- no derivados de paths, PID, HWND u otros valores predecibles
- al menos 128 bits de entropía para handles bearer
- prefijo humano por tipo, sin codificar estructura interna
- nunca usar el identificador nativo de Windows como identidad primaria

Ejemplos conceptuales:

- wrk_...
- proc_...
- obs_...

Internamente cada recurso registra:

- handle
- resource kind
- WorkSession propietaria, si aplica
- ExecutionContext
- created_at
- last_accessed_at
- expiry policy
- lifecycle state
- resource object/provider state
- disposer/cleanup strategy

Errores de handle deben distinguir claramente:

- recurso inexistente
- recurso expirado
- recurso cerrado
- tipo de handle incorrecto

Un modelo debe poder recuperarse sin adivinar qué pasó.

### No crear handles innecesarios

- Process: sí necesita handle
- Computer Observation: probablemente sí, de vida corta
- WorkSession: sí
- Python worker: session-owned; no necesita handle público inicialmente
- Work Plan: estado singleton dentro de WorkSession; no necesita handle propio inicialmente

## 6. Resource ownership

Un recurso puede ser session-owned o independent.

Session-owned: Loom lo cierra al cerrar/expirar la WorkSession. Es el default para workers, procesos auxiliares, terminales y recursos creados para completar una tarea.

Independent: su lifetime no depende del cierre de WorkSession. Se reserva para casos donde el usuario/agente quiera dejar una aplicación o proceso vivo deliberadamente.

El contrato público exacto se definirá con Process, pero el modelo interno debe permitir ambas políticas.

## 7. Capability model

El Core trabaja con operations fuertemente tipadas. El adapter traduce desde MCP u otro protocolo.

Una operación define como mínimo:

- namespace/capability
- operation name
- request type
- response type
- metadata de comportamiento
- handler

Metadata útil:

- read-only / mutating
- idempotent / non-idempotent
- execution/concurrency class
- si requiere WorkSession
- resource kinds que consume/produce

Esta metadata puede luego alimentar MCP annotations, PTC y documentación sin introducir dependencias de MCP en Core.

El Core no procesa JSON Schema ni conoce tools/call.

Los adapters generan schemas a partir de contratos públicos/DTOs y mapean resultados.

## 8. Error model

Las excepciones internas no cruzan directamente la frontera pública.

Resultado interno:

    Success<T> | LoomError

Códigos base propuestos:

- invalid_argument
- not_found
- already_exists
- conflict
- access_denied
- unsupported
- resource_closed
- resource_expired
- busy
- cancelled
- deadline_exceeded
- execution_failed
- internal

Capabilities pueden añadir errores de dominio realmente accionables, por ejemplo stale_observation.

Estructura:

- code estable
- message útil para modelo/humano
- retryable opcional
- target/resource opcional
- details estructurados opcionales

Stack traces y excepciones completas quedan en diagnóstico, no en respuestas normales.

Un exit code distinto de cero de un proceso es un resultado válido del proceso, no un error del Core.

Una búsqueda sin coincidencias también es éxito con colección vacía.

## 9. Events, tracing y audit

No usar un único mecanismo para tres necesidades diferentes.

### Activity / tracing

Cada Invocation crea una System.Diagnostics.Activity mediante ActivitySource.

Sirve para:

- duración
- jerarquía/correlación
- status
- tags
- integración futura con OpenTelemetry

OpenTelemetry es un collector/exporter opcional, no una dependencia conceptual del Core.

### Domain/Event Bus

Eventos discretos de runtime:

- WorkSessionCreated / Closing / Closed
- InvocationStarted / Completed
- ResourceCreated / StateChanged / Disposed
- ProcessStarted / Exited
- PythonWorkerStarted / Reset / Exited
- ComputerObservationCreated

Envelope mínimo:

- event_id
- timestamp
- kind
- source
- work_id opcional
- invocation_id opcional
- resource_handle opcional
- payload estructurado

El bus es observabilidad; la corrección del runtime nunca depende de que un subscriber procese un evento.

Implementación recomendada: bounded System.Threading.Channels para permitir backpressure y límites claros.

### Audit

Subscriber separado que decide qué hacer durable.

No persistir por defecto screenshots, cada chunk de stdout o cada movimiento de mouse.

## 10. Concurrency

Loom debe admitir varias Invocations simultáneas.

No existe un lock global del Core.

Reglas iniciales:

- Filesystem reads/searches: concurrentes
- Process resources distintos: concurrentes
- stdin de un mismo proceso: serializado
- Python worker: una ejecución a la vez por worker
- Computer global input: serializado mediante DesktopInputGate
- UI Automation: thread/apartment dedicado según requisitos de Windows

Locks y gates pertenecen al recurso/provider correspondiente, no al adapter MCP.

## 11. Process resource model

Process será el primer recurso duradero implementado.

Launch request interno conceptual:

- executable
- args[]
- working_directory
- environment overrides
- stdin inicial opcional
- I/O mode: pipes | terminal
- ownership
- WorkSession opcional/obligatoria según ownership

Handle: proc_...

El handle envuelve el estado real. PID es metadata, no identidad.

### Output

Para pipes:

- stdout y stderr separados
- buffers acotados
- cursores monotónicos
- lectura no destructiva

Conceptualmente:

    read(proc, cursor) -> chunks + next_cursor + state

Esto evita que polling repetido, retries o dos consumers destruyan output.

Si el buffer sobreescribe contenido viejo, la respuesta debe indicar desde qué cursor siguen disponibles datos.

Para ConPTY:

- un único stream de terminal
- mismo concepto de cursor

Estados:

- starting
- running
- exited
- terminating
- terminated
- failed_to_start

El handle puede sobrevivir un tiempo después del exit para permitir leer output y metadata final.

Procesos session-owned se asocian a Job Object cuando sea apropiado para permitir cleanup del árbol completo.

## 12. Python Runtime

Python Runtime es ejecución general, no parte de Computer.

v0.1 propone un worker Python lazy por WorkSession.

No hay worker global compartido entre agentes/sesiones.

Lifecycle:

- primera python.execute(work_id, code) crea worker
- globals/imports persisten entre ejecuciones
- una ejecución activa por worker
- timeout/cancelación pueden matar el worker
- un worker muerto/corrupto se descarta
- siguiente ejecución puede crear uno nuevo
- python.reset(work_id) permite reset explícito

Nunca ejecutar código generado por el modelo dentro del proceso C# de Loom.

La separación es de lifecycle/fault containment, no un sandbox.

Output:

- texto/log
- stdout/stderr acotados
- imágenes estructuradas
- status/error

PyAutoGUI puede estar disponible como librería. Un futuro módulo loom puede volver a entrar por capabilities estructuradas.

## 13. Work Plan

Agent Support es opcional y separado de execution.

v0.1 mantiene un único Work Plan por WorkSession.

No necesita handle adicional.

Modelo propuesto tras revisar concurrencia:

- revision
- ordered steps
  - id estable dentro del plan
  - text
  - status: pending | active | waiting | completed

No existe la restricción de un único paso activo. Varias tareas lógicas pueden estar activas o esperando simultáneamente.

`waiting` representa trabajo iniciado que está esperando un resultado externo o recurso (por ejemplo, un proceso largo) mientras el agente puede continuar con pasos independientes.

La lista completa se actualiza atómicamente.

Puede usarse expected_revision para detectar actualizaciones stale.

El Work Plan no es la fuente de verdad del lifecycle de Process/Invocation y v0.1 no enlaza automáticamente plan steps con resource handles.

No hay en v0.1:

- deadlines
- prioridades
- dependencias
- proyectos
- persistencia cross-session
- planificación autónoma

El adapter/perfil puede decidir no exponer Work Plan a hosts que ya tengan planner propio.

## 14. Computer relation with Core

El detalle de Computer se especificará después, pero v0.1 fija:

- acciones nativas en provider Windows
- UIA + captura + input nativo
- DesktopInputGate global
- observaciones referenciables mediante ID/handle efímero
- validación de contexto para detectar observaciones stale
- Python Runtime puede usar PyAutoGUI, pero PyAutoGUI no es el backend autoritativo

## 15. Adapter contract

Adapter es una traducción fina:

    external request -> internal operation -> internal result/error -> external response

Responsabilidades:

- protocolo/transport
- auth/caller metadata cuando exista
- schema/public DTO mapping
- cancellation del caller
- structured output
- mapeo de errores
- compatibilidad/versionado

No contiene:

- process lifecycle
- handle state
- policy de filesystem
- Python state
- Computer state

### MCP

Para MCP 2026-07-28 se prefiere stateless transport.

Todo estado Loom que cruce llamadas viaja mediante handles explícitos.

Structured Content + outputSchema deben usarse donde aporten valor.

MCP Tasks podrá mapear invocaciones largas en el futuro sin cambiar el Core.

## 16. Host lifecycle

Startup:

1. cargar config
2. crear HostFullTrustExecutionContext
3. inicializar registries/providers
4. inicializar observabilidad
5. iniciar adapters
6. aceptar trabajo

Shutdown:

1. dejar de aceptar nuevas invocaciones
2. cancelar host token
3. dejar una ventana de finalización
4. cerrar WorkSessions/resources
5. terminar recursos owned restantes
6. flush acotado de audit
7. salir

## 17. Flujo completo de una llamada

Ejemplo process.start:

1. Adapter recibe request.
2. Valida/deserializa DTO público.
3. Core resuelve WorkSession y ExecutionContext.
4. Crea Invocation + Activity + linked cancellation.
5. Emite InvocationStarted.
6. Capability valida semántica.
7. Provider Windows crea proceso/Job Object/pipes.
8. Core registra ProcessResource.
9. Se produce proc_...
10. Invocation pasa a succeeded.
11. Emite ResourceCreated + InvocationCompleted.
12. Adapter devuelve structured result con handle y metadata.
13. El proceso continúa independiente de la Invocation.
14. Calls posteriores usan proc_...

Este flujo es el patrón base para cualquier estado que sobreviva entre requests.

## 18. Decisiones deliberadamente aplazadas

- nombres públicos exactos de las tools
- JSON schemas finales
- TTLs concretos
- tamaños de buffers
- formato durable de audit
- storage de sessions/resources si algún día se persisten
- paquetes incluidos en Python
- forma exacta del bridge loom
- idempotency keys
- authentication remota
- elevación
