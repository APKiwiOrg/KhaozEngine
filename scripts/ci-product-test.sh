#!/usr/bin/env bash
# Exercise the selected assemblies from the existing Release outputs.
set -euo pipefail
: "${RUNNER_TEMP:?}"
mode=${1:?test mode is required}
results="$RUNNER_TEMP/product-ci-results/$mode"
selection=obj/ci-product/selection.json

run_test() {
  local assembly=$1 filter=$2 name=$3
  dotnet test "$assembly/$assembly.csproj" -c Release --no-build --no-restore --filter "$filter" \
    --logger "trx;LogFileName=$name.trx" --results-directory "$results"
}

case "$mode" in
  product)
    dotnet test obj/ci-product/tests.slnx -c Release --no-build --no-restore \
      --filter 'Category!=LiveSocket&Category!=Diagnostic' --logger trx --results-directory "$results"
    jq -r '.test_roots[] | split("/")[-1] | sub("\\.csproj$"; "")' "$selection" > "$RUNNER_TEMP/product-ci-test-assemblies"
    mapfile -t assemblies < "$RUNNER_TEMP/product-ci-test-assemblies"
    python3 -B scripts/ci-product-trx.py "$results" "${assemblies[@]}"
    ;;
  determinism)
    assembly=${2:?determinism assembly is required}
    case "$assembly" in
      KhaozEngine.Foundation.Tests) filter='FullyQualifiedName~DeterministicFp' ;;
      KhaozEngine.FloatingPoint.Tests) filter='FullyQualifiedName~DeterministicFp|FullyQualifiedName~FannedOutDeterminism' ;;
      *) exit 2 ;;
    esac
    for tier in 0 1; do
      name="$assembly-tier-$tier"
      DOTNET_TieredCompilation=$tier run_test "$assembly" "$filter" "$name"
      python3 -B scripts/ci-product-trx.py --require-all "$results/$name.trx" "$assembly"
    done
    ;;
  water-kernel)
    filter='Category=WaterKernelContract&FullyQualifiedName!~TileWaterPrismCensusTests'
    for tier in 0 1; do
      DOTNET_TieredCompilation=$tier run_test KhaozEngine.TileWorld.Physics.Tests "$filter" "physics-tier-$tier"
      DOTNET_TieredCompilation=$tier run_test KhaozEngine.TileWorld.Physics.FloatingPoint.Tests "$filter" "fp-tier-$tier"
      python3 -B scripts/check-water-kernel-trx.py "$results/physics-tier-$tier.trx" "$results/fp-tier-$tier.trx"
    done
    ;;
  sql-*)
    assembly=${2:?SQL test assembly is required}
    run_test "$assembly" "${3:?SQL test filter is required}" "$mode"
    python3 -B scripts/ci-product-trx.py --require-all "$results/$mode.trx" "$assembly"
    ;;
  *) exit 2 ;;
esac
