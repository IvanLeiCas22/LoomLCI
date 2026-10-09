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

## Runbook de cutover productivo futuro (NO EJECUTAR TODAVÍA)

1. Terminar ensayos pendientes con un **instalador auténtico en instalación aislada** y, para RB-02, interrupciones abruptas de un Launcher real. Comprobar que IvanSpace/Tarea Programada supervisan fuera de Jobs del Host a detener.
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
- [ ] E2E aislado **con instalador auténtico**, túnel/credenciales de prueba y sin interferir con el productivo.
- [ ] Kill abrupto del Launcher real durante transiciones del journal v2, recuperado en instalación aislada.
- [ ] Publicación/instalación productiva supervisada; smoke de 25 tools MCP/16 capacidades bridge, rollback y nuevo Launcher.
- [ ] Resolver RB-05 en su propio bloque; no usar uninstall para recuperación.

**Garantía real:** se comprueba origen del proceso por ancestría, pero los Jobs anidados requieren una prueba de supervivencia real, no se infiere su independencia completa por `ProcessId` o por `Independent`. El e2e mock demuestra la secuencia y protección de rutas, no equivalencia total con la ejecución real del Inno Setup y tunnel-client.
