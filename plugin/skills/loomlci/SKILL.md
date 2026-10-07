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
4. Para inspección visual local usa las tools visuales del filesystem: imágenes conocidas directamente y PDFs mediante extracción textual o render de página según corresponda. No sustituyas visión directa por OCR/base64 manual.
5. Usa Python cuando convenga cálculo, parsing, transformación o estado Python persistente dentro de la WorkSession. Si hacen falta paquetes de terceros, prepara el set completo con `python_packages_prepare`; si devuelve `workerRestartRequired=true`, ejecuta `python_reset` antes del siguiente `python_execute`. Usa Filesystem para operaciones estructuradas de archivos y Process para ejecutables/subprocesos.
6. Para comandos cortos no interactivos que deban terminar en la misma llamada, prefiere `process_run`. Usa `process_start` cuando necesites un proceso durable, background, interacción posterior o semántica de terminal.
7. En procesos durables conserva el `processHandle`. Usa `process_read` cuando importe output y `process_status` cuando sólo importe estado/exit metadata. Usa terminal/resize/input sólo cuando el programa realmente requiera consola interactiva.
8. Un exit code distinto de cero es resultado del proceso, no necesariamente error de LoomLCI. Usa `process_terminate` para detener un proceso vivo y libera recursos retenidos según el lifecycle que indique el schema.
9. Cierra la WorkSession al terminar para limpiar recursos session-owned. Los recursos independientes sobreviven al cierre y requieren lifecycle explícito.
10. No inventes capabilities. Si algo no está expuesto en el schema vivo, dilo claramente.

## Directorio de trabajo

No uses el directorio del plugin como directorio de los procesos del usuario. Define `baseDirectory` en `work_create` según la tarea. Si el usuario no dio una ruta, usa sólo ubicaciones de proyecto que estén disponibles explícitamente en el contexto; no inventes ni codifiques una ruta fija de desarrollo.
