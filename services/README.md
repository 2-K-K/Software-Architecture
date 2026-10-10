# Лабораторна робота 3. Розподілена версія системи продажу квитків

Ядро з ЛР2 розбито на чотири процеси. Доменні правила (Session, Seat, Order, Ticket) взяті з `src/Ticketing.Domain` без змін.

## Сервіси

| Сервіс                | Порт | Контекст    | Володіє даними             | Роль                                                             |
| --------------------- | ---- | ----------- | -------------------------- | ---------------------------------------------------------------- |
| Gateway               | 5000 | точка входу | немає                      | Єдина адреса для клієнтів, маршрутизація за `/api/{сегмент}`     |
| Catalog.Service       | 5001 | Каталог     | Event, Session, Seat       | Заходи, сеанси, схеми залів, резервування та підтвердження місць |
| Sales.Service         | 5002 | Продаж      | Order, Ticket, стан оплати | Замовлення, оплата, квитки, перевірка на вході                   |
| Notifications.Service | 5003 | Сповіщення  | список надісланих листів   | Споживає подію `TicketsIssued`                                   |

## Запуск

Потрібен .NET 8 SDK.

```bash
cd services
./run-all.sh            # запускає 4 процеси, логи в services/logs
./experiment.sh         # експеримент з вимкненим сервісом
./stop-all.sh
```

Або вручну, у чотирьох терміналах:

```bash
dotnet run --project services/Catalog.Service --urls http://localhost:5001
dotnet run --project services/Sales.Service --urls http://localhost:5002
dotnet run --project services/Notifications.Service --urls http://localhost:5003
dotnet run --project services/Gateway --urls http://localhost:5000
```
