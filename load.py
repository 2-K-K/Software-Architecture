"""ЛР6: навантажувальний експеримент (стандартна бібліотека, без залежностей).

Сценарій: GET /api/sessions/{id}/seats на Catalog.Service (схема залу з 2000 місцями).
Профіль: одночасні користувачі 10, 30, ..., 190 (крок 20), по --duration секунд на рівень.
Модель: кожен користувач — окреме keep-alive з'єднання і безперервний цикл запитів
(максимальне навантаження без пауз між запитами).

Вимірювання: p50, p95, p99 (мс) і частка помилок на кожному рівні; середні значення
Server-Timing (lock, build, json) для пошуку вузького місця.

Запуск (сервіс Catalog має бути запущений з Auth__Key):
    AUTH_KEY=<ключ> python3 load.py --label baseline --cache off --out results-baseline.csv
"""
import argparse
import base64
import csv
import hashlib
import hmac
import http.client
import json
import os
import platform
import statistics
import sys
import threading
import time
import urllib.request
from datetime import datetime, timedelta, timezone

QS1_P95_MS = 500.0  # ЛР1, QS-1: p95 ≤ 500 мс


def b64u(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).decode().rstrip("=")


def issue(sub: str, role: str, key: bytes, ttl_seconds: int = 3600) -> str:
    payload = json.dumps({"sub": sub, "role": role, "exp": int(time.time()) + ttl_seconds}).encode()
    return b64u(payload) + "." + b64u(hmac.new(key, payload, hashlib.sha256).digest())


def post(url: str, body: dict, token: str) -> dict:
    req = urllib.request.Request(url, data=json.dumps(body).encode(), method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def create_session(catalog: str, token: str, seats_count: int) -> str:
    event = post(f"{catalog}/api/events", {"title": "Навантаження ЛР6", "organizerName": "Тест"}, token)
    starts = (datetime.now(timezone.utc) + timedelta(days=30)).strftime("%Y-%m-%dT%H:%M:%SZ")
    seats = [{"row": (i // 50) + 1, "number": (i % 50) + 1, "priceUah": 250} for i in range(seats_count)]
    session = post(f"{catalog}/api/sessions",
                   {"eventId": event["eventId"], "venueName": "Великий зал", "startsAt": starts, "seats": seats},
                   token)
    return session["sessionId"]


def parse_server_timing(value: str) -> dict:
    out = {}
    for part in value.split(","):
        name, _, dur = part.strip().partition(";dur=")
        if name and dur:
            try:
                out[name] = float(dur)
            except ValueError:
                pass
    return out


def percentile(sorted_values, p):
    if not sorted_values:
        return float("nan")
    idx = min(len(sorted_values) - 1, int(round(p / 100 * (len(sorted_values) - 1))))
    return sorted_values[idx]


def worker(host, port, path, deadline, sample_every, out):
    conn = http.client.HTTPConnection(host, port, timeout=10)
    latencies, errors, timings = [], 0, []
    i = 0
    while time.perf_counter() < deadline:
        t0 = time.perf_counter()
        try:
            conn.request("GET", path)
            resp = conn.getresponse()
            resp.read()
            elapsed = (time.perf_counter() - t0) * 1000
            if resp.status == 200:
                latencies.append(elapsed)
            else:
                errors += 1
            if i % sample_every == 0 and resp.getheader("Server-Timing"):
                timings.append(parse_server_timing(resp.getheader("Server-Timing")))
        except (OSError, http.client.HTTPException):
            errors += 1
            conn.close()
            conn = http.client.HTTPConnection(host, port, timeout=10)
        i += 1
    conn.close()
    out.append((latencies, errors, timings))


def run_level(host, port, path, users, duration, sample_every=50):
    out = []
    deadline = time.perf_counter() + duration
    threads = [threading.Thread(target=worker, args=(host, port, path, deadline, sample_every, out))
               for _ in range(users)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    latencies = sorted(x for lat, _, _ in out for x in lat)
    errors = sum(e for _, e, _ in out)
    timings = [t for _, _, ts in out for t in ts]
    total = len(latencies) + errors
    avg = lambda key: statistics.mean(t[key] for t in timings if key in t) if timings else float("nan")
    return {
        "users": users,
        "requests": total,
        "rps": round(total / duration, 1),
        "p50_ms": round(percentile(latencies, 50), 2),
        "p95_ms": round(percentile(latencies, 95), 2),
        "p99_ms": round(percentile(latencies, 99), 2),
        "error_pct": round(100 * errors / total, 2) if total else 0.0,
        "lock_ms": round(avg("lock"), 3),
        "build_ms": round(avg("build"), 3),
        "json_ms": round(avg("json"), 3),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--catalog", default="http://localhost:5001")
    ap.add_argument("--label", required=True, help="назва прогону, наприклад baseline або optimized")
    ap.add_argument("--cache", choices=["on", "off"], required=True, help="для звіту: стан кешу у сервісі")
    ap.add_argument("--seats", type=int, default=2000)
    ap.add_argument("--from-users", type=int, default=10)
    ap.add_argument("--to-users", type=int, default=190)
    ap.add_argument("--step", type=int, default=20)
    ap.add_argument("--duration", type=int, default=20, help="секунд на рівень")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    key = os.environ.get("AUTH_KEY", "").encode()
    if len(key) < 32:
        sys.exit("Задайте AUTH_KEY (той самий ключ, що й у сервісів, не менше 32 символів).")

    org_token = issue("org@example.com", "organizer", key)
    session_id = create_session(args.catalog, org_token, args.seats)
    host, port = urllib.parse.urlsplit(args.catalog).hostname, urllib.parse.urlsplit(args.catalog).port
    path = f"/api/sessions/{session_id}/seats"

    # Прогрів: кілька запитів перед першим рівнем
    for _ in range(20):
        urllib.request.urlopen(args.catalog + path, timeout=30).read()

    rows = []
    print(f"# прогін: {args.label}, кеш={args.cache}, місць={args.seats}, тривалість рівня={args.duration} с")
    print(f"# середовище: {platform.platform()}, Python {platform.python_version()}, CPU={os.cpu_count()}")
    header = ["users", "requests", "rps", "p50_ms", "p95_ms", "p99_ms", "error_pct", "lock_ms", "build_ms", "json_ms"]
    print("\t".join(header))
    for users in range(args.from_users, args.to_users + 1, args.step):
        row = run_level(host, port, path, users, args.duration)
        rows.append(row)
        print("\t".join(str(row[h]) for h in header), flush=True)

    with open(args.out, "w", newline="", encoding="utf-8") as f:
        f.write(f"# label={args.label} cache={args.cache} seats={args.seats} duration_per_level={args.duration}s "
                f"cpu={os.cpu_count()} python={platform.python_version()}\n")
        writer = csv.DictWriter(f, fieldnames=header)
        writer.writeheader()
        writer.writerows(rows)

    breach = next((r for r in rows if r["p95_ms"] > QS1_P95_MS), None)
    if breach:
        print(f"# QS-1 порушено (p95 > {QS1_P95_MS:.0f} мс) вперше на {breach['users']} користувачах")
    else:
        print(f"# QS-1 не порушено до {rows[-1]['users']} користувачів")


if __name__ == "__main__":
    import urllib.parse  # noqa: E402  (імпорт тут, щоб не ускладнювати верх файлу)
    main()