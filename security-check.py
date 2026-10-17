"""ЛР6: перевірка безпеки на запущених сервісах (стандартна бібліотека, без залежностей).

Перед запуском запустіть Gateway (5000), Catalog (5001), Sales (5002), Notifications (5003)
з тими самими змінними середовища, що й тут:
    Auth__Key=<ключ не менше 32 символів>  Auth__DemoPassword=<пароль демо-акаунтів>

Запуск:  AUTH_KEY=<той самий ключ> DEMO_PASSWORD=<пароль> python security-check.py
Перевірка недоступного сервісу позначається як SKIP, а не як PASS.
"""
import base64
import hashlib
import hmac
import json
import os
import sys
import time
import urllib.error
import urllib.request

GATE = "http://localhost:5000"
CATALOG = "http://localhost:5001"
SALES = "http://localhost:5002"
NOTIFY = "http://localhost:5003"

AUTH_KEY = os.environ.get("AUTH_KEY", "")
DEMO_PASSWORD = os.environ.get("DEMO_PASSWORD", "")
if len(AUTH_KEY) < 32 or not DEMO_PASSWORD:
    sys.exit("Задайте AUTH_KEY (не менше 32 символів) і DEMO_PASSWORD.")

results = []


def record(name, ok, detail="", skip=False):
    status = "SKIP" if skip else ("PASS" if ok else "FAIL")
    results.append((status, name, detail))
    print(f"{status:4}  {name}  {detail}")


def call(method, url, body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            raw = resp.read()
            return resp.status, (json.loads(raw) if raw else {})
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw)
        except json.JSONDecodeError:
            return e.code, {}
    except urllib.error.URLError as e:
        raise ConnectionError(str(e)) from e


def b64u(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).decode().rstrip("=")


def forge(sub, role, exp_offset_seconds, key=AUTH_KEY.encode()):
    """Токен у тому самому форматі, що й TokenService. Підпис вірний лише тоді, коли key правильний."""
    payload = json.dumps({"sub": sub, "role": role, "exp": int(time.time()) + exp_offset_seconds}).encode()
    sig = hmac.new(key, payload, hashlib.sha256).digest()
    return b64u(payload) + "." + b64u(sig)


def login(login_name, password=DEMO_PASSWORD):
    status, body = call("POST", f"{GATE}/api/auth/login", {"login": login_name, "password": password})
    return body.get("token") if status == 200 else None


def check(name, fn):
    try:
        fn()
    except ConnectionError as e:
        record(name, False, f"сервіс недоступний: {e}", skip=True)


print("== Вхід і токен")
def t_login_wrong():
    status, _ = call("POST", f"{GATE}/api/auth/login", {"login": "org@example.com", "password": "wrong"})
    record("Вхід з невірним паролем відхиляється", status == 401, f"HTTP {status}")
check("login-wrong", t_login_wrong)

org = login("org@example.com")
org2 = login("org2@example.com")
alice = login("alice@example.com")
bob = login("bob@example.com")
gate = login("gate@example.com")
admin = login("admin@example.com")
record("Вхід дає токен для всіх демо-ролей", all([org, org2, alice, bob, gate, admin]))

print("== Перевірка на вході (шлюз) і обхід шлюзу")
def t_gate_no_token():
    status, _ = call("POST", f"{GATE}/api/events", {"title": "X", "organizerName": "Y"})
    record("Шлюз: створення заходу без токена → 401", status == 401, f"HTTP {status}")
check("gate-no-token", t_gate_no_token)

def t_bypass_catalog():
    status, _ = call("POST", f"{CATALOG}/api/events", {"title": "X", "organizerName": "Y"})
    record("Обхід шлюзу (прямо в Catalog) без токена → 401", status == 401, f"HTTP {status}")
check("bypass-catalog", t_bypass_catalog)

def t_bypass_internal():
    status, _ = call("POST", f"{CATALOG}/internal/sessions/{'0'*8}-0000-0000-0000-000000000000/reservations",
                     {"orderId": "00000000-0000-0000-0000-000000000001", "seats": []})
    record("Прямий виклик внутрішнього ендпоінта Catalog без токена → 401", status == 401, f"HTTP {status}")
check("bypass-internal", t_bypass_internal)

print("== Підроблені та прострочені токени")
def t_forged_role():
    alice_tok = alice
    payload, sig = alice_tok.split(".")
    fake_payload = b64u(json.dumps({"sub": "alice@example.com", "role": "organizer",
                                    "exp": int(time.time()) + 600}).encode())
    status, _ = call("POST", f"{GATE}/api/events", {"title": "X", "organizerName": "Y"}, token=f"{fake_payload}.{sig}")
    record("Підміна ролі в токені (підпис не збігається) → 401", status == 401, f"HTTP {status}")
check("forged-role", t_forged_role)

def t_expired():
    tok = forge("org@example.com", "organizer", -60)
    status, _ = call("POST", f"{GATE}/api/events", {"title": "X", "organizerName": "Y"}, token=tok)
    record("Прострочений токен із вірним підписом → 401", status == 401, f"HTTP {status}")
check("expired", t_expired)

def t_wrong_key():
    tok = forge("org@example.com", "organizer", 600, key=b"another-key-that-is-long-enough-0123456789")
    status, _ = call("POST", f"{CATALOG}/api/events", {"title": "X", "organizerName": "Y"}, token=tok)
    record("Токен, підписаний іншим ключем, відхиляє Catalog → 401", status == 401, f"HTTP {status}")
check("wrong-key", t_wrong_key)

print("== Ролі та власник ресурсу")
def t_buyer_creates_event():
    status, _ = call("POST", f"{GATE}/api/events", {"title": "X", "organizerName": "Y"}, token=alice)
    record("Покупець не може створити захід → 403", status == 403, f"HTTP {status}")
check("buyer-event", t_buyer_creates_event)

event_id = None
session_id = None
def t_org_event():
    global event_id
    status, body = call("POST", f"{GATE}/api/events", {"title": "Безпека ЛР6", "organizerName": "Тест"}, token=org)
    event_id = body.get("eventId")
    record("Організатор створює захід → 201", status == 201 and bool(event_id), f"HTTP {status}")
check("org-event", t_org_event)

def t_org_session():
    global session_id
    status, body = call("POST", f"{GATE}/api/sessions", {
        "eventId": event_id, "venueName": "Зал", "startsAt": "2030-12-01T18:00:00Z",
        "seats": [{"row": 1, "number": n, "priceUah": 250} for n in (1, 2, 3)]}, token=org)
    session_id = body.get("sessionId")
    record("Власник створює сеанс → 201", status == 201 and bool(session_id), f"HTTP {status}")
check("org-session", t_org_session)

def t_other_org_session():
    status, _ = call("POST", f"{GATE}/api/sessions", {
        "eventId": event_id, "venueName": "Зал", "startsAt": "2030-12-01T18:00:00Z",
        "seats": [{"row": 2, "number": 1, "priceUah": 250}]}, token=org2)
    record("Інший організатор не може додати сеанс до чужого заходу → 403", status == 403, f"HTTP {status}")
check("other-org-session", t_other_org_session)

def t_public_seats():
    status, _ = call("GET", f"{GATE}/api/sessions/{session_id}/seats")
    record("Схема залу доступна без входу (публічна) → 200", status == 200, f"HTTP {status}")
check("public-seats", t_public_seats)

order_id = None
def t_order_own():
    global order_id
    status, body = call("POST", f"{GATE}/api/orders", {
        "sessionId": session_id, "buyerEmail": "alice@example.com", "seats": [{"row": 1, "number": 1}]}, token=alice)
    order_id = body.get("orderId")
    record("Покупець оформлює замовлення на свою адресу → 201", status == 201 and bool(order_id), f"HTTP {status}")
check("order-own", t_order_own)

def t_order_other_email():
    status, _ = call("POST", f"{GATE}/api/orders", {
        "sessionId": session_id, "buyerEmail": "bob@example.com", "seats": [{"row": 1, "number": 2}]}, token=alice)
    record("Квитки не можна надіслати на чужу адресу → 403", status == 403, f"HTTP {status}")
check("order-other-email", t_order_other_email)

def t_other_buyer_pays():
    status, _ = call("POST", f"{GATE}/api/orders/{order_id}/payment", token=bob)
    record("Чужий покупець не може оплатити замовлення → 403", status == 403, f"HTTP {status}")
check("other-buyer-pays", t_other_buyer_pays)

def t_other_buyer_reads():
    status, _ = call("GET", f"{GATE}/api/orders/{order_id}", token=bob)
    record("Чужий покупець не бачить замовлення → 403", status == 403, f"HTTP {status}")
check("other-buyer-reads", t_other_buyer_reads)

def t_owner_pays():
    status, body = call("POST", f"{GATE}/api/orders/{order_id}/payment", token=alice)
    ok = status == 200 and body.get("status") == "Paid"
    record("Власник оплачує; Sales підтверджує місця в Catalog службовим токеном → 200", ok, f"HTTP {status}")
check("owner-pays", t_owner_pays)

def t_buyer_validates():
    status, _ = call("POST", f"{GATE}/api/tickets/validation", {"code": "X"}, token=alice)
    record("Покупець не може перевіряти квитки на вході → 403", status == 403, f"HTTP {status}")
check("buyer-validates", t_buyer_validates)

def t_controller_validates():
    status, body = call("POST", f"{GATE}/api/tickets/validation", {"code": "NOPE"}, token=gate)
    record("Контролер перевіряє квиток → 200 (відмова за невідомим кодом)", status == 200 and body.get("accepted") is False,
           f"HTTP {status}")
check("controller-validates", t_controller_validates)

def t_notify_no_token():
    status, _ = call("POST", f"{NOTIFY}/events", {"eventId": "11111111-1111-1111-1111-111111111111", "type": "TicketsIssued",
                                                  "occurredAt": "2026-10-13T10:00:00Z",
                                                  "payload": {"orderId": "22222222-2222-2222-2222-222222222222",
                                                              "sessionId": "33333333-3333-3333-3333-333333333333",
                                                              "buyerEmail": "x@example.com", "ticketCodes": ["A"]}})
    record("Підроблена подія в Notifications без службового токена → 401", status == 401, f"HTTP {status}")
check("notify-no-token", t_notify_no_token)

def t_notify_list_buyer():
    status, _ = call("GET", f"{NOTIFY}/api/notifications", token=alice)
    record("Список сповіщень (адреси покупців) лише для адміністратора → 403 для покупця", status == 403, f"HTTP {status}")
check("notify-list-buyer", t_notify_list_buyer)

print()
failed = [r for r in results if r[0] == "FAIL"]
passed = [r for r in results if r[0] == "PASS"]
skipped = [r for r in results if r[0] == "SKIP"]
print(f"Разом: PASS {len(passed)}, FAIL {len(failed)}, SKIP {len(skipped)}")
sys.exit(1 if failed else 0)