# Instalador Windows

> Estado: **CERRADO end-to-end (2026-10-06).** LoomLCI ya dispone de un instalador Windows convencional por usuario, registrado en Aplicaciones instaladas, con uninstall real y sin duplicar la lógica de configuración del Launcher.

## Objetivo

Agregar una capa de instalación Windows convencional sobre [[Deployment portable]], manteniendo el portable como fuente de verdad del payload y `SetupService` como única implementación de configuración de máquina, tunnel, secret, profile y doctor.

## Diseño final

- toolchain de build fijado en **Inno Setup 7.1.0**;
- script Inno: `installer/LoomLCI.iss`;
- builder: `scripts/Build-WindowsInstaller.ps1`;
- el builder genera primero el portable en staging temporal, valida el payload y luego lo empaqueta en un único `LoomLCI-<version>-win-x64-setup.exe`;
- instalación **per-user**, `PrivilegesRequired=lowest`, sin UAC/admin por defecto;
- AppId estable: `LoomLCI.38a7c958-cc59-44ed-9f58-29310a508c16`;
- registro estándar en HKCU / Aplicaciones instaladas con nombre visible `LoomLCI` y versión separada;
- el instalador solicita Tunnel ID y Runtime API key, pero no reimplementa configuración: ejecuta el Launcher extraído en staging con `setup`;
- la runtime key no se pasa por command line: el wizard la materializa temporalmente y el Launcher la copia a su secret user-only;
- los accesos directos siguen siendo creados por `SetupService`, por lo que no existen dos implementaciones de shortcuts;
- no se inicia ni detiene automáticamente otro runtime durante Setup; el cutover sigue siendo explícito.

El builder requiere Inno Setup 7.1.0 ya disponible en la máquina de desarrollo. Puede localizarlo en la instalación per-user/per-machine o mediante `LOOMLCI_ISCC`; una versión distinta se rechaza.

## Uninstall

El uninstall convencional:

1. intenta detener el runtime instalado usando `LoomLCI.Launcher.exe stop` con los roots correctos;
2. elimina `LoomLCI.lnk` y `Detener LoomLCI.lnk`;
3. elimina versiones, tools y Launcher instalados;
4. elimina el root administrado `%LOCALAPPDATA%\LoomLCI`, incluyendo:
   - `deployment` y la runtime key;
   - profiles/state/logs;
   - CPython privado bajo `runtimes\python`;
   - assets privados como el worker Python;
5. elimina la entrada de Aplicaciones instaladas.

No borra el tunnel remoto ni la configuración de la app MCP de ChatGPT.

## Validación aislada

Se compiló un installer E2E con AppId y roots temporales independientes de la instalación real:

- install root: `%LOCALAPPDATA%\Temp\LoomLCI-Installer-E2E\install`;
- Loom root: `%LOCALAPPDATA%\Temp\LoomLCI-Installer-E2E\loom`;
- `/NOSHORTCUTS=1` para no tocar el escritorio real;
- instalación silenciosa: exit code **0**;
- Launcher, Host, tunnel-client y `machine.json`: presentes;
- `activeVersion=0.1.0-dev-installer-e2e`;
- secret presente con DACL protegida y una sola regla explícita;
- entrada HKCU de uninstall creada con `DisplayName=LoomLCI`;
- Launcher aislado reportó runtime detenido/no registrado, sin interferir con el runtime real;
- uninstall silencioso: exit code **0**;
- install root, Loom root y clave de uninstall: eliminados completamente.

Una primera prueba de uninstall mediante `process_run` dejó sólo `unins000.exe`. No era un bug del installer: `process_run` cierra el Job Object al terminar el root process y puede matar el helper de auto-borrado de Inno. Repetido mediante `process_start`, el helper sobrevivió lo necesario y el directorio desapareció por completo.

## Validación final

- suite Release completa: **272/272** = Core 96 + Windows 141 + MCP 5 + PdfWorker 6 + Launcher 9 + Integration 15;
- installer final: `artifacts/installer/LoomLCI-0.1.0-dev-installer-win-x64-setup.exe`;
- tamaño observado: **59.710.907 bytes**;
- SHA-256: `d2fb515704f5d590017240f23857b8accd40ae811ebae865a88eb316c259b93b`;
- instalación real sobre el deployment existente: exit code **0**;
- `machine.json`: `activeVersion=0.1.0-dev-installer`, `previousVersion=0.1.0-dev-launcher-ux`;
- Aplicaciones instaladas: `LoomLCI`, versión `0.1.0-dev-installer`, uninstall bajo `%LOCALAPPDATA%\Programs\LoomLCI\unins000.exe`;
- shortcuts reales preservados con `start --pause` / `stop --pause`;
- cutover realizado con IvanSpace únicamente para `stop/start`;
- runtime final: `0.1.0-dev-installer`, `process_running=true`, `healthy=true`, `ready=true`;
- smoke directo ChatGPT -> LoomLCI posterior al cutover: OK;
- IntegrationTests contra la **DLL instalada**: **15/15**.

## Fuera de alcance de esta etapa

- auto-update y rollback automático;
- code signing / publisher confiable;
- MSI/MSIX;
- Windows Service;
- autoarranque al login;
- win-arm64.

El follow-up **Producto / Deployment 3** quedó cerrado en [[Auto-update firmado]]. El siguiente bloque es **Producto / Deployment 4: generación/verificación de metadata y skill del plugin**.

## Toolchain / licencia

Inno Setup 7.1.0 se usa como herramienta de build y el setup generado es actualmente para uso de desarrollo/personal. Si LoomLCI pasa a distribución comercial, revisar explícitamente los términos/licenciamiento de Inno Setup antes de publicar.
