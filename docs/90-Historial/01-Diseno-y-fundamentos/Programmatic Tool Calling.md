# Programmatic Tool Calling (PTC)

> Estado: investigación; propuesta de compatibilidad, no feature propia de Loom.

## Qué es

PTC es una capacidad del harness/model runtime de OpenAI. El modelo genera JavaScript que puede invocar tools elegibles desde un runtime hospedado. Sirve para encadenar llamadas, hacer loops, paralelismo, joins, filtros, deduplicación, agregaciones y validaciones sin volver al modelo entre cada resultado intermedio.

El JavaScript no obtiene por eso acceso local general. Las tools siguen ejecutándose donde corresponde: un MCP tool sigue ejecutándose en su servidor, shell en su entorno, etc.

## Cuándo recomienda OpenAI usarlo

Bueno para stages acotados con flujo de datos predecible:
- filtrar grandes resultados
- combinar/join de varias consultas
- deduplicar
- agregar/resumir estructuradamente
- validar campos o condiciones
- generar argumentos de llamadas dependientes mediante código

No es preferible cuando:
- cada resultado cambia la decisión semántica del modelo
- una sola llamada basta
- los resultados intermedios ya son pequeños
- hay writes/acciones sensibles o approvals
- el resultado final debe conservar artifacts/citations nativos

## Relación con LoomLCI

PTC no debe vivir dentro de Loom. Pertenece al harness superior.

Loom debe ser PTC-friendly:
- schemas de entrada/salida explícitos y predecibles
- output_schema cuando el adapter lo soporte
- errores estructurados y documentados
- herramientas pequeñas y composables
- resultados acotados, paginados/cursorizados cuando corresponda
- handles explícitos
- evitar estado implícito
- namespaces coherentes y pequeños
- lectura/búsqueda especialmente apta para composición programática

## Relación con MCP

OpenAI permite que MCP tools sean invocados desde PTC mediante `allowed_callers: ["programmatic"]` o `["direct", "programmatic"]` cuando el host/adaptador lo configura.

Esto es configuración del host OpenAI, no algo que el servidor MCP estándar de Loom deba codificar como dependencia interna.

Tool Search y PTC se complementan: una tool diferida primero debe cargarse mediante Tool Search; un programa que ya está ejecutándose no puede descubrir nuevas tools por sí mismo.

## Relación con Python Runtime

PTC y Python Runtime no se reemplazan.

PTC:
- vive en el harness
- orquesta tools
- JavaScript aislado
- no es compute local general
- no persiste globals entre programas

Python Runtime de Loom:
- vive en la PC local
- ejecuta Python general Full Trust
- puede mantener estado entre llamadas
- puede procesar datos/imágenes y usar filesystem/red/subprocess
- puede usar PyAutoGUI y un futuro módulo `loom`

Un host con PTC podría incluso llamar `python.execute` como una tool más.

## ChatGPT normal

ChatGPT puede usar plugins con apps MCP locales en desktop, pero la documentación pública de plugins no garantiza que esa superficie exponga PTC o permita configurarlo explícitamente. Por eso Loom no debe depender de PTC para ofrecer una buena experiencia a ChatGPT normal.

## Conclusión propuesta

Sí tener PTC en cuenta desde ahora como objetivo de compatibilidad y criterio de diseño de tools.

No implementar un runtime JavaScript/PTC dentro de Loom y no introducir dependencias internas de OpenAI.

Loom debe funcionar perfectamente con direct tool calling. Si el harness superior soporta PTC, debe poder aprovechar las mismas tools sin cambios en el Core.

## Fuentes

- OpenAI Programmatic Tool Calling: https://developers.openai.com/api/docs/guides/tools-programmatic-tool-calling
- OpenAI Tool Search: https://developers.openai.com/api/docs/guides/tools-tool-search
- OpenAI Deployment checklist: https://developers.openai.com/api/docs/guides/deployment-checklist
- OpenAI Agents: https://developers.openai.com/api/docs/guides/agents
- OpenAI Function calling: https://developers.openai.com/api/docs/guides/function-calling
- ChatGPT plugins: https://help.openai.com/en/articles/20001256-plugins-in-chatgpt
