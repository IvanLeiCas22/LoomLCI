---
tipo: decisiones
estado: vigente
actualizado: 2026-10-09
---
# Decisiones vigentes

Esta página contiene **decisiones que siguen aplicando**. El historial de deliberaciones originales está en [[Decisiones]] y los registros de hitos están en [[Indice historico]].

## Producto y ejecución

1. LoomLCI **es un runtime**, no un agente; planificación cognitiva, prompts y memoria quedan fuera del Core.
2. **Full Trust** respeta los permisos Windows del usuario; no define roots restrictivos ni eleva privilegios automáticamente.
3. MCP funciona como **adaptador**: el Core no depende del protocolo. El transporte productivo es STDIO por Secure MCP Tunnel.
4. Capabilities estructuradas y ejecución general de procesos coexisten. Consultar primero las operaciones estructuradas cuando aporten valor.
5. Un proceso/worker con duración mayor a una invocación debe tener ownership/lifecycle explícitos; los trabajos cortos usan `process_run`.
6. Work Plan es opt-in, efímero por WorkSession, con IDs estables, revision/CAS y múltiples pasos `active`/`waiting` permitidos. No se sincroniza con recursos reales.
7. El Python de LoomLCI es privado y versionado, con un worker por WorkSession; `loom.fs` y `loom.process` son bridge interno, no herramientas MCP adicionales.

## Distribución y seguridad operativa

- El plugin contiene identidad/skill y snapshot del contrato, **no** instala otro servidor ni duplica manualmente schemas MCP.
- El catálogo MCP vivo es fuente de verdad para las herramientas; el script de verificación detecta drift del snapshot.
- Las releases locales siguen monotonicidad de secuencia, verificación de integridad, backup y rollback.
- **Nunca** detener/actualizar LoomLCI productivo bajo el control de un proceso hijo suyo. Usar supervisor externo comprobado para cutover.
- Uninstall preserva datos de usuario por defecto; operaciones de borrado requieren decisión explícita.
- Diagnósticos sólo por opt-in, con filtrado de campos, cuotas y retención; no hay telemetría remota incorporada.

## Acuerdo de trabajo

Flujo: **revisar código actual → investigar y analizar → proponer → aprobación del usuario → implementación por el asistente → compilación, pruebas, despliegue supervisado cuando corresponda → documentación y commit**. El usuario aprueba y no debe copiar comandos o ejecutar pruebas de rutina salvo necesidad real.

## Sin decisión de implementación productiva

Computer H1, actualizaciones/push automáticos, desarrollo de UI Windows, y mejoras distantes de distribución permanecen en [[Backlog]]. Su documentación de diseño no implica aprobación automática para ejecutarlos.
