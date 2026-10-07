---
name: loomlci
description: Usa LoomLCI cuando el usuario pida trabajar sobre su computadora local mediante el runtime LoomLCI.
---

# LoomLCI

Usa las herramientas de la app MCP de LoomLCI como interfaz local preferida cuando el usuario pida trabajar con LoomLCI.

## Fuente de verdad

Las descripciones y schemas vivos de las tools MCP son la fuente de verdad para capabilities, parámetros, límites y semántica. No asumas que esta skill enumera exhaustivamente todo lo disponible.

## Flujo general

1. Crea una WorkSession cuando la tarea necesite una base directory compartida o recursos session-owned. Reutiliza su `workId` mientras continúe el mismo trabajo.
2. Si la tarea es no trivial y requiere varias fases significativas, acciones dependientes o checkpoints, usa Work Plan en esa WorkSession. En una WorkSession recién creada, inicialízalo inmediatamente después de `work_create` con `work_plan_update(expectedRevision=0)`, antes del trabajo sustantivo. Mantén pocos pasos orientados a resultados y actualízalos sólo en hitos. Omite Work Plan para lookups simples o tareas cortas de un solo paso.
3. Para explorar o modificar archivos, prefiere las operaciones estructuradas de Filesystem frente a shell cuando exista una operación equivalente. Elige entre tree, búsqueda de paths, búsqueda de texto, lectura exacta y patches según la tarea.
4. Para contenido visual local ya existente, usa las tools visuales del filesystem: `filesystem_view_image` para PNG/JPEG/WebP y `filesystem_render_pdf_page` para una página PDF que necesite inspección visual. No cargues un archivo local a Python sólo para mostrarlo ni sustituyas visión directa por OCR/base64 manual.
5. Usa `python_execute` cuando convenga cálculo, parsing, transformación o estado Python persistente dentro de la WorkSession. Si hacen falta paquetes de terceros, prepara el set completo con `python_packages_prepare`; si devuelve `workerRestartRequired=true`, ejecuta `python_reset` antes del siguiente `python_execute`. Dentro del worker Python, cuando el mismo workflow necesite filesystem o procesos ligados a esa WorkSession, importa `loom.fs` y `loom.process` en vez de volver a entrar por MCP/tunnel: `loom.fs` cubre filesystem estructurado y PDF textual; `loom.process` cubre procesos Loom-managed y sus procesos durables son SessionOwned. Si Python genera una imagen PNG/JPEG/WebP completamente en memoria y el modelo debe verla en ese mismo `python_execute`, usa `loom.display_image(...)`. En ChatGPT Code Mode, si el resultado expone `content_items` y está disponible el helper visual nativo, pasa explícitamente cada item con `type === "image"` a `image(item)` antes de interpretar o describir la imagen; no reduzcas ese resultado sólo a `StructuredContent`, `outputs[]`, JSON o metadata. Si `content_items` o `image(...)` no están disponibles, no los inventes ni hagas fallback a base64 o archivos temporales: conserva el resultado MCP estándar y reporta la limitación si la imagen no llega al contexto visual. Para una imagen local que ya existe usa `filesystem_view_image`; para una página PDF existente usa `filesystem_render_pdf_page`. Si necesitas explícitamente un proceso `Independent`, usa Process top-level.
6. Fuera de un workflow Python, para comandos cortos no interactivos que deban terminar en la misma llamada, prefiere `process_run`. Usa `process_start` cuando necesites un proceso durable, background, interacción posterior o semántica de terminal.
7. En procesos durables conserva el `processHandle`. Usa `process_read` cuando importe output y `process_status` cuando sólo importe estado/exit metadata. Usa terminal/resize/input sólo cuando el programa realmente requiera consola interactiva.
8. Un exit code distinto de cero es resultado del proceso, no necesariamente error de LoomLCI. Usa `process_terminate` para detener un proceso vivo y libera recursos retenidos según el lifecycle que indique el schema.
9. Cierra la WorkSession al terminar para limpiar recursos session-owned. Los recursos independientes sobreviven al cierre y requieren lifecycle explícito.
10. No inventes capabilities. Si algo no está expuesto en el schema vivo, dilo claramente.

## Directorio de trabajo

No uses el directorio del plugin como directorio de los procesos del usuario. Define `baseDirectory` en `work_create` según la tarea. Si el usuario no dio una ruta, usa sólo ubicaciones de proyecto que estén disponibles explícitamente en el contexto; no inventes ni codifiques una ruta fija de desarrollo.
