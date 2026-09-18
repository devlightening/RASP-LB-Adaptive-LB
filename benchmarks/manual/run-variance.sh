#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"

cd "$REPO_ROOT"

APPSETTINGS="Gateway/RaspLb.Gateway/appsettings.json"
OUTDIR="${BENCHMARK_OUTPUT_DIR:-benchmarks/manual/variance-warmup}"
mkdir -p "$OUTDIR"

ORIGINAL_POLICY="$(grep -o '"LoadBalancingPolicy": "[^"]*"' "$APPSETTINGS" | head -1 | cut -d '"' -f 4)"

restore_policy() {
  sed -i "s/\"LoadBalancingPolicy\": \"Rasp[A-Za-z0-9]*\"/\"LoadBalancingPolicy\": \"$ORIGINAL_POLICY\"/" "$APPSETTINGS"
}

trap restore_policy EXIT

wait_ready() {
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
    code=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5200/debug/retries 2>/dev/null || echo "000")
    if [ "$code" = "200" ]; then return 0; fi
    sleep 2
  done
  echo "WARNING: gateway not ready after wait"
  return 0
}

for POLICY in RaspV0 RaspV1 RaspV2; do
  echo "=== Switching to $POLICY ==="
  sed -i "s/\"LoadBalancingPolicy\": \"Rasp[A-Za-z0-9]*\"/\"LoadBalancingPolicy\": \"$POLICY\"/" "$APPSETTINGS"
  grep LoadBalancingPolicy "$APPSETTINGS"

  docker compose up --build -d --no-deps gateway > /dev/null 2>&1
  wait_ready

  for run in 1 2 3 4 5; do
    echo "--- $POLICY run $run ---"
    docker compose restart gateway > /dev/null 2>&1
    wait_ready
    dotnet run --project Benchmark/RaspLb.Benchmark/RaspLb.Benchmark.csproj -c Release --no-build > "$OUTDIR/${POLICY}_run${run}.txt" 2>&1
  done
done

echo "DONE"
