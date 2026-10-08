# Python Runtime

> Estado vigente al 2026-10-08: **E1 y Python 1, 2 y 3 cerrados end-to-end**. Esta nota describe la base E1, inicialmente stdlib-only/textual; la evolución posterior está en [[Python 1 - Paquetes administrados]], [[Python 2 - Bridge privado loom]] y [[Python 3 - Outputs binarios e imágenes]]. El Host instalado es la Release 5.

## Decisión

LoomLCI incorporó un Python Runtime general y persistente como capability de ejecución de primera clase.

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

**Evolución posterior a E1:** Python 1 implementó paquetes administrados/versionados con `uv` privado y environments inmutables; no se depende de `pip` del usuario.

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

**Fuera del alcance original E1 (estado posterior aclarado):**

- PyAutoGUI y Computer: siguen diferidos, Computer H1 está diseñado pero pausado.
- Imágenes: **implementadas en Python 3** vía `loom.display_image`.
- Paquetes de terceros/científicos: **implementados en Python 1** vía `python_packages_prepare`.
- Bridge `loom.*`: **implementado en Python 2**, extendido para imágenes en Python 3.

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
