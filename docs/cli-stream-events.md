# Claude CLI stream-json: полный инвентарь событий

**Источник:** прямой запуск `claude -p --output-format stream-json --verbose` (CLI v2.1.119) с захватом stdout. Все примеры лежат в `Saved/ClaudeScratch/cli-events/*.jsonl`.

**Цель:** показать ВСЕ типы событий и поля, которые CLI способен эмитить, чтобы Runner мог корректно их обрабатывать. Раннер сейчас видит только подмножество.

---

## 1. Карта верхнеуровневых типов событий

| `type` | Когда приходит | Раннер парсит сейчас? |
|---|---|---|
| `system` | Старт сессии, статус, sub-agent lifecycle, hook lifecycle | Частично (только `model`, без `subtype`) |
| `rate_limit_event` | Перед каждым обращением к модели | **Игнорируется** |
| `assistant` | Каждый content_block, который сгенерировала модель | Да, но обрезанно |
| `user` | Каждый tool_result или replay входящего сообщения | Частично |
| `stream_event` | Только при `--include-partial-messages`: дельты | **Не запрашивается** |
| `result` | Финал turn'а | Частично |

Ниже — раскрытие каждого по полям.

---

## 2. `system` — пять подтипов через `subtype`

Раннер сейчас обрабатывает их одинаково, но CLI различает 5+ подтипов. Поле `subtype` определяет смысл.

### 2.1 `system/init` — старт каждого turn'а

⚠️ **Важно:** `init` приходит на КАЖДОМ turn'е в persistent stdin-сессии, не только при первом запуске. Раннер должен это учитывать (например, не сбрасывать stat-ы по init).

```json
{
  "type":"system","subtype":"init",
  "cwd":"D:\\Workspaces\\Claude\\ShikigamiProject",
  "session_id":"73294113-49dd-4a34-a2eb-49842d869a39",
  "tools":["Bash","Read"],
  "mcp_servers":[],
  "model":"claude-opus-4-7[1m]",
  "permissionMode":"default",
  "slash_commands":["update-config","debug",...,"team-onboarding"],
  "apiKeySource":"none",
  "claude_code_version":"2.1.119",
  "output_style":"default",
  "agents":["Explore","general-purpose","Plan","statusline-setup",...],
  "skills":["update-config","debug",...],
  "plugins":[{"name":"frontend-design","path":"...","source":"..."}],
  "analytics_disabled":false,
  "uuid":"dcbdb232-d864-48b6-aee4-4df9cbe751b9",
  "memory_paths":{"auto":"C:\\Users\\Admin\\.claude\\projects\\...\\memory\\"},
  "fast_mode_state":"off"
}
```

| Поле | Смысл | Использовать в UI? |
|---|---|---|
| `cwd` | Рабочий каталог процесса | Header tooltip |
| `session_id` | UUID сессии (тот же, что передан в `--session-id`) | Уже есть |
| `tools` | Какие инструменты доступны модели в этой сессии | Можно показать |
| `mcp_servers` | Список подключённых MCP-серверов | Показывать в header |
| `model` | Какая модель реально активна (с `[1m]` для 1M context) | Header |
| `permissionMode` | `default` / `bypassPermissions` / `plan` / `acceptEdits` / `dontAsk` | Header badge |
| `slash_commands` | Все слэш-команды, доступные в этой сессии | Можно для diag |
| `apiKeySource` | `none` (OAuth) / `apikeyhelper` / `env` / `bedrock` / `vertex` | Diag |
| `claude_code_version` | Версия CLI | Header tooltip |
| `output_style` | Стиль ответа | Diag |
| `agents` | Какие subagent-types доступны для Task/Agent tool | Полезно для horde-режима |
| `skills` | Доступные user-invocable skills | Diag |
| `plugins` | Активные плагины (имя, путь, источник) | Diag |
| `analytics_disabled` | Telemetry off? | Diag |
| `uuid` | UUID этого конкретного init-события | — |
| `memory_paths.auto` | Путь auto-memory (наш `MEMORY.md` + файлы) | Diag |
| `fast_mode_state` | `off` / `on` (Opus 4.6 fast mode) | Header |

### 2.2 `system/status` — индикатор активности модели

Появляется только при `--include-partial-messages`. Позволяет показать "модель думает / запрашивает" ДО первого токена.

```json
{"type":"system","subtype":"status","status":"requesting","uuid":"...","session_id":"..."}
```

Возможные значения `status`: точно встречен `requesting`. По коду CLI вероятны другие (например, `tool_executing`), но в наших прогонах больше ничего не пришло.

### 2.3 `system/task_started` / `task_progress` / `task_notification` — sub-agent lifecycle

Эти события CLI эмитит автоматически, когда модель вызывает Task tool (внутри API он называется `Agent`, а не `Task`!). Они идут **в дополнение** к `tool_use`/`tool_result`-парам и дают живой ход sub-agent'а.

```json
{
  "type":"system","subtype":"task_started",
  "task_id":"a88241f81743c3056",
  "tool_use_id":"toolu_01FrV5KEXAmyMAQQaN33mMi6",
  "description":"Count .csproj files in src/",
  "task_type":"local_agent",
  "prompt":"Count how many .csproj files...",
  "uuid":"...","session_id":"..."
}
```

```json
{
  "type":"system","subtype":"task_progress",
  "task_id":"a88241f81743c3056",
  "tool_use_id":"toolu_01FrV5KEXAmyMAQQaN33mMi6",
  "description":"Finding src/**/*.csproj",
  "usage":{"total_tokens":1795,"tool_uses":1,"duration_ms":770},
  "last_tool_name":"Glob",
  "uuid":"...","session_id":"..."
}
```

```json
{
  "type":"system","subtype":"task_notification",
  "task_id":"a88241f81743c3056",
  "tool_use_id":"toolu_01FrV5KEXAmyMAQQaN33mMi6",
  "status":"completed",
  "output_file":"",
  "summary":"Count .csproj files in src/",
  "usage":{"total_tokens":1931,"tool_uses":1,"duration_ms":1843},
  "uuid":"...","session_id":"..."
}
```

Раннер их сейчас не показывает. Это ИДЕАЛЬНЫЙ источник для строки "Шаг" / "что сейчас делает sub-agent".

### 2.4 `system/hook_started` / `hook_response` — выполнение hooks

Только при `--include-hook-events`. Раннер этот флаг не передаёт.

```json
{"type":"system","subtype":"hook_started",
 "hook_id":"55a909be-...","hook_name":"PreToolUse:Bash","hook_event":"PreToolUse",
 "uuid":"...","session_id":"..."}
```

```json
{"type":"system","subtype":"hook_response",
 "hook_id":"55a909be-...","hook_name":"PreToolUse:Bash","hook_event":"PreToolUse",
 "output":"HOOK_PRE_BASH\n","stdout":"HOOK_PRE_BASH\n","stderr":"","exit_code":0,
 "outcome":"success",
 "uuid":"...","session_id":"..."}
```

Возможные `hook_event`: `PreToolUse`, `PostToolUse`, `UserPromptSubmit`, `Stop`, `SubagentStop`, `Notification`, и т.д. — что настроено в `~/.claude/settings.json`.

---

## 3. `rate_limit_event` — текущее состояние лимитов

```json
{
  "type":"rate_limit_event",
  "rate_limit_info":{
    "status":"allowed",
    "resetsAt":1777129200,
    "rateLimitType":"five_hour",
    "overageStatus":"allowed",
    "overageResetsAt":1777593600,
    "isUsingOverage":false
  },
  "uuid":"...","session_id":"..."
}
```

| Поле | Смысл |
|---|---|
| `status` | `allowed` / `warning` / `exceeded` |
| `resetsAt` | Unix timestamp основного окна |
| `rateLimitType` | `five_hour` / `weekly` / `weekly_opus` |
| `overageStatus` | `allowed` / ... — состояние overage-окна |
| `overageResetsAt` | Когда обнулится overage |
| `isUsingOverage` | Уже жжём overage-бюджет? |

Раннер ИГНОРИРУЕТ. Это плохо: пользователь имеет право знать "до сброса 4ч 12мин, статус warning, ты в overage".

---

## 4. `assistant` — модельный output

Каждый content_block приходит **отдельным** `assistant`-событием с одним и тем же `message.id`. Раньше я думал, что один `assistant` = одно сообщение целиком. Это не так — сообщение **дробится по блокам**.

Полная форма:

```json
{
  "type":"assistant",
  "message":{
    "model":"claude-opus-4-7",
    "id":"msg_01...",
    "type":"message",
    "role":"assistant",
    "content":[<один блок>],
    "stop_reason":null,
    "stop_sequence":null,
    "stop_details":null,
    "usage":{...},
    "context_management":null
  },
  "diagnostics":null,
  "parent_tool_use_id":null,
  "session_id":"...",
  "uuid":"..."
}
```

### 4.1 Поля верхнего уровня

| Поле | Смысл | Раннер? |
|---|---|---|
| `parent_tool_use_id` | `null` для прямых; `"toolu_..."` если это вывод sub-agent'а внутри Task | **Не парсит** — нет различения main vs sub-agent в логе |
| `session_id` | Сессия | — |
| `uuid` | UUID события (для дедупликации) | — |
| `diagnostics` | Обычно `null`. Может быть `{"cache_miss_reason":{"type":"unavailable"}}` и т.п. | Не парсит |

### 4.2 `message.usage` — токены текущего шага

```json
{
  "input_tokens":5,
  "cache_creation_input_tokens":5675,
  "cache_read_input_tokens":3057,
  "cache_creation":{
    "ephemeral_5m_input_tokens":0,
    "ephemeral_1h_input_tokens":5675
  },
  "output_tokens":2,
  "service_tier":"standard",
  "inference_geo":"not_available"
}
```

Раннер парсит `input_tokens` + `cache_creation_input_tokens` + `cache_read_input_tokens` + `output_tokens`. ПРОПУСКАЕТ:
- `cache_creation.ephemeral_1h_input_tokens` — кэш на 1 час (дороже).
- `cache_creation.ephemeral_5m_input_tokens` — кэш на 5 минут (стандартный).
- `service_tier` — `standard` / `priority` / `batch`.
- `inference_geo` — где исполнялась inference.

### 4.3 `message.content[]` — блоки

#### `text`

```json
{"type":"text","text":"Cats are small, agile predators..."}
```

Раннер показывает.

#### `thinking` (Opus 4.7+)

```json
{
  "type":"thinking",
  "thinking":"",
  "signature":"EpcDClkIDRgCKkD/1Qm/S9LnX7hCXoWSx..."
}
```

`thinking` всегда пустой в 4.7+ (зашифровано). `signature` — base64-блоб ~4000 символов, нужен только для multi-turn-контекста CLI. Раннер хорошо знает об этом ограничении (есть в CLAUDE.md).

После пустого thinking-блока модель часто эмитит обычный `text`-блок с переформулированным reasoning'ом — это и есть "видимое мышление" в 4.7+. Раннер это не помечает как thinking — просто рендерит как обычный text.

#### `tool_use`

```json
{
  "type":"tool_use",
  "id":"toolu_011wBcHQSDahheNLwb4Htdd6",
  "name":"Bash",
  "input":{"command":"ls | head -n 2","description":"List first two entries"},
  "caller":{"type":"direct"}
}
```

| Поле | Смысл | Раннер? |
|---|---|---|
| `id` | toolu_id, ключ для матчинга с tool_result | Парсит |
| `name` | Имя инструмента (`Bash`, `Read`, `Glob`, `Grep`, `Edit`, `Write`, `Agent` для Task, `mcp__Server__tool` для MCP) | Парсит, но `ExtractToolDetail` знает только 6 имён — остальные дают пустую строку |
| `input` | Аргументы — структура зависит от tool | Парсит частично |
| `caller.type` | `direct` (модель вызвала сама) / возможны другие | **Не парсит** |

⚠️ Task tool называется в API **`Agent`**. Раннеровский `ExtractToolDetail` его не знает → детали sub-agent-вызова теряются.

#### `image` (теоретически)

Когда модель возвращает изображение (редко, Opus умеет). Не встречалось в наших прогонах, но формат документирован:

```json
{"type":"image","source":{"type":"base64","media_type":"image/png","data":"..."}}
```

Раннер обрабатывать не умеет.

### 4.4 `message.context_management`

Обычно `null`. Когда CLI делает context-compaction (autocompact / hooks), здесь появляется `{"applied_edits":[...]}`. Раннер не парсит.

---

## 5. `user` — два разных смысла

### 5.1 user/tool_result — ответ инструмента в контекст модели

```json
{
  "type":"user",
  "message":{
    "role":"user",
    "content":[{
      "tool_use_id":"toolu_011wBcHQSDahheNLwb4Htdd6",
      "type":"tool_result",
      "content":"Build\nCLAUDE.md",
      "is_error":false
    }]
  },
  "parent_tool_use_id":null,
  "session_id":"...",
  "uuid":"...",
  "timestamp":"2026-04-25T10:20:55.958Z",
  "tool_use_result":{...}
}
```

⚠️ Поле `tool_use_result` (на ВЕРХНЕМ уровне события, не внутри message) — это **типизированный** ответ инструмента, который раннер полностью теряет:

**Для Bash:**
```json
"tool_use_result":{
  "stdout":"Build\nCLAUDE.md",
  "stderr":"",
  "interrupted":false,
  "isImage":false,
  "noOutputExpected":false
}
```

**Для Read:**
```json
"tool_use_result":{
  "type":"text",
  "file":{
    "filePath":"D:\\...\\CLAUDE.md",
    "content":"# Shikigami Project",
    "numLines":1,
    "startLine":1,
    "totalLines":457
  }
}
```

**Для Task/Agent (sub-agent):**
```json
"tool_use_result":{
  "status":"completed",
  "prompt":"Count how many .csproj files...",
  "agentId":"a88241f81743c3056",
  "agentType":"Explore",
  "content":[{"type":"text","text":"**Count: 3 .csproj files**..."}],
  "totalDurationMs":1844,
  "totalTokens":1963,
  "totalToolUseCount":1,
  "usage":{...},
  "toolStats":{
    "readCount":0,"searchCount":1,"bashCount":0,
    "editFileCount":0,"linesAdded":0,"linesRemoved":0,
    "otherToolCount":0
  }
}
```

| Поле | Смысл | Раннер? |
|---|---|---|
| `tool_use_id` | id `tool_use`-блока, к которому это ответ | Парсит |
| `is_error` | Ошибка инструмента | **Не использует** |
| `timestamp` | Время выполнения tool | **Не парсит** |
| `parent_tool_use_id` | Если это tool_result ВНУТРИ sub-agent'а — id Task | **Не парсит** |
| `tool_use_result.toolStats` (sub-agent) | Сводка по инструментам sub-agent'а | Потеря — полезно для горды |

### 5.2 user/replay — эхо входящего stdin

При `--replay-user-messages` CLI эхоит входящие stdin-сообщения с `isReplay: true`:

```json
{
  "type":"user",
  "message":{"role":"user","content":"Remember the secret number 47..."},
  "session_id":"...","parent_tool_use_id":null,
  "uuid":"...","timestamp":"2026-04-25T10:22:34.886Z",
  "isReplay":true
}
```

Полезно для подтверждения "сообщение принято CLI". Раннер не использует — но через флаг `--replay-user-messages` можно было бы строить таймлайн "пользователь → cli → модель → ответ".

---

## 6. `stream_event` — partial messages (только при `--include-partial-messages`)

Раннер этот флаг не передаёт → всегда работает в "буферном" режиме: текст приходит ОДНОЙ глыбой по завершении блока. Поэтому 5-секундный ответ ждёт 5 секунд тишины и потом "плюх" — полное сообщение.

С флагом получаем настоящий стриминг:

```json
{"type":"stream_event","event":{"type":"message_start","message":{...empty content...}},
 "session_id":"...","parent_tool_use_id":null,"uuid":"...","ttft_ms":1477}

{"type":"stream_event","event":{"type":"content_block_start","index":0,
 "content_block":{"type":"text","text":""}},...}

{"type":"stream_event","event":{"type":"content_block_delta","index":0,
 "delta":{"type":"text_delta","text":"Cats"}},...}

{"type":"stream_event","event":{"type":"content_block_delta","index":0,
 "delta":{"type":"text_delta","text":" are small, agile predators..."}},...}

{"type":"stream_event","event":{"type":"content_block_stop","index":0},...}

{"type":"stream_event","event":{"type":"message_delta",
 "delta":{"stop_reason":"end_turn","stop_sequence":null,"stop_details":null},
 "usage":{...},
 "context_management":{"applied_edits":[]}},...}

{"type":"stream_event","event":{"type":"message_stop"},...}
```

| Внутреннее `event.type` | Когда |
|---|---|
| `message_start` | Перед первым блоком; даёт `ttft_ms` (time-to-first-token) |
| `content_block_start` | Открытие блока (text/thinking/tool_use); `index` указывает позицию |
| `content_block_delta` | Кусок: `{type:"text_delta",text:...}`, `{type:"thinking_delta",thinking:...}`, `{type:"input_json_delta",partial_json:...}` |
| `content_block_stop` | Закрытие блока |
| `message_delta` | Финал: `stop_reason`, итоговый `usage`, `context_management` |
| `message_stop` | Самый конец |

После `content_block_stop` обычно ВДОБАВОК приходит полный (агрегированный) `assistant`-событие. То есть с флагом получаем И deltas, И финальную "сборку".

**`stop_reason`** в `message_delta`: `end_turn` / `max_tokens` / `stop_sequence` / `tool_use` / `pause_turn` / `refusal`.

---

## 7. `result` — финал turn'а

Полная форма:

```json
{
  "type":"result",
  "subtype":"success",
  "is_error":false,
  "api_error_status":null,
  "duration_ms":7883,
  "duration_api_ms":9079,
  "num_turns":2,
  "result":"There are **3** .csproj files in `src/`:...",
  "stop_reason":"end_turn",
  "session_id":"...",
  "total_cost_usd":0.1022885,
  "usage":{
    "input_tokens":6,
    "cache_creation_input_tokens":13150,
    "cache_read_input_tokens":12784,
    "output_tokens":355,
    "server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},
    "service_tier":"standard",
    "cache_creation":{"ephemeral_1h_input_tokens":13150,"ephemeral_5m_input_tokens":0},
    "inference_geo":"",
    "iterations":[{"input_tokens":1,"output_tokens":103,"cache_read_input_tokens":12784,...}],
    "speed":"standard"
  },
  "modelUsage":{
    "claude-haiku-4-5-20251001":{
      "inputTokens":3979,"outputTokens":165,
      "cacheReadInputTokens":0,"cacheCreationInputTokens":0,
      "webSearchRequests":0,"costUSD":0.004804,
      "contextWindow":200000,"maxOutputTokens":32000
    },
    "claude-opus-4-7[1m]":{
      "inputTokens":6,"outputTokens":355,
      "cacheReadInputTokens":12784,"cacheCreationInputTokens":13150,
      "webSearchRequests":0,"costUSD":0.0974845,
      "contextWindow":1000000,"maxOutputTokens":64000
    }
  },
  "permission_denials":[],
  "terminal_reason":"completed",
  "fast_mode_state":"off",
  "uuid":"..."
}
```

| Поле | Смысл | Раннер? |
|---|---|---|
| `subtype` | `success` / `error_max_turns` / `error_during_execution` | **Не парсит** |
| `is_error` | bool | Парсит |
| `api_error_status` | Код ошибки API (`overloaded_error` etc.) или null | **Не парсит** |
| `duration_ms` | Wall-clock время turn'а | **Не парсит** |
| `duration_api_ms` | Время на API-вызовы | **Не парсит** |
| `num_turns` | Сколько внутренних raund-trip'ов модели было | **Не парсит** |
| `result` | Финальный текст | Парсит |
| `stop_reason` | Почему модель остановилась | **Не парсит** |
| `total_cost_usd` | Накопленная стоимость С НАЧАЛА сессии (не данного turn'а!) | Парсит |
| `usage.iterations[]` | Поlist токенов по каждому внутреннему API-вызову | **Не парсит** |
| `usage.server_tool_use` | Сколько раз модель использовала встроенные web_search/web_fetch | **Не парсит** |
| `usage.speed` | `standard` / `fast` (Opus 4.6 fast mode) | **Не парсит** |
| `modelUsage[<model>]` | Per-model breakdown — критично, т.к. в одной сессии работают и Opus (главный), и Haiku (sub-agent'ы, suggestions, hooks) | Парсит ТОЛЬКО первую модель — теряет полную раскладку |
| `permission_denials[]` | Список того, что модель пыталась сделать, но получила отказ | **Не парсит** |
| `terminal_reason` | `completed` / `interrupted` / `error` / `max_budget_reached` | **Не парсит** |
| `fast_mode_state` | Активирован ли fast mode | **Не парсит** |

⚠️ `total_cost_usd` — кумулятивно по всей сессии. То есть второй turn в нашем тесте показал 0.074, а первый — 0.037. Если раннер показывает stat "стоимость", он должен либо вычитать предыдущий total, либо помнить, что это не "на этот ход", а "всего".

---

## 8. Что Раннер пропускает (gap-список)

| # | Что пропущено | Что показать пользователю | Серьёзность |
|---|---|---|---|
| 1 | `system/init.permissionMode`, `mcp_servers`, `tools` | Header / tooltip "сессия запущена с такими правами и инструментами" | Средняя |
| 2 | `system/status` (требует partial flag) | "modeling..." индикатор до первого токена | Средняя |
| 3 | `system/task_started/progress/notification` | Реальный прогресс sub-agent'а, а не загадочный `(thinking...)` | **Высокая** |
| 4 | `system/hook_started/response` (требует hook flag) | Видимость работы hooks | Низкая |
| 5 | `rate_limit_event` | "лимит сбросится через 4ч 12мин, ты в overage" | **Высокая** |
| 6 | `assistant.parent_tool_use_id` | Раннер не отличает реплики самой модели от реплик sub-agent'а внутри Task → лог сливается | **Высокая** |
| 7 | `assistant.diagnostics` (`cache_miss_reason`) | "почему дорого: cache miss type=unavailable" | Низкая |
| 8 | `usage.cache_creation.ephemeral_1h_input_tokens` vs `_5m_` | Точная стоимость кэша | Низкая |
| 9 | `tool_use.caller.type` | Откуда вызов | Низкая |
| 10 | `tool_use_result` (типизированная struct) для Bash/Read/Task | stderr отдельно, totalDurationMs, agent toolStats — всё это ценно | **Высокая** |
| 11 | `tool_result.is_error` | Раскрашивать failed-tool-call красным | Средняя |
| 12 | `--include-partial-messages` → реальный стриминг текста | Текст по символам, как в обычном CLI Claude Code | **Высокая** (UX) |
| 13 | `result.stop_reason` | "max_tokens", "tool_use", "refusal" — диагностика | Высокая |
| 14 | `result.permission_denials[]` | Список отказов в правах в этом turn'е | Средняя |
| 15 | `result.modelUsage` (все модели, не первая) | Точный per-model cost (Haiku отдельно от Opus) | Средняя |
| 16 | `result.duration_ms` / `duration_api_ms` | "ход занял 7.8с / API 9с" | Низкая |
| 17 | `result.num_turns` | Сколько раз модель сходила на API в этом ходе | Низкая |
| 18 | `result.terminal_reason` | Отличить `completed` от `interrupted` | Средняя |
| 19 | `total_cost_usd` кумулятивно vs per-turn | Раннер показывает кумулятивный как будто per-turn? Нужно проверить | Высокая (если баг) |
| 20 | Task tool именуется `Agent` в API | `ExtractToolDetail` не знает → деталь sub-agent-call'а пуста | Средняя |
| 21 | `assistant`-событие на каждый block, message_id один | Раннер сейчас прибавляет `ToolsUsed++` каждый раз — корректно для tool_use, но если бы он считал "сообщения", получил бы N вместо 1 | Низкая |

---

## 9. Что добавить во флаги запуска CLI

Текущая команда раннера (`CliSession.Start`):
```
claude -p
  --input-format stream-json
  --output-format stream-json
  --verbose
  --strict-mcp-config
  [--session-id | --resume]
  [--agent | --model] [--allowedTools] [--effort]
```

Рекомендуется добавить:

| Флаг | Зачем |
|---|---|
| `--include-partial-messages` | Получать `stream_event` с дельтами текста и `system/status` ДО первого токена. Включить рендеринг по дельтам в логе |
| `--include-hook-events` | Видеть `hook_started`/`hook_response` если у пользователя настроены hooks |
| `--replay-user-messages` | Эхо отправленных сообщений от CLI — точная отметка "CLI принял" |

Эти три флага не ломают совместимость, только добавляют события.

---

## 10. Артефакты

Все сырые JSONL-дампы:

| Файл | Сценарий |
|---|---|
| `Saved/ClaudeScratch/cli-events/01_simple.jsonl` | Простой текстовый ответ |
| `Saved/ClaudeScratch/cli-events/02_thinking.jsonl` | `--effort xhigh`, thinking + signature |
| `Saved/ClaudeScratch/cli-events/03_tools.jsonl` | Bash + Read |
| `Saved/ClaudeScratch/cli-events/04_partial.jsonl` | `--include-partial-messages` (deltas) |
| `Saved/ClaudeScratch/cli-events/05_hooks.jsonl` | `--include-hook-events` без hooks (пусто) |
| `Saved/ClaudeScratch/cli-events/05b_hooks.jsonl` | `--include-hook-events` с inline-hooks через `--settings` |
| `Saved/ClaudeScratch/cli-events/06_multiturn.jsonl` | persistent stdin, два turn'а, `--replay-user-messages` |
| `Saved/ClaudeScratch/cli-events/07_subagent.jsonl` | Task→Agent vызов Explore-сабагента |
| `Saved/ClaudeScratch/cli-events/08_parallel.jsonl` | Параллельные Bash-вызовы |
