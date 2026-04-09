# codex-multi Spec

## 1. Goal

Нужна минималистичная CLI-утилита на C#, которая помогает переключать несколько аккаунтов `Codex` без промежуточного HTTP-сервера и без OmniRoute.

Утилита должна:

- хранить несколько профилей `auth.json`;
- уметь быстро переключать активный `~/.codex/auth.json`;
- запускать обычный `codex` как дочерний процесс;
- отслеживать ошибку rate limit;
- при rate limit автоматически переключать профиль и поднимать `codex resume <session_id> "<prompt>"`.

Главная идея: это не прокси и не сервер. Все запросы в API по-прежнему делает сам `codex`. Утилита только управляет локальным `auth.json`, процессом `codex` и повторным запуском через `resume`.

## 2. Non-goals

Утилита не должна:

- подменять HTTP API;
- работать как локальный gateway;
- модифицировать `.codex/config.toml`, кроме явно запрошенных сценариев;
- пытаться продолжить запрос "в середине стрима";
- чинить ошибки, не связанные с rate limit;
- поддерживать одновременную безопасную работу нескольких независимых `codex-multi` в одном и том же `~/.codex` без явной блокировки.

## 3. High-level behavior

Базовый сценарий:

1. Пользователь запускает `codex-multi` вместо `codex`.
2. `codex-multi` выбирает активный профиль.
3. `codex-multi` атомарно подменяет `~/.codex/auth.json`.
4. `codex-multi` запускает настоящий `codex` как дочерний процесс.
5. Во время работы `codex-multi` отслеживает локальную session log JSONL и/или состояние дочернего процесса.
6. Если обнаружен `Rate limit reached ...`, текущий запуск считается прерванным по лимиту.
7. `codex-multi` переключает профиль на следующий доступный.
8. `codex-multi` снова атомарно подменяет `~/.codex/auth.json`.
9. `codex-multi` запускает `codex resume <session_id> "<resume prompt>"`.

## 4. Key assumption

У локального `codex` есть команда:

```text
codex resume [SESSION_ID] [PROMPT]
```

То есть после обрыва по rate limit можно не начинать новый чат, а поднимать ту же сессию с новым user prompt, например:

```text
Continue from the last point. The previous attempt was interrupted by a rate limit.
```

Это новый ход в той же сессии, а не восстановление "в середине токена".

## 5. UX requirements

Утилита должна быть минималистичной. Основной UX:

```bash
codex-multi
codex-multi "fix this bug"
codex-multi resume --last
codex-multi auth list
codex-multi auth save work
codex-multi auth use work
codex-multi auth current
codex-multi auth remove work
```

Требование: пользователь должен по возможности продолжать думать про `codex`, а не про внутреннюю механику wrapper.

## 6. CLI design

Предлагаемая структура команд.

### 6.1 Main mode

```bash
codex-multi [args...]
```

Поведение:

- если подкоманда не распознана как служебная, все аргументы считаются аргументами для настоящего `codex`;
- `codex-multi` сначала подставляет активный профиль, затем запускает `codex` с этими аргументами;
- при rate limit выполняет ротацию профиля и повторный запуск через `resume`, если это возможно.

### 6.2 Auth profile management

```bash
codex-multi auth list
codex-multi auth save <name>
codex-multi auth import <name> --from <path>
codex-multi auth use <name>
codex-multi auth current
codex-multi auth remove <name>
codex-multi auth show <name>
```

Поведение:

- `save <name>`: сохранить текущий `~/.codex/auth.json` как профиль;
- `import <name> --from <path>`: импортировать профиль из произвольного пути;
- `use <name>`: сделать профиль активным и сразу подменить `~/.codex/auth.json`;
- `current`: показать активный профиль и краткую информацию из его `auth.json`;
- `list`: показать все профили и отметить активный;
- `remove`: удалить профиль;
- `show`: показать метаданные профиля без печати секретов.

### 6.3 Optional utility commands

Можно добавить:

```bash
codex-multi next
codex-multi prev
codex-multi doctor
```

Но это optional. Для MVP не обязательно.

## 7. Storage layout

Утилита должна хранить свои данные отдельно от `~/.codex`.

Предлагаемый layout:

```text
~/.config/codex-multi/
  config.json
  profiles/
    personal/
      auth.json
      meta.json
    work/
      auth.json
      meta.json
  logs/
  locks/
```

### 7.1 config.json

Минимально:

```json
{
  "activeProfile": "personal",
  "profileOrder": ["personal", "work"],
  "resumePrompt": "Continue from the last point. The previous attempt was interrupted by a rate limit.",
  "codexExecutable": "codex"
}
```

### 7.2 meta.json

Метаданные без секретов:

```json
{
  "name": "personal",
  "savedAtUtc": "2026-04-09T10:00:00Z",
  "authMode": "chatgpt",
  "accountId": "a799175e-e863-4c89-84e1-fa0aa93798ac",
  "emailHint": "pat***@outlook.com"
}
```

## 8. Security requirements

- Профили содержат секреты и refresh/access tokens.
- Файлы профилей должны иметь максимально приватные права доступа.
- В логах нельзя печатать токены, полный `auth.json` или секретные поля.
- При показе профиля разрешены только маскированные значения.
- Нельзя хранить секреты в консольном output по умолчанию.

## 9. Auth.json switching

Подмена `~/.codex/auth.json` должна быть атомарной.

Требуемый алгоритм:

1. Прочитать профиль из `~/.config/codex-multi/profiles/<name>/auth.json`.
2. Записать временный файл рядом с `~/.codex/auth.json`.
3. Выполнить atomic rename/replace.
4. Проверить, что финальный файл читается и содержит валидный JSON.

Нельзя:

- писать файл "поверх" без temp file;
- оставлять частично записанный `auth.json`;
- переключать профиль без lock-механизма.

## 10. Single-instance / locking

Так как все профили используют один и тот же `~/.codex/auth.json`, нужен lock.

MVP requirement:

- одновременно может работать только один `codex-multi`, который управляет `~/.codex/auth.json`;
- при старте утилита должна брать process lock;
- если lock уже взят, запуск должен завершаться понятной ошибкой.

Пример ошибки:

```text
Another codex-multi instance is already managing ~/.codex/auth.json.
```

## 11. Detecting rate limit

Rate limit нужно ловить без HTTP-прокси.

### 11.1 Primary detection source

Предпочтительно читать локальные session logs `Codex`:

```text
~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl
```

Нужно уметь находить session file, соответствующий текущему запуску `codex`.

Признаки:

- создан после запуска текущего процесса;
- соответствует текущему cwd;
- содержит `session_meta` / `turn_context`;
- содержит event с текстом вроде:

```text
Rate limit reached for gpt-5.4 ...
```

### 11.2 Secondary detection source

Можно дополнительно отслеживать stderr/stdout дочернего `codex`, но это optional.

### 11.3 Matching rule

Rate limit считается пойманным, если:

- в session log есть event/error message containing `Rate limit reached`;
- и дочерний процесс завершился или перестал продвигать сессию;
- и ошибка относится к текущему активному запуску.

## 12. Session tracking

Утилита должна знать:

- текущий `session_id`;
- путь к текущему `rollout-*.jsonl`;
- последний известный `turn_id`, если потребуется для логирования;
- какой профиль использовался на этом запуске.

Нужно уметь извлекать `session_id` из session log, чтобы после rate limit вызвать:

```bash
codex resume <session_id> "<resume prompt>"
```

Нельзя полагаться только на `--last`, потому что при нескольких сессиях он может выбрать не ту.

## 13. Process model

Настоящий `codex` должен запускаться как дочерний процесс.

Требования:

- интерактивный TUI должен оставаться рабочим;
- wrapper не должен требовать локального сервера;
- wrapper не должен ломать input/output пользователя без явной необходимости.

Рекомендуемый подход для MVP:

- запускать `codex` как child process с прикреплённым терминалом;
- для детекции состояния опираться в первую очередь на session logs, а не на перехват TTY.

## 14. Resume behavior

При rate limit утилита должна:

1. определить текущий `session_id`;
2. выбрать следующий профиль;
3. подменить `auth.json`;
4. запустить:

```bash
codex resume <session_id> "<resume prompt>"
```

Default resume prompt:

```text
Continue from the last point. The previous attempt was interrupted by a rate limit.
```

Resume prompt должен настраиваться через config.

## 15. Profile rotation policy

MVP policy:

- профили хранятся в фиксированном порядке;
- при rate limit используется следующий профиль по кругу;
- если профили закончились, утилита завершает работу с понятным сообщением.

Пример:

```text
All available profiles have been exhausted or failed with rate limits.
```

Optional later:

- cooldown на профиль;
- пропуск профиля после недавнего rate limit;
- ручной blacklist/disable.

## 16. Failure handling

Нужно различать типы сбоев.

### 16.1 Must rotate

- `Rate limit reached`

### 16.2 Must not auto-rotate

- invalid auth
- transport/network failure
- local crash
- unknown parameter
- user cancelled
- config parse failure

Для этих ошибок утилита не должна автоматически менять профиль, если нет отдельного явного правила.

## 17. Persistence of runtime state

Во время работы можно хранить runtime state в temp/state file, например:

```text
~/.config/codex-multi/runtime.json
```

Например:

```json
{
  "activeRunId": "2026-04-09T10:15:00Z-12345",
  "currentProfile": "personal",
  "sessionId": "019d7188-0e26-79f3-a4f3-3345dd062394",
  "rolloutPath": "/home/user/.codex/sessions/2026/04/09/rollout-....jsonl",
  "startedAtUtc": "2026-04-09T10:15:00Z"
}
```

Это optional for MVP, but recommended.

## 18. Proposed implementation language/runtime

- Language: C#
- Runtime: .NET 8 or newer
- App type: console application
- OS target: Linux first

## 19. Recommended implementation shape

Рекомендуется разделить код на модули:

- `Cli`
- `Config`
- `Profiles`
- `AuthJsonSwitcher`
- `CodexProcessRunner`
- `SessionLogWatcher`
- `RateLimitDetector`
- `ResumeCoordinator`
- `Locking`

## 20. Logging

Утилита должна логировать собственные действия отдельно от `codex`.

Например:

```text
~/.config/codex-multi/logs/2026-04-09.log
```

Что логировать:

- старт wrapper;
- выбранный профиль;
- найденный session file;
- найденный session id;
- срабатывание rate limit;
- переключение на другой профиль;
- запуск `resume`.

Что не логировать:

- access token;
- refresh token;
- полный `auth.json`.

## 21. Suggested MVP acceptance criteria

### 21.1 Profile management

- `auth save <name>` сохраняет текущий `~/.codex/auth.json`;
- `auth list` показывает профили;
- `auth use <name>` подменяет текущий `~/.codex/auth.json`;
- `auth current` показывает активный профиль.

### 21.2 Normal run

- `codex-multi` запускает обычный `codex`;
- при отсутствии rate limit поведение не отличается от обычного запуска `codex`.

### 21.3 Rate limit recovery

- при обнаружении `Rate limit reached` wrapper переключает профиль;
- wrapper поднимает `codex resume <session_id> "<prompt>"`;
- сессия продолжается в том же разговоре;
- если профилей больше нет, пользователь получает понятное сообщение.

### 21.4 Safety

- без lock нельзя запустить два инстанса, управляющих одним `~/.codex/auth.json`;
- подмена `auth.json` атомарна;
- секреты не попадают в логи.

## 22. Nice-to-have features

Не обязательны для MVP:

- `codex-multi status`
- `codex-multi doctor`
- cooldown per profile after rate limit
- ручной `switch` на следующий профиль без запуска `codex`
- статистика: сколько раз каждый профиль ловил rate limit
- маскирование email/account в `auth list`
- конфигурируемая стратегия ротации

## 23. Open questions for implementer

Перед реализацией стоит принять решения по этим пунктам:

1. Нужен ли strict single-instance globally, или достаточно lock per `~/.codex` directory?
2. Нужно ли поддерживать несколько разных `CODEX_HOME`, или MVP ограничивается только `~/.codex`?
3. Нужно ли перехватывать TTY, или достаточно session log watcher?
4. Нужно ли считать успешным recovery только после нового `task_started`, или достаточно успешного старта `resume` процесса?
5. Нужна ли команда `codex-multi resume` как явная обёртка вокруг `codex resume`?

## 24. Minimal recommended scope

Если делать самый прагматичный MVP, то достаточно:

- один бинарник `codex-multi`;
- подкоманды `auth save/list/use/current/remove`;
- запуск обычного `codex`;
- lock на один экземпляр;
- поиск session log;
- детекция `Rate limit reached`;
- ротация на следующий профиль;
- запуск `codex resume <session_id> "<prompt>"`.

Все остальные функции можно отложить.
