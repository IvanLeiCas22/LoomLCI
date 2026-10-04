# Plan y tareas

> Estado: F1.1 Core + F1.2 MCP implementados/validados. F1.3 pasó preflight/smoke. El ajuste de ergonomía MCP mejoró el golden set de 0/3 a 1/3 positivos, con 2/2 negativos sin sobreplanning. Pendiente investigar/aplicar un ajuste mínimo de la skill del plugin y repetir fresh-agent.

## Distinciones necesarias

Hay tres conceptos diferentes que no conviene mezclar:

1. Plan de ejecución: checklist corta del trabajo actual del agente.
2. Tareas duraderas: backlog o TODOs que sobreviven a una sesión.
3. MCP Tasks: extensión del protocolo para llamadas de herramientas asíncronas/largas. No es un plan del agente.

## Qué hacen otros agentes

Codex tiene `update_plan`: recibe una lista de pasos con estados pending / in_progress / completed y permite como máximo un paso in_progress. El propio código de Codex aclara que es una checklist de progreso y es distinta de Plan Mode.

En Codex 0.152.0 `update_plan` pasó a ser opt-in/default-off. Un motivo práctico documentado por usuarios e integraciones es evitar dos superficies de planificación cuando el host o un MCP externo ya proporciona una. Esto sugiere que el plan debe ser opcional, no una capability universal obligatoria.

Claude Code tiene un sistema de tareas habilitado por defecto en modo interactivo y permite compartir una lista entre sesiones mediante `CLAUDE_CODE_TASK_LIST_ID`. Claude Desktop además expone paneles separados de plan y tasks. Esto confirma que el estado estructurado externo al modelo sí es útil en trabajo agentic largo.

MCP 2026-07-28 tiene una extensión Tasks, pero sirve para representar el lifecycle durable de una llamada asíncrona (`tasks/get`, `tasks/update`, `tasks/cancel`). No debe reutilizarse para la checklist cognitiva del agente.

## Conclusión propuesta

Agregar una capa opcional `Agent Support`, separada del Core de ejecución.

LoomLCI
- Core / execution: Filesystem, Process, Computer
- Agent Support: Work Plan (opcional)

Loom no decide el plan; solamente almacena, valida y devuelve el estado que el modelo escribe.

### Concurrencia: revisión de la propuesta

La restricción de Codex de un único `in_progress` no debe copiarse automáticamente en Loom. Codex usa `update_plan` principalmente como indicador secuencial de foco/progreso; eso no significa que el runtime no pueda tener procesos o trabajos concurrentes.

OpenAI GPT-6 soporta Async Tool Calling: el modelo puede iniciar una tool lenta y continuar razonando o llamando otras tools mientras la aplicación ejecuta la primera. También existen parallel function calls y Multi-agent para workstreams independientes. Codex `exec_command` puede devolver un session ID cuando un proceso sigue vivo, permitiendo al agente continuar con otro trabajo y volver luego al proceso. Anthropic documenta background tasks que mantienen procesos largos activos sin bloquear a Claude Code.

Esto valida el caso: iniciar una compilación larga, trabajar en otra cosa y volver después al resultado.

### Alcance inicial revisado propuesto

Work Plan efímero para la tarea actual:
- pasos cortos con identidad estable
- múltiples pasos pueden estar activos simultáneamente
- estados propuestos: `pending`, `active`, `waiting`, `completed`
- `waiting` significa que el paso está en curso pero espera un proceso, resultado externo, usuario u otra condición; el agente puede avanzar con trabajo independiente
- actualización atómica con `revision`
- no acoplar automáticamente un paso a ProcessHandle/Invocation en v0.1

Ejemplo:

    [waiting] Compilar proyecto        -> proc_123 sigue ejecutándose
    [active]  Actualizar documentación
    [pending] Revisar resultado build

El Resource Registry sigue siendo la fuente de verdad de procesos/jobs. El Work Plan sólo representa el estado lógico que el modelo quiere mantener.

No construir todavía un scheduler, DAG de dependencias, gestor persistente de proyectos ni multi-agent coordinator. Dependencias explícitas pueden añadirse después si los evals muestran que son necesarias.

### Exposición

Debe poder activarse/desactivarse por adapter/perfil:
- ChatGPT base: probablemente activado, porque aporta una capacidad que el host no ofrece de forma equivalente.
- Codex / Claude Code u otros harnesses con planificación propia: desactivado para no duplicar herramientas.

La implementación F1.1 mantiene el Work Plan directamente dentro de WorkSession y no usa Resource Registry. El formato exacto de la tool y la política de exposición se cierran en F1.2.

## Fuentes

- Codex update_plan spec: https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/plan_spec.rs
- Codex plan handler: https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/plan.rs
- Codex Plan Mode vs update_plan: https://github.com/openai/codex/blob/main/codex-rs/collaboration-mode-templates/templates/plan.md
- MCP Tasks extension: https://tasks.extensions.modelcontextprotocol.io/specification/draft/tasks
- Claude Code environment variables / task lists: https://code.claude.com/docs/en/env-vars
