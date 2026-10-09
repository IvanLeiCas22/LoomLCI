---
tipo: guia_desarrollo
estado: vigente
actualizado: 2026-10-09
---
# Guía de desarrollo

## Stack y carpetas

El repositorio utiliza **.NET 10**, SDK indicado en `global.json` (10.0.400, `rollForward=latestFeature`), C#, tests .NET, scripts PowerShell y plugin de workflow.

| Ruta | Contenido |
| --- | --- |
| `src/` | Core, Windows providers, MCP, Host, Launcher, PdfWorker |
| `tests/` | Tests unitarios, integración, contratos y harnesses |
| `scripts/` | Build portable, instalador, plugin, contrato, preflight/cutover y smoke |
| `plugin/` | Manifest, skill y snapshot canónico del contrato MCP |
| `runtime/` | Recursos del runtime empotrado |
| `docs/` | Referencias vigentes e historial Obsidian |
| `artifacts/` | Generados locales; **ignorados por Git**, no son documentación fuente |

## Iteración normal

1. Inspeccionar código **en su estado actual**, contratos y tests antes de proponer.
2. Investigar, analizar y presentar una propuesta. Esperar aprobación para modificar implementación.
3. Trabajar preferentemente con LoomLCI MCP productivo; reservar IvanSpace al cutover/fallback.
4. Introducir cambios mínimos con tests de regresión; realizar build Release cuando corresponda.
5. Para comprobación temprana del MCP, `scripts/Test-FastContracts.ps1` compila Host Release y compara contratos; **no reemplaza** la suite completa ni las pruebas de release.
6. Antes de empaquetar, probar el Host publicado/integración, no confundir `bin/Debug` con lo instalado; `LOOMLCI_TEST_HOST_DLL` permite fijar Host en pruebas MCP.
7. Si el cambio afecta deployment, ejecutar preflight, backup y smoke supervisados según [[Actualizacion y recuperacion]].
8. Actualizar sólo documentos canónicos afectados y registrar evidencia histórica; revisar `git diff`, `git status`, enlaces y después hacer commit.

## Comandos orientativos

```powershell
dotnet --version
dotnet test .\LoomLCI.slnx -c Release
.\scripts\Test-FastContracts.ps1
.\scripts\Test-Documentation.ps1
git status --short
```

Para construir paquetes, consultar los parámetros **actuales** de `Build-PortablePackage.ps1`, `Build-WindowsInstaller.ps1` y `Build-PluginPackage.ps1`. No ejecutar builds de instalación ni scripts de cutover sin necesidad; una release necesita secuencia, hashes y respaldo coherentes.

## Contrato y skill

`plugin/plugin.json` define versión/identidad; `plugin/skills/loomlci/SKILL.md` define workflow; `plugin/contract/mcp-contract.json` guarda snapshot; el **Host MCP vivo** es la fuente de verdad operativa. La verificación de contrato y el build del plugin deben detectar nombres, schemas y anotaciones divergentes. Ver [[Plugin metadata]].

## Checklist de cierre

- [ ] Fuente y pruebas relevantes verificadas
- [ ] Contrato/plugin revisados si cambian tools
- [ ] Estado de instalación separado del de implementación en fuente
- [ ] Documentación vigente y evidencia histórica reconciliadas
- [ ] Enlaces Obsidian validados
- [ ] `git diff --check` sin errores
- [ ] Commit coherente; registrar explícitamente si faltó prueba/despliegue
