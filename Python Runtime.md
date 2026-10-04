# Python Runtime

> Estado: **arquitectura confirmada; E1.1–E1.4 implementados y validados; E1.5 (smoke por Secure MCP Tunnel + fresh-agent) pendiente**. Ver [[Bloque E - Python Runtime]].

## Decisión

LoomLCI incorporará un Python Runtime general y persistente como capability de ejecución de primera clase.

No es parte de Computer y no es un DSL. PyAutoGUI será, como máximo, una librería futura sobre esta base.

## Forma confirmada

- worker Python separado del proceso C#;
- exactamente un worker lazy por WorkSession;
- sin PythonHandle público en v0.1;
- globals/imports/funciones persistentes;
- una ejecución activa por worker;
- segunda ejecución concurrente devuelve `busy`;
- excepciones Python ordinarias no destruyen el worker;
- timeout/cancelación/crash/protocolo roto descartan el worker;
- siguiente ejecución recrea automáticamente;
- `python_reset(workId)` permite reset explícito;
- cleanup automático por `work_close` / expiry;
- Full Trust con permisos normales del usuario, no sandbox.

## Runtime

No depender de `python` del PATH ni de instalaciones del usuario.

Runtime inicial fijado:

- CPython 3.14.8 x64;
- distribución oficial embeddable de Windows;
- runtime privado/versionado por LoomLCI;
- stdlib solamente en E1;
- sin pip dinámico.

Los paquetes futuros se vendorizarán/versionarán como parte de la aplicación.

## IPC

Named Pipe duplex privado entre C# y Python:

- asynchronous + byte mode;
- una única instancia con `FirstPipeInstance`;
- DACL protegida: allow al SID del usuario actual + deny explícito a `NetworkSid`;
- handle no heredable;
- framing length-prefixed + JSON UTF-8;
- handshake/versionado;
- request IDs y límites acotados.

No usar stdout como canal de protocolo.

La comunicación Named Pipe con CPython estándar fue validada experimentalmente en la máquina de desarrollo.

## API pública E1

    python_execute(workId, code, timeoutSeconds=60, maxOutputChars=65536)
    python_reset(workId)

`python_execute` devuelve structuredContent con:

- status `completed|exception`;
- stdout;
- stderr;
- flags de truncamiento;
- excepción estructurada cuando corresponda.

E1.4 fija además: código ≤256 KiB UTF-8 estricto; `maxOutputChars` cuenta Unicode code points por stream; una excepción Python ordinaria mantiene `ok=true`/`status=exception`, mientras timeout/crash/fallos de Loom son tool errors.

## Relación con Process

Python sigue siendo una capability separada, pero el backend Windows reutiliza `IProcessProvider` y por lo tanto Native Process + Job Objects.

El worker se registra como resource kind interno `python_worker`, session-owned. No se expone como ProcessHandle.

## Primer alcance

E1 valida únicamente:

- runtime privado;
- lifecycle;
- namespace persistente;
- IPC;
- stdout/stderr;
- excepciones;
- busy;
- timeout/cancelación;
- reset;
- cleanup;
- MCP.

Diferido:

- PyAutoGUI;
- Computer;
- imágenes;
- paquetes científicos;
- bridge `loom.*`.

El detalle implementable y los tests de aceptación están en [[Bloque E - Python Runtime]].

## Motivos principales

Un runtime persistente permite:

- cálculos y transformaciones locales;
- parsing/generación;
- loops/condiciones sin round trips;
- estado en memoria entre llamadas;
- futura composición con Computer.

OpenAI utiliza públicamente el mismo patrón general de code execution persistente para Computer Use, aunque sus runtimes de producto no deben asumirse idénticos al sample.

## Fuentes

- https://docs.python.org/3/using/windows.html
- https://www.python.org/ftp/python/3.14.8/
- https://github.com/openai/openai-cua-sample-app
- https://github.com/openai/openai-cua-sample-app/blob/main/python-app/README.md
- https://learn.microsoft.com/windows/win32/ipc/named-pipe-security-and-access-rights
- https://github.com/modelcontextprotocol/csharp-sdk/issues/1835
