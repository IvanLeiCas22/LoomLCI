# Ergonomía - process_run

> Estado: **CERRADO end-to-end.** Runtime portable `0.1.0-dev-process-run` instalado y healthy/ready; suite Release 259/259 y smoke directo desde ChatGPT confirmado con catálogo MCP refrescado.

## Objetivo

Reducir el patrón repetitivo:

`process_start -> process_status/process_read -> process_release`

para comandos cortos, no interactivos y de salida acotada, sin debilitar la superficie durable existente de Process.

Ejemplos objetivo:

- `git status --short`
- `git rev-parse HEAD`
- `dotnet --version`
- `blender --version`
- consultas CLI similares que normalmente terminan en pocos segundos.

## Estado actual relevante

La superficie pública Process ya separa correctamente:

- `process_start`: crea un proceso durable con `processHandle`;
- `process_status`: estado/exit metadata;
- `process_read`: output retenido y cursores exactos;
- `process_write` / `process_resize`: interacción;
- `process_terminate`: detención fuerte del árbol;
- `process_release`: descarta un recurso ya terminal.

Core usa `InvocationRunner`, que ya aporta lease de WorkSession, cancelación, timeout opcional, tracing y eventos. El backend Windows ya posee `WindowsProcessResource.WaitForExitAndOutputAsync`, pero hoy ese wait no forma parte de `IProcessResource`.

El output de Process usa spool recuperable, cursores UTF-16 exactos y un presupuesto de respuesta máximo de 1.048.576 caracteres por lectura.

## Alternativas

### A. Implementar process_run sólo como composición MCP

La tool podría llamar internamente:

1. `StartAsync`;
2. polling con `StatusAsync`;
3. `ReadAsync`;
4. `ReleaseAsync`.

Ventaja: poco cambio en Core.

Problemas:

- una sola tool pública produciría varias invocaciones lógicas internas;
- lifecycle y cleanup ante timeout/cancelación serían más difíciles de razonar;
- se registraría temporalmente un recurso durable que el usuario nunca pidió;
- requiere polling aunque Windows ya tiene un wait nativo;
- la observabilidad sería más ruidosa y menos fiel al concepto one-shot.

**No recomendada.**

### B. Agregar una operación one-shot real en Core

Agregar `ProcessCapability.RunAsync` como una única invocación:

1. validar argumentos y resolver working directory igual que `process_start`;
2. iniciar un recurso mediante `IProcessProvider`;
3. mantenerlo privado/no registrado en ResourceRegistry;
4. esperar exit + drain de output;
5. leer stdout/stderr una vez con presupuesto acotado;
6. devolver resultado;
7. disponer siempre el recurso al terminar.

Para esto conviene elevar el wait existente a la interfaz:

`IProcessResource.WaitForExitAndOutputAsync(CancellationToken)`

El backend Windows ya tiene la implementación esencial.

Ventajas:

- una única invocación lógica `process.run`;
- sin handle durable ni retención post-exit innecesaria;
- timeout/cancelación aprovechan `InvocationRunner`;
- `DisposeAsync` garantiza cleanup del Job Object y descendientes;
- reutiliza launcher, pipes, output store y semántica Windows actuales;
- no necesita polling.

**Recomendada.**

## Contrato propuesto

### Core conceptual

```
ProcessRunRequest(
    Executable,
    Arguments?,
    WorkingDirectory?,
    Environment?,
    WorkId?,
    Timeout,
    MaxOutputChars)

ProcessRunResult(
    ProcessId,
    ExitCode,
    StartedAt,
    ExitedAt,
    Stdout,
    Stderr,
    StdoutTruncated,
    StderrTruncated,
    StdoutObservedChars,
    StderrObservedChars)
```

No devuelve `ProcessHandle`.

### MCP

```
process_run(
    executable,
    arguments = null,
    workingDirectory = null,
    workId = null,
    environment = null,
    timeoutSeconds = 30,
    maxOutputChars = 65536
)
```

Límites propuestos:

- `timeoutSeconds`: 1..600;
- `maxOutputChars`: 1..1.048.576;
- sólo `pipes`;
- sin stdin;
- sin terminal/ConPTY;
- sin `independent`;
- sin handle durable.

`workId` sería opcional. Si existe, mantiene lease de WorkSession durante toda la ejecución y permite resolver el directorio base. Si `workingDirectory` es relativo, se requiere una WorkSession con `baseDirectory`, igual que hoy.

## Semántica recomendada

- exit code distinto de cero: **resultado exitoso de la tool**, no error de Loom;
- fallo al iniciar: error de tool existente (`execution_failed`, etc.);
- timeout: `deadline_exceeded`;
- cancelación/user close/host shutdown: `cancelled` o semántica existente de WorkSession;
- timeout/cancelación deben disponer el recurso y terminar su Job Object;
- output mayor a `maxOutputChars`: devolver prefijo acotado + flags de truncación/observed chars, no crear un handle oculto para continuar;
- para necesitar output recuperable completo, background, stdin, terminal o ejecución larga: usar `process_start`.

## Output y límite MCP

Conviene reutilizar `IProcessResource.Read(..., maxChars)` después del exit. Ese método ya reparte dinámicamente el presupuesto total entre stdout/stderr.

Con el máximo actual de 1.048.576 unidades UTF-16 totales, incluso el peor escaping JSON permanece por debajo del presupuesto MCP de 9 MiB usado por LoomLCI. Aun así, puede reutilizarse el helper genérico de medición de payload como defensa adicional si la implementación lo vuelve conveniente.

## Distinción pública que debe quedar explícita

**`process_run`**
- comando corto;
- no interactivo;
- espera hasta terminar;
- devuelve exit code + output;
- no deja recurso.

**`process_start`**
- proceso largo/background/interactivo;
- devuelve handle;
- output durable;
- permite stdin, ConPTY, terminate, release y polling.

La skill futura debe enseñar esta selección, pero el schema vivo sigue siendo la fuente de verdad.

## Cambios esperados si se aprueba

### Core

- nuevos contratos `ProcessRunRequest/Result`;
- `ProcessCapability.RunAsync`;
- exponer `WaitForExitAndOutputAsync` en `IProcessResource`;
- factorizar la validación/creación de `ProcessLaunchSpec` para no duplicar reglas entre Start y Run.

### Windows

- hacer pública a través de la interfaz la espera que ya implementa `WindowsProcessResource`;
- sin nuevo launcher ni nuevo mecanismo de Job Object.

### MCP

- nueva tool `process_run`;
- DTO propio;
- annotations: destructive=true, readOnly=false, idempotent=false, openWorld=false;
- catálogo normal pasaría de 22 a 23 tools con Work Plan habilitado.

### Tests mínimos

- comando corto stdout + exit 0;
- exit no cero sigue siendo `ok=true`;
- stderr;
- working directory relativo y absoluto;
- environment overrides;
- timeout termina árbol y no deja recurso;
- cancelación limpia;
- output > `maxOutputChars` informa truncación;
- no quedan resources registrados después de éxito/error;
- contrato/schema MCP;
- roundtrip STDIO real;
- smoke live usando LoomLCI sobre comandos como `git status --short` y `dotnet --version`.

## Conclusión

No hace falta cambiar `process_start` ni simplificar su lifecycle. La mejora correcta es una segunda operación complementaria, one-shot, implementada en Core y reutilizando el backend Process existente.

No quedan preguntas técnicas bloqueantes antes de implementación. El timeout default de 30 s y el shape exacto del DTO pueden ajustarse durante implementación sin cambiar la arquitectura propuesta.

## Resultado de implementación

La arquitectura recomendada se implementó sin convertir `process_run` en una composición de tools públicas:

- nuevos `ProcessRunRequest` / `ProcessRunResult` en Core;
- `ProcessCapability.RunAsync` como una única invocation `process.run`;
- el proceso one-shot no se registra en `ResourceRegistry` y no expone `ProcessHandle`;
- `IProcessResource.WaitForExitAndOutputAsync` quedó en el contrato interno y Windows reutiliza la espera que ya existía;
- `process_start` y `process_run` comparten la construcción/validación de `ProcessLaunchSpec`;
- timeout/cancelación salen por `InvocationRunner` y `await using` garantiza dispose/Job cleanup;
- el resultado devuelve exit code, stdout/stderr acotados, flags de truncación y cantidad observada;
- exit code no cero sigue siendo un resultado normal de la tool.

La tool MCP final conserva el contrato investigado: `timeoutSeconds=30`, `maxOutputChars=65536`, rango 1..600 s y 1..1.048.576 chars, sólo pipes, sin stdin/terminal/independent.

### Validación

- Core dirigido: **3/3**;
- Windows dirigido: **4/4**;
- MCP/STDIO dirigido: **2/2**;
- IntegrationTests completos: **14/14**;
- suite Release completa: **259/259** = Core 87 + Windows 141 + MCP 5 + PdfWorker 6 + Launcher 6 + Integration 14;
- builder portable: Launcher **6/6** + IntegrationTests contra Host publicado **14/14**;
- paquete: `LoomLCI-0.1.0-dev-process-run-win-x64.zip`;
- SHA-256: `aaa67eb47ab056e0bd206147cd4e346f8212491a63512be05ca8d310f9726e09`;
- runtime instalado: `0.1.0-dev-process-run`, healthy/ready;
- el cutover se hizo con IvanSpace y las tools existentes siguieron operativas inmediatamente después.

Acceptance final directo desde ChatGPT: **OK** en un chat nuevo con catálogo MCP refrescado. Se ejecutó `git status --short` específicamente mediante `process_run`: `exitCode=0`, `stderr` vacío, `stdout` completo/no truncado y sin `processHandle`; sólo se devolvió el `processId` como metadata. Con esto `process_run` queda cerrado end-to-end.
