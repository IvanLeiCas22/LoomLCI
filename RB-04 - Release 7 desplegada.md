---
tipo: deployment
proyecto: LoomLCI
fecha: 2026-10-08
version: 0.1.0-dev-173b4ffc00f6
sequence: 7
estado: cerrado_end_to_end
bloques: [RB-01, RB-02, RB-04]
---

# RB-04 — Release 7 desplegada y validada

## Resultado operativo

**Release 7 `0.1.0-dev-173b4ffc00f6` (sequence 7) instalada, activa y `healthy=true` / `ready=true`, sobre el túnel productivo ya existente.** La anterior es **Release 6 `0.1.0-dev-6b1566a` (sequence 6)**, conservada para rollback. El cambio se realizó desde **IvanSpace externo**, NO lanzando un proceso supervisor dentro del Host que se iba a detener.

**RB-01 / RB-02 / RB-04: CERRADOS end-to-end para el alcance desplegado.** RB-03, RB-05 y observabilidad durable RB-06 permanecen pendientes. Publicación automática, distribución del feed firmado v2 y Computer H1 siguen fuera de alcance.

## Fuente y preflight

- Árbol fuente limpio sobre `main`, commit `173b4ffc00f6` (los cinco commits correctivos seguían por delante de `origin/main` al compilar).
- `dotnet test LoomLCI.slnx -c Release --no-restore --verbosity quiet`: **423/423 PASS**, 0 fallos, 0 omitidas; incluye Launcher **53/53**, Windows **196/196**, Core **141/141**, Integración **21/21**, MCP **6/6**, PDF **6/6**.
- Instalador genuino Inno Setup 7.1.0 y portable generados para sequence 7 y **update protocol 2**. Archivo de metadata del instalador verificado por SHA-256, versión, secuencia y rutas. No se publicó una nueva GitHub Release ni un manifest remoto firmado.
- Staging externo (mantener por ahora):
  `%LOCALAPPDATA%\LoomLCI-RB04-Staging\release7-173b4ffc00f6`
- SHA-256 setup: `C7EC50D57F6496314A74EF7693EDD3EF7745A3504590EF3F10A63302DE0284CC`
- SHA-256 Launcher: `460ADFA630487F76734E5F2943B2A29D3A0CDBC056AB816C5F8378EA5121184E`
- SHA-256 Host: `31DB4BF0E70B0C9910EC3E2E024D3FB4795ED846696918C8BC5D4A6622B90C13`
- Preflight `Invoke-SafeCutover.ps1 -Mode Preflight`, ejecutado desde IvanSpace: **exit 0**, comparación exacta **Release 6/6 → Release 7/7**, rutas y digests OK, sin journal pendiente, secret user-only existente.

## Cutover supervisado productivo

Supervisor: `scripts/Invoke-SafeCutover.ps1 -Mode Execute -ConfirmExternalSupervisor`, invocado **desde IvanSpace**.

1. Confirmó Release 6 `healthy/ready`, versión e identidad del túnel.
2. Guardó **Launcher anterior, machine.json, metadata y registro de auditoría** en:
   `%LOCALAPPDATA%\LoomLCI-CutoverBackups\20261009T013103Z-26c8564be883461097ed825267055b1c`
3. Detuvo el runtime antiguo (`stop` exit 0), ejecutó el instalador genuino (exit 0), validó version/sequence + hashes de Host/Launcher y preservación de previous.
4. Arrancó Release 7 (`start` exit 0) y obtuvo status exit 0 con `process_running=True`, `healthy=True`, `ready=True`, tunnel correcto y `previous=Release 6`.
5. Resultado del supervisor: **`CUTOVER_OK` y exit 0**. IvanSpace sobrevivió al corte y la conexión ChatGPT se restableció.

## Smoke desde ChatGPT normal sobre la instalación nueva

- **25 herramientas MCP** disponibles; `work_create` y `work_close` operativas.
- `filesystem_read_files` leyó `global.json`; `process_run` ejecutó `git status --short --branch` con exit 0.
- Python persistente: `import loom, loom.fs, loom.process`, `len(loom.capabilities()) == 16`; persistencia entre dos `python_execute` y `loom.process.run('git.exe', ['--version'])` exit 0.
- Work Plan: `work_plan_update` revision 1, `work_plan_patch` CAS revision 2 e ID estable.
- **RB-01 (smoke atomicidad):** lote de dos reemplazos con segundo inválido rechazado; se confirmó que **ninguno** de los dos archivos temporales cambió. Lote válido de dos reemplazos aplicado completo; archivos limpiados.
- **RB-01 F-01:** `loom.fs.apply_patch` rechazó fichero con **UTF-8 inválido**, preservando bytes originales. **F-02:** rechazó reemplazo con salida de **16 MiB + 1 byte**, preservando original. Carpetas temporales eliminadas.
- **RB-02:** sin `journal.json` pendiente, `activeSequence=7`, `previousSequence=6`, `highestSequence=7`; Host de previous presente. La recuperación ante crash real y 11 puntos de transición, incluida restauración interrumpida, fue validada previamente **11/11** en un proceso .NET externo con runtime simulado; el E2E aislado adicional probó stop/upgrade/start/rollback de un túnel real independiente. No se provocó un crash productivo.
- **RB-04:** supervisor externo `CUTOVER_OK`, backup con audit log íntegro y binarios instalados coincidentes con los digests esperados. **Accesos directos `LoomLCI.lnk` y `Detener LoomLCI.lnk` conservados byte a byte (SHA-256 sin cambios).** Runtime key con ACL protegida.
- Postcheck independiente desde IvanSpace: **`RB04_POSTCUTOVER_INTEGRITY_OK`** y journal limpio.

## Límites y tareas posteriores

- No hacer `rollback` en producción como smoke destructivo: Release 6 queda instalada y recuperable; el rollback real ya pasó en un túnel de prueba separado.
- No se reclama durabilidad física absoluta ante cortes de luz. La arquitectura de journal y su recuperación han sido ensayadas en proceso real aislado; el instalador productivo realizó el salto de Launcher+Host, no `update apply` de la misma release.
- No se publicó todavía manifest/paquete **firmado** para Release 7 en GitHub; protocolo 2 está instalado y sus reglas cuentan con pruebas de compatibilidad. La automatización de publicación/actualización permanece diferida.
- **No se hizo push** de los commits locales ni se creó una GitHub Release. Conservar por ahora staging y carpeta de respaldo para contingencias.
- Sigue pendiente RB-03 (limpieza de recursos), RB-05 (uninstall y datos ajenos) y RB-06 (observabilidad).
