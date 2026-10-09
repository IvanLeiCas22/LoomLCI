# Bloque E - Python Runtime

## Estado

**E1 cerrado. E1.1–E1.5 implementados y validados, incluido smoke real por Secure MCP Tunnel y fresh-agent específico de Python.**

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

El worker captura `BaseException` y devuelve `status=exception`, por lo que `SyntaxError`, excepciones ordinarias, `SystemExit` y `KeyboardInterrupt` explícito no destruyen el REPL. El namespace y cualquier asignación previa al error permanecen. `os._exit`, crash nativo o pérdida del pipe sí son fallos duros.

No hay stdin interactivo en E1: `sys.stdin` se restaura a EOF antes/después de cada ejecución, por lo que `input()` devuelve `EOFError` inmediatamente en lugar de bloquear hasta timeout.

No se introduce un DSL.

## IPC

### Canal

Named Pipe duplex privado C# <-> worker.

Servidor C#:

- `PipeDirection.InOut`;
- byte mode;
- asynchronous;
- una única instancia + `FirstPipeInstance`;
- nombre aleatorio no predecible;
- DACL protegida: allow explícito sólo al SID del usuario actual;
- deny explícito a `NetworkSid`;
- handle no heredable.

No depender únicamente de `CurrentUserOnly`: Named Pipes pueden exponerse por red y la ACL explícita evita acceso remoto incluso bajo la misma identidad. El protocolo E1.2 usa `NamedPipeServerStreamAcl.Create`.

Se validó con CPython 3.14.8 embeddable que la stdlib puede abrir directamente `\\.\pipe\...` y comunicarse con el server .NET sin dependencias externas.

### Framing

No usar stdout como protocolo.

Frames:

    uint32 little-endian payloadLength
    UTF-8 JSON payload

Límites E1.2: código ≤256 KiB UTF-8, request frame ≤2 MiB, response frame ≤32 MiB, request ID ≤128 chars, mensaje de excepción ≤16 Ki chars y traceback ≤64 Ki chars. El largo se valida antes de reservar el payload.

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

E1 captura stdout/stderr Python de cada ejecución mediante proxies permanentes de `sys.stdout` / `sys.stderr` y un `ContextVar` por ejecución. Con `-X thread_inherit_context=1`, threads normales iniciados durante la ejecución heredan el buffer; al terminar, el buffer se cierra para impedir contaminación de la siguiente llamada.

Garantizado en E1.2:

- `print()`;
- `sys.stdout.write()` / `sys.stderr.write()`;
- librerías que escriban a esos streams.

No se promete capturar dentro del resultado escrituras directas a file descriptors (`os.write(1, ...)`), extensiones nativas ni stdout/stderr heredado por subprocessos. Esas salidas permanecen en los pipes diagnósticos del proceso; para subprocessos con output relevante se usa captura explícita de Python o la capability Process.

Si el usuario reasigna `sys.stdout`/`sys.stderr`, esa escritura puede escapar de la captura de esa ejecución, pero los proxies se restauran antes de la siguiente.

No se prometen semánticas de ProcessOutputStore/cursors: cada `python_execute` es una Invocation síncrona con un resultado finito.

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

El worker corre con el environment/permisos normales del usuario. Lanzamiento fijado para E1: `-I -B -u -X utf8 -X faulthandler -X thread_inherit_context=1`. El worker agrega `""` a `sys.path` para permitir imports relativos al cwd pese al `._pth` aislado del embeddable. `-B` evita `__pycache__`. Estas opciones reducen interferencia externa y mejoran diagnóstico, pero **no crean un sandbox**.

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

### E1.2 - Worker + IPC ✓

Implementado:

- `runtime/python/worker.py`;
- `runtime/python/runtime.json` con CPython 3.14.8 x64 embeddable + SHA-256 oficial;
- protocolo v1 length-prefixed JSON UTF-8;
- handshake `hello` con versión/PID/Python;
- request IDs y validación estricta;
- límites de frame/código/excepción;
- namespace persistente;
- captura bounded de stdout/stderr con `ContextVar`;
- herencia de captura en `threading.Thread`;
- excepción estructurada capturando `BaseException`;
- stdin EOF;
- restauración de streams entre ejecuciones;
- imports desde cwd bajo embeddable aislado;
- Named Pipe con DACL user-only + deny NetworkSid + FirstPipeInstance;
- EOF/protocolo corrupto invalidan/cierran el worker;
- harness real usando Native Process + CPython privado, sin conectar todavía `PythonCapability`.

Validación E1.2:

- 13 tests nuevos de Windows;
- handshake real con CPython **3.14.8** privado;
- persistencia, Unicode, stdout/stderr, truncamiento, excepciones, `SystemExit`, stdin EOF, threads, stream restoration e imports desde cwd;
- framing fragmentado, límite previo a allocation, UTF-8 inválido y requestId inválido;
- mensaje de protocolo no soportado termina el worker con exit code 2;
- Core: **58/58**;
- Windows: **91/91**;
- Integration MCP: **6/6**;
- total Debug: **155/155**;
- total Release: **155/155**;
- build Release: **0 warnings / 0 errors**;
- ningún `python.exe` privado queda vivo al finalizar los tests.

E1.2 quedó limitado deliberadamente a worker/protocolo; E1.3 ya conecta esa base con `IPythonRuntimeProvider`, provisioning y lifecycle real vía Job Objects.

### E1.3 - Backend Windows ✓

Implementado:

- `PythonRuntimeAssets`: `worker.py` y `runtime.json` embebidos en `LoomLCI.Windows`, sin dependencia del repo/cwd del Host;
- `PythonRuntimeProvisioner`: auto-provisioning on-demand de CPython privado;
- descarga acotada a 64 MiB, SHA-256 fijado, staging + publicación por rename y cleanup ante error/cancelación;
- marker de instalación y validación de layout embeddable (`python.exe` + `pythonXY._pth`);
- worker materializado por hash bajo `%LOCALAPPDATA%\LoomLCI\assets\python`;
- `WindowsPythonRuntimeProvider` reutilizando `IProcessProvider`;
- startup deadline interno de 10 s;
- pipe creado antes del process start, conexión + handshake versionado, PID y Python exactos;
- cwd inicial desde `WorkSession.BaseDirectory` y perfil del usuario como fallback;
- variables Python externas relevantes removidas del environment;
- `WindowsPythonWorkerResource` dueño de process + pipe;
- health basado en estado interno + root process + conexión del pipe;
- `busy` inmediato para execute concurrente;
- cancelación/fallo IPC invalidan el worker y disponen el `IProcessResource`;
- Job Object mata root + descendientes en timeout, reset, crash, `work_close` y dispose;
- disposer idempotente/no-throw;
- provider/capability registrados en Host, todavía sin tools MCP públicas.

Validación E1.3:

- 12 tests Windows nuevos;
- provisioning falso verificado, reuse y concurrencia con una sola descarga;
- hash incorrecto y cancelación limpian staging;
- ejecución real desde cwd ajeno al repo;
- persistencia y CPython 3.14.8 exacto;
- `os._exit` invalida y la siguiente llamada recrea con PID nuevo;
- timeout de loop infinito mata worker + subprocess;
- `work_close` mata worker + subprocess, incluso con ejecución activa;
- reset cambia PID y limpia namespace;
- `busy` real bajo ejecución concurrente;
- Core: **58/58**;
- Windows: **103/103**;
- Integration MCP existente: **6/6**;
- total Debug: **167/167**;
- total Release: **167/167**;
- build Release: **0 warnings / 0 errors**;
- ningún `python.exe` privado queda vivo tras la suite.

E1.3 dejó preparado el backend; E1.4 ya expone esa capability mediante MCP.

### E1.4 - MCP ✓

Implementado:

- `PythonTools` con `python_execute` y `python_reset`;
- DTOs propios y `ToolEnvelope<...>`/structuredContent siguiendo el patrón MCP existente;
- `python_execute(workId, code, timeoutSeconds=60, maxOutputChars=65536)`;
- schema: `workId` y `code` requeridos, timeout 1..600 s y output 1..1.048.576 code points por stream;
- annotations de `python_execute`: destructive, non-idempotent, open-world;
- annotations de `python_reset`: destructive, idempotent, closed-world;
- excepción Python ordinaria => tool success (`ok=true`, `status=exception`); sólo fallos de Loom/infraestructura usan `isError=true`;
- stdout/stderr/traceback permanecen sólo en structuredContent; el text content no duplica payloads grandes;
- ServerInstructions explican cuándo preferir Python, Filesystem o Process;
- Host expone las dos tools por STDIO.

Hardenings incorporados antes de publicar la capability:

- límite de código de **256 KiB UTF-8** validado en Core antes de crear/tocar worker;
- Unicode inválido devuelve `invalid_argument`;
- límites de stdout/stderr y metadata se validan como **Unicode code points**, no unidades UTF-16.

Validación E1.4:

- 2 tests Core nuevos para límite UTF-8/Unicode inválido;
- 1 test Windows nuevo para truncamiento correcto con emoji/surrogate pair;
- 1 roundtrip MCP STDIO nuevo con Host lanzado desde cwd ajeno al repo;
- catálogo/schema/annotations de ambas tools verificados;
- persistencia `x=40 -> print(x+2)`;
- excepción Python comprobada como resultado normal y traceback sólo estructurado;
- Unicode/emoji respeta `maxOutputChars`;
- código >256 KiB devuelve `invalid_argument` sin perder el worker existente;
- reset limpia namespace;
- timeout devuelve `deadline_exceeded` y la siguiente ejecución recrea;
- WorkSession cerrada devuelve `resource_closed`;
- Core: **60/60**;
- Windows: **104/104**;
- Integration MCP: **7/7**;
- total Debug: **171/171**;
- total Release: **171/171**;
- build Release: **0 warnings / 0 errors**;
- ningún `python.exe` privado queda vivo tras la suite.

La validación Debug del Host de E1.4 se ejecutó desde un output alternativo porque el Host Debug activo mantenía el Secure MCP Tunnel de esa sesión. E1.5 recompiló y reinició el runtime administrado antes del smoke público final.

### E1.5 - Validación ✓

Validación automatizada final:

- Core: **60/60**;
- Windows: **104/104**;
- Integration MCP: **7/7**;
- total Debug: **171/171**;
- total Release: **171/171**;
- Host Debug y Release: **0 warnings / 0 errores**;
- repo limpio antes y después de la validación.

Smoke real por Secure MCP Tunnel con el Host Debug recién compilado:

- runtime administrado `loomlci`: `live` / `ready`;
- catálogo público: **17 tools**, incluidas `python_execute` y `python_reset`;
- CPython privado observado: **3.14.8**;
- persistencia demostrada con `x = 40` seguido de `print(x + 2)` => `42`;
- excepción Python ordinaria (`ValueError`) devolvió `status=exception` y el namespace siguió utilizable;
- `python_reset` cambió PID y eliminó el namespace anterior;
- loop infinito con `timeoutSeconds=1` devolvió `deadline_exceeded` y la siguiente ejecución recreó el worker correctamente;
- `work_close` completó el cleanup;
- no quedó ningún `python.exe` privado de LoomLCI vivo al finalizar.

Fresh-agent final desde un chat nuevo de ChatGPT normal, sin conocimiento previo del proyecto:

- descubrió **17 capacidades públicas**;
- usó naturalmente sólo `work_create`, `python_execute`, `python_reset` y `work_close`;
- identificó **CPython 3.14.8 de 64 bits**;
- verificó persistencia de `fresh_agent_value = 12345` entre ejecuciones;
- provocó `RuntimeError`, recibió `status=exception` y confirmó que el estado previo seguía presente;
- ejecutó `python_reset` y confirmó que la variable anterior dejó de existir;
- provocó timeout con un loop infinito, recibió `deadline_exceeded` y comprobó que la ejecución posterior funcionaba con namespace nuevo;
- cerró la WorkSession y confirmó `resource_closed` al intentar reutilizarla.

Ergonomía observada:

- el fresh-agent consideró claras y suficientes las descripciones públicas de persistencia, excepciones, reset y descarte/recreación del worker;
- el comportamiento real coincidió con el contrato público;
- única aspereza menor: los tool errors como `deadline_exceeded` llegan a ChatGPT envueltos exteriormente como error de invocación en lugar de como envelope de éxito uniforme. Esto coincide con la semántica MCP ya documentada y no se atribuye al Python Runtime;
- una primera tentativa inmediatamente posterior al timeout fue bloqueada por la capa de orquestación de OpenAI por estado de seguridad indeterminado; el reintento funcionó y no hubo evidencia de fallo en LoomLCI.

**Conclusión:** E1.5 pasa y el bloque E1 queda cerrado.

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
- Windows Named Pipe security and access rights:
  https://learn.microsoft.com/windows/win32/ipc/named-pipe-security-and-access-rights
- MCP C# SDK 2.2.0 binary content regression:
  https://github.com/modelcontextprotocol/csharp-sdk/issues/1835
