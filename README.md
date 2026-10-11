# FaultTolerantOrders — исходная версия

Это намеренно уязвимая версия стенда для сравнения. OrderService сначала сохраняет заказ в PostgreSQL, а затем напрямую публикует событие `OrderCreated` в RabbitMQ. Здесь ещё нет HTTP Idempotency, Outbox и Inbox. Цель первого опыта — увидеть последствия сбоя между сохранением заказа и публикацией события.

## Запуск

Нужны .NET 10 SDK и Docker Compose. Для сборки исходников без Docker:

```powershell
dotnet build FaultTolerantOrders.slnx -m:1
```

Для запуска полного стенда:

```powershell
docker compose up --build -d
```

API: `http://localhost:8080`. Панель RabbitMQ: `http://localhost:15672` (`lab` / `labpass`). Пароли в `compose.yaml` предназначены только для локальной демонстрации.

## Контрольный запрос

```powershell
$body = '{"userId":"c7a5f714-0455-421c-b574-28f3f4c6c0ab","items":[{"productId":"eb3c8700-4aa5-4d4e-88e1-dff06df6be2f","quantity":2,"unitPrice":1500}]}'
Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/orders -ContentType 'application/json' -Body $body
```

Ожидаемый ответ — `201 Created`. Список заказов:

```powershell
Invoke-RestMethod http://localhost:8080/api/orders
```

Число уведомлений:

```powershell
docker compose exec notification-db psql -U lab -d notifications -c 'SELECT COUNT(*) FROM "Notifications";'
```

## Опыт 1: сбой после фиксации заказа

Заголовок `X-Fault-After-Commit` действует только при запуске API в среде `Development`. Он аварийно завершает процесс сразу после сохранения заказа и до публикации события. Ошибка соединения у HTTP-клиента в этом опыте ожидаема.

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/orders -ContentType 'application/json' -Headers @{'X-Fault-After-Commit'='true'} -Body $body
```

Compose перезапустит API. После этого проверьте `GET /api/orders`, таблицу Notifications и логи:

```powershell
docker compose logs --tail=100 order-api notification-worker
```

**Предположение для проверки:** новый заказ останется в базе, а соответствующее уведомление не появится. Это ещё не результат эксперимента; фактические числа заносите в [журнал опытов](docs/experiment-log.md).

В этой исходной версии нет подтверждения публикации от брокера. [Документация RabbitMQ](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) поясняет, что клиент не хранит неудавшиеся публикации для последующей отправки. Позже это окно отказа будет закрыто Outbox.

## Ограничения текущего этапа

- Схема создаётся через `EnsureCreatedAsync`. Перед её развитием нужны миграции EF Core или сброс демонстрационных томов.
- Полный запуск требует Docker Compose; без него можно проверить только сборку исходников.
- Демонстрационные пароли и заголовок инъекции сбоя нельзя использовать в развёрнутой системе.
