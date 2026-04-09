# codex-multi

`codex-multi` это минималистичный CLI-wrapper над обычным `codex`.

Он не проксирует HTTP и не подменяет API. Вместо этого утилита:

- хранит несколько профилей `auth.json`;
- быстро переключает активный `~/.codex/auth.json`;
- запускает обычный `codex` как дочерний процесс;
- следит за локальными session logs;
- если видит `Rate limit reached`, переключает профиль и поднимает ту же сессию через `codex resume`.

## Что умеет текущий MVP

- `codex-multi auth save <name>`
- `codex-multi auth import <name> --from <path>`
- `codex-multi auth list`
- `codex-multi auth current`
- `codex-multi auth show <name>`
- `codex-multi auth use <name>`
- `codex-multi auth remove <name>`
- `codex-multi [args...]`

Если команда не распознана как `auth`, все аргументы пробрасываются в настоящий `codex`.

Главное поведение текущей версии:

- можно хранить несколько аккаунтов `codex`
- можно запускать несколько экземпляров `codex-multi` одновременно
- все экземпляры могут жить на одном общем `CODEX_HOME`
- при `Rate limit reached` только один экземпляр реально переключает общий `auth.json`, остальные потом подхватывают новый профиль на своём `resume`

## Требования

- Linux
- установленный и рабочий `codex`
- доступ к `~/.codex/auth.json`
- .NET SDK 10 для сборки из исходников

Примечание: в этом репозитории проект сейчас таргетит `net10.0`, потому что он собирался в окружении с локально доступными reference packs под .NET 10. По логике самой утилиты ограничений на `8+` нет.

## Сборка

```bash
git clone git@github.com:Tr0sT/codex-multi.git
cd codex-multi
DOTNET_CLI_HOME=/tmp NUGET_PACKAGES=/tmp/nuget dotnet build --ignore-failed-sources
```

После сборки запускать можно так:

```bash
dotnet ./bin/Debug/net10.0/CodexMulti.dll
```

Если хотите положить утилиту в `PATH`, самый простой вариант:

```bash
DOTNET_CLI_HOME=/tmp NUGET_PACKAGES=/tmp/nuget dotnet publish -c Release --ignore-failed-sources
install -Dm755 ./bin/Release/net10.0/publish/CodexMulti ~/.local/bin/codex-multi
```

После этого команда будет доступна как:

```bash
codex-multi
```

## Быстрый старт

Если нужен самый короткий сценарий:

1. Соберите или установите `codex-multi`.
2. Сохраните текущий аккаунт:

```bash
codex-multi auth save personal
```

3. Подмените `~/.codex/auth.json` на другой аккаунт и сохраните его тоже:

```bash
codex-multi auth save work
```

4. Проверьте порядок профилей:

```bash
codex-multi auth list
```

5. Запускайте вместо обычного `codex`:

```bash
codex-multi
```

Если нужно несколько окон одновременно, просто запускайте `codex-multi` в каждом из них. Дополнительные `CODEX_HOME` для этого больше не нужны.

## Где лежат данные

По умолчанию:

- конфиг: `~/.config/codex-multi/config.json`
- runtime state: `~/.config/codex-multi/runtime.json`
- профили: `~/.config/codex-multi/profiles/<name>/auth.json`
- метаданные профиля: `~/.config/codex-multi/profiles/<name>/meta.json`
- логи wrapper: `~/.config/codex-multi/logs/YYYY-MM-DD.log`
- lock-файл: `~/.config/codex-multi/locks/state.lock`

Используются и стандартные env-переменные:

- `XDG_CONFIG_HOME` влияет на каталог `codex-multi`
- `CODEX_HOME` влияет на путь к `auth.json` и `sessions`

То есть если у вас не стандартный `~/.codex`, wrapper это поддерживает.

## Как добавить аккаунты

Есть два рабочих способа.

### Вариант 1. Через текущий `~/.codex/auth.json`

Подходит, если вы умеете получить нужный `auth.json` обычным `codex`.

1. Сделайте так, чтобы в `~/.codex/auth.json` лежал аккаунт A.
2. Сохраните его:

```bash
codex-multi auth save personal
```

3. Подмените `~/.codex/auth.json` на аккаунт B любым привычным способом.
4. Сохраните и его:

```bash
codex-multi auth save work
```

5. Проверьте список:

```bash
codex-multi auth list
```

### Вариант 2. Через готовые файлы

Если у вас уже есть несколько копий `auth.json`, импортируйте их напрямую:

```bash
codex-multi auth import personal --from /path/to/personal-auth.json
codex-multi auth import work --from /path/to/work-auth.json
```

После импорта:

```bash
codex-multi auth list
```

## Как переключать активный аккаунт

Показать текущий профиль:

```bash
codex-multi auth current
```

Включить конкретный профиль и сразу подменить `~/.codex/auth.json`:

```bash
codex-multi auth use work
```

Эта команда также обновляет общий `runtime.json`, поэтому новые запуски `codex-multi` будут стартовать уже с этого профиля.

Посмотреть метаданные профиля без токенов:

```bash
codex-multi auth show work
```

Удалить профиль:

```bash
codex-multi auth remove work
```

## Как запускать `codex` через wrapper

Обычный интерактивный запуск:

```bash
codex-multi
```

Запуск с аргументами:

```bash
codex-multi "fix this bug"
codex-multi resume 019d7188-0e26-79f3-a4f3-3345dd062394 "continue"
```

Важно: wrapper не добавляет свой отдельный протокол команд. Всё, что не `auth`, уходит в обычный `codex`.

## Что происходит при rate limit

Во время запуска `codex-multi` делает следующее:

1. Читает общий `runtime.json`, где хранится текущий active profile и `generation`.
2. Подставляет активный профиль в `~/.codex/auth.json`.
3. Запускает обычный `codex`.
4. Следит за `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`.
5. Если в логе видит `Rate limit reached`, считает запуск прерванным по лимиту.
6. Под коротким global lock проверяет, не успел ли другой экземпляр уже переключить профиль.
7. Если никто не успел, этот экземпляр выбирает следующий профиль по кругу, меняет `auth.json` и увеличивает `generation`.
8. Если другой экземпляр уже успел переключить профиль, этот экземпляр не трогает `auth.json`, а просто подхватывает новый active profile из `runtime.json`.
9. Запускает:

```bash
codex resume <session_id> "Continue from the last point. The previous attempt was interrupted by a rate limit."
```

Если профили закончились, wrapper завершится с ошибкой:

```text
All available profiles have been exhausted or failed with rate limits.
```

## Порядок профилей и конфиг

Файл `config.json` создаётся автоматически при первом запуске.

Пример:

```json
{
  "activeProfile": "personal",
  "profileOrder": ["personal", "work"],
  "resumePrompt": "Continue from the last point. The previous attempt was interrupted by a rate limit.",
  "codexExecutable": "codex"
}
```

Что можно менять руками:

- `activeProfile`: какой профиль считается активным по умолчанию
- `profileOrder`: порядок ротации при rate limit
- `resumePrompt`: текст, который уйдёт в `codex resume`
- `codexExecutable`: путь или имя настоящего `codex`

Например, если реальный бинарь лежит не в `PATH`, можно прописать абсолютный путь в `codexExecutable`.

## Несколько одновременных экземпляров

Текущая реализация допускает несколько параллельных запусков `codex-multi` с одним и тем же `CODEX_HOME`.

Схема работы такая:

- все экземпляры используют один общий `~/.codex/auth.json`
- все экземпляры используют один общий `runtime.json`
- если несколько процессов стартовали на одном и том же профиле и потом все дошли до `Rate limit reached`, только один из них реально делает switch
- остальные видят, что `generation` уже изменилась, и просто делают свой `resume` на новом активном профиле

Практически это означает, что можно просто открыть несколько терминалов и запускать в каждом:

```bash
codex-multi
```

Ничего дополнительно настраивать для multi-instance режима не нужно:

- не нужно заводить отдельные `CODEX_HOME`
- не нужно заводить отдельные `XDG_CONFIG_HOME`
- не нужно руками синхронизировать `auth.json`

Если один процесс первым упрётся в rate limit, он переключит общий active profile. Остальные, когда дойдут до своего rate limit, не будут второй раз менять `auth.json`, а только продолжат свои сессии через `resume` уже на новом профиле.

Короткий пример:

1. В первом терминале:

```bash
codex-multi
```

2. Во втором терминале:

```bash
codex-multi "fix this bug"
```

3. Оба процесса могут стартовать на `personal`.
4. Если первый переключил систему на `work`, второй не будет делать второй switch. Он увидит новый `generation` и просто выполнит свой `resume` уже на `work`.

Важно:

- живой `codex` не переключается на другой аккаунт "на лету"
- новый профиль подхватывается только на границе нового запуска или `resume`
- координация касается именно `Rate limit reached`; для других ошибок автопереключения нет
- если вы вручную меняете `~/.codex/auth.json` мимо `codex-multi`, можно рассинхронизировать shared state

## Пример полного сценария

Сохранить два аккаунта:

```bash
codex-multi auth save personal
codex-multi auth save work
```

Проверить:

```bash
codex-multi auth list
```

Сделать активным `personal`:

```bash
codex-multi auth use personal
```

Работать как обычно:

```bash
codex-multi
```

Если `personal` упрётся в `Rate limit reached`, wrapper сам переключится на `work` и попробует продолжить ту же сессию через `resume`.

Если в этот момент у вас открыты ещё другие экземпляры `codex-multi`, они не должны делать второй лишний switch. При своём собственном `Rate limit reached` они увидят, что active profile уже изменился, и просто продолжат работу через `resume`.

## Ограничения текущего MVP

- поддерживается только автоматическая реакция на `Rate limit reached`
- на другие ошибки профили автоматически не переключаются
- нет команд `doctor`, `next`, `prev`, `status`
- нет своего логина или управления аккаунтами OpenAI
- координация нескольких параллельных экземпляров best-effort и завязана на локальные session logs
- если несколько `codex` стартуют почти одновременно в одном и том же `cwd`, wrapper матчится с rollout-файлами по ближайшему времени старта; это сильно лучше прежнего single-instance поведения, но всё ещё не идеальная идентификация процесса

## Безопасность

- в профилях лежат реальные токены, относитесь к каталогу `~/.config/codex-multi` как к секретному
- `auth show` и `auth list` не печатают токены
- wrapper старается выставлять приватные права на свои файлы
- не публикуйте содержимое `profiles/*/auth.json`

## Диагностика

Посмотреть текущий профиль:

```bash
codex-multi auth current
```

Посмотреть общий runtime state:

```bash
sed -n '1,120p' ~/.config/codex-multi/runtime.json
```

Посмотреть логи wrapper:

```bash
tail -f ~/.config/codex-multi/logs/$(date +%F).log
```

Проверить, что настоящий `codex` находится в `PATH`:

```bash
which codex
```

Если используете нестандартный путь до `codex`, выставьте `codexExecutable` в `config.json`.

Если нужно понять, кто именно переключил профиль последним, смотрите поля `generation`, `updatedAtUtc` и `updatedByRunId` в `runtime.json`.
