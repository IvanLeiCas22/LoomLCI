# Bloque C1 - Native Process y Job Objects

## Estado

Investigación y análisis completados. **Sin cambios funcionales aplicados.** Listo para implementación cuando sea aprobada.

## Objetivo

Reforzar la implementación Windows de `Process` sin cambiar la interfaz pública actual:
- ownership nativo del árbol de procesos;
- cleanup fiable de descendientes;
- base reutilizable para ConPTY;
- mantener intactas las semánticas actuales de `process_start/read/write/status/terminate`.

## Estado actual

`WindowsProcessProvider` usa `System.Diagnostics.Process` con:
- `UseShellExecute=false`;
- stdin/stdout/stderr redirigidos;
- `CreateNoWindow=true`;
- UTF-8 para stdout/stderr;
- `Process.Kill(entireProcessTree:true)` para terminate/cleanup.

`ProcessIoMode` sólo contiene `Pipes`.

## Decisiones cerradas

### Job por ProcessHandle

Crear un Job Object por proceso Loom, no por WorkSession.

Relación:
`ProcessHandle -> Job Object -> root + descendants`.

Razones:
- `process_terminate` afecta sólo ese árbol;
- procesos independientes de la misma WorkSession quedan aislados;
- `work_close` sigue limpiando cada recurso session-owned mediante ResourceRegistry.

### Asignación atómica

Usar `PROC_THREAD_ATTRIBUTE_JOB_LIST` durante `CreateProcessW`.

No hace falta `CREATE_SUSPENDED` sólo para evitar una carrera de asignación: Windows 10+ permite que el proceso nazca ya asociado al Job.

### Job limits

Configurar solamente:
- `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.

No agregar todavía límites de CPU, memoria, UI ni breakaway.

Descendientes creados normalmente permanecen en el Job. No habilitar `BREAKAWAY_OK` ni `SILENT_BREAKAWAY_OK`.

### Explicit terminate

`process_terminate` debe usar `TerminateJobObject`.

Importante: aunque el root ya haya salido naturalmente (`state=exited`), todavía puede haber descendientes vivos. En ese caso:
- terminar el Job;
- conservar el estado/exit code natural del root;
- no tratar el terminate como no-op sólo porque el root ya terminó.

### Runtime/túnel

El `LoomLCI.Host` real fue inspeccionado y ya corre dentro de un Job Object externo.

Se hizo una prueba live desde Loom:
- proceso dentro del job externo;
- crea un Job Object adicional;
- asigna un child a ese Job;
- resultado: `assign=True; err=0; exit=0`.

Conclusión: jobs anidados funcionan en el entorno real actual y deben ser parte de los tests de C1.

## Launcher nativo

Mantener Core/MCP sin cambios y reemplazar sólo el backend Windows de launch.

Piezas propuestas:

### WindowsNativeProcessLauncher

Responsable de:
- construir command line;
- environment;
- pipes;
- STARTUPINFOEX;
- attribute list;
- `CreateProcessW`;
- transferir ownership de handles a `WindowsProcessResource`;
- cleanup total ante errores parciales.

### WindowsJobObject

Responsable de:
- `CreateJobObject`;
- `SetInformationJobObject`;
- `TerminateJobObject`;
- cierre seguro del Job handle.

### WindowsProcessResource

Mantiene sus responsabilidades actuales:
- root process state;
- stdin;
- stdout/stderr pumps;
- ProcessOutputStore;
- cursors;
- exit metadata;
- lifecycle.

Dejará de depender del objeto `System.Diagnostics.Process` para launch/lifecycle básico y conservará un process handle nativo seguro.

## STARTUPINFOEX

Usar una única attribute list con:
1. `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`;
2. `PROC_THREAD_ATTRIBUTE_JOB_LIST`.

Esta misma infraestructura sirve luego para C2/ConPTY, agregando `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`.

## Handles / pipes

El child sólo debe heredar:
- stdin read;
- stdout write;
- stderr write.

Usar `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`; `bInheritHandles=TRUE` es requisito de Windows.

Los parent ends deben permanecer no heredables.

Los child ends deben ser heredables sólo durante la ventana mínima necesaria. Serializar el tramo crítico de launch de Loom para evitar fugas entre launches concurrentes.

Riesgo residual:
un tercero dentro del mismo Host que haga un `CreateProcess` concurrente con herencia indiscriminada podría teóricamente capturar un handle temporalmente heredable. .NET 10 no expone el lock interno de `System.Diagnostics.Process`. El Host actual no tiene otro launcher de este tipo; la ventana se minimizará y todos los launches Loom usarán allow-list.

## Pipes administrados vs nativos

Es viable reutilizar `AnonymousPipeServerStream`/SafePipeHandle para reducir interop.

En .NET 10 los anonymous pipes Windows no ofrecen overlapped async nativo; esto no empeora materialmente el diseño actual, que ya usa pipes clásicos con pumps independientes.

No es necesario introducir named pipes sólo para C1.

## Command line / arguments

Preservar exactamente el contrato público `executable + arguments[]`.

Windows recibe una sola command line. Para cada argumento usar la misma regla que `.NET ProcessStartInfo.ArgumentList` (`PasteArguments.AppendArgument`, MIT).

Para argv[0]/executable:
- construir un primer token citado de forma segura;
- pasar `lpApplicationName=NULL` para conservar la búsqueda de ejecutables de `CreateProcessW` (incluyendo `.exe` y PATH);
- no inferir shell.

Agregar golden tests de:
- vacío;
- espacios;
- comillas;
- backslashes antes de comillas/final;
- Unicode;
- ejecutable con path con espacios;
- executable por nombre/PATH y sin extensión.

## Environment

Semántica a preservar:
- sin overrides: `lpEnvironment=NULL` -> herencia directa del environment actual;
- con overrides: partir del environment actual, case-insensitive en Windows, aplicar set/remove y crear bloque Unicode ordenado;
- usar `CREATE_UNICODE_ENVIRONMENT`.

Los pseudo-vars de current directory de drives (`=C:`, etc.) deben conservarse cuando se construye un bloque explícito.

## Working directory

Mantener la resolución actual de Core.

Pasar el path resuelto como `lpCurrentDirectory`.

## Output/input encoding

Mantener UTF-8 explícito para stdout/stderr como hoy.

Los streams siguen alimentando `ProcessOutputStore`; no cambiar cursors ni retention.

## Exit observation

Conservar el process HANDLE devuelto por `CreateProcessW` para evitar carreras con procesos muy cortos.

Observar ese handle directamente y obtener exit code con Win32; no reconstruir el proceso sólo por PID.

Cerrar el thread HANDLE inmediatamente después del launch.

## Disposal ordering

En cleanup/session close:
1. cerrar/terminar Job para detener root + descendientes;
2. permitir que pipes lleguen a EOF / terminar pumps;
3. liberar process/pipe/job handles;
4. eliminar spools como hoy.

En `process_terminate` no se destruye el recurso: output retenido sigue siendo legible hasta cierre del recurso, igual que en Bloque A.

## .NET 11

La rama actual de .NET ya incorpora APIs como `KillOnParentExit` / inherited handles y usa Job Objects.

Pero el runtime real de Loom es .NET 10.0.11 y esas APIs **no existen** en su reference assembly.

No se recomienda abandonar .NET 10 LTS por esto. Además, Loom necesita controlar explícitamente `TerminateJobObject` y su lifecycle.

## Interop

Usar `Microsoft.Windows.CsWin32` como source generator, `PrivateAssets=all`, con `NativeMethods.txt` limitado a las APIs necesarias.

Esto ya coincide con la arquitectura del proyecto y será reutilizable por ConPTY/Computer.

## APIs Win32 previstas

- CreateJobObject
- SetInformationJobObject
- TerminateJobObject
- CreateProcessW
- InitializeProcThreadAttributeList
- UpdateProcThreadAttribute
- DeleteProcThreadAttributeList
- GetExitCodeProcess
- CloseHandle / safe handles
- funciones de pipe/handle sólo si las abstracciones administradas no alcanzan durante implementación

## Tests obligatorios antes de cerrar C1

### Regresión

Toda la suite actual debe seguir verde.

### Launch parity

- executable lookup por PATH;
- extensión implícita;
- full path con espacios;
- argumentos vacíos/espacios/quotes/backslashes/Unicode;
- working directory;
- env add/replace/remove;
- stdin/stdout/stderr;
- procesos muy cortos;
- launches concurrentes.

### Job semantics

- root -> child -> grandchild;
- `process_terminate` mata todo el árbol;
- `work_close` mata todo el árbol session-owned;
- independent sobrevive a `work_close`;
- root sale pero child queda vivo: `process_terminate` mata child y conserva root como `exited`;
- repeated terminate idempotente;
- host dentro de job externo / nested job;
- cancellation/failure no deja procesos ni handles huérfanos.

### Output

- output del root conserva los tests de Bloque A;
- descendants que heredan stdout/stderr no rompen lifecycle;
- output sigue legible después de terminate hasta close.

### Live

- smoke directo;
- smoke mediante Secure MCP Tunnel;
- fresh-agent regression de process tools.

## Fuera de C1

- ConPTY / terminal stream;
- graceful Ctrl+C;
- CPU/memory limits;
- completion ports/job telemetry;
- TTL automático de ProcessHandle;
- cambios al contrato MCP público.

## Conclusión

No quedan decisiones arquitectónicas bloqueantes identificadas para C1.

El próximo paso, si se aprueba, es implementar primero el launcher/job backend conservando el contrato público actual y validar parity antes de iniciar C2/ConPTY.
