# Auto-update firmado

> **Estado actual (2026-10-08): Release 7 DESPLEGADA con Launcher/Host de update protocol 2, sequence 7 y rollback Release 6 disponible.** Instalación productiva supervisada desde IvanSpace y smoke ChatGPT completados; ver [[RB-04 - Release 7 desplegada]] y [[RB-04 - Supervisor externo y cutover seguro]]. El **feed remoto firmado de una nueva GitHub Release 7 NO se publicó** ni se activó automatización de actualizaciones.

> **Actualización de robustez (2026-10-08):** la corrección de journal v2 [[RB-02 - Journal y promoción recuperable]] ya está desplegada en Release 7; tests Release **423/423**, kill real **11/11** en proceso .NET aislado y E2E con túnel de prueba válido completados. No interpretar recuperación validada mediante proceso/crash como garantía de durabilidad física absoluta ante corte eléctrico.

> **Cierre histórico del updater firmado original:** verificación de manifest/ZIP, descarga con progreso y reanudación, rollback. La mención a **Release 5** debajo corresponde al estado histórico anterior a Release 6/7. **La instalación vigente es Release 7 (sequence 7), healthy/ready; Release 6 disponible como rollback.** La publicación periódica/automática sigue diferida.

## Alcance final

- canal inicial: GitHub Releases público de `IvanLeiCas22/LoomLCI`;
- feed estable:
  - `releases/latest/download/loomlci-update.json`;
  - `releases/latest/download/loomlci-update.sig`;
- manifest firmado con ECDSA P-256;
- private key fuera del repo, bajo `%LOCALAPPDATA%\LoomLCI-ReleaseSigning`;
- public key embebida en el Launcher;
- package ZIP específico de Host con SHA-256 y tamaño esperados;
- `sequence` monotónico como anti-rollback;
- `minUpdateProtocol` para bloquear releases que requieran un Launcher más nuevo;
- el updater actualiza sólo el Host; actualizar el propio Launcher sigue requiriendo el instalador convencional;
- comandos públicos del Launcher:
  - `update check`;
  - `update apply`;
  - `rollback`.

## Transacción

El flujo de `update apply` quedó definido y validado como:

1. lock exclusivo del deployment;
2. leer/validar manifest firmado;
3. descargar ZIP acotado;
4. verificar tamaño + SHA-256;
5. extraer en staging con guards de path traversal/tamaño/entries;
6. validar `package.json`;
7. publicar `versions/<new>`;
8. persistir journal;
9. detener y confirmar el runtime anterior;
10. cambiar `machine.json` de forma atómica;
11. iniciar la nueva versión;
12. exigir `healthy=true`, `ready=true` y tunnel esperado;
13. eliminar journal;
14. conservar sólo `active + previous`.

Si falla después del stop, se restaura el config original y se vuelve a iniciar la versión conocida como buena. Si hay crash/corte durante la transacción, el próximo Launcher usa el journal para finalizar o recuperar determinísticamente.

## Versionado / rollback

`MachineConfig` conserva:

- `activeVersion` / `activeSequence`;
- `previousVersion` / `previousSequence`;
- `highestSequence`.

`highestSequence` no retrocede durante rollback, por lo que un feed firmado viejo no puede volver a introducir un release con sequence inferior al máximo ya alcanzado.

`rollback` usa el mismo motor transaccional, intercambiando active/previous sin descarga.

## Builders

`scripts/Build-UpdateRelease.ps1` genera:

- `LoomLCI-<version>-win-x64-update.zip`;
- `loomlci-update.json`;
- `loomlci-update.sig`.

`scripts/Build-WindowsInstaller.ps1` y `Build-PortablePackage.ps1` propagan también el sequence para que installer/portable y update package compartan la misma identidad lógica.

## Validación automatizada

Suite Release final:

- Core: **96/96**;
- Windows: **141/141**;
- MCP: **5/5**;
- PdfWorker: **6/6**;
- Launcher: **20/20**;
- Integration: **15/15**;
- total: **283/283**.

Los tests del Launcher cubren, entre otros:

- firma válida / manifest mutado;
- update exitoso;
- fallo de start con rollback automático;
- rollback explícito;
- anti-rollback por `highestSequence`;
- firma inválida;
- lock concurrente;
- compatibilidad con `machine.json` legacy;
- crash recovery antes/después de start;
- reaplicación después de rollback.

## Validación local previa

Antes de GitHub se validó un feed HTTP loopback firmado:

- runtime base instalado: `0.1.0-dev-autoupdate-base`, sequence 1;
- update local: `0.1.0-dev-local-e2e`, sequence 2;
- update + restart + health: OK;
- rollback: OK;
- limpieza final: sólo active + previous;
- overrides HTTP sólo se permiten en loopback y mediante variables de entorno explícitas.

## GitHub Release real

Release pública:

- tag: `v0.1.0-dev-github-e2e`;
- versión: `0.1.0-dev-github-e2e`;
- sequence: **3**;
- Latest: sí;
- prerelease: no.

Assets publicados:

- `LoomLCI-0.1.0-dev-github-e2e-win-x64-setup.exe`;
- `LoomLCI-0.1.0-dev-github-e2e-win-x64-update.zip`;
- `loomlci-update.json`;
- `loomlci-update.sig`.

ZIP de update:

- tamaño: **49.296.596 bytes**;
- SHA-256: `328f9d7dfeeae9c9780998d4fc4333451ce2c5876bb9d447880cfb9fc813bb29`.

## E2E real contra GitHub

Desde la instalación real:

1. `update check` contra GitHub:
   - actual: `0.1.0-dev-local-e2e`, sequence 2;
   - release: `0.1.0-dev-github-e2e`, sequence 3;
   - resultado: update disponible.
2. `update apply` ejecutado con IvanSpace sólo para no cortar la conexión MCP que coordinaba la prueba:
   - exit code 0;
   - activo: `0.1.0-dev-github-e2e`, sequence 3;
   - previous: `0.1.0-dev-local-e2e`, sequence 2.
3. smoke posterior desde ChatGPT -> LoomLCI:
   - `process_running=true`;
   - `healthy=true`;
   - `ready=true`;
   - tunnel correcto.
4. journal: ausente.
5. versiones instaladas: exactamente active + previous.
6. nuevo `update check`: `LoomLCI ya está actualizado.`

## Fuera de alcance cercano

- actualización automática del propio Launcher;
- code signing / Authenticode del EXE;
- canales beta/prerelease.

## Mejoras lejanas / no priorizadas

Quedan registradas como evolución futura, **no como próximos pasos**:

- automatizar la publicación mediante GitHub Actions: build + tests + generación de setup/ZIP/manifest/firma + upload de assets al crear un tag/release;
- automatizar el consumo en las PCs instaladas: chequeo al iniciar o periódico y aplicación automática/semiautomática de releases firmadas;
- resolver antes de automatizar CI cómo custodiar y usar la private key de firma sin degradar el modelo de confianza actual.

Hasta entonces, el flujo aceptado sigue siendo manual en ambos extremos: se publican los assets de cada GitHub Release manualmente y cada instalación ejecuta `update check` / `update apply` de forma explícita.

El bloque posterior de metadata/skill del plugin ya quedó **cerrado** (plugin 0.5.1); ver [[Plugin metadata]] y [[Roadmap post-G1]].

## Seguimiento histórico: descargas lentas y robustez del Launcher (2026-10-07; antes de Release 5)

Investigación en PC `GAMING`, sin cambios del Host instalado:

- En el mismo ZIP de GitHub Releases (~49 MB), tanto .NET 10 `HttpClient` como `curl` midieron aproximadamente 90–120 KiB/s; una descarga de prueba desde Cloudflare alcanzó ~1,9 MiB/s. Por lo tanto, la lentitud no es exclusiva de .NET y los intentos iniciales se cancelaron antes de darles tiempo para terminar.
- El código anterior utilizaba `ResponseHeadersRead` y no establecía un límite para cada lectura posterior; el Launcher llamaba `ApplyAsync(CancellationToken.None)`. La CLI tampoco informaba avance.
- **En el corte de investigación, cambio en código fuente todavía no desplegado (posteriormente incluido en Release 5):** se extrajo `UpdatePackageDownloader`; muestra bytes, porcentaje, velocidad e intento en la CLI; aplica un timeout por inactividad de **45 s** y un límite total de **30 min**; permite hasta **4 intentos**, con pausas crecientes y reanudación mediante HTTP `Range`.
- Se exige `206 Partial Content` con `Content-Range` coherente cuando se reanuda. Si el servidor ignora `Range` y responde `200 OK`, se reinicia limpiamente desde cero; respuestas parciales incompatibles se rechazan. El paquete completo se vuelve a verificar por tamaño y **SHA-256 contra el manifest firmado** antes de extraer o activar.
- Los controles de activación, journal, rollback y `highestSequence` no se modifican. La descarga/reintento sucede antes de iniciar la transacción.
- Pruebas nuevas sin red: transferencia lenta con progreso, IOException/EOF, timeout inactivo, reanudación exacta, Range ignorado, Range inválido, SHA alterado, cancelación explícita, deadline total y límite de reintentos.
- **Validación del código fuente:** suite Release **366/366** (Core 128, Launcher 30, MCP 6, PdfWorker 6, Windows 175, Integration 21); regresión específica posterior **21/21** y `git diff --check` correcto. No se hizo E2E del Launcher nuevo contra GitHub ni se desplegó en ninguna PC.

**Limitación arquitectónica que sigue vigente:** `update apply` actualiza sólo el **Host**, no el Launcher. **Cierre aplicado:** en Release 5 se distribuyó y ejecutó el instalador convencional para actualizar también el Launcher. La mención a Release 4 en las pruebas anteriores corresponde al estado histórico previo a ese despliegue.

**Estado histórico previo a release 5:** al cierre de la investigación inicial, `0.1.0-dev-python3` seguía activo en `GAMING` y todavía no se había ejecutado el update real.

## Release 5: publicación y E2E real (2026-10-08)

- Commit `42ee90c` y tag `v0.1.0-dev-42ee90c` publicados en GitHub. Release `0.1.0-dev-42ee90c`, sequence **5**, publicada como **Latest**, no prerelease, con los cuatro assets (setup, ZIP, manifest JSON y firma).
- El ZIP de update mide **49.365.306 bytes**, SHA-256 `13a1772549d14554ee95fe0c4a6c76c622fed42ea2c37d21309a99849b0899d8`; tamaño, digest y firma ECDSA del manifest verificados. Setup: **59.753.924 bytes**, SHA-256 `041af9b9b8b63c2b3be51275e032810fcb0f45414a7a16dc67ffe4c00d04ceb7`.
- Build firmado y pruebas del Launcher **30/30**, IntegrationTests contra Host publicado **21/21**. Prueba de installer aislado: exit code 0, launcher/payload/profile/config completos, runtime key con ACL user-only, registro HKCU correcto; desinstalación aislada completa. La descarga inicial del tunnel-client se evitó en el ensayo manual mediante binario local verificado por SHA-256.
- Ejecutado `update apply` desde el **Launcher corregido de la carpeta portable**, mediante **IvanSpace** para evitar perder el canal MCP. Progreso visible `0%, 63,7%, 100%`, descarga completa en primer intento; proceso terminó **exit code 0** en ~7,3 s.
- Runtime instalado activo `0.1.0-dev-42ee90c`, sequence **5**; previous `0.1.0-dev-python3`, sequence **0**; highestSequence **5**. Estado **healthy/ready**; tunnel correcto; sin journal. `update check`: ya actualizado. Sólo directorios active/previous.
- Smoke fresh-agent posterior con LoomLCI: `work_create`, `process_run`, `python_execute` con `import loom, loom.fs, loom.process`; 15 capabilities, lectura del árbol del repo y git status, exit 0. Repo limpio y sincronizado con origin/main.
- **Launcher instalado — cierre E2E:** se ejecutó el setup real de release 5 sobre la instalación activa; exit code **0**. Su SHA-256 `9d33140d6d993bdbe7989944fdb63d3610d73175d52a51ef9064109e140b86df` coincide exactamente con el Launcher portable de release 5. Registro de Windows actualizado a `0.1.0-dev-42ee90c`. `machine.json` conserva active `0.1.0-dev-42ee90c` (sequence 5), previous `0.1.0-dev-python3` (sequence 0), highestSequence 5; directorios active/previous presentes, sin journal; runtime healthy/ready; `update check` indica actualizado. Credencial preservada con ACL user-only; respaldos del Launcher anterior y `machine.json` presentes bajo `%LOCALAPPDATA%\LoomLCI-Release5-Backup`.
- **Smoke del Launcher instalado:** desde ChatGPT → LoomLCI, `work_create` + `python_execute` con `loom`, `loom.fs` y `loom.process`; 15/15 capabilities, acceso filesystem y `git status --short` mediante procesos, exit code 0. Git limpio. Se completa así el despliegue real de Host **y** Launcher. No se ejecutó rollback (sólo se verificó su disponibilidad).
