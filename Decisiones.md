# Decisiones

## Confirmadas hasta ahora

- LoomLCI será un runtime/capa local, no un agente.
- Full Trust será el caso de uso principal: LoomLCI no impondrá restricciones adicionales sobre lo que permita la cuenta de Windows.
- Full Trust no implica elevación administrativa; UAC/elevación será un concepto separado.
- Las ubicaciones/proyectos conocidos serán contexto y accesos rápidos, no límites de seguridad.
- MCP será una frontera/adaptador externo; el modelo interno no dependerá de MCP.
- Las capabilities estructuradas coexistirán con ejecución general de procesos/shell.
- Se preservará una abstracción de ExecutionContext aunque la primera implementación sea Host/Full Trust.
- Todo debe poder observarse, pero observabilidad no implica persistencia inmediata en disco.
- Programmatic Tool Calling (PTC) será un objetivo de compatibilidad para el diseño de las tools, no un componente interno de LoomLCI.
- LoomLCI debe funcionar correctamente con direct tool calling; si el harness superior soporta PTC, debe poder componer las mismas tools sin cambios en el Core.
- El Work Plan permitirá múltiples pasos activos o en espera al mismo tiempo; no se impondrá la restricción de un único `in_progress`.
- El estado lógico del Work Plan estará separado del lifecycle real de procesos, invocaciones y otros recursos; en v0.1 no habrá acoplamiento automático entre steps y resource handles.
- Agent Support F1.1 mantiene un único Work Plan efímero directamente dentro de cada WorkSession; no usa Resource Registry, se descarta al comenzar close/expiry y sus snapshots se actualizan atómicamente con `expectedRevision` para evitar lost updates.
- Los pasos del Work Plan usan IDs opacos `step_*`, permiten múltiples estados `active`/`waiting`, y F1.1 limita el plan a 32 pasos de una sola línea y 512 Unicode scalar values por texto.
- Agent Support se expone sólo por opt-in del adapter; el Host objetivo para ChatGPT habilita `work_plan_get`, `work_plan_update` y `work_plan_patch`. `work_plan_get` es read-only/non-idempotent por refresh de lifetime; las dos mutations son destructive/non-idempotent.
- `work_plan_patch` es la superficie ergonómica para cambios pequeños: `add`/`update`/`remove`, 1–32 cambios, aplicación atómica sobre copia, un único incremento de revision y sin auto-merge. `work_plan_update` conserva full replacement para creación inicial, reorder, reconciliación completa y clear.
- Python Runtime usará un CPython privado/versionado por LoomLCI, no el Python del PATH/usuario; E1 fija CPython 3.14.8 x64 embeddable.
- Python Runtime tendrá un único worker lazy y session-owned por WorkSession; `workId` será suficiente como identidad pública y no se expondrá `PythonHandle` en E1.
- El IPC Python ↔ Loom usará Named Pipe privado/versionado separado de stdout/stderr; el backend Windows reutilizará `IProcessProvider`/Job Objects para lifecycle y cleanup.
- `worker.py` y `runtime.json` se embeben en `LoomLCI.Windows`; el Host no dependerá del repo ni de su cwd para localizar assets de Python.
- El CPython privado se auto-provisionará on-demand bajo `%LOCALAPPDATA%\LoomLCI`, con descarga acotada, SHA-256 fijado, staging y publicación por rename; los tests del provisioner usarán HTTP/ZIP falsos en vez de Internet.
- E1 de Python será stdlib-only y textual; PyAutoGUI, imágenes, paquetes de terceros y `loom.*` quedan diferidos.
- Antes de Computer se implementará Visual Files G1 con tres tools read-only: `filesystem_view_image`, `filesystem_read_pdf` y `filesystem_render_pdf_page`; Computer pasa al bloque H. G1 mantiene nombres públicos `filesystem_*` pero separa internamente `VisualFilesCapability`/provider del Filesystem clásico.
- Visual Files fija 6 MiB máximos de binario por imagen/PNG y además un cap de 9 MiB sobre el `CallToolResult` MCP serializado estimado exactamente; esto deja margen frente al límite real de 10 MiB del Secure MCP Tunnel incluso con escaping de base64. Los PDFs se limitan a 64 MiB.
- `filesystem_read_files` conserva el límite interno de 64 MiB de texto agregado en Core/Windows, pero el adapter MCP rechaza localmente cualquier `CallToolResult` serializado >9 MiB con `unsupported/mcp_payload_too_large`. La capa interna permanece agnóstica del transporte y el guard MCP contempla escaping JSON y metadata.
- `process_run` será la superficie one-shot para comandos cortos/no interactivos: se implementa en Core como una única invocation, reutiliza `IProcessProvider`/Job Objects/pipes, no registra un recurso durable ni devuelve `ProcessHandle`, y trata exit codes no cero como resultado normal. `process_start` conserva el lifecycle durable/interactivo.
- G1.1 expone `filesystem_view_image` como tool pública de Filesystem pero mantiene internamente `VisualFilesCapability`/provider separados; PNG/JPEG/WebP se detectan por estructura del contenido, no por extensión, sin decoder local. La lectura usa `FileShare.Read`; un writer/lock activo devuelve `busy` retryable con `reason=file_busy`.
- La extracción textual de PDF usa PdfPig 0.1.16 dentro de `LoomLCI.PdfWorker`, ejecutado como child mode privado `LoomLCI.Host --internal-pdf-worker-v1`; así conserva aislamiento de proceso con Job Object, límite de memoria de 256 MiB y timeout de 20 s sin publicar un segundo runtime .NET self-contained. `filesystem_read_pdf` quedó implementado en G1.2 con límites por página/agregado y lectura estable `FileShare.Read`.
- G1.3 descarta la decisión preliminar de `Windows.Data.Pdf`: Microsoft no soporta oficialmente esas clases en el modelo desktop portable/unpackaged de LoomLCI. El render visual quedó implementado en `d25c3ce` con `bblanchon.PDFium.Win32 157.0.8086` mediante P/Invoke mínimo propio dentro de un child mode aislado de `LoomLCI.PdfWorker`, `StbImageWriteSharp 1.16.7`, Job Object 256 MiB, timeout 20 s, PNG <=6 MiB y payload MCP <=9 MiB. La migración a Windows TFM ya realizada en G1.0 se conserva y Computer H reutilizará esa base.
- `python_execute`/`python_reset` serán la superficie MCP pública inicial. Una excepción Python ordinaria será un resultado exitoso (`ok=true`, `status=exception`); sólo fallos de Loom/infraestructura serán tool errors.
- El código enviado a Python se limita a 256 KiB en UTF-8 estricto y se valida en Core antes de crear/tocar worker.
- `maxOutputChars` y los límites de metadata Python se interpretan como Unicode code points, no unidades UTF-16.
- Launcher seguirá siendo CLI en esta etapa: la UX de escritorio se resuelve con `--pause` opt-in y accesos directos de iniciar/detener, sin introducir todavía WinForms/WPF/tray. La CLI normal permanece no interactiva para automatización, y el packaging del README usa template UTF-8 leído/escrito explícitamente para no depender del encoding implícito de Windows PowerShell 5.1.
- El instalador Windows convencional usa Inno Setup **7.1.0** como toolchain fijado y se mantiene como wrapper fino sobre el portable: no reimplementa configuración, sino que delega en `LoomLCI.Launcher setup`. La instalación es per-user/sin admin por defecto, usa un AppId estable, registra LoomLCI en Aplicaciones instaladas y el uninstall elimina tanto binarios como `%LOCALAPPDATA%\LoomLCI` (deployment, secrets y runtimes/assets privados). Auto-update/rollback automático y code signing quedan en bloques posteriores.
