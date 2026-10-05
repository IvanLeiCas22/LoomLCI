# Plan y tareas

> Estado: **Agent Support F1 y Deployment portable cerrados**. El deployment está validado end-to-end en dos PCs Windows x64. [[Bloque G - Visual Files]] pasa a ser el próximo bloque: lectura nativa de imágenes y PDFs antes de Computer. La investigación de [[Bloque H - Computer]] permanece cerrada, pero su implementación queda después de G1.

## Distinciones necesarias

Hay tres conceptos diferentes que no conviene mezclar:

1. Plan de ejecución: checklist corta del trabajo actual del agente.
2. Tareas duraderas: backlog o TODOs que sobreviven a una sesión.
3. MCP Tasks: extensión del protocolo para llamadas de herramientas asíncronas/largas. No es un plan del agente.

## Qué hacen otros agentes

Codex tiene `update_plan`: recibe una lista de pasos con estados pending / in_progress / completed y permite como máximo un paso in_progress. El propio código de Codex aclara que es una checklist de progreso y es distinta de Plan Mode.

En Codex 0.152.0 `update_plan` pasó a ser opt-in/default-off. Un motivo práctico documentado por usuarios e integraciones es evitar dos superficies de planificación cuando el host o un MCP externo ya proporciona una. Esto sugiere que el plan debe ser opcional, no una capability universal obligatoria.

Claude Code tiene un sistema de tareas habilitado por defecto en modo interactivo y permite compartir una lista entre sesiones mediante `CLAUDE_CODE_TASK_LIST_ID`. Claude Desktop además expone paneles separados de plan y tasks. Esto confirma que el estado estructurado externo al modelo sí es útil en trabajo agentic largo.

MCP 2026-07-28 tiene una extensión Tasks, pero sirve para representar el lifecycle durable de una llamada asíncrona (`tasks/get`, `tasks/update`, `tasks/cancel`). No debe reutilizarse para la checklist cognitiva del agente.

## Conclusión propuesta

Agregar una capa opcional `Agent Support`, separada del Core de ejecución.

LoomLCI
- Core / execution: Filesystem, Process, Computer
- Agent Support: Work Plan (opcional)

Loom no decide el plan; solamente almacena, valida y devuelve el estado que el modelo escribe.

### Concurrencia: revisión de la propuesta

La restricción de Codex de un único `in_progress` no debe copiarse automáticamente en Loom. Codex usa `update_plan` principalmente como indicador secuencial de foco/progreso; eso no significa que el runtime no pueda tener procesos o trabajos concurrentes.

OpenAI GPT-6 soporta Async Tool Calling: el modelo puede iniciar una tool lenta y continuar razonando o llamando otras tools mientras la aplicación ejecuta la primera. También existen parallel function calls y Multi-agent para workstreams independientes. Codex `exec_command` puede devolver un session ID cuando un proceso sigue vivo, permitiendo al agente continuar con otro trabajo y volver luego al proceso. Anthropic documenta background tasks que mantienen procesos largos activos sin bloquear a Claude Code.

Esto valida el caso: iniciar una compilación larga, trabajar en otra cosa y volver después al resultado.

### Alcance inicial revisado propuesto

Work Plan efímero para la tarea actual:
- pasos cortos con identidad estable
- múltiples pasos pueden estar activos simultáneamente
- estados propuestos: `pending`, `active`, `waiting`, `completed`
- `waiting` significa que el paso está en curso pero espera un proceso, resultado externo, usuario u otra condición; el agente puede avanzar con trabajo independiente
- actualización atómica con `revision`
- no acoplar automáticamente un paso a ProcessHandle/Invocation en v0.1

Ejemplo:

    [waiting] Compilar proyecto        -> proc_123 sigue ejecutándose
    [active]  Actualizar documentación
    [pending] Revisar resultado build

El Resource Registry sigue siendo la fuente de verdad de procesos/jobs. El Work Plan sólo representa el estado lógico que el modelo quiere mantener.

No construir todavía un scheduler, DAG de dependencias, gestor persistente de proyectos ni multi-agent coordinator. Dependencias explícitas pueden añadirse después si los evals muestran que son necesarias.

### Exposición

Debe poder activarse/desactivarse por adapter/perfil:
- ChatGPT base: probablemente activado, porque aporta una capacidad que el host no ofrece de forma equivalente.
- Codex / Claude Code u otros harnesses con planificación propia: desactivado para no duplicar herramientas.

La implementación F1.1 mantiene el Work Plan directamente dentro de WorkSession y no usa Resource Registry. El formato exacto de la tool y la política de exposición se cierran en F1.2.

## Cierre F1.3

El holdout real-world confirmó que la política de selección es suficientemente discriminante:

- workflows con creación/diagnóstico/corrección/validación: 4/4 usaron Work Plan de forma útil;
- lookups simples: 3/3 omitieron Work Plan;
- una misma WorkSession escaló correctamente de lookup simple sin plan a workflow multi-fase con plan.

No continuar optimizando prompting ni contrato de Work Plan sin evidencia nueva.

## Deployment portable

[[Deployment portable]] quedó implementado y validado en la PC principal:

- Launcher `start` / `stop` / `status` / `setup`;
- Host Release self-contained `win-x64`;
- tunnel-client v0.0.14 fijado y verificado;
- profile/state/secrets aislados por máquina;
- acceso directo de escritorio;
- cutover y rollback reales;
- smoke end-to-end desde ChatGPT.

Validación multi-PC cerrada: el mismo paquete se instaló desde cero en una notebook Windows x64, ChatGPT operó correctamente sobre ella y luego se volvió a la PC de escritorio deteniendo/iniciando el launcher correspondiente. Quedan como UX futura la confirmación visible al finalizar y un acceso directo de stop; no bloquean el roadmap.

## Próximo bloque de capability - Visual Files G1

Investigación y diseño v0.1 cerrados en [[Bloque G - Visual Files]]. **G1.0 y G1.1 ya están implementados; G1.1 sólo espera refresh del catálogo cliente para su smoke visual directo.** El próximo bloque de implementación es G1.2 PDF text worker.

Superficie v0.1 fijada:

1. `filesystem_view_image`: PNG/JPEG/WebP -> `ImageContentBlock`.
2. `filesystem_read_pdf`: extracción textual paginada con PdfPig.
3. `filesystem_render_pdf_page`: render de una página PDF -> PNG mediante Windows.Data.Pdf.

Implementación incremental propuesta:

1. **G1.0 Binary/image foundation — CERRADO**: Windows TFM, helper MCP mixed structured + image, guard 6/9 MiB, 4 MCP tests, harness Release y portable validados; suite total 207/207.
2. **G1.1 Local image — IMPLEMENTADO**: `filesystem_view_image`, provider/capability separados, resolver compartido, PNG/JPEG/WebP, `FileShare.Read`, 223/223 tests y runtime portable final healthy/ready. Pendiente sólo refresh de acciones + smoke visual directo.
3. **G1.2 PDF text worker**: `LoomLCI.PdfWorker` one-shot, PdfPig 0.1.16, Job Object/timeout, rangos/límites y `filesystem_read_pdf`.
4. **G1.3 PDF render**: Windows.Data.Pdf y `filesystem_render_pdf_page`.
5. **G1.4 Evaluation + portable**: corpus real, malformed/oversized, tunnel/ChatGPT, fresh-agent y deployment.

Decisiones cerradas: tunnel 10 MiB real, imágenes/PNG <=6 MiB + payload MCP visual <=9 MiB, PDF <=64 MiB, texto PDF <=65.536 code points por página y <=262.144 agregados, PdfPig 0.1.16 aislado en worker privado con 256 MiB/20 s, y PDFs protegidos reportados como `unsupported`. La investigación específica de G1.0 también cerró TFM, helper MCP, tests MCP, harness Release y publish portable.

## Bloque siguiente - Computer H1

Investigación cerrada en [[Bloque H - Computer]]. Computer se implementará después de G1 y reutilizará la base visual/WinRT que G deje validada.

Etapas actualmente diseñadas:

1. **H1.0 Computer Windows foundation**: interop específico de Computer y reutilización del helper MCP de imagen ya validado por G1.
2. **H1.1 Observation + topology**: lifecycle, stale semantics, monitores/ventanas y `computer_observe`.
3. **H1.2 Capture**: WGC window/monitor, desktop stitch, PNG y `computer_capture`.
4. **H1.3 Native input**: DesktopInputGate, activate, SendInput y `computer_input`.
5. **H1.4 UI Automation**: MTA dispatcher, bounded inspect y semantic actions.
6. **H1.5 Evaluation + deployment**: tunnel, real-world, fresh-agent y actualización portable.

No agregar PyAutoGUI bridge, UIAccess, OCR, video, HDR ni policy engine general dentro de H1.

## Fuentes

- Codex update_plan spec: https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/plan_spec.rs
- Codex plan handler: https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/plan.rs
- Codex Plan Mode vs update_plan: https://github.com/openai/codex/blob/main/codex-rs/collaboration-mode-templates/templates/plan.md
- MCP Tasks extension: https://tasks.extensions.modelcontextprotocol.io/specification/draft/tasks
- Claude Code environment variables / task lists: https://code.claude.com/docs/en/env-vars
