# FaultTolerantOrders — Transactional Outbox

OrderService сохраняет заказ и событие `OrderCreated` в одной транзакции PostgreSQL. Фоновый Outbox Processor публикует событие в RabbitMQ и отмечает его опубликованным только после подтверждения брокера. NotificationService сохраняет уведомление и отправляет ACK.

Исходная версия для сравнения находится в коммите `982dc98`; её первый опыт записан в [журнале](docs/experiment-log.md). HTTP Idempotency и Inbox пока не реализованы. Повторная доставка и повторные уведомления по-прежнему возможны.

## Запуск

Нужны .NET 10 SDK и Docker Compose. Для сборки исходников без Docker:

```powershell
dotnet build FaultTolerantOrders.slnx -m:1
```

Для запуска полного стенда:

```powershell
docker compose up --build -d
```

При старте API применяются миграции EF Core. Начальная миграция поддерживает новую базу и прежнюю baseline-схему Orders/OrderItems, созданную EnsureCreated: существующие заказы сохраняются, добавляется OutboxMessages. Автоматического создания событий для старых заказов нет. Сбрасывать тома PostgreSQL для обновления не требуется. SQL обновления: [outbox-migration.sql](docs/experiments/outbox-migration.sql).

API: `http://localhost:8080`. Панель RabbitMQ: `http://localhost:15672` (`lab` / `labpass`). Пароли в `compose.yaml` предназначены только для локальной демонстрации.

## Контрольный запрос

```powershell
$body = '{"userId":"c7a5f714-0455-421c-b574-28f3f4c6c0ab","items":[{"productId":"eb3c8700-4aa5-4d4e-88e1-dff06df6be2f","quantity":2,"unitPrice":1500}]}'
Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/orders -ContentType 'application/json' -Body $body
```

Ответ `201 Created` означает сохранение заказа и события, а не завершение обработки уведомления. Публикация выполняется отдельно; при временно недоступном брокере сохранённое событие остаётся для повтора. Список заказов:

```powershell
Invoke-RestMethod http://localhost:8080/api/orders
```

Число уведомлений:

```powershell
docker compose exec notification-db psql -U lab -d notifications -c 'SELECT COUNT(*) FROM "Notifications";'
```

## Опыт 2: сбой после фиксации заказа и Outbox

Заголовок `X-Fault-After-Commit` действует только при запуске API в среде `Development`. Он аварийно завершает процесс после сохранения заказа и Outbox. Ошибка соединения у HTTP-клиента ожидаема. Работающий фоновый процессор способен успеть опубликовать событие до падения, поэтому для воспроизводимой проверки восстановления сначала применяется override с Outbox__Enabled=false, затем он снимается. Полные команды и таблица наблюдений: [experiment-2-outbox.md](docs/experiment-2-outbox.md).

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/orders -ContentType 'application/json' -Headers @{'X-Fault-After-Commit'='true'} -Body $body
```

После восстановления и включения процессора проверьте заказ, запись Outbox с PublishedAt и уведомление. Логи:

```powershell
docker compose logs --tail=100 order-api notification-worker
```

**Предположение для проверки:** новая pending-запись Outbox переживёт падение, а после включения процессора появятся PublishedAt и уведомление. Фактический результат полного опыта с PostgreSQL/RabbitMQ ещё нужно получить на компьютере с Docker.

## Тесты

```powershell
dotnet test FaultTolerantOrders.slnx -m:1
```

Локальные тесты используют SQLite для проверки атомарного сохранения и состояний доставки. Зависимость брокера заменена управляемым publisher: проверяется поведение приложения при ошибке и ожидании подтверждения, а не протокол RabbitMQ. PostgreSQL-тесты пропускаются, пока не задана строка подключения:

```powershell
$env:OUTBOX_TEST_POSTGRES = 'Host=localhost;Port=5433;Database=orders;Username=lab;Password=labpass'
dotnet test tests/OrderService.Tests/OrderService.Tests.csproj -m:1 --filter 'Category=PostgreSQL'
```

Эти тесты создают и удаляют только собственные схемы с именами outbox_test_<guid>; данные стенда в основной схеме сохраняются. Проверяются переход с baseline-схемы, создание новой базы, конкурентный захват строки и отсроченные повторы.

## Как устроена доставка

Outbox Processor опрашивает готовые строки раз в 2 секунды, до 20 операций за проход. PostgreSQL FOR UPDATE SKIP LOCKED позволяет экземплярам брать разные строки; транзакция удерживается до результата попытки и его фиксации. Ожидание публикации ограничено 10 секундами, в том числе если клиент продолжает закрывать канал после отмены. При таймауте результат отправки считается неопределённым и допускает повтор. При ошибке сохраняются RetryCount, NextRetryAt и Error; задержка растёт от 1 до 30 секунд.

RabbitPublisher использует persistent messages, durable exchange/queue, mandatory=true и publisher confirms. PublishedAt означает подтверждение брокера, а не завершение consumer. Если процесс упадёт после принятия события брокером до фиксации PublishedAt, событие отправится повторно с прежним MessageId. Consumer пока не защищён Inbox.

Источники: [RabbitMQ publisher confirms](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet), [PostgreSQL row locking](https://www.postgresql.org/docs/17/sql-select.html#SQL-FOR-UPDATE-SHARE), [миграции EF Core](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying).

## Ограничения текущего этапа

- OrderService использует миграции; NotificationService пока создаёт первоначальную схему через EnsureCreatedAsync.
- Транзакция диспетчера удерживает соединение с БД на время публикации. Для высокой нагрузки потребуется отдельное исследование и оптимизация.
- Полный запуск требует Docker Compose; без него можно проверить только сборку исходников.
- Демонстрационные пароли и заголовок инъекции сбоя нельзя использовать в развёрнутой системе.
