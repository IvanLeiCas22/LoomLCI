# Python 3 - Outputs binarios e imágenes

> Estado: **P3.0 CERRADO; P3.1 (MCP image transport + payload hardening) pendiente.** Investigación/diseño cerrados y typed output foundation/protocol v3 implementados y validados.

## Objetivo

Permitir que código ejecutado dentro de `python_execute` produzca contenido visual/binario que llegue al modelo sin salir del worker Python, sin MCP recursivo y reutilizando los límites/semántica ya validados por Visual Files.

El primer tipo model-visible recomendado es **imagen**. El transporte interno debe quedar preparado para otros outputs binarios, pero no conviene prometer blobs/audio genéricos hasta validar su comportamiento real en ChatGPT.

## Estado actual relevante

### Python

- catálogo MCP público: **25 tools**;
- `python_execute` reutiliza un worker persistente por WorkSession;
- protocolo Worker/Host: **v2**, length-prefixed JSON UTF-8 sobre Named Pipe;
- request frame: **2 MiB**;
- final response frame: **32 MiB**;
- bridge call: **2 MiB**;
- bridge result: **8 MiB**;
- resultado actual: status + stdout/stderr + truncation + exception;
- `loom.__bridge_version__ == 1`;
- assets `worker.py` / `loom_bridge.py` son content-addressed, por lo que un Host nuevo materializa automáticamente scripts nuevos sin modificar CPython 3.14.8.

### Visual Files

Camino ya validado end-to-end:

```text
bytes válidos
  -> ImageContentBlock.FromBytes(...)
  -> CallToolResult mixto
  -> Secure MCP Tunnel
  -> ChatGPT/model vision
```

Límites actuales:

- imagen PNG/JPEG/WebP: **6 MiB** raw;
- `CallToolResult` serializado visual: **9 MiB**;
- tunnel observado: hard limit **10 MiB**;
- el cálculo autoritativo considera base64 + escaping JSON real;
- las tools visuales que devuelven imagen **omiten outputSchema** porque ChatGPT descartaba el `ImageContentBlock` adicional cuando el resultado mixto era tratado como output tipado.

## Hallazgos

### 1. No usar el bridge RPC para transportar la imagen

No conviene implementar algo como:

```python
loom._bridge_call("output.image", {"data": ...})
```

El bridge call está limitado a **2 MiB**. Incluso base64 de una imagen de ~1,5 MiB ya rozaría ese límite, muy por debajo de los 6 MiB permitidos por Visual Files.

Además sería un round-trip innecesario durante la ejecución.

### 2. No usar archivos temporales como transporte principal

Es técnicamente viable hacer Python -> temp file -> Host -> ImageContentBlock, pero agrega:

- I/O a disco;
- cleanup en crash/cancel;
- ownership/ACL/stale files;
- más estados de lifecycle;
- semántica distinta para bytes en memoria vs paths.

Puede seguir siendo un fallback futuro, pero no es la opción recomendada para P3.

### 3. El resultado final de python_execute es el punto correcto

Recomendación:

```python
loom.display_image(data)
```

debe ser una API **local al worker**, no un bridge method.

Durante la ejecución acumula outputs en estado execution-local. Al terminar, `worker.py` incorpora esos outputs al frame final junto a stdout/stderr/exception. El Host decodifica, valida y `PythonTools` los convierte a `ImageContentBlock`.

Ventajas:

- no MCP recursivo;
- no nuevos recursos/handles;
- no archivos temporales;
- mantiene outputs ligados a un único execute;
- una excepción Python normal puede conservar imágenes producidas antes de la excepción, igual que stdout/stderr;
- timeout/cancel/crash siguen descartando el resultado completo y el worker según la semántica actual.

### 4. JSON/base64 interno es suficiente

Con el cap raw actual:

```text
6 MiB = 6.291.456 bytes
base64 = 8.388.608 bytes
```

Python `json.dumps(..., ensure_ascii=True)` no escapa `+`, por lo que una imagen de 6 MiB sigue ocupando ~8 MiB dentro del pipe. No hace falta introducir framing binario/raw en P3.

Raw sidecar frames ahorrarían copias, pero aumentarían mucho la complejidad de framing, interleaving, cancelación y tests para un payload ya acotado.

### 5. Hay que endurecer el frame final combinado

El frame actual es 32 MiB. Con:

- stdout máximo 1.048.576 code points;
- stderr máximo 1.048.576;
- escaping Unicode patológico;
- hasta 6 MiB de imagen;

el frame final puede superar 32 MiB aunque cada componente individual sea válido.

P3 debe resolverlo explícitamente. Recomendación:

- bump del protocolo Worker/Host **v2 -> v3**;
- elevar de forma acotada el final response frame a **40 MiB** (o valor equivalente calculado con margen);
- mantener guards específicos de cantidad/tamaño de outputs;
- el límite externo autoritativo sigue siendo el **CallToolResult MCP de 9 MiB**, no el frame local.

No conviene depender de que `_write_frame` mate el worker si el resultado combinado resulta grande.

### 6. python_execute debe convertirse en resultado MCP mixto

Actualmente `python_execute` anuncia `outputSchema`.

Para que las imágenes lleguen a visión, debe alinearse con `filesystem_view_image` / `filesystem_render_pdf_page`:

- mantener `StructuredContent` manual;
- omitir `OutputSchemaType` / outputSchema en `python_execute`;
- devolver:
  - TextContentBlock breve;
  - StructuredContent con metadata normal;
  - uno o más `ImageContentBlock` en éxito.

Esto cambia metadata MCP pero **no agrega una tool pública**: el catálogo sigue en 25.

### 7. Reutilizar validación Visual Files

No confiar en bytes/mime declarados por Python.

Conviene extraer el detector/validador PNG/JPEG/WebP hoy privado en `WindowsVisualFilesProvider` a un helper reutilizable de Core/VisualFiles.

Así:

- `filesystem_view_image` mantiene la misma validación;
- Python image outputs usan exactamente la misma detección;
- el MIME real lo determina LoomLCI a partir de bytes.

### 8. Límite por ejecución

Propuesta inicial:

- máximo **4 imágenes** por `python_execute`;
- máximo individual: **6 MiB**;
- máximo raw agregado: **6 MiB**;
- máximo MCP serializado total del `CallToolResult`: **9 MiB**.

El cap agregado permite varias plots pequeñas sin multiplicar el riesgo de transporte.

Si el resultado mixto supera 9 MiB por stdout/stderr + imágenes + escaping, devolver un error local estructurado, por ejemplo:

```text
code = unsupported
reason = python_result_too_large / visual_payload_too_large
```

No truncar ni descartar imágenes silenciosamente.

### 9. API Python recomendada

Inicialmente sólo una primitive sin dependencias externas:

```python
loom.display_image(data)
```

Acepta:

- `bytes`;
- `bytearray`;
- `memoryview`.

No aceptar objetos PIL/matplotlib mágicamente en la primera versión. El usuario/agente puede usar `io.BytesIO`:

```python
buf = io.BytesIO()
fig.savefig(buf, format="png")
loom.display_image(buf.getvalue())
```

El Host detecta PNG/JPEG/WebP.

No recomiendo path como input inicial: un archivo existente ya tiene el camino probado `filesystem_view_image`; P3 debe concentrarse en imágenes **en memoria generadas por Python**.

### 10. Semántica de ejecución/threading

El output collector debe reutilizar la disciplina ya probada del bridge:

- ligado al `requestId` activo;
- thread-safe;
- child threads con contexto heredado pueden emitir antes de finalizar el execute;
- threads tardíos/stale después de `_end_execution` reciben error;
- outputs son execution-local, no persisten al execute siguiente;
- globals/imports sí continúan persistiendo como hoy.

Una excepción Python normal conserva outputs generados antes de la excepción. Infra failure/cancel/timeout no devuelve outputs parciales.

### 11. Versionado

Recomendado:

- Worker/Host protocol: **v3**;
- `loom.__bridge_version__`: **2**, porque cambia la API Python pública aunque el RPC bridge siga conceptualmente igual;
- CPython: **sin cambio**;
- package-store schema: **sin cambio**;
- MCP public tool count: **25**.

Los assets content-addressed evitan stale worker/bridge después del deployment.

## Binarios genéricos

El SDK MCP 2.2.0 también contiene:

- `AudioContentBlock`;
- `EmbeddedResourceBlock`;
- `BlobResourceContents`.

Pero el propio SDK deja el render de embedded resources a criterio del cliente y LoomLCI **no tiene validación ChatGPT/tunnel end-to-end** para esos tipos.

Por eso P3 no debería exponer todavía `display_bytes` genérico ni audio como contrato estable.

La estructura interna de outputs puede quedar extensible por `kind`, pero el único kind público de P3 inicial debe ser `image`.

Después de cerrar imágenes, un spike independiente puede probar BlobResource/Audio por el tunnel antes de ampliar la API.

## Etapas propuestas

### P3.0 — Typed output foundation / protocol v3 — CERRADO

Implementado:

- contratos Core `PythonExecutionOutput` / `PythonExecutionOutputKind.Image`;
- límites privados: 4 outputs, 6 MiB por imagen y 6 MiB raw agregado;
- protocol Worker/Host **v3**;
- final response frame elevado de 32 MiB a **40 MiB** con regresión de presupuesto combinado;
- `loom.__bridge_version__ = 2`;
- collector execution-local ligado al request activo;
- `loom.display_image(bytes|bytearray|memoryview)` local al worker, sin RPC del bridge;
- outputs codificados como base64 sólo en el frame final JSON;
- parsing Host defensivo para count, kind, base64, tamaño individual y agregado;
- normal exception conserva outputs previos;
- outputs no persisten entre executes, pero globals/imports sí;
- stale child threads reciben `output_unavailable`;
- límites excedidos son recuperables y no invalidan el worker;
- `loom.capabilities()` permanece en **15** porque `display_image` no es capability RPC;
- **ningún cambio MCP visible** todavía: `python_execute` continúa con su contrato actual hasta P3.1.

Validación:

- Core Python focalizado: **28/28**;
- Windows Python/protocol focalizado: **51/51**;
- IntegrationTests: **20/20**;
- suite Release serial completa: **350/350**;
- contrato MCP exportado desde Host Release: **25 tools**, byte a byte idéntico al snapshot canónico;
- SHA-256 del contrato: `674e3edc6f209dbf15074b08ace8484cb7c67c122aa343d73a8df842d86e6064`.

No se desplegó el runtime instalado ni se actualizó el plugin privado en P3.0. Esos pasos permanecen para P3.2 después de cerrar el transporte MCP visual.

### P3.1 — MCP image transport + payload hardening

- detector visual compartido Core;
- `PythonExecutionResult` con outputs validados;
- `python_execute` mixed `CallToolResult`;
- remover outputSchema de `python_execute`;
- generalizar `McpVisualPayloadLimits` para múltiples imágenes;
- 9 MiB exact combined budget;
- hardening adicional del resultado text-only de Python para no depender del hard limit del tunnel;
- metadata/server instructions actualizadas.

### P3.2 — Evaluation / deployment / plugin

Tests:

- Core: validación/tamaños/aggregate;
- Windows protocol: v3, base64, count, malformed/oversize, combined-frame edge;
- runtime: display_image, multiple images, normal exception + image, stale child thread, reset;
- MCP Integration: `ImageContentBlock` round-trip, `OutputSchema == null`, metadata estructurada preservada, oversize recoverable y worker sano;
- suite Release serial;
- portable/deployment sobre runtime instalado;
- consumer smoke Secure MCP Tunnel;
- fresh-agent visual real.

Consumer smoke mínimo:

1. WorkSession;
2. `python_execute` genera un PNG pequeño en memoria;
3. `loom.display_image(...)`;
4. ChatGPT recibe la imagen directamente en ese mismo tool result y la describe;
5. segundo execute prueba persistencia del worker;
6. caso negativo: imagen local existente sigue eligiendo `filesystem_view_image`.

Luego reconciliar plugin/skill, probablemente como **0.5.0**, porque aparece una capability/workflow Python nueva.

## Criterio de cierre Python 3

P3 queda cerrado cuando:

- no hay nueva tool MCP pública;
- protocol v3 + API loom v2 son robustos;
- `loom.display_image` entrega PNG/JPEG/WebP in-memory al modelo;
- 4 outputs / 6 MiB agregado / 9 MiB MCP se respetan sin depender del tunnel;
- `python_execute` mixed content llega a ChatGPT con StructuredContent + ImageContentBlock;
- normal exception conserva imágenes previas y worker sano;
- timeout/cancel/crash no filtran outputs parciales;
- fresh-agent elige `loom.display_image` para imagen generada en Python y `filesystem_view_image` para imagen local existente;
- runtime instalado + plugin reconciliado pasan smoke/read-back;
- repo/documentación quedan limpios y commiteados.

## Decisión recomendada

Implementar P3 como **image-output first**, usando el resultado final de `python_execute` y no el bridge RPC.

No introducir todavía:

- framing binario raw;
- temp-file transport;
- nueva tool MCP;
- objetos PIL/matplotlib mágicos;
- generic blobs/audio públicos.

Ese scope aprovecha todo lo ya validado por Visual Files y mantiene la complejidad acotada.
