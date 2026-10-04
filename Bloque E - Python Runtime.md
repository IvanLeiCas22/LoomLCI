# Bloque E - Python Runtime

## Estado

**E1.1 (Core) implementado y validado; E1.2 (worker + IPC) pendiente.**

Este bloque define el primer vertical slice de Python Runtime después del cierre de la baseline v0.1. No modifica todavía Computer ni Agent Support.

## Objetivo

Agregar ejecución Python general y persistente por WorkSession:

- Python real, Full Trust, en proceso separado;
- globals/imports/funciones persistentes entre llamadas;
- una ejecución activa por worker;
- timeout/cancelación con descarte del worker;
- stdout/stderr acotados;
- excepciones Python ordinarias recuperables;
- cleanup automático por `work_close` / expiry;
- MCP mínimo y composable.

No se busca construir un sandbox ni reemplazar Process/Filesystem.

## Decisiones cerradas

### Runtime privado

LoomLCI no usa `python` de PATH ni instalaciones Python del usuario.

Motivo práctico validado en la máquina de desarrollo: `python` puede resolver a un runtime embebido de otra aplicación mientras `py` resuelve a otra versión distinta. Esa ambigüedad no es aceptable para una capability estable.

Se fija inicialmente:

- CPython **3.14.8 x64**;
- distribución oficial **embeddable** de Windows;
- runtime privado/versionado por LoomLCI;
- sin `pip` administrado por el usuario;
- dependencias de terceros futuras se vendorizarán/versionarán como parte de LoomLCI.

La distribución embeddable está pensada para formar parte de otra aplicación y queda casi aislada de PATH, registro, variables de entorno y paquetes instalados por el usuario.

El binario del runtime no debe quedar versionado dentro de Git. La fuente del worker sí.

### Un worker lazy por WorkSession

No hay `PythonHandle` público en v0.1.

La WorkSession ya es el handle explícito del estado lógico que necesita Python:

    work_create(...)
      -> python_execute(workId, ...)
      -> python_execute(workId, ...)
      -> python_reset(workId)
      -> work_close(workId)

La primera ejecución crea el worker. Las siguientes reutilizan el mismo namespace mientras siga sano.

Internamente el worker sigue siendo un recurso registrado:

- kind: `python_worker`;
- ownership: `SessionOwned`;
- handle interno opaco con prefijo `pyw_`;
- disposer: termina y libera worker + IPC + árbol de procesos.

El handle interno no cruza la frontera MCP.

### Relación con Process

Python es una capability distinta de Process, pero su backend Windows reutiliza el launcher existente.

`WindowsPythonRuntimeProvider` usa `IProcessProvider` para iniciar el intérprete privado en modo pipes. Por lo tanto hereda:

- Native CreateProcess;
- Job Object;
- descendientes dentro del mismo Job;
- cleanup del árbol;
- handles nativos;
- lifecycle robusto ya validado.

El worker Python envuelve internamente el `IProcessResource`; no se registra además como un ProcessHandle público.

No se duplica launcher nativo ni semántica de Job Objects.

### Resolución 0/1 worker sin estado paralelo

No guardar un `ConcurrentDictionary<WorkId, Worker>` independiente del ResourceRegistry.

Agregar una primitiva mínima al registry para obtener handles activos por:

    kind + owner WorkId

Por ejemplo:

    GetActiveOwnedHandles(string kind, WorkId ownerWorkId)

`PythonCapability` mantiene sólo un gate de creación global corto para evitar dos primeros starts simultáneos. Bajo ese gate:

1. consulta recursos `python_worker` activos de la WorkSession;
2. 0 -> crea y registra uno;
3. 1 -> reutiliza;
4. >1 -> error interno, porque viola el invariant.

El gate sólo serializa la creación/reset, no las ejecuciones normales de workers ya existentes.

Así `work_close`, expiry y tombstones continúan siendo la única fuente de verdad del lifecycle.

## API interna propuesta

### Contratos Core

    PythonExecuteRequest(
        WorkId,
        Code,
        Timeout,
        MaxOutputChars)

    PythonExecutionResult(
        Status,
        Stdout,
        Stderr,
        StdoutTruncated,
        StderrTruncated,
        Exception)

    PythonExceptionInfo(
        Type,
        Message,
        Traceback)

Estados de ejecución visibles:

- `completed`
- `exception`

Los errores de infraestructura siguen usando `LoomResult`:

- `invalid_argument`
- `not_found`
- `resource_closed`
- `resource_expired`
- `busy`
- `cancelled`
- `deadline_exceeded`
- `execution_failed`
- `internal`

Una excepción Python ordinaria **no** es un error de tool/Loom: la ejecución fue realizada correctamente y el worker sigue utilizable.

### Provider

Core define conceptualmente:

    IPythonRuntimeProvider.StartAsync(PythonWorkerStartSpec, token)
        -> IPythonWorkerResource

    IPythonWorkerResource.ExecuteAsync(...)
    IPythonWorkerResource.IsHealthy
    IPythonWorkerResource.DisposeAsync()

`PythonWorkerStartSpec` contiene sólo lo necesario para iniciar el worker, incluido el cwd inicial.

Core no conoce:

- ruta concreta de python.exe;
- Named Pipes Windows;
- layout de la distribución embeddable;
- Job Objects.

## MCP público inicial

Sólo dos tools nuevas.

### python_execute

    python_execute(
        workId,
        code,
        timeoutSeconds = 60,
        maxOutputChars = 65536)

Reglas:

- `workId` obligatorio;
- `code` no vacío;
- timeout inicial permitido: 1..600 s;
- `maxOutputChars`: 1..1,048,576 por stream;
- crea el worker lazy si no existe;
- conserva globals si termina normalmente o con excepción Python;
- segunda ejecución concurrente sobre el mismo worker devuelve `busy`, no espera en cola;
- timeout/cancelación/crash/protocolo inválido dejan el worker unhealthy y se descarta;
- la próxima ejecución crea uno nuevo.

Resultado estructurado conceptual:

    {
      "status": "completed" | "exception",
      "stdout": "...",
      "stderr": "...",
      "stdoutTruncated": false,
      "stderrTruncated": false,
      "exception": null | {
        "type": "...",
        "message": "...",
        "traceback": "..."
      }
    }

No devolver imágenes en E1.

### python_reset

    python_reset(workId)

- idempotente;
- si no existe worker activo, éxito;
- descarta el worker y su namespace;
- la siguiente `python_execute` crea uno nuevo;
- usa el ResourceRegistry, no un canal especial de reset dentro del intérprete.

Si converge con una ejecución ya iniciada, el cierre respeta las leases del ResourceRegistry; no se permite doble dispose.

## Ejecución Python

Worker separado, nunca `Python.NET`, hosting del intérprete dentro de C# ni `exec` embebido en el Host.

Namespace persistente conceptual:

    namespace = {
        "__name__": "__main__",
        "__builtins__": __builtins__,
    }

Cada request ejecuta:

    exec(compile(code, "<loom-python>", "exec"), namespace)

Una excepción se captura con traceback y se devuelve como `status=exception`. El namespace no se reinicia por una excepción ordinaria.

No se introduce un DSL.

## IPC

### Canal

Named Pipe duplex privado C# <-> worker.

Servidor C#:

- `PipeDirection.InOut`;
- byte mode;
- asynchronous;
- `PipeOptions.CurrentUserOnly`;
- nombre aleatorio no predecible.

En Windows, `CurrentUserOnly` comprueba mismo usuario y mismo nivel de elevación.

Se validó experimentalmente que CPython estándar puede abrir directamente `\\.\pipe\...` con stdlib y comunicarse con `NamedPipeServerStream` sin dependencias externas.

### Framing

No usar stdout como protocolo.

Frames:

    uint32 little-endian payloadLength
    UTF-8 JSON payload

Límite de frame acotado y validado antes de reservar memoria.

Handshake inicial worker -> Host:

    {
      "type": "hello",
      "protocolVersion": 1,
      "pid": ...,
      "pythonVersion": "3.14.8"
    }

Request:

    {
      "type": "execute",
      "requestId": "...",
      "code": "...",
      "maxOutputChars": 65536
    }

Response:

    {
      "type": "result",
      "requestId": "...",
      ...
    }

IDs de request impiden aceptar una respuesta stale o cruzada.

El pipe es el canal de control. stdout/stderr del proceso worker quedan reservados para diagnósticos inesperados del propio worker.

## stdout / stderr

E1 captura stdout/stderr Python de cada ejecución y devuelve texto acotado.

No se prometen todavía semánticas de ProcessOutputStore/cursors: cada `python_execute` es una Invocation síncrona con un resultado finito.

Si se supera el límite:

- conservar prefijo hasta el límite;
- seguir ejecutando el código;
- marcar `stdoutTruncated` / `stderrTruncated`;
- no matar el worker sólo por truncamiento.

Subprocessos complejos o output masivo siguen siendo mejor caso de uso para Process.

## Timeout, cancelación y fallos

Default público: 60 s.

`PythonCapability` usa el timeout nativo de `InvocationRunner`.

El provider/worker debe reaccionar a cancellation:

1. cortar la espera de IPC;
2. terminar/disponer el worker y su Job;
3. marcar recurso unhealthy;
4. liberar pipe;
5. propagar cancelación para que InvocationRunner produzca `cancelled` o `deadline_exceeded`.

Casos que invalidan el worker:

- timeout;
- cancelación del caller/WorkSession;
- proceso Python termina inesperadamente;
- EOF del pipe;
- frame malformado;
- protocolVersion incompatible;
- requestId incorrecto;
- error de I/O irrecuperable.

Una excepción Python normal no lo invalida.

## Working directory y environment

Cwd inicial:

- `WorkSession.BaseDirectory` si existe;
- de lo contrario, perfil del usuario de Windows.

No hay cwd global en Core.

Python puede ejecutar `os.chdir()`; al ser namespace/proceso persistente, ese cambio persiste deliberadamente dentro de ese worker hasta reset.

El worker corre con el environment/permisos normales del usuario. La distribución embeddable y `-I` reducen interferencia de configuración Python externa, pero **no crean un sandbox**.

## Distribución / layout

Fuente versionada propuesta:

    runtime/python/
      worker.py
      runtime.json
      README.md

Runtime privado no versionado:

    %LOCALAPPDATA%/LoomLCI/runtimes/python/3.14.8-amd64/

`runtime.json` fijará como mínimo:

- versión;
- arquitectura;
- tipo `embed`;
- URL oficial;
- hash esperado.

El mecanismo de provisioning puede ser un paso de desarrollo/instalación separado; no debe depender de PATH.

## Paquetes

E1: **stdlib solamente**.

No incluir todavía:

- PyAutoGUI;
- Pillow;
- NumPy;
- OpenCV;
- pandas;
- matplotlib;
- pip;
- módulo `loom`.

Las dependencias futuras se incorporan sólo después de validar compatibilidad y necesidad real.

PyAutoGUI pertenece al bloque que conecte Python con Computer, no a la fundación del runtime.

## Imágenes

No implementar `display(image)` en E1.

Razones:

- primero validar lifecycle/IPC/persistencia;
- el SDK MCP C# 2.2.0 usado hoy tiene un issue abierto de serialización de bloques binarios/ImageContentBlock;
- no conviene diseñar el contrato inicial alrededor de una superficie externa actualmente defectuosa.

El protocolo IPC queda preparado para agregar outputs tipados después.

## loom.*

No implementar bridge `loom.*` en E1.

Un bridge Worker -> Core implica RPC bidireccional/reentrancia y debe diseñarse junto con Computer u otra necesidad concreta.

Python general ya aporta valor sin ese bridge.

## Archivos/capas previstos

### Core

    LoomLCI.Core/Python/
      PythonContracts.cs
      PythonCapability.cs

Cambios pequeños adicionales:

- `ResourceRegistry.GetActiveOwnedHandles(...)` o equivalente.

### Windows

    LoomLCI.Windows/Python/
      WindowsPythonRuntimeProvider.cs
      WindowsPythonWorkerResource.cs
      PythonWorkerProtocol.cs

Reutiliza `IProcessProvider`.

### MCP

    LoomLCI.Mcp/PythonTools.cs

Se registra junto a Work/Process/Filesystem.

### runtime

    runtime/python/worker.py
    runtime/python/runtime.json

No crear un assembly `LoomLCI.Python` en E1.

## Plan de implementación

### E1.1 - Contratos Core ✓

Implementado:

- contratos `PythonExecuteRequest`, resultados/excepción y provider/resource;
- `PythonCapability`;
- `ResourceRegistry.GetActiveOwnedHandles(kind, ownerWorkId)`;
- invariant 0/1 worker por WorkSession;
- lazy start/reuse;
- exclusión de ejecución delegada al worker con error `busy`;
- reset idempotente;
- reset concurrente espera leases activas antes de dispose;
- timeout/cancellation compuestos por `InvocationRunner`;
- worker unhealthy/cancelled se descarta;
- siguiente execute recrea worker;
- eventos básicos `ResourceCreated/ResourceClosed`;
- cleanup automático por close/expiry de WorkSession.

Validación E1.1:

- 19 tests nuevos de Core;
- Core: **58/58**;
- Windows: **78/78**;
- Integration MCP: **6/6**;
- total Debug: **142/142**;
- total Release: **142/142**.

Todavía no hay Python real: E1.2 incorpora worker + IPC.

### E1.2 - Worker + IPC

- worker.py;
- protocolo v1;
- framing;
- handshake;
- namespace persistente;
- stdout/stderr bounded;
- excepción estructurada;
- Named Pipe CurrentUserOnly;
- manejo de EOF/protocolo corrupto.

### E1.3 - Backend Windows

- resolver runtime privado;
- iniciar vía `IProcessProvider`;
- esperar handshake;
- Job cleanup;
- worker health;
- cancellation/timeout mata árbol;
- cwd inicial.

### E1.4 - MCP

- `python_execute`;
- `python_reset`;
- DTO/outputSchema;
- descripciones;
- ServerInstructions actualizadas mínimamente.

### E1.5 - Validación

- Core fake provider;
- Windows real;
- MCP STDIO real;
- build Debug/Release;
- smoke por Secure MCP Tunnel;
- fresh-agent corto específico de Python.

## Tests mínimos de aceptación

### Core.Tests

1. primera execute crea exactamente un worker;
2. segunda execute reutiliza el mismo;
3. sessions diferentes obtienen workers diferentes;
4. dos first-execute concurrentes no crean dos workers;
5. ejecución concurrente del mismo worker devuelve `busy`;
6. excepción Python ordinaria se representa como resultado, no Loom error;
7. worker unhealthy se descarta y la siguiente ejecución recrea;
8. reset sin worker es idempotente;
9. reset elimina worker y la siguiente execute crea otro;
10. work_close cierra worker session-owned una sola vez;
11. expiry cierra worker;
12. timeout/cancellation se mapean correctamente;
13. lookup owned-resource no devuelve recursos closed/expired;
14. invariant >1 worker produce error detectable, no selección arbitraria.

### Windows.Tests

1. handshake real con CPython privado;
2. `x=41` seguido de `print(x+1)` devuelve 42;
3. imports y función definida persisten;
4. stdout y stderr separados;
5. truncamiento no mata worker;
6. `raise ValueError` devuelve traceback y el siguiente execute funciona;
7. `os._exit(...)` invalida worker y siguiente execute recrea;
8. timeout de loop infinito mata root + descendientes;
9. `work_close` mata worker + subprocess descendiente;
10. reset cambia PID/namespace;
11. mismo worker rechaza execute concurrente con `busy`;
12. cwd inicial coincide con BaseDirectory;
13. Named Pipe rechaza protocolo/requestId inválido en tests del protocolo.

### IntegrationTests / MCP

1. catálogo contiene `python_execute` y `python_reset`;
2. schemas marcan `workId` y `code` requeridos;
3. create work -> execute `x=40` -> execute `print(x+2)` => 42;
4. excepción devuelve `ok=true`, `status=exception`;
5. reset -> variable anterior deja de existir;
6. close work -> python_execute devuelve `resource_closed`;
7. timeout se representa con error estructurado `deadline_exceeded`;
8. contenido textual MCP no duplica stdout/traceback; permanece en structuredContent.

## Criterio de cierre de E1

E1 se considera cerrado cuando:

- todos los tests existentes siguen verdes;
- nuevos tests Python pasan en Debug y Release;
- no hay warnings;
- CPython usado por Loom no depende de PATH/registro del usuario;
- persistencia, reset, busy, timeout y cleanup están demostrados;
- ningún worker/subproceso queda vivo después de test/close/timeout;
- smoke real por Secure MCP Tunnel confirma persistencia;
- fresh-agent puede descubrir y usar Python sin instrucciones privadas.

## Fuera de alcance de E1

- Computer / UIA / screenshots;
- PyAutoGUI;
- imágenes;
- pip dinámico;
- paquetes científicos;
- notebooks/Jupyter;
- múltiples workers por WorkSession;
- PythonHandle público;
- worker independent;
- bridge `loom.*`;
- sandbox;
- ejecución remota;
- persistence del namespace a disco.

## Fuentes principales

- Python embeddable package:
  https://docs.python.org/3/using/windows.html
- CPython 3.14.8 embeddable amd64:
  https://www.python.org/ftp/python/3.14.8/
- OpenAI Computer Use sample:
  https://github.com/openai/openai-cua-sample-app
- OpenAI Python/PyAutoGUI sample:
  https://github.com/openai/openai-cua-sample-app/blob/main/python-app/README.md
- .NET PipeOptions.CurrentUserOnly:
  https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0
- MCP C# SDK 2.2.0 binary content regression:
  https://github.com/modelcontextprotocol/csharp-sdk/issues/1835
