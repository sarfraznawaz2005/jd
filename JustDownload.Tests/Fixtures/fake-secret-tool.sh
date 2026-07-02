#!/bin/sh
# Fake stand-in for libsecret's secret-tool, used only by LinuxSecretToolSecretStoreTests to prove
# LinuxSecretToolSecretStore's real Process.Start/stdin-write/stdout-read/exit-code logic without a
# real Secret Service daemon (none is installable on the box that runs this test). It mirrors just
# enough of secret-tool's CLI contract for the three commands the store issues:
#   store --label <text> service <svc> account <acct>   (secret arrives on stdin)
#   lookup service <svc> account <acct>                  (echoes the secret back, no output if unknown)
#   clear service <svc> account <acct>                   (removes the entry)
# State lives under $FAKE_SECRET_TOOL_VAULT, a directory the test owns and cleans up.

set -eu

vault="${FAKE_SECRET_TOOL_VAULT:?FAKE_SECRET_TOOL_VAULT must be set}"
mkdir -p "$vault"

command="$1"
shift

account=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --label)
      shift 2
      ;;
    account)
      account="$2"
      shift 2
      ;;
    *)
      shift 2
      ;;
  esac
done

file="$vault/$account"

case "$command" in
  store)
    cat > "$file"
    ;;
  lookup)
    if [ -f "$file" ]; then
      cat "$file"
      exit 0
    fi
    exit 1
    ;;
  clear)
    rm -f "$file"
    ;;
  *)
    exit 2
    ;;
esac
