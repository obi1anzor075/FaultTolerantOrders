# Опыт 2: доставка сохранённого события после сбоя API

Цель: повторить отказ между сохранением заказа и отправкой события и сравнить исходную версию с Transactional Outbox. Результаты заполняются после выполнения на компьютере с Docker. Предыдущий аварийный заказ не используется: он создан до появления Outbox и автоматически не восстанавливается.

## 1. Запуск с управляемой паузой публикации

В PowerShell из корня проекта:

```powershell
docker compose -f compose.yaml -f compose.outbox-fault.yaml up --build -d
docker compose -f compose.yaml -f compose.outbox-fault.yaml ps
docker compose logs --tail=50 order-api
```

Дождаться запуска API и строки `Outbox processor is disabled`. Override отключает только процессор публикации, API по-прежнему сохраняет заказ и событие. Это исключает гонку, когда процессор успевает отправить событие до инъекции падения. Для опыта запускать один экземпляр API.

## 2. Запрос со сбоем

Создаём отдельный UserId, чтобы найти заказ после оборванного HTTP-ответа. Сохранить переменные в этом окне PowerShell:

```powershell
$taskOutboxUserId = [guid]::NewGuid().ToString()
$taskOutboxBody = @{
    userId = $taskOutboxUserId
    items = @(@{
        productId = 'eb3c8700-4aa5-4d4e-88e1-dff06df6be2f'
        quantity = 2
        unitPrice = 1500
    })
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/orders `
    -ContentType 'application/json' -Body $taskOutboxBody `
    -Headers @{'X-Fault-After-Commit'='true'}
```

Ошибка соединения ожидаема. POST повторно не отправлять: HTTP Idempotency ещё не добавлена. Compose перезапустит API с отключённым процессором.

## 3. Состояние после перезапуска, до включения публикации

После восстановления API получить заказ:

```powershell
$taskOrders = @(Invoke-RestMethod http://localhost:8080/api/orders)
$taskMatchedOrders = @($taskOrders | Where-Object { $_.userId -eq $taskOutboxUserId })
if ($taskMatchedOrders.Count -ne 1) { throw 'Expected exactly one order for this experiment user' }
$taskOutboxOrderId = $taskMatchedOrders[0].id
$taskMatchedOrders | ConvertTo-Json | Set-Content -Path .\experiment-2-order.json -Encoding utf8
$taskOutboxOrderId
```

Проверить сохранённое событие и уведомления; сохранить вывод:

```powershell
@'
SELECT "Id", "OrderId", "PublishedAt", "RetryCount", "NextRetryAt", "Error"
FROM "OutboxMessages" WHERE "OrderId" = :'order_id';
'@ | docker compose exec -T order-db psql -U lab -d orders -v ON_ERROR_STOP=1 -v "order_id=$taskOutboxOrderId" |
    Tee-Object -FilePath .\experiment-2-outbox-before.txt

@'
SELECT COUNT(*) AS notifications FROM "Notifications" WHERE "OrderId" = :'order_id';
'@ | docker compose exec -T notification-db psql -U lab -d notifications -v ON_ERROR_STOP=1 -v "order_id=$taskOutboxOrderId" |
    Tee-Object -FilePath .\experiment-2-notifications-before.txt
```

Проверяемое предположение: одна запись события, PublishedAt пуст, уведомлений 0. Это предполагаемые значения, не заранее заполненные результаты.

## 4. Включение процессора

```powershell
docker compose up -d order-api
docker compose logs -f --tail=50 order-api notification-worker
```

Обычный Compose убирает override и пересоздаёт API с включённым процессором. Дождаться `Committed publish marker for outbox ...` и `Created notification for order ...` для нужного заказа, затем остановить просмотр Ctrl+C.

Повторить обе SQL-команды из шага 3 с именами файлов `experiment-2-outbox-after.txt` и `experiment-2-notifications-after.txt`. Проверяем: Id события остался прежним, PublishedAt заполнен, для заказа появилось уведомление. Обычно ожидается 1 уведомление; если больше, записать фактическое число — Inbox на этом этапе отсутствует.

Сохранить логи и версию кода на компьютере запуска:

```powershell
docker compose logs --no-color --timestamps order-api notification-worker rabbitmq |
    Out-File -FilePath .\experiment-2.log -Encoding utf8
git rev-parse HEAD
git status --short
```

## Таблица результатов

| Показатель для нового OrderId | После сбоя, процессор отключён | После включения процессора |
|---|---|---|
| Заказ существует | | |
| Id события Outbox | | |
| PublishedAt | | |
| RetryCount | | |
| Число уведомлений | | |

- Дата и версия кода:
- OrderId и MessageId:
- Временные метки сбоя и восстановления:
- Временная метка подтверждения публикации:
- Временная метка обработки уведомления:
- Фактическое наблюдение и вывод:

Методическое ограничение: используется управляемая пауза публикации. Она гарантирует pending-событие перед восстановлением, но не измеряет естественную вероятность гонки фонового процессора с падением API. Для времени восстановления нужно различать доступность API и доставку события.

## Дополнительная проверка: брокер недоступен

При включённом процессоре остановить RabbitMQ командой `docker compose stop rabbitmq`. Отправить новый обычный POST с новым UserId, без заголовка сбоя. Проверить 201, наличие заказа и Outbox, увеличение RetryCount. Запустить брокер `docker compose start rabbitmq`; проверить публикацию того же события и появление уведомления. Записать наблюдение отдельно от основного опыта.
