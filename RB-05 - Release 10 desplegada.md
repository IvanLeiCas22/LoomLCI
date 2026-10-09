---
tipo: despliegue
proyecto: LoomLCI
fecha: 2026-10-09
bloque: RB-05
estado: desplegado_validado_productivo
sequence: 10
---

# RB-05 — Release 10 desplegada

## Resultado

**DESPLEGADO Y VALIDADO** con el instalador real de Inno Setup 7.1.0 y supervisor externo **IvanSpace**, iniciado fuera de `LoomLCI.Host.exe`. El proceso supervisor devolvió exit 0, con marcador **`CUTOVER_OK`**; tiempo del cutover **19,593 s**.

- Versión activa: **`0.1.0-dev-95462385fbc6`**, sequence **10**, construida desde commit `9546238`.
- Rollback disponible: **`0.1.0-dev-5f5cea9ecbf4`**, sequence **9**.
- El mismo Secure MCP Tunnel/alias `loomlci-installed` reporta `process_running=True`, `healthy=True`, `ready=True`.
- AppId productivo sin cambios: `LoomLCI.38a7c958-cc59-44ed-9f58-29310a508c16`. Windows registra la nueva versión y existe **un solo** `unins000.exe`.
- Backup externo creado **antes de detener**: `%LOCALAPPDATA%\LoomLCI-CutoverBackups\20261009T051236Z-793bd183d9f24768819bf3f6702ef602`; incluye Launcher anterior, machine.json anterior, metadata y `cutover.log`. No es una restauración automática de secretos: no restaurar config a ciegas.
- **NO se ejecutó el uninstall ni la purga productivos**, ni rollback destructivo. Su comportamiento se validó antes mediante E2E aislado `RB05_GENUINE_E2E_PASS` (véase [[RB-05 - Desinstalación segura y preservación de datos]]).
- No se publicó GitHub Release, no se hizo push, no se cambió el plugin privado ni se habilitó actualización automática.

## Paquete y autenticidad

- Generado con `scripts/Build-WindowsInstaller.ps1 -OutputRoot .\artifacts\installer-rb05-seq10 -Version 0.1.0-dev-95462385fbc6 -Sequence 10 -SkipPortableZip`, **sin omitir pruebas**. Comando de build exit 0; `RB05_SEQ10_PACKAGE_OK`.
- Instalador: `artifacts/installer-rb05-seq10/LoomLCI-0.1.0-dev-95462385fbc6-win-x64-setup.exe`
- SHA-256 instalador: `9a89332ac36edfdf3cc7c3bee0b338cbde7773b88d7ccca92a581f0f679ec971`.
- SHA-256 Launcher publicado y verificado instalado: `6c1cb056446993cf0666206eb20b5e86df617fd78d9a3b23241f2342919d4c9a`.
- SHA-256 Host publicado y verificado instalado: `62a1583e94cbd981e30a6e7e6fa81fc37097ab33784ebf17f1f81782ba6ad8dd`.
- Contratos MCP prevalidación PASS; **57/57 Launcher**, **21/21 IntegrationTests contra Host publicado**. Suite Release fuente anterior al despliegue **437/437**; commit de fuente sin cambios.
- `Invoke-SafeCutover.ps1 -Mode Preflight` desde IvanSpace: **exit 0**, metadata/rutas/versiones/sequence/hash coherentes y ausencia de journal de recuperación. `-Mode Execute -ConfirmExternalSupervisor`: backup, stop exit 0, installer exit 0, hash del binario instalado, verificación de previous, start exit 0, status `healthy/ready` y **`CUTOVER_OK`**.

## Smoke independiente post-cutover

- IvanSpace siguió disponible después del stop/start; verificó por separado `Launcher status` **seq10 healthy/ready**.
- Hashes de ambos shortcuts del escritorio sin cambios respecto del baseline.
- Hashes de 3 archivos vigilados, incluyendo credencial productiva y datos de prueba, sin cambios (no se imprimió ninguna clave).
- Persistencia de los 8 directorios compartidos observados: `assets`, `http-test`, `packages`, `runtimes`, `secrets`, `staging`, `tools`, `tunnel-profiles`.
- `machine.json`: active seq10, previous seq9, highest seq10; existe el marcador de ownership del `deployment` con roots correctos.
- Binarios del Launcher/Host instalados coinciden con los SHA-256 exactos del paquete; Host anterior disponible para rollback.
- Único uninstaller registrado en Windows, `DisplayVersion` coincide con target.
- Reconexión ChatGPT → **LoomLCI MCP productivo**: WorkSession nueva creada, `python_execute` importó `loom`, `loom.fs`, `loom.process`, verificó **16 capacidades**, ejecutó `loom.process.run('git.exe', ['status','--short'])` con exit 0 y repo limpio.

## Política RB-05 ahora instalada

`[UninstallDelete]` **no elimina** `%LOCALAPPDATA%\LoomLCI` ni sus datos compartidos. `UninstallLogMode=overwrite` sustituye el registro heredado en actualización de arquitectura/AppId compatibles. `InitializeUninstall` detiene el runtime y falla de forma cerrada ante fallo de stop. Los accesos directos sólo se eliminan con evidencia de propiedad; los accesos anteriores se conservaron intactos mediante `/NOSHORTCUTS=1` durante el cutover. La purga `purge-data --confirm-erase-deployment` es opt-in y **no se ejecutó**.

La validación productiva fue **no destructiva**; no afirmar que se ensayó la desinstalación de la instalación real. Pruebas destructivas sólo en instalaciones aisladas previas.

## Estado posterior

- RB-05 puede marcarse **cerrado para el alcance desplegado y validado**.
- Siguiente secuencia de instalación/auto-update local **>=11**, no reutilizar 10 con otros bytes.
- Próximo bloque del mini-roadmap: **RB-06 observabilidad durable opt-in**. Computer H1 y releases automáticas siguen diferidos.
- Procedimiento habitual: el asistente ejecuta compilación/validación y usa supervisor externo para cutover; el usuario conserva aprobación.
