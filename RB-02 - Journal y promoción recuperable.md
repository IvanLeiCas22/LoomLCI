---
tipo: implementacion
proyecto: LoomLCI
bloque: RB-02
fecha: 2026-10-08
estado: codigo_validado_pendiente_cutover
---

# RB-02 — Journal y promoción recuperable

> **Implementado y validado en el repositorio. NO DESPLEGADO.** La instalación productiva continúa en Release 6 `0.1.0-dev-6b1566a` (sequence 6). El cambio del Launcher requiere instalador convencional y supervisor de cutover externo (ver [[Roadmap de robustez post-auditoría]] y [[A4.1 - Release 6 desplegada]]). No declarar cerrado end-to-end sin E2E de instalación/recuperación.

## Hallazgos originales

- `UpdateService.ApplyAsync` publicaba/sustituía un directorio de Host **antes** de crear el journal.
- `HostPackageInstaller.InstallHost(... replaceExisting:true)` podía borrar una versión ya existente antes de reemplazarla.
- El preflight no prohibía una firma válida con `sequence` nuevo y **el mismo nombre de la versión activa**; el nombre previo era comparado con `Ordinal`, no con la equivalencia de rutas habitual de Windows.
- `machine.json` y journal usaban escritura temporal y rename sin flush explícito del contenido.
- El relato histórico de [[Auto-update firmado]] describía la recuperación como completa pese a que no cubría todos estos estados.

## Implementación en código

- `UpdateService.cs`: preflight contra colisiones con active (case-insensitive) y contra nombres/sequences incompatibles con previous; se conserva la **reaplicación de previous** tras rollback si identidad y `sequence` coinciden.
- Se prepara y verifica una copia del Host firmado en `versions/<version>.staging-<id>` **sin tocar las versiones instaladas**. Se compara SHA-256 de todos los archivos copiados con el contenido previamente verificado del ZIP.
- Se guarda journal schema **v2** en etapa `Prepared` **antes de la primera mutación de un directorio de versión existente**, con `TargetExisted` y `OperationId` válido.
- Promoción: persistir `Promoting` → mover target preexistente a `versions/<version>.backup-<id>` → mover staging a target → persistir `Promoted` → detener/activar/iniciar.
- Commit sólo después de confirmación del runtime: `RuntimeStarted` → `Committed` durable → retirar respaldo/staging → limpiar versiones antiguas → eliminar journal. Si hay fallo después de `Committed`, recuperar/finalizar la actualización en lugar de declarar que se restauró la anterior.
- Recuperación v2: un proceso nuevo puede restaurar el target original usando el backup y reintentar la propia recuperación si ésta fue interrumpida. **Al restaurar se copia desde el backup y no se consume** hasta borrar el journal; este detalle permite idempotencia frente a crash mientras se restaura. Las etapas `Committed` se finalizan sin rollback de un target ya confirmado.
- `UpdateJournal.cs`: lector **compatible con schema v1** de instalaciones anteriores y guardado v2 con identificador validado. `MachineConfig.cs` y `DurableFile.cs`: escritura de contenido a temporal hermano + `Flush(true)` + rename. La persistencia del directorio NTFS no está cubierta por una garantía universal frente a pérdida súbita de energía.
- `HostPackageInstaller.cs`: se elimina la rama pública destructiva `replaceExisting`; Setup no elimina versiones existentes.
- Sin cambios en firma ECDSA P-256, package SHA-256 firmado, límite de extracción, `highestSequence`, `minUpdateProtocol`, `DeploymentLock` o herramientas públicas.

## Validación

- Suite Release **420/420**: Launcher **50/50** (antes 30), Core **141/141**, Windows **196/196**, MCP **6/6**, PDF **6/6**, Integración **21/21**.
- 20 pruebas adicionales en Launcher: secuencia activa falsificada, diferencias de mayúsculas, colisión de previous, fallo inyectado en `prepared/promoting/backed_up/published`, destinos nuevos y preexistentes, estados persistidos tras interrupciones en `Stopping/RuntimeStopped/Activated/Starting/RuntimeStarted`, continuación de `Committed`, idempotencia de una restauración interrumpida, journal imposible de recuperar sin backup.
- Se preservan pruebas preexistentes de firma/descarga, rollback, high-watermark y casos journal schema v1.
- **Incidencia de suite:** la primera ejecución completa tuvo un fallo aislado de temporización en `PythonRunManyBridgeTests.BatchDeadlineCancelsActiveAndLeavesQueuedUnstarted` (área Core sin cambios). Ese test aprobó en reintento individual y **la siguiente ejecución completa aprobó 420/420**. No confundir con una prueba del actualizador.

## Límites y pendientes

- [x] Protección de versión activa y de previous por identidad y sequence.
- [x] Staging en volumen de destino, verificación de copia y journal temprano.
- [x] Backup y promoción recuperables; recuperación reintentable; compatibilidad de journals v1.
- [x] Tests de fallo controlado y reconstrucción de estados de crash; suite Release.
- [ ] Ensayo de **terminación abrupta de proceso real**, en entorno independiente, en todos los puntos decisivos; los actuales ensayos reconstruyen estados en disco e inyectan excepciones, no emulan una pérdida súbita de energía.
- [ ] Prueba E2E con instalación aislada incluyendo Launcher, actualización, rollback y control de carpeta activa.
- [ ] Cutover productivo supervisado desde **fuera de LoomLCI**, coordinado con **RB-04**. Primero actualizar el Launcher usando instalador, luego el Host (el updater no actualiza su propio Launcher).
- [ ] Smoke desde ChatGPT del nuevo runtime y su rollback; cerrar RB-01 y RB-02 end-to-end sólo después.

**Alcance de garantía:** las operaciones de directorio son recuperables entre las etapas persistidas y reintentables bajo los ensayos ejecutados; no se promete aislamiento absoluto de procesos externos ni durabilidad garantizada ante cortes físicos de alimentación. `IUpdateRuntimeControl.IsReadyAsync` verifica estado/túnel, no una atestación criptográfica del ejecutable efectivamente cargado por el proceso; la copia de Host publicada sí se valida byte a byte contra el paquete verificado.

## Próximo paso

Investigación y ejecución segura de [[Roadmap de robustez post-auditoría|RB-04]] (supervisor externo/cutover) antes de instalar el Launcher y Host nuevos. RB-03 puede investigarse en paralelo sin interrumpir la versión actual.
