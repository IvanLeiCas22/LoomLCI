# LoomLCI

Segundo cerebro del proyecto LoomLCI.

## Estado

**Corte operativo verificado el 2026-10-09:** instalación productiva **`0.1.0-dev-fc4373bdff73` (sequence 11)**, `healthy=true` y `ready=true` sobre el mismo Secure MCP Tunnel; **rollback disponible a seq10** (`0.1.0-dev-95462385fbc6`). **RB-06 desplegado y validado** con IvanSpace externo: `CUTOVER_OK`, backup durable, hashes, datos y shortcuts preservados, un solo uninstaller, reconexión ChatGPT MCP Python con 16 capacidades; **diagnósticos permanecen desactivados por defecto, sin logs productivos**. Ver [[RB-06 - Release 11 desplegada]], [[RB-06 - Observabilidad durable y reconciliacion documental]] y [[RB-05 - Release 10 desplegada]] (histórico). **No es GitHub Release**; no hubo push ni activación remota.

**Capacidades cerradas:** Process, Filesystem (funcional, con deudas de integridad identificadas en auditoría), Native Process/Job Objects, ConPTY, resource lifetime, Python Runtime E1, [[Python 1 - Paquetes administrados|Python 1]], [[Python 2 - Bridge privado loom|Python 2]], [[Python 3 - Outputs binarios e imágenes|Python 3]], Agent Support F1, Visual Files G1, ergonomía, Launcher, [[Instalador Windows]], update firmado y [[Plugin metadata]]. El Host publica **25 tools MCP**, `loom.capabilities()` enumera **16** y el plugin privado es **0.5.1**. Suite RB-03 en fuente: **433/433** (21/21 IntegrationTests); paquete final local seq8 validado además con **53/53 Launcher** y **21/21 integración contra Host publicado**. Host y Launcher internos instalados, smoke ChatGPT productivo de **25 herramientas MCP**, Python **16 capacidades** y cierre real de proceso SessionOwned verificado.

**Trabajo vigente:** [[Roadmap de robustez post-auditoría]] — seis prioridades aprobadas para resolver. [[RB-01 - Integridad de filesystem_apply_patch|RB-01]], [[RB-02 - Journal y promoción recuperable|RB-02]] y [[RB-04 - Supervisor externo y cutover seguro|RB-04]] están **CERRADOS end-to-end** tras despliegue supervisado de [[RB-04 - Release 7 desplegada|Release 7]] y smoke ChatGPT. [[RB-03 - Cierre recuperable de recursos|RB-03]] está **CERRADO end-to-end en instalación local seq8**, validada con smoke ChatGPT y supervisor IvanSpace. **DX-01 — optimización del workflow de desarrollo y actualización** quedó **implementado, validado y probado con despliegue manual excepcional seq9** (prevalidación MCP, empaquetado sin ZIP, tiempos por fase, E2E aislado). **Workflow habitual sin cambios: el asistente se ocupa del código, pruebas, builds, documentación y despliegue supervisado; el usuario aprueba decisiones y sólo interviene cuando es necesario.** Ver [[DX-01 - Optimizacion workflow de desarrollo y despliegue]]. **RB-05 CERRADO en seq10** tras E2E aislado y cutover productivo supervisado, integridad y smoke MCP posteriores. Ver [[RB-05 - Release 10 desplegada]]. **RB-06 cerrado end-to-end en seq11**: diagnóstico local opt-in con privacidad y cuotas, 448/448 suite fuente, 23/23 integración contra Host publicado y smoke real tras cutover IvanSpace. Se mantiene desactivado por defecto y no genera registros sin habilitación voluntaria. Ver [[RB-06 - Release 11 desplegada]]. Próxima secuencia productiva: **>=12**. Se revisa cada bloque antes de modificarlo. El [[Roadmap post-G1]] está cerrado y [[Bloque H - Computer|Computer H1]] continúa pausado. La publicación y aplicación automática de releases siguen diferidas. `UI_RENDER` inline de imágenes Python en ChatGPT sigue siendo una limitación del consumidor documentada, no un defecto del Host. Ver [[Plan y tareas]] y [[Preguntas abiertas]].

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
