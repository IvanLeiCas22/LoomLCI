---
tipo: operacion
estado: vigente
actualizado: 2026-10-09
---
# Instalación y conexión

## Producto desplegado

LoomLCI se distribuye para Windows x64 con un Host .NET publicado, Launcher, runtime local de túnel y un instalador Inno Setup. La **versión instalada actual** se consulta sólo en [[Estado del sistema]], no se duplica aquí.

```text
ChatGPT → app "LoomLCI MCP" → Secure MCP Tunnel
       → tunnel-client (runtime instalado)
       → LoomLCI.Host.exe por STDIO
```

El plugin privado `plugin/` aporta metadata y skill; no inicia otro servidor MCP y no debe registrar una conexión redundante.

## Rutas convencionales

- Instalación por usuario: `%LOCALAPPDATA%\Programs\LoomLCI`.
- Configuración y datos del deployment: `%LOCALAPPDATA%\LoomLCI\deployment`.
- Assets Python administrados: `%LOCALAPPDATA%\LoomLCI`.
- Carpeta de código fuente y builds: **independiente** de instalación y datos de usuario.

No escribir credenciales en la bóveda, comandos versionados, chat o logs. El setup utiliza un archivo de clave con permisos de usuario y referencia a esa ruta.

## Operación de uso

```powershell
$launcher = "$env:LOCALAPPDATA\Programs\LoomLCI\LoomLCI.Launcher.exe"
& $launcher status
& $launcher start
& $launcher stop
```

`start` y `stop` son comandos explícitos: no probarlos innecesariamente en el runtime productivo. Para consultar su CLI, ejecutar `LoomLCI.Launcher.exe` sin argumentos; `setup` requiere preparación de paquete/identidad/secretos.

## Validación

1. `status` reporta proceso activo y `healthy/ready`.
2. La app `LoomLCI MCP` aparece conectada a Secure MCP Tunnel en ChatGPT.
3. ChatGPT puede crear/cerrar WorkSession y ejecutar una herramienta real.
4. Tras cambios en el contrato MCP, refrescar el catálogo en ChatGPT.
5. No afirmar despliegue verificado sólo porque compilan fuentes: conservar evidencia de un smoke desde la app productiva.

Referencias históricas: [[Deployment portable]], [[Instalador Windows]], [[Integracion con ChatGPT]], [[Plugin metadata]].
