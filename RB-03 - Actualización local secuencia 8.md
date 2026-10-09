---
tipo: deployment
proyecto: LoomLCI
fecha: 2026-10-09
version: 0.1.0-dev-f2802e2ba043
sequence: 8
estado: desplegado_local_validado
bloque: RB-03
---

# RB-03 — Actualización local secuencia 8

> **Operativa, validada y NO publicada en GitHub.** Host y Launcher internos **`0.1.0-dev-f2802e2ba043`, sequence 8**, con `healthy=true`, `ready=true` y mismo túnel productivo. Versión anterior de rollback: **`0.1.0-dev-173b4ffc00f6`, sequence 7**. Esta instalación local NO constituye GitHub Release, Git tag firmado ni publicación de un feed remoto.

## Motivo y fuente reproducible

Actualizar el Host productivo para activar RB-03 (propagación de fallos, cierres reintentables, expiración y disposers), sin hacer publicación pública. Fuente `main` limpia, código RB-03 originalmente en `1b84935`. Durante la validación del Host **publicado** se detectó y reparó un cambio incompatible de descripciones MCP: `c19d472` restauró `process_read`/`before closing` y endureció la prueba de contratos; `f2802e2` sincronizó el snapshot `plugin/contract/mcp-contract.json`. El instalador definitivo se generó desde `f2802e2ba043`, no desde los paquetes preliminares rechazados.

## Validación previa al corte

- Suite Release secuencial RB-03, previa a esos ajustes de documentación/contrato: **433/433**, incluidas **21/21 IntegrationTests**; sin fallos ni omitidas.
- Contra Host publicado definitivo: la compilación local pasó los tests de **Launcher 53/53** y los **21/21 IntegrationTests**, incluida `StdioAdapterExposesSelfDescribingToolContracts` y `PluginContractSnapshotMatchesAdvertisedTools`. Build-WindowsInstaller.ps1 con tests habilitados terminó **código 0**.
- Dos compilaciones locales previas abortaron como correspondía ante incompatibilidades de descripción y snapshot del plugin. Fueron **corregidas antes de producción**; no se instalaron. El snapshot y el contrato quedan reconciliados en el commit final.
- Ensayo auténtico en túnel **independiente** de prueba usando el nuevo código: instalación, `healthy/ready`, update supervisado, rollback a versión anterior, stop, uninstall aislado, shortcuts intactos, `RB04_LIVE_TUNNEL_ISOLATED_E2E_OK`, exit 0. El test no cambió la instalación productiva.
- El instalador final se generó fuera del InstallRoot bajo `%LOCALAPPDATA%\LoomLCI-Local-Staging\rb03-f2802e2ba043`, con Inno Setup **7.1.0** y `updateProtocol=2` en portable. Se verificaron metadata, version, sequence, rutas y hashes.
- Digests SHA-256 definitivos:
  - Setup `5FEDB361F1EAEA1C79D4C6F40A684BB9F719FB7572BD05F5EB7A4CAC57BD411A`
  - Launcher `53FDF72CD5D964D99838D16D02317A3C8A539DF7D3DAA67D2E7A010C2A2E6064`
  - Host `350A3772B66B22B3514160866E4D30CD815BAF30E2F804F7DA04B223BFEA30A1`
- `Invoke-SafeCutover.ps1 -Mode Preflight` desde IvanSpace terminó exit 0 y validó **sequence 7 → 8**, identidad instalada, journal ausente, credencial existente, rutas productivas y hashes de setup.

## Cutover productivo

Ejecutado **desde IvanSpace externo**, nunca como hijo del Host que se detiene:
`Invoke-SafeCutover.ps1 -Mode Execute -ConfirmExternalSupervisor`.

1. Verificó Release 7 `healthy=true`, `ready=true`, túnel y versión.
2. Guardó Launcher, machine.json, metadata y log fuera de instalación:
   `%LOCALAPPDATA%\LoomLCI-CutoverBackups\20261009T030048Z-2f5e9699433047ef9140d90aa8b3b353`.
3. `stop` (exit 0), instalador genuino `/VERYSILENT /NOSHORTCUTS=1` con secret sólo por ruta de archivo (exit 0), verificación de hashes/previous, `start` (exit 0).
4. Version 8 / sequence 8, `previous=version7/sequence7`, `healthy=true`, `ready=true`, `process_running=True`, túnel productivo esperado; supervisor **`CUTOVER_OK`, exit 0**. No se desinstaló Release 7, no se tocó ni publicó GitHub.

## Smoke real desde ChatGPT y auditoría independiente

- Nuevo `work_create`/Filesystem/Process/`work_close` desde ChatGPT exitoso; **25 tools MCP** accesibles.
- Python persistente: `import loom, loom.fs, loom.process`; `len(loom.capabilities())==16`, `loom.fs.list_tree`, `loom.process.run('git.exe',['--version'])` exit 0, globals preservados entre ejecuciones.
- Se inició un PowerShell temporal SessionOwned (`Start-Sleep`) con el Host nuevo; `work_close` devolvió éxito y el PID de prueba ya no existía al inspeccionarlo desde IvanSpace.
- Postcheck externo `LOCAL_SEQ8_POSTUPDATE_INTEGRITY_OK`: hashes Launcher/Host exactos, `highestSequence=8`, previous sequence 7 conservada con Host disponible, journal ausente, atajos `LoomLCI.lnk` y `Detener LoomLCI.lnk` conservados byte a byte, API key local con ACL protegida y backup/auditoría presentes.

## Compatibilidad futura y límites

- **No existe una GitHub Release seq8 ni manifest remoto seq8**. Este es un build interno instalado de manera local. El anti-rollback conserva `highestSequence=8`; **toda próxima actualización, sea local o firmada, debe tener `sequence >= 9`** y no debe reutilizar seq8 con otros bytes/versiones. Elegir nuevos identificadores de versión y secuencias mayores al publicar.
- El feed de futuras releases firmadas debe conservar `minUpdateProtocol=2` cuando corresponda, metadata coherente, SHA-256, firma verificable, y solo aplicarse con supervisión cuando cambie el Launcher.
- No ejecutar uninstall productivo hasta resolver [[Roadmap de robustez post-auditoría|RB-05]]: el uninstall aún contiene reglas recursivas riesgosas para datos bajo `%LOCALAPPDATA%\LoomLCI`.
- El rollback productivo no se ejecutó destructivamente (se conserva y fue validado mediante E2E aislado). Los fallos de cleanup de recursos fueron ensayados con disposers ficticios y pruebas de Core; **no se provocó un fallo nativo deliberado en producción**. No se garantiza imposibilidad absoluta de problemas futuros.
- Conservar el setup local verificado y respaldo externo mientras se estabiliza. GitHub, push y firma de nuevas publicaciones siguen fuera de alcance. Plugin privado actual 0.5.1 sigue conectado.
- Próximos bloques: RB-05 (uninstall), RB-06 (observabilidad). `RB-03` cerrado end-to-end para el alcance anterior.
