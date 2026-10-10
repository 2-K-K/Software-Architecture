#!/usr/bin/env bash
# Запускає всі чотири сервіси як окремі процеси. Логи: services/logs/*.log
# Зупинка: services/stop-all.sh
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p logs pids

start() {
  local name="$1" project="$2" url="$3"
  dotnet run --no-launch-profile --project "$project" --urls "$url" > "logs/$name.log" 2>&1 &
  echo $! > "pids/$name.pid"
  echo "запущено $name на $url (pid $!)"
}

start catalog       Catalog.Service/Catalog.Service.csproj             http://localhost:5001
start sales         Sales.Service/Sales.Service.csproj                 http://localhost:5002
start notifications Notifications.Service/Notifications.Service.csproj http://localhost:5003
start gateway       Gateway/Gateway.csproj                             http://localhost:5000

echo "Готово. Шлюз: http://localhost:5000"
