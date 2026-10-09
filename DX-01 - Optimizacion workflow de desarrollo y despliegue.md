---
tipo: plan_tecnico
proyecto: LoomLCI
bloque: DX-01
fecha: 2026-10-09
estado: implementado_validado_en_fuente_sin_despliegue
prioridad: maxima_previa_RB05_RB06
---

# DX-01 — Optimización del ciclo de desarrollo, tests y empaquetado

> **DX-01 IMPLEMENTADO Y VALIDADO EN CÓDIGO LOCAL**, sin despliegue productivo. Decisión 2026-10-09: optimizar el workflow antes de RB-05 y RB-06; RB-04 sigue cerrado. Instalación operativa: **sequence 8, sin cambios**. No confundir una prevalidación rápida con la aceptación completa para producción.

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

## Diseño e implementación

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

- [x] Prevalidación rápida falla antes del empaquetado ante descripción/snapshot desactualizados; inyección controlada y restauración exacta verificadas.
- [x] `Build-WindowsInstaller` genera setup verificable sin ZIP con `-SkipPortableZip` explícito; `Build-PortablePackage` mantiene ZIP por defecto y acepta `-SkipZip`. Ambos modos validados con binarios reales.
- [x] Etapas de preflight, publicación, tests, ZIP e Inno cronometradas con `DX01_STAGE`; preservados tests de Host publicado, metadata, SHA-256 y preflight supervisor. Los switches heredados `-SkipTests`/`-SkipPortableTests` continúan siendo **sólo para validación aislada**, no aceptación de producción.
- [x] Suite Release 433/433, empaquetado nuevo con 53/53 Launcher y 21/21 IntegrationTests contra Host publicado, más E2E genuino aislado con cutover/rollback y shortcuts intactos. Producto seq8 sin cambios.
- [x] Documentación y mediciones de ambos modos. Cambios de scripts validados localmente: **no requiere cutover de seq8**. Git será confirmado y commiteado al cerrar la implementación.

## Uso habitual y gates

Desde la raíz del repositorio, en Windows PowerShell:

```powershell
# Durante el desarrollo: gate corto, NO autoriza despliegue
.\scripts\Test-FastContracts.ps1

# Pruebas relevantes según los archivos afectados (ejemplo)
dotnet test tests\LoomLCI.Core.Tests\LoomLCI.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~LifetimeTests'

# Antes de aprobar una instalación: suite Release completa
dotnet test LoomLCI.slnx -c Release --no-restore -m:1

# Empaquetado completo para un instalador local, SIN ZIP redundante
.\scripts\Build-WindowsInstaller.ps1 -SkipPortableZip -Version 'VERSION_UNICA' -Sequence 9

# Portable clásico para distribución (con ZIP; mismo comportamiento de siempre)
.\scripts\Build-PortablePackage.ps1 -Version 'VERSION_UNICA' -Sequence 9
```

**Importante:** las secuencias 9 de arriba son ilustrativas. Antes de distribuir, se debe confirmar `highestSequence` y usar un identificador mayor que el instalado, nunca reutilizar una secuencia publicada. Construir en staging controlado y ejecutar el cutover real sólo desde un supervisor externo con validaciones de preflight, SHA-256 y backup. **No usar** `-SkipTests` ni `-SkipPortableTests` como evidencia de aceptación de un instalador de producción; son opciones heredadas para ensayos acotados. La prueba rápida no equivale al gate final.

## Resultados de validación DX-01 (2026-10-09)

| Verificación | Resultado |
| --- | --- |
| `Test-FastContracts.ps1` | PASS: dos tests de contrato MCP sobre Host Release compilado desde fuente; 22,5 s en primera corrida, 15,8 s con build incremental |
| Fail-fast negativo | PASS: snapshot MCP intencionalmente desactualizado, fallo esperado **15,6 s** antes de publish/ZIP/setup; snapshot restaurado byte a byte y Git lo mostró sin cambio |
| Suite Release completa | **433/433 PASS**, con `21/21` IntegrationTests |
| Installer `-SkipPortableZip` sin saltar tests | PASS, **168,5 s**, Launcher **53/53**, published Host Integration **21/21**, manifest/version/sequence/rutas/SHA-256 correctos y ZIP ausente |
| Portable clásico con ZIP | PASS, binarios y manifest correctos, ZIP verificado SHA-256; compresión medida **14,9 s** en esta máquina |
| `Test-LiveIsolatedTunnelE2E.ps1` | PASS **104,9 s**: dos instaladores sin ZIP, conexión a túnel de test, cutover supervisado, rollback, uninstall aislado, accesos directos conservados, exit 0 |

Medición de las etapas en el empaquetado completo sin ZIP: `portable-fast-preflight=16,0 s`, `publish-host=5,5 s`, `publish-launcher=3,6 s`, `launcher-tests=4,6 s`, `published-host-integration=107,7 s`, `inno-setup=30,3 s`. Son tiempos de pared de etapas y se superponen con el contador total `installer-portable`; **no sumarlos indiscriminadamente**.

**Conclusión:** el ahorro directo del ZIP es aproximadamente 15 s cuando habría que generarlo; la prevalidación agrega ~16 s a un build final exitoso, por lo que **DX-01 no reduce drásticamente un empaquetado que ya pasaría**, sino que evita reempaquetar cuando fallan contratos (como ocurrió durante seq8). El E2E aislado pasó de ~132 s históricos a ~105 s en esta corrida, pero son mediciones distintas, no un benchmark controlado del mismo run. Los 21 tests MCP contra Host publicado siguen siendo el costo dominante (~108 s) y no se eliminaron.

La mejora de caché/reutilización de artefactos quedó explícitamente **diferida**: aplicarla sin fingerprints completos podría permitir falsos verdes. El estado del sistema productivo sigue siendo seq8 y no se publicó ninguna Release.

**Próximo bloque:** investigación/análisis de [[Roadmap de robustez post-auditoría|RB-05]] y luego RB-06. DX-01 no despliega nuevo Host/Launcher; cualquier futuro cutover debe respetar la secuencia >=9.
