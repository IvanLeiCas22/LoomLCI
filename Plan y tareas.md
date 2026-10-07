# Plan y tareas

> Estado: **Agent Support F1, Deployment portable, Launcher UX, [[Instalador Windows]], [[Auto-update firmado]], [[Plugin metadata]], Visual Files G1, hardening de `filesystem_read_files`, `process_run`, [[Ergonomía - Work Plan patch]], Python 1, Python 2 y la reconciliación final del plugin 0.4.0 cerrados end-to-end**. Computer H1 queda deliberadamente pausado. [[Roadmap post-G1]] continúa por Python 3.

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

## Ergonomía Work Plan patch

El follow-up post-G1 quedó implementado en [[Ergonomía - Work Plan patch]]:

- `work_plan_patch` modifica sólo pasos afectados mediante `add` / `update` / `remove`;
- conserva `expectedRevision` CAS, IDs estables y atomicidad;
- no hace auto-merge ni reorder;
- `work_plan_update` queda para creación, reorder, full replacement y clear;
- suite Release 269/269 y runtime portable `0.1.0-dev-work-plan-patch` healthy/ready.

El smoke directo final desde un chat con catálogo MCP refrescado pasó correctamente, incluyendo CAS stale, atomicidad, preservación de IDs y `remove + update`. Este bloque queda cerrado; el roadmap continúa por UX de Launcher.

## Deployment portable

[[Deployment portable]] quedó implementado y validado en la PC principal:

- Launcher `start` / `stop` / `status` / `setup`;
- Host Release self-contained `win-x64`;
- tunnel-client v0.0.14 fijado y verificado;
- profile/state/secrets aislados por máquina;
- acceso directo de escritorio;
- cutover y rollback reales;
- smoke end-to-end desde ChatGPT.

Validación multi-PC cerrada: el mismo paquete se instaló desde cero en una notebook Windows x64, ChatGPT operó correctamente sobre ella y luego se volvió a la PC de escritorio deteniendo/iniciando el launcher correspondiente. El follow-up de Launcher UX quedó cerrado: los accesos directos usan pausa visible, existe `Detener LoomLCI`, la CLI automatizable conserva su comportamiento sin pausa y el README portable ya no depende del encoding implícito de Windows PowerShell 5.1.

## Instalador Windows - CERRADO

[[Instalador Windows]] quedó implementado y validado end-to-end:

- Inno Setup 7.1.0 fijado como toolchain;
- instalador EXE per-user, sin admin por defecto;
- portable como fuente de verdad del payload;
- `SetupService` sigue siendo dueño de tunnel, secret, profile, doctor y shortcuts;
- registro estándar en Aplicaciones instaladas;
- uninstall real con stop previo y limpieza de binarios/config/runtime Python privados;
- install/uninstall aislado completo con roots/AppId de prueba;
- suite Release **272/272**;
- instalación real final `0.1.0-dev-installer` healthy/ready;
- IntegrationTests contra la DLL instalada: **15/15**.

## Auto-update firmado - CERRADO

[[Auto-update firmado]] quedó implementado y validado end-to-end:

- GitHub Releases como canal estable público;
- manifest firmado ECDSA P-256 con public key embebida y private key fuera del repo;
- `sequence` monotónico / `highestSequence` como anti-rollback;
- `update check`, `update apply` y `rollback`;
- lock exclusivo + journal persistente + recuperación determinista tras crash/corte;
- rollback automático si falla el start/health de la nueva versión;
- limpieza de versiones antiguas conservando sólo `active + previous`;
- suite Release **283/283**;
- Release real `v0.1.0-dev-github-e2e` publicada como Latest;
- E2E real GitHub -> manifest firmado -> ZIP -> apply -> restart -> health: **OK**;
- runtime final `0.1.0-dev-github-e2e`, sequence 3, healthy/ready;
- `previous=0.1.0-dev-local-e2e`, sequence 2;
- journal ausente y sólo dos versiones instaladas.

## Plugin metadata - CERRADO end-to-end

[[Plugin metadata]] mantiene el mecanismo anti-drift cerrado y la reconciliación post-Python 0.4.0 quedó cerrada end-to-end:

- `plugin/` como fuente canónica de manifest, README y skill;
- export MCP real mediante `initialize` + `tools/list`, sin parsear C#;
- snapshot versionado de **25 tools**, idéntico al Host Python 2 instalado;
- build que falla ante drift no aceptado;
- verifier de tool refs, duplicación de manifests, rutas/wiring Debug y markers semánticos del workflow Python final;
- IntegrationTest específico para `python_execute` + packages + `loom.fs`/`loom.process` + visuales/Independent top-level;
- `.codex-plugin/plugin.json` generado desde el manifest canónico;
- `mcp.json` / `.mcp.json` vacíos para neutralizar el wiring STDIO histórico;
- plugin privado actualizado `0.3.0 -> 0.4.0` con CAS y read-back (`pluginrel_6ac5e253c70081918df6046afb0572fa`);
- package 0.4.0: 4.639 bytes, SHA-256 `304a984ab0adab29032b529374593e33e80c32d9e917e4b799cd905562ddd36c`;
- suite Release **338/338**, IntegrationTests **20/20**;
- evaluación fresh-agent final: **4/4 PASS** (bridge Python autónomo, filesystem simple top-level, visual top-level y proceso `Independent` top-level);
- reconciliación 0.4.0 **CERRADA end-to-end**.

## Python 1 - Paquetes administrados - CERRADO

[[Python 1 - Paquetes administrados]] quedó implementado e instalado:

- `uv 0.12.23` privado fijado por URL + SHA-256;
- PyPI oficial, wheels-only, locks exactos con hashes;
- environments inmutables/content-addressed y reutilizables;
- nueva tool `python_packages_prepare`;
- binding por WorkSession + `python_reset` explícito al cambiar environment;
- GC LRU/best-effort con 2 GiB para environments y 1 GiB para cache de `uv`, protegiendo WorkSessions/workers vivos;
- catálogo Host con Work Plan: **25 tools**;
- suite Release **298/298**;
- portable `0.1.0-dev-python1` generado y setup side-by-side OK;
- runtime instalado `0.1.0-dev-python1`, healthy/ready, rollback hacia `0.1.0-dev-github-e2e`;
- IntegrationTests contra la DLL instalada: **17/17**.

El smoke directo de `python_packages_prepare` desde ChatGPT quedó completado con catálogo MCP refrescado: environment NumPy 2.5.3 + Pandas 3.0.6 reutilizado correctamente y `python_execute` ejecutó imports/cálculo real. **Python 1 queda CERRADO end-to-end.**

## Python 2 - Bridge privado loom.*

[[Python 2 - Bridge privado loom]] tiene **P2.0 CERRADO**:

- protocolo Worker/Host v2 sobre el Named Pipe privado existente;
- `bridge_call` / `bridge_result` multiplexados antes del resultado final;
- handler ligado a cada `python_execute`/WorkSession;
- correlación `requestId`/`callId`, errores recuperables y protección frente a threads tardíos;
- módulo privado `loom`, bridge API v1 y `loom.capabilities()` foundation;
- package-store schema v2 con namespace `loom` reservado;
- suite Release serial **311/311**;
- smoke Release NumPy + Pandas + `import loom`: **P20_SMOKE_OK**.

**P2.1 queda CERRADO**: `loom.fs` expone `list_tree`, `find_paths`, `search_text`, `read_files`, `apply_patch`, `manage_directory` y `read_pdf` textual reutilizando Core.

**P2.2 queda CERRADO**: `loom.process` expone `run/start/status/read/write/resize/terminate/release`, con procesos creados desde Python siempre SessionOwned, ownership estricto por WorkSession, soporte pipes/ConPTY, cursors/retention y hardening general de `ProcessCapability.StartAsync` frente a `work_close` concurrente.

**P2.3 queda CERRADO**: callbacks del bridge ahora detectan la salida real del worker mientras están activos, cancelan y drenan su cleanup antes de retornar; Process separa `WaitForExitAsync` del drenaje de output; oversized result frames devuelven error estructurado `bridge_payload_too_large`; quedaron regression/stress tests para threads/repeated calls, worker crash durante callback, work_close/cancellation, run timeout cleanup, durable start + work_close y transición concurrente de package environment. Suite Release serial **337/337**, Integration **19/19**, catálogo MCP **25 tools** sin cambios y smoke Release NumPy 2.5.3 + Pandas 3.0.6 + `loom.fs` + `loom.process` + oversize recuperable: **P23_COMBINED_SMOKE_OK**.

**P2.4 queda CERRADO end-to-end; Python 2 queda CERRADO.** Portable `0.1.0-dev-python2` sequence 0 generado (ZIP 81.308.776 bytes, SHA-256 `049bea4dd71d7f5b62c55f3acf1f49ed66f80a6428672ec0460846520a2d0eb1`), Launcher **20/20**, Integration **19/19** contra el Host publicado y **19/19** contra la DLL instalada. Setup side-by-side dejó Python 1 como rollback; cutover `stop/start/status` vía IvanSpace quedó `healthy=true`, `ready=true`, mismo tunnel. Consumer smoke directo desde ChatGPT pasó con NumPy 2.5.3 + Pandas 3.0.6 + `loom.capabilities()==15` + `loom.fs` + `loom.process.run` + `LoomError` recuperable + estado persistente. El contrato MCP instalado publica 25 tools y metadata nueva de `python_execute`; este chat conservó un snapshot viejo tras refresh, pero un chat nuevo recibió la metadata correcta y completó el fresh-agent smoke con **`P24_FRESH_AGENT_OK`**. La reconciliación final de la skill/plugin privado ya quedó cerrada en **0.4.0**.

## Python 3 - Outputs binarios e imágenes - P3.0 CERRADO

[[Python 3 - Outputs binarios e imágenes]] mantiene el enfoque **image-output first** sin nueva tool MCP. **P3.0 queda CERRADO** con protocol Worker/Host **v3**, `loom.__bridge_version__=2`, collector execution-local y `loom.display_image(bytes|bytearray|memoryview)` local al worker. Los outputs viajan como base64 sólo en el frame final privado; se limitan a 4 imágenes, 6 MiB por imagen y 6 MiB raw agregado. Normal exception conserva outputs previos, stale threads reciben `output_unavailable` y límites excedidos son recuperables sin descartar el worker. El frame final privado pasa de 32 a **40 MiB** con regresión de presupuesto combinado.

Validación P3.0: Core focalizado **28/28**, Windows Python/protocol **51/51**, Integration **20/20**, suite Release serial **350/350**. El contrato MCP Release permanece **25 tools, byte a byte sin drift** (SHA-256 `674e3edc6f209dbf15074b08ace8484cb7c67c122aa343d73a8df842d86e6064`). No se desplegó runtime ni plugin. Próxima etapa: **P3.1 MCP image transport + payload hardening**.

## Visual Files G1 - CERRADO

Investigación, implementación y evaluación final cerradas en [[Bloque G - Visual Files]] y [[G1.4 - Evaluation + portable]]. **G1.0–G1.4 están cerrados.** El follow-up de compatibilidad visual quedó resuelto: `filesystem_view_image` y `filesystem_render_pdf_page` omiten `outputSchema` y fueron validadas con visión directa en ChatGPT. La repetición específica del paquete G1 en notebook queda diferida/no bloqueante.

Superficie v0.1 fijada:

1. `filesystem_view_image`: PNG/JPEG/WebP -> `ImageContentBlock`.
2. `filesystem_read_pdf`: extracción textual paginada con PdfPig.
3. `filesystem_render_pdf_page`: render de una página PDF -> PNG mediante PDFium nativo aislado.

Implementación incremental propuesta:

1. **G1.0 Binary/image foundation — CERRADO**: Windows TFM, helper MCP mixed structured + image, guard 6/9 MiB, 4 MCP tests, harness Release y portable validados; suite total 207/207.
2. **G1.1 Local image — CERRADO / VISIÓN DIRECTA OK**: `filesystem_view_image`, provider/capability separados, resolver compartido, PNG/JPEG/WebP, `FileShare.Read` y runtime portable healthy/ready. Follow-up: omitir `outputSchema` preserva `ImageContentBlock` en ChatGPT; smoke visual directo confirmado sin fallback.
3. **G1.2 PDF text worker — CERRADO (`b6e41fd`)**: ver [[G1.2 - PDF text worker]]. PdfPig 0.1.16 aislado en child Host, Job Object 256 MiB, timeout 20 s, stable file lock, JSON one-shot y `filesystem_read_pdf`; **235/235 tests**, Host publicado **11/11**, runtime `0.1.0-dev-b6e41fd645d3` healthy/ready y smoke directo ChatGPT/tunnel **OK** con catálogo de 21 tools.
4. **G1.3 PDF render — CERRADO (`d25c3ce`)**: ver [[G1.3 - PDF render]]. PDFium nativo aislado en `LoomLCI.PdfWorker`, P/Invoke mínimo, StbImageWriteSharp, Job 256 MiB/20 s, bounds 256..4096 y `filesystem_render_pdf_page`; **245/245 tests**, Host publicado **12/12**, paquete final instalado en `0.1.0-dev-63d256e9b658` (follow-up de licencias `63d256e`) y runtime healthy/ready. Smoke directo ChatGPT/tunnel **OK** con catálogo de 22 tools: PNG 1800x1080 / 101.550 bytes y error de page-range correcto.
5. **G1.4 Evaluation + portable — CERRADO**: ver [[G1.4 - Evaluation + portable]]. Hardening de mappings, suite Release **249/249**, builder portable, Host publicado, corpus ChatGPT/tunnel y fresh-agent pasaron. La notebook queda diferida/no bloqueante hasta que futuros cambios de packaging o capabilities justifiquen repetir la validación multi-PC.

Decisiones cerradas: tunnel 10 MiB real, imágenes/PNG <=6 MiB + payload MCP visual <=9 MiB, PDF <=64 MiB, texto PDF <=65.536 code points por página y <=262.144 agregados, PdfPig 0.1.16 aislado en worker privado con 256 MiB/20 s, y PDFs protegidos reportados como `unsupported`. La investigación específica de G1.0 también cerró TFM, helper MCP, tests MCP, harness Release y publish portable.

## Computer H1 - PAUSADO

Investigación cerrada en [[Bloque H - Computer]]. Su implementación queda deliberadamente en pausa por ahora; el diseño se conserva para retomarlo más adelante sin reabrir la investigación base.

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
