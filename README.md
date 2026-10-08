# Система продажу квитків на культурні заходи (варіант 18)

Лабораторна робота № 2

## Шари та відповідність каталогів

| Шар | Каталог | Відповідальність | Чого тут бути не повинно |
| Presentation | `src/Ticketing.Api` | HTTP-ендпоінти, DTO, перевірка формату вводу, відображення помилок у коди HTTP, `CompositionRoot.cs` | бізнес-правил |
| Application | `src/Ticketing.Application` | сценарії використання, транзакційні межі, порти (`IClock`, `IUnitOfWork`, `IPaymentGateway`) | знань про СУБД і HTTP |
| Domain | `src/Ticketing.Domain` | сутності, об'єкти-значення, доменні правила, **інтерфейси репозиторіїв** | залежностей від інших шарів і бібліотек |
| Infrastructure | `src/Ticketing.Infrastructure` | реалізації репозиторіїв, одиниця роботи, платіжний шлюз-імітація, генератор кодів | бізнес-правил |

Напрямок залежностей: `Api -> Application -> Domain`, `Infrastructure -> Application, Domain`.
`Domain` не залежить ні від чого. `Api` посилається на `Infrastructure` лише у `CompositionRoot.cs`.

## Доменна модель

`Event` (Захід), `Session` (Сеанс, корінь агрегату з місцями `Seat`), `Order` (Замовлення), `Ticket` (Квиток);
об'єкти-значення `Money`, `SeatCode`. Правила резервування й продажу місць — у `Session`, станів замовлення — у `Order`.

## Сценарії використання

| Сценарій                                  | Клас                    | Маршрут                                |
| ----------------------------------------- | ----------------------- | -------------------------------------- |
| Резервування місць і створення замовлення | `ReserveSeatsHandler`   | `POST /api/sessions/{id}/reservations` |
| Оплата й видача квитків                   | `PayOrderHandler`       | `POST /api/orders/{id}/payment`        |
| Перевірка квитка на вході                 | `ValidateTicketHandler` | `POST /api/tickets/validation`         |
