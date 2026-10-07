# Python 2 - Bridge privado loom.*

> Estado: **P2.0 y P2.1 cerrados; P2.2 (Process bridge) pendiente de investigación/análisis específico.**
>
> Objetivo: permitir que el código ejecutado dentro del worker Python invoque capabilities internas de LoomLCI mediante un módulo privado `loom.*`, conservando WorkSession, validaciones, lifecycle, errores, observabilidad y cancellation, sin volver a entrar por MCP/Secure MCP Tunnel.

## Baseline relevante

Python 1 está cerrado end-to-end:

- CPython 3.14.8 x64 embeddable privado;
- un worker persistente SessionOwned por WorkSession;
- Named Pipe privado full-duplex entre Host y worker;
- protocolo privado v1, JSON UTF-8 con framing length-prefix;
- `python_execute` serializa una ejecución por worker;
- timeout/cancel/protocol corruption descartan el worker;
- paquetes privados administrados mediante `python_packages_prepare`;
- `-X thread_inherit_context=1` activo;
- stdout/stderr separados del canal de control.

El protocolo v1 hoy es estrictamente:

```
Host -> worker: execute
worker -> Host: result
```

Mientras se ejecuta el código, el Host queda esperando un único `result`.

## Hallazgo de lifecycle: threads sobreviven al execute

Se comprobó sobre el runtime instalado que código Python puede crear un thread daemon, devolver el resultado de `python_execute` y dejar ese thread vivo. El thread modificó estado compartido después de haber terminado el primer execute y ese cambio fue visible en el execute siguiente.

Consecuencia: un bridge no puede basarse sólo en “el worker está vivo”. Cada callback debe quedar ligado a la **ejecución concreta** que lo originó. Un thread tardío de una ejecución anterior no puede poder emitir RPC dentro de una ejecución posterior.

## Arquitecturas evaluadas

### A. Python -> MCP/tunnel -> LoomLCI — descartada

No conviene hacer que el worker sea un cliente MCP de su propio Host:

- reentra por una superficie externa innecesaria;
- depende de Secure MCP Tunnel/app/cache de tools;
- duplica serialización y validación;
- expone conceptos de transporte al runtime privado;
- complica correlación con la WorkSession actual;
- puede introducir recursión y approvals/metadata irrelevantes dentro del propio Host.

Python 2 debe usar Core directamente.

### B. Segundo Named Pipe exclusivo para bridge — no recomendado

Ventaja: deja intacto el camino actual `execute/result`.

Problemas:

- segundo listener/task de fondo por worker;
- callbacks podrían ocurrir fuera de una ejecución activa;
- cancellation y timeout dejan de estar naturalmente ligados a `python_execute`;
- lifecycle/close/expiry es más complejo;
- aumenta la superficie de concurrencia y cleanup.

No aporta suficiente valor frente al pipe full-duplex ya existente.

### C. Multiplexar el Named Pipe actual — recomendado

Protocolo conceptual v2:

```
Host   -> Worker: execute(requestId)
Worker -> Host:   bridge_call(requestId, callId, method, args)
Host   -> Worker: bridge_result(requestId, callId, ok/result|error)
... cero o más callbacks ...
Worker -> Host:   result(requestId)
```

El Host deja de esperar “un único frame result” y pasa a ejecutar una **conversation pump**:

1. envía `execute`;
2. lee frames;
3. si recibe `bridge_call`, valida IDs/método/payload;
4. despacha internamente;
5. devuelve `bridge_result`;
6. continúa leyendo;
7. termina sólo al recibir el `result` del request activo.

No hay HTTP, MCP ni otro proceso intermedio.

## Correlación y threading

### Identidad

Cada callback lleva:

- `requestId`: ID del `python_execute` activo;
- `callId`: ID único del callback.

Host y worker validan ambos estrictamente.

### Threads

El worker debe mantener:

- un `ContextVar` con el `requestId` de la ejecución;
- un estado global con la ejecución actualmente activa;
- un lock exclusivo para transacciones del bridge.

Gracias a `thread_inherit_context=1`, un thread creado durante el execute A hereda A.

Antes de enviar un callback:

1. obtiene su requestId heredado;
2. toma el bridge lock;
3. verifica que coincide con la ejecución global activa;
4. realiza `bridge_call -> bridge_result` completo manteniendo el lock.

Al terminar el código principal:

1. el thread principal toma el mismo lock;
2. esto espera cualquier callback ya iniciado;
3. invalida la ejecución global;
4. recién después genera el `result` final.

Por lo tanto:

- un callback concurrente válido se serializa correctamente;
- el `result` final nunca se cruza con un callback en vuelo;
- un thread tardío de A durante B conserva A y falla localmente por mismatch;
- un thread creado fuera de una ejecución activa no puede usar el bridge;
- múltiples callbacks concurrentes se serializan inicialmente; no se necesita multiplexación paralela.

CPython 3.14 confirma que `-X thread_inherit_context=1` hace que un `Thread` nuevo arranque con una copia del contexto del caller. Esto cubre threads creados durante el execute. Un thread/pool preexistente que no herede el `ContextVar` del execute no podrá usar `loom.*`; en el primer corte esto se considera una limitación explícita y segura, no se intenta inferir el request activo globalmente porque eso permitiría que un thread viejo se cuele en una ejecución posterior.

El bridge inicial será **sincrónico**. Una superficie `loom.aio` puede evaluarse sólo si aparece una necesidad real.

## Reutilización de Core

El bridge no debe llamar providers directamente.

Agregar una abstracción conceptual:

```
IPythonBridgeDispatcher
    DispatchAsync(workId, method, args, cancellationToken)
```

Su implementación vive en Core y usa `FilesystemCapability`, `ProcessCapability`, `VisualFilesCapability` y `ResourceRegistry`. El grafo de DI actual permite esto sin ciclos: esas capabilities no dependen de Python.

El dispatcher **no se fija al arrancar el worker**. Core define `PythonBridgeCall(method, arguments)` y un `PythonBridgeHandler`; `PythonCapability` entrega ese handler, ligado a la WorkSession, en cada `IPythonWorkerResource.ExecuteAsync(spec, bridgeHandler, cancellationToken)`. Así el backend Windows sólo puede despachar callbacks mientras existe el `python_execute` que los originó; no queda un callback global utilizable entre ejecuciones.

Su implementación usa las capabilities Core existentes.

Ventajas:

- conserva validaciones;
- conserva resolución relativa a `WorkSession.BaseDirectory`;
- conserva events/telemetría;
- conserva Job Objects y ResourceRegistry;
- conserva cancellation/timeouts;
- evita duplicar lógica de MCP.

Las calls Core anidadas pueden adquirir otra invocation lease sobre la misma WorkSession. El modelo actual de WorkSession permite múltiples invocaciones activas y el expiry exige activeInvocationCount=0, por lo que esta reentrancia está estructuralmente soportada.

La WorkSession usada por el bridge es siempre la del worker. Python **no recibe ni elige un workId**.

## Superficie Python propuesta

El módulo es privado, siempre presente y no requiere `python_packages_prepare`.

```python
import loom
```

### Base

```python
loom.__bridge_version__
loom.capabilities()
loom.LoomError
```

`loom.capabilities()` devuelve localmente la superficie disponible para que el módulo sea autodocumentable.

Los wrappers tienen firmas Python explícitas y docstrings; no se expone una API pública genérica `loom.call(method, ...)`.

### `loom.fs`

Primer alcance recomendado:

- `list_tree`
- `find_paths`
- `search_text`
- `read_files`
- `apply_patch`
- `manage_directory`
- `read_pdf` (textual/structured)

Los paths relativos conservan la base de la WorkSession. Los absolutos siguen permitidos, coherente con Full Trust.

No incluir todavía:

- `view_image`
- `render_pdf_page`

Esos resultados son binarios y encajan mejor con Python 3.

### `loom.process`

Incluir:

- `run`
- `start`
- `status`
- `read`
- `write`
- `resize`
- `terminate`
- `release`

`loom.process.start` crea **sólo procesos SessionOwned**. No expone ownership independent en la primera versión.

Motivo: un proceso creado desde un worker debe quedar ligado a la misma WorkSession y limpiarse junto con ella. Si se necesita un proceso independiente deliberadamente, sigue existiendo la tool MCP pública o el propio `subprocess` de Python.

Para operaciones que reciben un process handle, el dispatcher valida que:

- el recurso sea Process;
- siga activo;
- sea SessionOwned;
- su `OwnerWorkId` sea exactamente la WorkSession del worker.

Un handle de otra WorkSession o independent no puede ser operado mediante ese bridge.

## Superficies excluidas inicialmente

### Work

No exponer:

- `work_create`
- `work_close`

El worker ya está ligado a una WorkSession. `work_close` desde dentro de su propia ejecución intentaría destruir el recurso que está ejecutando la llamada.

### Python

No exponer:

- `python_execute`
- `python_reset`
- `python_packages_prepare`

Serían recursivos o podrían cerrar/mutar el worker activo.

### Work Plan

No incluir inicialmente `work_plan_*`.

No hay impedimento técnico fuerte, pero Work Plan es estado lógico del agente y no aporta un caso de uso suficiente dentro de un script Python. El agente puede manipularlo antes/después del execute.

### Visual binario

`filesystem_view_image` y `filesystem_render_pdf_page` quedan fuera de Python 2 inicial para no mezclar el bridge estructurado con la futura ruta binaria de Python 3.

### Computer

Computer sigue fuera del roadmap activo. El dispatcher genérico por método permitirá agregarlo después sin rediseñar el protocolo.

## API y resultados

El bridge transporta solamente JSON estructurado en Python 2:

- dict;
- list;
- str;
- int/float finito;
- bool;
- None.

Los wrappers devuelven tipos Python nativos. Las keys públicas deben ser `snake_case`.

El bridge no reutiliza DTOs de `LoomLCI.Mcp`: Core no referencia al assembly MCP y debe seguir agnóstico del transporte externo. `PythonBridgeDispatcher` mapea explícitamente resultados Core a objetos JSON privados del bridge mediante `System.Text.Json`; los wrappers Python convierten esos objetos a `dict/list` nativos. Esto evita crear una dependencia Core -> MCP y permite evolucionar metadata MCP sin romper `loom.*`.

La serialización es JSON estricta: UTF-8 válido, sin `NaN`/`Infinity`, profundidad acotada y sólo tipos estructurados soportados. Enums de Core se convierten a strings snake_case.

Los process handles se devuelven como strings opacos.

### Errores

Un error normal de una capability **no corrompe el worker**.

Host:

```json
{
  "ok": false,
  "error": {
    "code": "not_found",
    "message": "...",
    "retryable": false,
    "details": null
  }
}
```

Worker:

```python
try:
    ...
except loom.LoomError as exc:
    print(exc.code, exc.retryable, exc.details)
```

Si no se captura, aparece como excepción Python normal de `python_execute`; el worker sigue sano.

Sólo framing inválido, IDs inconsistentes, corrupción del pipe o una violación de protocolo invalidan el worker.

## Cancellation y timeout

Todos los callbacks reciben el mismo cancellation token efectivo del `python_execute`.

- timeout externo del `python_execute` sigue siendo el deadline máximo;
- una capability puede tener además un timeout propio más corto, por ejemplo `loom.process.run(timeout=...)`;
- si vence el timeout global, se conserva el comportamiento actual: se cancela la operación y se descarta el worker;
- si una capability devuelve un error normal propio, el worker permanece usable.

## Tamaños

No diseñar Python 2 como transporte binario.

Mantener frames estrictamente acotados. Límites iniciales cerrados para implementación:

- execute request: límite actual de 2 MiB;
- `bridge_call`: **2 MiB** máximo;
- `bridge_result`: **8 MiB** máximo;
- result final de Python: conserva el límite actual de 32 MiB del protocolo;
- si un resultado estructurado del bridge excede 8 MiB, responder con `LoomError` recuperable (`unsupported`/payload too large) en vez de matar el worker.

Las operaciones que ya tienen paginación deben preservarla.

## Namespace reservado `loom`

El módulo se instala explícitamente en `sys.modules["loom"]` al iniciar el worker, después de incorporar el package environment y antes de ejecutar código de usuario.

Esto garantiza que `import loom` resuelva el bridge privado y no dependa del contenido de site-packages.

Además, Python 2 debería declarar el top-level `loom` como namespace reservado y rechazar environments administrados que publiquen:

- `site/loom.py`;
- `site/loom/`.

Esto evita ambigüedad con paquetes de terceros. La reserva se valida **después de `uv pip sync` y antes de publicar el environment**.

Además hay que incrementar `PackageStoreSchemaVersion` de 1 a 2. El schema forma parte del hash de `environmentId` y el marker existente ya lo valida, por lo que los environments Python 1 previos no se reutilizarán accidentalmente bajo las nuevas reglas: se crearán IDs v2 separados. Esto también preserva rollback, porque un Host viejo sigue calculando IDs v1.

La implementación privada puede vivir bajo otro nombre interno, por ejemplo `_loomlci_bridge`, y publicar `loom` explícitamente en `sys.modules`. Python 3.14 consulta `sys.modules` antes de buscar módulos en `sys.path`, por lo que el alias privado tiene precedencia sobre el entorno de paquetes. El módulo puede registrar además `loom.fs` y `loom.process` como submódulos para soportar tanto `import loom` como `import loom.fs`.

## Asset del módulo

No conviene inflar indefinidamente `worker.py`.

Agregar un asset versionado:

```
runtime/python/loom_bridge.py
```

Embebido junto con `worker.py` y materializado content-addressed bajo `%LOCALAPPDATA%\LoomLCI\assets\python`.

El worker lo carga por path explícito; no depende de `sys.path`, cwd ni packages instalados.

## Versionado

El protocolo Worker/Host debe pasar de **v1 a v2**, porque cambian los tipos de frames y la conversación.

No hace falta cambiar CPython 3.14.8.

Los assets son content-addressed, por lo que el Host nuevo materializa automáticamente worker/bridge nuevos sin modificar el runtime base.

Exponer además `loom.__bridge_version__ = 1` para versionar la API Python independientemente del framing.

## Tool count

Python 2 no necesita una nueva tool MCP.

El catálogo público permanece en **25 tools**.

En P2.0 cambia únicamente la implementación interna de `python_execute`; no se agregó ninguna tool ni se modificó todavía su metadata pública, porque `loom.capabilities()` aún devuelve una superficie vacía. La descripción MCP/skill se actualizará cuando P2.1 incorpore la primera capability útil. La reconciliación final del plugin sigue después del bloque Python.

## Implementación y validación de P2.0

P2.0 quedó implementado con:

- protocolo Worker/Host **v2** sobre el mismo Named Pipe full-duplex;
- conversation pump `execute -> bridge_call/bridge_result* -> result`;
- handler del bridge entregado por `PythonCapability` **por cada ejecución**, ligado al WorkId de la WorkSession;
- correlación estricta `requestId`/`callId`;
- callbacks sincrónicos serializados y protección frente a threads tardíos mediante `ContextVar` + generación activa;
- errores normales convertidos en `loom.LoomError` sin invalidar el worker;
- timeout/cancel/protocol corruption conservan la semántica de descartar el worker;
- asset privado `runtime/python/loom_bridge.py`, materializado content-addressed y publicado como `loom` en `sys.modules`;
- API foundation `loom.__bridge_version__ == 1`, `loom.LoomError` y `loom.capabilities()`; todavía no hay wrappers `loom.fs`/`loom.process`;
- frames del bridge fijados en 2 MiB para calls y 8 MiB para results;
- package-store schema **v2**, con namespace top-level `loom` reservado antes de publicar environments nuevos.

Validación:

- Core: **104/104**;
- Windows: **159/159**;
- Integration: **17/17**;
- Launcher: **20/20**;
- MCP: **5/5**;
- PdfWorker: **6/6**;
- suite Release serial (`-m:1`): **311/311**;
- smoke real contra Host Release: NumPy 2.5.3 + Pandas 3.0.6 + `import loom`, bridge API 1, `loom.capabilities() == []`, cálculo real y reutilización del environment v2: **`P20_SMOKE_OK`**.

Las corridas paralelas mostraron flakes históricos de tests Process/ConPTY por locks transitorios de archivos PID; la misma suite Windows pasó 159/159 aislada y la suite completa serial pasó 311/311. No se atribuye ese comportamiento a P2.0.

El runtime instalado permanece deliberadamente en `0.1.0-dev-python1`; el deployment/cutover de Python 2 está reservado para P2.4.

## Subetapas propuestas

### P2.0 - Protocol v2 + bridge foundation — CERRADO

- contratos privados Core;
- protocol v2;
- conversation pump Host;
- `bridge_call/bridge_result`;
- correlation requestId/callId;
- error envelope;
- cancellation;
- bridge lock + generation/context propagation;
- asset `loom_bridge.py`;
- `import loom`, `LoomError`, `capabilities()`;
- unknown methods -> `unsupported` recuperable.

Tests:

- cero, uno y múltiples callbacks antes del result;
- fragmented frames;
- malformed frames / ID mismatch;
- normal LoomError no mata worker;
- timeout sí descarta;
- bridge call threaded dentro del mismo execute;
- stale thread de execute anterior no puede usar ejecución nueva;
- final result espera callback ya iniciado.

## Investigación y diseño específico de P2.1

P2.1 reutiliza **Filesystem Core directamente**. No debe duplicar acceso a disco ni llamar a las tools MCP.

### Routing modular del bridge

Antes de agregar métodos reales conviene convertir `PythonBridgeDispatcher` en un router de módulos privados:

```text
PythonBridgeDispatcher
  -> bridge.capabilities
  -> PythonFilesystemBridgeModule   (P2.1)
  -> PythonProcessBridgeModule      (P2.2)
```

Contrato conceptual:

```csharp
IPythonBridgeModule
    IReadOnlyList<string> Methods
    DispatchAsync(workId, call, cancellationToken)
```

Los módulos se registran como singletons y el dispatcher recibe `IEnumerable<IPythonBridgeModule>`. `bridge.capabilities` agrega sus methods en orden determinista. Esto evita que el dispatcher central crezca con todos los métodos de Filesystem + Process y deja P2.2 sin rediseño.

No hay ciclo de DI: `FilesystemCapability` y `VisualFilesCapability` no dependen de Python.

### Reentrancia y WorkSession

Cada callback P2.1 recibe únicamente el `WorkId` del worker. Python no puede elegir otro WorkId.

El módulo llama a:

- `FilesystemCapability` para árbol, paths, search, read, patch y directorios;
- `VisualFilesCapability.ReadPdfTextAsync` para PDF textual.

Estas capabilities vuelven a entrar a `InvocationRunner` con el mismo WorkId. El modelo actual admite múltiples invocation leases simultáneas sobre una WorkSession y expiry exige `activeInvocationCount == 0`, por lo que la nested invocation es segura y mantiene:

- resolución relativa contra `WorkSession.BaseDirectory`;
- cancellation de WorkSession;
- eventos/telemetría Core;
- validaciones existentes;
- providers Windows actuales.

### Métodos privados del protocolo

P2.1 agrega exactamente estos methods:

```text
fs.list_tree
fs.find_paths
fs.search_text
fs.read_files
fs.apply_patch
fs.manage_directory
fs.read_pdf
```

Por lo tanto `loom.capabilities()` devuelve esos siete strings en orden estable.

No se agregan methods para imágenes ni render PDF en P2.1.

### API Python

El asset privado registra `loom.fs` como submódulo en `sys.modules` y como atributo de `loom`. Deben funcionar tanto:

```python
import loom
loom.fs.list_tree(".")
```

como:

```python
import loom.fs
loom.fs.list_tree(".")
```

Firmas propuestas:

```python
loom.fs.list_tree(
    path=".",
    *,
    include_generated=False,
    exclude_directories=None,
    max_depth=3,
    max_entries=1000,
    cursor=None,
)

loom.fs.find_paths(
    path,
    queries,
    *,
    match_mode="substring",
    type="any",
    include_generated=False,
    exclude_directories=None,
    max_depth=12,
    max_results=100,
    cursor=None,
)

loom.fs.search_text(
    path,
    queries,
    *,
    case_sensitive=False,
    include_generated=False,
    exclude_directories=None,
    max_depth=12,
    max_results=100,
    context_lines=1,
    cursor=None,
)

loom.fs.read_files(files)
loom.fs.apply_patch(changes)
loom.fs.manage_directory(action, path)

loom.fs.read_pdf(
    path,
    *,
    start_page=1,
    max_pages=10,
)
```

`read_files(files)` mantiene una forma estructurada simple y explícita:

```python
loom.fs.read_files([
    {"path": "a.txt"},
    {"path": "b.txt", "offset": 10, "limit": 20},
])
```

`apply_patch(changes)` usa el mismo vocabulario lógico que la tool MCP:

```python
loom.fs.apply_patch([
    {
        "op": "replace",
        "path": "file.txt",
        "old_text": "old",
        "new_text": "new",
        "expected_occurrences": 1,
    }
])
```

No se agrega todavía azúcar adicional como `read_file()`, `mkdir()` o `rm()`; primero se mantiene una superficie pequeña y paralela a Core.

Los wrappers tienen firmas explícitas y docstrings, normalizan sólo ergonomía local y llaman al método privado correspondiente. La validación autoritativa sigue estando en Core.

### Parsing de argumentos

Aunque los wrappers generan payloads válidos, `loom._bridge_call` sigue siendo accesible técnicamente y el dispatcher debe tratar todo payload como no confiable.

Cada método necesita parsing estricto:

- `arguments` debe ser object;
- tipos JSON exactos;
- campos requeridos explícitos;
- defaults iguales a Core/MCP;
- enums sólo por strings soportados;
- arrays con límites equivalentes;
- campos desconocidos -> `invalid_argument` para detectar typos y drift.

No conviene deserializar directamente DTOs de MCP. Los contratos privados pueden ser records/classes Core con nombres JSON snake_case y `UnmappedMemberHandling=Disallow`, o parsing equivalente explícito.

### Forma de resultados

Errores no vuelven como `{"ok": false}` al usuario Python: el asset convierte automáticamente el envelope privado a `loom.LoomError`.

Los éxitos retornan `dict/list` nativos con keys **snake_case** estables.

Ejemplo de `list_tree`:

```python
{
    "root": r"C:\...",
    "max_depth": 3,
    "max_entries": 1000,
    "entries": [
        {
            "path": "src/file.cs",
            "name": "file.cs",
            "type": "file",
            "size": 1234,
            "depth": 2,
            "children_excluded": None,
        }
    ],
    "truncated": False,
    "next_cursor": None,
}
```

Las claves nullable se mantienen presentes con `None` en Python para hacer el shape predecible.

Mapping fijado:

- enum entry type -> `file | directory | symlink`;
- match mode -> `substring | suffix`;
- DTOs MCP no se reutilizan;
- `read_pdf` devuelve metadata + `pages[]` textual, sin bytes/imágenes.

### Límites del bridge vs Filesystem Core

P2.0 fijó:

- `bridge_call`: 2 MiB;
- `bridge_result`: 8 MiB.

Esto implica dos diferencias deliberadas con la tool MCP:

1. `loom.fs.apply_patch` no puede transportar un patch cercano al límite Core de 16 MiB si el frame total supera 2 MiB.
2. `loom.fs.read_files` no puede devolver hasta el presupuesto interno de 64 MiB ni el presupuesto MCP de ~9 MiB: el resultado privado debe caber en 8 MiB.

Esto es aceptable para P2.1: el bridge está orientado a operaciones estructuradas normales dentro de scripts, no a transporte masivo. Para archivos grandes Python ya puede trabajar localmente con `open()`, y los rangos/cursors permiten mantener llamadas estructuradas acotadas.

Pero el fallo debe ser explícito y recuperable.

Agregar en Core un contrato compartido de límites, por ejemplo `PythonBridgeLimits`, y hacer que Windows Protocol use esos mismos valores para evitar drift.

Antes de enviar un resultado, el módulo Filesystem debe medir el JSON mapeado. Si excede el budget:

```text
code: unsupported
details.reason: bridge_payload_too_large
details.max_bridge_result_bytes: 8388608
```

Para `read_files`, el mensaje debe recomendar reducir `limit` o dividir archivos entre llamadas.

El guard genérico de `WriteBridgeResultAsync` permanece como última defensa.

### Semántica que se conserva

`loom.fs` hereda de Core:

- paths relativos a `BaseDirectory`;
- paths absolutos permitidos;
- pruning generated por default;
- cursores opacos y detección de stale cursor;
- `search_text` literal OR, no regex;
- excerpts de search limitados a 500 chars por línea/context;
- lectura de texto con ranges 1-based;
- apply_patch validado y rollback best-effort;
- create directory con padres;
- delete directory sólo si está vacío;
- PDF textual crash-isolated, sin OCR;
- límite PDF 64 MiB, hasta 25 páginas/call y output textual agregado acotado.

No se replica manualmente ninguna de esas reglas en Python salvo la validación superficial del wrapper.

### Errores

Los `LoomError` de Filesystem pasan sin cambiar sus códigos:

- `invalid_argument`;
- `not_found`;
- `conflict`;
- `access_denied`;
- `unsupported`;
- etc.

`details` se conserva estructurado. Los errores propios del bridge usan keys snake_case.

Un error normal no descarta el worker.

### Metadata MCP

P2.1 no agrega tools: el catálogo sigue en **25**.

Sí vuelve útil la superficie `loom.*`, por lo que al implementar P2.1 conviene actualizar la descripción pública de `python_execute` y las server instructions para indicar que `import loom` permite Filesystem estructurado dentro del worker.

Eso modifica metadata del contrato MCP, por lo que:

- regenerar snapshot canónico;
- correr verifier del plugin;
- no publicar todavía una nueva versión del plugin privado; la reconciliación final sigue al cierre del bloque Python.

### Tests necesarios

#### Core

- router agrega methods determinísticamente;
- método duplicado entre modules falla al construir/inicializar;
- unknown method -> `unsupported`;
- parsing inválido/unknown fields -> `invalid_argument`;
- mapping snake_case de cada resultado;
- errores Core pasan a bridge sin convertirse en `internal`;
- payload >8 MiB -> `unsupported` recuperable.

#### Worker/bridge

- `import loom.fs`;
- wrappers generan method/arguments correctos;
- LoomError sigue sano;
- llamadas repetidas en el mismo execute;
- calls desde thread heredado continúan funcionando con `loom.fs`.

#### Integración Host real

En una WorkSession temporal:

1. `loom.fs.list_tree(".")` relativo a BaseDirectory;
2. `find_paths` + cursor;
3. `search_text` + cursor;
4. `read_files` con ranges;
5. `apply_patch` real y verificación;
6. `manage_directory` create/delete;
7. `read_pdf` textual sobre fixture real;
8. error `not_found` capturado y segundo execute exitoso;
9. environment Python v2 con NumPy/Pandas + `loom.fs` coexistiendo.

### Criterio de cierre de P2.1

P2.1 queda cerrado cuando el Host Release del repo pueda ejecutar, en un único worker persistente:

```python
import loom

tree = loom.fs.list_tree(".", max_depth=2)
hits = loom.fs.search_text(".", ["PythonBridge"], max_results=10)
files = loom.fs.read_files([{"path": "README.md", "limit": 20}])

print(tree["root"])
print(len(hits["matches"]))
print(files["files"][0]["has_more_after"])
```

y además realizar un patch real, leer PDF textual, recuperar un `LoomError` normal sin perder el worker y coexistir con un environment NumPy/Pandas.

No requiere deployment/cutover del runtime instalado; eso sigue reservado para P2.4.

### P2.1 - Filesystem bridge — CERRADO

Implementado y validado:

- `PythonBridgeDispatcher` convertido en router modular mediante `IPythonBridgeModule`, con detección de methods duplicados y `loom.capabilities()` determinista;
- nuevo `PythonFilesystemBridgeModule` sobre `FilesystemCapability` + `VisualFilesCapability`, sin reentrada por MCP;
- siete methods privados: `fs.list_tree`, `fs.find_paths`, `fs.search_text`, `fs.read_files`, `fs.apply_patch`, `fs.manage_directory`, `fs.read_pdf`;
- submódulo privado `loom.fs`, disponible con `import loom` y `import loom.fs`;
- wrappers Python explícitos con defaults equivalentes a Core/MCP;
- parsing estricto JSON con campos desconocidos rechazados;
- resultados nativos `dict/list` con keys `snake_case` y enums string;
- `LoomError` de Core preservado sin invalidar el worker;
- `PythonBridgeLimits` compartido entre Core y Windows: 2 MiB call / 8 MiB result / method <=128 chars;
- oversize estructurado convertido en `unsupported` con `reason=bridge_payload_too_large` antes del guard de protocolo;
- paths relativos conservan `WorkSession.BaseDirectory`, cursors y validaciones Core;
- PDF textual reutiliza el worker aislado existente, sin OCR ni bytes visuales;
- descripción MCP de `python_execute` y server instructions actualizadas; snapshot canónico regenerado y plugin `0.3.0` verificado con **25 tools**.

Validación P2.1:

- Core: **113/113**;
- Windows: **159/159**;
- Integration: **18/18**;
- Launcher: **20/20**;
- MCP: **5/5**;
- PdfWorker: **6/6**;
- suite Release serial (`-m:1`): **321/321**;
- integración real `loom.fs`: árbol/cursor, path search/cursor, text search/cursor, read ranges, patch, directorios, PDF textual, error `not_found` recuperable y segundo execute con worker persistente: **OK**;
- smoke Host Release con environment Python v2 reutilizado + NumPy **2.5.3** + Pandas **3.0.6** + `loom.fs`: **`P21_SMOKE_OK`**.

El runtime instalado permanece deliberadamente en `0.1.0-dev-python1`; el deployment/cutover de Python 2 sigue reservado para P2.4.

### P2.2 - Process bridge

- `run`;
- lifecycle durable completo;
- start forzado SessionOwned;
- validation de ownership en handles;
- terminal/write/resize;
- terminate/release;
- work_close limpia procesos creados desde Python.

Acceptance:

- run real;
- proceso pipes;
- proceso terminal;
- handle de otra WorkSession rechazado;
- independent rechazado;
- close/expiry sin leaks.

### P2.3 - Hardening / evaluation

- concurrency y threads;
- payload limits;
- cancellation en callback;
- crash/broken pipe;
- worker reset;
- package environment change;
- repeated bridge calls;
- benchmark básico de overhead local;
- suite completa Release.

### P2.4 - Deployment / consumer smoke

- portable/update/cutover;
- IntegrationTests contra Host instalado;
- smoke MCP real:
  - preparar NumPy;
  - ejecutar Python;
  - `import loom`;
  - filesystem bridge;
  - process bridge;
  - error recuperable;
  - segundo execute demuestra worker persistente/sano;
- fresh-agent luego de refrescar metadata de tools cuando corresponda.

## Criterio de cierre

Python 2 queda cerrado cuando un agente pueda ejecutar algo equivalente a:

```python
import numpy as np
import loom

tree = loom.fs.list_tree(".", max_depth=2)
proc = loom.process.run("git.exe", ["status", "--short"])

print(np.mean([1, 2, 3]))
print(len(tree["entries"]))
print(proc["exit_code"])
```

usando el mismo worker/WorkSession, sin MCP recursivo, con lifecycle/cancellation correctos y con el worker todavía usable después de un error normal del bridge.

## Decisiones recomendadas

1. **Mismo Named Pipe, multiplexado.**
2. **Core directo, nunca MCP/tunnel.**
3. **Bridge siempre ligado al WorkId del worker.**
4. **Protocol v2 + bridge API v1.**
5. **Callbacks sincrónicos y serializados inicialmente.**
6. **Correlación por generation/requestId para threads.**
7. **Filesystem + PDF text + Process como alcance inicial.**
8. **Work, Python, Work Plan y binary visual fuera.**
9. **Procesos iniciados desde Python siempre SessionOwned.**
10. **Namespace `loom` reservado y módulo autodocumentable.**
11. **Sin nueva tool MCP: catálogo permanece en 25.**
