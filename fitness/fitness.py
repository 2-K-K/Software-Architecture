#!/usr/bin/env python3
"""ЛР7: архітектурні fitness-функції (стандартна бібліотека Python, без залежностей).

Статичні (читають код, не запускають сервіси):
  F1  Домен (src/Ticketing.Domain) не імпортує інфраструктуру, фреймворки й доступ до даних. Поріг: 0 порушень.
  F2  У графі посилань між проєктами (.csproj) немає циклів. Поріг: 0 циклів.
Динамічна (потребує запущеного Catalog.Service):
  F3  p95 відповіді GET /api/sessions/{id}/seats не перевищує 500 мс (QS-1 з ЛР1)
      при 50 одночасних користувачах. Поріг: 500 мс.

Запуск:
  python3 fitness/fitness.py --static
  AUTH_KEY=<ключ ≥ 32 символів> python3 fitness/fitness.py --dynamic
Код виходу: 0 — усі перевірки пройдено; 1 — є порушення (конвеєр зупиняється).
"""
import argparse
import http.client
import base64
import hashlib
import hmac
import json
import os
import pathlib
import re
import sys
import threading
import time
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parent.parent
P95_THRESHOLD_MS = 500.0
USING = re.compile(r"^\s*(?:global\s+)?using\s+(?:static\s+)?(?P<ns>[A-Za-z_][\w.]*)\s*;", re.MULTILINE)
DOMAIN_FORBIDDEN = ["Ticketing.Application", "Ticketing.Infrastructure", "Ticketing.Api", "Microsoft",
                    "System.Data", "System.Net", "Npgsql", "Hangfire", "StackExchange", "Newtonsoft"]
PROJECT_REF = re.compile(r'<ProjectReference\s+Include="([^"]+)"')


def f1_domain_purity():
    violations = []
    for f in (ROOT / "src" / "Ticketing.Domain").rglob("*.cs"):
        if "/obj/" in f.as_posix() or "/bin/" in f.as_posix():
            continue
        for m in USING.finditer(f.read_text(encoding="utf-8")):
            ns = m.group("ns")
            if any(ns == p or ns.startswith(p + ".") for p in DOMAIN_FORBIDDEN):
                violations.append(f"{f.relative_to(ROOT)}: using {ns}")
    return len(violations) == 0, violations


def f2_no_project_cycles():
    graph = {}
    for proj in ROOT.rglob("*.csproj"):
        if "/obj/" in proj.as_posix() or "/bin/" in proj.as_posix():
            continue
        deps = []
        for ref in PROJECT_REF.findall(proj.read_text(encoding="utf-8")):
            deps.append((proj.parent / ref.replace("\\", "/")).resolve())
        graph[proj.resolve()] = deps

    cycles, state, stack = [], {}, []

    def visit(node):
        state[node] = 1
        stack.append(node)
        for nxt in graph.get(node, []):
            if state.get(nxt) == 1:
                cycles.append(" -> ".join(p.relative_to(ROOT).as_posix() for p in stack[stack.index(nxt):] + [nxt]))
            elif nxt not in state:
                visit(nxt)
        stack.pop()
        state[node] = 2

    for node in list(graph):
        if node not in state:
            visit(node)
    return len(cycles) == 0, cycles


def _call(method, url, body=None, token=None):
    u = urllib.parse.urlsplit(url)
    conn = http.client.HTTPConnection(u.hostname, u.port, timeout=10)
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    conn.request(method, u.path, body=json.dumps(body) if body is not None else None, headers=headers)
    res = conn.getresponse()
    data = res.read()
    conn.close()
    return res.status, json.loads(data) if data else {}


def _token(key, sub, role):
    b64 = lambda b: base64.urlsafe_b64encode(b).decode().rstrip("=")
    payload = json.dumps({"sub": sub, "role": role, "exp": int(time.time()) + 900}).encode()
    return b64(payload) + "." + b64(hmac.new(key, payload, hashlib.sha256).digest())


def f3_seats_p95(base, users=50, duration=10):
    key = os.environ.get("AUTH_KEY", "").encode()
    if len(key) < 32:
        return False, ["Задайте AUTH_KEY (не менше 32 символів)."]
    org = _token(key, "org@example.com", "organizer")
    _, ev = _call("POST", f"{base}/api/events", {"title": "Фітнес ЛР7", "organizerName": "CI"}, org)
    seats = [{"row": (i // 50) + 1, "number": (i % 50) + 1, "priceUah": 250} for i in range(2000)]
    _, se = _call("POST", f"{base}/api/sessions", {"eventId": ev["eventId"], "venueName": "Зал",
                  "startsAt": "2030-12-01T18:00:00Z", "seats": seats}, org)
    path = f"/api/sessions/{se['sessionId']}/seats"
    host, port = urllib.parse.urlsplit(base).hostname, urllib.parse.urlsplit(base).port

    latencies, errors = [], [0]
    lock = threading.Lock()
    deadline = time.perf_counter() + duration

    def worker():
        conn = http.client.HTTPConnection(host, port, timeout=10)
        local = []
        while time.perf_counter() < deadline:
            t0 = time.perf_counter()
            try:
                conn.request("GET", path)
                r = conn.getresponse()
                r.read()
                if r.status == 200:
                    local.append((time.perf_counter() - t0) * 1000)
                else:
                    with lock:
                        errors[0] += 1
            except (OSError, http.client.HTTPException):
                with lock:
                    errors[0] += 1
                conn.close()
                conn = http.client.HTTPConnection(host, port, timeout=10)
        conn.close()
        with lock:
            latencies.extend(local)

    threads = [threading.Thread(target=worker) for _ in range(users)]
    for t in threads: t.start()
    for t in threads: t.join()

    if not latencies:
        return False, ["Жодного успішного запиту."]
    latencies.sort()
    p95 = latencies[min(len(latencies) - 1, int(round(0.95 * (len(latencies) - 1))))]
    ok = p95 <= P95_THRESHOLD_MS and errors[0] == 0
    return ok, [f"p95 = {p95:.1f} мс (поріг {P95_THRESHOLD_MS:.0f} мс), запитів {len(latencies)}, помилок {errors[0]}"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--static", action="store_true")
    ap.add_argument("--dynamic", action="store_true")
    ap.add_argument("--catalog", default=os.environ.get("CATALOG_URL", "http://localhost:5001"))
    args = ap.parse_args()
    if not (args.static or args.dynamic):
        args.static = True

    checks = []
    if args.static:
        checks.append(("F1 Домен не залежить від інфраструктури", f1_domain_purity))
        checks.append(("F2 Немає циклічних залежностей між проєктами", f2_no_project_cycles))
    if args.dynamic:
        checks.append(("F3 p95 схеми залу ≤ 500 мс при 50 користувачах (QS-1)", lambda: f3_seats_p95(args.catalog)))

    failed = 0
    for name, fn in checks:
        t0 = time.perf_counter()
        ok, details = fn()
        dt = time.perf_counter() - t0
        print(f"{'PASS' if ok else 'FAIL'}  {name}  ({dt:.2f} с)")
        for d in details[:20]:
            print(f"      {d}")
        failed += 0 if ok else 1
    print(f"Разом: {len(checks) - failed} з {len(checks)} пройдено")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
