---
tipo: estado_operativo
estado: vigente
actualizado: 2026-10-09
---
# Estado del sistema

**Única referencia documental del estado de la instalación productiva**. Corte verificado el **9 de octubre de 2026** mediante `LoomLCI.Launcher.exe status` desde LoomLCI MCP productivo. Para información en tiempo real posterior, ejecutar otra vez `status`.

| Elemento | Estado verificado |
| --- | --- |
| Instalación activa | `0.1.0-dev-fc4373bdff73` |
| Sequence | **11** |
| Rollback disponible | `0.1.0-dev-95462385fbc6` (sequence 10) |
| Runtime | `loomlci-installed` |
| Servicio | `process_running=True`, `healthy=True`, `ready=True` |
| Conexión | ChatGPT normal → app `LoomLCI MCP` → Secure MCP Tunnel → Host STDIO |
| Plugin de workflow | versión **0.5.1** (`plugin/plugin.json`) |
| Catálogo MCP | **25 herramientas** con Work Plan habilitado; definición real en el Host |
| Bridge Python | **16 capacidades** declaradas por `loom.capabilities()` en el smoke de seq11 |
| Diagnósticos | Capacidad instalada y **desactivada por defecto**; no había logs productivos al cierre de RB-06 |
| Computer Use | H1 diseñado pero **no implementado/productivo** |
| Actualización automática/publicación remota | Diferida; no inferir publicación GitHub a partir de una instalación local |

**Evidencias:** [[RB-06 - Release 11 desplegada]] documenta paquete, SHA-256, preflight, cutover externo, tests y smoke productivo. [[RB-05 - Release 10 desplegada]] registra el rollback previo. El commit documental posterior no altera los binarios instalados.

## Implementado

- WorkSession, control de recursos, expiración, invocaciones y Work Plan efímero.
- Filesystem: navegación, lectura, búsquedas con paginación y parches estructurados.
- Process: pipes, ConPTY, Job Objects, procesos independientes, `process_run` y APIs persistentes.
- Python embebido privado, paquetes PyPI administrados, bridge `loom.fs`/`loom.process` y `loom.display_image`.
- Visual Files: imagen local, texto PDF y render de páginas PDF.
- Launcher, instalador, actualizaciones firmadas, rollback, desinstalación protectora, cutover externo y diagnóstico local opt-in.

Para uso y límites, consultar [[Filesystem]], [[Process]], [[Python]], [[Visual Files]], [[Work Plan]] y [[Diagnosticos]].

## Alcances y limitaciones

- Los bloques **RB-01 a RB-06** quedaron cerrados para su alcance aprobado, no constituyen una garantía de ausencia absoluta de defectos. Evidencias en [[Roadmap de robustez post-auditoría]].
- Las imágenes generadas en Python pueden recibirse visualmente por el modelo, pero la presentación inline en la interfaz ChatGPT depende del consumidor; no confundirla con error del Host.
- No emplear el propio Host como supervisor de su parada, actualización o rollback. Ver [[Actualizacion y recuperacion]].
- Los diagnósticos no generan datos sin habilitación voluntaria y son best-effort, no auditoría inviolable.

## Verificar el estado después de un cambio

```powershell
& "$env:LOCALAPPDATA\Programs\LoomLCI\LoomLCI.Launcher.exe" status
& "$env:LOCALAPPDATA\Programs\LoomLCI\LoomLCI.Launcher.exe" diagnostics status
```

Registrar toda nueva release en el historial; actualizar **esta página** como estado activo único, en vez de repetir número de sequence/rollback en notas operativas.
