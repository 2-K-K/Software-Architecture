#!/usr/bin/env bash
# Експеримент з вимкненим сервісом (ЛР3, п. 8).
# Вимагає запущених сервісів через run-all.sh. Результати друкуються в консоль і в results/experiment.txt
#
# Сценарій A (синхронний): вимикаємо Catalog, створюємо замовлення через шлюз.
# Сценарій B (асинхронний): вимикаємо Notifications, оплачуємо замовлення,
#                           потім вмикаємо Notifications і перевіряємо, що подія доставлена.
set -uo pipefail
cd "$(dirname "$0")"
mkdir -p results pids
OUT=results/experiment.txt
: > "$OUT"

GW=http://localhost:5000
log() { echo "$*" | tee -a "$OUT"; }

json_field() { grep -o "\"$1\":\"[^\"]*\"" | head -1 | cut -d'"' -f4; }

stop_service()  { kill "$(cat pids/$1.pid)" 2>/dev/null; sleep 2; log "  [вимкнено] $1"; }
start_service() {
  case "$1" in
    catalog)       project=Catalog.Service/Catalog.Service.csproj;             url=http://localhost:5001 ;;
    notifications) project=Notifications.Service/Notifications.Service.csproj; url=http://localhost:5003 ;;
  esac
  dotnet run --no-launch-profile --project "$project" --urls "$url" > "logs/$1.log" 2>&1 &
  echo $! > "pids/$1.pid"
  for _ in $(seq 1 60); do
    curl -s "$url/health" >/dev/null 2>&1 && break
    sleep 1
  done
  log "  [увімкнено] $1"
}

# 1. Підготовка: захід, сеанс на завтра, 6 місць
START=$(date -u -d '+1 day' +%Y-%m-%dT%H:%M:%SZ)
EV=$(curl -s -X POST "$GW/api/events" -H 'Content-Type: application/json' \
  -d '{"title":"Концерт","organizerName":"Філармонія"}' | json_field eventId)
SESSION=$(curl -s -X POST "$GW/api/sessions" -H 'Content-Type: application/json' \
  -d "{\"eventId\":\"$EV\",\"venueName\":\"Велика зала\",\"startsAt\":\"$START\",\"seats\":[{\"row\":1,\"number\":1,\"priceUah\":500},{\"row\":1,\"number\":2,\"priceUah\":500}]}" \
  | json_field sessionId)
log "Сеанс: $SESSION"

ORDER_BODY="{\"sessionId\":\"$SESSION\",\"buyerEmail\":\"guest@example.com\",\"seats\":[{\"row\":1,\"number\":1}]}"

log ""
log "=== Сценарій A: синхронний виклик, вимкнено Catalog ==="
stop_service catalog
RESP=$(curl -s -o /tmp/resp.json -w "%{http_code} %{time_total}" -X POST "$GW/api/orders" \
  -H 'Content-Type: application/json' -d "$ORDER_BODY")
log "  Відповідь шлюзу: HTTP $(echo "$RESP" | cut -d' ' -f1), час $(echo "$RESP" | cut -d' ' -f2) с"
log "  Тіло: $(cat /tmp/resp.json)"
start_service catalog

log ""
log "=== Сценарій B: асинхронна подія, вимкнено Notifications ==="
# Після перезапуску Catalog сеанс у пам'яті зник, тому створюємо новий
EV=$(curl -s -X POST "$GW/api/events" -H 'Content-Type: application/json' \
  -d '{"title":"Вистава","organizerName":"Театр"}' | json_field eventId)
SESSION=$(curl -s -X POST "$GW/api/sessions" -H 'Content-Type: application/json' \
  -d "{\"eventId\":\"$EV\",\"venueName\":\"Мала сцена\",\"startsAt\":\"$START\",\"seats\":[{\"row\":1,\"number\":1,\"priceUah\":300}]}" \
  | json_field sessionId)
ORDER=$(curl -s -X POST "$GW/api/orders" -H 'Content-Type: application/json' \
  -d "{\"sessionId\":\"$SESSION\",\"buyerEmail\":\"buyer@example.com\",\"seats\":[{\"row\":1,\"number\":1}]}" \
  | json_field orderId)

stop_service notifications
PAY=$(curl -s -o /tmp/pay.json -w "%{http_code} %{time_total}" -X POST "$GW/api/orders/$ORDER/payment")
log "  Оплата: HTTP $(echo "$PAY" | cut -d' ' -f1), час $(echo "$PAY" | cut -d' ' -f2) с"
log "  Тіло: $(cat /tmp/pay.json)"
log "  Продавець не чекав споживача. Подія стоїть у черзі Sales з повторами."

sleep 5
start_service notifications
log "  Очікуємо доставку події (до 30 с)…"
for _ in $(seq 1 30); do
  COUNT=$(curl -s "$GW/api/notifications" | grep -o '"count":[0-9]*' | cut -d: -f2)
  [ "${COUNT:-0}" -ge 1 ] && break
  sleep 1
done
log "  Надіслано сповіщень після відновлення: ${COUNT:-0}"
log "  Список: $(curl -s "$GW/api/notifications")"

log ""
log "Результати збережено у $OUT"
