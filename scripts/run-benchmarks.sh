#!/usr/bin/env bash
# Run BenchmarkDotNet performance benchmarks.
#
# Usage:
#   ./scripts/run-benchmarks.sh              # Run all benchmarks
#   ./scripts/run-benchmarks.sh --list       # List available benchmarks
#   ./scripts/run-benchmarks.sh --filter "*Parser*"  # Run specific benchmarks
#
# Results are written to tests/AgentAcademy.Server.Benchmarks/BenchmarkDotNet.Artifacts/

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BENCH_DIR="$ROOT_DIR/tests/AgentAcademy.Server.Benchmarks"

echo "🔨 Building benchmarks in Release mode..."
dotnet build "$BENCH_DIR/AgentAcademy.Server.Benchmarks.csproj" \
  -c Release --verbosity quiet

echo ""
echo "🏃 Running benchmarks..."
dotnet run --project "$BENCH_DIR/AgentAcademy.Server.Benchmarks.csproj" \
  -c Release --no-build -- "$@"

echo ""
echo "📊 Results in: $BENCH_DIR/BenchmarkDotNet.Artifacts/"
