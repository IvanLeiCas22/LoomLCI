# LoomLCI

Plugin privado de workflow/metadata para usar LoomLCI desde ChatGPT.

## Arquitectura

El plugin **no arranca ni configura el servidor MCP**. La conexión real se realiza mediante la app `LoomLCI MCP` conectada al Secure MCP Tunnel y el runtime LoomLCI instalado en Windows.

La skill complementa esa app con reglas de workflow. Las descripciones y schemas vivos del servidor MCP son la fuente de verdad para las capabilities disponibles, sus parámetros, límites y semántica.

## Requisitos

- LoomLCI instalado y en estado `healthy/ready`.
- app `LoomLCI MCP` conectada al tunnel de esa instalación.
- refrescar el catálogo de tools en ChatGPT después de cambios de contrato MCP.

No se requiere el repo local, un Host Debug ni una ruta fija de desarrollo.

## Mantenimiento

El repo LoomLCI conserva la fuente canónica bajo `plugin/`. `scripts/Build-PluginPackage.ps1` exporta el contrato MCP real, verifica referencias de la skill y genera el paquete compatible para Plugin Creator.

Los archivos MCP incluidos en el paquete publicado son neutralizadores vacíos para sobrescribir el wiring STDIO histórico de releases 0.2.x; la app del tunnel sigue siendo la única conexión MCP objetivo.
