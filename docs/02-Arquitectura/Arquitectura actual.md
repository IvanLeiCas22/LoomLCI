---
tipo: arquitectura
estado: vigente
actualizado: 2026-10-09
---
# Arquitectura actual

LoomLCI es un **runtime de ejecución local**, no un agente, planificador autónomo ni memoria conversacional.

## Recorrido de una llamada

```text
ChatGPT normal
  → aplicación "LoomLCI MCP" + Secure MCP Tunnel
  → tunnel-client (runtime local instalado)
  → LoomLCI.Host (MCP STDIO)
  → LoomLCI.Mcp (adaptador, schemas, control de payload)
  → LoomLCI.Core (WorkSession, recursos, capacidades)
  → LoomLCI.Windows (filesystem, process, Python, visual)
  → Windows
```

El Host registra `WorkSessionManager`, `ResourceRegistry`, `InvocationRunner`, capabilities y proveedores Windows. El código de composición se encuentra en `src/LoomLCI.Host/Program.cs` y `src/LoomLCI.Mcp/McpServiceCollectionExtensions.cs`.

## Capas en código

| Proyecto | Responsabilidad |
| --- | --- |
| `LoomLCI.Core` | Contratos, invocaciones, errores, WorkSession, ownership, recursos, capacidades, eventos |
| `LoomLCI.Windows` | Implementaciones Windows; Win32/Job Objects/ConPTY, Python privado y proveedores de archivos/PDF |
| `LoomLCI.Mcp` | Tools públicas MCP, validación de parámetros y representación de resultados |
| `LoomLCI.Host` | Composición por DI, ciclo de vida, MCP STDIO y observabilidad |
| `LoomLCI.Launcher` | Setup, start/stop/status, updates, rollback, uninstall, diagnósticos |
| `LoomLCI.PdfWorker` | Trabajo PDF aislado fuera del Host |

## Estado y recursos

- `WorkSession`: contexto opcional de directorio y dueño de recursos session-owned. Una WorkSession nueva no es una barrera de seguridad.
- `ResourceRegistry`: handles explícitos para recursos que sobreviven a una invocación (p. ej. procesos), con cleanup y expiración.
- `Process`: Jobs de Windows y streams; ownership `Independent` puede sobrevivir a `work_close` pero no es invulnerable al cierre del Host que lo creó.
- `Work Plan`: registro **lógico** efímero por WorkSession, independiente de los procesos; CAS por revision.
- `Python`: worker persistente lazy dentro de la WorkSession y bridge privado; no agrega un segundo servidor MCP.
- `Observabilidad`: event bus y diagnósticos locales opt-in, desacoplados de la ejecución.

## Principios y límites

Full Trust usa los permisos de la cuenta Windows; **no** equivale a UAC elevado. MCP es frontera de protocolo, no modelo interno del dominio. Computer H1 y Streamable HTTP productivo no forman parte de la arquitectura desplegada: ver [[Backlog]] y [[Bloque H - Computer]].

La configuración verificable de cada herramienta está en el contrato MCP publicado, no en tablas manuscritas de argumentos. Referencias históricas: [[Arquitectura propuesta]], [[Especificacion interna v0.1]], [[Estructura del repositorio v0.1]].
