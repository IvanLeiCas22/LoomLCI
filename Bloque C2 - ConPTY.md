# Bloque C2 - ConPTY

## Estado

**Diseño aprobado. C2.1 (contrato Core/MCP) implementado y validado; siguiente etapa: C2.2 backend ConPTY.**

C1 dejó resueltos el launch nativo con `CreateProcessW`, `STARTUPINFOEX`, Job Objects, quoting, environment, working directory, estado, output retenido y lifecycle de `ProcessHandle`. C2 agrega el segundo modo de I/O previsto desde la arquitectura inicial: **terminal mediante ConPTY**.

## Objetivo

Permitir que un proceso Loom pueda ejecutarse dentro de una terminal Windows real cuando una CLI necesita semántica TTY/console, sin crear un recurso paralelo ni duplicar el lifecycle de Process.

Resultado esperado:

- modo actual `pipes` para automatización normal;
- nuevo modo `terminal` para shells, REPLs, TUIs y programas que detectan consola;
- mismo `proc_...`, ownership, Job Object, estado y terminate;
- input interactivo, stream terminal retenido con cursor y resize;
- compatibilidad desde Windows 10 1809;
- teardown que no deje descendientes ni `HPCON` vivos.

## Por qué C2

Los pipes actuales ya permiten escribir stdin y leer stdout/stderr, pero no hacen que el child vea una consola real.

ConPTY agrega:

- detección de terminal/console por parte del child;
- un único canal terminal UTF-8;
- secuencias Virtual Terminal para color, cursor, pantalla y teclas de control;
- tamaño de terminal observable por las aplicaciones;
- resize durante la ejecución;
- comportamiento correcto de shells y CLIs que cambian buffering/prompts cuando detectan TTY.

No reemplaza pipes. Ambos modos sirven casos distintos.

## Fuentes principales

- Microsoft Learn - Pseudoconsoles:
  https://learn.microsoft.com/windows/console/pseudoconsoles
- Microsoft Learn - CreatePseudoConsole:
  https://learn.microsoft.com/windows/console/createpseudoconsole
- Microsoft Learn - Creating a Pseudoconsole session:
  https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session
- Microsoft Learn - ResizePseudoConsole:
  https://learn.microsoft.com/windows/console/resizepseudoconsole
- Microsoft Learn - ClosePseudoConsole:
  https://learn.microsoft.com/windows/console/closepseudoconsole
- Microsoft Learn - ReleasePseudoConsole:
  https://learn.microsoft.com/windows/console/releasepseudoconsole
- Microsoft Terminal ConPTY samples:
  https://github.com/microsoft/terminal/tree/main/samples/ConPTY

## Hechos de plataforma relevantes

### Compatibilidad

`CreatePseudoConsole`, `ResizePseudoConsole` y `ClosePseudoConsole` están disponibles desde Windows 10 October 2018 Update / 1809.

`ReleasePseudoConsole` está disponible desde Windows 11 24H2, build 26100.

Loom debe funcionar correctamente sin `ReleasePseudoConsole`; cuando exista se usa para mejorar el ownership/teardown, no como requisito de C2.

### Comunicación

ConPTY usa dos canales síncronos:

- host -> pseudoconsole: input;
- pseudoconsole -> host: output.

Windows recomienda atender input y output independientemente para evitar deadlocks.

El stream es UTF-8 y mezcla texto plano con secuencias Virtual Terminal.

### Child process

El proceso hospedado recibe el `HPCON` mediante:

`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`

En modo terminal:

- no heredar los handles de los pipes;
- `bInheritHandles = FALSE`;
- no usar `STARTF_USESTDHANDLES`;
- no usar `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`;
- mantener `PROC_THREAD_ATTRIBUTE_JOB_LIST`;
- usar `EXTENDED_STARTUPINFO_PRESENT`;
- no usar `CREATE_NO_WINDOW` salvo que una prueba futura demuestre una necesidad concreta.

Los extremos de pipe entregados a `CreatePseudoConsole` se cierran del lado host después de crear el child. El host conserva únicamente sus extremos de lectura/escritura para comunicarse con la pseudoconsola.

### Cierre

`ClosePseudoConsole` puede producir output final.

Antes de Windows 11 24H2 puede bloquear hasta que todos los clientes se desconecten. Por eso no debe ejecutarse en el mismo flujo que drena output.

En Windows 11 24H2+, `ReleasePseudoConsole` permite ceder ownership al sistema. Después de `ReleasePseudoConsole`, el host continúa atendiendo los pipes hasta que fallen/cierren y finalmente llama `ClosePseudoConsole` para liberar el handle restante.

## Decisiones de contrato público

### 1. Un único recurso Process

No crear `terminal_...` ni un recurso paralelo.

Ambos modos usan:

`proc_...`

Esto preserva:

- ownership session-owned / independent;
- `process_status`;
- `process_write`;
- `process_terminate`;
- Job Object;
- WorkSession cleanup;
- eventos e Invocation lifecycle.

### 2. ProcessIoMode

Extender:

```text
pipes
terminal
```

Default público: `pipes`.

Los callers existentes no cambian comportamiento.

### 3. process_start

Agregar:

```text
ioMode: "pipes" | "terminal" = "pipes"
terminalColumns: int? = null
terminalRows: int? = null
```

Reglas:

- `terminalColumns` y `terminalRows` son opcionales para poder distinguir ausencia de un valor explícito;
- en `terminal`, los valores omitidos se resuelven a 80×24;
- si el caller pasa dimensiones explícitas con `pipes`, devolver `invalid_argument` en vez de ignorarlas silenciosamente;
- rango propuesto: 1..32767 porque ConPTY recibe `COORD` con componentes `short`;
- no agregar flags avanzados de ConPTY en C2;
- crear con flags `0`, sin `PSEUDOCONSOLE_INHERIT_CURSOR`.

Agregar `ioMode` a `ProcessStartResult` / DTO para que el caller conozca explícitamente qué tipo de recurso obtuvo.

### 4. process_status

Mantener semántica actual y agregar:

```text
ioMode
```

No agregar detalles de `HPCON` ni handles Win32.

### 5. process_read

Mantener una sola tool.

Firma conceptual:

```text
process_read(
  processHandle,
  stdoutCursor = 0,
  stderrCursor = 0,
  terminalCursor = 0,
  maxChars = 65536
)
```

Resultado conceptual:

```text
{
  process,
  ioMode,
  stdout?,
  stderr?,
  terminal?
}
```

Semántica:

#### pipes

- `stdout` y `stderr` presentes;
- `terminal = null`;
- `terminalCursor` debe ser 0.

#### terminal

- `terminal` presente;
- `stdout = null`;
- `stderr = null`;
- `stdoutCursor` y `stderrCursor` deben ser 0.

Si se pasa un cursor no aplicable y distinto de cero, devolver `invalid_argument`. Esto detecta errores del caller en vez de ignorarlos.

`maxChars` conserva el rango y la semántica actuales.

### 6. Cursor del stream terminal

ConPTY entrega bytes UTF-8, pero Loom expone texto .NET.

Decisión:

- decodificar el stream incrementalmente como UTF-8;
- almacenar el texto terminal en `ProcessOutputStore`;
- conservar cursores absolutos en posiciones UTF-16, igual que stdout/stderr actuales;
- no contar bytes UTF-8 ni celdas visuales;
- no interpretar secuencias VT para el cursor Loom.

Esto mantiene una sola semántica pública de cursor en Process.

Las secuencias VT forman parte del texto y cuentan como caracteres normales para el cursor.

### 7. Output raw

C2 no es un emulador de terminal.

El stream se conserva **raw**:

- texto;
- `\r`, `\n`;
- ANSI/VT;
- clears;
- cursor movement;
- colores;
- títulos;
- otras secuencias emitidas por ConPTY.

No:

- eliminar ANSI;
- reconstruir pantalla;
- normalizar carriage returns;
- entregar sólo la línea visible final.

Una capa futura puede construir snapshots de pantalla a partir del stream sin cambiar C2.

### 8. Retención

Usar la misma política de `ProcessOutputStore` que pipes.

En terminal existe un único store retenido.

La cuota, cursores, `earliestAvailableCursor`, `retainedUntilCursor`, `observedUntilCursor`, `truncated` y `retentionLimitReached` mantienen la semántica del Bloque A.

### 9. process_write

Reutilizar la tool actual.

#### pipes

Sin cambios.

#### terminal

Escribir texto literalmente al input ConPTY codificado como UTF-8.

No se agrega newline automáticamente.

Control characters y secuencias VT se permiten literalmente.

### 10. Ctrl+C

No agregar `process_interrupt` en C2.

Para una terminal, Ctrl+C se representa escribiendo ETX:

```text
"\u0003"
```

Esto coincide con el modelo de input terminal de ConPTY.

`process_terminate` sigue siendo la operación fuerte para detener el árbol completo mediante Job Object.

Si futuras pruebas reales demuestran que hace falta distinguir señales/intenciones más ricas, se diseña después con evidencia.

### 11. Resize

Agregar una tool:

```text
process_resize(processHandle, columns, rows)
```

Sólo válida para `ioMode=terminal`.

Para pipes devuelve `unsupported` o `conflict`; preferencia: `unsupported`, porque el recurso existe pero ese modo no soporta resize.

Rango:

```text
1..32767
```

Internamente llama `ResizePseudoConsole`.

No almacenar una "pantalla"; las dimensiones son metadata operacional del terminal.

## Arquitectura interna propuesta

### Core

Extender `ProcessIoMode`:

```csharp
Pipes,
Terminal
```

Agregar dimensiones iniciales a `ProcessStartRequest` y `ProcessLaunchSpec`.

Agregar `IoMode` a resultados de start/status/read.

Extender `ProcessOutputReadResult` para modelar:

- stdout/stderr en pipes;
- terminal en ConPTY.

Agregar:

```text
ProcessCapability.ResizeAsync(...)
IProcessResource.ResizeAsync(...)
```

El Core no conoce `HPCON`.

### Windows

No duplicar `WindowsProcessResource`.

Extraer/componer una abstracción interna de I/O, por ejemplo:

```text
IWindowsProcessIo
WindowsPipeProcessIo
WindowsTerminalProcessIo
```

Responsabilidades comunes que permanecen en `WindowsProcessResource`:

- PID/process handle;
- Job Object;
- estado;
- exit code;
- started/exited timestamps;
- terminate;
- lifecycle raíz;
- ownership indirecto mediante ResourceRegistry.

Responsabilidades de cada I/O backend:

#### WindowsPipeProcessIo

- stdin writer;
- stdout/stderr readers;
- dos pumps;
- dos stores;
- read;
- write;
- dispose.

Debe preservar exactamente el comportamiento actual.

#### WindowsTerminalProcessIo

- input writer;
- output reader;
- un pump;
- un terminal store;
- `HPCON`;
- resize;
- write;
- release/close;
- dispose.

### WindowsPseudoConsole

Crear un wrapper específico para `HPCON`.

Responsabilidades:

- `CreatePseudoConsole`;
- `ResizePseudoConsole`;
- detección/uso opcional de `ReleasePseudoConsole`;
- `ClosePseudoConsole`;
- sincronización interna;
- idempotencia de release/close;
- impedir resize después de comenzar teardown.

No exponer `HPCON` fuera de LoomLCI.Windows salvo al launcher durante la ventana de creación.

### Interop

Agregar a `NativeMethods.txt` sólo lo necesario:

- CreatePseudoConsole
- ResizePseudoConsole
- ClosePseudoConsole
- ReleasePseudoConsole
- COORD
- HPCON

`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` puede venir generado; si CsWin32 no expone el valor de forma utilizable, mantener una constante local documentada como ya se hace con otros atributos.

La versión instalada de CsWin32/Win32Metadata ya contiene estas APIs y tipos.

## Launcher terminal

Flujo propuesto:

1. validar tamaño;
2. construir command line;
3. construir environment;
4. crear Job Object;
5. crear input pipe síncrono;
6. crear output pipe síncrono;
7. crear `HPCON` con los extremos ConPTY;
8. preparar una única `STARTUPINFOEX` con:
   - `PROC_THREAD_ATTRIBUTE_JOB_LIST`;
   - `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`;
9. `CreateProcessW` con:
   - `bInheritHandles = FALSE`;
   - `EXTENDED_STARTUPINFO_PRESENT`;
   - `CREATE_UNICODE_ENVIRONMENT` cuando corresponda;
10. cerrar thread handle;
11. cerrar los extremos de pipe entregados a ConPTY que el host ya no necesita;
12. construir `WindowsTerminalProcessIo`;
13. construir `WindowsProcessResource`.

El launch de pipes continúa con la lógica existente.

No intentar unificar artificialmente los handles heredables de pipes con ConPTY.

## Lifecycle y teardown

### Política de lifetime

Para `ioMode=terminal`, el lifetime de la sesión terminal sigue al proceso raíz.

Cuando el root sale naturalmente:

1. conservar su exit code real;
2. iniciar teardown terminal;
3. terminar descendientes todavía vivos mediante el Job Object;
4. drenar output final;
5. liberar ConPTY.

Razón: una terminal cuyo shell/root terminó no debe quedar viva indefinidamente por un descendant background. Es equivalente a cerrar la sesión terminal y evita recursos huérfanos.

Esto es deliberadamente más estricto que el modo pipes, donde un descendant puede seguir vivo hasta `process_terminate` o cleanup del recurso.

### process_terminate

Para terminal:

1. marcar `terminating`;
2. impedir nuevos writes/resizes;
3. terminar Job Object;
4. conservar output pump activo;
5. completar teardown ConPTY;
6. esperar al root si todavía no salió;
7. fijar estado `terminated`;
8. mantener output retenido accesible.

### Windows 11 24H2+

Si `ReleasePseudoConsole` está disponible:

1. Job termina root + descendants cuando corresponde;
2. `ReleasePseudoConsole`;
3. mantener output pump hasta EOF/fallo;
4. `ClosePseudoConsole`;
5. liberar pipes/store.

### Windows 10 1809 .. Windows 11 23H2

No existe `ReleasePseudoConsole`.

1. Job termina los clientes cuando corresponde;
2. mantener output pump drenando;
3. ejecutar `ClosePseudoConsole` fuera del pump de output;
4. esperar que retorne;
5. liberar pipes/store.

Nunca ejecutar un `ClosePseudoConsole` potencialmente bloqueante en el mismo flujo encargado de drenar output.

### Dispose / work_close

Debe ser idempotente.

Orden conceptual:

1. bloquear nuevas operaciones I/O;
2. terminar Job Object;
3. teardown ConPTY mientras output continúa drenándose;
4. completar pumps;
5. liberar process handle;
6. liberar pipes;
7. liberar store/spool;
8. liberar gates.

## Concurrencia

La regla existente sigue:

- distintos ProcessHandles: concurrentes;
- writes al mismo proceso: serializados.

Agregar:

- resize del mismo terminal serializado contra teardown;
- release/close de `HPCON` serializados e idempotentes;
- write y resize deben detectar terminal en closing/closed y devolver error claro.

No confiar en `ResourceRegistry.Entry.Gate` para proteger operaciones normales: hoy ese gate sólo serializa `CloseAsync`. El recurso Windows debe proteger su propio lifecycle.

## Manejo de errores

### process_start

- dimensiones inválidas -> `invalid_argument`;
- ConPTY no disponible -> `unsupported`;
- CreatePseudoConsole falla -> `execution_failed` salvo mapping más específico;
- CreateProcess falla después de crear HPCON -> cleanup completo, sin registrar recurso parcial.

### process_read

- cursor negativo -> `invalid_argument`;
- cursor no correspondiente al modo y distinto de cero -> `invalid_argument`.

### process_write

- root/terminal ya cerrado -> `conflict` o `execution_failed` según estado;
- mantener comportamiento actual cuando el root ya no está running.

### process_resize

- dimensiones inválidas -> `invalid_argument`;
- process en pipes -> `unsupported`;
- terminal closing/closed -> `conflict`;
- ResizePseudoConsole falla -> `execution_failed`.

## Compatibilidad pública

Cambios aditivos:

- `process_start.ioMode`;
- `process_start.terminalColumns`;
- `process_start.terminalRows`;
- `process_read.terminalCursor`;
- campos `ioMode` y `terminal` en resultados;
- nueva `process_resize`.

Defaults mantienen el comportamiento actual.

No renombrar tools existentes.

El refresh del schema/actions será necesario antes del smoke/fresh-agent final porque sí cambia el contrato MCP público.

## Tests obligatorios

### Regresión pipes

Toda la suite C1 debe seguir verde sin cambios de comportamiento.

En particular:

- quoting;
- env;
- cwd;
- output cursors;
- output grande;
- Unicode;
- terminate;
- child/grandchild cleanup;
- session-owned;
- independent;
- launch failure;
- concurrencia.

### Core

- default `ioMode=pipes`;
- terminal size defaults 80x24;
- dimensiones inválidas;
- dimensiones explícitas con pipes rechazadas;
- terminalCursor validado;
- cursor incorrecto para modo rechazado;
- resize sobre pipes -> unsupported;
- ioMode propagado en start/status/read.

### Windows terminal básico

- lanzar `cmd.exe` en terminal;
- leer prompt/output;
- escribir comando;
- recibir resultado;
- salir normalmente con exit code correcto.

### Console detection

Probar un child que pueda distinguir redirección de consola.

Esperado:

- pipes -> redirected;
- terminal -> console/TTY.

La prueba debe usar una señal observable estable, no depender sólo de colores.

### VT raw

Emitir secuencias ANSI conocidas y verificar que el stream retenido las conserve byte/textualmente después de decode UTF-8.

No normalizar `\r`.

### Unicode y cursores

- Unicode BMP;
- surrogate pair;
- UTF-8 dividido entre reads del pipe;
- lecturas con `maxChars` pequeño;
- reconstrucción exacta usando `nextCursor`.

### Resize

Proceso interactivo:

1. reporta tamaño inicial;
2. `process_resize`;
3. reporta nuevo tamaño.

Probar varias dimensiones válidas y errores de rango.

### Ctrl+C

Lanzar un comando terminal de larga duración.

Enviar `"\u0003"`.

Verificar que el programa recibe/interpreta Ctrl+C sin usar `process_terminate` y que la terminal sigue siendo utilizable cuando el shell continúa.

Después salir limpiamente.

### Output grande

Generar output terminal suficiente para ejercitar spool/cursors y confirmar la misma semántica de retención que pipes.

### Tree cleanup

- terminal root -> child -> grandchild;
- `process_terminate` elimina todo;
- output previo sigue disponible.

### Natural root exit con descendant

Root crea descendant y sale.

Esperado por política C2:

- root conserva exit code real;
- descendant es eliminado durante teardown terminal;
- no queda ConPTY vivo.

### WorkSession

Session-owned terminal:

- `work_close` elimina root + descendants;
- handle queda cerrado.

Independent terminal:

- sobrevive `work_close`;
- status/read/write siguen utilizables;
- cleanup explícito posterior funciona.

### Launch failure

Forzar fallo después de haber preparado recursos cuando sea posible.

Verificar:

- sin resource registrado;
- sin job vivo;
- sin process;
- sin HPCON;
- sin pipe/pump/spool residual.

### Concurrencia

- múltiples terminales simultáneos;
- pipe + terminal simultáneos;
- writes concurrentes al mismo terminal serializados;
- resize durante output;
- resize compitiendo con terminate/close no causa crash, leak ni use-after-close.

### MCP integration

Verificar schema:

- allowed values de `ioMode`;
- rangos de columns/rows;
- `terminalCursor`;
- `process_resize`;
- descriptions suficientemente claras para fresh-agent.

Roundtrip MCP real:

1. work_create;
2. process_start terminal;
3. process_read;
4. process_write;
5. process_resize;
6. salida normal o terminate;
7. read final;
8. work_close.

### Fresh-agent final

Agente sin contexto previo debe poder inferir:

- cuándo usar pipes vs terminal;
- que terminal tiene un único stream;
- cómo continuar por cursor;
- que no debe quitar ANSI/VT;
- cómo escribir Enter/Ctrl+C;
- cómo resize;
- cuándo usar terminate;
- cleanup de session-owned e independent.

## Criterios de aceptación

C2 se considera cerrado cuando:

- pipes no sufren regresiones;
- terminal funciona end-to-end por MCP;
- cmd/PowerShell interactivos funcionan;
- una prueba demuestra detección real de consola;
- input, Ctrl+C y resize funcionan;
- stream raw y Unicode/cursors son exactos;
- output retenido funciona después de exit/terminate;
- Job Object limpia todo el árbol;
- natural root exit no deja clients/HPCON huérfanos;
- session-owned e independent conservan sus semánticas;
- teardown no deadlockea;
- suite Release/Debug sin warnings/errors;
- smoke live por Secure MCP Tunnel correcto;
- fresh-agent regression correcta.

## Fuera de C2

- emulación/renderizado de pantalla terminal;
- stripping/interpretación ANSI;
- mouse VT;
- clipboard/OSC avanzado;
- terminal snapshots semánticos;
- API específica de señales;
- graceful shutdown general distinto del input terminal;
- CPU/memory limits;
- completion ports/job telemetry;
- TTL de ProcessHandle;
- redesign de ResourceRegistry;
- Python Runtime;
- Computer Use.

## Propuesta de implementación por etapas

### C2.1 - Contrato Core/MCP ✓

Implementado:

- `ProcessIoMode.Terminal`;
- tamaño inicial opcional, resuelto a 80×24 en modo terminal;
- resultados mode-aware;
- `terminalCursor`;
- `process_resize`;
- validaciones por modo y dimensiones;
- fake/core tests;
- integration schema/roundtrip tests;
- pipes mantienen su comportamiento y `process_resize` devuelve `unsupported`.

Validación Release:

- Core: **12/12**;
- Windows: **63/63**;
- Integration MCP: **4/4**;
- total: **79/79**;
- Host Release: **0 warnings, 0 errores**.

El backend Windows todavía rechaza `ioMode=terminal` como `unsupported`, deliberadamente hasta C2.2.

### C2.2 - Backend ConPTY

- wrapper `WindowsPseudoConsole`;
- pipes síncronos;
- launcher con `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`;
- `WindowsTerminalProcessIo`;
- read/write;
- VT/raw/Unicode tests;
- detección de consola.

### C2.3 - Resize y lifecycle

- resize;
- Ctrl+C smoke;
- root-exit policy;
- teardown 24H2+ con ReleasePseudoConsole;
- fallback pre-24H2;
- races/cleanup tests.

### C2.4 - Validación final

- suite completa;
- Release/Debug;
- runtime actualizado;
- smoke MCP público;
- Secure MCP Tunnel;
- fresh-agent;
- documentación de cierre.

## Estado de decisión

Diseño aprobado. C2.1 quedó implementado y validado.

No hay una investigación arquitectónica pendiente conocida antes de C2.2. Los puntos de riesgo del backend ConPTY están identificados y tienen estrategia de prueba explícita.
