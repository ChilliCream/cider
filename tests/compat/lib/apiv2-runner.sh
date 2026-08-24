#!/usr/bin/env bash
# tests/compat/lib/apiv2-runner.sh
#
# A from-scratch reimplementation of enough of Podman's test/apiv2 harness
# (test/apiv2/test-apiv2, upstream Apache-2.0) to `source` the upstream
# `.at` files unmodified and run their assertions against OUR socket
# instead of a `podman system service`.
#
# Why reimplement instead of running upstream's `test-apiv2` as-is: that
# script hard-launches `podman system service` on a TCP port and requires
# the real `podman` binary throughout (both for the HTTP-hitting `t()` calls
# and for direct `podman ...` shell-outs used to set up fixtures). We have
# neither `podman` nor a TCP listener — we have a unix socket. This file
# provides drop-in `t`/`is`/`is_not`/`like`/`jsonify` functions (same
# names, same testfile-facing behavior) plus a `podman()` shell function
# that shims common subcommands to the `docker` CLI (already pointed at our
# socket by lib/daemon.sh), so the upstream `.at` files can be `source`d
# completely unmodified.
#
# ─────────────────────────────────────────────────────────────────────────
# Design decision: libpod vs. Docker-compat grading
# ─────────────────────────────────────────────────────────────────────────
# Every `.at` file interleaves two kinds of `t` calls: bare/`/v1.xx/`-prefixed
# paths hit Docker's compat API (what cider implements); `libpod/...`
# or `/vX.Y.Z/libpod/...` paths hit Podman's own extension API (which is
# NOT part of the Docker Engine API contract and which cider has no
# obligation to implement).
#
# We only *grade* (count toward pass/fail/allowlist) the Docker-compat
# calls. But several `.at` files use a libpod call purely as a fixture: e.g.
# `iid=$(jq -r '.[0].Id' <<<"$output")` right after `t GET libpod/images/json`,
# where `$iid` then feeds a dozen downstream *compat* assertions later in the
# same file. If we simply skipped the HTTP call for libpod paths, `$output`
# would be empty and every one of those downstream compat assertions would
# fail for a fixture reason that has nothing to do with cider's
# Docker-compat behavior — a false signal.
#
# So: for any `libpod/...` path, we still perform the HTTP call, but we
# redirect it to the *same relative path with the `libpod/` segment
# stripped* — i.e. `libpod/images/json` actually hits our compat
# `images/json`, `libpod/info` hits our compat `info`, etc. This is not
# always shape-identical (e.g. libpod's `/info` has `.store.volumePath`,
# our compat `/info` does not — see reports/podman-apiv2-failures.md for the
# handful of downstream assertions this still leaves broken), but for the
# common case — "grab the id/name out of a listing" — the compat and libpod
# resource shapes overlap enough (`.Id`, `.Names[0]`, `.Name`) that this
# keeps the fixture chain alive. The subtests belonging to the *libpod* call
# itself are always recorded as `skip` (never graded), regardless of what
# the redirected response looks like — we are not claiming cider
# supports libpod, only reusing the response as a variable source.
# ─────────────────────────────────────────────────────────────────────────

: "${CIDER_COMPAT_SOCKET:?apiv2-runner.sh requires CIDER_COMPAT_SOCKET (source lib/daemon.sh first)}"

# Test image used throughout upstream podman .at files (small, stable,
# multi-purpose fixture image maintained by the podman project).
IMAGE="${IMAGE:-quay.io/libpod/testimage:20241011}"
PODMAN_TEST_IMAGE_REGISTRY="quay.io"
PODMAN_TEST_IMAGE_USER="libpod"
PODMAN_TEST_IMAGE_NAME="testimage"
PODMAN_TEST_IMAGE_TAG="20241011"

WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/cider-compat-apiv2.XXXXXX")"

# ---------------------------------------------------------------------------
# Bookkeeping: every subtest lands in exactly one of these three buckets.
# PASS/FAIL entries are graded (Docker-compat); SKIP entries are not
# (libpod-only, or a suite feature this runner doesn't implement — see
# start_registry/stop_registry below).
# ---------------------------------------------------------------------------
declare -a PASS_RESULTS=()
declare -a FAIL_RESULTS=()   # each entry: id \x1f testname \x1f expected \x1f actual
declare -a SKIP_RESULTS=()
SEQ=0
CURRENT_FILE="?"
# Set by t() before each assertion; is()/like()/is_not() consult it so the
# exact same comparison code path works for both graded and libpod-redirect
# (ungraded) calls.
_T_GRADE=1

_apiv2_color() {
  if [ -t 1 ]; then
    case "$1" in
      red) printf '\033[31m' ;;
      green) printf '\033[32m' ;;
      yellow) printf '\033[33m' ;;
      reset) printf '\033[0m' ;;
    esac
  fi
}

# _record ok|skip testname expected actual
_record() {
  local ok=$1 testname=$2 expected=${3:-} actual=${4:-}
  local id
  id="$(printf '%s#%04d' "$CURRENT_FILE" "$SEQ")"
  SEQ=$((SEQ + 1))

  if [[ "$ok" == "skip" || "$_T_GRADE" == "0" ]]; then
    SKIP_RESULTS+=("$id $testname")
    printf '%sskip%s %s %s\n' "$(_apiv2_color yellow)" "$(_apiv2_color reset)" "$id" "$testname"
    return
  fi
  if [[ "$ok" == "1" ]]; then
    PASS_RESULTS+=("$id $testname")
    printf '%sok%s   %s %s\n' "$(_apiv2_color green)" "$(_apiv2_color reset)" "$id" "$testname"
  else
    FAIL_RESULTS+=("$id"$'\x1f'"$testname"$'\x1f'"$expected"$'\x1f'"$actual")
    printf '%sFAIL%s %s %s (expected: %s / actual: %s)\n' "$(_apiv2_color red)" "$(_apiv2_color reset)" "$id" "$testname" "$expected" "$actual"
  fi
}

########
#  is  #  Simple comparison (same contract as upstream)
########
is() {
  local actual=$1 expect=$2 testname=$3
  if [ "$actual" = "$expect" ]; then
    _record 1 "$testname=$expect"
  else
    _record 0 "$testname" "$expect" "$actual"
  fi
}

############
#  is_not  #
############
is_not() {
  local actual=$1 expect_not=$2 testname=$3
  if [ "$actual" != "$expect_not" ]; then
    _record 1 "$testname!=$expect_not"
  else
    _record 0 "$testname" "!= $expect_not" "$actual"
  fi
}

# _bre_to_ere PATTERN -- convert the small subset of POSIX BRE metacharacters
# upstream's .at files actually use (\+ \{n\} \(...\) \| \?) to ERE syntax,
# so bash's own `[[ =~ ]]` (always ERE) can evaluate them natively.
#
# Why this exists: upstream's own `t()`/`like()` shell out to `expr "$x" :
# "$pattern"`, which works on Linux (GNU expr, where `\+` means "one or
# more"). macOS ships BSD expr, which does NOT implement that GNU BRE
# extension -- `expr foo : '[^=~]\+=.*'` silently fails to match on this
# machine, which (before this fix) made nearly every `.field=value` /
# `.field~pattern` assertion in every `.at` file fall through to a bogus
# "compare the whole JSON body as a literal string" branch. Reimplementing
# the matching with bash's own regex engine sidesteps the BSD/GNU expr
# incompatibility entirely instead of depending on which `expr` happens to
# be on PATH.
_bre_to_ere() {
  printf '%s' "$1" | sed -e 's/\\+/+/g' -e 's/\\{/{/g' -e 's/\\}/}/g' \
    -e 's/\\(/(/g' -e 's/\\)/)/g' -e 's/\\|/|/g' -e 's/\\?/?/g'
}

##########
#  like  #  Pattern comparison. Same contract as upstream's `expr "$actual"
#           : "$expect"` (a BRE match anchored at the *start* of $actual,
#           not required to consume the whole string) -- see _bre_to_ere.
##########
like() {
  local actual=$1 expect=$2 testname=$3
  local ere
  ere=$(_bre_to_ere "$expect")
  if [[ "$actual" =~ ^($ere) ]]; then
    _record 1 "$testname ~ $expect"
  else
    _record 0 "$testname" "~ $expect" "$actual"
  fi
}

#############
#  jsonify  #  'foo=bar,x=y' -> {"foo":"bar","x":"y"}  (verbatim from upstream)
#############
jsonify() {
  local -a settings_out
  for i in "$@"; do
    local lhs rhs
    IFS='=' read -r lhs rhs <<<"$i"
    if [[ $rhs =~ \" || $rhs == true || $rhs == false || $rhs == "[]" || $rhs =~ ^-?[0-9]+$ ]]; then
      :
    elif [[ $rhs == False ]]; then
      rhs=false
    elif [[ $rhs == True ]]; then
      rhs=true
    else
      rhs="\"${rhs}\""
    fi
    settings_out+=("\"${lhs}\":${rhs}")
  done
  (IFS=','; echo "{${settings_out[*]}}")
}

# random_string LEN — used by a couple of upstream .at files
random_string() {
  local length=${1:-10}
  head /dev/urandom | tr -dc a-zA-Z0-9 | head -c"$length"
}

# start_registry/stop_registry: upstream spins up a real TLS registry
# container (with openssl-generated certs) to test authenticated push.
# That's registry/auth plumbing, not Docker-compat surface, and 60-auth.at
# is explicitly out of scope for this harness — so these are deliberate
# no-ops. Any `t` call downstream that depended on $REGISTRY_PORT will fail
# predictably (empty port -> malformed URL); such failures are categorized
# "podman-specific (local registry fixture, out of scope)" in the report.
start_registry() { echo "# [apiv2-runner] start_registry: no-op (registry/auth out of scope)" >&2; }
stop_registry() { :; }

# start_service/stop_service: 70-short-names.at calls these directly (to
# restart podman's service with a different short-names config between
# sub-scenarios). We have one long-lived cider daemon for the whole
# run and no equivalent config-reload mechanism to exercise, so these are
# no-ops; downstream assertions that depended on the config change fail on
# their own merits (categorized podman-specific/short-name-config).
start_service() { :; }
stop_service() { :; }

#######
#  t  #  Main test helper — same calling convention as upstream, retargeted
#        at our unix socket, with libpod redirect+skip grading (see header).
#######
t() {
  local method=$1; shift
  local path=$1; shift
  local -a curl_args form_args
  local content_type="application/json"
  local testname="$method $path"

  if [[ $method = "POST" || $method == "PUT" || $method == "DELETE" ]]; then
    local -a post_args
    if [[ $method == "POST" ]]; then
      _add_curl_args() { curl_args+=(--data-binary @"$1"); }
    else
      _add_curl_args() { curl_args+=(--upload-file "$1"); }
    fi
    for arg; do
      case "$arg" in
        -) curl_args+=(--disable); shift ;;
        --form=*) form_args+=(--form); form_args+=("${arg#--form=}"); content_type="multipart/form-data"; shift ;;
        *=*) post_args+=("$arg"); shift ;;
        *.json) _add_curl_args "$arg"; content_type="application/json"; shift ;;
        *.tar) _add_curl_args "$arg"; content_type="application/x-tar"; shift ;;
        *.yaml) _add_curl_args "$arg"; shift ;;
        application/*) content_type="$arg"; shift ;;
        [1-9][0-9][0-9]) break ;;
        *) echo "# [apiv2-runner] t(): internal error: invalid POST arg '$arg'" >&2; return 1 ;;
      esac
    done
    if [[ -z "${curl_args[*]:-}" && -z "${form_args[*]:-}" ]]; then
      curl_args=(-d "$(jsonify "${post_args[@]:-}")")
      testname="$testname [${curl_args[*]}]"
    elif [[ -z "${curl_args[*]:-}" ]]; then
      curl_args=(--form "request.json=$(jsonify "${post_args[@]:-}")" "${form_args[@]}")
      testname="$testname [${curl_args[*]} ${form_args[*]}]"
    fi
  fi

  # entrypoint path can include a descriptive comment; strip it off (matches upstream)
  path=${path%% *}

  # --- libpod detection + redirect -----------------------------------
  local is_libpod=0
  local http_path="$path"
  if [[ "$path" == libpod/* ]]; then
    is_libpod=1
    http_path="${path#libpod/}"
  elif [[ "$path" == */libpod/* ]]; then
    is_libpod=1
    http_path="${path#*/libpod/}"
  fi

  local url
  if [[ "$http_path" =~ ^http:// ]]; then
    url="$http_path"
  else
    local p="$http_path"
    p="${p//'['/%5B}"; p="${p//']'/%5D}"; p="${p//'{'/%7B}"; p="${p//'}'/%7D}"; p="${p//':'/%3A}"
    case "$p" in
      /*) url="http://localhost${p}" ;;
      *)  url="http://localhost/v1.44/${p}" ;;
    esac
  fi

  if [[ $method == "HEAD" ]]; then
    curl_args+=("--head")
  fi

  local expected_code=$1; shift || true
  if [[ "$expected_code" == "101" ]]; then
    curl_args+=("-H" "Connection: upgrade" "-H" "Upgrade: tcp")
  fi

  rm -f "$WORKDIR"/curl.*
  local response rc
  { response=$(curl -X "$method" --unix-socket "$CIDER_COMPAT_SOCKET" "${curl_args[@]}" \
                  -H "Content-type: $content_type" \
                  --max-time "${CIDER_COMPAT_CURL_TIMEOUT:-30}" \
                  --dump-header "$WORKDIR/curl.headers.out" \
                  --write-out '%{http_code}^%{content_type}^%{time_total}' \
                  -o "$WORKDIR/curl.result.out" "$url" 2>"$WORKDIR/curl.result.err"); rc=$?; } || :

  _T_GRADE=$([[ $is_libpod == 1 ]] && echo 0 || echo 1)

  if [[ $rc -ne 0 ]]; then
    _record 0 "$testname : curl" "exit 0" "curl exit $rc (see $WORKDIR/curl.result.err)"
    _T_GRADE=1
    return
  fi

  local actual_code content_type_out time_total
  IFS='^' read -r actual_code content_type_out time_total <<<"$response"

  # NOT local: upstream .at files read $output after t() returns (e.g.
  # `iid=$(jq -r '.[0].Id' <<<"$output")`) — this is a deliberate part of
  # the public contract, matching upstream's own (also-global) `output`.
  output=
  if [[ $content_type_out =~ /octet || $content_type_out =~ x-tar ]]; then
    output="[$(file --brief "$WORKDIR/curl.result.out" 2>/dev/null)]"
  else
    output=$(tr -d '\0' <"$WORKDIR/curl.result.out" 2>/dev/null)
  fi

  is "$actual_code" "$expected_code" "$testname : status"

  if [[ $expected_code = 204 || $expected_code = 304 ]]; then
    if [ -n "$output" ] && [ -n "${*:-}" ]; then
      _record 0 "$testname: ${expected_code} status returns no output" "''" "$output"
    fi
    _T_GRADE=1
    return
  fi

  if [[ "$actual_code" != "$expected_code" ]]; then
    for i; do
      _record skip "$testname: $i (wrong status code)"
    done
    _T_GRADE=1
    return
  fi

  # Classify each assertion arg as !=/=/~ (on a `.jq.field` prefix) or a
  # direct literal comparison against the whole body. Uses bash's own ERE
  # engine (`=~`), not `expr` -- see _bre_to_ere's comment for why: upstream
  # shells out to `expr "$i" : '[^\!]\+\!=.\+'` etc, which depends on GNU
  # expr's `\+` extension that BSD expr (macOS's /bin/expr) doesn't
  # implement, so those classification checks always failed here and every
  # assertion fell through to the "direct literal comparison" branch.
  local i
  for i; do
    local json_field expect expect_not actual
    if [[ "$i" =~ ^([^!]+)\!=(.*)$ ]]; then
      json_field=${BASH_REMATCH[1]}
      expect_not=${BASH_REMATCH[2]}
      actual=$(jq -r "$json_field" <<<"$output" 2>/dev/null)
      is_not "$actual" "$expect_not" "$testname : $json_field"
    elif [[ "$i" =~ ^([^=~]+)=(.*)$ ]]; then
      json_field=${BASH_REMATCH[1]}
      expect=${BASH_REMATCH[2]}
      actual=$(jq -r "$json_field" <<<"$output" 2>/dev/null)
      is "$actual" "$expect" "$testname : $json_field"
    elif [[ "$i" =~ ^([^=~]+)~(.*)$ ]]; then
      json_field=${BASH_REMATCH[1]}
      expect=${BASH_REMATCH[2]}
      actual=$(jq -r "$json_field" <<<"$output" 2>/dev/null)
      like "$actual" "$expect" "$testname : $json_field"
    else
      is "$output" "$i" "$testname : output"
    fi
  done

  _T_GRADE=1
}

# ---------------------------------------------------------------------------
# podman() shim: translates common subcommands used by upstream .at files
# for fixture setup to the `docker` CLI, which lib/daemon.sh has already
# pointed at our socket via DOCKER_HOST. Podman's CLI is deliberately
# Docker-compatible for all of these, so a verbatim passthrough is correct
# for the vast majority of calls. A short list of genuinely libpod-only
# subcommands (no Docker Engine API equivalent) are explicit no-ops that
# print a note to stderr instead of failing the whole `.at` file — any
# downstream `t` assertion that depended on their effect will simply fail
# on its own merits, which gets categorized "podman-specific" in the report.
# ---------------------------------------------------------------------------
podman() {
  local sub=${1:-}
  case "$sub" in
    manifest|generate|healthcheck|init|side)
      echo "# [apiv2-runner] podman $*: no Docker Engine API equivalent, skipping (libpod-only feature)" >&2
      return 0
      ;;
    untag)
      shift
      docker rmi "$@"
      ;;
    unshare)
      # podman-only namespace bootstrap; nothing to do against a remote daemon.
      return 0
      ;;
    *)
      docker "$@"
      ;;
  esac
}

# ---------------------------------------------------------------------------
# run_at_file PATH — source one upstream .at file, isolating it just enough
# that a hard failure in one file doesn't kill the whole run.
# ---------------------------------------------------------------------------
run_at_file() {
  local file=$1
  CURRENT_FILE="$(basename "$file" .at)"
  SEQ=0
  echo "=================================================================="
  echo "# ${CURRENT_FILE}.at"
  echo "=================================================================="
  set +e
  # shellcheck disable=SC1090
  source "$file"
  set -e 2>/dev/null || true
}

apiv2_summary() {
  echo
  echo "=================================================================="
  printf 'apiv2 summary: %d passed, %d failed, %d skipped (libpod/out-of-scope)\n' \
    "${#PASS_RESULTS[@]}" "${#FAIL_RESULTS[@]}" "${#SKIP_RESULTS[@]}"
  echo "=================================================================="
}
