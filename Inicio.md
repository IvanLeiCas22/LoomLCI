# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

**Corte operativo verificado el 2026-10-08:** instalación productiva en **Release 7, `0.1.0-dev-173b4ffc00f6` (sequence 7)**, `healthy=true` y `ready=true` sobre el Secure MCP Tunnel productivo. Rollback disponible: **Release 6 `0.1.0-dev-6b1566a` (sequence 6)**. Ver [[RB-04 - Release 7 desplegada]], [[A4.1 - Release 6 desplegada]] (histórico) y [[Auto-update firmado]].

**Capacidades cerradas:** Process, Filesystem (funcional, con deudas de integridad identificadas en auditoría), Native Process/Job Objects, ConPTY, resource lifetime, Python Runtime E1, [[Python 1 - Paquetes administrados|Python 1]], [[Python 2 - Bridge privado loom|Python 2]], [[Python 3 - Outputs binarios e imágenes|Python 3]], Agent Support F1, Visual Files G1, ergonomía, Launcher, [[Instalador Windows]], update firmado y [[Plugin metadata]]. El Host publica **25 tools MCP**, `loom.capabilities()` enumera **16** y el plugin privado es **0.5.1**. Suite Release 7: **423/423** en fuente, incluidas **21/21 IntegrationTests**; el nuevo Host y Launcher están **instalados**. Smoke ChatGPT productivo: catálogo **25 herramientas MCP**, Python **16 capacidades**, F-01/F-02 y atomicidad de Filesystem verificados.

**Trabajo vigente:** [[Roadmap de robustez post-auditoría]] — seis prioridades aprobadas para resolver. [[RB-01 - Integridad de filesystem_apply_patch|RB-01]], [[RB-02 - Journal y promoción recuperable|RB-02]] y [[RB-04 - Supervisor externo y cutover seguro|RB-04]] están **CERRADOS end-to-end** tras despliegue supervisado de [[RB-04 - Release 7 desplegada|Release 7]] y smoke ChatGPT. RB-03, RB-05 y observabilidad RB-06 siguen pendientes. Se revisa cada bloque antes de modificarlo. El [[Roadmap post-G1]] está cerrado y [[Bloque H - Computer|Computer H1]] continúa pausado. La publicación y aplicación automática de releases siguen diferidas. `UI_RENDER` inline de imágenes Python en ChatGPT sigue siendo una limitación del consumidor documentada, no un defecto del Host. Ver [[Plan y tareas]] y [[Preguntas abiertas]].

## Notas

- [[Decisiones]]
- [[Investigacion]]
- [[Arquitectura propuesta]]
- [[Especificacion interna v0.1]]
- [[Estructura del repositorio v0.1]]
- [[Milestone 1 - Process vertical slice]]
- [[Milestone 2 - Filesystem vertical slice]]
- [[Bloque C1 - Native Process y Job Objects]]
- [[Bloque C2 - ConPTY]]
- [[Bloque D0 - Resource lifetime y expiry]]
- [[Validacion final fresh-agent]]
- [[Preguntas abiertas]]
- [[Python Runtime]]
- [[Bloque E - Python Runtime]]
- [[Python 1 - Paquetes administrados]]
- [[Python 2 - Bridge privado loom]]
- [[Python 3 - Outputs binarios e imágenes]]
- [[Bloque F - Agent Support]]
- [[Bloque G - Visual Files]]
- [[G1.1 - Local image]]
- [[Bloque H - Computer]]
- [[F1.3 - Benchmark real-world Work Plan]]
- [[Ergonomía - Work Plan patch]]
- [[Plan y tareas]]
- [[Roadmap de robustez post-auditoría]]
- [[RB-04 - Supervisor externo y cutover seguro]]
- [[RB-02 - Journal y promoción recuperable]]
- [[RB-01 - Integridad de filesystem_apply_patch]]
- [[Roadmap post-G1]]
- [[Auto-update firmado]]
- [[Plugin metadata]]
- [[Programmatic Tool Calling]]
- [[Integracion con ChatGPT]]
- [[Deployment portable]]
- [[Instalador Windows]]
- [[Fuentes]]
