---
tipo: implementacion
proyecto: LoomLCI
bloque: RB-04
fecha: 2026-10-08
estado: codigo_validado_pendiente_cutover_real
---

# RB-04 — Supervisor externo y cutover seguro

> **Fuente implementada y validada sin cortar el runtime productivo.** El producto sigue en Release 6 `0.1.0-dev-6b1566a` (sequence 6). RB-01, RB-02 y RB-04 están pendientes de instalación/smoke final, NO cerrados end-to-end.

## Causa y decisión

El incidente del primer cutover de Release 6 confirma que `Independent` a nivel de WorkSession **no** implica escape de los Windows Job Objects con `KILL_ON_JOB_CLOSE`. El propio runtime que se detiene no puede supervisar de forma fiable su reemplazo.

**Decisión:** supervisor externo, normalmente la instancia de IvanSpace iniciada por el Programador de tareas de Windows, sin ampliar `process_start` para permitir romper Jobs. Un proceso iniciado desde LoomLCI, aunque sea `Independent`, está prohibido para el cutover productivo. La prueba de ancestría del script es una defensa adicional, no una prueba absoluta sobre todos los Job Objects.

## Cambios de fuente

1. `UpdateTrust.SupportedProtocol = 2`.
2. `Build-PortablePackage.ps1`: `package.json.updateProtocol = 2`.
3. `Build-UpdateRelease.ps1`: `package.json.updateProtocol = 2` y manifest firmado `minUpdateProtocol = 2`. Un Launcher viejo con `SupportedProtocol = 1` debe responder `RequiresNewInstaller` y rechazar `update apply` antes de descargar/publicar.
4. `UpdateService.ValidatePackageMatchesRelease` rechaza un paquete con `updateProtocol < release.minUpdateProtocol`, además de los chequeos existentes de versión, sequence, plataforma y protocolo máximo.
5. `Build-WindowsInstaller.ps1` produce `LoomLCI-<version>-win-x64-setup.deployment.json` al lado del EXE, con SHA-256 del instalador, versión, sequence y raíces exactas incorporadas por Inno Setup. Usa el marcador `{localappdata}` cuando la ruta es relativa al usuario destino. No guarda secretos.
6. `scripts/Invoke-SafeCutover.ps1`: **Preflight** sólo lectura por defecto; **Execute** exige `-ConfirmExternalSupervisor`, verifica la ancestría, el SHA-256 del instalador, identidad/sequence/rutas mediante el metadata, versión instalada y ausencia de journal, existencia del Host y archivo de credencial, y los SHA-256 previstos del Launcher/Host de destino.
7. Ejecuta `stop` / Inno Setup / `start` / `status` desde el supervisor externo, con validación de versión, túnel, healthy, ready y process_running. El instalador recibe sólo **la ruta** de la key por `/RUNTIMEKEYFILE` y `/NOSHORTCUTS=1` para preservar accesos directos. El script almacena copia de Launcher y machine.json, metadata y log en una carpeta de respaldo **externa al árbol de instalación**; no guarda el valor secreto.
8. Frente a fallo evita restaurar machine.json a ciegas. Si la configuración sigue original, intenta arrancar la misma versión. Si cambió, conserva backup y requiere diagnóstico/rollback supervisado (RB-02).
9. `Test-SafeCutoverPreflight.ps1` y `Test-SafeCutoverExecute.ps1`: pruebas aisladas con ejecutables/datos ficticios en TEMP. **Nunca ejecutan el instalador contra la PC productiva**.

## Ensayos ejecutados

- Launcher Release **53/53**, con tres regresiones de protocolo 2.
- Suite Release completa **423/423**: Core 141, Windows 196, Launcher 53, MCP 6, PDF 6, Integración 21.
- Preflight aislado **7/7**: válido; digest alterado; versión vieja inesperada; metadata con otra ruta; metadata SHA incongruente; Execute sin confirmación; journal pendiente.
- Cutover aislado desde IvanSpace externo, con binarios mock: stop -> mock installer -> verificación SHA y previous/active -> start -> status -> backup/log. **Exit code 0, marcador RB04_EXTERNAL_ISOLATED_EXECUTE_OK.** Los binarios y el config viven en TEMP, se eliminan al terminar.
- Builder real de Inno Setup 7.1.0 generó un EXE para rutas aisladas con versión `0.1.0-dev-rb04-build-test` y sequence 777; el ensayo validó SHA, ruta de instalación, ruta de datos y versión/sequence de su archivo deployment.json. **Exit code 0.** No se ejecutó el instalador real ni se cambió la instalación productiva.
- Se confirmó que el script y sus dos test runners funcionan en PowerShell de Windows. No requieren plugin nuevo ni herramientas MCP nuevas.

## Evidencia adicional — interrupción real e instalador auténtico (2026-10-08)

- **Kill de proceso real 11/11:** `tests/LoomLCI.Launcher.CrashHarness` ejecutó `UpdateService` del producto en un proceso .NET separado. `scripts/Test-RealLauncherCrashRecovery.ps1`, iniciado desde IvanSpace externo, esperó un checkpoint de journal persistido, aplicó `Stop-Process -Force` al proceso hijo y arrancó una instancia **nueva** para recuperar el estado. Se verificaron los checkpoints `prepared`, `promoting`, `backed_up`, `published`, `stopping`, `runtime_stopped`, `config_saved`, `activated`, `runtime_started`, `committed` y un segundo crash en `restored_files` durante el rollback. **Exit code 0, `RB04_REAL_PROCESS_KILL_RECOVERY_OK (11/11)`.** Se eliminaron los directorios temporales de prueba. El runtime fue un stub de archivos; firma ECDSA P-256 efímera de prueba, no feed productivo; no se probó health real del tunnel-client.
- **Instalador Inno Setup auténtico ejecutado desde IvanSpace:** `scripts/Test-GenuineIsolatedInstaller.ps1` compiló Host/Launcher, generó un Inno Setup 7.1.0 con `AppId`, installation root, data root y `sequence` únicos **bajo TEMP**, usó un tunnel ID y key ficticios, y verificó SHA-256 de Launcher/Host contra el portable construido.
- **Defecto real encontrado:** en modo `/VERYSILENT /SUPPRESSMSGBOXES`, la configuración `CurStepChanged(ssPostInstall)` falló con exit 1 del Launcher, pero Inno Setup **devolvió exit 0** y dejó `machine.json` ausente. Se corrigió con `ConfigurationExitCode := 100` antes de `ConfigureLoomLCI()`, reset a 0 solamente en éxito, y `GetCustomSetupExitCode`. Repetición: setup informó **exit 100**, la ausencia de config fue tratada como fallo esperado de credenciales de prueba, y la integridad de binarios pasó.
- **Efecto colateral detectado y remediado:** `[UninstallDelete]` borraba los accesos directos del escritorio por nombre **incluso al desinstalar una instalación temporal con `/NOSHORTCUTS=1`**. Se restituyeron `LoomLCI.lnk` y `Detener LoomLCI.lnk` para Release 6, verificando target/argumentos originales. Se eliminaron **únicamente esas dos líneas destructivas**, como protección puntual hasta [[Roadmap de robustez post-auditoría|RB-05]]. En la última repetición el uninstaller aislado devolvió exit 0 y se verificó **`RB04_DESKTOP_SHORTCUTS_UNCHANGED`** con SHA-256 de ambos archivos antes y después.
- **Resultado final del test del instalador:** build Inno real OK, setup con credencial ficticia **exit 100**, hash de binarios OK, `machine.json` no publicado, uninstall aislado **exit 0**, shortcuts productivos intactos, script padre **exit 0**. Esto es una **prueba satisfactoria del camino de fallo genuino y su limpieza**, no un E2E de alta de un túnel válido ni de conectividad.
- **Suite después de los cambios:** `dotnet test LoomLCI.slnx -c Release --no-restore --verbosity quiet` **423/423** (21/21 integración). El crash-harness es un proyecto de prueba independiente de la solución, no se incluye dentro de esas 423.

## E2E con túnel real independiente — validado (2026-10-08)

**Resultado: `RB04_LIVE_TUNNEL_ISOLATED_E2E_OK`, supervisor externo IvanSpace, exit 0.**

- Se reutilizó el registro remoto del anterior experimento `LoomLCI HTTP Test` y su Runtime API key local conservada con ACL de usuario. No se leyó ni se registró el valor de la clave en el chat ni en Git. El tunnel remoto es distinto del productivo; el plugin HTTP anterior sigue desinstalado.
- Se implementó `scripts/Test-LiveIsolatedTunnelE2E.ps1`: crea `AppId` Inno, install/data roots y versiones **únicos en TEMP**; clona sólo el binario oficial `tunnel-client` (SHA-256 comprobado), construye dos instaladores reales con sequence **701 y 702** y verifica metadatos/hashes; usa el secreto de prueba únicamente por referencia `/RUNTIMEKEYFILE`.
- **Instalación inicial auténtica + conexión real:** `BUILD_OK r4-live-a-3b2eea2c14da`, `GENUINE_SETUP_OK`, `HEALTH_OK` y `INSTALL_AND_CONNECT_OK`; `tunnel-client` reportó `healthy/ready/process_running=True` e identidad esperada del túnel de prueba.
- **Actualización supervisada:** el segundo instalador genuino `r4-live-b-3b2eea2c14da` pasó el `Invoke-SafeCutover -Mode Preflight`; el supervisor **IvanSpace** realizó `stop -> Inno Setup -> hash/version/previous -> start -> status`, marcadores `SUPERVISED_CUTOVER_OK` y `HEALTH_OK` de la segunda versión. El supervisor sobrevivió a la detención real del runtime aislado.
- **Rollback real:** el Launcher recuperó la primera versión, volvió a `healthy/ready` (`HEALTH_OK r4-live-a-3b2eea2c14da`), la detuvo y registró `ROLLBACK_AND_STOP_OK`.
- **Limpieza:** `CLEANUP_UNINSTALL_EXIT=0`, `DESKTOP_SHORTCUTS_UNCHANGED`; el script padre cerró **exit 0**, borró el árbol de instalación temporal y conservó un log operacional **sin claves** en `%TEMP%\\LoomLCI.RB04.Evidence.3b2eea2c14da.log`.
- **Error recuperado durante preparación:** el primer intento llegó a `GENUINE_SETUP_OK` pero un harness utilizaba `$args` (variable automática PowerShell) como parámetro propio. Esto ejecutaba `start` por defecto en vez de `status` y provocaba un **falso `Not healthy`**; tras corregirlo a `$launcherArgs`, el ensayo E2E completo pasó. No hay evidencia de fallo de disponibilidad del túnel.

**Alcance:** conexión/runtime reales y dos instaladores auténticos bajo supervisión externa, sin sustituir la instalación productiva. No se reinstaló el plugin de prueba de ChatGPT ni se ejecutó un smoke remoto desde ese plugin (no es necesario para la independencia del cutover local). Sigue pendiente el **cutover productivo aprobado** y su smoke desde ChatGPT, por lo que RB-01/RB-02/RB-04 aún no están cerrados end-to-end.

## Runbook de cutover productivo futuro (NO EJECUTAR TODAVÍA)

1. **Ya realizados:** kill real 11/11 e instalación genuina aislada del **camino de error** con credenciales ficticias. **Completado:** E2E con túnel **válido y separado del productivo**, corte/reinicio y supervivencia de IvanSpace ante la detención del Host aislado. Pendiente solamente desplegar y verificar la Release productiva, sin sustituir el smoke remoto desde ChatGPT.
2. Tras autorizar una release, construir setup y paquete con **nombre/version únicos** y siguiente sequence. Generar también release firmada `minUpdateProtocol=2` para futuros updates, y verificar explícitamente firma, digests y correspondencia de artefactos.
3. Revisar `setup.deployment.json`, SHA-256 del setup, SHA-256 de los ejecutables Launcher/Host extraídos del portable, y que las rutas reales coincidan con las del instalador.
4. Ejecutar primero `Invoke-SafeCutover.ps1 -Mode Preflight` de sólo lectura. Proveer `-InstallerPath`, `-InstallerSha256`, `-TargetVersion`, `-TargetSequence`, `-ExpectedCurrentVersion`, `-ExpectedCurrentSequence`, y hashes previstos. El metadata debe estar junto al EXE. Usar `-InstallRoot`/`-DataRoot` sólo si se compiló un instalador explícitamente para esas rutas.
5. Ejecutar **solamente desde IvanSpace externo**, no desde Loom, con las mismas opciones y `-Mode Execute -ConfirmExternalSupervisor`. Si falla, seguir el log/respaldo; no desinstalar ni sobrescribir el config a ciegas. La ejecución del instalador real puede cortar temporalmente la conexión del chat.
6. Verificar desde ese supervisor la identidad, SHA, active/previous, journal ausente, `healthy/ready` y túnel; reconectar ChatGPT y ejecutar smoke real de RB-01/RB-02. No lanzar después `update apply` de esa **misma release**, porque el instalador ya actualiza Host y Launcher.
7. Sólo tras esos chequeos declarar RB-01/RB-02/RB-04 CERRADOS end-to-end. Los commits RB-01 y RB-02 siguen en main local mientras no se pushean/publíquen.

## Pendientes para cierre

- [x] Diagnóstico de Jobs y definición de supervisor externo.
- [x] Protocolo 2 y pruebas de compatibilidad de release firmada.
- [x] Preflight con hashes, metadata de rutas y bloqueo de operaciones inseguras.
- [x] Backup de Launcher/machine.json y log en carpeta separada; conservar shortcuts.
- [x] Simulación completa aislada supervisada por IvanSpace y builder real sin ejecutar setup.
- [x] Ensayo de instalador **auténtico** y fail-closed con credenciales ficticias, archivos verificados por SHA-256; error exit 100 y uninstall seguro, sin tocar runtime productivo.
- [x] E2E con **túnel válido independiente del productivo**, instalación inicial + health real, actualización supervisada y rollback real end-to-end.
- [x] Kill del **proceso real del harness .NET** que ejecuta UpdateService v2 y recuperación desde un segundo proceso **11/11** (runtime stub).
- [x] Detención real de runtime/túnel aislados con supervisor externo IvanSpace superviviente, upgrade y rollback saludables. Los 11 kill-forzados de proceso cubren separadamente journal/recuperación.
- [ ] Publicación/instalación productiva supervisada; smoke de 25 tools MCP/16 capacidades bridge, rollback y nuevo Launcher.
- [ ] Resolver RB-05 en su propio bloque; no usar uninstall para recuperación.

**Garantía real:** se comprueba origen del proceso por ancestría, pero los Jobs anidados requieren una prueba de supervivencia real, no se infiere su independencia completa por `ProcessId` o por `Independent`. Además del mock, el E2E con Inno Setup y tunnel-client auténticos demostró el flujo completo en un túnel de prueba separado. No equivale a demostrar el smoke de la siguiente release productiva.
