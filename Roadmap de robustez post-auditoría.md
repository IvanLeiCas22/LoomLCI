---
tipo: roadmap
proyecto: LoomLCI
fecha: 2026-10-08
estado: aprobado_para_resolver
fase: documentacion
origen: auditoria_integral_2026-10-08
---

# Roadmap de robustez post-auditoría

> **Decisión del usuario (2026-10-08): resolver los seis bloques de la auditoría integral.** Este documento es un backlog duradero y priorizado, **no** un Work Plan efímero ni evidencia de implementación. Estado inicial: **documentación creada; cambios de código, validaciones y despliegues pendientes**. Se conserva el workflow: investigar el punto y el código vigente → diseñar → contrastar con el usuario → implementar → probar → desplegar si procede → documentar y commitear.
>
> Baseline verificada durante la auditoría: instalación productiva **Release 6 `0.1.0-dev-6b1566a` (sequence 6)**, rollback Release 5 `0.1.0-dev-42ee90c` (sequence 5), `healthy/ready`; suite Release **379/379**, Integration **21/21**, 25 tools MCP y 16 capacidades de `loom`. El repositorio se encontraba limpio antes de iniciar los cambios documentales. El chequeo NuGet `--vulnerable --include-transitive --no-restore` no reportó vulnerabilidades conocidas. No equivale a un pentest.
>
> Relacionado con [[Roadmap post-G1]] (cerrado), [[A4.1 - Release 6 desplegada]], [[Deployment portable]], [[Auto-update firmado]], [[Bloque D0 - Resource lifetime y expiry]] y [[Bloque H - Computer]] (H1 sigue pausado).

## Alcance, prioridad y estado

| ID | Bloque | Prioridad | Estado | Dependencia |
| --- | --- | --- | --- | --- |
| RB-01 | Integridad de `filesystem_apply_patch` (`replace` y límites) | Alta | Pendiente | Primero |
| RB-02 | Update: staging, journal y recuperación ante fallos tempranos | Alta | Pendiente | Antes de futuros cutovers |
| RB-03 | Propagación de fallos al cerrar WorkSession/recursos | Media-alta | Pendiente | Independiente de RB-01/02 |
| RB-04 | Cutover/rollback externos, seguros respecto de Windows Jobs | Media-alta | Pendiente | Coordinar con RB-02 |
| RB-05 | Uninstall sin pérdida accidental de datos locales | Media | Pendiente | Independiente |
| RB-06 | Reconciliación documental y observabilidad durable/opt-in | Baja | Parcial: navegación y estado operativo reconciliados; observabilidad pendiente | Transversal |

**Regla de cierre:** ningún bloque se da por `CERRADO` sólo porque pase la suite existente. Debe cumplir los criterios específicos de abajo, tener pruebas de regresión, una verificación end-to-end cuando corresponda y estado actualizado en este documento. No considerar Computer H1 ni la automatización de publicaciones/actualizaciones como parte de este mini-roadmap.

## RB-01 — Integridad de Filesystem

**Hallazgos:** F-01 (pérdida de bytes al reemplazar texto no UTF-8 válido) y F-02 (archivo de salida de `replace` superior a 16 MiB). **Evidencia:** reproducidos sobre archivos temporales ajenos al repositorio y eliminados después. Un `replace` aceptó bytes `FF 61 58 62` y produjo `EF BF BD 41 58 62`; otro aceptó una salida de **18.600.030 bytes** pese al límite declarado de **16.777.216 bytes**. El riesgo de rollback insuficiente ante una escritura interrumpida se deduce del código; no se provocó un fallo físico.

**Código:** `src/LoomLCI.Windows/Filesystem/WindowsFilesystemProvider.cs`, especialmente `ValidatePatch`, `EnsureTextFile` y `ApplyPatchAsync` (caso `Replace`); contratos en `src/LoomLCI.Core/Filesystem`. Pruebas: `tests/LoomLCI.Windows.Tests/FilesystemCapabilityTests.cs`.

- [ ] Investigar encoding/BOM, escritura temporal, atomicidad y concurrencia sobre el archivo de destino.
- [ ] Definir qué encodings se aceptan y rechazar datos inválidos sin alterarlos ni normalizarlos silenciosamente.
- [ ] Validar el **tamaño final codificado** antes de escribir, también para reemplazos múltiples.
- [ ] Hacer seguro el rollback ante fallo de escritura, cancelación y fallos posteriores dentro del batch; no prometer atomicidad frente a corte eléctrico sin comprobarla.
- [ ] Añadir tests: bytes inválidos/BOM, tamaño de salida excedido, escrituras interrumpidas e interacción entre cambios.
- [ ] Ejecutar suite Release, validar con ensayos aislados y documentar el contrato resultante.

**Aceptación:** no hay pérdida de bytes ajenos al cambio; entradas inválidas fallan sin mutación; nunca se emite una salida por encima del límite; fallo/cancelación no deja contenido truncado y los tests de reversión pasan.

## RB-02 — Robustez del actualizador

**Hallazgo:** U-01, ventana entre instalar/reemplazar la carpeta de destino y crear el journal. **Evidencia:** análisis estático del orden del código; falta reproducir interrupciones en esos puntos. Se identificó además la posibilidad de `sequence` superior reutilizando el nombre de la versión activa en un feed firmado erróneo.

**Código:** `src/LoomLCI.Launcher/UpdateService.cs` (`ApplyAsync`, `Evaluate`, `RecoverIfNeededLockedAsync`) y `HostPackageInstaller.cs`; pruebas `tests/LoomLCI.Launcher.Tests/UpdateTests.cs`.

- [ ] Diseñar preflight que prohíba sobrescribir la versión activa y valide coherencia nombre de versión/`sequence`/rutas.
- [ ] Revisar promoción de staging con preservación recuperable de versiones previas.
- [ ] Persistir estado de recuperación **antes de la primera mutación riesgosa** y hacerlo consistente con la recuperación por etapa.
- [ ] Inyectar fallos en descarga/extracción/staging/journal/promoción/stop/start y verificar integridad de original/rollback.
- [ ] Preservar firma ECDSA, SHA-256, límites, `highestSequence` anti-rollback y compatibilidad de protocolo.
- [ ] Validar E2E con un entorno aislado; no ejecutar el propio actualizador dentro del árbol LoomLCI que debe detener.

**Aceptación:** interrupción en cada etapa ensayada permite recuperar un runtime verificado; jamás se borra la versión activa antes de contar con protección suficiente; no hay activación de contenido no validado.

## RB-03 — Fallos de limpieza de recursos

**Hallazgo:** R-01. **Evidencia:** análisis estático; `DisposeEntryAsync` puede devolver error y dejar el recurso activo, mientras `TransitionOwnedAsync` ignora el resultado y `WorkSessionManager.CloseAsync` puede informar éxito.

**Código:** `src/LoomLCI.Core/Resources/ResourceRegistry.cs`, `src/LoomLCI.Core/Work/WorkSessionManager.cs`; pruebas de Core Lifetime/WorkSession.

- [ ] Diseñar estado y política de reintento/reporte para fallos de disposer; preservar invariantes de recursos y sesiones.
- [ ] Propagar correctamente errores en `CloseOwnedAsync`/`ExpireOwnedAsync`/`CloseAsync`; evitar confirmaciones falsas.
- [ ] Agregar pruebas con disposer que falla, reintentos, cancelación concurrente y limpieza múltiple (una falla no debe ocultar las demás).
- [ ] Verificar ausencia de fugas y la semántica de tombstones/expiry.

**Aceptación:** el llamador puede distinguir cierre completo de parcial/fallido; ningún recurso fallido queda sin ruta de observación o recuperación; pruebas de regresión exitosas.

## RB-04 — Cutover externo y seguro

**Hallazgo:** U-02, incidente documentado de Release 6. **Evidencia:** `Independent` respecto de WorkSession no implica independencia respecto de Job Objects antecesores; el primer `update apply` iniciado desde el entorno productivo se interrumpió al detenerlo; el segundo, desde IvanSpace, finalizó.

**Fuentes:** [[A4.1 - Release 6 desplegada]], `src/LoomLCI.Windows/Process/WindowsJobObject.cs`, [[Deployment portable]], [[Auto-update firmado]].

- [ ] Formalizar runbook para `stop/update apply/rollback/start/status` desde un supervisor **fuera del árbol del Host productivo**.
- [ ] Evaluar si basta el procedimiento externo (IvanSpace como fallback/cutover) o merece incorporarse un mecanismo seguro en Launcher, sin confundir ownership lógico con escape de Job.
- [ ] Probar escenarios de cierre del Host/túnel y recuperación, manteniendo el rollback verificado.
- [ ] Documentar explícitamente qué debe ejecutar ChatGPT/LoomLCI y qué **no** debe autoejecutarse desde el runtime que se apaga.

**Aceptación:** un cutover real no mata al supervisor, el resultado de instalación se puede verificar tras `stop` y existe recuperación repetible si falla.

## RB-05 — Desinstalación y preservación de datos

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
