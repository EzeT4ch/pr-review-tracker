# Estándar de revisión y reporte

Este documento define cómo `prt analyze` revisa un PR y cómo se reporta el resultado. El objetivo es que todos los PRs se lean igual, sin importar el agente (Claude Code, Codex, opencode) ni quién los revise.

## 1. El panel

Cada PR lo revisa un **panel de revisores especializados**. Cada revisor es un agente con un foco acotado, su propia sesión persistida por PR y las mismas reglas de severidad y formato:

| Revisor | Prefijo | Foco |
|---|---|---|
| `architecture` | `ARQ` | Capas, límites, contratos públicos, acoplamiento, consistencia con el repo, costo de cambio |
| `bugs` | `BUG` | Lógica, bordes, nulos, errores, concurrencia, persistencia, regresiones, tests que no prueban |
| `performance` | `PERF` | Acceso a datos (N+1, índices, paginación), complejidad, I/O, memoria, caché |
| `security` | `SEC` | AuthN/AuthZ, inyección, validación de entrada, secretos y datos sensibles, cripto, dependencias |

Los revisores corren en paralelo y en modo **solo lectura**. Cada uno recibe:

- el **diff** del PR (descargado de Bitbucket) y un archivo con descripción, ramas y commits;
- el **contexto del proyecto** (`context.md`) y los documentos listados en `review.json → contextFiles`;
- como directorio de trabajo, el **checkout local** si está asociado (`prt map` o la página Repos de la web), o si no el **clon gestionado por prt** en la punta de la rama de destino del PR, para juzgar el cambio contra el proyecto tal como está hoy (la base queda registrada en el reporte);
- en pasadas siguientes, **sus hallazgos anteriores**, para marcar cada uno como `still_open` o `resolved` conservando su id.

## 2. Severidades

| Severidad | Icono | Significa | Efecto en el veredicto |
|---|---|---|---|
| `blocker` | 🔴 | No se puede mergear: pérdida de datos, vulnerabilidad explotable, caída, contrato o requisito roto | `block` |
| `major` | 🟠 | Corregir antes de mergear o con compromiso explícito | `request_changes` |
| `minor` | 🟡 | Conviene corregir, no bloquea | `approve_with_comments` |
| `info` | 🔵 | Observación o sugerencia opcional | ninguno |

Los sinónimos que usan los distintos modelos (`critical`, `high`, `medium`, `low`, `nit`…) se normalizan a estas cuatro.

## 3. Veredicto

- El veredicto de cada revisor **no puede ser más leve** que su hallazgo abierto más grave; si lo es, `prt` lo ajusta y lo avisa.
- El veredicto del panel es **el peor** de los revisores con reporte válido.
- Se muestra con las mismas etiquetas en todo el sistema: `APROBAR`, `APROBAR CON CAMBIOS`, `CAMBIOS REQUERIDOS`, `BLOQUEADO`.

## 4. Formato de respuesta (contrato con el agente)

Cada revisor responde **solo** con este JSON. El formato es fijo y no se configura, porque el parser depende de él:

```json
{
  "reviewer": "security",
  "verdict": "approve | approve_with_comments | request_changes | block",
  "summary": "2 a 4 oraciones",
  "findings": [
    {
      "id": "SEC-1",
      "severity": "blocker | major | minor | info",
      "category": "subtema",
      "title": "hasta 80 caracteres",
      "file": "ruta/relativa o null",
      "line": 42,
      "description": "qué pasa, en qué escenario y por qué importa",
      "recommendation": "qué cambiar",
      "confidence": "high | medium | low",
      "status": "new | still_open | resolved"
    }
  ],
  "questions": ["decisiones que requieren criterio humano"]
}
```

Normalización que aplica `prt`:

- Acepta el JSON suelto, dentro de un bloque ` ```json ` o rodeado de texto.
- Corrige ids que no siguen `PREFIJO-n` y conserva los que sí, para seguir un hallazgo entre pasadas.
- Normaliza rutas (`\` → `/`) y líneas (`"42-50"` → `42`).
- Ignora, con aviso, los hallazgos sin título ni descripción.
- Si la respuesta no cumple el contrato, pide **una corrección** en la misma sesión. Si vuelve a fallar, el revisor queda como `formato inválido` y el panel sigue con el resto.

## 5. Reglas de revisión

Las define `standards.md` (versión incorporada en `src/Prt.Core/Panel/Defaults/standards.md`). En resumen:

- Revisar el cambio y lo que impacta directamente; lo preexistente, como `info`.
- Cada hallazgo con evidencia: archivo, línea, escenario concreto. Sin evidencia, confianza `low` o pregunta.
- Un hallazgo por problema, con una recomendación accionable.
- Las decisiones de arquitectura o negocio van en `questions`, redactadas para responderse sin releer el PR.

## 6. Reporte

Cada corrida genera, siempre con la misma estructura:

1. **Resultado**: veredicto del panel, fecha, head y conteo por severidad.
2. **Tabla por revisor**: agente, veredicto, hallazgos abiertos por severidad y resueltos.
3. **Hallazgos abiertos**: ordenados por severidad, en una tabla `| ID | Sev. | Hallazgo | Ubicación | Acción | Estado |`. La ubicación es `Archivo:línea`, la acción es la primera oración de la recomendación y en Obsidian el ID enlaza a su detalle (block ref `^arq-1`).
4. **Detalle** de cada hallazgo: severidad, categoría, ubicación con la ruta completa, confianza, estado, descripción y recomendación.
5. **Resueltos desde la pasada anterior**.
6. **Preguntas para el revisor humano**, como checklist.
7. **Resúmenes por revisor**, y errores si algún revisor falló.

En Obsidian queda como `PR Reviews/<ws>/<repo>/PR-<n>/Análisis <fecha> (run <id>).md`, en la misma carpeta que la nota principal del PR. La nota del PR muestra la última corrida (sección *Panel de revisores*) y la agrega a la timeline. En la terminal se ve con `prt report <pr>` (`--markdown` o `--json` para exportar).

## 7. Personalización por proyecto

La configuración se resuelve por capas; cada capa pisa a la anterior:

1. **Incorporado**: este estándar y los cuatro revisores.
2. **Global**: `~/.config/prt/` (Windows: `%APPDATA%\prt\`). Tus reglas para todos los proyectos.
3. **Repo**: `<checkout>/.prt/`. Se versiona con el código y lo comparte el equipo.
4. **Usuario por proyecto**: `~/.config/prt/projects/<workspace>/<repo>/`. Ajustes personales que no querés commitear.

Cada capa puede tener:

```
reviewers/<nombre>.md   # un revisor: reemplaza al del mismo nombre o agrega uno nuevo
standards.md            # reemplaza el estándar de revisión
context.md              # se ACUMULA entre capas: dominio, arquitectura, volúmenes, qué no reportar
review.json             # se combina propiedad por propiedad
```

Un revisor es un Markdown con frontmatter; el cuerpo es su foco:

```markdown
---
name: pci
title: Cumplimiento PCI
id_prefix: PCI
agent: codex        # opcional: si no, el default del proyecto o el global
enabled: true
order: 5
---
Revisás que ningún dato de tarjeta (PAN, CVV) se almacene, loguee o viaje sin cifrar...
```

`review.json`:

```json
{
  "reviewers": ["architecture", "bugs", "security", "pci"],
  "defaultAgent": "claude",
  "agents": { "security": "codex" },
  "maxParallel": 4,
  "contextFiles": ["docs/ARCHITECTURE.md", "docs/adr/"]
}
```

- `reviewers`: cuáles corren por defecto y en qué orden. Los demás se pueden pedir con `prt analyze -r`.
- `agents`: agente por revisor; pisa el `agent` del archivo del revisor.

Desde el panel web (Revisores, o Repos → Revisores del proyecto) se editan los revisores de la capa global, personal o del repo, sin tocar archivos a mano. `prt reviewers init <ws/repo>` copia todo lo incorporado a `.prt/` para empezar a editar (con `--scope user` o `--scope global` va a las otras capas). `prt reviewers list <ws/repo>` muestra qué revisor aplica y de qué archivo sale.
