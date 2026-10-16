#!/usr/bin/env bash
# ЛР5: вимірювання часу відповіді GET /api/sessions/{id}/seats до і після кешу (за однакових умов).
# Запускає Catalog.Service двічі: з вимкненим кешем (Catalog:SeatsCache:Enabled=false) і з увімкненим.
# Потрібен .NET 8 SDK (BUILD_FLAGS=--no-restore, якщо пакети вже відновлено). Результат: таблиця медіани/p95 у мілісекундах; сирі заміри в measurements/.
set -euo pipefail

N=${N:-500}          # запитів на режим
PORT=${PORT:-5101}
BASE="http://localhost:$PORT"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/measurements"
mkdir -p "$OUT"

measure() {
  local mode=$1 flag=$2
  dotnet build "$HERE/Catalog.Service" -c Release -v q ${BUILD_FLAGS:-} >/dev/null
  dotnet run -c Release --no-build --project "$HERE/Catalog.Service" -- \
    --urls "$BASE" --Catalog:SeatsCache:Enabled="$flag" > "$OUT/catalog-$mode.log" 2>&1 &
  local pid=$!

  until curl -s "$BASE/health" >/dev/null 2>&1; do sleep 0.5; done

  local ev se
  ev=$(curl -s -X POST "$BASE/api/events" -H 'Content-Type: application/json' \
        -d '{"title":"Замір","organizerName":"ЛР5"}' | python -c "import sys,json;print(json.load(sys.stdin)['eventId'])")
  se=$(curl -s -X POST "$BASE/api/sessions" -H 'Content-Type: application/json' \
        -d "{\"eventId\":\"$ev\",\"venueName\":\"Зал\",\"startsAt\":\"2030-12-01T18:00:00Z\",\"seats\":[
              {\"row\":1,\"number\":1,\"priceUah\":250},{\"row\":1,\"number\":2,\"priceUah\":250},
              {\"row\":2,\"number\":1,\"priceUah\":300},{\"row\":2,\"number\":2,\"priceUah\":300}]}" \
        | python -c "import sys,json;print(json.load(sys.stdin)['sessionId'])")

  for _ in $(seq 20); do curl -s -o /dev/null "$BASE/api/sessions/$se/seats"; done   # прогрів

  for _ in $(seq "$N"); do
    curl -s -o /dev/null -w "%{time_total}\n" "$BASE/api/sessions/$se/seats"
  done > "$OUT/times-$mode.txt"

  kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
}

measure nocache false
measure cache true

python - "$OUT" "$N" <<'PY'
import sys, statistics, pathlib
out, n = pathlib.Path(sys.argv[1]), int(sys.argv[2])
rows = []
for mode in ("nocache", "cache"):
    t = sorted(float(x) * 1000 for x in (out / f"times-{mode}.txt").read_text().split())
    p95 = t[int(0.95 * (len(t) - 1))]
    rows.append((mode, len(t), statistics.median(t), p95, statistics.mean(t)))
lines = ["| Режим | Запитів | Медіана, мс | p95, мс | Середнє, мс |", "|---|---|---|---|---|"]
for mode, cnt, med, p95, avg in rows:
    name = "без кешу" if mode == "nocache" else "з кешем"
    lines.append(f"| {name} | {cnt} | {med:.2f} | {p95:.2f} | {avg:.2f} |")
(out / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
print("\n".join(lines))
PY
