#!/bin/bash
# Installed by infra for postgres.backup. Mounted as schedule, backup, list and restore.
set -euo pipefail

ready_file=/tmp/infra-backup.ready
empty_hash=e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }

fail() {
  log "error: $*"
  exit 1
}

require_settings() {
  local name
  for name in PGHOST PGUSER PGPASSWORD PGDATABASE BACKUP_ENDPOINT BACKUP_REGION BACKUP_BUCKET BACKUP_PREFIX BACKUP_CREDENTIALS; do
    [ -n "${!name:-}" ] || fail "$name is not set"
  done
  access_key=${BACKUP_CREDENTIALS%%:*}
  secret_key=${BACKUP_CREDENTIALS#*:}
  if [ "$access_key" = "$BACKUP_CREDENTIALS" ] || [ -z "$access_key" ] || [ -z "$secret_key" ]; then
    fail "BACKUP_CREDENTIALS must be <access key>:<secret key>"
  fi
}

ensure_curl() {
  command -v curl >/dev/null 2>&1 && return 0
  log "installing curl"
  local output
  if ! output=$(apt-get update -qq 2>&1 && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends curl ca-certificates 2>&1); then
    printf '%s\n' "$output" >&2
    fail "could not install curl"
  fi
  rm -rf /var/lib/apt/lists/*
}

encode() {
  LC_ALL=C perl -e 'my ($text, $keep) = @ARGV; my $k = quotemeta($keep // ""); $text =~ s/([^A-Za-z0-9_.~\-$k])/sprintf("%%%02X", ord($1))/ge; print $text' "$1" "${2:-}"
}

s3() {
  local method=$1 target=$2 payload_hash=$3
  shift 3
  curl --config - -fsS --retry 3 -X "$method" \
    --aws-sigv4 "aws:amz:${BACKUP_REGION}:s3" \
    -H "x-amz-content-sha256: $payload_hash" \
    "$@" "${BACKUP_ENDPOINT%/}/${BACKUP_BUCKET}${target}" <<<"user = \"${access_key}:${secret_key}\""
}

wait_for_postgres() {
  local attempt
  for attempt in $(seq 1 60); do
    pg_isready -q && return 0
    sleep 2
  done
  fail "postgres at $PGHOST did not accept connections"
}

list_dumps() {
  local token="" query response
  while :; do
    query="list-type=2&prefix=$(encode "$BACKUP_PREFIX/$PGDATABASE-")"
    [ -z "$token" ] || query="$query&continuation-token=$(encode "$token")"
    response=$(s3 GET "/?$query" "$empty_hash")
    printf '%s' "$response" | perl -0777 -ne '
      sub text { my $value = shift; $value =~ s/&lt;/</g; $value =~ s/&gt;/>/g; $value =~ s/&quot;/"/g; $value =~ s/&apos;/'"'"'/g; $value =~ s/&amp;/&/g; $value }
      while (/<Contents>(.*?)<\/Contents>/sg) {
        my $item = $1;
        my ($key) = $item =~ /<Key>(.*?)<\/Key>/s;
        my ($size) = $item =~ /<Size>(\d+)<\/Size>/;
        my ($modified) = $item =~ /<LastModified>(.*?)<\/LastModified>/;
        print text($key), "\t", $size, "\t", $modified, "\n" if $key =~ /\.dump$/;
      }'
    token=$(printf '%s' "$response" | perl -0777 -ne 'print $1 if /<IsTruncated>true<\/IsTruncated>/ && /<NextContinuationToken>(.*?)<\/NextContinuationToken>/s')
    [ -n "$token" ] || break
  done
}

dump=""
trap 'rm -f "$dump"' EXIT

run_backup() {
  local key size
  key="$BACKUP_PREFIX/$PGDATABASE-$(date -u +%Y%m%dT%H%M%SZ).dump"
  dump=$(mktemp /tmp/infra-backup.XXXXXX)
  log "dumping $PGDATABASE from $PGHOST"
  pg_dump -Fc -f "$dump"
  pg_restore --list "$dump" >/dev/null
  size=$(stat -c %s "$dump")
  s3 PUT "/$(encode "$key" /)" "$(sha256sum "$dump" | cut -d' ' -f1)" -T "$dump" -o /dev/null
  log "uploaded $key ($size bytes) to $BACKUP_BUCKET"
  printf '%s\n' "$key"
}

run_restore() {
  local key=${1:-} target=${2:-$PGDATABASE}
  [ -n "$key" ] || fail "usage: restore <key> [database]"
  [[ $target =~ ^[A-Za-z_][A-Za-z0-9_-]*$ ]] || fail "database name '$target' must be letters, digits, _ and -"
  dump=$(mktemp /tmp/infra-restore.XXXXXX)
  log "downloading $key from $BACKUP_BUCKET"
  s3 GET "/$(encode "$key" /)" "$empty_hash" -o "$dump"
  pg_restore --list "$dump" >/dev/null || fail "$key is not a pg_dump custom-format archive"
  wait_for_postgres
  if [ "$target" = "$PGDATABASE" ]; then
    log "restoring $key over $PGDATABASE"
    pg_restore --clean --if-exists --no-owner --no-acl --single-transaction -d "$PGDATABASE" "$dump"
  else
    log "restoring $key into the new database $target"
    createdb "$target"
    if ! pg_restore --no-owner --no-acl --single-transaction -d "$target" "$dump"; then
      dropdb --if-exists "$target" || true
      fail "restore into $target failed; dropped $target"
    fi
  fi
  log "restored $key into $target"
}

field_matches() {
  local spec=$1 value=$((10#$2)) lowest=$3 highest=$4 part range step from to
  local -a parts
  IFS=, read -ra parts <<<"$spec"
  for part in "${parts[@]}"; do
    range=${part%%/*}
    step=1
    [ "$range" = "$part" ] || step=${part#*/}
    case "$range" in
      '*') from=$lowest to=$highest ;;
      *-*) from=${range%-*} to=${range#*-} ;;
      *) from=$range to=$range; [ "$range" = "$part" ] || to=$highest ;;
    esac
    if ((value >= 10#$from && value <= 10#$to && (value - 10#$from) % 10#$step == 0)); then
      return 0
    fi
  done
  return 1
}

schedule_due() {
  local minute hour day month weekday day_ok=1 weekday_ok=1
  local -a fields
  read -r minute hour day month weekday <<<"$(date -u '+%M %H %d %m %w')"
  read -ra fields <<<"$BACKUP_SCHEDULE"
  field_matches "${fields[0]}" "$minute" 0 59 || return 1
  field_matches "${fields[1]}" "$hour" 0 23 || return 1
  field_matches "${fields[3]}" "$month" 1 12 || return 1
  field_matches "${fields[2]}" "$day" 1 31 && day_ok=0
  if field_matches "${fields[4]}" "$weekday" 0 7 || { [ "$weekday" = 0 ] && field_matches "${fields[4]}" 7 0 7; }; then
    weekday_ok=0
  fi
  if [ "${fields[2]}" != '*' ] && [ "${fields[4]}" != '*' ]; then
    [ "$day_ok" = 0 ] || [ "$weekday_ok" = 0 ]
  else
    [ "$day_ok" = 0 ] && [ "$weekday_ok" = 0 ]
  fi
}

run_schedule() {
  trap 'exit 0' TERM INT
  rm -f "$ready_file"
  wait_for_postgres
  s3 GET "/?list-type=2&max-keys=1&prefix=$(encode "$BACKUP_PREFIX/")" "$empty_hash" -o /dev/null \
    || fail "can't list $BACKUP_BUCKET/$BACKUP_PREFIX/ at $BACKUP_ENDPOINT; check the bucket and the credentials"
  touch "$ready_file"
  log "backing up $PGDATABASE on '$BACKUP_SCHEDULE' (UTC) to $BACKUP_BUCKET/$BACKUP_PREFIX/"
  while :; do
    sleep $((60 - 10#$(date -u +%S))) &
    wait $!
    if schedule_due; then
      set +e
      (
        set -e
        trap 'rm -f "$dump"' EXIT
        run_backup >/dev/null
      )
      local status=$?
      set -e
      [ "$status" = 0 ] || log "backup failed with exit code $status; trying again at the next scheduled time"
    fi
  done
}

command=$(basename "$0")
case "$command" in
  schedule | backup | list | restore) ;;
  *) fail "unknown command $command; run this script as schedule, backup, list or restore" ;;
esac

require_settings
ensure_curl
case "$command" in
  schedule) run_schedule ;;
  backup) run_backup ;;
  list) list_dumps ;;
  restore) run_restore "$@" ;;
esac
