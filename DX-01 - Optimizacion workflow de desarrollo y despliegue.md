---
tipo: plan_tecnico
proyecto: LoomLCI
bloque: DX-01
fecha: 2026-10-09
estado: investigado_propuesta_pendiente_aprobacion
prioridad: maxima_previa_RB05_RB06
---

# DX-01 — Optimización del ciclo de desarrollo, tests y empaquetado

> Decisión de prioridad del usuario (2026-10-09): resolver la lentitud del workflow de código/instalación ANTES de continuar con RB-05 (uninstall seguro) y RB-06 (observabilidad durable). RB-04 está cerrado desde Release 7. **En esta fase sólo se investigó, midió y documentó el diseño; sin cambios de código ni despliegue.**

## Investigación del estado real

Archivos revisados: `scripts/Build-PortablePackage.ps1`, `scripts/Build-WindowsInstaller.ps1`, `scripts/Test-LiveIsolatedTunnelE2E.ps1`, `tests/LoomLCI.IntegrationTests/McpStdioTests.cs` y [[RB-03 - Actualización local secuencia 8]].

1. `Build-WindowsInstaller.ps1` siempre invoca `Build-PortablePackage.ps1`; este siempre ejecuta `dotnet publish` para Host y Launcher, genera ZIP portable con `Compress-Archive` y, salvo `-SkipPortableTests`, ejecuta **53 tests Launcher y 21 tests MCP contra el Host publicado**. Luego Inno Setup vuelve a comprimir la carpeta portable en un setup independiente, sin consumir el ZIP.
2. Durante RB-03, dos empaquetados fallaron *tardíamente* por `StdioAdapterExposesSelfDescribingToolContracts` y `PluginContractSnapshotMatchesAdvertisedTools`: errores de la descripción de `work_close` y del snapshot `plugin/contract/mcp-contract.json`. Esas dos pruebas pueden ejecutarse focalizadamente **antes de construir un instalador completo**.
3. `McpStdioTests.GetHostDll` acepta `LOOMLCI_TEST_HOST_DLL` para seleccionar explícitamente un Host compilado; hay que verificar que realmente pertenece al código actual antes de utilizarlo (evitar falsos verdes de artefactos viejos).
4. `Test-LiveIsolatedTunnelE2E.ps1` genera **dos instaladores completos** (dos `Build-WindowsInstaller.ps1` con `-SkipPortableTests`) para probar el cutover/rollback. Debe reservarse para cambios en Launcher, setup, update o supervisor, o para gates programados; no es razonable repetirlo ante toda modificación pequeña de Host.
5. El cutover productivo supervisado `scripts/Invoke-SafeCutover.ps1` NO es cuello de botella y **no debe simplificarse ni ejecutarse como hijo del Host que detiene**.

### Tiempos y evidencia

- `Test-LiveIsolatedTunnelE2E.ps1` anterior: 2026-10-09 02:46:50Z–02:49:01Z, ~131 s, incluyendo dos builds, E2E, rollback y uninstall aislado.
- Compilación/paquete final: ~163 s desde creación hasta artefacto; **21 IntegrationTests: 1m43s (103 s)** y **Inno Setup: 29,047 s** (log de `build.log`). El resto (~31 s) incluye .NET publish, ZIP, hashes y demás IO. Estas categorías son aproximadas.
- Dos intentos de paquete rechazados: ~37 y ~83 s. Las pruebas focalizadas MCP que se repitieron después pasaban en ~11 s.
- Cutover productivo: backup/stop/installer/start/health aproximadamente **9 s** (log de `cutover.log`), excluyendo smoke, análisis y documentación.
- Tamaños: ZIP portable **77,57 MiB**; setup **57,01 MiB**; Host publicado ~118,62 MiB en 244 archivos; Launcher single-file ~70,25 MiB. No tomar tiempo de compresión como tiempo de compilación de C#.
- Suite completa de código RB-03 fue ~433/433; el tiempo de pruebas completas no se debe sumar sin criterio al de package, porque en este episodio hubo ejecuciones repetidas.

## Diseño propuesto (sin implementar todavía)

### DX-01.A — Fail-fast y perfil de validación rápida

- Agregar un comando/script de prevalidación claramente distinto de la *aceptación para producción*.
- Compilar en `Release` y ejecutar primero `StdioAdapterExposesSelfDescribingToolContracts` + `PluginContractSnapshotMatchesAdvertisedTools` contra un Host de **esa compilación**, con `LOOMLCI_TEST_HOST_DLL` explícito y comprobado.
- Añadir filtros de tests afectados por el cambio; un resultado `fast pass` **jamás** autoriza un cutover por sí solo.
- Reportar duraciones y salida/estado por etapa; fallar al primer error con código no cero. No ocultar flakiness mediante reintentos automáticos de tests fallidos.

### DX-01.B — Evitar empaquetado redundante

- Opción **opt-in** para omitir `Compress-Archive` del portable si sólo se necesita el instalador Inno. Conservar comportamiento histórico por defecto para quien necesite ZIP y mantener `package.json`, licencias, checks de assets y SHA-256/metadata del setup.
- `Test-LiveIsolatedTunnelE2E.ps1` podrá omitir ZIP en sus dos builds, porque consume el setup y la carpeta portable. Verificar que no cambia su teardown.
- Estudiar reutilización de **artefactos idénticos y verificados**; nunca reutilizar una carpeta mutable por sólo coincidencia de nombre/versión. Requiere invariantes de contenido, hashes y lock. Postergar cache compleja si DX-01.A/B cubren el cuello de botella.

### DX-01.C — Gates proporcionados por tipo de cambio

| Caso | Pruebas de iteración | Gate previo a producción |
| --- | --- | --- |
| Código Host/Core/MCP | Compilación + tests focalizados + contratos MCP rápidos | Suite total + Host publicado contra integración + setup verificado + smoke |
| Launcher / updater / Inno / supervisor | Tests focalizados Launcher + contratos + E2E aislado genuino | Suite total + E2E cutover/rollback + setup + supervisor externo |
| Sólo documentación | No ejecutar full build sin necesidad | Ningún cutover por cambios sólo documentales |

La secuencia monotónica y política de distribución siguen siendo obligatorias: runtime productivo seq8; próxima secuencia **>=9** si se actualiza.

### DX-01.D — Medición y regresiones del workflow

- Cronometrar explícitamente build Host, build Launcher, contract-fast, Launcher tests, integración full, ZIP (si aplica), Inno y preflight; distinguir wall time de duración xUnit e informar resumen legible.
- Añadir smoke/test automatizado de modo con/sin ZIP, manifest y metadata inalterados, falla temprana del snapshot sin setup “apto”, preservación de `-SkipPortableTests` sólo para entornos permitidos, y compatibilidad de rutas de builds portables existentes.
- Medir A/B en la misma máquina, sin modificar la instalación productiva; reportar números reales y no estimar ahorros como garantías.

## Límites de seguridad

No modificar `Invoke-SafeCutover.ps1`, rollback ni journal para ganar velocidad. No omitir la integración contra Host **publicado** ni tests Release completos en un deployment final. No publicar ni desplegar DX-01 de modo autónomo: son cambios a scripts y tests locales, y los nuevos modos se validan con staging aislado. No modificar el runtime seq8 durante esta fase.

## Criterios de aceptación

- [ ] Prevalidación rápida falla antes del empaquetado ante descripción/snapshot desactualizados.
- [ ] `Build-WindowsInstaller` genera setup verificable sin ZIP sólo con opción explícita; camino tradicional produce ambos y no cambia su contrato.
- [ ] Pruebas y timings de ambas rutas registrados (fast vs full) y sin bypass accidental de seguridad.
- [ ] Una corrida final con Host publicado + integración completa y E2E aislado adecuado al cambio, sin tocar la instalación productiva.
- [ ] Documentación, Git limpio, medición comparativa y decisión separada si se requiere cutover seq9.

**Próximo paso:** aprobación del diseño concreto DX-01.A/B/C/D antes de implementar; luego retomar [[Roadmap de robustez post-auditoría|RB-05]] y RB-06.
