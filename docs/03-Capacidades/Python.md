---
tipo: referencia_capacidad
estado: vigente
actualizado: 2026-10-09
---
# Python

## Runtime

`python_execute` ejecuta código dentro de un worker CPython **privado de LoomLCI**, iniciado bajo demanda y persistente mientras viva la WorkSession. Reutiliza imports, variables globales y estado entre ejecuciones. No es el Python instalado en el PATH de Windows. El runtime fijado de esta implementación es **CPython 3.14.8 x64**.

- `python_reset` descarta el worker y el namespace para esa WorkSession.
- `python_packages_prepare` prepara el conjunto completo deseado de paquetes PyPI, fuera del runtime inmutable, con versiones/hashes; si cambia el entorno de un worker activo, reiniciar mediante `python_reset`.
- Los timeouts/errores graves pueden reiniciar el worker; una excepción Python normal se devuelve como resultado de ejecución sin destruir necesariamente el namespace.

## Bridge privado

Dentro de `python_execute`:

```python
import loom
import loom.fs
import loom.process

print(loom.capabilities())
print(loom.fs.list_tree(".", max_depth=1, max_entries=10))
print(loom.process.run("git.exe", ["status", "--short"]))
```

`loom.fs` reutiliza operaciones estructuradas de Filesystem/PDF y `loom.process` los procesos Loom-managed de la misma WorkSession. `loom.process.run_many` permite trabajos concurrentes con límites configurables; sus resultados preservan el orden de entrada.

`loom.display_image(png_bytes)` emite imagen PNG/JPEG/WebP generada **en memoria** para el modelo; el render inline en el cliente ChatGPT puede variar. Para una imagen ya existente usar [[Visual Files]], no copiar su binario por Python sólo para verlo.

## Seguridad y límites

- No introducir secretos en stdout, traceback, imágenes o notas.
- El runtime provisiona assets verificados y el package store usa PyPI oficial, wheels y lock por hashes; no es una instalación libre desde cualquier URL/VCS.
- La herramienta `python_execute` no ofrece stdin interactivo en la versión documentada.
- Consultar los schemas MCP vivos para máximos de código/output, paquetes y timeouts.

**Evidencias:** [[Bloque E - Python Runtime]], [[Python 1 - Paquetes administrados]], [[Python 2 - Bridge privado loom]], [[Python 3 - Outputs binarios e imágenes]], [[A4.1 - Ejecucion paralela local]].
