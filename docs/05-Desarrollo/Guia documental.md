---
tipo: norma_documental
estado: vigente
actualizado: 2026-10-09
---
# Guía documental

## Objetivo

Una nota vigente responde **qué es cierto ahora**, una nota histórica responde **qué se investigó, decidió o probó entonces**. Se preservaron las 58 notas originales en `docs/90-Historial/`; las nuevas referencias resumen hechos contrastados, no sustituyen evidencia.

## Fuente única por tema

- Estado productivo, versión y rollback: **[[Estado del sistema]]**.
- Arquitectura efectiva: [[Arquitectura actual]].
- Compromisos: [[Decisiones vigentes]].
- Herramientas y uso: referencias en `docs/03-Capacidades/`; schemas reales en Host MCP.
- Operación: `docs/04-Operacion/`.
- Desarrollo: [[Guia de desarrollo]].
- Futuro: [[Backlog]].
- Registro histórico: [[Indice historico]].

No repetir versiones activas en cada nota operativa; enlazar a Estado del sistema. En cada release, guardar número/commit/hashes, pruebas, estado de instalación y comprobaciones de rollback en evidencia fechada.

## Convenciones

Para documentos nuevos usar frontmatter simple:

```yaml
---
tipo: referencia_capacidad
estado: vigente
actualizado: 2026-10-09
---
```

Los valores de `tipo` y `estado` deben ser uniformes; preferir `indice`, `estado_operativo`, `arquitectura`, `decisiones`, `referencia_capacidad`, `operacion`, `guia_desarrollo`, `norma_documental`, `backlog` o `evidencia`; y `vigente`, `historico`, `planificado` donde correspondan. Si se necesitan tags de bloque/release, agregar propiedades explícitas y consistentes.

- Usar títulos únicos en toda la bóveda; los enlaces internos de Obsidian deben resolver un solo destino.
- La documentación técnica refleja implementaciones reales; separar «diseñado», «implementado», «validado» y «desplegado».
- Archivar investigaciones, pruebas, benchmarks y releases sin borrar conclusiones ni errores históricos.
- Referenciar rutas de código, tests, scripts y commits de la evidencia. No pegar secretos, claves, tokens o datos personales innecesarios.
- Comprobar enlaces, links relativos, Markdown, Unicode y la estructura de la bóveda después de cada migración.
- Al mover notas mantener identidad del basename cuando sea posible y usar `git mv` para preservar la trazabilidad.
- Si un documento histórico contiene expresiones como «actual», interpretarlas en la fecha de ese documento.

## Verificación y mantenimiento

Antes del commit: ejecutar `scripts/Test-Documentation.ps1` (wikilinks, títulos duplicados, frontmatter vigente, enlaces Markdown relativos y conteo histórico), comprobar la conservación de las notas originales, `git diff --check` y ausencia de cambios inesperados en producción. Al cambiar de versión no editar todas las releases anteriores: añadir nueva evidencia y actualizar la referencia canónica del estado operativo.

La carpeta `.obsidian/` es configuración local **ignorada por Git**; mantenerla en la raíz y validar que el workspace no dependa de rutas obsoletas.
