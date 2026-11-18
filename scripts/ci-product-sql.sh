#!/usr/bin/env bash
# Run the legacy live filters against isolated databases on one disposable SQL Server.
set -euo pipefail
: "${RUNNER_TEMP:?}"
mode=${1:?SQL mode is required}
container=product-ci-sql
password_file="$RUNNER_TEMP/$container-password"
catalog_db=khaoz-catalog-test-ci
accounts_db=khaoz-accounts-test-ci
journal_db=khaoz-journal-test-ci
commerce_db=khaoz-commerce-test-ci

sql() {
  SQLCMDPASSWORD=$password docker exec --env SQLCMDPASSWORD "$container" \
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -l 3 -t 5 -Q "$1"
}

connection() {
  printf 'Server=127.0.0.1,1433;Initial Catalog=%s;User ID=sa;Password=%s;Encrypt=True;TrustServerCertificate=True' "$1" "$password"
}

create_database() {
  sql "CREATE DATABASE [$1]"
}

if [[ $mode == start ]]; then
  password="Engine!$(openssl rand -hex 24)"
  echo "::add-mask::$password"
  (umask 077 && printf '%s\n' "$password" > "$password_file")
  MSSQL_SA_PASSWORD=$password docker run --detach --name "$container" \
    --publish 127.0.0.1:1433:1433 --env ACCEPT_EULA=Y --env MSSQL_SA_PASSWORD \
    mcr.microsoft.com/mssql/server@sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f
  for (( attempt=1; attempt<=40; attempt++ )); do
    if sql 'SELECT 1' >/dev/null 2>&1; then
      echo 'SQL Server accepted a connection'
      exit 0
    fi
    sleep 2
  done
  echo 'SQL Server did not become ready' >&2
  exit 1
fi

read -r password < "$password_file"
case "$mode" in
  sql-catalog)
    create_database "$catalog_db"
    KE_CATALOG_SQLSERVER=$(connection "$catalog_db") bash scripts/ci-product-test.sh "$mode" \
      KhaozEngine.Server.Tests 'FullyQualifiedName~KhaozEngine.Tests.Catalog'
    ;;
  sql-accounts)
    create_database "$accounts_db"
    KE_ACCOUNTS_SQLSERVER=$(connection "$accounts_db") bash scripts/ci-product-test.sh "$mode" \
      KhaozEngine.Accounts.Tests 'FullyQualifiedName~KhaozEngine.Tests.Accounts.SqlServer'
    ;;
  sql-server)
    create_database "$journal_db"
    create_database "$commerce_db"
    KE_SQLSERVER_TEST_CONNSTRING=$(connection "$journal_db") KE_COMMERCE_SQLSERVER=$(connection "$commerce_db") \
      bash scripts/ci-product-test.sh "$mode" KhaozEngine.Server.Tests \
      'FullyQualifiedName~KhaozEngine.Tests.WorldStore|FullyQualifiedName~KhaozEngine.Tests.Commerce.SqlServer'
    ;;
  *) exit 2 ;;
esac
