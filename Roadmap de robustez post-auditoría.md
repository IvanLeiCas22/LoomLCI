---
tipo: roadmap
proyecto: LoomLCI
fecha: 2026-10-08
estado: aprobado_para_resolver
fase: implementacion_por_bloque
origen: auditoria_integral_2026-10-08
---

# Roadmap de robustez post-auditoría

> **Decisión del usuario (2026-10-08): resolver los seis bloques de la auditoría integral.** Este documento es un backlog duradero y priorizado, **no** un Work Plan efímero ni evidencia de implementación. Estado actual: **RB-01, RB-02 y RB-04 implementados y validados en fuente, pendientes de despliegue/smoke; RB-03 y RB-05 pendientes, RB-06 documental parcialmente avanzado**. Se conserva el workflow: investigar el punto y el código vigente → diseñar → contrastar con el usuario → implementar → probar → desplegar si procede → documentar y commitear.
>
> Baseline verificada durante la auditoría: instalación productiva **Release 6 `0.1.0-dev-6b1566a` (sequence 6)**, rollback Release 5 `0.1.0-dev-42ee90c` (sequence 5), `healthy/ready`; suite Release **379/379**, Integration **21/21**, 25 tools MCP y 16 capacidades de `loom`. El repositorio se encontraba limpio antes de iniciar los cambios documentales. El chequeo NuGet `--vulnerable --include-transitive --no-restore` no reportó vulnerabilidades conocidas. No equivale a un pentest.
>
> Relacionado con [[Roadmap post-G1]] (cerrado), [[A4.1 - Release 6 desplegada]], [[Deployment portable]], [[Auto-update firmado]], [[Bloque D0 - Resource lifetime y expiry]] y [[Bloque H - Computer]] (H1 sigue pausado).

## Alcance, prioridad y estado

| ID | Bloque | Prioridad | Estado | Dependencia |
| --- | --- | --- | --- | --- |
| RB-01 | Integridad de `filesystem_apply_patch` (`replace` y límites) | Alta | Código validado (400/400), pendiente despliegue/smoke | Primero |
| RB-02 | Update: staging, journal y recuperación ante fallos tempranos | Alta | Código validado (420/420), E2E/cutover pendientes | Antes de futuros cutovers |
| RB-03 | Propagación de fallos al cerrar WorkSession/recursos | Media-alta | Pendiente | Independiente de RB-01/02 |
| RB-04 | Cutover/rollback externos, seguros respecto de Windows Jobs | Media-alta | Código validado (423/423), cutover real pendiente | Coordinar con RB-02 |
| RB-05 | Uninstall sin pérdida accidental de datos locales | Media | Pendiente | Independiente |
| RB-06 | Reconciliación documental y observabilidad durable/opt-in | Baja | Parcial: navegación y estado operativo reconciliados; observabilidad pendiente | Transversal |

**Regla de cierre:** ningún bloque se da por `CERRADO` sólo porque pase la suite existente. Debe cumplir los criterios específicos de abajo, tener pruebas de regresión, una verificación end-to-end cuando corresponda y estado actualizado en este documento. No considerar Computer H1 ni la automatización de publicaciones/actualizaciones como parte de este mini-roadmap.

## RB-01 — Integridad de Filesystem

**Estado (2026-10-08): código implementado y validado; pendiente deployment y smoke productivo.** Nota técnica: [[RB-01 - Integridad de filesystem_apply_patch]]. **No dar por cerrado end-to-end** hasta verificarlo desde ChatGPT tras un cutover externo seguro.

**Hallazgos originales:** F-01 (corrupción de bytes UTF-8 inválidos) y F-02 (salida superior a 16 MiB); también se corrigieron la sobrescritura directa, la falta de rollback registrable antes de publicar y el silenciamiento de fallos de restauración.

**Código:** `src/LoomLCI.Windows/Filesystem/WindowsFilesystemProvider.cs`; regresiones en `tests/LoomLCI.Windows.Tests/FilesystemCapabilityTests.cs`.

- [x] Investigar encoding/BOM, staging, atomicidad acotada, rollback y concurrencia.
- [x] Decodificación estricta sin pérdida silenciosa; preservar BOM/encoding válidos, rechazar bytes inválidos.
- [x] Validar **tamaño final codificado** para `write` y `replace`, incluyendo BOM, antes de publicar.
- [x] Preparación de archivos temporales, publicación con respaldo verificable, manejo de cancelación y errores `rollback_failed`/`cleanup_failed`.
- [x] Regresiones con bytes inválidos, UTF-8/16/32, múltiples reemplazos, sobrepaso de límites, cambio externo, fallos inyectados, concurrencia y restauración.
- [x] Suite Release: **400/400**; Windows **196/196**, Integración **21/21**, pruebas focalizadas Filesystem **48/48** (antes 27).
- [x] Documentar contrato y sus límites reales (sin garantía de crash/power-loss ni aislamiento absoluto de procesos externos).
- [x] Commit de implementación/documentación.
- [ ] Despliegue con supervisor independiente del Host y smoke de consumidor ChatGPT, coordinados con RB-02/RB-04.

**Aceptación para cierre definitivo:** validar el código instalado y los rechazos F-01/F-02 desde las herramientas productivas; conservar rollback operativo y actualizar esta sección a **CERRADO end-to-end** sólo entonces.

## RB-02 — Robustez del actualizador

**Estado (2026-10-08): implementación validada en código; instalación real y pruebas de terminación abrupta pendientes.** Nota técnica: [[RB-02 - Journal y promoción recuperable]]. No declararlo `CERRADO end-to-end` sin prueba instalada y cutover externo seguro.

**Hallazgos:** sustitución del directorio antes de guardar journal, eliminación potencial de una versión anterior, reutilización del nombre active con otro sequence y persistencia incompleta de archivos de control.

**Código:** `UpdateService.cs`, `UpdateJournal.cs`, `HostPackageInstaller.cs`, `MachineConfig.cs`, `DurableFile.cs`; pruebas `UpdateTests.cs`.

- [x] Preflight rechaza sobrescritura de active y colisiones con previous; conserva re-aplicación legítima tras rollback.
- [x] Staging verificado con SHA-256 por archivo y respaldo recuperable de una versión preexistente.
- [x] Journal v2 ANTES de promover versiones, stages explícitas, flush de config/journal, recuperación reintentable e interoperabilidad de journals v1.
- [x] Casos de fallo inyectado y escenarios recreados en disco en preparación, promoción, activación, confirmación y restauración.
- [x] Preservar ECDSA, SHA-256 del feed, límites, `highestSequence` y compatibilidad de protocolo.
- [x] Suite Release de fuente: **420/420**, Launcher **50/50**, Integration **21/21**. Primera corrida con un fallo transitorio ajeno al Launcher (Core deadline), reintento individual y segunda corrida completa aprobaron.
- [ ] Ensayos aislados con kill real del Launcher y E2E completo con instalador + rollback.
- [ ] Instalación en entorno productivo con supervisor externo RB-04 y verificación ChatGPT.

**Aceptación definitiva:** journaling previo a cambios riesgosos, rollback/reanudación sin perder active/previous, ejecución real aislada del Launcher y smoke instalado. No prometer durabilidad física absoluta ante corte eléctrico.

## RB-03 — Fallos de limpieza de recursos

**Hallazgo:** R-01. **Evidencia:** análisis estático; `DisposeEntryAsync` puede devolver error y dejar el recurso activo, mientras `TransitionOwnedAsync` ignora el resultado y `WorkSessionManager.CloseAsync` puede informar éxito.

**Código:** `src/LoomLCI.Core/Resources/ResourceRegistry.cs`, `src/LoomLCI.Core/Work/WorkSessionManager.cs`; pruebas de Core Lifetime/WorkSession.

- [ ] Diseñar estado y política de reintento/reporte para fallos de disposer; preservar invariantes de recursos y sesiones.
- [ ] Propagar correctamente errores en `CloseOwnedAsync`/`ExpireOwnedAsync`/`CloseAsync`; evitar confirmaciones falsas.
- [ ] Agregar pruebas con disposer que falla, reintentos, cancelación concurrente y limpieza múltiple (una falla no debe ocultar las demás).
- [ ] Verificar ausencia de fugas y la semántica de tombstones/expiry.

**Aceptación:** el llamador puede distinguir cierre completo de parcial/fallido; ningún recurso fallido queda sin ruta de observación o recuperación; pruebas de regresión exitosas.

## RB-04 — Cutover externo y seguro

**Estado (2026-10-08): código/procedimiento y ensayos aislados validados, pendiente E2E con instalador genuino y cutover productivo.** Nota: [[RB-04 - Supervisor externo y cutover seguro]]. No declarar `CERRADO end-to-end` hasta probar la actualización real y el smoke desde ChatGPT.

**Hallazgo U-02:** `Independent` de WorkSession no escapa de Jobs ancestrales. Durante Release 6 un intento iniciado desde LoomLCI perdió su supervisor; el cutover iniciado desde IvanSpace funcionó.

**Implementado:** protocolo de actualización 2, paquete y manifest firmado marcados `minUpdateProtocol=2`, validación de coherencia package/manifest, metadata de instalador con digest/version/sequence/rutas, supervisor PowerShell externo de ejecución explícita y preflight fail-closed, copia de respaldo y log fuera del installation root.

- [x] Revisar código Windows Job, Launcher, Setup y Task Scheduler/IvanSpace.
- [x] Formalizar runbook de `stop / installer / start / status` con supervisor fuera del Host productivo, sin alterar `process_start` ni habilitar escape genérico de Jobs.
- [x] Rejectar Launchers antiguos vía release firmada protocolo 2; **53/53 pruebas Launcher**.
- [x] Preflight aislado **7/7**, metadata de rutas/sha y no-journal, rechazo de ejecuciones sin `-ConfirmExternalSupervisor`.
- [x] Ensayo completo de cutover desde IvanSpace externo contra **launcher/instalador ficticios**, con backup, start y health, `RB04_EXTERNAL_ISOLATED_EXECUTE_OK`.
- [x] Inno Setup 7.1.0 compiló y **ejecutó** instalador auténtico en rutas aisladas (ID distinto), verificando metadata y SHA-256 del Host/Launcher. La credencial falsa impidió terminar Setup; el error pasó de exit 0 engañoso a **exit 100** tras corregir `GetCustomSetupExitCode`. La desinstalación aislada terminó exit 0.
- [x] Suite Release integral **423/423**, Integration **21/21**, sin corte del runtime productivo.
- [x] **11/11** terminaciones forzadas de proceso .NET real en puntos de journal RB-02, incluida restauración interrumpida, recuperadas desde otra instancia (controlador de runtime simulado).
- [x] Instalador genuino probado en **ruta temporal**, camino de error por credenciales ficticias correctamente informado, binarios verificados, ambos accesos directos del escritorio preservados en la última repetición.
- [ ] E2E genuino con túnel **válido e independiente del productivo** y supervivencia de supervisor frente al cierre del Host bajo Job Objects.
- [ ] Supervisar cutover productivo desde IvanSpace externo, validar runtime/smoke/rollback y cierre RB-01/RB-02/RB-04.

**Aceptación definitiva:** el supervisor externo sobrevive al `stop` real, reconoce versión y túnel correctos, obtiene y conserva evidencia durable; existe rollback verificable y nuevo Launcher/Host funcional. El E2E mock ni el kill de UpdateService con runtime simulado prueban por sí solos la independencia de todos los Job Objects.

## RB-05 — Desinstalación y preservación de datos

> **Ajuste puntual ya aplicado durante las pruebas de RB-04:** se quitaron de `[UninstallDelete]` dos reglas que borraban `LoomLCI.lnk` y `Detener LoomLCI.lnk` del escritorio por nombre incluso desde un AppId aislado. Los dos accesos directos se habían eliminado en la primera prueba y se restauraron/verificaron; la repetición con SHA-256 pasó. **No equivale al cierre de RB-05**: la eliminación recursiva de datos y el ownership de shortcuts requieren todavía su diseño y ensayos propios.

**Hallazgo:** I-01. **Evidencia:** `installer/LoomLCI.iss`, sección `[UninstallDelete]`, elimina recursivamente `%LOCALAPPDATA%\LoomLCI`, que puede contener datos compartidos por la instalación y experimentos.

- [ ] Inventariar rutas propias de la instalación, secretos, caches, perfiles y datos de experimentos.
- [ ] Definir comportamiento de uninstall seguro: preservar datos del usuario por defecto y ofrecer eliminación explícita y suficientemente advertida, si se aprueba ese diseño.
- [ ] Delimitar exactamente las rutas que el uninstall puede borrar; considerar reinstalación y coexistencia de instalaciones.
- [ ] Agregar smoke de desinstalar/reinstalar conservando datos y de purga explícita en entorno aislado.

**Aceptación:** desinstalación estándar no elimina datos ajenos ni secretos/perfiles que deban conservarse; purga total requiere elección consciente y es comprobable.

## RB-06 — Documentación y observabilidad

**Hallazgo:** D-01 y deuda operativa transversal. **Evidencia:** al auditar, `Inicio.md`, `Plan y tareas.md`, `Preguntas abiertas.md` y la cabecera de `Decisiones.md` seguían presentando Release 5/366 como estado actual, pese a Release 6/379. `LoomEventBus.cs` usa un canal efímero de 1.024 eventos con `DropOldest`.

- [x] Crear este roadmap y registrar las seis prioridades con criterios de cierre.
- [x] Reconciliar páginas de entrada con Release 6/379 y enlaces al nuevo backlog (conservar hechos históricos etiquetados como tales).
- [ ] Investigar observabilidad durable **opt-in**: eventos mínimos, límites, rotación, retención, diagnósticos de errores, privacidad y capacidad de deshabilitarla.
- [ ] Evitar por defecto logs de credenciales, payloads sensibles y contenidos completos de archivos.
- [ ] Agregar pruebas de límites/retención y una guía de diagnóstico.
- [ ] Cerrar la documentación con la nueva evidencia una vez implementados RB-01 a RB-05.

**Aceptación:** la documentación de entrada no contradice el runtime vigente; los eventos esenciales son auditables cuando el usuario habilita esa capacidad, sin almacenar secretos ni crecer indefinidamente.

## Ejecución y control de cambios

- Trabajar **por bloque**. Antes de tocar código, volver a inspeccionar su estado y presentar la propuesta concreta al usuario. La aprobación global de resolver las seis deudas no sustituye la revisión puntual del diseño.
- Para cada bloque registrar fecha, diseño aprobado, archivos tocados, pruebas focalizadas y completas, commit, release/despliegue (si aplica), smoke real y estado de rollback.
- No mezclar estos parches correctivos con la implementación pausada de Computer H1 ni con futuras automatizaciones de releases.
- Documentación actual: se modifica la bóveda Obsidian del repo; **no se ejecutó implementación** de RB-01 a RB-06 durante la creación del roadmap.

## Próximo paso

**RB-01 — investigación específica y propuesta de corrección de `filesystem_apply_patch`**. No implementarlo hasta revisar el diseño con el usuario.
