---
tipo: implementacion
proyecto: LoomLCI
bloque: RB-02
fecha: 2026-10-08
estado: cerrado_end_to_end
---

# RB-02 — Journal y promoción recuperable

> **CERRADO end-to-end en Release 7 `0.1.0-dev-173b4ffc00f6` (sequence 7)** con Launcher nuevo, journal v2 y rollback Release 6 disponible. Antes del despliegue se validaron 11/11 terminaciones forzadas de proceso aislado y E2E de actualización/rollback con túnel real independiente. Ver [[RB-04 - Release 7 desplegada]].

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
- [x] Ensayo de **terminación abrupta de proceso real** 11/11 checkpoints del journal y recuperación desde otra instancia .NET en entorno aislado con runtime simulado.
- [x] E2E de instalación aislada con Launcher auténtico, túnel remoto de prueba válido, actualización supervisada, rollback y healthy/ready.
- [x] Cutover productivo supervisado por IvanSpace desde **fuera del Host**, Host+Launcher actualizados simultáneamente mediante instalador para sequence 7; el updater firmado, por separado, sólo actualiza el Host.
- [x] Smoke directo desde ChatGPT al nuevo runtime; `previous=Release 6`, archivos y digests de rollback verificados (sin ejecutar rollback destructivo en producción).

**Alcance de garantía:** las operaciones de directorio son recuperables entre las etapas persistidas y reintentables bajo los ensayos ejecutados; no se promete aislamiento absoluto de procesos externos ni durabilidad garantizada ante cortes físicos de alimentación. `IUpdateRuntimeControl.IsReadyAsync` verifica estado/túnel, no una atestación criptográfica del ejecutable efectivamente cargado por el proceso; la copia de Host publicada sí se valida byte a byte contra el paquete verificado.

## Próximo paso

**RB-02 cerrado end-to-end en Release 7.** Mantener Release 6 como rollback, el respaldo externo y la evidencia de [[RB-04 - Release 7 desplegada]]. Próxima investigación: RB-03.
