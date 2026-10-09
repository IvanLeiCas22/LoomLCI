---
tipo: despliegue
proyecto: LoomLCI
fecha: 2026-10-09
bloque: RB-06
estado: cerrado_end_to_end_desplegado_productivo
sequence: 11
---

# RB-06 — Release 11 desplegada

## Resultado productivo

**DESPLEGADO Y VALIDADO el 2026-10-09:** `0.1.0-dev-fc4373bdff73`, **sequence 11**. El instalador real de Inno Setup 7.1.0 fue ejecutado con IvanSpace como **supervisor externo** al Host productivo. Resultado **`CUTOVER_OK`**, proceso supervisor **exit 0** (10,718 s), instalador **exit 0**; Host nuevo `process_running=True`, `healthy=True`, `ready=True`. Alias y túnel productivo preservados.

- **Rollback**: `0.1.0-dev-95462385fbc6` (sequence **10**), binario anterior comprobado disponible.
- Backup creado **antes de detener**: `%LOCALAPPDATA%\LoomLCI-CutoverBackups\20261009T054403Z-001526b1f7034035afa17b44af65db2e`. La carpeta contiene Launcher anterior, `machine.json` anterior, `metadata.json` y `cutover.log` con `CUTOVER_OK`; no restaurar configuración a ciegas.
- No se ejecutó uninstall, `purge-data`, rollback ni limpieza de logs productivos. No se hizo push ni se publicó un GitHub Release. No se modificó el plugin privado.
- **Diagnósticos desactivados por defecto**: `diagnostics status` reporta `enabled: False`, `files: 0`; no se creó `config/diagnostics.json` ni se generaron archivos de diagnóstico productivos. La prueba de `enable/tail/disable/clear` se efectuó **sólo con la versión publicada y una ruta temporal aislada**. Habilitar diagnósticos productivos es decisión posterior del usuario.

## Proveniencia del paquete

- Código fuente base: commit **`fc4373b`** (SHA corto de 12: `fc4373bdff73`). Commit de cierre documental posterior no altera bytes del paquete.
- Build: `scripts/Build-WindowsInstaller.ps1 -OutputRoot .\artifacts\installer-rb06-seq11 -PortableOutputRoot $env:TEMP\LoomLCI.RB06.Seq11.Portable -Version 0.1.0-dev-fc4373bdff73 -Sequence 11 -SkipPortableZip`. **Sin** `-SkipPortableTests`. Resultado `RB06_SEQ11_PACKAGE_OK`.
- `artifacts/installer-rb06-seq11/LoomLCI-0.1.0-dev-fc4373bdff73-win-x64-setup.exe`
- SHA-256 instalador: `4abe5adb0ce73dca5a136657e1bf0689a58d1a745aa2ee3ece369c09e1397efc`.
- SHA-256 Launcher publicado e instalado: `227acfae92536d6e791ea6ecd0f8bff61e04136d6b31f6791ee609f6fdfb7888`.
- SHA-256 Host publicado e instalado: `879754df6569b865041f7c8256ba775c6d971d2d766b3797c2d61aecad6df4a3`.
- Validación fuente previa: **448/448** suite Release. Build final: contratos MCP PASS; Launcher **62/62**; integración MCP **23/23 contra Host publicado**. Inno Setup **7.1.0**, compilación final exit 0.
- Smoke aislado del Launcher publicado en data root temporal: `diagnostics status` sin crear logs, `enable`, `tail`, `disable`, `clear --confirm` exitosos (`RB06_SEQ11_ISOLATED_CLI_SMOKE_PASS`). Datos temporales eliminados.

## Despliegue y verificaciones posteriores

- Preflight read-only desde IvanSpace: exit 0, confirmó identidades `seq10 -> seq11`, SHA-256, metadata del instalador, rutas, secuencias monotónicas y journal de recuperación ausente.
- Cutover ejecutado **desde IvanSpace** (externo al Host, no desde proceso LoomLCI): estado inicial healthy/ready, backup durable, Launcher stop exit 0, Inno exit 0, hashes y previous comprobados, Launcher start exit 0, health/ready y `CUTOVER_OK`.
- Revisión independiente post-cutover desde IvanSpace: **SHA-256 de 2 accesos directos y 3 archivos vigilados sin cambios**, directorios compartidos preservados, `machine.json` con active 11/previous 10/highest 11 y marcador de ownership de deployment coherente. Ningún secreto fue mostrado ni persistido en evidencia.
- Binarios Launcher y Host instalados coinciden exactamente con SHA publicados. Host de rollback 10 existe. Windows conserva un único `unins000.exe` y la entrada de registro del instalador reporta la nueva versión.
- Config opt-in productiva ausente, cero logs y comando `diagnostics status` desactivado: **`RB06_DIAGNOSTICS_DEFAULT_OFF_OK`**.
- Backup externo y `cutover.log` comprobados: **`EXTERNAL_BACKUP_OK`**. Gate final `RB06_SEQ11_POSTCUTOVER_INTEGRITY_PASS`.
- Reconexión ChatGPT → **LoomLCI MCP productivo nuevo**: WorkSession recién creada; `python_execute` importó `loom`, `loom.fs`, `loom.process`, comprobó **16 capacidades** y ejecutó `git status --short` a través del bridge con exit 0 y salida vacía. Gate `RB06_SEQ11_FRESH_MCP_PYTHON_16_CAPABILITIES_PASS`.

## Funcionalidad disponible (opt-in, desactivada)

```text
LoomLCI.Launcher.exe diagnostics status
LoomLCI.Launcher.exe diagnostics enable
LoomLCI.Launcher.exe diagnostics tail 20
LoomLCI.Launcher.exe diagnostics disable
LoomLCI.Launcher.exe diagnostics clear --confirm
```

Política detallada en [[RB-06 - Observabilidad durable y reconciliacion documental]]: JSONL local, filtros allowlist, datos sensibles excluidos, cola auxiliar independiente, rotación 4 MiB, 7 días y presupuesto 32 MiB. Los eventos son best-effort; no son un rastro de auditoría imposible de perder ni evidencia de los contenidos de comandos.

## Estado final

- **RB-01 a RB-06 completados para el alcance acordado**; RB-06 se considera cerrado **end-to-end** por build, tests, preflight, despliegue y smoke productivo.
- **Diagnósticos no activados en producción**: esto es intencional por diseño opt-in; no confundir instalación de la capacidad con su habilitación.
- Siguiente secuencia local de upgrade: **>=12**. Computer H1 y publicación/automatización de releases permanecen diferidos. El workflow habitual continúa con ejecución del asistente y aprobación del usuario.

Referencias: [[RB-05 - Release 10 desplegada]], [[RB-06 - Observabilidad durable y reconciliacion documental]], [[Roadmap de robustez post-auditoría]], [[DX-01 - Optimizacion workflow de desarrollo y despliegue]].
