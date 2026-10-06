# Auto-update firmado

> Estado: **CERRADO end-to-end (2026-10-06).** LoomLCI ya puede consultar GitHub Releases, verificar un manifest firmado, descargar/verificar un Host update, activarlo transaccionalmente, recuperar ante fallos/crash y volver a `previousVersion`.

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

El siguiente bloque de [[Roadmap post-G1]] es **Producto / Deployment 4: mecanismo de generación/verificación de metadata y skill del plugin**.
